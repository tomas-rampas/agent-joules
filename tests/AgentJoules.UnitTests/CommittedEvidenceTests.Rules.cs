using AgentJoules.Reporting.Endpoints;
using AgentJoules.TestSupport.Docs;
using AgentJoules.TestSupport.Evidence;

using Xunit;

namespace AgentJoules.UnitTests;

// The rules of CommittedEvidenceRules over documents built in memory (CommittedEvidenceDraft). A pass is an empty list; a violation names the field and both values.
public sealed partial class CommittedEvidenceTests
{
    private static IReadOnlyList<string> Check(CommittedEvidenceDraft draft) =>
        CommittedEvidenceRules.CheckAll(
            draft.RunJson(),
            draft.HasCalibrationFile ? [(CommittedEvidenceDraft.CalibrationFileName, draft.CalibrationJson)] : [],
            EndpointsDocParser.Parse(draft.EndpointsText()),
            NimAttemptDocumentParser.Parse(draft.DecisionText()),
            draft.ReadmeText());

    private static string Failure(CommittedEvidenceDraft draft, params string[] names)
    {
        string message = string.Join('\n', Check(draft));
        Assert.NotEmpty(message);
        foreach (string name in names)
        {
            Assert.Contains(name, message, StringComparison.Ordinal);
        }

        return message;
    }

    [Fact]
    [Trait("Spec", "REQ-020")]
    [Trait("Spec", "REQ-021")]
    [Trait("Spec", "EDGE-010")]
    public void A_consistent_set_passes() => Assert.Empty(Check(new CommittedEvidenceDraft()));

    [Fact]
    [Trait("Spec", "REQ-020")]
    [Trait("Spec", "EDGE-010")]
    public void An_ollama_set_with_the_link_and_the_ollama_decision_passes() =>
        Assert.Empty(Check(new CommittedEvidenceDraft { Runtime = "ollama", Tag = "nemotron-3-nano:4b", DecisionRuntime = "ollama" }));

    // ---- REQ-020 ----

