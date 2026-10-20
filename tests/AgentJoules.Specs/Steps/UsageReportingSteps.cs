using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Metering;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-042: usage is reported as measured when the provider stated it and as not-reported when it did not, never as 0. Scoped to the feature so
/// that sentences it shares in wording with other bindings ("the stream is consumed") resolve here only inside this feature. The state lives in
/// <see cref="UsageReportingContext"/>; every delay is applied by the fake chat client on the fake clock, so nothing waits on the wall clock.
/// </summary>
[Binding]
[Scope(Feature = "AJ-042 Report absent usage as not-reported, never 0")]
public sealed class UsageReportingSteps(UsageReportingContext context)
{
    private static readonly IReadOnlyList<ChatMessage> Messages = [new ChatMessage(ChatRole.User, "Summarise the report.")];

    // A step that waits on a call must fail, not hang the run, when the code under test leaks (system clock, not the fake one).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly UsageReportingContext _context = context;

    [Given(@"^a fake client that returns usage with (\d+) input and (\d+) output tokens$")]
    public void GivenAFakeClientThatReturnsUsage(long input, long output) =>
        _context.Inner = new FakeChatClient(_context.Time) { ResponseUsage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output } };

    [Given(@"^a fake client that returns no usage$")]
    public void GivenAFakeClientThatReturnsNoUsage() => _context.Inner = new FakeChatClient(_context.Time);

    [Given(@"^a fake stream whose updates carry no usage content$")]
    public void GivenAFakeStreamWithoutUsageContent() =>
        _context.Inner = new FakeChatClient(_context.Time)
        {
            Updates = [new FakeUpdate("The report ", TimeSpan.FromMilliseconds(100)), new FakeUpdate("says energy matters.", TimeSpan.FromMilliseconds(100))],
        };

    [Given(@"^a task with maxOutputTokens (\d+) and a response reporting (\d+) output tokens$")]
    public void GivenATaskLimitAndAResponseReportingOutputTokens(int limit, long output)
    {
        _context.MaxOutputTokens = limit;
        _context.Inner = new FakeChatClient(_context.Time) { ResponseUsage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = output } };
    }

    [Given(@"^a task with maxOutputTokens (\d+) and a response without usage$")]
    public void GivenATaskLimitAndAResponseWithoutUsage(int limit)
    {
        _context.MaxOutputTokens = limit;
        _context.Inner = new FakeChatClient(_context.Time);
    }

    [When(@"^a buffered call is metered$")]
    public async Task WhenABufferedCallIsMetered()
    {
        MeteredChatClient client = await _context.CreateClientAsync();
        _ = await client.GetResponseAsync(Messages, _context.Options()).WaitAsync(HangGuard);
    }

    [When(@"^the stream is consumed$")]
    public async Task WhenTheStreamIsConsumed()
    {
        MeteredChatClient client = await _context.CreateClientAsync();
        await ConsumeAsync(client).WaitAsync(HangGuard);
    }

    [Then(@"^(inputTokens|outputTokens) is (\d+) with label ""(.*)""$")]
    public void ThenATokenMetricIsMeasured(string member, long expected, string label)
    {
        Metric metric = MetricOf(_context.SingleReading(), member);

        Assert.Equal(LabelNamed(label), metric.Label);
        Assert.Equal((double)expected, metric.Value);
    }

    [Then(@"^inputTokens and outputTokens have label ""(.*)"" and a null value$")]
    public void ThenBothTokenMetricsHaveLabelAndNoValue(string label)
    {
        StepReading reading = _context.SingleReading();

        foreach (Metric metric in new[] { reading.InputTokens, reading.OutputTokens })
        {
            Assert.Equal(LabelNamed(label), metric.Label);
            Assert.Null(metric.Value);
            Assert.Equal("provider-omitted-usage", metric.Reason);
        }
    }

    [Then(@"^cost has label ""(.*)"" and a null value$")]
    public void ThenCostHasLabelAndNoValue(string label)
    {
        Metric cost = _context.SingleReading().Cost;

        Assert.Equal(LabelNamed(label), cost.Label);
        Assert.Null(cost.Value);
    }

    [Then(@"^the reading has flag ""(.*)""$")]
    public void ThenTheReadingHasFlag(string flag) =>
        Assert.True(_context.SingleReading().Flags.HasFlag(ReadingFlagNames.Parse(flag)), $"The reading does not carry the flag {flag}.");

    [Then(@"^the reading has no flag ""(.*)""$")]
    public void ThenTheReadingHasNoFlag(string flag) =>
        Assert.False(_context.SingleReading().Flags.HasFlag(ReadingFlagNames.Parse(flag)), $"The reading carries the flag {flag}.");

    private static async Task ConsumeAsync(MeteredChatClient client)
    {
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Messages))
        {
            _ = update;
        }
    }

    private static Metric MetricOf(StepReading reading, string member) => member == "inputTokens" ? reading.InputTokens : reading.OutputTokens;

    // The label names of the feature are the vocabulary strings of the results file.
    private static MetricLabel LabelNamed(string name) => name switch
    {
        "measured" => MetricLabel.Measured,
        "not-reported" => MetricLabel.NotReported,
        "N/A" => MetricLabel.NotApplicable,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "The feature names a label that the step does not know."),
    };
}