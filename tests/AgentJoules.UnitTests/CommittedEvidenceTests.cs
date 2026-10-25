using System.Text.Json.Nodes;

using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Evidence;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-115 (REQ-020, REQ-021, EDGE-010): the committed laptop evidence and the README statements are checked against each other. The facts of this file read the repository through
/// <see cref="CommittedEvidenceReader"/> (the latest local run file chosen by <c>RunFileSelector</c>, <c>results/calibration-*.json</c>, <c>docs/endpoints.md</c>, <c>docs/nim-local-attempt.md</c> and <c>README.md</c>) and
/// judge them with <see cref="CommittedEvidenceRules"/>. They run on every build and are never skipped: a tree without a completed or partial local run file fails them, so a missing disclosure breaks the build. The
/// same rules run over documents built in memory in <c>CommittedEvidenceTests.Rules.cs</c>, so the rules are proved apart from the evidence. That the configured <c>Local:Runtime</c> equals the committed Decision is
/// <c>Shipped_Local_Runtime_equals_the_committed_decision</c> (AJ-102); it is not repeated here.
/// </summary>
public sealed partial class CommittedEvidenceTests
{
    private const string NoLocalRunFile = "results/: no completed or partial local run file";

    private static readonly CommittedEvidenceReader Reader = new();

    // The latest local run file as a tree; the absence of a file is a failure, never a skip.
    private static JsonNode LatestRun() =>
        Reader.LatestLocalRunFile() is { } path
            ? CommittedEvidenceRules.ParseRun(File.ReadAllText(path))
            : throw new XunitException(NoLocalRunFile + " (AJ-114 commits it)");

    private static void AssertNone(IReadOnlyList<string> violations)
    {
        if (violations.Count > 0)
        {
            throw new XunitException(string.Join('\n', violations));
        }
    }

    // ---- the committed evidence ----

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void The_latest_local_run_file_names_a_known_runtime_and_a_model_tag_of_the_local_section() =>
        AssertNone(CommittedEvidenceRules.CheckRuntime(LatestRun(), Reader.EndpointsDocument()));

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void The_latest_local_run_file_has_a_numeric_offload_and_partial_offload_when_it_is_below_100() =>
        AssertNone(CommittedEvidenceRules.CheckOffload(LatestRun()));

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void The_readme_sentence_names_the_runtime_and_the_tag_of_the_latest_local_run_file() =>
        AssertNone(CommittedEvidenceRules.CheckReadmeSentence(Reader.ReadmeText(), LatestRun()));

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void The_latest_local_run_file_has_a_30_second_baseline_three_repeats_a_load_time_and_a_power_source() =>
        AssertNone(CommittedEvidenceRules.CheckMeasurement(LatestRun()));

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_committed_calibration_file_holds_three_label_objects() =>
        AssertNone(CommittedEvidenceRules.CheckCalibration(Reader.CalibrationTexts()));

    [Fact]
    [Trait("Spec", "EDGE-010")]
    public void The_decision_the_readme_link_and_the_latest_local_run_file_agree() =>
        AssertNone(CommittedEvidenceRules.CheckDecision(Reader.NimDecision(), Reader.ReadmeText(), LatestRun()));

    // ---- a tree without evidence fails ----

    [Fact]
    [Trait("Spec", "REQ-020")]
    [Trait("Spec", "REQ-021")]
    [Trait("Spec", "EDGE-010")]
    public void A_tree_without_a_local_run_file_fails_and_never_skips()
    {
        using var empty = TempWorkspace.CreateEmpty();
        using var withOtherFiles = TempWorkspace.CreateEmpty();
        _ = withOtherFiles.WriteFile(Path.Combine("results", "probe-20261019T205344Z.json"), "{}");
        _ = withOtherFiles.WriteFile(Path.Combine("results", CommittedEvidenceDraft.CalibrationFileName), CommittedEvidenceDraft.ValidCalibrationJson);

        Assert.Equal([NoLocalRunFile], CommittedEvidenceRules.CheckAll(new CommittedEvidenceReader(empty.Root)));
        Assert.Equal([NoLocalRunFile], CommittedEvidenceRules.CheckAll(new CommittedEvidenceReader(withOtherFiles.Root)));
    }

    [Fact]
    [Trait("Spec", "REQ-020")]
    [Trait("Spec", "REQ-021")]
    [Trait("Spec", "EDGE-010")]
    public void A_tree_with_a_completed_local_run_file_is_judged_by_every_rule()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        new CommittedEvidenceDraft().WriteTo(workspace);

        Assert.Empty(CommittedEvidenceRules.CheckAll(new CommittedEvidenceReader(workspace.Root)));
    }
}