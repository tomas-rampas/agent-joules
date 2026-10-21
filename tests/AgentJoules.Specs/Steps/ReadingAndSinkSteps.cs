using System.Globalization;
using System.Text.Json;

using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-040: the per-call reading and the metrics sink. Self-contained: its state lives here, not in ScenarioWorld, and the class is scoped
/// to the feature so that a sentence it shares in wording with another binding (for example "the reading is published") is resolved here
/// inside this feature and never matches another feature. Nothing here waits on a clock; the unmetered meter runs on a fake clock inside
/// <see cref="StepReadingFixtures"/>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-040 Define the per-call reading and publish it to a metrics sink")]
public sealed class ReadingAndSinkSteps
{
    private InMemoryMetricsSink? _sink;
    private InMemoryMetricsSink? _workflowSink;
    private InMemoryMetricsSink? _judgeSink;
    private StepReading? _reading;
    private Metric? _metric;
    private Exception? _constructionFailure;

    [Given(@"^an in-memory metrics sink$")]
    public void GivenAnInMemoryMetricsSink() => _sink = new InMemoryMetricsSink();

    [When(@"^readings for agents ""([^""]*)"", ""([^""]*)"" and ""([^""]*)"" are published in that order$")]
    public void WhenReadingsForAgentsArePublishedInThatOrder(string first, string second, string third)
    {
        foreach (string agent in new[] { first, second, third })
        {
            RequireSink().Publish(StepReadingFixtures.Metered(agent));
        }
    }

    [Then(@"^the snapshot lists ""([^""]*)"", ""([^""]*)"" and ""([^""]*)"" in that order$")]
    public void ThenTheSnapshotListsInThatOrder(string first, string second, string third)
    {
        Assert.Equal([first, second, third], RequireSink().Snapshot().Select(reading => reading.Agent));
    }

    [Given(@"^a reading for tier ""([^""]*)"" built with the unmetered energy meter$")]
    public async Task GivenAReadingForTierBuiltWithTheUnmeteredEnergyMeter(string tier)
    {
        _reading = await StepReadingFixtures.HostedAsync(JsonSerializer.Deserialize<Tier>("\"" + tier + "\""));
    }

    [When(@"^the reading is published$")]
    public void WhenTheReadingIsPublished()
    {
        _sink ??= new InMemoryMetricsSink();
        _sink.Publish(_reading ?? throw new InvalidOperationException("No reading was built."));
    }

    [Then(@"^grossJoules, netJoules, avgW and peakW each have label ""([^""]*)"", reason ""([^""]*)"" and a null value$")]
    public void ThenTheEnergyMetricsAreNotApplicable(string label, string reason)
    {
        // The expected metric is built from the words of the scenario through the same converter the results file uses, then compared whole.
        string json = string.Create(CultureInfo.InvariantCulture, $"{{\"value\":null,\"label\":\"{label}\",\"reason\":\"{reason}\"}}");
        Metric expected = JsonSerializer.Deserialize<Metric>(json)!;
        StepReading published = Assert.Single(RequireSink().Snapshot());

        foreach (Metric metric in new[] { published.GrossJoules, published.NetJoules, published.AvgW, published.PeakW })
        {
            Assert.Equal(expected, metric);
            Assert.Equal(MetricLabel.NotApplicable, metric.Label);
            Assert.Null(metric.Value);
        }
    }

    [Given(@"^a metric factory$")]
    public void GivenAMetricFactory()
    {
        // The factories of Metric cannot be asked for a number under an N/A label, so the request goes through the one path that takes a
        // label and a value from the caller: the metric JSON converter, which runs the same constructor checks as the factories.
        _metric = null;
        _constructionFailure = null;
    }

    [When(@"^a metric with label ""([^""]*)"" and value (-?\d+) is requested$")]
    public void WhenAMetricWithLabelAndValueIsRequested(string label, int value)
    {
        string json = string.Create(CultureInfo.InvariantCulture, $"{{\"value\":{value},\"label\":\"{label}\",\"reason\":\"requested-by-test\"}}");
        try
        {
            _metric = JsonSerializer.Deserialize<Metric>(json);
        }
        catch (ArgumentException failure)
        {
            _constructionFailure = failure;
        }
    }

    [Then(@"^construction fails and no metric is created$")]
    public void ThenConstructionFailsAndNoMetricIsCreated()
    {
        ArgumentException failure = Assert.IsType<ArgumentException>(_constructionFailure);
        Assert.Equal("value", failure.ParamName);
        Assert.Null(_metric);
    }

    [Given(@"^a workflow sink and a separate judge sink$")]
    public void GivenAWorkflowSinkAndASeparateJudgeSink()
    {
        _workflowSink = new InMemoryMetricsSink();
        _judgeSink = new InMemoryMetricsSink();
        Assert.NotSame(_workflowSink, _judgeSink);
    }

    [When(@"^a reading for agent ""([^""]*)"" is published to the judge sink$")]
    public void WhenAReadingForAgentIsPublishedToTheJudgeSink(string agent)
    {
        (_judgeSink ?? throw new InvalidOperationException("No judge sink was created.")).Publish(StepReadingFixtures.Metered(agent));
    }

    [Then(@"^the workflow sink snapshot is empty$")]
    public void ThenTheWorkflowSinkSnapshotIsEmpty()
    {
        Assert.Empty((_workflowSink ?? throw new InvalidOperationException("No workflow sink was created.")).Snapshot());
        Assert.Single((_judgeSink ?? throw new InvalidOperationException("No judge sink was created.")).Snapshot());
    }

    private InMemoryMetricsSink RequireSink() => _sink ?? throw new InvalidOperationException("No metrics sink was created.");
}