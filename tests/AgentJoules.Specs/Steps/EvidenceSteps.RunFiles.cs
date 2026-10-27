using System.Globalization;
using System.Text.Json.Nodes;

using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// The M7 evidence stories (AJ-150 the run files and the dashboard, AJ-151 the cloudgpu evidence, AJ-152 the generated outputs, AJ-153 the documentation), split over the partial files of this class by story. Scoped to the four
/// story tags because sentences such as "the evidence check runs" and "it fails with" belong to every documentation story. The state lives in <see cref="EvidenceContext"/>: a Given step names the subject (the committed tree or a
/// fixture in a temporary workspace) and the rule to apply, the When step applies it, the Then step reads the violations. The @integration scenarios are bound here too and pass without asserting while
/// <c>AGENTJOULES_INTEGRATION</c> is closed.
/// </summary>
[Binding]
[Scope(Tag = "AJ-S150")]
[Scope(Tag = "AJ-S151")]
[Scope(Tag = "AJ-S152")]
[Scope(Tag = "AJ-S153")]
public sealed partial class EvidenceSteps(EvidenceContext context, ReplayContext replay, IReqnrollOutputHelper output)
{
    private readonly EvidenceContext _context = context;
    private readonly ReplayContext _replay = replay;
    private readonly IReqnrollOutputHelper _output = output;

    private string? _localBaseUrl;
    private string? _committedDriver;
    private string? _installedDriver;

    [AfterScenario]
    public async Task DisposeTheReplay() => await _replay.DisposeAsync();

