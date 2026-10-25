using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

using Xunit;

using static AgentJoules.TestSupport.ReportRunFixtures;

namespace AgentJoules.UnitTests;

public sealed partial class ReportRenderingTests
{
    // ---- AJ-129 (REQ-031, EDGE-017): the cloudgpu row follows the real state of the tier and is never merged with a laptop row ----

    private static string LocalAndCloudSummary(RunStatus status = RunStatus.Completed) =>
        Render(Wrap(RunDocumentFixtures.Committed(Tier.Local)), Wrap(CloudGpuConsequenceFixtures.Document(status, sampling: true)));

    [Fact]
    [Trait("Spec", "EDGE-017")]
    [Trait("Spec", "REQ-031")]
    public void The_laptop_and_cloud_rows_stay_separate_with_no_merged_or_averaged_figure()
    {
        string summary = LocalAndCloudSummary();

        Assert.Equal(["local", "cloudgpu", "nvidia", "nebius"], TierCells(summary).Distinct());
        Assert.All(DataRows(summary).Where(row => CellsOf(row)[TierColumn] == "cloudgpu"), row => Assert.DoesNotContain("local", row, StringComparison.Ordinal));
        Assert.Empty(CloudGpuConsequenceFixtures.MergedJouleLines(LinesOf(summary)));
        Assert.DoesNotContain("average", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mean across tiers", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [Trait("Spec", "EDGE-017")]
    [Trait("Spec", "REQ-031")]
    [InlineData("| local | 12.5 J | cloudgpu 30 J |", true)]
    [InlineData("local 12 J, cloudgpu 3J", true)]
    [InlineData("local 7 kJ, cloudgpu 3 kJ", true)]
    [InlineData("local 7 Joules, cloudgpu 3 Joule", true)]
    [InlineData("local 7 Jx, cloudgpu 3 kJoulesx", false)]
    [InlineData("| local | 12.5 J |", false)]
    [InlineData("| cloudgpu | 30 J |", false)]
    [InlineData("local and cloudgpu are separate rows", false)]
    public void The_merged_figure_check_fails_on_a_line_that_mixes_both_tiers_and_a_joule_figure(string line, bool merged) =>
        Assert.Equal(merged, CloudGpuConsequenceFixtures.MergedJouleLines([line]).Count == 1);

    [Fact]
    [Trait("Spec", "EDGE-017")]
    [Trait("Spec", "REQ-031")]
    public void A_partial_cloudgpu_file_renders_a_cloudgpu_row_that_says_partial()
    {
        string summary = LocalAndCloudSummary(RunStatus.Partial);

        string row = Assert.Single(DataRows(summary), line => line.StartsWith("| cloudgpu |", StringComparison.Ordinal));
        Assert.Contains("partial", row, StringComparison.Ordinal);
        Assert.Contains(BulletLines(summary), line => line.StartsWith("- cloudgpu:", StringComparison.Ordinal) && line.Contains("partial", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "EDGE-017")]
    [Trait("Spec", "REQ-031")]
    public void An_absent_cloudgpu_tier_renders_not_run_in_the_table_and_the_bullets()
    {
        string summary = Render(Wrap(RunDocumentFixtures.Committed(Tier.Local)));

        Assert.Contains(NotRunRow("cloudgpu"), DataRows(summary));
        Assert.Contains("- cloudgpu: not run", BulletLines(summary));
    }

    [Fact]
    [Trait("Spec", "EDGE-017")]
    [Trait("Spec", "REQ-031")]
    public void Differing_models_or_precision_across_tiers_are_stated_with_the_precision()
    {
        string summary = LocalAndCloudSummary();

        string line = Assert.Single(DisclosureLines(summary), disclosure => disclosure.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal));
        Assert.Equal(@"Models differ across tiers: local=nemotron-3-nano:30b \(Q4\_K\_M\), cloudgpu=model-a \(FP8\)", line);
    }
}