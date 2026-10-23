using System.Globalization;

using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-086: <c>AJ report</c> turns run files into the Markdown summary, the SVG chart and the HTML page (REQ-016, REQ-030). Scoped to the feature because sentences such as "the exit code is 2" are reused by other
/// stories with other meanings. The state lives in <see cref="ReportCommandContext"/>. Every scenario runs the real command line through the harness; the output folder is in the workspace of the scenario.
/// </summary>
[Binding]
[Scope(Feature = "AJ-086 Wire the report command")]
public sealed class ReportCommandSteps(ReportCommandContext context)
{
    // A command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private readonly ReportCommandContext _context = context;

    [Given(@"^the fixture results folder$")]
    public void GivenTheFixtureResultsFolder() => _context.UseFixtureFolder();

    [Given(@"^an empty results folder$")]
    public void GivenAnEmptyResultsFolder() => _context.UseEmptyFolder();

    [Given(@"^a path that does not exist$")]
    public void GivenAPathThatDoesNotExist() => _context.UseMissingPath();

    [Given(@"^a results folder with one run file where cost has label ""([^""]*)"" and value ([0-9.]+)$")]
    public void GivenAResultsFolderWithACorruptedRunFile(string label, string value) =>
        _context.WriteRunFileWithCost(label, double.Parse(value, CultureInfo.InvariantCulture));

    [When(@"^""report --in <fixtures> --out <tmp>"" is executed$")]
    public async Task WhenReportOnFixturesIsExecuted() => await _context.RunAsync().WaitAsync(HangGuard);

    [When(@"^""report --in <empty> --out <tmp>"" is executed$")]
    public async Task WhenReportOnTheEmptyFolderIsExecuted() => await _context.RunAsync().WaitAsync(HangGuard);

    [When(@"^""report --in <path>"" is executed$")]
    public async Task WhenReportOnThePathIsExecuted() => await _context.RunAsync().WaitAsync(HangGuard);

    [When(@"^""report --in <folder> --out <tmp>"" is executed$")]
    public async Task WhenReportOnTheFolderIsExecuted() => await _context.RunAsync().WaitAsync(HangGuard);

    [When(@"^the report is generated twice$")]
    public async Task WhenTheReportIsGeneratedTwice()
    {
        await _context.RunAsync().WaitAsync(HangGuard);
        Assert.Equal(0, _context.Required().ExitCode);
        await _context.RunAsync().WaitAsync(HangGuard);
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        CliRunResult result = _context.Required();

        Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");

        // An exit 2 must come from the command itself, never from the parse check (an unknown command or argument).
        Assert.DoesNotContain("(ParseError)", result.Stderr, StringComparison.Ordinal);
    }

    [Then(@"^summary\.md, joules-per-step\.svg and index\.html exist in the output folder$")]
    public void ThenTheThreeFilesExist()
    {
        foreach (string name in ReportCommandContext.OutputFiles)
        {
            string path = Path.Combine(_context.Outputs[0], name);

            Assert.True(File.Exists(path), $"{name} was not written");
            Assert.True(new FileInfo(path).Length > 0, $"{name} is empty");
        }
    }

    [Then(@"^the summary lists ""([^""]*)"", ""([^""]*)"", ""([^""]*)"" and ""([^""]*)""$")]
    public void ThenTheSummaryLists(string first, string second, string third, string fourth)
    {
        string summary = File.ReadAllText(Path.Combine(_context.Outputs[0], "summary.md"));

        foreach (string row in new[] { first, second, third, fourth })
        {
            Assert.Contains("| " + row + " |", summary, StringComparison.Ordinal);
        }
    }

    [Then(@"^the SHA-256 sums of the three files are equal between the two runs$")]
    public void ThenTheSumsAreEqual()
    {
        Assert.Equal(2, _context.Outputs.Count);
        Assert.NotEqual(_context.Outputs[0], _context.Outputs[1]);

        string[] first = _context.HashesOf(0);
        string[] second = _context.HashesOf(1);

        Assert.Equal(3, first.Length);
        Assert.Equal(first, second);
    }

    [Then(@"^the exit code is (\d+) and the output names the file$")]
    public void ThenTheExitCodeIsAndTheOutputNamesTheFile(int expected)
    {
        string name = _context.FileName ?? throw new XunitException("No run file was written.");
        CliRunResult result = _context.Required();

        Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stderr: {result.Stderr}");
        Assert.DoesNotContain("(ParseError)", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(result.Stdout.Split('\n').Select(static line => line.TrimEnd('\r')), line => line.StartsWith(name + ": ", StringComparison.Ordinal));
        Assert.DoesNotContain(_context.Root, result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_context.Outputs[0]), "the output folder was created although a check failed");
    }
}