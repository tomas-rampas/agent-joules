using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-041: the metered chat client with exactly one reading per call. Scoped to the feature so that sentences it shares in wording with
/// other bindings ("the stream is consumed", "the reading has ...") resolve here only inside this feature. The state lives in
/// <see cref="MeteredCallsContext"/>; every delay is applied by the fake chat client on the fake clock, so nothing waits on the wall clock.
/// </summary>
[Binding]
[Scope(Feature = "AJ-041 Meter buffered and streaming calls with exactly one reading per call")]
public sealed class MeteredCallsSteps(MeteredCallsContext context)
{
    private static readonly IReadOnlyList<ChatMessage> Messages = [new ChatMessage(ChatRole.User, "Summarise the report.")];

    // A step that waits on a held call must fail, not hang the run, when the code under test leaks (system clock, not the fake one).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly MeteredCallsContext _context = context;

    private IChatClient Client => _context.Client ?? throw new InvalidOperationException("No metered client was created.");

    [Given(@"^a metered client for agent ""(.*)"" over a fake client that answers in one response$")]
    public async Task GivenAMeteredClientOverABufferedFake(string agent)
    {
        _context.Inner = new FakeChatClient(_context.Time) { ResponseText = "The report says energy matters." };
        _context.Client = new MeteredChatClient(_context.Inner, MeteredCallsContext.IdentityOf(agent), await _context.CreateServicesAsync());
    }

    [Given(@"^a metered client for agent ""(.*)"" over a fake stream whose first update arrives after (\d+) ms$")]
    public async Task GivenAMeteredClientOverAStreamWithAFirstUpdateDelay(string agent, int milliseconds)
    {
        _context.Inner = new FakeChatClient(_context.Time)
        {
            Updates =
            [
                new FakeUpdate("The report ", TimeSpan.FromMilliseconds(milliseconds)),
                new FakeUpdate("says ", TimeSpan.FromMilliseconds(100)),
                new FakeUpdate("energy matters.", TimeSpan.FromMilliseconds(100)),
            ],
        };
        _context.Client = new MeteredChatClient(_context.Inner, MeteredCallsContext.IdentityOf(agent), await _context.CreateServicesAsync());
    }

    [Given(@"^a fake inner client$")]
    public void GivenAFakeInnerClient() => _context.Inner = new FakeChatClient(_context.Time);

    [Given(@"^a metered client over a fake stream with (\d+) updates$")]
    public async Task GivenAMeteredClientOverAStreamWithUpdates(int count)
    {
        _context.Inner = new FakeChatClient(_context.Time)
        {
            Updates = [.. Enumerable.Range(0, count).Select(static i => new FakeUpdate($"part {i} ", TimeSpan.FromMilliseconds(50)))],
        };
        _context.Client = new MeteredChatClient(_context.Inner, MeteredCallsContext.IdentityOf("Planner"), await _context.CreateServicesAsync());
    }

    [Given(@"^a metered client over a fake stream that throws after the first update$")]
    public async Task GivenAMeteredClientOverAFailingStream()
    {
        _context.Inner = new FakeChatClient(_context.Time)
        {
            Updates = [new FakeUpdate("part 0 ", TimeSpan.FromMilliseconds(50)), new FakeUpdate("part 1 ", TimeSpan.FromMilliseconds(50))],
            FailAfterUpdates = 1,
            Failure = new InvalidDataException("The stream broke."),
        };
        _context.Client = new MeteredChatClient(_context.Inner, MeteredCallsContext.IdentityOf("Planner"), await _context.CreateServicesAsync());
    }

    [Given(@"^a metered client with a call in flight$")]
    public async Task GivenAMeteredClientWithACallInFlight()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Release = release;
        _context.Inner = new FakeChatClient(_context.Time) { Hold = release.Task };
        _context.Client = new MeteredChatClient(_context.Inner, MeteredCallsContext.IdentityOf("Planner"), await _context.CreateServicesAsync());

