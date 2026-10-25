using System.Text.Json.Nodes;

using AgentJoules.Reporting.Endpoints;
using AgentJoules.TestSupport.Evidence;

using Xunit;

namespace AgentJoules.UnitTests;

// The rules of CommittedEvidenceRules that a field-name assertion could pass for the wrong reason, and the shapes the first set of tests left out. Each rule is called alone and its one message is compared whole.
public sealed partial class CommittedEvidenceTests
{
    private static JsonNode RunOf(CommittedEvidenceDraft draft) => CommittedEvidenceRules.ParseRun(draft.RunJson());

    private static EndpointsDocument EndpointsOf(CommittedEvidenceDraft draft) => EndpointsDocParser.Parse(draft.EndpointsText());

    [Theory]
    [InlineData("llamafile", "\"llamafile\"")]
    [InlineData("NIM", "\"NIM\"")]
    [InlineData(null, "null or missing")]
    [Trait("Spec", "REQ-020")]
    public void An_unknown_runtime_gives_exactly_the_vocabulary_message(string? runtime, string found)
    {
        var draft = new CommittedEvidenceDraft { Runtime = runtime };

        Assert.Equal(
            $"environment.localRuntime: expected \"nim\" or \"ollama\" but found {found}",
            Assert.Single(CommittedEvidenceRules.CheckRuntime(RunOf(draft), EndpointsOf(draft))));
    }

    [Theory]
    [InlineData(-1.0, "-1")]
    [InlineData(100.5, "100.5")]
    [Trait("Spec", "REQ-020")]
    public void An_offload_outside_zero_to_100_gives_exactly_the_range_message(double percent, string found) =>
        Assert.Equal(
            $"environment.gpuOffloadPercent: expected a number from 0 to 100 but found {found}",
            Assert.Single(CommittedEvidenceRules.CheckOffload(RunOf(new CommittedEvidenceDraft { GpuOffloadPercent = percent }))));

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void A_missing_offload_gives_exactly_the_range_message() =>
        Assert.Equal(
            "environment.gpuOffloadPercent: expected a number from 0 to 100 but found null or missing",
            Assert.Single(CommittedEvidenceRules.CheckOffload(RunOf(new CommittedEvidenceDraft { GpuOffloadPercent = null }))));

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void The_planner_tag_comes_first_in_the_sentence_even_when_the_planner_is_not_first_in_the_file()
    {
        string expected = CommittedEvidenceDraft.DefaultTag + ", nemotron-3-nano:4b";
        var draft = new CommittedEvidenceDraft { PlannerNotFirst = true, ReviewerTag = "nemotron-3-nano:4b", ReadmeTag = expected };

        Assert.Empty(CommittedEvidenceRules.CheckReadmeSentence(draft.ReadmeText(), RunOf(draft)));

        var reversed = new CommittedEvidenceDraft { PlannerNotFirst = true, ReviewerTag = "nemotron-3-nano:4b", ReadmeTag = "nemotron-3-nano:4b, " + CommittedEvidenceDraft.DefaultTag };
        _ = Assert.Single(CommittedEvidenceRules.CheckReadmeSentence(reversed.ReadmeText(), RunOf(reversed)));
    }

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void A_baseline_model_entry_does_not_enter_the_sentence_but_must_be_listed_in_the_local_section()
    {
        const string qwen = "qwen3:8b";
        var listed = new CommittedEvidenceDraft { BaselineTag = qwen, EndpointTags = [CommittedEvidenceDraft.DefaultTag, qwen], ReadmeTag = CommittedEvidenceDraft.DefaultTag };
        Assert.Empty(Check(listed));

        var withBaselineTag = new CommittedEvidenceDraft { BaselineTag = qwen, EndpointTags = [CommittedEvidenceDraft.DefaultTag, qwen], ReadmeTag = CommittedEvidenceDraft.DefaultTag + ", " + qwen };
        _ = Assert.Single(CommittedEvidenceRules.CheckReadmeSentence(withBaselineTag.ReadmeText(), RunOf(withBaselineTag)));

        var unlisted = new CommittedEvidenceDraft { BaselineTag = qwen, ReadmeTag = CommittedEvidenceDraft.DefaultTag, EndpointTags = [CommittedEvidenceDraft.DefaultTag] };
        Assert.Contains(qwen, Assert.Single(CommittedEvidenceRules.CheckRuntime(RunOf(unlisted), EndpointsOf(unlisted))), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void An_endpoints_document_without_a_local_section_fails_and_says_so()
    {
        var draft = new CommittedEvidenceDraft { EndpointsHaveLocalSection = false };

        string message = Assert.Single(CommittedEvidenceRules.CheckRuntime(RunOf(draft), EndpointsOf(draft)));

        Assert.Contains("the document has no \"## local\" section", message, StringComparison.Ordinal);
        Assert.StartsWith("environment.models[0].modelId:", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void An_empty_models_list_gives_exactly_the_models_message()
    {
        var draft = new CommittedEvidenceDraft { EmptyModels = true };

        Assert.Equal(
            "environment.models: expected a non-empty list of model entries but found []",
            Assert.Single(CommittedEvidenceRules.CheckRuntime(RunOf(draft), EndpointsOf(draft))));
    }

    [Fact]
    [Trait("Spec", "REQ-020")]
    public void A_results_block_without_its_end_marker_hides_the_rest_of_the_readme()
    {
        string message = Assert.Single(CommittedEvidenceRules.CheckReadmeSentence(
            "# x\n\n<!-- results:begin -->\n\nLocal numbers were produced with nim running nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1.\n",
            RunOf(new CommittedEvidenceDraft())));

        Assert.StartsWith("README.md: no sentence", message, StringComparison.Ordinal);

        var draft = new CommittedEvidenceDraft { ReadmeBlockClosed = false };
        Assert.Empty(CommittedEvidenceRules.CheckReadmeSentence(draft.ReadmeText(), RunOf(draft)));
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_string_load_time_gives_exactly_the_number_message() =>
        Assert.Equal(
            "environment.modelLoadSeconds: expected a number but found \"4.5\"",
            Assert.Single(CommittedEvidenceRules.CheckMeasurement(RunOf(new CommittedEvidenceDraft { ModelLoadSecondsText = "4.5" }))));

    [Theory]
    [InlineData("Local numbers were produced with  running nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1.")]
    [InlineData("Local numbers were produced with nim running .")]
    [InlineData("Local numbers were produced with nim running  .")]
    [Trait("Spec", "REQ-020")]
    public void A_sentence_with_an_empty_runtime_or_tag_does_not_count(string sentence)
    {
        string message = Assert.Single(CommittedEvidenceRules.CheckReadmeSentence("# x\n\n" + sentence + "\n", RunOf(new CommittedEvidenceDraft())));

        Assert.StartsWith("README.md: no sentence", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_baseline_of_29_999_seconds_fails_and_30_passes()
    {
        Assert.Equal(
            "baseline.seconds: expected at least 30 but found 29.999",
            Assert.Single(CommittedEvidenceRules.CheckMeasurement(RunOf(new CommittedEvidenceDraft { BaselineSeconds = 29.999 }))));
        Assert.Empty(CommittedEvidenceRules.CheckMeasurement(RunOf(new CommittedEvidenceDraft { BaselineSeconds = 30 })));
    }
}