    [Theory]
    [InlineData("llamafile")]
    [InlineData("NIM")]
    [InlineData(null)]
    [Trait("Spec", "REQ-020")]
    public void An_unknown_runtime_fails_and_names_the_field(string? runtime) => _ = Failure(new CommittedEvidenceDraft { Runtime = runtime }, "environment.localRuntime");

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void A_tag_missing_from_the_local_section_fails_and_names_the_tag()
    {
        string message = Failure(new CommittedEvidenceDraft { EndpointTags = ["some/other-model"] }, CommittedEvidenceDraft.DefaultTag, "docs/endpoints.md");

        Assert.Contains("environment.models", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void A_tag_that_only_the_nvidia_section_lists_does_not_count() =>
        _ = Failure(new CommittedEvidenceDraft { Tag = "nvidia/hosted-only-model", EndpointTags = [] }, "nvidia/hosted-only-model");

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void Partial_offload_without_the_flag_fails_and_names_the_flag() =>
        _ = Failure(new CommittedEvidenceDraft { GpuOffloadPercent = 62 }, "partial-offload", "62");

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void Partial_offload_with_the_flag_passes()
    {
        var draft = new CommittedEvidenceDraft { GpuOffloadPercent = 62 };
        draft.Flags.Add("partial-offload");

        Assert.Empty(Check(draft));
    }

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void Full_offload_with_the_partial_offload_flag_fails()
    {
        var draft = new CommittedEvidenceDraft { GpuOffloadPercent = 100 };
        draft.Flags.Add("partial-offload");

        _ = Failure(draft, "partial-offload", "100");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1.0)]
    [InlineData(100.5)]
    [Trait("Spec", "REQ-020")]
    public void An_offload_that_is_not_a_percentage_fails_and_names_the_field(double? percent) =>
        _ = Failure(new CommittedEvidenceDraft { GpuOffloadPercent = percent }, "environment.gpuOffloadPercent");

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void The_readme_sentence_with_the_tag_of_the_run_file_passes() =>
        Assert.Empty(CommittedEvidenceRules.CheckReadmeSentence(
            "x\n\nLocal numbers were produced with nim running nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1.\n",
            CommittedEvidenceRules.ParseRun(new CommittedEvidenceDraft().RunJson())));

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void A_readme_that_names_another_tag_fails_and_names_both_tags() =>
        _ = Failure(new CommittedEvidenceDraft { ReadmeTag = "nemotron-3-nano:4b" }, "nemotron-3-nano:4b", CommittedEvidenceDraft.DefaultTag);

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void A_readme_that_names_another_runtime_fails_and_names_both_runtimes() => _ = Failure(new CommittedEvidenceDraft { ReadmeRuntime = "ollama" }, "ollama", "nim");

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void A_readme_without_the_sentence_fails() => _ = Failure(new CommittedEvidenceDraft { ReadmeHasSentence = false }, "Local numbers were produced with");

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void A_sentence_inside_the_generated_results_block_does_not_count() =>
        _ = Failure(new CommittedEvidenceDraft { ReadmeSentenceInsideBlock = true }, "Local numbers were produced with", "results");

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void Agents_with_different_models_are_listed_comma_separated_with_the_planner_first()
    {
        var draft = new CommittedEvidenceDraft { ReviewerTag = "nemotron-3-nano:4b" };
        string expected = CommittedEvidenceDraft.DefaultTag + ", nemotron-3-nano:4b";

        Assert.Equal(expected, CommittedEvidenceRules.ExpectedTagText(CommittedEvidenceDraft.Agents, draft.RunTags));
        Assert.Empty(Check(draft));
        _ = Failure(new CommittedEvidenceDraft { ReviewerTag = "nemotron-3-nano:4b", ReadmeTag = CommittedEvidenceDraft.DefaultTag }, expected);
    }

    // ---- REQ-021 ----

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_baseline_shorter_than_30_seconds_fails_and_names_the_field() => _ = Failure(new CommittedEvidenceDraft { BaselineSeconds = 20 }, "baseline.seconds", "20");

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void Fewer_than_three_repeats_fail_and_name_the_field() => _ = Failure(new CommittedEvidenceDraft { Repeat = 2 }, "run.repeat", "2");

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_missing_load_time_fails_and_names_the_field() => _ = Failure(new CommittedEvidenceDraft { ModelLoadSeconds = null }, "environment.modelLoadSeconds");

    [Theory]
    [InlineData("wall")]
    [InlineData(null)]
    [Trait("Spec", "REQ-021")]
    public void A_power_source_outside_the_vocabulary_fails_and_names_the_field(string? source) =>
        _ = Failure(new CommittedEvidenceDraft { PowerSource = source }, "environment.powerSource");

    [Theory]
    [InlineData("ac")]
    [InlineData("battery")]
    [InlineData("unknown")]
    [Trait("Spec", "REQ-021")]
    public void Every_power_source_of_the_vocabulary_passes(string source) => Assert.Empty(Check(new CommittedEvidenceDraft { PowerSource = source }));

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void The_non_publishable_flag_fails_and_names_it()
    {
        var draft = new CommittedEvidenceDraft();
        draft.Flags.Add("non-publishable");

        _ = Failure(draft, "run.flags", "non-publishable");
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_missing_calibration_file_fails() => _ = Failure(new CommittedEvidenceDraft { HasCalibrationFile = false }, "calibration-*.json");

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_calibration_field_that_is_a_bare_number_fails_and_names_the_field() =>
        _ = Failure(
            new CommittedEvidenceDraft { CalibrationJson = """{"counterJoules": 5, "samplingJoules": {"value": 1, "label": "measured"}, "relativeDifference": {"value": 0.1, "label": "measured"}}""" },
            CommittedEvidenceDraft.CalibrationFileName,
            "counterJoules");

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_calibration_value_of_null_needs_a_reason()
    {
        const string withoutReason = """{"counterJoules": {"value": null, "label": "N/A"}, "samplingJoules": {"value": 1, "label": "measured"}, "relativeDifference": {"value": null, "label": "N/A", "reason": "counter-not-supported"}}""";

        _ = Failure(new CommittedEvidenceDraft { CalibrationJson = withoutReason }, "counterJoules", "reason");
        Assert.DoesNotContain("relativeDifference", string.Join('\n', Check(new CommittedEvidenceDraft { CalibrationJson = withoutReason })), StringComparison.Ordinal);
    }

    // ---- EDGE-010 ----

    [Fact]
    [Trait("Spec", "EDGE-010")]
    public void A_fallback_decision_without_the_readme_link_fails() =>
        _ = Failure(new CommittedEvidenceDraft { Runtime = "ollama", Tag = "nemotron-3-nano:4b", DecisionRuntime = "ollama", ReadmeHasLink = false }, "EDGE-010", "docs/nim-local-attempt.md");

    [Fact]
    [Trait("Spec", "EDGE-010")]
    public void A_fallback_decision_with_a_run_file_that_says_nim_fails() =>
        _ = Failure(new CommittedEvidenceDraft { Runtime = "nim", DecisionRuntime = "ollama" }, "EDGE-010", "ollama", "nim");

    [Fact]
    [Trait("Spec", "EDGE-010")]
    public void A_nim_decision_with_a_run_file_that_says_ollama_fails() =>
        _ = Failure(new CommittedEvidenceDraft { Runtime = "ollama", Tag = "nemotron-3-nano:4b", DecisionRuntime = "nim" }, "EDGE-010", "ollama", "nim");

    [Fact]
    [Trait("Spec", "EDGE-010")]
    public void A_decision_without_a_runtime_line_fails() => _ = Failure(new CommittedEvidenceDraft { DecisionRuntime = null }, "EDGE-010", "Local runtime:");

    [Fact]
    [Trait("Spec", "EDGE-010")]
    public void The_link_is_asserted_for_a_nim_decision_too() => _ = Failure(new CommittedEvidenceDraft { ReadmeHasLink = false }, "EDGE-010", "docs/nim-local-attempt.md");
}