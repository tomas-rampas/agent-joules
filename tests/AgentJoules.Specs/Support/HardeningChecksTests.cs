using System.Text;

using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.Specs.Support;

/// <summary>
/// One row for each additive rule of the AJ-154 and AJ-155 parts of <see cref="EvidenceChecks"/> that no scenario of the two stories exercises (negative rows assert the violation text, positive rows assert none).
/// Each row lays a minimal fixture out in a <see cref="TempWorkspace"/> or in a string; the rows named Live read the committed repository and nothing else. No row reads the network, a GPU or a key.
/// </summary>
public sealed class HardeningChecksTests
{
    private static string Matrix(Func<string, string>? row = null, string caption = "## Acceptance matrix")
    {
        StringBuilder text = new("# Build log\n\n" + caption + "\n\n| ID | Command or artifact | Observed | Status | Reason |\n|---|---|---|---|---|\n");
        foreach (string id in EvidenceChecks.MatrixIds)
        {
            _ = text.Append(row is null ? $"| {id} | dotnet test | passed | MET | |" : row(id)).Append('\n');
        }

        return text.ToString();
    }

    private static string Filter(Func<string, string>? row = null)
    {
        StringBuilder text = new("## Spec filter results\n\n| ID | Passed | Failed | Skipped | Exit code |\n|---|---|---|---|---|\n");
        foreach (string id in EvidenceChecks.SpecFilterIds)
        {
            _ = text.Append(row is null ? $"| Spec={id} | 2 | 0 | 0 | 0 |" : row(id)).Append('\n');
        }

        return text.ToString();
    }

    private static string CleanBuild() => "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n";

    private static void AssertViolation(IReadOnlyList<string> violations, string expected) =>
        Assert.True(violations.Contains(expected, StringComparer.Ordinal), "expected: " + expected + "\nactual:\n" + string.Join("\n", violations));

    [Fact]
    public void ListsHaveTheStoryCounts()
    {
        Assert.Equal(51, EvidenceChecks.MatrixIds.Count);
        Assert.Equal(36, EvidenceChecks.SpecFilterIds.Count);
        Assert.Equal(7, EvidenceChecks.SourceProjects.Count);
        Assert.Equal(4, EvidenceChecks.TestProjects.Count);
    }

    [Fact]
    public void MatrixAcceptsAFullTableAndReasonedRows()
    {
        Assert.Empty(EvidenceChecks.AcceptanceMatrixViolations(Matrix()));
        Assert.Empty(EvidenceChecks.AcceptanceMatrixViolations(Matrix(caption: "**Acceptance matrix**")));
        Assert.Empty(EvidenceChecks.AcceptanceMatrixViolations(Matrix(id => id == "REQ-024" ? $"| {id} | first CI run | not run yet | USER-ACTION | the push is the user's |" : $"| {id} | dotnet test | passed | MET | |")));
    }

    [Theory]
    [InlineData("REQ-001", "| REQ-001 | dotnet test | passed |  | |", "empty status: REQ-001")]
    [InlineData("REQ-002", "| REQ-002 | dotnet test | passed | PASS | |", "unknown status: REQ-002 (PASS)")]
    [InlineData("EDGE-010", "| EDGE-010 |  | passed | MET | |", "empty evidence: EDGE-010")]
    [InlineData("REQ-024", "| REQ-024 | first CI run | not run | USER-ACTION | |", "USER-ACTION without reason: REQ-024")]
    [InlineData("REQ-020", "| REQ-020 | a run | blocked | BLOCKED |", "BLOCKED without reason: REQ-020")]
    public void MatrixRejectsABadRow(string id, string replacement, string expected)
    {
        string text = Matrix(row => row == id ? replacement : $"| {row} | dotnet test | passed | MET | |");

        AssertViolation(EvidenceChecks.AcceptanceMatrixViolations(text), expected);
    }

    [Fact]
    public void MatrixRejectsADuplicateAnUnexpectedRowAndAMissingTable()
    {
        string text = Matrix() + "| REQ-001 | dotnet test | passed | MET | |\n| REQ-033 | dotnet test | passed | MET | |\n";

        IReadOnlyList<string> violations = EvidenceChecks.AcceptanceMatrixViolations(text);

        AssertViolation(violations, "duplicate row: REQ-001");
        AssertViolation(violations, "unexpected row: REQ-033");
        AssertViolation(EvidenceChecks.AcceptanceMatrixViolations("# Build log\n\nNo table here.\n"), "no Acceptance matrix table");
        AssertViolation(EvidenceChecks.AcceptanceMatrixViolations(null), "docs/build-log.md is missing");
    }

