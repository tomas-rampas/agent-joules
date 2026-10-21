using AgentJoules.Core.Cli;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-050: Planner, fan-out to Researcher and Writer, fan-in barrier, Reviewer, run as a workflow. Scoped to the feature because sentences such as "the sink
/// holds ..." and "the workflow runs" are reused by other stories with other meanings. The state lives in <see cref="FanOutFanInContext"/>; the clients are fakes
/// (or the real SDK against a fake endpoint that answers 500) and the clock moves only when a fake yields or the code under test waits.
/// </summary>
[Binding]
[Scope(Feature = "AJ-050 Run Planner, fan-out Researcher and Writer, fan-in Reviewer as a workflow")]
public sealed class FanOutFanInSteps(FanOutFanInContext context)
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly FanOutFanInContext _context = context;

    [AfterScenario]
    public async Task DisposeTheRig() => await _context.DisposeAsync();

    [Given(@"^the workflow factory over four fake chat clients$")]
    [Given(@"^fake streaming clients$")]
    public static void GivenFakeClients()
    {
        // The default rig: four streaming fakes, concurrent mode.
    }

    [Given(@"^serialized mode and fake clients whose Researcher takes 1 second and Writer takes 2 seconds$")]
    public void GivenSerializedModeAndSlowBranches()
    {
        _context.Serialized = true;
        _context.Delays["Researcher"] = TimeSpan.FromSeconds(1);
        _context.Delays["Writer"] = TimeSpan.FromSeconds(2);
    }

    [Given(@"^a (Planner|Researcher|Writer|Reviewer) client whose endpoint answers (\d+)$")]
    public void GivenAnAgentWhoseEndpointFails(string agent, int status)
    {
        _context.FailingAgent = agent;
        _context.EndpointStatus = status;
    }

    [When(@"^the workflow runs$")]
    public async Task WhenTheWorkflowRuns() => await _context.RunAsync(FanOutFanInContext.DefaultTask, HangGuard);

    [When(@"^the workflow runs the task ""([^""]*)""$")]
    public async Task WhenTheWorkflowRunsTheTask(string taskName) => await _context.RunAsync(taskName, HangGuard);

    [Then(@"^the sink holds readings for ""([^""]*)"", ""([^""]*)"", ""([^""]*)"" and ""([^""]*)""$")]
    public void ThenTheSinkHoldsReadingsFor(string first, string second, string third, string fourth)
    {
        RequireSuccess();

        IReadOnlyList<StepReading> readings = _context.RequireRig().Readings();
        string[] expected = [first, second, third, fourth];

        Assert.Equal(4, readings.Count);
        Assert.Equal(expected.Order(StringComparer.Ordinal), readings.Select(reading => reading.Agent).Order(StringComparer.Ordinal));
        Assert.NotNull(_context.Output);
        Assert.All(readings, reading => Assert.Equal(WorkflowRig.TaskName, reading.Task));
    }

    [Then(@"^each reading agent name matches the agent owning that client$")]
    public void ThenEachReadingNamesTheOwnerOfItsClient()
    {
        WorkflowRig rig = _context.RequireRig();

        foreach (StepReading reading in rig.Readings())
        {
            // The fake that served the call recorded the model of the agent that owns it; the reading must carry that agent name and that model.
            FakeChatCall call = Assert.Single(rig.FakeOf(reading.Agent).Calls);
            Assert.Equal(WorkflowRig.ModelOf(reading.Agent), call.Options?.ModelId);
            Assert.Equal(call.Options?.ModelId, reading.ModelId);
        }
    }

    [Then(@"^the Reviewer window starts after the Writer window ends$")]
    public void ThenTheReviewerStartsAfterTheWriterEnds()
    {
        RequireSuccess();

        WorkflowRig rig = _context.RequireRig();
        StepReading researcher = Assert.Single(rig.ReadingsOf("Researcher"));
        StepReading writer = Assert.Single(rig.ReadingsOf("Writer"));
        StepReading reviewer = Assert.Single(rig.ReadingsOf("Reviewer"));

        Assert.True(reviewer.StartUtc >= writer.EndUtc, $"The Reviewer window started at {reviewer.StartUtc:O}, before the Writer window ended at {writer.EndUtc:O}.");
        Assert.True(reviewer.StartUtc >= researcher.EndUtc, $"The Reviewer window started at {reviewer.StartUtc:O}, before the Researcher window ended at {researcher.EndUtc:O}.");
    }

    [Then(@"^every reading has a measured ttftMs$")]
    public void ThenEveryReadingHasAMeasuredTtft()
    {
        RequireSuccess();

        IReadOnlyList<StepReading> readings = _context.RequireRig().Readings();

        Assert.Equal(4, readings.Count);
        Assert.All(readings, reading =>
        {
            Assert.Equal(MetricLabel.Measured, reading.TtftMs.Label);
            Assert.NotNull(reading.TtftMs.Value);
        });
    }

    [Then(@"^StepFailedException is raised$")]
    public void ThenStepFailedExceptionIsRaised() => _ = Assert.IsType<StepFailedException>(_context.Thrown);

    [Then(@"^the sink holds an ""([^""]*)"" reading for ""([^""]*)"" and no reading for ""([^""]*)""$")]
    public void ThenTheSinkHoldsAnErrorReadingAndNoReading(string outcome, string agent, string absent)
    {
        WorkflowRig rig = _context.RequireRig();

        Assert.Equal(outcome, Assert.Single(rig.ReadingsOf(agent)).Outcome);
        Assert.Empty(rig.ReadingsOf(absent));
    }

    [Then(@"^the sink holds one reading for ""([^""]*)"" with outcome ""([^""]*)""$")]
    public void ThenTheSinkHoldsOneReading(string agent, string outcome)
    {
        _ = Assert.IsType<StepFailedException>(_context.Thrown);

        WorkflowRig rig = _context.RequireRig();

        Assert.Equal(outcome, Assert.Single(rig.ReadingsOf(agent)).Outcome);
        Assert.Single(rig.Readings());
    }

    [Then(@"^no reading for ""([^""]*)"" or ""([^""]*)""$")]
    public void ThenNoReadingFor(string first, string second)
    {
        WorkflowRig rig = _context.RequireRig();

        Assert.Empty(rig.ReadingsOf(first));
        Assert.Empty(rig.ReadingsOf(second));
    }

    private void RequireSuccess() => Assert.True(_context.Thrown is null, $"The workflow failed with {_context.Thrown?.GetType().Name}.");
}