using System.IO.Compression;
using System.Text;

using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.Specs.Support;

/// <summary>
/// One row for each additive rule of <see cref="EvidenceChecks"/> that the scenarios of AJ-157, AJ-158 and AJ-159 do not exercise, and the acceptance rows that prove the fixtures of <see cref="SubmissionFixtures"/> satisfy every rule,
/// so that a negative scenario fails for its own defect only. Not pinned here: the rules that report an absent table or an absent document ("Cut-off plan has no table", "Buffer has no table", "timeline has no target", "Buffer table has no target time", "missing docs/..."). Each row works on text or on a minimal fixture in a <see cref="TempWorkspace"/>; nothing reads the repository, the network or a key.
/// </summary>
public sealed class SubmissionChecksTests
{
    private static void AssertViolation(IReadOnlyList<string> violations, string expected) =>
        Assert.True(violations.Contains(expected, StringComparer.Ordinal), "expected: " + expected + "\nactual:\n" + string.Join("\n", violations));

    private static void AssertClean(IReadOnlyList<string> violations) =>
        Assert.True(violations.Count == 0, string.Join("\n", violations));

    [Fact]
    public void TheChecklistFixtureSatisfiesEveryRule()
    {
        string checklist = SubmissionFixtures.Checklist();

        AssertClean(EvidenceChecks.SubmissionOrderViolations(checklist));
        AssertClean(EvidenceChecks.TimelineViolations(checklist));
        AssertClean(EvidenceChecks.CutOffViolations(checklist));
        AssertClean(EvidenceChecks.SubmissionStatementViolations(checklist));
    }

    [Fact]
    public void TheBuildLogFixtureSatisfiesEveryRule()
    {
        string log = SubmissionFixtures.BuildLog();

        AssertClean(EvidenceChecks.DropOrderMonotonicViolations(log));
        AssertClean(EvidenceChecks.DropTriggerViolations(log));
        AssertClean(EvidenceChecks.DropRiskViolations(log));
        AssertClean(EvidenceChecks.NeverDroppedViolations(log));
        AssertClean(EvidenceChecks.BufferViolations(log));
    }