    [Fact]
    public void MatrixReportsAMalformedIdRow()
    {
        string text = Matrix() + "| EDGE-19 | x | y | UNMET | |\n";

        AssertViolation(EvidenceChecks.AcceptanceMatrixViolations(text), "malformed row: EDGE-19");
    }

    [Theory]
    [InlineData("Acceptance matrix notes follow.")]
    [InlineData("**Acceptance matrix** was reviewed again.")]
    [InlineData("Table: Acceptance matrix, second reading")]
    public void MatrixKeepsTheHeadingCaptionWhenALaterLineRepeatsTheTitle(string later)
    {
        string text = Matrix() + "\n" + later + "\n\n| ID | Note |\n|---|---|\n| REQ-001 | x |\n";

        Assert.Empty(EvidenceChecks.AcceptanceMatrixViolations(text));
    }

    [Fact]
    public void MatrixFallsBackToABoldCaptionWhenNoHeadingNamesTheTable()
    {
        Assert.Equal(51, EvidenceChecks.AcceptanceMatrixRowIds(Matrix(caption: "**Acceptance matrix**")).Count);
    }

    [Fact]
    public void MatrixIgnoresTablesOfOtherEntriesWithTheSameIds()
    {
        string other = "## Another entry\n\n| ID | Result |\n|---|---|\n| REQ-001 | PASS |\n\n";

        Assert.Empty(EvidenceChecks.AcceptanceMatrixViolations(other + Matrix() + "\n## Later entry\n\n| REQ-001 | x |\n"));
    }

    [Fact]
    public void FilterAcceptsAFullTableWithEitherIdForm()
    {
        Assert.Empty(EvidenceChecks.SpecFilterViolations(Filter()));
        Assert.Empty(EvidenceChecks.SpecFilterViolations(Filter(id => $"| {id} | 1 | 0 | 3 | 0 |")));
        Assert.Equal(36, EvidenceChecks.SpecFilterRowIds(Filter()).Count);
    }

    [Theory]
    [InlineData("| Spec=REQ-003 | 0 | 0 | 0 | 0 |", "REQ-003 has no passed test")]
    [InlineData("| Spec=REQ-003 | 3 | 1 | 0 | 1 |", "REQ-003 has failed tests")]
    [InlineData("| Spec=REQ-003 | 3 | 0 | 0 | 1 |", "REQ-003 exit code is 1")]
    [InlineData("| Spec=REQ-003 | three | 0 | 0 | 0 |", "REQ-003 has a missing or non-numeric count")]
    [InlineData("| Spec=REQ-003 | 3 | 0 | 0 |", "REQ-003 has a missing or non-numeric count")]
    public void FilterRejectsABadRow(string replacement, string expected) =>
        AssertViolation(EvidenceChecks.SpecFilterViolations(Filter(id => id == "REQ-003" ? replacement : $"| Spec={id} | 2 | 0 | 0 | 0 |")), expected);

    [Fact]
    public void FilterRejectsAnUnexpectedRow()
    {
        string text = Filter() + "| Spec=EDGE-099 | 0 | 5 | 0 | 1 |\n";

        AssertViolation(EvidenceChecks.SpecFilterViolations(text), "unexpected row: EDGE-099");
    }

    [Fact]
    public void FilterRejectsAMissingDuplicateAndAbsentTable()
    {
        string text = Filter(id => id == "EDGE-019" ? "| Spec=EDGE-999 | 2 | 0 | 0 | 0 |" : $"| Spec={id} | 2 | 0 | 0 | 0 |") + "| Spec=REQ-003 | 2 | 0 | 0 | 0 |\n";

        IReadOnlyList<string> violations = EvidenceChecks.SpecFilterViolations(text);

        AssertViolation(violations, "missing row: EDGE-019");
        AssertViolation(violations, "duplicate row: REQ-003");
        AssertViolation(EvidenceChecks.SpecFilterViolations("# Build log\n"), "no Spec filter results table");
    }

