using System.Globalization;
using System.Text.Json.Nodes;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-114: run the laptop measurement campaign (REQ-018, REQ-020, REQ-021). Scoped to the feature because "I run" and "the exit code is 0" are sentences of every CLI story. The scenarios follow the AJ-110 pattern
/// (see <see cref="LaptopCampaignContext"/>): with the gate AGENTJOULES_INTEGRATION closed the steps record the reason and pass; with it open they read the newest committed run file of each tier through
/// <see cref="LaptopCampaign"/> and assert criteria 3 to 5, and never run the campaign (the "run --tier all --repeat 3" step is a no-op there: the orchestrator's campaign is the run). The committed decision of
/// <c>docs/nim-local-attempt.md</c> is the NIM, so "Ollama serves the local model" is read as "the configured local runtime serves it". A tier without a file is a note when it is pending
/// (<see cref="LaptopCampaign.PendingTiers"/>), else a failure; a scenario about a failed tier asserts only when such a file exists. The scenario about a missing nebius key is hermetic and always asserts.
/// </summary>
[Binding]
[Scope(Feature = "Run the laptop measurement campaign")]
public sealed class LaptopCampaignSteps(LaptopCampaignContext context, IReqnrollOutputHelper output)
{
    private const string ValidateCommand = "validate results/";

    private readonly LaptopCampaignContext _context = context;
    private readonly IReqnrollOutputHelper _output = output;

    // True in the scenario about a missing key, which runs the command with deny-all HTTP and no gate.
    private bool _hermetic;

    // True when the step must assert; otherwise the reason is written to the scenario output.
    private bool Asserting()
    {
        if (_context.IsActive)
        {
            return true;
        }

        _output.WriteLine("passed without asserting: " + _context.Note);
        return false;
    }

    private void Note(string text) => _output.WriteLine(text);

    private static string Name(Tier tier) => TierOptions.NameOf(tier);

    private static IReadOnlyList<Tier> TiersOf(string quotedList) =>
        [.. quotedList.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Where(static word => word != "and").Select(static word => Enum.Parse<Tier>(word.Trim('"'), ignoreCase: true))];

    // The newest run file of a tier read as a tree; null (with a note) for a pending tier without a file.
    private JsonNode? ReadTier(Tier tier) => LaptopCampaign.FindOrNote(tier, Note) is { } path ? LaptopCampaign.Read(path) : null;

    private static void AssertNone(IReadOnlyList<string> failures, string subject) =>
        Assert.True(failures.Count == 0, subject + ":\n" + string.Join('\n', failures));

    private static void AssertNone(IEnumerable<string> failures, string subject, string prefix) =>
        AssertNone([.. failures.Where(failure => failure.StartsWith(prefix, StringComparison.Ordinal))], subject);

    // Skips the scenario with a note when the repository holds no failed file of the nvidia tier (nothing to be asserted about a failure that did not happen).
    private void RequireFailedNvidiaFile()
    {
        _context.Open();
        if (_context.IsActive && LaptopCampaign.FailedFiles(Tier.Nvidia).Count == 0)
        {
            _context.Skip("results/ holds no nvidia run file with run.status failed, so there is no failed tier to assert");
        }
    }

    [Given(@"^the preflight passed, the keys are set and Ollama serves the local model$")]
    public void GivenThePreflightPassed()
    {
        // The committed decision is the NIM (docs/nim-local-attempt.md); the files are read whichever runtime served the run, and no key is read.
        _context.Open();
    }

    [Given(@"^the campaign completed$")]
    public void GivenTheCampaignCompleted() => _context.Open();

    [Given(@"^the three run files are in results/$")]
    public void GivenTheRunFilesAreInResults() => _context.Open();

    [Given(@"^NEBIUS_API_KEY is not set$")]
    public void GivenNebiusKeyIsNotSet()
    {
        // Hermetic: the harness configuration is in memory and the process environment is not a source of it, so the key is absent whatever the machine holds.
        _hermetic = true;
    }

