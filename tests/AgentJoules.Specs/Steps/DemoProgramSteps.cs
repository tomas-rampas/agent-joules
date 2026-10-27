using System.Text.Json.Nodes;

using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-135: the demo program end to end (REQ-027, REQ-028, REQ-032). Scoped to the feature because sentences such as "it fails with" and "the exit code is 0" belong to every documentation story. The Given steps read the
/// committed script, story file, README, task files and run files through <see cref="RepositoryRoot"/> and the negative ones mutate a copy in memory or edit the set of repository paths, so no file is created or deleted
/// in the repository. The help of a command, the replay and the dashboard dry run are the real commands in process (<see cref="ReadmeGuidesContext"/>, <see cref="ReplayContext"/>,
/// <see cref="DashboardServerContext"/>); the ignore rule is read from <c>.gitignore</c> (no <c>git check-ignore</c> process is started).
/// </summary>
[Binding]
[Scope(Feature = "AJ-135 Demo program end to end")]
public sealed class DemoProgramSteps(DemoProgramContext context, ReplayContext replay, ReadmeGuidesContext guides, DashboardServerContext dashboard)
{
    private const string ScriptPath = "content/demo-script.md";
    private const string StoryPath = "docs/plan/stories/M6-docs-content-demo.md";
    private const string BuildLogPath = "docs/build-log.md";
    private const string ReportPage = "docs/report/index.html";

    private static readonly string[] ShapeTiers = ["local", "nvidia", "nebius"];

    private readonly DemoProgramContext _context = context;
    private readonly ReplayContext _replay = replay;
    private readonly ReadmeGuidesContext _guides = guides;
    private readonly DashboardServerContext _dashboard = dashboard;

    private static string CommittedScript() => RepositoryRoot.ReadAllText(ScriptPath);

    private static string StoryText() => RepositoryRoot.ReadAllText(StoryPath);

    [AfterScenario]
    public async Task DisposeTheRigs()
    {
        await _replay.DisposeAsync();
        await _dashboard.DisposeAsync();
    }

    [Given(@"^the committed content/demo-script\.md$")]
    public void GivenTheCommittedScript() => _context.Script = CommittedScript();

    [Given(@"^the committed content/demo-script\.md and the story file$")]
    public void GivenTheScriptAndTheStory()
    {
        _context.Script = CommittedScript();
        _context.Story = RepositoryRoot.ReadAllText(StoryPath);
    }

    [Given(@"^the committed content/demo-script\.md and the repository files$")]
    public void GivenTheScriptAndTheFiles()
    {
        _context.Script = CommittedScript();
        _context.Files = RepositoryFiles.Files();
    }

    [Given(@"^the committed content/demo-script\.md and the README$")]
    public void GivenTheScriptAndTheReadme()
    {
        _context.Script = CommittedScript();
        _context.Readme = RepositoryRoot.ReadAllText("README.md");
    }

    [Given(@"^a committed nebius run file fixture with 36 steps$")]
    public void GivenACommittedNebiusRunFile()
    {
        string relative = RepositoryFiles.LatestRunFile("nebius") ?? throw new Xunit.Sdk.XunitException("results/ holds no nebius run file");
        _replay.UseCommittedFile(Tier.Nebius, RepositoryRoot.Resolve(relative));
        Assert.Equal(36, _replay.Run!.Steps.Count);
    }

    [Given(@"^no file at results/missing\.json$")]
    public static void GivenNoFileAtResultsMissing() => Assert.False(RepositoryFiles.Exists("results/missing.json"));

    [Given(@"^a copy of the script without the sentence about recording without a browser open$")]
    public void GivenAScriptWithoutTheBrowserSentence()
    {
        _context.Script = DocumentChecks.WithoutPhrase(CommittedScript(), "recorded without a browser", "recorded elsewhere");
        Assert.NotNull(DocumentChecks.BrowserStatement(_context.Script));
    }

    [Given(@"^the six CLI commands used by the demo$")]
    public static void GivenTheSixCommands() => Assert.Equal(6, DocumentChecks.CliCommands.Count);

    [Given(@"^the latest committed run files for local, nvidia and nebius$")]
    public void GivenTheLatestRunFiles() => _context.RunFiles = new Dictionary<string, string>(RepositoryFiles.RunFiles(), StringComparer.Ordinal);

