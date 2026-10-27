using System.Globalization;
using System.Security.Cryptography;

using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-155: the final hardening evidence (the build output, the test summary, the format exit code, the CI workflow, the key-shaped-string scan and the record of the scan fallback) and, gated by
/// <c>AGENTJOULES_INTEGRATION</c>, the scan with the real keys. The captured outputs are written into the scenario; the commands themselves are run by the orchestrator. A key value is read only to compare and is never printed:
/// the assertions of the gated scenarios carry the variable name, an exit code and a count, and the assertion that could show a value is ordered after the one that proves it absent.
/// </summary>
[Binding]
[Scope(Tag = "AJ-S155")]
public sealed class EvidenceHardeningSteps(HardeningContext context, IReqnrollOutputHelper output)
{
    private static readonly string[] KeyVariables = ["NVIDIA_API_KEY", "NEBIUS_API_KEY", "NGC_API_KEY"];

    private readonly HardeningContext _context = context;
    private readonly IReqnrollOutputHelper _output = output;

    private static string TestSummaryText(int passed, int failed, int skipped) =>
        string.Create(CultureInfo.InvariantCulture, $"{(failed > 0 ? "Failed!" : "Passed!")}  - Failed: {failed,5}, Passed: {passed,5}, Skipped: {skipped,5}, Total: {passed + failed + skipped,5}, Duration: 1 s - AgentJoules.Specs.dll (net10.0)\n");

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

    [Given(@"^a captured build output containing ""([^""]*)"" and ""([^""]*)"" and exit code ([0-9]+)$")]
    public void GivenACleanBuild(string warnings, string errors, int exitCode)
    {
        string captured = "Build succeeded.\n    " + warnings + "\n    " + errors + "\n";
        _context.Check = () => EvidenceChecks.BuildOutputViolations(captured, exitCode);
    }

    [Given(@"^a captured build output containing ""([^""]*)""$")]
    public void GivenABuildWithWarnings(string warnings)
    {
        string captured = "Build succeeded.\n    " + warnings + "\n    0 Error(s)\n";
        _context.Check = () => EvidenceChecks.BuildOutputViolations(captured, 0);
    }

    [Given(@"^a captured test run with ([0-9]+) passed, ([0-9]+) failed and ([0-9]+) skipped$")]
    public void GivenATestRun(int passed, int failed, int skipped)
    {
        _context.Text = TestSummaryText(passed, failed, skipped);
        _context.ExitCode = failed > 0 ? 1 : 0;
        _context.Check = () => EvidenceChecks.TestRunViolations(_context.Text, _context.ExitCode);
    }

    [Given(@"^a captured test run with ([0-9]+) failed$")]
    public void GivenAFailingTestRun(int failed) => GivenATestRun(passed: 5, failed, skipped: 0);

    [Given(@"^a captured ""dotnet format --verify-no-changes"" exit code ([0-9]+)$")]
    public void GivenAFormatExit(int exitCode) => _context.Check = () => EvidenceChecks.FormatViolations(exitCode);

    [When(@"^the hardening check runs$")]
    [When(@"^the CI check runs$")]
    [When(@"^the secret scan runs$")]
    [When(@"^it is searched$")]
    [When(@"^the scan step runs$")]
    public void WhenTheCheckRuns() => _context.Result = (_context.Check ?? throw new Xunit.Sdk.XunitException("No rule was chosen by a Given step."))();

    [Then(@"^box 4 passes$")]
    public void ThenBox4Passes() => Assert.True(_context.Violations.Count == 0, string.Join("\n", _context.Violations));

    [When(@"^the hardening check formats the summary$")]
    public void WhenTheSummaryIsFormatted() => _context.Line = EvidenceChecks.TestSummaryLineOf(_context.Text ?? string.Empty, _context.ExitCode);

    [Then(@"^the line reads ""([^""]*)""$")]
    public void ThenTheLineReads(string line) => Assert.Equal(line, _context.Line);

