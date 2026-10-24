using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;

using Xunit;

using static AgentJoules.TestSupport.ReportRunFixtures;

namespace AgentJoules.UnitTests;

public sealed partial class ReportRenderingTests
{
    // ---- M3 exit (peer M-1, REQ-010): the self-judging statement has three states, always prints the ids it compared and never says "no" ----

    private const string JudgeId = "nvidia/nemotron-nano-3-30b-a3b";
    private const string StepId = "nemotron-3-nano:30b";

    // The report model of one local run file whose tier report is replaced by the given facts: the constructed model of the tests of each state.
    private static ReportModel ModelWith(bool? selfJudged, string[] judgeIds, params string[] stepIds)
    {
        ReportModel built = ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 1.0))]);
        TierReport report = built.Tiers[0] with
        {
            SelfJudged = selfJudged,
            JudgeModelIds = judgeIds,
            JudgedStepModelIds = [.. stepIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        };

        return built with { Tiers = [report] };
    }

    private static string LocalBullet(ReportModel model) => Assert.Single(BulletLines(SummaryMarkdownRenderer.Render(model)), line => line.StartsWith("- local: ", StringComparison.Ordinal));

    // The text between "self-judging " and "; energy method" of the one bullet of a summary.
    private static string SelfJudgingOf(ReportModel model)
    {
        string bullet = LocalBullet(model);
        int start = bullet.IndexOf("; self-judging ", StringComparison.Ordinal) + "; self-judging ".Length;
        int end = bullet.IndexOf("; energy method", StringComparison.Ordinal);

        Assert.True(start > "; self-judging ".Length && end > start);
        return bullet[start..end];
    }

    [Fact]
    public void Self_judging_is_yes_with_the_compared_ids_when_the_judge_model_is_the_step_model()
    {
        string text = SelfJudgingOf(ModelWith(true, [JudgeId], JudgeId, JudgeId));

        Assert.Equal($"yes \\(judge model {JudgeId} equals the step model id\\)", text);
    }

    [Fact]
    public void Self_judging_is_not_determinable_with_both_ids_when_the_ids_differ()
    {
        string text = SelfJudgingOf(ModelWith(false, [JudgeId], StepId, StepId));

        Assert.Equal($"not determinable from the model ids \\(judge model {JudgeId}; step model {StepId}; compared by model id only\\)", text);
    }

    [Fact]
    public void Self_judging_is_not_determinable_when_one_judged_step_has_another_id_and_lists_every_step_id()
    {
        string text = SelfJudgingOf(ModelWith(true, [JudgeId], JudgeId, StepId));

        Assert.Equal($"not determinable from the model ids \\(judge model {JudgeId}; step model {StepId}+{JudgeId}; compared by model id only\\)", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void Self_judging_is_not_judged_when_no_step_was_judged(bool? selfJudged)
    {
        string text = SelfJudgingOf(ModelWith(selfJudged, [], StepId));

        Assert.Equal("not judged \\(no step was judged\\)", text);
    }

    [Fact]
    public void Self_judging_is_not_judged_when_the_file_makes_no_statement()
    {
        string text = SelfJudgingOf(ModelWith(null, [JudgeId], StepId));

        Assert.Equal("not judged \\(no step was judged\\)", text);
    }

    [Fact]
    public void Several_judge_models_are_listed_and_the_statement_is_not_determinable()
    {
        string text = SelfJudgingOf(ModelWith(false, ["judge-a", "judge-b"], StepId));

        Assert.Equal($"not determinable from the model ids \\(judge model judge-a, judge-b; step model {StepId}; compared by model id only\\)", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public void No_state_says_no(bool? selfJudged)
    {
        string[][] judges = [[], [JudgeId]];
        string[][] steps = [[StepId], [JudgeId], [JudgeId, StepId]];

        foreach (string[] judge in judges)
        {
            foreach (string[] step in steps)
            {
                string line = LocalBullet(ModelWith(selfJudged, judge, step));

                Assert.DoesNotMatch(@"self-judging no\b", line);
            }
        }
    }

    [Fact]
    public void The_disclosure_sentence_still_follows_the_run_flag()
    {
        SelectedRun selfJudged = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Run = document.Run with { SelfJudged = true } });
        SelectedRun other = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Run = document.Run with { SelfJudged = false } });

        Assert.Contains(ReportSentences.SelfJudged, ReportAggregator.Build([selfJudged]).Disclosures);
        Assert.DoesNotContain(ReportSentences.SelfJudged, ReportAggregator.Build([other]).Disclosures);
    }

    [Fact]
    public void The_committed_run_files_print_the_three_states_through_the_shared_header_lines()
    {
        ReportModel model = ReportAggregator.Build(CommittedRuns());

        string[] bullets = [.. SummaryMarkdownRenderer.Render(model).Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal) && line.Contains("; self-judging ", StringComparison.Ordinal))];
        string[] states = [.. bullets.Select(static bullet => bullet[(bullet.IndexOf("; self-judging ", StringComparison.Ordinal) + "; self-judging ".Length)..bullet.IndexOf("; energy method", StringComparison.Ordinal)])];

        Assert.Equal(3, states.Length);
        Assert.StartsWith("not determinable from the model ids \\(judge model nvidia/nemotron-nano-3-30b-a3b; step model nemotron-3-nano:30b;", states[0], StringComparison.Ordinal);
        Assert.Equal("yes \\(judge model nvidia/nemotron-nano-3-30b-a3b equals the step model id\\)", states[1]);
        Assert.StartsWith("not determinable from the model ids \\(judge model nvidia/nemotron-nano-3-30b-a3b; step model nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B;", states[2], StringComparison.Ordinal);
    }
}