    [Given(@"^a workspace with run files for local, nvidia and nebius only$")]
    public static void GivenAWorkspaceWithThreeTiers()
    {
        // The workspace is laid out by the When step: the fixture run files of three tiers and no cloudgpu file.
    }

    [Given(@"^a copy of the script whose block contains ""([^""]*)""$")]
    public void GivenAScriptWhoseBlockContains(string command)
    {
        _context.Script = DocumentChecks.WithBlockLine(CommittedScript(), DocumentChecks.DemoProgramHeading, command);
        Assert.Contains(command, _context.Script, StringComparison.Ordinal);
    }

    [Given(@"^a copy of the script whose demo step (\d+) reads ""([^""]*)""$")]
    public void GivenAScriptWhoseStepReads(int step, string command)
    {
        string script = CommittedScript();
        string original = DocumentChecks.DemoProgram(script, StoryText()).Commands[step - 1].Text;
        string[] lines = script.Split('\n');
        int at = Array.IndexOf(lines, original);
        Assert.True(at >= 0, "the step is not a whole line of the committed script");
        lines[at] = command;
        _context.Script = string.Join('\n', lines);
        Assert.NotEqual(script, _context.Script);
    }

    [Given(@"^a workspace where the cloudgpu probe file is renamed$")]
    public void GivenAWorkspaceWithARenamedCloudGpuProbe()
    {
        _context.Script = CommittedScript();
        _context.Files = RepositoryFiles.Files();
        _context.Other = Assert.Single(_context.Files, static file => file.StartsWith("results/probe-", StringComparison.Ordinal) && file.EndsWith("-cloudgpu.json", StringComparison.Ordinal));
        _ = _context.Files.Remove(_context.Other);
        _ = _context.Files.Add(_context.Other + ".bak");
    }

    [Given(@"^a workspace without any cloudgpu file$")]
    public void GivenAWorkspaceWithoutCloudGpuFiles()
    {
        _context.Script = CommittedScript();
        _context.Files = RepositoryFiles.Files();
        Assert.True(_context.Files.RemoveWhere(static file => file.Contains("cloudgpu", StringComparison.Ordinal)) > 0);
    }

    [Given(@"^a workspace where docs/report/index\.html is deleted$")]
    public void GivenAWorkspaceWithoutTheReportPage()
    {
        _context.Script = CommittedScript();
        _context.Files = RepositoryFiles.Files();
        Assert.True(_context.Files.Remove(ReportPage), "the committed report page is not among the repository files");
    }