    [Theory]
    [InlineData("OpenTelemetry.Exporter.Console", "1.18.0", null)]
    [InlineData("Plotly.NET", "5.0.0", "out-of-scope package: Plotly.NET")]
    [InlineData("Microsoft.SemanticKernel", "1.0.0", "out-of-scope package: Microsoft.SemanticKernel")]
    [InlineData("Microsoft.Agents.AI.DevUI", "1.0.0", "out-of-scope package: Microsoft.Agents.AI.DevUI")]
    [InlineData("ScottPlot", "5.0.0", "out-of-scope package: ScottPlot")]
    [InlineData("OxyPlot.Core", "2.1.0", "out-of-scope package: OxyPlot.Core")]
    [InlineData("Microsoft.Agents.AI.Mcp", "1.0.0", "out-of-scope package: Microsoft.Agents.AI.Mcp")]
    [InlineData("Microsoft.Agents.AI.A2A", "1.0.0", "out-of-scope package: Microsoft.Agents.AI.A2A")]
    [InlineData("Some.Package", "1.0.0-alpha.3", "prerelease package: Some.Package 1.0.0-alpha.3")]
    [InlineData("Some.Package", "2.0.0-preview.1", "prerelease package: Some.Package 2.0.0-preview.1")]
    public void PackageScopeRejectsAnOutOfScopeOrPrereleasePackage(string package, string version, string? expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("Directory.Packages.props", $"<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"{package}\" Version=\"{version}\" />\n  </ItemGroup>\n</Project>\n");

        IReadOnlyList<string> violations = EvidenceChecks.PackageScopeViolations(workspace.Root);

        if (expected is null)
        {
            Assert.Empty(violations);
        }
        else
        {
            AssertViolation(violations, expected);
        }
    }

