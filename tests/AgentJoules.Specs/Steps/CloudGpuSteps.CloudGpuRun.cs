using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-127 (REQ-031, EDGE-015, EDGE-016, EDGE-017): run the cloudgpu workload on the VM and copy the results back. The scenarios follow the AJ-023 convention (see the AJ-125 steps): with the gate AGENTJOULES_INTEGRATION closed, or
/// with the committed evidence absent, a step writes <c>not asserted: &lt;reason&gt;</c> and passes; otherwise it asserts the newest committed <c>results/&lt;yyyyMMddTHHmmssZ&gt;-cloudgpu.json</c> (<see cref="CloudGpuEvidence"/>), the validator
/// over every committed run file and the Runs and Cost sections of <c>docs/cloudgpu-run.md</c>. The scenarios about a stop (the cost cap, the four-hour box, a lost VM) assert only when the file or the document records that stop.
/// The judge-key scenario is hermetic and always asserts: the real <c>run --tier cloudgpu --dry-run</c> runs through <see cref="RunCommandHarness"/> with the cloudgpu tier enabled, a scripted GPU, fake models endpoints and no
/// NVIDIA_API_KEY. Every step is scoped to the tag <c>AJ-S127</c>.
/// </summary>
public sealed partial class CloudGpuSteps
{
    private const string RunTag = "AJ-S127";
    private const string CliPrefix = "./AgentJoules.Cli ";
    private const string JudgeKey = "NVIDIA_API_KEY";

    private static readonly TimeSpan RunHangGuard = TimeSpan.FromSeconds(30);

    private bool _runJudgeKeyMissing;
    private string _runCommand = string.Empty;
    private CliRunResult? _runResult;

    private CliRunResult RunResult => _runResult ?? throw new Xunit.Sdk.XunitException("The command has not run yet.");

    // The newest committed run file as a tree; the evidence exists when this is called.
    private static JsonNode NewestRunTree() => JsonNode.Parse(File.ReadAllText(CloudGpuEvidence.NewestRun()!))!;

    private static string? StopReasonOf(JsonNode run) =>
        run["run"]?["stopReason"] is JsonValue value && value.TryGetValue(out string? reason) ? reason : null;

