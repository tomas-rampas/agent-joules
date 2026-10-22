using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Runs;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-076: score each ok step with a metered hosted judge (REQ-010). Scoped to the feature because sentences such as "the run is judged" are reused by other stories with other meanings. The
/// state lives in <see cref="JudgeScoringContext"/>; the real <c>JudgeService</c> runs over fake provider clients on a fake clock, and the scenario about the workflow sink runs the real command
/// with the production judge over a fake endpoint.
/// </summary>
[Binding]
[Scope(Feature = "AJ-076 Score each ok step with a metered hosted judge")]
public sealed class JudgeScoringSteps(JudgeScoringContext context)
{
    private readonly JudgeScoringContext _context = context;

    private JudgeResult Result => _context.Result ?? throw new XunitException("The run was not judged.");

    private StepReading TheStep => Assert.Single(Result.Steps);

    private static MetricLabel LabelNamed(string name) => name switch
    {
        "model-judged" => MetricLabel.ModelJudged,
        "N/A" => MetricLabel.NotApplicable,
        _ => throw new XunitException($"Unknown label word {name}."),
    };

    [Given(@"^a completed local step with net energy (\d+) J and a fake judge that replies ""([^""]*)""$")]
    public void GivenACompletedLocalStepAndAFakeJudge(double joules, string reply)
    {
        _context.Steps.Add(JudgeScoringContext.Step("Writer", netJoules: joules));
        _context.Reply = reply;
    }

    [Given(@"^4 workflow readings and 4 judge readings with known token counts$")]
    public void GivenFourWorkflowAndFourJudgeReadings()
    {
        foreach (string agent in new[] { "Planner", "Researcher", "Writer", "Reviewer" })
        {
            _context.Steps.Add(JudgeScoringContext.Step(agent));
        }
    }

    [Given(@"^a judge model ID equal to the Writer model ID$")]
    public void GivenAJudgeModelEqualToTheWriterModel()
    {
        foreach (string agent in new[] { "Planner", "Researcher", "Writer", "Reviewer" })
        {
            _context.Steps.Add(JudgeScoringContext.Step(agent));
        }

        _context.JudgeModel = _context.Steps.Single(static step => step.Agent == "Writer").ModelId;
    }

    [Given(@"^a step with outcome ""([^""]*)""$")]
    public void GivenAStepWithOutcome(string outcome) => _context.Steps.Add(JudgeScoringContext.Step("Writer", outcome));

    [Given(@"^a step with outcome ""ok"" and outputChars 0$")]
    public void GivenAnOkStepWithoutOutput() => _context.Steps.Add(JudgeScoringContext.Step("Writer", text: string.Empty));

    [Given(@"^a judged run$")]
    public void GivenAJudgedRun() =>

        // The command is run by the When step: this scenario is about what the workflow sink holds after a run whose judge scored its steps, so the judge answers 4.
        _context.Reply = "4";

    [When(@"^the run is judged$")]
    public Task WhenTheRunIsJudged() => _context.JudgeAsync();

    [When(@"^the run totals are assembled$")]
    public void WhenTheRunTotalsAreAssembled()
    {
        // The judge readings are the metered readings the judge would have published: known token counts 11, 12, 13 and 14 for the four calls.
        double[] tokens = [11, 12, 13, 14];
        StepReading[] judge = [.. _context.Steps.Select((step, index) => JudgeScoringContext.JudgeReadingOf(step, tokens[index]))];
        _context.Totals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, _context.Steps, judge, wallClockMs: 4000.0);
        _context.JudgeInputTokenSum = tokens.Sum();
    }

    [When(@"^the workflow sink snapshot is read$")]
    public Task WhenTheWorkflowSinkSnapshotIsRead() => _context.RunJudgedCommandAsync();

    [Then(@"^the step quality is (\d+) with label ""([^""]*)""$")]
    public void ThenTheStepQualityIs(double value, string label)
    {
        Assert.Equal(value, TheStep.Quality.Value);
        Assert.Equal(LabelNamed(label), TheStep.Quality.Label);
    }

    [Then(@"^joulesPerQualityPoint is (\d+)$")]
    public void ThenJoulesPerQualityPointIs(double value)
    {
        // The ratio is derived when the document is assembled, from the quality the judge wrote and the net energy of the step.
        Metric ratio = RunAssembler.WithDerivedMetrics(TheStep).JoulesPerQualityPoint;
        Assert.Equal(value, ratio.Value);
        Assert.Equal(MetricLabel.ModelJudged, ratio.Label);
    }

    [Then(@"^totals inputTokens equals the sum over the workflow readings only$")]
    public void ThenTotalsInputTokensEqualsTheWorkflowSum()
    {
        Totals totals = _context.Totals ?? throw new XunitException("The totals were not assembled.");
        Assert.Equal(_context.Steps.Sum(static step => step.InputTokens.Value!.Value), totals.InputTokens.Value);
    }

    [Then(@"^totals\.judge inputTokens equals the sum over the judge readings$")]
    public void ThenTotalsJudgeInputTokensEqualsTheJudgeSum()
    {
        Totals totals = _context.Totals ?? throw new XunitException("The totals were not assembled.");
        Assert.Equal(_context.JudgeInputTokenSum, totals.Judge.InputTokens.Value);
    }

    [Then(@"^run\.selfJudged is true$")]
    public void ThenRunSelfJudgedIsTrue()
    {
        RunInfo run = RunAssembler.BuildRunInfo("20261022T091530Z-local", Tier.Local, ["summarise"], RunMode.Serialized, 1, new DateTimeOffset(2026, 10, 22, 9, 15, 30, TimeSpan.Zero), RunStatus.Completed, Result.SelfJudged);
        Assert.True(run.SelfJudged);
    }

    [Then(@"^the fake judge received no call for that step$")]
    public void ThenTheFakeJudgeReceivedNoCall() => Assert.Empty(_context.Calls);

    [Then(@"^the step quality has label ""([^""]*)""$")]
    public void ThenTheStepQualityHasLabel(string label) => Assert.Equal(LabelNamed(label), TheStep.Quality.Label);

    [Then(@"^the step quality has label ""([^""]*)"" and reason ""([^""]*)""$")]
    public void ThenTheStepQualityHasLabelAndReason(string label, string reason)
    {
        Assert.Equal(LabelNamed(label), TheStep.Quality.Label);
        Assert.Equal(reason, TheStep.Quality.Reason);
        Assert.Equal("not-scored", TheStep.QualityReason);
    }

    [Then(@"^it contains no reading for agent ""([^""]*)""$")]
    public void ThenItContainsNoReadingForAgent(string agent)
    {
        System.Text.Json.JsonElement[] steps = [.. (_context.Document ?? throw new XunitException("The command did not run.")).RootElement.GetProperty("steps").EnumerateArray()];
        System.Text.Json.JsonElement[] judge = [.. _context.Document.RootElement.GetProperty("judge").EnumerateArray()];

        Assert.Equal(4, steps.Length);
        Assert.DoesNotContain(steps, step => step.GetProperty("agent").GetString() == agent);
        Assert.Equal(4, (_context.StepLines ?? throw new XunitException("The command did not run.")).Count);
        Assert.DoesNotContain(_context.StepLines, line => line.Contains(agent, StringComparison.Ordinal));

        // The positive control: the judge ran, and its readings are in the separate array.
        Assert.Equal(4, judge.Length);
        Assert.All(judge, reading => Assert.Equal(agent, reading.GetProperty("agent").GetString()));
    }
}