using System.CommandLine;

using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// A minimal <c>run</c> command for the scenarios that need one before the real command exists (AJ-054 and AJ-056 build it later in the milestone; the
/// precedent is AJ-046). It does what the real command must do first and nothing else: it resolves <c>--out</c>, builds the run file name and constructs
/// the <see cref="ResultsFileWriter"/>, whose probe throws when the directory cannot be written; only after that does it send one request to the fake
/// handler, so a recorded request proves that the probe did not stop the command. Once a real <c>run</c> command exists, <see cref="RunCheckpointContext"/>
/// fails the scenario instead of registering this class beside the real one.
/// </summary>
/// <param name="handler">The fake endpoint that records every request the command sends.</param>
/// <param name="time">The clock the run file name is read from.</param>
public sealed class RunCommandStandIn(FakeHttpMessageHandler handler, TimeProvider time)
{
    private static readonly Uri CompletionsUri = new("https://nvidia.fake.test/v1/chat/completions");

    private readonly Option<string> _tier = new("--tier") { Description = "The tier to run." };
    private readonly Option<string> _out = new("--out") { Description = "The output directory." };
    private readonly Option<int> _repeat = new("--repeat") { Description = "The number of repeats.", DefaultValueFactory = _ => 3 };

    /// <summary>Creates the command to hand to <c>CliHostOverrides.AdditionalCommands</c>.</summary>
    /// <returns>The <c>run</c> command with <c>--tier</c>, <c>--out</c> and <c>--repeat</c>.</returns>
    public Command Create()
    {
        var command = new Command("run", "Stand-in for the run command: the writer probe, then one request.") { _tier, _out, _repeat };
        command.SetAction(RunAsync);
        return command;
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        Tier tier = Enum.Parse<Tier>(parseResult.GetRequiredValue(_tier), ignoreCase: true);
        string directory = Path.GetFullPath(parseResult.GetRequiredValue(_out));
        string name = RunFileName.Create(time, tier, [RunCheckpointFixtures.NemotronModel]);

        // The probe runs here, before any call; a directory that cannot be written ends the command with the mapped failure.
        _ = new ResultsFileWriter(directory, name);

        using var client = new HttpClient(handler, disposeHandler: false);
        using HttpResponseMessage response = await client.PostAsync(CompletionsUri, new StringContent("{}"), cancellationToken);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
}