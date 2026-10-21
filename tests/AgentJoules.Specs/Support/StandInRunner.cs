using AgentJoules.Cli;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The one place the scenarios that run the stand-in <c>run</c> command (<see cref="RunCommandStandIn"/>) through the real command line harness are wired: the real preflight and
/// provider-client registrations, the configuration override of the scenario, and the fake endpoint of each named tier client (a client with no endpoint falls through to the deny-all
/// handler of the harness). AJ-052 and AJ-053 use it; AJ-054 deletes it with the stand-in.
/// </summary>
public static class StandInRunner
{
    /// <summary>
    /// Runs <paramref name="arguments"/> through the harness with the stand-in <c>run</c> command.
    /// </summary>
    /// <param name="arguments">The command line, for example <c>run --tier all --dry-run</c>.</param>
    /// <param name="rewire">What the owner of the scenarios must rewire once a real <c>run</c> command exists, for the failure message.</param>
    /// <param name="fallback">The fake endpoint the stand-in sends to when the host has no provider clients.</param>
    /// <param name="settings">The configuration override of the run; keys are root keys (<c>NVIDIA_API_KEY</c>) or full keys.</param>
    /// <param name="endpoints">The primary handler of each named tier client, by lowercase tier name.</param>
    /// <returns>The result of the run.</returns>
    /// <exception cref="Xunit.Sdk.XunitException">The application lists a <c>run</c> command of its own (AJ-054 must rewire the scenarios).</exception>
    public static async Task<CliRunResult> RunAsync(
        string arguments,
        string rewire,
        FakeHttpMessageHandler fallback,
        IReadOnlyDictionary<string, string?> settings,
        IEnumerable<KeyValuePair<string, HttpMessageHandler>> endpoints)
    {
        await RunCommandStandIn.EnsureNoRealRunCommandAsync(rewire);

        var standIn = new RunCommandStandIn(fallback, new FakeTimeProvider());
        KeyValuePair<string, HttpMessageHandler>[] registered = [.. endpoints];
        var overrides = new CliHostOverrides(
            Services: services =>
            {
                _ = services.AddAgentJoulesProviderClients().AddAgentJoulesPreflight();
                foreach ((string tierName, HttpMessageHandler endpoint) in registered)
                {
                    _ = services.AddHttpClient(ProviderClientFactory.HttpClientName(tierName)).ConfigurePrimaryHttpMessageHandler(() => endpoint);
                }
            },
            Configuration: settings,
            AdditionalCommands: [standIn.Create()],
            OnHostStarted: standIn.Attach);

        return await CliHarness.RunAsync(arguments, overrides);
    }
}