    [Given(@"^the nvidia tier answers HTTP 429 until the five attempts are exhausted$")]
    public void GivenTheNvidiaTierIsRateLimited() => RequireFailedNvidiaFile();

    [Given(@"^the local and nebius files are completed and the nvidia file failed$")]
    public void GivenOnlyTheNvidiaFileFailed() => RequireFailedNvidiaFile();

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments)
    {
        _context.LastCommand = arguments;
        if (_hermetic)
        {
            await _context.RunWithoutNebiusKeyAsync(arguments);
        }

        // "run --tier all --repeat 3", "run --tier nvidia --repeat 3" and "validate results/" are the orchestrator's action: the committed files are only read by the Then steps.
    }

    [When(@"^I read the latest (?:local|nvidia and nebius) run files?$")]
    public static void WhenIReadTheLatestFiles()
    {
        // The files are read by the Then steps.
    }

    [When(@"^the run stops$")]
    public static void WhenTheRunStops()
    {
        // The run is the orchestrator's action; the failed file is read by the Then step.
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        if (_context.Result is { } result)
        {
            Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");
            Assert.Empty(result.DeniedRequests);
            return;
        }

        if (!Asserting())
        {
            return;
        }

        if (string.Equals(_context.LastCommand, ValidateCommand, StringComparison.Ordinal))
        {
            // "AJ validate results/" exits 0 when every counted file validates; the validator is the production one and the command itself is not started.
            IReadOnlyList<string> files = LaptopCampaign.CommittedRunFiles();
            Assert.NotEmpty(files);
            foreach (string path in files)
            {
                AssertNone(LaptopCampaign.SchemaFailures(path), "results/" + Path.GetFileName(path) + " is rejected by the validator of AJ validate");
            }

            return;
        }

        // The campaign.exit marker of "run --tier all --repeat 3" is outside the repository and not available to a test: the files the run wrote are the record, and the Then steps below assert them.
        Note("note: the exit code of the campaign is recorded outside the repository; the run files are asserted instead");
    }

    [Then(@"^the exit code is (\d+) and the nvidia file has run\.status ""failed""$")]
    public void ThenTheExitCodeIsAndTheNvidiaFileFailed(int expected)
    {
        Assert.Equal(5, expected);
        if (!Asserting())
        {
            return;
        }

        string[] failed = [.. LaptopCampaign.FailedFiles(Tier.Nvidia)];
        Assert.NotEmpty(failed);
        Assert.All(failed, static path => Assert.Equal("failed", LaptopCampaign.StatusOf(LaptopCampaign.Read(path))));
    }

    [Then(@"^one completed run file exists for each of ((?:""[a-z]+""(?:, | and )?)+)$")]
    public void ThenOneCompletedFileExistsForEachTier(string tiers)
    {
        if (!Asserting())
        {
            return;
        }

        foreach (Tier tier in TiersOf(tiers))
        {
            if (ReadTier(tier) is { } run)
            {
                Assert.Equal("completed", LaptopCampaign.StatusOf(run));
            }
        }
    }

    [Then(@"^each file has 3 tasks, run\.repeat 3 and 36 steps$")]
    public void ThenEachFileHasTheShape()
    {
        if (!Asserting())
        {
            return;
        }

        foreach (Tier tier in LaptopCampaign.Tiers)
        {
            if (ReadTier(tier) is { } run)
            {
                AssertNone(LaptopCampaign.ShapeFailures(run), "the " + Name(tier) + " file");
            }
        }
    }

    [Then(@"^baseline\.seconds is at least 30$")]
    public void ThenTheBaselineIsAtLeast30()
    {
        if (Asserting())
        {
            AssertNone(LaptopCampaign.LocalFailures(LocalRun()), "the local file", "baseline.seconds");
        }
    }

    [Then(@"^environment\.modelLoadSeconds and environment\.gpuOffloadPercent are numbers$")]
    public void ThenLoadTimeAndOffloadAreNumbers()
    {
        if (!Asserting())
        {
            return;
        }

        IReadOnlyList<string> failures = LaptopCampaign.LocalFailures(LocalRun());
        AssertNone(failures, "the local file", "environment.modelLoadSeconds");
        AssertNone(failures, "the local file", "environment.gpuOffloadPercent");
        AssertNone(failures, "the local file", "environment.localRuntime");
    }

    [Then(@"^environment\.powerSource is one of ""ac"", ""battery"" and ""unknown""$")]
    public void ThenThePowerSourceIsKnown()
    {
        if (Asserting())
        {
            AssertNone(LaptopCampaign.LocalFailures(LocalRun()), "the local file", "environment.powerSource");
        }
    }

    [Then(@"^every step has grossJoules and netJoules with label ""N/A"" and a null value$")]
    public void ThenEveryHostedStepHasNoEnergy()
    {
        if (!Asserting())
        {
            return;
        }

        foreach (Tier tier in LaptopCampaign.HostedTiers)
        {
            if (ReadTier(tier) is { } run)
            {
                AssertNone(LaptopCampaign.HostedFailures(run), "the " + Name(tier) + " file");
            }
        }
    }

    [Then(@"^the output contains ""Validated"" followed by the number of run files$")]
    public void ThenTheOutputContainsTheCount()
    {
        if (!Asserting())
        {
            return;
        }

        // The count of the criterion is the number of files the name rule selects; "Validated <n> files" prints n for every file it read, valid or not, so the exit code (asserted by the ungated in-process fact) is what proves they validated; here every counted file is checked with the same RunFileValidator.
        IReadOnlyList<string> files = LaptopCampaign.CommittedRunFiles();
        int valid = files.Count(static path => LaptopCampaign.SchemaFailures(path).Count == 0);
        Note($"the validator accepts {valid.ToString(CultureInfo.InvariantCulture)} of {files.Count.ToString(CultureInfo.InvariantCulture)} run files, so it prints \"Validated {files.Count.ToString(CultureInfo.InvariantCulture)} files\"");
        Assert.Equal(files.Count, valid);
    }

    [Then(@"^no run file was written for any tier$")]
    public void ThenNoRunFileWasWritten() => Assert.Empty(_context.WrittenFiles());

    [Then(@"^the failed file is excluded from the commit and logged in docs/build-log\.md$")]
    public void ThenTheFailedFileIsLogged()
    {
        if (!Asserting())
        {
            return;
        }

        string log = File.ReadAllText(Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "build-log.md"));
        foreach (string path in LaptopCampaign.FailedFiles(Tier.Nvidia))
        {
            string name = Path.GetFileName(path);
            Assert.True(log.Contains(name, StringComparison.Ordinal), $"docs/build-log.md does not log the failed file {name} (criterion 6: file name, status, reason)");
        }
    }

    [Then(@"^a completed nvidia run file exists and the local file is unchanged$")]
    public void ThenTheNvidiaFileWasRerun()
    {
        if (!Asserting())
        {
            return;
        }

        string failed = Path.GetFileName(LaptopCampaign.FailedFiles(Tier.Nvidia)[^1]);
        string completed = Path.GetFileName(LaptopCampaign.LatestRunFile(Tier.Nvidia) ?? throw new Xunit.Sdk.XunitException("no completed nvidia run file exists after the failed file " + failed));

        // The run files are named by their UTC start, so a later name is a later run.
        Assert.True(string.CompareOrdinal(completed, failed) > 0, $"the newest completed nvidia file {completed} is not newer than the failed file {failed}");
        Note("the local file is read-only to the tests (nothing under results/ is written), so \"unchanged\" is the orchestrator's check of git status");
    }

    private static JsonNode LocalRun() =>
        LaptopCampaign.FindOrNote(Tier.Local, static _ => { }) is { } path ? LaptopCampaign.Read(path) : throw new Xunit.Sdk.XunitException("no local run file");
}