    private void AssertRun(Action<JsonNode> assert, Func<JsonNode, string?>? inapplicable = null) =>
        AssertEvidence(
            () => CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Run) ?? inapplicable?.Invoke(NewestRunTree()),
            () => assert(NewestRunTree()));

    private static string? NotStoppedBy(JsonNode run, string reason) =>
        string.Equals(StopReasonOf(run), reason, StringComparison.Ordinal) ? null : $"the newest cloudgpu run file has no stop reason \"{reason}\", so that stop did not occur";

    // AJ-127

    [Given(@"^the NIM container serves the model and the session environment is set$")]
    [Given(@"^the dry-run passed and the guard allows a run$")]
    [Given(@"^the run file was copied back with scp$")]
    [Given(@"^the run executes inside a tmux session on the VM$")]
    [Given(@"^the VM becomes unreachable during the run$")]
    [Given(@"^the user reports (?:[\d.]+) USD observed on the billing page$")]
    [Given(@"^(?:\d+) hours have passed since the handover$")]
    [When(@"^the SSH connection breaks and is re-established$")]
    [When(@"^the builder copies what exists and asks the user to check the console$")]
    [When(@"^the builder is about to start the next invocation$")]
    [Scope(Tag = RunTag)]
    public static void RunUserActionOrBuilderProcedure()
    {
        // The VM session, the SSH connection and the billing page are the user's and the builder's; a test never reaches them. The Then steps check what they left in the committed files and in docs/cloudgpu-run.md.
    }

    [Given(@"^the judge key is not set in the VM session$")]
    [Scope(Tag = RunTag)]
    public void GivenTheJudgeKeyIsNotSet() => _runJudgeKeyMissing = true;

    [When(@"^I run ""([^""]*)"" on the VM$")]
    [Scope(Tag = RunTag)]
    public async Task WhenIRunOnTheVmAJ127(string command)
    {
        _runCommand = command;
        if (!_runJudgeKeyMissing)
        {
            _output.WriteLine("not asserted: a test never reaches the VM (the builder runs \"" + command + "\" there); the Then steps check what it left");
            return;
        }

        // Hermetic: the real run command with the cloudgpu tier enabled, no SSH variables required (as in the VM session), the judge key unset and every tier answering its models route.
        Assert.StartsWith(CliPrefix, command, StringComparison.Ordinal);
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [JudgeKey] = null,
            ["NEBIUS_API_KEY"] = ProviderClientFixtures.FakeKey(),
            ["AgentJoules:Tiers:cloudgpu:Enabled"] = "true",
            ["AgentJoules:CloudGpu:RequireSshVariables"] = "false",
        };
        var endpoints = new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal);
        foreach (string tierName in new[] { "local", "cloudgpu", "nvidia", "nebius" })
        {
            endpoints[tierName] = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };
        }

        RunCommandSetup setup = RunCommandSetup.Create(settings, endpoints, new ScriptedNvmlApi(new FakeTimeProvider()));
        _runResult = await RunCommandHarness.RunAsync(command[CliPrefix.Length..], setup).WaitAsync(RunHangGuard);
        Assert.Empty(_runResult.DeniedRequests);
    }

    [When(@"^I run ""([^""]*)""$")]
    [Scope(Tag = RunTag)]
    public void WhenIRunAJ127(string command)
    {
        _runCommand = command;
        _output.WriteLine("not asserted: \"" + command + "\" validates the committed files; the Then step runs validate through the CLI harness");
    }

    [Then(@"^the exit code is (\d+)$")]
    [Scope(Tag = RunTag)]
    public async Task ThenTheExitCodeIsAJ127(int expected)
    {
        if (_runJudgeKeyMissing)
        {
            Assert.True(RunResult.ExitCode == expected, $"Expected exit code {expected.ToString(CultureInfo.InvariantCulture)} but got {RunResult.ExitCode.ToString(CultureInfo.InvariantCulture)}; stderr: {RunResult.Stderr}");
            return;
        }

        if (_runCommand.StartsWith("validate", StringComparison.Ordinal))
        {
            await AssertValidatedFilesAsync(expected);
            return;
        }

        string command = _runCommand.StartsWith(CliPrefix, StringComparison.Ordinal) ? _runCommand[CliPrefix.Length..] : _runCommand;
        AssertDocument(
            document => Assert.True(RecordedExitCodes(document, command).Contains(expected), $"docs/cloudgpu-run.md records no exit code {expected.ToString(CultureInfo.InvariantCulture)} for \"{command}\"."),
            document => NoRecordedCommand(document, command));
    }

    // "AJ validate results/" runs through the CLI harness over the repository folder (no network, no key): it exits 0 and prints "Validated <n> files", n being the run files of the folder.
    private static async Task AssertValidatedFilesAsync(int expected)
    {
        // Hermetic and always asserted: the command reads files of the working tree only, so it needs neither the gate nor a cloudgpu file.
        Assert.Equal(0, expected);
        int count = LaptopCampaign.CommittedRunFiles(LaptopResults.ResultsFolder()).Count;
        CliRunResult result = await CliHarness.RunTokensAsync(["validate", "results/"], new AgentJoules.Cli.CliHostOverrides(WorkingDirectory: ConfigurationFixtures.RepositoryRoot())).WaitAsync(RunHangGuard);

        string wanted = $"Validated {count.ToString(CultureInfo.InvariantCulture)} files";
        Assert.True(result.ExitCode == expected, $"validate results/ exited with {result.ExitCode.ToString(CultureInfo.InvariantCulture)}, not {expected.ToString(CultureInfo.InvariantCulture)}; stdout: {result.Stdout}");
        Assert.True(result.Stdout.Split('\n').Any(line => string.Equals(line.TrimEnd('\r'), wanted, StringComparison.Ordinal)), $"validate results/ did not print \"{wanted}\".");
    }

    [Then(@"^the output is recorded in docs/cloudgpu-run\.md$")]
    [Scope(Tag = RunTag)]
    public void ThenTheOutputIsRecorded() =>
        AssertDocument(
            document => Assert.True(RecordedExitCodes(document, "run --tier cloudgpu --dry-run").Count > 0, "docs/cloudgpu-run.md records no exit code after the dry-run command."),
            document => NoRecordedCommand(document, "run --tier cloudgpu --dry-run"));

    [Then(@"^the run file has 36 steps, run\.repeat 3 and run\.status ""completed""$")]
    [Scope(Tag = RunTag)]
    public void ThenTheRunFileHasTheShape() =>
        AssertEvidence(
            () => CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Run),
            () =>
            {
                string path = CloudGpuEvidence.NewestRun()!;
                string[] failures = [.. CloudGpuEvidence.RunFailures(Path.GetFileName(path), File.ReadAllText(path)).Where(static failure => !failure.StartsWith("environment.", StringComparison.Ordinal))];
                Assert.True(failures.Length == 0, Path.GetFileName(path) + ":\n" + string.Join('\n', failures));

                // Completed, or partial with a stop reason (a run the guard stopped), as the tmux step and the field rules read it.
                JsonNode tree = NewestRunTree();
                string? status = tree["run"]?["status"]?.GetValue<string>();
                Assert.True(
                    string.Equals(status, "completed", StringComparison.Ordinal) || (string.Equals(status, "partial", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(StopReasonOf(tree))),
                    "run.status is neither \"completed\" nor \"partial\" with a stop reason.");
            });

    [Then(@"^environment\.provider is ""nebius-ai-cloud"" and environment\.precision is ""FP8""$")]
    [Scope(Tag = RunTag)]
    public void ThenTheEnvironmentHasTheFp8Metadata() =>
        AssertEvidence(
            () => CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Run),
            () =>
            {
                string path = CloudGpuEvidence.NewestRun()!;
                string[] failures = [.. CloudGpuEvidence.RunFailures(Path.GetFileName(path), File.ReadAllText(path)).Where(static failure => failure.StartsWith("environment.", StringComparison.Ordinal))];
                Assert.True(failures.Length == 0, Path.GetFileName(path) + ":\n" + string.Join('\n', failures));
            });

    [Then(@"^the output contains ""Validated"" followed by the number of run files$")]
    [Scope(Tag = RunTag)]
    public static Task ThenTheOutputCountsTheRunFiles() => AssertValidatedFilesAsync(0);

    [Then(@"^the output names ""([^""]*)""$")]
    [Scope(Tag = RunTag)]
    public void ThenTheOutputNamesAJ127(string name)
    {
        Assert.Contains(name, RunResult.Stdout + "\n" + RunResult.Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the run continues and finishes with status ""completed""$")]
    [Scope(Tag = RunTag)]
    public void ThenTheRunFinishesInTmux()
    {
        AssertSection("Runs", runs => Assert.True(runs.Contains("tmux", StringComparison.Ordinal), "the Runs section does not mention the tmux session."));
        AssertRun(
            run => Assert.Equal("completed", run["run"]?["status"]?.GetValue<string>()),
            static run => string.Equals(run["run"]?["status"]?.GetValue<string>(), "partial", StringComparison.Ordinal) ? "the newest cloudgpu run file is partial (a stop, not an SSH drop), so the partial scenarios apply" : null);
    }

    [Then(@"^no retry is made past the box or the cap$")]
    [Scope(Tag = RunTag)]
    public void ThenNoRetryIsMade() =>
        AssertSection(
            "Runs",
            runs => Assert.True(runs.Contains("no retry", StringComparison.OrdinalIgnoreCase), "the Runs section does not state that no retry was made."),
            static runs => Regex.IsMatch(runs, @"(?i)lost|unreachable|incomplete", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ? null : "no lost-VM incident is recorded in the Runs section");

    [Then(@"^the incident is recorded and the file is not used in any report$")]
    [Scope(Tag = RunTag)]
    public void ThenTheIncompleteFileIsExcluded() =>
        AssertSection(
            "Runs",
            _ =>
            {
                string[] incomplete = [.. CloudGpuEvidence.CommittedRunFiles().Where(static file => string.Equals(JsonNode.Parse(File.ReadAllText(file))?["run"]?["status"]?.GetValue<string>(), "incomplete", StringComparison.Ordinal)).Select(static file => Path.GetFileName(file))];
                Assert.True(incomplete.Length == 0, "a committed cloudgpu run file has run.status incomplete (an excluded file is not committed): " + string.Join(", ", incomplete));
            },
            static runs => Regex.IsMatch(runs, @"(?i)lost|unreachable|incomplete", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ? null : "no lost-VM incident is recorded in the Runs section");

    [Then(@"^no new invocation starts$")]
    [Scope(Tag = RunTag)]
    public void ThenNoNewInvocationStarts() =>
        AssertRun(
            run => Assert.Equal("partial", run["run"]?["status"]?.GetValue<string>()),
            static run => NotStoppedBy(run, "cost-cap"));

    [Then(@"^the builder copies the existing results and tells the user to delete the VM$")]
    [Scope(Tag = RunTag)]
    public void ThenTheUserIsToldToDeleteTheVm() =>
        AssertSection(
            "Cost",
            cost => Assert.True(cost.Contains("delete", StringComparison.OrdinalIgnoreCase), "the Cost section does not tell the user to delete the VM."),
            static _ => CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Run) ?? NotStoppedBy(NewestRunTree(), "cost-cap"));

    [Then(@"^no new invocation starts and the stop reason ""time-box"" is recorded$")]
    [Scope(Tag = RunTag)]
    public void ThenTheTimeBoxIsRecorded() =>
        AssertSection(
            "Runs",
            runs => Assert.True(runs.Contains("time-box", StringComparison.Ordinal), "the Runs section does not record the stop reason time-box."),
            static _ => CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Run) ?? NotStoppedBy(NewestRunTree(), "time-box"));
}