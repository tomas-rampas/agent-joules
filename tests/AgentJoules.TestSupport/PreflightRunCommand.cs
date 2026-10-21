using System.CommandLine;

using AgentJoules.Cli.Output;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Providers;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentJoules.TestSupport;

/// <summary>
/// The test-only part of a <c>run</c> command that exists before the real one (AJ-054 and AJ-056 build it later in the milestone): it parses <c>--tier</c> and
/// <c>--dry-run</c>, runs the real <see cref="PreflightService"/> key check from the started host, prints the judge warning through the real
/// <see cref="ConsoleReporter"/>, and then, outside a dry run, makes exactly one chat request to the first selected tier through the real provider pipeline
/// (<see cref="IProviderClientFactory"/> and the named <see cref="HttpClient"/> of the tier), so "the endpoint refused the connection" and "0 requests were recorded" are
/// measured. It is not the run command: nothing here measures, retries or writes a file. The host that runs the command is handed over through <see cref="Attach"/>, which
/// the test passes as <c>CliHostOverrides.OnHostStarted</c>; the services it needs (<c>AddAgentJoulesPreflight</c>, <c>AddAgentJoulesProviderClients</c>) are the test's to register.
/// </summary>
public sealed class PreflightRunCommand
{
    private readonly Option<string> _tier = new("--tier") { Description = "A tier name or all.", Required = true };
    private readonly Option<bool> _dryRun = new("--dry-run") { Description = "Check the keys and make no request." };

    private IHost? _host;

    /// <summary>Hands over the started host (the test passes this method as <c>CliHostOverrides.OnHostStarted</c>).</summary>
    /// <param name="host">The host the command runs in.</param>
    public void Attach(IHost host) => Volatile.Write(ref _host, host);

    /// <summary>True once a host is attached and registers the <see cref="PreflightService"/>; false for a host that does not (the AJ-055 scenario, which has no key check).</summary>
    public bool IsAvailable => Volatile.Read(ref _host)?.Services.GetService<PreflightService>() is not null;

    /// <summary>Creates the stand-alone <c>run</c> command with <c>--tier</c> and <c>--dry-run</c>.</summary>
    /// <returns>The command to hand to <c>CliHostOverrides.AdditionalCommands</c>.</returns>
    public Command Create()
    {
        var command = new Command("run", "Stand-in for the run command: the key check, then one request.") { _tier, _dryRun };
        command.SetAction(async (parseResult, cancellationToken) =>
            await ExecuteAsync(parseResult.GetRequiredValue(_tier), parseResult.GetValue(_dryRun), beforeRequest: null, cancellationToken) ?? 0);
        return command;
    }

    /// <summary>
    /// Runs the key check and, outside a dry run, the one request.
    /// </summary>
    /// <param name="selection">The <c>--tier</c> value.</param>
    /// <param name="dryRun">True for <c>--dry-run</c>.</param>
    /// <param name="beforeRequest">Runs after the check and before the request (the Specs stand-in probes its output directory here); null for none.</param>
    /// <param name="cancellationToken">The command token.</param>
    /// <returns>0 when the request was made, or in a dry run; null when the host has no <see cref="IProviderClientFactory"/>, so the caller sends its own request.</returns>
    public async Task<int?> ExecuteAsync(string selection, bool dryRun, Func<Task>? beforeRequest, CancellationToken cancellationToken)
    {
        IServiceProvider services = (_host ?? throw new InvalidOperationException("No host was attached: pass Attach as CliHostOverrides.OnHostStarted.")).Services;

        PreflightService preflight = services.GetRequiredService<PreflightService>();
        IReadOnlyList<Tier> tiers = preflight.SelectTiers(selection);
        KeyCheckResult keys = preflight.CheckKeys(tiers, dryRun);
        if (keys.MissingJudgeVariable is { } judge)
        {
            services.GetRequiredService<ConsoleReporter>().ReportMissingJudgeKey(judge);
        }

        if (dryRun)
        {
            return 0;
        }

        if (beforeRequest is not null)
        {
            await beforeRequest();
        }

        if (services.GetService<IProviderClientFactory>() is not { } clients)
        {
            return null;
        }

        string name = TierOptions.NameOf(tiers[0]);
        TierOptions tier = services.GetRequiredService<IOptions<AgentJoulesOptions>>().Value.Tiers[name];
        using IChatClient client = clients.Create(tiers[0], tier.ResolveModelId("Planner"));
        _ = await client.GetResponseAsync("ping", cancellationToken: cancellationToken);
        return 0;
    }
}