    [Fact]
    public void PackageScopeNamesAMissingFile()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        AssertViolation(EvidenceChecks.PackageScopeViolations(workspace.Root), "Directory.Packages.props is missing");
    }

    [Fact]
    public void PythonFilesAreFoundByADirectoryScanOutsideGit()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("tools/helper.py", "print(1)\n");
        _ = workspace.WriteFile("obj/generated.py", "print(2)\n");

        IReadOnlyList<string> violations = EvidenceChecks.PythonFileViolations(workspace.Root);

        Assert.Equal(["python file: tools/helper.py"], violations);
    }

    [GitFact]
    public void PythonFilesAreFoundByGitLsFilesInAWorkTree()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Assert.Equal(0, GitRunner.Run(workspace.Root, "init", "--quiet", workspace.Root).ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "config", "user.name", "Spec Fixture").ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "config", "user.email", "fixture@example.invalid").ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "config", "commit.gpgsign", "false").ExitCode);
        _ = workspace.WriteFile("tools/helper.py", "print(1)\n");
        _ = workspace.WriteFile("notes.txt", "x\n");
        Assert.Empty(EvidenceChecks.PythonFileViolations(workspace.Root));
        Assert.Equal(0, GitRunner.Run(workspace.Root, "add", "tools/helper.py", "notes.txt").ExitCode);
        Assert.Equal(0, EvidenceChecks.CommitWithoutHooks(workspace.Root, "add a script").ExitCode);

        Assert.Equal(["python file: tools/helper.py"], EvidenceChecks.PythonFileViolations(workspace.Root));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public void PackableMustOccurOnce(int occurrences, int expectedViolations)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("Directory.Build.props", "<Project>\n" + string.Concat(Enumerable.Repeat("  <IsPackable>false</IsPackable>\n", occurrences)) + "</Project>\n");

        Assert.Equal(expectedViolations, EvidenceChecks.PackableViolations(workspace.Root).Count);
    }

    [Fact]
    public void ProjectFoldersMustEqualTheDesign()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        foreach (string name in EvidenceChecks.SourceProjects.Where(static name => !name.EndsWith("Dashboard", StringComparison.Ordinal)))
        {
            _ = workspace.WriteFile($"src/{name}/{name}.csproj", "<Project />\n");
        }

        foreach (string name in EvidenceChecks.TestProjects)
        {
            _ = workspace.WriteFile($"tests/{name}/{name}.csproj", "<Project />\n");
        }

        _ = workspace.WriteFile("src/Extra.Tool/Extra.Tool.csproj", "<Project />\n");
        _ = workspace.WriteFile("src/NotAProject/readme.txt", "x\n");

        IReadOnlyList<string> violations = EvidenceChecks.ProjectFolderViolations(workspace.Root);

        Assert.Equal(["missing project: src/AgentJoules.Dashboard", "unexpected project: src/Extra.Tool"], violations);
    }

    [GitFact]
    public void StretchIsAllowedWhenEveryBoxIsTicked()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Assert.Equal(0, GitRunner.Run(workspace.Root, "init", "--quiet", workspace.Root).ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "config", "user.name", "Spec Fixture").ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "config", "user.email", "fixture@example.invalid").ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "config", "commit.gpgsign", "false").ExitCode);
        Assert.Equal(0, EvidenceChecks.CommitWithoutHooks(workspace.Root, "STRETCH: energy budget").ExitCode);

        Assert.Empty(EvidenceChecks.StretchViolations(workspace.Root, uncheckedBoxes: 0));
        AssertViolation(EvidenceChecks.StretchViolations(workspace.Root, uncheckedBoxes: 2), EvidenceChecks.StretchTooEarly);
    }

    [GitFact]
    public void StretchIgnoresARepositoryWithoutACommitAndAFolderWithoutGit()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Assert.Empty(EvidenceChecks.StretchViolations(workspace.Root, uncheckedBoxes: 1));
        Assert.Equal(0, GitRunner.Run(workspace.Root, "init", "--quiet", workspace.Root).ExitCode);

        Assert.Empty(EvidenceChecks.StretchViolations(workspace.Root, uncheckedBoxes: 1));
    }

    [GitFact]
    public void LiveTreeIsInScope()
    {
        // The project, package, packable and Python rules read the committed tree; the stretch rule is given one open box, so any STRETCH commit would show.
        // uncheckedBoxes: 1 is permanent and stricter than criterion 3: a legitimate STRETCH commit made after every box is ticked would turn this row red; the orchestrator flips it to 0 at the stretch gate.
        Assert.True(EvidenceChecks.ScopeViolations(RepositoryRoot.Path, uncheckedBoxes: 1).Count == 0, string.Join("\n", EvidenceChecks.ScopeViolations(RepositoryRoot.Path, uncheckedBoxes: 1)));
    }

    [Theory]
    [InlineData("Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n", 1, "build failed: exit 1")]
    [InlineData("Build succeeded.\n    10 Warning(s)\n    0 Error(s)\n", 0, "build has warnings")]
    [InlineData("Build FAILED.\n    0 Warning(s)\n    1 Error(s)\n", 0, "build has errors")]
    [InlineData("Build succeeded.\n    0 Error(s)\n", 0, "build output lacks \"0 Warning(s)\"")]
    [InlineData("Build succeeded.\n    0 Warning(s)\n", 0, "build output lacks \"0 Error(s)\"")]
    public void BuildOutputRejectsAFailureWarningOrMissingLine(string output, int exit, string expected) =>
        AssertViolation(EvidenceChecks.BuildOutputViolations(output, exit), expected);

    [Fact]
    public void BuildOutputOfTheSecondRunIsAcceptedToo() => Assert.Empty(EvidenceChecks.BuildOutputViolations(CleanBuild() + "Time Elapsed 00:00:03.21\n", 0));

    [Fact]
    public void TestRunCountsAddUpTheProjectSummaries()
    {
        const string output = "Passed!  - Failed:     0, Passed:    10, Skipped:     1, Total:    11, Duration: 1 s - A.dll (net10.0)\nPassed!  - Failed:     0, Passed:     5, Skipped:     2, Total:     7, Duration: 1 s - B.dll (net10.0)\n";

        Assert.Equal((15, 0, 3), EvidenceChecks.TestRunCounts(output));
        Assert.Equal("dotnet test: passed=15 failed=0 skipped=3 exit=0", EvidenceChecks.TestSummaryLineOf(output, 0));
        Assert.Null(EvidenceChecks.TestRunCounts("nothing"));
        Assert.Null(EvidenceChecks.TestSummaryLineOf("nothing", 0));
    }

    [Fact]
    public void TestRunRejectsNoSummaryAndANonZeroExit()
    {
        AssertViolation(EvidenceChecks.TestRunViolations("nothing", 0), "no test summary in the output");
        AssertViolation(EvidenceChecks.TestRunViolations("Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 s - A.dll (net10.0)", 1), "dotnet test exit code is 1");
    }

    [Theory]
    [InlineData("- dotnet test: passed=412 failed=0 skipped=9 exit=0", null)]
    [InlineData("dotnet test: passed=412 failed=1 skipped=9 exit=1", "tests failed: 1")]
    [InlineData("dotnet test: passed=412 failed=0 skipped=9 exit=2", "dotnet test exit code is 2")]
    [InlineData("passed 412", "the build log has no dotnet test summary line")]
    [InlineData("- dotnet test: passed=400 failed=2 skipped=9 exit=1\n- dotnet test: passed=412 failed=0 skipped=9 exit=0\n", null)]
    [InlineData("- dotnet test: passed=412 failed=0 skipped=9 exit=0\n- dotnet test: passed=400 failed=2 skipped=9 exit=1\n", "tests failed: 2")]
    public void TestSummaryRecordIsChecked(string buildLog, string? expected)
    {
        IReadOnlyList<string> violations = EvidenceChecks.TestSummaryRecordViolations(buildLog);

        if (expected is null)
        {
            Assert.Empty(violations);
        }
        else
        {
            AssertViolation(violations, expected);
        }
    }

    [Fact]
    public void FormatAcceptsExitZero()
    {
        Assert.Empty(EvidenceChecks.FormatViolations(0));
        AssertViolation(EvidenceChecks.FormatViolations(2), "format check failed");
    }

    [Theory]
    [InlineData("windows-latest")]
    [InlineData("ubuntu-latest")]
    [InlineData("dotnet build -warnaserror -tl:off")]
    [InlineData("dotnet format --verify-no-changes")]
    [InlineData("dotnet test")]
    public void CiFileRejectsAMissingToken(string token)
    {
        string workflow = RepositoryRoot.ReadAllText(".github/workflows/ci.yml").Replace(token, "removed", StringComparison.Ordinal);

        AssertViolation(EvidenceChecks.CiFileViolations(workflow), "ci.yml lacks " + token);
    }

    [Fact]
    public void CiFileNeedsAProbeStepOutsideComments()
    {
        const string noProbe = "runs-on: windows-latest ubuntu-latest\n  run: dotnet build -warnaserror -tl:off\n  run: dotnet format --verify-no-changes\n  run: dotnet test\n";

        AssertViolation(EvidenceChecks.CiFileViolations(noProbe), "ci.yml lacks a probe step");
        AssertViolation(EvidenceChecks.CiFileViolations(noProbe + "# the probe is run elsewhere\n"), "ci.yml lacks a probe step");
        Assert.Empty(EvidenceChecks.CiFileViolations(noProbe + "  run: dotnet run -- probe --json\n"));
        AssertViolation(EvidenceChecks.CiFileViolations(null), "ci.yml is missing");
    }

    [Fact]
    public void CiFileRejectsASecretsReferenceInAComment()
    {
        string workflow = RepositoryRoot.ReadAllText(".github/workflows/ci.yml") + "\n# uses secrets.TOKEN\n";

        AssertViolation(EvidenceChecks.CiFileViolations(workflow), "secrets reference in ci.yml");
    }

    [Fact]
    public void ScanRecordHasTheFallbackNameAndFourAnnotatedCodes()
    {
        string line = EvidenceChecks.ScanRecordLine([1, 1, 1, 1]);

        Assert.Equal("Secret scan: git grep fallback; exit codes: pattern=1 (1 = no hits), NVIDIA_API_KEY=1 (1 = no hits), NEBIUS_API_KEY=1 (1 = no hits), NGC_API_KEY=1 (1 = no hits)", line);
        Assert.Empty(EvidenceChecks.ScanRecordViolations("# Build log\n\n- " + line + "\n"));
        _ = Assert.Throws<ArgumentException>(() => EvidenceChecks.ScanRecordLine([1, 1, 1]));
    }

    [Theory]
    [InlineData("Secret scan: gitleaks; exit 0", "the scan record lacks \"git grep fallback\"")]
    [InlineData("Secret scan: git grep fallback; exit codes: pattern=1 (1 = no hits), NVIDIA_API_KEY=1 (1 = no hits), NEBIUS_API_KEY=1 (1 = no hits)", "the scan record lacks the exit code of NGC_API_KEY")]
    [InlineData("Secret scan: git grep fallback; exit codes: pattern=0, NVIDIA_API_KEY=1 (1 = no hits), NEBIUS_API_KEY=1 (1 = no hits), NGC_API_KEY=1 (1 = no hits)", "pattern exit code is 0, expected 1 (1 = no hits)")]
    [InlineData("Secret scan: git grep fallback; exit codes: pattern=1, NVIDIA_API_KEY=1 (1 = no hits), NEBIUS_API_KEY=1 (1 = no hits), NGC_API_KEY=1 (1 = no hits)", "pattern exit code 1 is not annotated (1 = no hits)")]
    public void ScanRecordRejectsAnIncompleteRecord(string record, string expected) =>
        AssertViolation(EvidenceChecks.ScanRecordViolations(record), expected);

    [Fact]
    public void ScanRecordIsTheLastRecordLineNotTheFirst()
    {
        const string failing = "Secret scan: git grep fallback; exit codes: pattern=0, NVIDIA_API_KEY=1 (1 = no hits), NEBIUS_API_KEY=1 (1 = no hits), NGC_API_KEY=1 (1 = no hits)";
        string passing = EvidenceChecks.ScanRecordLine([1, 1, 1, 1]);

        Assert.Empty(EvidenceChecks.ScanRecordViolations("- " + failing + "\n- " + passing + "\n"));
        AssertViolation(EvidenceChecks.ScanRecordViolations("- " + passing + "\n- " + failing + "\n"), "pattern exit code is 0, expected 1 (1 = no hits)");
    }

    [Fact]
    public void ScanRecordIsTheLineInTheRecordFormEvenWhenAnOlderMentionComesAfterIt()
    {
        string record = EvidenceChecks.ScanRecordLine([1, 1, 1, 1]);
        string log = "# Build log\n\n- " + record + "\n- Exit criteria: the secret scan used the git grep fallback and found nothing.\n";

        Assert.Empty(EvidenceChecks.ScanRecordViolations(log));
        AssertViolation(EvidenceChecks.ScanRecordViolations("- the git grep fallback ran, pattern=1\n"), "the scan record lacks \"git grep fallback\"");
    }

    [Fact]
    public void ScanRecordNamesAMissingLog() => AssertViolation(EvidenceChecks.ScanRecordViolations(null), "the scan record is missing");

    [Fact]
    public void KeyShapedScanFindsATokenOutsideGitAndNeverEchoesIt()
    {
        // Built at run time, so no key-shaped literal sits in the source.
        string token = string.Concat("nvapi", "-", new string('k', 12));
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("docs/note.md", "token " + token + "\n");
        _ = workspace.WriteFile("docs/short.md", string.Concat("nvapi", "-", "abc") + "\n");

        IReadOnlyList<string> violations = EvidenceChecks.KeyShapedFileViolations(workspace.Root);

        Assert.False(violations.Any(violation => violation.Contains(token, StringComparison.Ordinal)), "a violation carries the token (the value is not echoed)");
        Assert.Equal([EvidenceChecks.KeyShapedInTrackedFile, "  in docs/note.md"], violations);
    }

    [Fact]
    public void KeyShapedScanPassesACleanFolder()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("docs/note.md", "no key here, only the prefix " + string.Concat("nvapi", "-") + "\n");

        Assert.Empty(EvidenceChecks.KeyShapedFileViolations(workspace.Root));
    }

    [GitFact]
    public void LiveTreeHasNoKeyShapedString()
    {
        IReadOnlyList<string> violations = EvidenceChecks.KeyShapedFileViolations(RepositoryRoot.Path);

        Assert.True(violations.Count == 0, "tracked files with a key-shaped string:\n" + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("first\nsecond")]
    public void ValueScanRefusesAnEmptyOrMultiLineValueWithoutEchoingIt(string value)
    {
        ArgumentException refusal = Assert.Throws<ArgumentException>(() => EvidenceChecks.TrackedFilesContaining(Path.GetTempPath(), value));

        Assert.DoesNotContain("first", refusal.Message, StringComparison.Ordinal);
    }

    [GitFact]
    public void ValueScanReportsAFileNameAndExitOneForAnAbsentValue()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string value = string.Concat("synthetic", "-", Guid.NewGuid().ToString("N"));
        _ = workspace.WriteFile("tracked.txt", "token=" + value + "\n");
        Assert.Equal(0, GitRunner.Run(workspace.Root, "init", "--quiet", workspace.Root).ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "add", "tracked.txt").ExitCode);

        GitResult found = EvidenceChecks.TrackedFilesContaining(workspace.Root, value);
        GitResult absent = EvidenceChecks.TrackedFilesContaining(workspace.Root, value + "x");

        Assert.Equal(0, found.ExitCode);
        Assert.Equal("tracked.txt", found.Stdout.Trim());
        Assert.Equal(1, absent.ExitCode);
        Assert.Equal(string.Empty, absent.Stdout);
    }
}