    [Given(@"^the committed \.github/workflows/ci\.yml$")]
    public void GivenTheCommittedWorkflow()
    {
        _context.UseRepository();
        _context.Check = () => EvidenceChecks.CiFileViolationsAt(_context.Root);
    }

    [Then(@"^it contains windows-latest, ubuntu-latest, the build, format and test commands and a probe step$")]
    public void ThenItContainsTheTokens() =>
        Assert.True(_context.Violations.All(static violation => violation == "secrets reference in ci.yml"), string.Join("\n", _context.Violations));

    [Then(@"^it contains no ""([^""]*)""$")]
    public void ThenItContainsNo(string text)
    {
        Assert.DoesNotContain("secrets reference in ci.yml", _context.Violations);
        Assert.False(EvidenceChecks.ReadText(_context.Root, ".github/workflows/ci.yml")?.Contains(text, StringComparison.Ordinal) ?? true, "the workflow holds " + text);
    }

    [Given(@"^gitleaks is not installed$")]
    public void GivenGitleaksIsNotInstalled()
    {
        // The scenario's premise is recorded, not enforced: the laptop that runs it may have gitleaks, and the record is read either way.
        _output.WriteLine(GitleaksOnPath() ? "gitleaks is on the PATH of this machine; the fallback record is still checked" : "gitleaks is not on the PATH of this machine");
        // The fallback runs four commands (the pattern scan, then one per key); a clean tree makes each exit 1.
        int[] exitCodes = [1, 1, 1, 1];
        _context.Text = EvidenceChecks.ScanRecordLine(exitCodes);
        _context.Check = () => EvidenceChecks.ScanRecordViolations(_context.Text);
    }

    private static bool GitleaksOnPath()
    {
        string[] names = OperatingSystem.IsWindows() ? ["gitleaks.exe", "gitleaks.cmd", "gitleaks.bat"] : ["gitleaks"];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(folder => names.Any(name => File.Exists(Path.Combine(folder.Trim('"'), name))));
    }

    [Then(@"^the build log records ""([^""]*)"" with the exit codes of the four commands$")]
    public void ThenTheLogRecordsTheFallback(string name)
    {
        Assert.True(_context.Violations.Count == 0, string.Join("\n", _context.Violations));
        Assert.Contains(name, _context.Text, StringComparison.Ordinal);
        foreach (string command in EvidenceChecks.ScanCommandNames)
        {
            Assert.Contains(command + "=1 (1 = no hits)", _context.Text, StringComparison.Ordinal);
        }

        // The committed build log holds the same record and the passing test summary (read through the root of the repository).
        string buildLog = RepositoryRoot.ReadAllText("docs/build-log.md");
        IReadOnlyList<string> record = EvidenceChecks.ScanRecordViolations(buildLog);
        Assert.True(record.Count == 0, string.Join('\n', record));
        IReadOnlyList<string> summary = EvidenceChecks.TestSummaryRecordViolations(buildLog);
        Assert.True(summary.Count == 0, string.Join('\n', summary));
    }