    [When(@"^the evidence check runs$")]
    [When(@"^the cloudgpu evidence check runs$")]
    [When(@"^it is parsed$")]
    public void WhenTheCheckRuns() => _context.Result = (_context.Check ?? throw new Xunit.Sdk.XunitException("No rule was chosen by a Given step."))();

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure)
    {
        Assert.Contains(failure, _context.Violations);
    }

    // The one place the stories assert a pass: the rule returned no violation, and the list is shown when it did.
    private void AssertNoViolations() =>
        Assert.True(_context.Violations.Count == 0, string.Join("\n", _context.Violations));

    // True when a gated scenario must not assert; the reason goes to the scenario output.
    private bool Skipped()
    {
        if (_context.SkipNote is null)
        {
            return false;
        }

        _output.WriteLine("passed without asserting: " + _context.SkipNote);
        return true;
    }

    // A fixture folder with the newest committed run file of each tier and a build log, then the given tiers edited.
    private TempWorkspace RunFileFixture(string[] tiers, bool statement = true)
    {
        TempWorkspace workspace = _context.NewFixture();
        EvidenceContext.CopyNewestRunFiles(workspace, tiers);
        _ = workspace.WriteFile("docs/build-log.md", statement ? "# Build log\n\nThe published runs were recorded without a browser showing the dashboard.\n" : "# Build log\n\nNothing about the browser.\n");
        _context.Check = () => EvidenceChecks.RunEvidence(_context.Root);
        return workspace;
    }

    [Given(@"^the committed results folder$")]
    [Given(@"^the newest local run file$")]
    public void GivenTheCommittedResults() => _context.UseRepository();

    [When(@"^the newest non-baseline file per tier is read$")]
    public void WhenTheNewestFilePerTierIsRead() => _context.Result = EvidenceChecks.TierViolations(_context.Root);

    [Then(@"^local, nvidia and nebius each have status ""completed"" and run\.repeat 3 or more$")]
    public void ThenEachTierIsCompleteWithRepeats() => AssertNoViolations();

    [When(@"^baseline\.seconds, modelLoadSeconds, powerSource, localRuntime and gpuOffloadPercent are read$")]
    public void WhenTheLocalFieldsAreRead() => _context.Result = EvidenceChecks.LocalFileViolations(_context.Root);

    [Then(@"^baseline\.seconds is 30 or more and the other four fields are present and valid$")]
    public void ThenTheLocalFieldsAreValid() => AssertNoViolations();

    [When(@"^""validate results/"" runs$")]
    public async Task WhenValidateRuns() => await _context.RunCommandAsync("validate", "results/");

    [Then(@"^the exit code is 0 and ""Validated <n> files"" equals the number of run files$")]
    public void ThenValidateCountsEveryRunFile()
    {
        CliRunResult result = _context.Command!;
        int expected = DocumentChecks.ExpectedValidatedCount(EvidenceChecks.ResultFileNames(_context.Root));
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"Validated {expected} files"), result.Stdout, StringComparison.Ordinal);
    }

    [Given(@"^a results folder whose newest nvidia file has run\.repeat 1$")]
    public void GivenANvidiaFileWithOneRepeat()
    {
        TempWorkspace workspace = RunFileFixture(["local", "nebius"]);
        EvidenceContext.WriteEditedRunFile(workspace, "nvidia", static root => root["run"]!["repeat"] = 1);
    }

    [Given(@"^a results folder without a nebius run file$")]
    public void GivenAFolderWithoutNebius() => _ = RunFileFixture(["local", "nvidia"]);

    [Given(@"^a nebius fixture whose grossJoules is \{value:0,label:""measured""\}$")]
    public void GivenANebiusFileWithZeroGrossJoules()
    {
        TempWorkspace workspace = _context.NewFixture();
        EvidenceContext.WriteEditedRunFile(workspace, "nebius", static root =>
            root["steps"]![0]!["grossJoules"] = new JsonObject { ["value"] = 0, ["label"] = "measured", ["unit"] = "J" });
    }

    [When(@"^""validate"" runs on the folder$")]
    public async Task WhenValidateRunsOnTheFolder() => await _context.RunCommandAsync("validate", "results/");

    [Then(@"^the exit code is 2 and the failure names the hosted step$")]
    public void ThenTheHostedStepIsNamed()
    {
        CliRunResult result = _context.Command!;
        string text = result.Stdout + "\n" + result.Stderr;
        Assert.True(result.ExitCode == 2, $"exit {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");

        // The validator names the run file and the metric (it carries no step index): the hosted step is located by both.
        Assert.Contains("grossJoules", text, StringComparison.Ordinal);
        string fileName = EvidenceChecks.NewestRunFileName(RepositoryRoot.Path, "nebius")!;
        Assert.Contains(fileName, text, StringComparison.Ordinal);
    }

    [Given(@"^a local fixture with baseline\.seconds 20$")]
    public void GivenALocalFileWithABaselineOf20()
    {
        TempWorkspace workspace = RunFileFixture(["nvidia", "nebius"]);
        EvidenceContext.WriteEditedRunFile(workspace, "local", static root => root["baseline"]!["seconds"] = 20);
    }

    [Given(@"^the newest committed nebius run file$")]
    public void GivenTheNewestNebiusFile()
    {
        string name = EvidenceChecks.NewestRunFileName(RepositoryRoot.Path, "nebius") ?? throw new Xunit.Sdk.XunitException("results/ holds no nebius run file");
        _replay.UseCommittedFile(Tier.Nebius, RepositoryRoot.Resolve("results/" + name));
    }

    [When(@"^""dashboard --replay <file> --speed 50 --exit-after 5"" runs in process$")]
    public async Task WhenTheReplayRuns() => await _replay.RunWithClientAsync("dashboard --replay " + ReplayContext.FixtureToken + " --speed 50 --exit-after 5");

    [Then(@"^the exit code is 0 and the number of events equals the number of steps in the file$")]
    public void ThenOneEventPerStep()
    {
        int steps = _replay.Run!.Steps.Count;
        Assert.True(_replay.ExitCode == 0, $"exit {_replay.ExitCode}; stderr: {_replay.Result?.Stderr}");
        Assert.Equal(steps, _replay.Log!.Count("step-completed"));
        Assert.Equal(steps, _replay.Log.Count("step-started"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{steps} steps, {_replay.Log.Count("step-completed")} step-completed events"));
    }

    [Given(@"^no file at results/absent\.json$")]
    public static void GivenNoFileAtResultsAbsent() => Assert.False(RepositoryFiles.Exists("results/absent.json"));

    [When(@"^""dashboard --replay results/absent\.json"" runs$")]
    public async Task WhenTheAbsentReplayRuns() => await _replay.RunAsync("dashboard --replay results/absent.json");

    [Then(@"^the exit code is not 0 and the message names the file$")]
    public void ThenTheAbsentFileIsNamed()
    {
        Assert.NotEqual(0, _replay.ExitCode ?? -1);
        Assert.Contains("absent.json", _replay.Result!.Stderr + _replay.Result.Stdout, StringComparison.Ordinal);
    }

    [Given(@"^a build log without the sentence that the published runs were recorded without a browser open$")]
    public void GivenABuildLogWithoutTheStatement() => _ = RunFileFixture(["local", "nvidia", "nebius"], statement: false);

    [Given(@"^a results folder containing ""([^""]+)""$")]
    public void GivenAFolderWithAStrayFile(string name)
    {
        TempWorkspace workspace = RunFileFixture(["local", "nvidia", "nebius"]);
        _ = workspace.WriteFile("results/" + name, "{}\n");
    }

    // AJ-150 @integration

    [Given(@"^the laptop GPU and the local runtime are up$")]
    [Given(@"^the laptop GPU and nvidia-smi$")]
    public void GivenTheLaptopIsUp() => _context.OpenGate();

    [Given(@"^the local runtime is down$")]
    public void GivenTheLocalRuntimeIsDown()
    {
        _context.OpenGate();
        using LoopbackPortBlock free = LoopbackPortBlock.Reserve(1);
        _localBaseUrl = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{free.First}/v1");
    }

    [When(@"^""run --tier local --dashboard --dry-run"" runs$")]
    public async Task WhenTheDashboardDryRunRuns()
    {
        if (!Skipped())
        {
            await _context.RunLocalDryRunAsync(_localBaseUrl);
        }
    }

    [Then(@"^the exit code is 0 and a line starts with ""Dashboard:"" and contains ""http://127\.0\.0\.1:""$")]
    public void ThenTheDryRunPrintsTheDashboardUrl()
    {
        if (Skipped())
        {
            return;
        }

        CliRunResult result = _context.Command!;
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}; stderr: {result.Stderr}");
        Assert.Contains(result.Stdout.Split('\n'), static line => line.TrimStart().StartsWith("Dashboard:", StringComparison.Ordinal) && line.Contains("http://127.0.0.1:", StringComparison.Ordinal));
    }

    [Then(@"^the exit code is 5 and the output names the endpoint$")]
    public void ThenTheDryRunNamesTheEndpoint()
    {
        if (Skipped())
        {
            return;
        }

        CliRunResult result = _context.Command!;
        string text = result.Stdout + "\n" + result.Stderr;

        // First, because the exit-code assertion prints stdout and stderr when it fails. The values are the fake ones seeded into the run.
        foreach (string variable in CliChildProcess.KeyVariables)
        {
            Assert.False(text.Contains(_context.FakeKeys[variable], StringComparison.Ordinal), variable + " appears in the output (the value is not echoed)");
        }

        Assert.True(result.ExitCode == 5, $"exit {result.ExitCode}; stderr: {result.Stderr}");
        Assert.Contains(new Uri(_localBaseUrl!).Authority, text, StringComparison.Ordinal);
    }

    [When(@"^""probe --json"" runs$")]
    public async Task WhenProbeRuns()
    {
        if (Skipped())
        {
            return;
        }

        CliRunResult result = await CliChildProcess.RunAsync(["probe", "--json"], TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}; stderr: {result.Stderr}");
        _installedDriver = EvidenceChecks.TextOf(EvidenceChecks.At(JsonNode.Parse(result.Stdout), "driverVersion"));
        _committedDriver = EvidenceChecks.CommittedProbeDriver(RepositoryRoot.Path);
    }

    [Then(@"^the exit code is 0 and driverVersion equals the driver of the committed probe file$")]
    public void ThenTheDriverMatches()
    {
        if (Skipped())
        {
            return;
        }

        Assert.NotNull(_committedDriver);
        Assert.Empty(EvidenceChecks.DriverViolations(_committedDriver, _installedDriver));
    }

    [Given(@"^the committed probe names driver 616\.92 and the installed driver is another version$")]
    public void GivenAChangedDriver()
    {
        _committedDriver = "616.92";
        _installedDriver = "999.99";
    }

    [When(@"^the comparison runs$")]
    public void WhenTheComparisonRuns() => _context.Result = EvidenceChecks.DriverViolations(_committedDriver, _installedDriver);

    [Then(@"^it reports ""driver changed since the committed probe"" and the probe is rerun and committed$")]
    public void ThenTheChangedDriverIsReported()
    {
        Assert.Contains(EvidenceChecks.DriverChanged, _context.Violations);
        Assert.NotNull(EvidenceChecks.NewestProbeFileName(RepositoryRoot.Path, cloudGpu: false));
    }
}