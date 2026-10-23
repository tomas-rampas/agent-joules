using System.Globalization;

using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-080: <c>AJ validate</c> checks run files against the embedded schema and the model (REQ-015). Scoped to the feature because sentences such as "the exit code is 2" are reused by other stories with
/// other meanings. The state lives in <see cref="ValidateCommandContext"/>. Every scenario runs the real command line through the harness, in a working directory that has no <c>config</c> and no <c>docs</c> folder.
/// </summary>
[Binding]
[Scope(Feature = "AJ-080 Validate run files with AJ validate")]
public sealed class ValidateCommandSteps(ValidateCommandContext context)
{
    // A command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly ValidateCommandContext _context = context;

    private string[] StdoutLines => [.. _context.Required().Stdout.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0)];

    [Given(@"^a folder with (\d+) valid run files and (\d+) probe file$")]
    public void GivenAFolderWithRunFilesAndAProbeFile(int runFiles, int probeFiles)
    {
        _context.CreateEmptyWorkingDirectory();
        _context.WriteFiles(runFiles, probeFiles);
    }

    [Given(@"^a working directory that has no docs folder$")]
    public void GivenAWorkingDirectoryWithoutDocs()
    {
        _context.CreateEmptyWorkingDirectory();
        Assert.False(Directory.Exists(Path.Combine(_context.Root, "docs")));
        Assert.False(Directory.Exists(Path.Combine(_context.Root, "config")));
    }

    [Given(@"^a folder with one run file where cost has label ""([^""]*)"" and value ([0-9.]+)$")]
    public void GivenAFolderWithACorruptedRunFile(string label, string value) =>
        _context.WriteRunFileWithCost(label, double.Parse(value, CultureInfo.InvariantCulture));

    [Given(@"^a path that does not exist$")]
    public void GivenAPathThatDoesNotExist()
    {
        _context.CreateEmptyWorkingDirectory();
        _context.UseMissingPath();
    }

    [Given(@"^a run file name that holds the text ""([^""]*)""$")]
    public void GivenARunFileThatHoldsText(string text) => _context.WriteRunFileWithText(text);

    [When(@"^""validate <folder>"" is executed$")]
    public async Task WhenValidateFolderIsExecuted() => await _context.RunAsync().WaitAsync(HangGuard);

    [When(@"^""validate <folder>"" is executed on valid files$")]
    public async Task WhenValidateFolderIsExecutedOnValidFiles()
    {
        _context.WriteFiles(runFiles: 3, probeFiles: 0);
        await _context.RunAsync().WaitAsync(HangGuard);
    }

    [When(@"^""validate <path>"" is executed$")]
    public async Task WhenValidatePathIsExecuted() => await _context.RunAsync().WaitAsync(HangGuard);

    [Then(@"^the output contains ""([^""]*)"" and the exit code is (\d+)$")]
    public void ThenTheOutputContainsAndTheExitCodeIs(string expected, int exitCode)
    {
        Assert.Contains(expected, _context.Required().Stdout, StringComparison.Ordinal);
        Assert.True(exitCode == _context.Required().ExitCode, $"Expected exit code {exitCode} but got {_context.Required().ExitCode}; stderr: {_context.Required().Stderr}");
        Assert.Empty(_context.Required().Stderr);
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        Assert.True(expected == _context.Required().ExitCode, $"Expected exit code {expected} but got {_context.Required().ExitCode}; stdout: {_context.Required().Stdout}; stderr: {_context.Required().Stderr}");

        // An exit 2 must come from the command itself, never from the parse check (an unknown command or argument).
        Assert.DoesNotContain("(ParseError)", _context.Required().Stderr, StringComparison.Ordinal);
    }

    [Then(@"^one output line names the file and the cost location$")]
    public void ThenOneOutputLineNamesTheFileAndTheCostLocation()
    {
        string name = _context.FileName ?? throw new Xunit.Sdk.XunitException("No run file was written.");
        string[] matching = [.. StdoutLines.Where(line => line.StartsWith(name + ": /steps/0/cost", StringComparison.Ordinal))];

        Assert.NotEmpty(matching);
        Assert.All(matching, line => Assert.DoesNotContain(_context.Root, line, StringComparison.Ordinal));
        Assert.Equal("Validated 1 files", StdoutLines[^1]);
    }

    [Then(@"^the exit code is (\d+) and the output names the file$")]
    public void ThenTheExitCodeIsAndTheOutputNamesTheFile(int expected)
    {
        string name = _context.FileName ?? throw new Xunit.Sdk.XunitException("No run file was written.");

        Assert.True(expected == _context.Required().ExitCode, $"Expected exit code {expected} but got {_context.Required().ExitCode}; stderr: {_context.Required().Stderr}");
        Assert.DoesNotContain("(ParseError)", _context.Required().Stderr, StringComparison.Ordinal);
        Assert.Contains(StdoutLines, line => line.StartsWith(name + ": ", StringComparison.Ordinal));
    }
}