        // The call runs synchronously up to the hold of the fake, so the client is in flight when this step returns.
        _context.FirstCall = _context.Client.GetResponseAsync(Messages);
        Assert.False(_context.FirstCall.IsCompleted, "The first call should still be in flight.");
    }

    [When(@"^the client is called with GetResponseAsync$")]
    public async Task WhenTheClientIsCalledWithGetResponseAsync()
    {
        ChatResponse response = await Client.GetResponseAsync(Messages).WaitAsync(HangGuard);
        Assert.Equal("The report says energy matters.", response.Text);
    }

    [When(@"^the stream is consumed to completion$")]
    public async Task WhenTheStreamIsConsumedToCompletion()
    {
        int count = 0;
        await foreach (ChatResponseUpdate update in Client.GetStreamingResponseAsync(Messages))
        {
            Assert.NotNull(update);
            count++;
        }

        Assert.Equal(3, count);
    }

    [When(@"^the decorator is added with ChatClientBuilder\.Use and built$")]
    public async Task WhenTheDecoratorIsAddedWithChatClientBuilderUseAndBuilt()
    {
        MeteringServices services = await _context.CreateServicesAsync();
        StepIdentity identity = MeteredCallsContext.IdentityOf("Planner");
        _context.Client = new ChatClientBuilder(_context.Inner ?? throw new InvalidOperationException("No inner client was created."))
            .Use(inner => new MeteredChatClient(inner, identity, services))
            .Build();
    }

    [When(@"^the caller cancels after the second update$")]
    public async Task WhenTheCallerCancelsAfterTheSecondUpdate()
    {
        _context.Cancellation = new CancellationTokenSource();
        int seen = 0;
        try
        {
            await foreach (ChatResponseUpdate update in Client.GetStreamingResponseAsync(Messages, cancellationToken: _context.Cancellation.Token))
            {
                Assert.NotNull(update);
                seen++;
                if (seen == 2)
                {
                    await _context.Cancellation.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException cancelled)
        {
            _context.Thrown = cancelled;
        }

        Assert.Equal(2, seen);
    }

    [When(@"^the stream is consumed$")]
    public async Task WhenTheStreamIsConsumed()
    {
        try
        {
            await foreach (ChatResponseUpdate update in Client.GetStreamingResponseAsync(Messages))
            {
                Assert.NotNull(update);
            }
        }
        catch (InvalidDataException failure)
        {
            _context.Thrown = failure;
        }
    }

    [When(@"^a second call starts on the same instance$")]
    public async Task WhenASecondCallStartsOnTheSameInstance()
    {
        try
        {
            _ = await Client.GetResponseAsync(Messages).WaitAsync(HangGuard);
        }
        catch (InvalidOperationException rejected)
        {
            _context.Thrown = rejected;
        }
    }

    [Then(@"^the sink holds exactly (\d+) readings? for agent ""(.*)""$")]
    public void ThenTheSinkHoldsExactlyReadingsForAgent(int count, string agent)
    {
        IReadOnlyList<StepReading> readings = _context.Readings();
        Assert.Equal(count, readings.Count);
        Assert.All(readings, reading => Assert.Equal(agent, reading.Agent));
    }

    [Then(@"^the sink holds exactly (\d+) readings? with outcome ""(.*)""$")]
    public void ThenTheSinkHoldsExactlyReadingsWithOutcome(int count, string outcome)
    {
        IReadOnlyList<StepReading> readings = _context.Readings();
        Assert.Equal(count, readings.Count);
        Assert.All(readings, reading => Assert.Equal(outcome, reading.Outcome));

        // A cancelled call reaches its caller as the cancellation the caller asked for.
        if (outcome == StepOutcomes.Cancelled)
        {
            _ = Assert.IsType<OperationCanceledException>(_context.Thrown, exactMatch: false);
        }
    }

    [Then(@"^the reading has ttftMs label ""(.*)""$")]
    public void ThenTheReadingHasTtftMsLabel(string label)
    {
        StepReading reading = Assert.Single(_context.Readings());
        MetricLabel expected = label switch
        {
            "N/A" => MetricLabel.NotApplicable,
            "measured" => MetricLabel.Measured,
            _ => throw new ArgumentOutOfRangeException(nameof(label), label, "The feature names a label this step does not know."),
        };
        Assert.Equal(expected, reading.TtftMs.Label);
        if (expected == MetricLabel.NotApplicable)
        {
            Assert.Equal("buffered-call", reading.TtftMs.Reason);
            Assert.Null(reading.TtftMs.Value);
        }
        else
        {
            Assert.Null(reading.TtftMs.Reason);
            Assert.NotNull(reading.TtftMs.Value);
            Assert.Equal("ms", reading.TtftMs.Unit);
        }
    }

    [Then(@"^ttftMs is within (\d+) ms of (\d+)$")]
    public void ThenTtftMsIsWithinMsOf(int tolerance, int expected)
    {
        StepReading reading = Assert.Single(_context.Readings());
        Assert.Equal(MetricLabel.Measured, reading.TtftMs.Label);
        Assert.Equal("ms", reading.TtftMs.Unit);
        Assert.InRange(reading.TtftMs.Value ?? double.NaN, expected - tolerance, expected + tolerance);
    }

    [Then(@"^calling the built client publishes (\d+) readings?$")]
    public async Task ThenCallingTheBuiltClientPublishesReadings(int count)
    {
        _ = await Client.GetResponseAsync(Messages).WaitAsync(HangGuard);
        Assert.Equal(count, _context.Readings().Count);
    }

    [Then(@"^the exception reaches the caller$")]
    public void ThenTheExceptionReachesTheCaller()
    {
        InvalidDataException failure = Assert.IsType<InvalidDataException>(_context.Thrown);
        Assert.Equal("The stream broke.", failure.Message);
    }

    [Then(@"^InvalidOperationException is thrown$")]
    public async Task ThenInvalidOperationExceptionIsThrown()
    {
        Assert.IsType<InvalidOperationException>(_context.Thrown);

        // Cleanup that doubles as a check: the call in flight is not disturbed by the rejected one and still ends with its one reading.
        _context.Release?.SetResult();
        _ = await (_context.FirstCall ?? throw new InvalidOperationException("No call was in flight.")).WaitAsync(HangGuard);
        _ = Assert.Single(_context.Readings());
    }
}