using System.ClientModel;
using System.Text;

using AgentJoules.Core.Results;
using AgentJoules.Metering;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-045: the 429 retry with backoff, and the wait kept out of the latency. Scoped to the feature because sentences such as "the stream is consumed" and
/// "the reading has ..." are reused by other stories with other meanings. The state lives in <see cref="RateLimitRetryContext"/>; every call runs the real
/// OpenAI SDK against a fake endpoint, and the fake clock moves only when the code under test waits, so nothing waits on the wall clock.
/// </summary>
[Binding]
[Scope(Feature = "AJ-045 Retry HTTP 429 with backoff and keep the wait out of latency")]
public sealed class RateLimitRetrySteps(RateLimitRetryContext context)
{
    private static readonly ChatMessage[] Messages = [new(ChatRole.User, "Summarise the report.")];

    private readonly RateLimitRetryContext _context = context;

    [Given(@"^an endpoint that answers 429, 429 and then 200$")]
    public void GivenAnEndpointThatAnswers429Twice() =>
        _context.NewEndpoint().SetStatuses(FakeRoute.ChatCompletions, 429, 429, 200);

    [Given(@"^an endpoint that answers 429 once with Retry-After 2 and then 200 after a 100 ms call$")]
    public void GivenAnEndpointWithARetryAfter() =>
        _context.NewEndpoint().SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests("2"), FakeAnswer.Ok(TimeSpan.FromMilliseconds(100)));

    [Given(@"^a streaming endpoint that answers 429 and then streams normally$")]
    public void GivenAStreamingEndpointThatAnswers429First() =>
        _context.NewEndpoint().SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests(), FakeAnswer.Ok());

    [Given(@"^an endpoint that answers 429 on every call$")]
    public void GivenAnEndpointThatAlwaysAnswers429() =>
        _context.NewEndpoint().SetStatuses(FakeRoute.ChatCompletions, 429);

    [Given(@"^an endpoint that answers 500$")]
    public void GivenAnEndpointThatAnswers500() =>
        _context.NewEndpoint().SetStatuses(FakeRoute.ChatCompletions, 500);

    [Given(@"^a streaming endpoint that yields one update and then fails with 429$")]
    public async Task GivenAStreamingEndpointThatFailsWith429AfterAnUpdate()
    {
        // A status exists only before the body, so the wire cannot carry a 429 after an update. The real SDK's own 429 exception is put after the first
        // real update of a real stream instead: the endpoint answers the stream normally and records its one request.
        ClientResultException rateLimit = await RateLimitFixtures.CaptureRateLimitAsync();
        _ = _context.NewEndpoint();
        _context.Decorate = provider => new FailAfterUpdatesChatClient(provider, updates: 1, rateLimit);
    }

    [When(@"^a metered call is made(?: with a fake clock)?$")]
    public async Task WhenAMeteredCallIsMade()
    {
        MeteredChatClient client = await _context.ClientAsync();
        try
        {
            Task<ChatResponse> call = client.GetResponseAsync(Messages);
            await FakeTimeDriver.RunWaitsToCompletionAsync(call, _context.Time);
        }
        catch (Exception failure) when (failure is not TimeoutException)
        {
            _context.Thrown = failure;
        }
    }

    [When(@"^the stream is consumed$")]
    public async Task WhenTheStreamIsConsumed()
    {
        MeteredChatClient client = await _context.ClientAsync();
        try
        {
            Task<string> run = ConsumeAsync(client);
            await FakeTimeDriver.RunWaitsToCompletionAsync(run, _context.Time);
        }
        catch (Exception failure) when (failure is not TimeoutException)
        {
            _context.Thrown = failure;
        }
    }

    [Then(@"^the reading has retries (\d+) and outcome ""([^""]*)""$")]
    public void ThenTheReadingHasRetriesAndOutcome(int retries, string outcome) => AssertReading(outcome, retries);

    [Then(@"^the reading has outcome ""([^""]*)"" and retries (\d+)$")]
    public void ThenTheReadingHasOutcomeAndRetries(string outcome, int retries) => AssertReading(outcome, retries);

    [Then(@"^the reading has outcome ""([^""]*)""$")]
    public void ThenTheReadingHasOutcome(string outcome) => AssertReading(outcome, retries: null);

    [Then(@"^the sink holds exactly (\d+) readings?$")]
    public void ThenTheSinkHoldsExactlyReadings(int count) => Assert.Equal(count, _context.Sink.Snapshot().Count);

    [Then(@"^waitMs is (\d+)$")]
    public void ThenWaitMsIs(long waitMs) => Assert.Equal(waitMs, _context.SingleReading().WaitMs);

    [Then(@"^latencyMs is (\d+)$")]
    public void ThenLatencyMsIs(double latencyMs) => Assert.Equal(latencyMs, _context.SingleReading().LatencyMs.Value);

    [Then(@"^exactly (\d+) HTTP requests? (?:was|were) recorded$")]
    public void ThenExactlyHttpRequestsWereRecorded(int count) => Assert.Equal(count, _context.ChatRequests);

    private void AssertReading(string outcome, int? retries)
    {
        StepReading reading = _context.SingleReading();
        Assert.Equal(outcome, reading.Outcome);
        if (retries is { } expected)
        {
            Assert.Equal(expected, reading.Retries);
        }

        // The reading and the exception tell the same story: an ok call ended without one and any other outcome reached the caller as one.
        if (outcome == StepOutcomes.Ok)
        {
            Assert.Null(_context.Thrown);
        }
        else
        {
            Assert.NotNull(_context.Thrown);
        }
    }

    private static async Task<string> ConsumeAsync(MeteredChatClient client)
    {
        StringBuilder text = new();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Messages))
        {
            _ = text.Append(update.Text);
        }

        return text.ToString();
    }
}