    [Given(@"^a tracked fixture file containing a runtime-built ""nvapi-"" plus ([0-9]+) characters value$")]
    public void GivenATrackedFileWithAKeyShapedValue(int length)
    {
        Assert.True(GitRunner.Available(), "git is not on PATH");
        TempWorkspace workspace = _context.NewFixture();

        // Built at run time from a prefix and random characters, so no key-shaped literal sits in a source file.
        _context.Secret = string.Concat("nvapi", "-", Convert.ToHexString(RandomNumberGenerator.GetBytes(length))[..length].ToLowerInvariant());
        _context.TrackedName = "tracked-note.txt";
        _ = workspace.WriteFile(_context.TrackedName, "token=" + _context.Secret + "\n");
        Assert.Equal(0, GitRunner.Run(workspace.Root, "init", "--quiet", workspace.Root).ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "add", _context.TrackedName).ExitCode);
        _context.Check = () => EvidenceChecks.KeyShapedFileViolations(_context.Root);
    }

    [Given(@"^a workflow fixture containing ""([^""]*)""$")]
    public void GivenAWorkflowWithASecret(string reference)
    {
        TempWorkspace workspace = _context.NewFixture();
        string committed = RepositoryFiles.ReadOrNull(".github/workflows/ci.yml") ?? string.Empty;
        _ = workspace.WriteFile(".github/workflows/ci.yml", committed + "\n# env: ${{ " + reference + " }}\n");
        _context.Check = () => EvidenceChecks.CiFileViolationsAt(_context.Root);
    }

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure)
    {
        // A token built at run time must not appear in any violation (the value is not echoed); this is ordered before the assertion that would print the list.
        if (_context.Secret is not null)
        {
            Assert.False(_context.Violations.Any(violation => violation.Contains(_context.Secret, StringComparison.Ordinal)), "a violation carries the token (the value is not echoed)");
        }

        Assert.Contains(failure, _context.Violations);
    }

    [Given(@"^NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY are set$")]
    public void GivenTheKeysAreSet()
    {
        _context.OpenGate();
        if (_context.SkipNote is null)
        {
            foreach (string variable in KeyVariables)
            {
                Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)), variable + " is not set");
            }
        }
    }

    [When(@"^""git grep -nF"" runs for each value$")]
    public void WhenGitGrepRunsForEachValue()
    {
        if (_context.SkipNote is not null)
        {
            return;
        }

        foreach (string variable in KeyVariables)
        {
            _context.Scans.Add((variable, EvidenceChecks.TrackedFilesContaining(RepositoryRoot.Path, Environment.GetEnvironmentVariable(variable)!)));
        }
    }

    [Then(@"^each exits 1 and prints no line$")]
    public void ThenEachExitsOne()
    {
        if (Skipped())
        {
            return;
        }

        Assert.Equal(KeyVariables.Length, _context.Scans.Count);
        foreach ((string variable, GitResult result) in _context.Scans)
        {
            Assert.True(
                result.ExitCode == 1 && result.Stdout.Length == 0,
                variable + ": git grep exited " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + " and listed " + result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ToString(CultureInfo.InvariantCulture) + " file name(s) (the value is not echoed)");
        }
    }

    [Given(@"^a temporary tracked file containing the NGC_API_KEY value$")]
    public void GivenATrackedFileWithTheNgcKey()
    {
        _context.OpenGate("NGC_API_KEY");
        if (_context.SkipNote is not null)
        {
            return;
        }

        // The value is written to a temporary folder outside the repository for the lifetime of the scenario; the folder is deleted when the scenario ends.
        Assert.True(GitRunner.Available(), "git is not on PATH");
        TempWorkspace workspace = _context.NewFixture();
        _context.TrackedName = "tracked-note.txt";
        _ = workspace.WriteFile(_context.TrackedName, "token=" + _context.KeyValue!.Trim() + "\n");
        Assert.Equal(0, GitRunner.Run(workspace.Root, "init", "--quiet", workspace.Root).ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "add", _context.TrackedName).ExitCode);
    }

    [When(@"^""git grep -nF"" runs for it$")]
    public void WhenGitGrepRunsForIt()
    {
        if (_context.SkipNote is not null)
        {
            return;
        }

        _context.Scans.Add(("NGC_API_KEY", EvidenceChecks.TrackedFilesContaining(_context.Root, _context.KeyValue!)));
    }

    [Then(@"^the scan fails and prints the file name only$")]
    public void ThenTheScanFailsAndPrintsTheFileName()
    {
        if (Skipped())
        {
            return;
        }

        GitResult result = Assert.Single(_context.Scans).Result;

        // Ordered first and worded without the output: a failure of this assertion must not show the value.
        Assert.False(result.Stdout.Contains(_context.KeyValue!.Trim(), StringComparison.Ordinal), "the scan output contains the key value (the value is not echoed)");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(_context.TrackedName, result.Stdout.Trim());
    }
}