    [Fact]
    public void ThePackageFixtureSatisfiesEveryRule()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);

        AssertClean(EvidenceChecks.ReleaseNotesViolations(workspace.Root));
        AssertClean(EvidenceChecks.ReleaseZipViolations(workspace.Root));
        AssertClean(EvidenceChecks.ReleaseCommandViolations(workspace.Root));
    }

    [Fact]
    public void CellsKeepAnEscapedPipeInsideOneCell()
    {
        IReadOnlyList<string> cells = EvidenceChecks.CellsOf("| a | row `cloudgpu \\| not run` | c |");

        Assert.Equal(3, cells.Count);
        Assert.Equal("row `cloudgpu | not run`", cells[1]);
    }

    [Fact]
    public void ASubmissionOrderOfElevenItemsIsRejected()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "12. After submission keep the repository on the submitted commit and confirm the v0.1.0 tag (user)\n", string.Empty);

        AssertViolation(EvidenceChecks.SubmissionOrderViolations(checklist), "submission order has 11 items, expected 12");
    }

    [Fact]
    public void ASubmissionOrderOutOfSequenceIsRejected()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "6. YouTube video link, <user fills> (user)", "6. Track, the user's pick (user)");

        AssertViolation(EvidenceChecks.SubmissionOrderViolations(checklist), "step 6 is out of order, expected: YouTube");
    }

    [Fact]
    public void ALastTargetLessThanThreeHoursBeforeTheDeadlineIsRejected()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "2026-10-30 14:00 UTC (07:00 PDT)", "2026-10-30 15:00 UTC (08:00 PDT)");

        AssertViolation(EvidenceChecks.TimelineViolations(checklist), "last target is less than 3 h before the deadline: 2026-10-30 15:00 UTC");
    }

    [Fact]
    public void AnOffsetCrossingMidnightIsAccepted()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "2026-10-30 07:00 UTC (00:00 PDT)", "2026-10-30 06:00 UTC (23:00 PDT)");

        AssertClean(EvidenceChecks.TimelineViolations(checklist));
    }

    [Fact]
    public void ATimelineWithFewerThanSixPairsIsRejected()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "- Pages and release live 2026-10-29 21:00 UTC (14:00 PDT)\n", string.Empty);

        AssertViolation(EvidenceChecks.TimelineViolations(checklist), "timeline has 5 UTC/PDT lines, expected 6 or more");
    }

    [Fact]
    public void ATimelineWithoutTheCentralEuropeNoteIsRejected()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "UTC+1 (CET)", "later");

        AssertViolation(EvidenceChecks.TimelineViolations(checklist), "missing CET note: UTC+1");
    }

    [Theory]
    [InlineData("| nebius tier |", "no cut-off row: nebius")]
    [InlineData("| video not ready |", "no cut-off row: video")]
    public void ACutOffPlanWithoutARowIsRejected(string rowStart, string expected)
    {
        string checklist = string.Join('\n', SubmissionFixtures.Checklist().Split('\n').Where(line => !line.StartsWith(rowStart, StringComparison.Ordinal)));

        AssertViolation(EvidenceChecks.CutOffViolations(checklist), expected);
    }

    [Fact]
    public void ANebiusRowWithoutTheBlockedRuleIsRejected()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "BLOCKED, no claim the files do not support", "skipped");

        AssertViolation(EvidenceChecks.CutOffViolations(checklist), "nebius row lacks the BLOCKED rule");
    }

    [Fact]
    public void ACloudGpuRowWithoutTheBoxesIsRejected()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "| AJ-129 | 21, 22, 23 |", "| AJ-129 | 21 |");

        AssertViolation(EvidenceChecks.CutOffViolations(checklist), "cloudgpu row lacks the boxes 21, 22 and 23");
    }

    [Fact]
    public void AMinimalCutThatDoesNotSumToSixtyFiveIsRejected()
    {
        const string Script = "| Shot | Start | Duration | On screen | Narration |\n|---|---|---|---|---|\n| 1 | 0 | 25 | a | b |\n| 2 | 20 | 50 | a | b |\n| 3 | 70 | 15 | a | b |\n| 6 | 85 | 15 | a | b |\n| 9 | 100 | 15 | a | b |\n";

        AssertViolation(EvidenceChecks.CutOffViolations(SubmissionFixtures.Checklist(), Script), "minimal cut of shots 1, 3, 6 and 9 sums to 70 s, expected 65 s");
    }

    [Fact]
    public void ANotesFileWithoutAHeadingIsRejected()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("docs/release-notes-v0.1.0.md", SubmissionFixtures.Change(SubmissionFixtures.ReleaseNotes(), "## Fixes", "## Repairs"));

        AssertViolation(EvidenceChecks.ReleaseNotesViolations(workspace.Root), "missing heading: Fixes");
    }

    [Fact]
    public void ABreakingChangesSectionThatIsNotNoneIsRejected()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("docs/release-notes-v0.1.0.md", SubmissionFixtures.Change(SubmissionFixtures.ReleaseNotes(), "## Breaking changes\n\nNone (first release)", "## Breaking changes\n\nThe CLI changed"));

        AssertViolation(EvidenceChecks.ReleaseNotesViolations(workspace.Root), "Breaking changes must read None (first release)");
    }

    [Fact]
    public void ANumberWithALabelButNoFileIsAccepted()
    {
        AssertClean(EvidenceChecks.NumberSourceViolations("The estimated cost was 4 USD."));
    }

    [Theory]
    [InlineData("run.raw")]
    [InlineData("keys/id_ed25519")]
    [InlineData("keys/id_rsa.pub")]
    [InlineData("tls/server.pem")]
    public void AForbiddenEntryNameIsRejected(string entry)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        SubmissionFixtures.WriteZip(workspace.Root, "report", ("index.html", "<html/>"), ("summary.md", "#"), ("joules-per-step.svg", "<svg/>"), (entry, "x"));

        Assert.True(EvidenceChecks.ReleaseZipViolations(workspace.Root).Any(violation => violation.StartsWith("forbidden entry in agent-joules-v0.1.0-report.zip", StringComparison.Ordinal)), "no forbidden-entry violation");
    }

    [Fact]
    public void AKeyShapedValueInAnyZipIsRejectedWithoutPrintingIt()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        string key = SubmissionFixtures.KeyShapedValue();
        SubmissionFixtures.WriteZip(workspace.Root, "report", ("index.html", key), ("summary.md", "#"), ("joules-per-step.svg", "<svg/>"));

        IReadOnlyList<string> violations = EvidenceChecks.ReleaseZipViolations(workspace.Root);

        AssertViolation(violations, "key material in agent-joules-v0.1.0-report.zip");
        Assert.False(violations.Any(violation => violation.Contains(key, StringComparison.Ordinal)), "a violation printed the key value");
    }

    [Fact]
    public void AnFddZipWithoutTheCliLibraryIsRejected()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        SubmissionFixtures.WriteZip(workspace.Root, "fdd", ("Other.dll", "binary"));

        AssertViolation(EvidenceChecks.ReleaseZipViolations(workspace.Root), "AgentJoules.Cli.dll not listed in agent-joules-v0.1.0-fdd.zip");
    }

    [Fact]
    public void AResultsZipWithAMissingFileIsRejected()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        _ = workspace.WriteFile("results/20261025T133440Z-nebius.json", "{}");

        AssertViolation(EvidenceChecks.ReleaseZipViolations(workspace.Root), "agent-joules-v0.1.0-results.zip has 1 files, results/ has 2 json files");
    }

    [Fact]
    public void AReportZipWithoutTheChartIsRejected()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        SubmissionFixtures.WriteZip(workspace.Root, "report", ("index.html", "<html/>"), ("summary.md", "#"));

        AssertViolation(EvidenceChecks.ReleaseZipViolations(workspace.Root), "joules-per-step.svg not listed in agent-joules-v0.1.0-report.zip");
    }

    [Fact]
    public void AMissingZipIsRejected()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        AssertViolation(EvidenceChecks.ReleaseZipViolations(workspace.Root), "missing asset: artifacts/agent-joules-v0.1.0-fdd.zip");
    }

    [Fact]
    public void AFolderThatIsNotARepositoryTracksNothing()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);

        AssertClean(EvidenceChecks.TrackedArtifactViolations(workspace.Root));
    }

    [Theory]
    [InlineData("--notes-file docs/release-notes-v0.1.0.md", "--notes-file docs/other.md", "notes file must be docs/release-notes-v0.1.0.md")]
    [InlineData(" artifacts/agent-joules-v0.1.0-report.zip\n```", "\n```", "asset not named: artifacts/agent-joules-v0.1.0-report.zip")]
    [InlineData("User action (the builder never runs this):\n\n```\ngh release create", "```\ngh release create", "release command not marked as user action")]
    public void AWrongReleaseCommandIsRejected(string from, string to, string expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        _ = workspace.WriteFile("docs/devpost-checklist.md", SubmissionFixtures.Change(SubmissionFixtures.Checklist(), from, to));

        AssertViolation(EvidenceChecks.ReleaseCommandViolations(workspace.Root), expected);
    }

    [Fact]
    public void ADropOrderWithAMissingStepIsRejected()
    {
        string log = string.Join('\n', SubmissionFixtures.BuildLog().Split('\n').Where(static line => !line.StartsWith("| C7 |", StringComparison.Ordinal)));

        AssertViolation(EvidenceChecks.DropOrderMonotonicViolations(log), "missing drop step: C7");
    }

    [Fact]
    public void ADropOrderWithSwappedStepsIsRejected()
    {
        string[] lines = SubmissionFixtures.BuildLog().Split('\n');
        int two = Array.FindIndex(lines, static line => line.StartsWith("| C2 |", StringComparison.Ordinal));
        (lines[two], lines[two + 1]) = (lines[two + 1], lines[two]);

        AssertViolation(EvidenceChecks.DropOrderMonotonicViolations(string.Join('\n', lines)), "drop step out of order: C3 (expected: C2)");
    }

    [Fact]
    public void AStepThatUntickBoxesIsRejected()
    {
        string log = SubmissionFixtures.ChangeRow(SubmissionFixtures.BuildLog(), "D2", "| none | none | RK-1 |", "| 12 | none | RK-1 |");

        AssertViolation(EvidenceChecks.DropOrderMonotonicViolations(log), "boxes unticked must be none: D2");
    }

    [Fact]
    public void ALevelThreeWithoutTheBoxesIsRejected()
    {
        string log = SubmissionFixtures.Change(SubmissionFixtures.BuildLog(), "It unticks boxes 21, 22 and 23 (substance).", "It unticks boxes 21 and 22.");

        AssertViolation(EvidenceChecks.DropOrderMonotonicViolations(log), "Level 3 must name boxes 21, 22 and 23");
    }

    [Fact]
    public void ALevelThreeRowInTheTableCountsAsTheEntry()
    {
        string log = SubmissionFixtures.Change(SubmissionFixtures.BuildLog(), "\nLevel 3: the cloudgpu tier, decided at 2026-10-26 17:00 UTC. It unticks boxes 21, 22 and 23 (substance). Risk RK-5.\n",
            "| L3 | AJ-120 | 24.95 | 24.95 | 2026-10-26 17:00 UTC and the user approves | cloudgpu absent | 21, 22, 23 | REGEN | RK-5 |\n");

        AssertClean(EvidenceChecks.DropOrderMonotonicViolations(log));
        AssertClean(EvidenceChecks.DropTriggerViolations(log));
    }

    [Fact]
    public void AStepWithoutARiskLinkIsRejected()
    {
        string log = SubmissionFixtures.ChangeRow(SubmissionFixtures.BuildLog(), "C6", "| RK-1 |", "|  |");

        AssertViolation(EvidenceChecks.DropRiskViolations(log), "no risk link: C6");
    }

    [Fact]
    public void AnUnknownRiskIsRejected()
    {
        string log = SubmissionFixtures.ChangeRow(SubmissionFixtures.BuildLog(), "C6", "| RK-1 |", "| RK-9 |");

        AssertViolation(EvidenceChecks.DropRiskViolations(log), "unknown risk: RK-9 in C6");
    }

    [Fact]
    public void TheRiskIdsOfAStoryAreReadFromItsTable()
    {
        const string Story = "## Risks\n\n| Id | Risk |\n|---|---|\n| RK-1 | a |\n| RK-2 | b |\n\n## Estimate\n\n| RK-3 | not a risk row |\n";

        Assert.Equal(["RK-1", "RK-2"], EvidenceChecks.StoryRiskIds(Story));
    }

    [Theory]
    [InlineData("- the secret scan\n", "never-dropped list lacks: secret scan")]
    [InlineData("- the user's video and the user's submission on Devpost\n", "never-dropped list lacks: user's video")]
    [InlineData("- the user's video and the user's submission on Devpost\n", "never-dropped list lacks: user's submission")]
    [InlineData("the user's submission on Devpost", "never-dropped list lacks: user's submission")]
    [InlineData("- the story (docs/devpost-story.md)\n", "never-dropped list lacks: story")]
    [InlineData("the three LinkedIn variants are never dropped", "never-dropped list lacks: three LinkedIn variants")]
    public void ANeverDroppedListWithoutAnItemIsRejected(string removed, string expected)
    {
        string log = SubmissionFixtures.BuildLog();
        string changed = SubmissionFixtures.Change(log, removed, string.Empty);

        AssertViolation(EvidenceChecks.NeverDroppedViolations(changed), expected);
    }

    [Fact]
    public void ABufferWithoutTheCapacityComparisonIsRejected()
    {
        string log = SubmissionFixtures.Change(SubmissionFixtures.BuildLog(), "these are planning figures", "these are figures");

        AssertViolation(EvidenceChecks.BufferViolations(log), "capacity comparison missing: M6 55.25 h, M7 and planning figures");
    }

    [Theory]
    [InlineData("Energy is GPU-board energy only.", "Energy is measured.", "missing sentence: Energy is GPU-board energy only.")]
    [InlineData("- agent-joules-v0.1.0-report.zip\n", "", "Assets does not name: agent-joules-v0.1.0-report.zip")]
    [InlineData("gh release view v0.1.0", "gh release list", "Verification lacks: gh release view v0.1.0")]
    [InlineData("## Migration\n\nNone (first release)", "## Migration\n\nRun the script", "Migration must read None (first release)")]
    public void AnIncompleteNotesFileIsRejected(string from, string to, string expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("docs/release-notes-v0.1.0.md", SubmissionFixtures.Change(SubmissionFixtures.ReleaseNotes(), from, to));

        AssertViolation(EvidenceChecks.ReleaseNotesViolations(workspace.Root), expected);
    }

    [Theory]
    [InlineData("gh release create v0.1.0", "gh release create v0.2.0", "release command does not create v0.1.0")]
    [InlineData(" artifacts/agent-joules-v0.1.0-report.zip\n```", " artifacts/agent-joules-v0.1.0-report.zip artifacts/extra.zip\n```", "unexpected asset: artifacts/extra.zip")]
    [InlineData("gh release view v0.1.0", "gh release list", "checklist lacks the verification command: gh release view v0.1.0")]
    public void AnotherWrongReleaseCommandIsRejected(string from, string to, string expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), from, to);
        _ = workspace.WriteFile("docs/devpost-checklist.md", checklist);

        AssertViolation(EvidenceChecks.ReleaseCommandViolations(workspace.Root), expected);
    }

    [Fact]
    public void AChecklistWithoutTheReleaseCommandBlockIsRejected()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "gh release create v0.1.0", "gh release make v0.1.0");
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("docs/devpost-checklist.md", checklist);

        AssertViolation(EvidenceChecks.ReleaseCommandViolations(workspace.Root), "release command not found: gh release create");
    }

    [Fact]
    public void AMissingNotesFileIsRejectedByTheReleaseCommand()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        File.Delete(Path.Combine(workspace.Root, "docs", "release-notes-v0.1.0.md"));

        AssertViolation(EvidenceChecks.ReleaseCommandViolations(workspace.Root), "notes file not found: docs/release-notes-v0.1.0.md");
    }

    [Fact]
    public void AFileThatIsNotAZipIsRejected()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        File.WriteAllText(Path.Combine(workspace.Root, "artifacts", "agent-joules-v0.1.0-fdd.zip"), "this is not a zip archive");

        AssertViolation(EvidenceChecks.ReleaseZipViolations(workspace.Root), "not a zip archive: agent-joules-v0.1.0-fdd.zip");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AKeyShapedUtf16ValueInAnyZipIsRejectedAtBothAlignments(int offset)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        string path = Path.Combine(workspace.Root, "artifacts", "agent-joules-v0.1.0-report.zip");
        File.Delete(path);
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using Stream stream = archive.CreateEntry("index.html").Open();
            stream.Write(new byte[offset]);
            stream.Write(Encoding.Unicode.GetBytes(SubmissionFixtures.KeyShapedValue()));
        }

        IReadOnlyList<string> violations = EvidenceChecks.ReleaseZipViolations(workspace.Root);

        AssertViolation(violations, "key material in agent-joules-v0.1.0-report.zip");
        Assert.False(violations.Any(violation => violation.Contains(SubmissionFixtures.KeyShapedValue(), StringComparison.Ordinal)), "a violation printed the key value");
    }

    [Fact]
    public void ARepositoryWhereGitCannotListTheIndexIsRejected()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(".git", "gitdir: no-such-folder\n");

        AssertViolation(EvidenceChecks.TrackedArtifactViolations(workspace.Root), "artifacts check could not run git");
    }

    [Fact]
    public void AnInitialisedRepositoryWithoutTrackedArtifactsIsClean()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        SubmissionFixtures.RunGit(workspace.Root, "init", "--quiet");

        AssertClean(EvidenceChecks.TrackedArtifactViolations(workspace.Root));
    }

    [Fact]
    public void TheAssetExistenceCheckOfTheReleaseCommandCanBeLeftOut()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WritePackage(workspace);
        Directory.Delete(Path.Combine(workspace.Root, "artifacts"), recursive: true);

        Assert.False(EvidenceChecks.AnyReleaseAssetExists(workspace.Root));
        AssertViolation(EvidenceChecks.ReleaseCommandViolations(workspace.Root), "asset not found: artifacts/agent-joules-v0.1.0-fdd.zip");
        AssertClean(EvidenceChecks.ReleaseCommandViolations(workspace.Root, requireAssets: false));
    }

    [Fact]
    public void OneZipAloneIsAnAssetAndTheOtherTwoAreMissing()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        SubmissionFixtures.WriteZip(workspace.Root, "report", ("index.html", "<html></html>"), ("summary.md", "# Summary"), ("joules-per-step.svg", "<svg/>"));

        Assert.True(EvidenceChecks.AnyReleaseAssetExists(workspace.Root));
        IReadOnlyList<string> violations = EvidenceChecks.ReleaseZipViolations(workspace.Root);
        AssertViolation(violations, "missing asset: artifacts/agent-joules-v0.1.0-fdd.zip");
        AssertViolation(violations, "missing asset: artifacts/agent-joules-v0.1.0-results.zip");
        Assert.DoesNotContain("missing asset: artifacts/agent-joules-v0.1.0-report.zip", violations);
    }

    [Fact]
    public void ANumberInASentenceThatSaysNotMeasuredIsRejected()
    {
        AssertViolation(EvidenceChecks.NumberSourceViolations("# Notes\n\nThe run was not measured at 5 J.\n"), "number without source: 5 J");
        AssertClean(EvidenceChecks.NumberSourceViolations("# Notes\n\nThe run was measured at 5 J.\n"));
    }

    [Fact]
    public void ANeverDroppedListThatMentionsVideoAndStoryElsewhereStillLacksTheItems()
    {
        string log = SubmissionFixtures.Change(SubmissionFixtures.BuildLog(), "- the story (docs/devpost-story.md)\n", "- the video and the story are discussed elsewhere in this section\n");
        log = SubmissionFixtures.Change(log, "- the user's video and the user's submission on Devpost\n", string.Empty);

        IReadOnlyList<string> violations = EvidenceChecks.NeverDroppedViolations(log);
        AssertViolation(violations, "never-dropped list lacks: story");
        AssertViolation(violations, "never-dropped list lacks: user's video");
    }

    [Theory]
    [InlineData("## Submission order", "## Order")]
    [InlineData("## Timeline", "## Plan")]
    [InlineData("## Cut-off plan", "## Cuts")]
    public void AMissingChecklistSectionIsRejected(string from, string to)
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), from, to);
        string expected = "missing section: " + from[3..];

        AssertViolation([.. EvidenceChecks.SubmissionOrderViolations(checklist), .. EvidenceChecks.TimelineViolations(checklist), .. EvidenceChecks.CutOffViolations(checklist)], expected);
    }

    [Fact]
    public void ATimelineWithoutTheBufferStatementIsRejected()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "3 hours of buffer remain", "Some buffer remains");

        AssertViolation(EvidenceChecks.TimelineViolations(checklist), "missing buffer statement: 3 hours");
    }

    [Fact]
    public void ALineThatMentionsTheDeadlineIsStillATarget()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "submission complete by 2026-10-30 14:00 UTC (07:00 PDT)", "submission complete by 2026-10-30 16:00 UTC (09:00 PDT), one hour before the deadline");

        AssertViolation(EvidenceChecks.TimelineViolations(checklist), "last target is less than 3 h before the deadline: 2026-10-30 16:00 UTC");
    }

    [Fact]
    public void ABufferRowThatMentionsTheDeadlineIsStillATarget()
    {
        string log = SubmissionFixtures.Change(SubmissionFixtures.BuildLog(), "| submission complete | 2026-10-30 14:00 UTC | 3 |", "| submission complete, one hour before the deadline | 2026-10-30 16:00 UTC | 1 |");

        AssertViolation(EvidenceChecks.BufferViolations(log), "buffer 1 h is below 3 h");
    }

    [Fact]
    public void ACutOffPlanWithoutAFallbackColumnIsRejected()
    {
        string checklist = SubmissionFixtures.Change(SubmissionFixtures.Checklist(), "| Fallback story |", "| Plan B |");

        AssertViolation(EvidenceChecks.CutOffViolations(checklist), "Cut-off plan table lacks column: fallback");
    }

    [Theory]
    [InlineData("| README.md, docs/report/ |", "|  |", "no files: local")]
    [InlineData("| " + SubmissionFixtures.Regenerate + " |", "|  |", "no regeneration command: local")]
    [InlineData("| AJ-150 | 1 |", "|  | 1 |", "no fallback story: local")]
    [InlineData("| AJ-150 | 1 |", "| AJ-150 |  |", "no unchecked boxes: local")]
    public void ABlankCutOffCellIsRejected(string from, string to, string expected)
    {
        string checklist = SubmissionFixtures.ChangeRow(SubmissionFixtures.Checklist(), "local tier", from, to);

        AssertViolation(EvidenceChecks.CutOffViolations(checklist), expected);
    }

    [Theory]
    [InlineData("cloudgpu tier", "| " + SubmissionFixtures.Regenerate + " |", "| `AJ report` |", "cloudgpu row lacks the regeneration commands")]
    [InlineData("cloudgpu tier", "row `cloudgpu \\| not run`", "row skipped", "cloudgpu row lacks: cloudgpu | not run")]
    [InlineData("cloudgpu tier", "`None besides Token Factory.`", "`None.`", "cloudgpu row lacks: None besides Token Factory.")]
    [InlineData("video not ready", "the minimal cut of 65 s, shots 1, 3, 6 and 9", "the minimal cut", "video row lacks the 65 s minimal cut")]
    public void AWrongCutOffRowIsRejected(string row, string from, string to, string expected)
    {
        string checklist = SubmissionFixtures.ChangeRow(SubmissionFixtures.Checklist(), row, from, to);

        AssertViolation(EvidenceChecks.CutOffViolations(checklist), expected);
    }

    [Theory]
    [InlineData("## Drop order", "## Steps", "missing section: Drop order")]
    [InlineData("## Never dropped", "## Kept", "missing section: Never dropped")]
    [InlineData("## Buffer", "## Reserve", "missing section: Buffer")]
    [InlineData("| Trigger |", "| When |", "drop order table lacks column: trigger")]
    [InlineData("| Boxes unticked |", "| Boxes |", "drop order table lacks column: boxes unticked")]
    [InlineData("Level 3: the cloudgpu tier, decided at 2026-10-26 17:00 UTC.", "Level 3: the cloudgpu tier, decided at 2026-10-30 17:00 UTC.", "trigger after deadline: L3")]
    [InlineData("Level 3: the cloudgpu tier, decided at 2026-10-26 17:00 UTC. It unticks boxes 21, 22 and 23 (substance). Risk RK-5.\n", "", "no Level 3 entry naming boxes 21, 22 and 23")]
    public void ABuildLogWithAWrongStructureIsRejected(string from, string to, string expected)
    {
        string log = SubmissionFixtures.Change(SubmissionFixtures.BuildLog(), from, to);

        AssertViolation([.. EvidenceChecks.DropOrderMonotonicViolations(log), .. EvidenceChecks.DropTriggerViolations(log), .. EvidenceChecks.NeverDroppedViolations(log), .. EvidenceChecks.BufferViolations(log)], expected);
    }

    [Theory]
    [InlineData("schema validation")]
    [InlineData("nebius run file")]
    [InlineData("local run file")]
    [InlineData("results block")]
    [InlineData("demo script")]
    [InlineData("feedback draft")]
    [InlineData("peer-review-critic")]
    [InlineData("push command")]
    public void ANeverDroppedItemInADropRowIsRejected(string phrase)
    {
        string log = SubmissionFixtures.ChangeRow(SubmissionFixtures.BuildLog(), "C4", "a requirement is reported UNMET", "drops the " + phrase);

        AssertViolation(EvidenceChecks.NeverDroppedViolations(log), "never-dropped item in drop list: " + phrase);
    }
}