    [Given(@"^a latest local run file with run\.repeat 1$")]
    public void GivenALocalRunFileWithOneRepeat()
    {
        _context.RunFiles = new Dictionary<string, string>(RepositoryFiles.RunFiles(), StringComparer.Ordinal);
        string name = _context.RunFiles.Keys.Where(static key => key.EndsWith("Z-local.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Last();
        _context.RunFiles[name] = RunDocumentFixtures.Edit(_context.RunFiles[name], static root => root["run"]!["repeat"] = 1);
    }

    [Given(@"^a copy of the script without the heading ""([^""]*)""$")]
    public void GivenAScriptWithoutTheHeading(string heading)
    {
        _context.Script = DocumentChecks.WithoutHeading(CommittedScript(), heading);
        Assert.NotNull(DocumentChecks.HasHeadingAtAnyLevel(_context.Script, heading));
    }

    [Given(@"^a workspace where docs/report/dashboard\.png exists and starts with the PNG signature$")]
    public void GivenAWorkspaceWithTheScreenshot()
    {
        _context.ScreenshotScenario = true;
        _context.ScreenshotBytes = [.. DocumentChecks.PngHeader(), 0, 0, 0, 13];
    }

    [Given(@"^a workspace where the screenshot is saved as docs/report/dash\.png and docs/report/dashboard\.png is absent$")]
    public void GivenAScreenshotUnderAnotherName()
    {
        _context.ScreenshotScenario = true;
        _context.ScreenshotBytes = null;
        _context.Files = ["docs/report/dash.png"];
    }

    [Given(@"^a build log with the line ""([^""]*)""$")]
    public void GivenABuildLogWithTheLine(string line) => _context.BuildLog = "# Build log\n\n" + line + "\n";

    [Given(@"^the committed docs/build-log\.md$")]
    public void GivenTheCommittedBuildLog()
    {
        _context.BuildLog = RepositoryRoot.ReadAllText(BuildLogPath);
        Assert.Single(_context.BuildLog.Split('\n'), static line => line.Contains("Dashboard width check: 1280 ok, 1920 ok", StringComparison.Ordinal));
    }

    [Given(@"^a copy of the script whose shot (\d+) lasts (\d+) seconds$")]
    public void GivenAShotThatLastsLonger(int shot, int seconds)
    {
        _context.Script = DocumentChecks.WithShotDuration(CommittedScript(), shot, seconds);
        _context.Story = RepositoryRoot.ReadAllText(StoryPath);
        Assert.NotEqual(CommittedScript(), _context.Script);
    }

    [Given(@"^the task files of the repository$")]
    public static void GivenTheTaskFiles() => Assert.NotEmpty(RepositoryFiles.TaskFiles());

    [Given(@"^the committed results folder$")]
    public static void GivenTheResultsFolder() => Assert.NotEmpty(RepositoryFiles.ResultsFileNames());

    [Given(@"^the committed report page$")]
    public static void GivenTheReportPage() => Assert.True(RepositoryFiles.Exists(ReportPage), "docs/report/index.html does not exist");

    [Given(@"^the committed \.gitignore$")]
    public void GivenTheCommittedGitignore() => _context.Other = RepositoryRoot.ReadAllText(".gitignore");

    [Given(@"^a \.gitignore without an artifacts line$")]
    public void GivenAGitignoreWithoutArtifacts() => _context.Other = "bin/\nobj/\n";

    [Given(@"^the committed repository$")]
    public void GivenTheCommittedRepository() => _context.Readme = RepositoryRoot.ReadAllText("README.md");

    [Given(@"^the run command with fake keys, a scripted GPU and the dashboard enabled$")]
    public void GivenTheDashboardDryRunSetup()
    {
        _dashboard.UseDryRunSetup();
        _dashboard.UseFreePorts();
    }

    [When(@"^the fenced block under ""## Demo program"" is parsed$")]
    public void WhenTheBlockIsParsed() => _context.Failures = DocumentChecks.DemoProgram(_context.Script, StoryText()).Failures;

    [When(@"^""dashboard --replay <file> --speed 50 --exit-after 5"" runs in process$")]
    public async Task WhenTheReplayRuns() => await _replay.RunWithClientAsync("dashboard --replay " + ReplayContext.FixtureToken + " --speed 50 --exit-after 5");

    [When(@"^""dashboard --replay results/missing\.json"" runs$")]
    public async Task WhenTheMissingReplayRuns() => await _replay.RunAsync("dashboard --replay results/missing.json");

    [When(@"^the demo check runs$")]
    public void WhenTheDemoCheckRuns() => _context.Failures = DocumentChecks.DemoScriptStructure(_context.Script, StoryText());

    [When(@"^each is run with ""--help"" in process$")]
    public async Task WhenEachIsRunWithHelp()
    {
        foreach (string command in DocumentChecks.CliCommands)
        {
            _ = await _guides.RunHelpAsync(command);
        }
    }

    [When(@"^run\.repeat and the step counts are read$")]
    [When(@"^the demo shape check runs$")]
    public void WhenTheShapeIsChecked() => _context.Failures = DocumentChecks.DemoRunShape(_context.RunFiles, ShapeTiers);

    [When(@"^""report --in results/ --out docs/report/"" runs$")]
    public async Task WhenTheReportRuns() => await _context.RunReportWithoutCloudGpuAsync();

    [When(@"^the artifact check runs$")]
    public void WhenTheArtifactCheckRuns() => _context.Failures = DocumentChecks.MissingArtifacts(_context.Script, _context.Files);

    [When(@"^the demo artifact check runs$")]
    public void WhenTheDemoArtifactCheckRuns()
    {
        var failures = new List<string>();
        if (_context.ScreenshotScenario)
        {
            _context.Screenshot = DocumentChecks.ScreenshotCheck(_context.ScreenshotBytes);
            if (_context.Screenshot.State == ScreenshotState.Invalid)
            {
                failures.Add(_context.Screenshot.Message);
            }
        }

        if (_context.BuildLog is not null)
        {
            failures.AddRange(DocumentChecks.WidthCheck(_context.BuildLog));
        }

        _context.Failures = failures;
    }

    [When(@"^the shot tables are compared$")]
    public void WhenTheShotTablesAreCompared() => _context.Failures = DocumentChecks.ShotTableMatchesStory(_context.Script, _context.Story);

    [When(@"^the task names are read from the name field$")]
    public void WhenTheTaskNamesAreRead()
    {
        (IReadOnlyList<string> names, IReadOnlyList<string> failures) = DocumentChecks.TaskSet(RepositoryFiles.TaskFiles());
        _context.TaskNames = names;
        _context.Failures = failures;
    }

    [When(@"^""validate results/"" runs on the repository$")]
    public async Task WhenValidateRuns() => await _context.ValidateCommittedResultsAsync();

    [When(@"^its script tags are counted$")]
    public void WhenScriptTagsAreCounted()
    {
        string html = RepositoryRoot.ReadAllText(ReportPage);
        _context.Count = DocumentChecks.CountOf(html, "<script");
    }

    [When(@"^the ignore rule for artifacts is read$")]
    public void WhenTheIgnoreRuleIsRead() => _context.Failures = DocumentChecks.ArtifactsIgnored(_context.Other) is { } failure ? [failure] : [];

    [When(@"^the screenshot is checked$")]
    public void WhenTheScreenshotIsChecked()
    {
        string path = RepositoryRoot.Resolve(DocumentChecks.DashboardScreenshot);
        _context.ScreenshotBytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        _context.Screenshot = DocumentChecks.ScreenshotCheck(_context.ScreenshotBytes);
    }

    [When(@"^the browser statement is looked for$")]
    public void WhenTheBrowserStatementIsLookedFor() =>
        _context.Failures = [.. new[] { DocumentChecks.BrowserStatement(_context.Script), DocumentChecks.BrowserStatement(_context.Readme) }.OfType<string>()];

    [When(@"^""run --tier local --dashboard --dry-run"" runs in process$")]
    public async Task WhenTheDashboardDryRunRuns() => await _dashboard.RunAsync("run --tier local --dashboard --dry-run");

    [Then(@"^it holds ten commands whose verbs are probe, run, run, calibrate, cloudgpu, validate, report, report, run, dashboard in that order$")]
    public void ThenItHoldsTenCommands()
    {
        DemoProgramResult program = DocumentChecks.DemoProgram(_context.Script, StoryText());

        Assert.Empty(program.Failures);
        Assert.Equal(DocumentChecks.DemoVerbs, program.Commands.Select(static command => command.Verb).ToArray());
        Assert.All(program.Commands.Where(static command => command.Verb != "cloudgpu"), static command => Assert.StartsWith("dotnet run --project src/AgentJoules.Cli -- ", command.Text, StringComparison.Ordinal));
        Assert.StartsWith("./AgentJoules.Cli", program.Commands[4].Text, StringComparison.Ordinal);
    }

    [Then(@"^the exit code is 0 and 36 events were emitted, one per step$")]
    public void ThenThirtySixEventsWereEmitted()
    {
        Assert.True(_replay.ExitCode == 0, $"exit {_replay.ExitCode}; stderr: {_replay.Result?.Stderr}");
        Assert.Equal(36, _replay.Log!.Count("step-completed"));
        Assert.Equal(36, _replay.Log.Count("step-started"));
    }

    [Then(@"^the exit code is not 0 and the message names the file$")]
    public void ThenTheMissingFileIsNamed()
    {
        Assert.NotEqual(0, _replay.ExitCode ?? -1);
        Assert.Contains("missing.json", _replay.Result!.Stderr + _replay.Result.Stdout, StringComparison.Ordinal);
    }

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) => Assert.Contains(failure, _context.Failures);

    [Then(@"^it fails with ""missing artifact"" and the path of the renamed file$")]
    public void ThenTheRenamedFileIsMissing() => Assert.Contains("missing artifact: " + _context.Other, _context.Failures);

    [Then(@"^it reports no failure$")]
    [Then(@"^it passes$")]
    [Then(@"^the width check passes$")]
    public void ThenNoFailure() => Assert.Empty(_context.Failures);

    [Then(@"^every exit code is 0$")]
    public void ThenEveryExitCodeIsZero()
    {
        foreach (string command in DocumentChecks.CliCommands)
        {
            Assert.True(0 == _guides.Help[command].ExitCode, $"{command} --help exited {_guides.Help[command].ExitCode}");
        }
    }

    [Then(@"^each file has run\.repeat 3 and 36 steps and no step energy is written as 0 where the label is N/A$")]
    public void ThenEachFileHasTheDemoShape()
    {
        Assert.Empty(_context.Failures);
        foreach (string tier in ShapeTiers)
        {
            string name = _context.RunFiles.Keys.Where(key => key.EndsWith($"Z-{tier}.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Last();
            Assert.Equal(3, JsonNode.Parse(_context.RunFiles[name])!["run"]!["repeat"]!.GetValue<int>());
        }
    }

    [Then(@"^the exit code is 0 and summary\.md contains the row ""cloudgpu \| not run""$")]
    public void ThenTheSummaryHasTheNotRunRow()
    {
        Assert.True(_context.Result!.ExitCode == 0, _context.Result.Stderr);
        Assert.Contains("cloudgpu | not run", _context.Summary ?? string.Empty, StringComparison.Ordinal);
    }

    [Then(@"^it passes for the screenshot$")]
    public void ThenTheScreenshotPasses()
    {
        Assert.Equal(ScreenshotState.Present, _context.Screenshot!.State);
        Assert.Empty(_context.Failures);
    }

    [Then(@"^it reports ""open user action: docs/report/dashboard\.png"" and the README has no dashboard link$")]
    public async Task ThenTheScreenshotIsAnOpenAction()
    {
        Assert.Equal(ScreenshotState.OpenUserAction, _context.Screenshot!.State);
        Assert.Equal("open user action: docs/report/dashboard.png", _context.Screenshot.Message);
        Assert.Empty(_context.Failures);

        // The README the report command writes in a workspace without docs/report/dashboard.png carries no link to it.
        _guides.PrepareWorkspace(RepositoryRoot.ReadAllText("README.md"), screenshotExists: false, regenerate: true);
        Assert.Equal(0, (await _guides.UpdateWorkspaceReadmeAsync()).ExitCode);
        Assert.DoesNotContain(DocumentChecks.DashboardScreenshot, _guides.WorkspaceReadme, StringComparison.Ordinal);
        Assert.Null(DocumentChecks.DashboardLink(_guides.WorkspaceReadme, screenshotExists: false));
    }

    [Then(@"^all nine rows are equal in shot number, start and duration$")]
    public void ThenAllNineRowsAreEqual()
    {
        Assert.Empty(_context.Failures);
        Assert.Equal(9, DocumentChecks.ShotTables(_context.Script).First(static table => !table.Fallback).Rows.Count);
    }

    [Then(@"^there are three task files and the task of the video command is one of them$")]
    public void ThenThereAreThreeTasks()
    {
        Assert.Empty(_context.Failures);
        Assert.Equal(3, _context.TaskNames.Count);
        string? videoTask = DocumentChecks.DemoVideoTask(CommittedScript());
        Assert.NotNull(videoTask);
        Assert.Contains(videoTask, _context.TaskNames);
    }

    [Then(@"^the exit code is 0 and it prints ""Validated n files"" with n the number of run files$")]
    public void ThenValidateCountsTheRunFiles()
    {
        Assert.True(_context.Result!.ExitCode == 0, _context.Result.Stdout + _context.Result.Stderr);
        Assert.Equal(DocumentChecks.ExpectedValidatedCount(RepositoryFiles.ResultsFileNames()), DocumentChecks.ValidatedFiles(_context.Result.Stdout));
    }

    [Then(@"^there are none$")]
    public void ThenThereAreNone() => Assert.Equal(0, _context.Count);

    [Then(@"^artifacts/ is ignored$")]
    public void ThenArtifactsIsIgnored() => Assert.Empty(_context.Failures);

    [Then(@"^it is a PNG or an open user action and the README link rule holds$")]
    public void ThenTheScreenshotAgrees()
    {
        Assert.NotEqual(ScreenshotState.Invalid, _context.Screenshot!.State);
        Assert.Null(DocumentChecks.DashboardLink(_context.Readme, _context.ScreenshotBytes is not null));
    }

    [Then(@"^both state that the published results were recorded without a browser$")]
    public void ThenBothStateTheBrowserRule() => Assert.Empty(_context.Failures);

    [Then(@"^the output has a Dashboard line with ""http://127\.0\.0\.1:"" and the exit code is 0$")]
    public void ThenTheDashboardLineIsPrinted()
    {
        Assert.True(_dashboard.Result!.ExitCode == 0, _dashboard.Result.Stderr);
        string line = Assert.Single(RunOrchestrationFixtures.StdoutLines(_dashboard.Result), static candidate => candidate.StartsWith("Dashboard:", StringComparison.Ordinal));
        Assert.Contains("http://127.0.0.1:", line, StringComparison.Ordinal);
    }
}