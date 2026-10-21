using System.CommandLine;

using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Hosting;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// A minimal <c>run</c> command for the scenarios that need one before the real command exists (AJ-054 and AJ-056 build it later in the milestone; the
/// precedent is AJ-046). It does what the real command must do first and nothing else, in the order the scenarios need:
/// <list type="number">
/// <item>When the host registers the <c>PreflightService</c> (the AJ-052 scenarios), it runs the real key check through <see cref="PreflightRunCommand"/> for <c>--tier &lt;name|all&gt;</c>
/// and <c>--dry-run</c>, prints the judge warning through the real console reporter, and ends there for a dry run.</item>
/// <item>When <c>--out</c> is given (the AJ-055 scenario), it resolves it, builds the run file name and constructs the <see cref="ResultsFileWriter"/>, whose probe throws when the
/// directory cannot be written.</item>
/// <item>Only after that it sends one request: through the real provider pipeline (<c>IProviderClientFactory</c> and the named client of the tier) when the host registers it,
/// otherwise straight to the fake handler, so a recorded request proves that the earlier steps did not stop the command.</item>
/// </list>
/// Once a real <c>run</c> command exists, <see cref="EnsureNoRealRunCommandAsync"/> fails the scenario instead of registering this class beside the real one; AJ-054 must rewire
/// the AJ-052 scenarios (all eight) and the AJ-055 unwritable-directory scenario to the real command and delete this class.
/// </summary>
/// <param name="handler">The fake endpoint that records every request the command sends when the host has no provider clients.</param>
/// <param name="time">The clock the run file name is read from.</param>
public sealed class RunCommandStandIn(FakeHttpMessageHandler handler, TimeProvider time)
{
    private static readonly Uri CompletionsUri = new("https://nvidia.fake.test/v1/chat/completions");

    private readonly PreflightRunCommand _preflight = new();
    private readonly Option<string> _tier = new("--tier") { Description = "The tier to run, or all." };
    private readonly Option<string> _out = new("--out") { Description = "The output directory." };
    private readonly Option<int> _repeat = new("--repeat") { Description = "The number of repeats.", DefaultValueFactory = _ => 3 };
    private readonly Option<bool> _dryRun = new("--dry-run") { Description = "Check the keys and make no request." };

    /// <summary>Fails the scenario when the application lists a <c>run</c> command of its own.</summary>
    /// <param name="rewire">What the scenario's owner must rewire, for the failure message.</param>
    /// <returns>A task that completes when the root help was read.</returns>
    /// <exception cref="XunitException">The application lists a <c>run</c> command: the stand-in must not run beside it, because the request counts of a scenario see only the fake handler of the stand-in, so a real command would pass unchecked.</exception>
    public static async Task EnsureNoRealRunCommandAsync(string rewire)
    {
        // The root help lists the commands one per line; "run --help" cannot tell, because the help option wins over an unrecognized command.
        CliRunResult help = await CliHarness.RunAsync("--help");
        if (help.Stdout.Split('\n').Any(static line => line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim().Split(' ')[0] == "run"))
        {
            throw new XunitException("a run command exists: rewire " + rewire + " (AJ-054/AJ-056)");
        }
    }

    /// <summary>Hands over the started host (the scenario passes this method as <c>CliHostOverrides.OnHostStarted</c>).</summary>
    /// <param name="host">The host the command runs in.</param>
    public void Attach(IHost host) => _preflight.Attach(host);

    /// <summary>Creates the command to hand to <c>CliHostOverrides.AdditionalCommands</c>.</summary>
    /// <returns>The <c>run</c> command with <c>--tier</c>, <c>--out</c>, <c>--repeat</c> and <c>--dry-run</c>.</returns>
    public Command Create()
    {
        var command = new Command("run", "Stand-in for the run command: the key check, the writer probe, then one request.") { _tier, _out, _repeat, _dryRun };
        command.SetAction(RunAsync);
        return command;
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        string selection = parseResult.GetRequiredValue(_tier);
        string? output = parseResult.GetValue(_out);
        Task Probe() => ProbeOutputAsync(selection, output);

        if (_preflight.IsAvailable)
        {
            if (await _preflight.ExecuteAsync(selection, parseResult.GetValue(_dryRun), Probe, cancellationToken) is { } code)
            {
                return code;
            }
        }
        else
        {
            await Probe();
        }

        using var client = new HttpClient(handler, disposeHandler: false);
        using HttpResponseMessage response = await client.PostAsync(CompletionsUri, new StringContent("{}"), cancellationToken);
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    // The probe runs before any call; a directory that cannot be written ends the command with the mapped failure.
    private Task ProbeOutputAsync(string selection, string? output)
    {
        if (output is not null)
        {
            Tier tier = Enum.Parse<Tier>(selection, ignoreCase: true);
            string directory = Path.GetFullPath(output);
            string name = RunFileName.Create(time, tier, [RunCheckpointFixtures.NemotronModel]);
            _ = new ResultsFileWriter(directory, name);
        }

        return Task.CompletedTask;
    }
}