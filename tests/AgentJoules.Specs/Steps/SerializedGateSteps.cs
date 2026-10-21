using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-046: serialize metered windows with the process-wide meter gate. Scoped to the feature so that sentences it shares in wording with other
/// bindings resolve here only inside this feature. The state lives in <see cref="SerializedGateContext"/>; every delay is applied by the fake chat
/// client on the fake clock, so nothing waits on the wall clock. The first branch is held in flight by a task the step completes, which is what makes
/// the second branch observably wait for the gate; the hang guard is the system clock and only ends a run that already failed.
/// </summary>
[Binding]
[Scope(Feature = "AJ-046 Serialize metered windows with the process-wide meter gate")]
public sealed class SerializedGateSteps(SerializedGateContext context)
{
    private static readonly IReadOnlyList<ChatMessage> Messages = [new ChatMessage(ChatRole.User, "Summarise the report.")];

    // A step that waits on a held call or on the gate must fail, not hang the run, when the code under test leaks (system clock, not the fake one).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly SerializedGateContext _context = context;

    [Given(@"^serialized mode and two metered clients that each take 1 second on a fake clock$")]
    public async Task GivenSerializedModeAndTwoClientsThatEachTakeOneSecond()
    {
        await _context.UseModeAsync(RunMode.Serialized);
        _context.HoldFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.FirstInner = new FakeChatClient(_context.Time) { Hold = _context.HoldFirst.Task, ResponseDelay = TimeSpan.FromSeconds(1) };
        _context.SecondInner = new FakeChatClient(_context.Time) { ResponseDelay = TimeSpan.FromSeconds(1) };
    }

    [Given(@"^serialized mode and a first call that holds the gate for 1 second$")]
    public async Task GivenSerializedModeAndAFirstCallThatHoldsTheGate()
    {
        await _context.UseModeAsync(RunMode.Serialized);
        _context.HoldFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.FirstInner = new FakeChatClient(_context.Time) { Hold = _context.HoldFirst.Task, ResponseDelay = TimeSpan.Zero };
        _context.FirstCall = _context.ClientOver(_context.FirstInner, "First").GetResponseAsync(Messages);

        // The first call runs synchronously up to its hold, so it is inside the gate when this step returns; it holds the gate until the When step releases it.
        Assert.False(_context.FirstCall.IsCompleted, "The first call should still be in flight.");
    }

    [Given(@"^concurrent mode$")]
    public async Task GivenConcurrentMode() => await _context.UseModeAsync(RunMode.Concurrent);

    [Given(@"^serialized mode and a first call that throws$")]
    public async Task GivenSerializedModeAndAFirstCallThatThrows()
    {
        await _context.UseModeAsync(RunMode.Serialized);
        _context.FirstInner = new FakeChatClient(_context.Time) { Failure = new InvalidDataException("The provider broke.") };

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => _context.ClientOver(_context.FirstInner, "First").GetResponseAsync(Messages).WaitAsync(HangGuard));
    }

    [Given(@"^serialized mode and a first call that is cancelled mid-flight$")]
    public async Task GivenSerializedModeAndAFirstCallThatIsCancelledMidFlight()
    {
        await _context.UseModeAsync(RunMode.Serialized);
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.FirstInner = new FakeChatClient(_context.Time) { Hold = never.Task };
        _context.Cancellation = new CancellationTokenSource();
        Task<ChatResponse> first = _context.ClientOver(_context.FirstInner, "First").GetResponseAsync(Messages, cancellationToken: _context.Cancellation.Token);
        Assert.False(first.IsCompleted, "The first call should still be in flight.");

        // The window of the call has a length before the call is cancelled, so it ends with a cancelled reading.
        _context.Time.Advance(TimeSpan.FromMilliseconds(100));
        await _context.Cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(HangGuard));
    }

    [When(@"^both clients are called at the same time$")]
    public async Task WhenBothClientsAreCalledAtTheSameTime()
    {
        FakeChatClient firstInner = _context.FirstInner ?? throw new InvalidOperationException("No first client was created.");
        FakeChatClient secondInner = _context.SecondInner ?? throw new InvalidOperationException("No second client was created.");
        Task<ChatResponse> first = _context.ClientOver(firstInner, "First").GetResponseAsync(Messages);
        Task<ChatResponse> second = _context.ClientOver(secondInner, "Second", isJudge: true).GetResponseAsync(Messages);

        // Only the first branch is inside the gate; the second has not reached its inner client.
        _ = Assert.Single(firstInner.Calls);
        Assert.False(second.IsCompleted, "The second branch should wait for the gate.");
        Assert.Empty(secondInner.Calls);
        (_context.HoldFirst ?? throw new InvalidOperationException("No hold was created.")).SetResult();
        _ = await Task.WhenAll(first, second).WaitAsync(HangGuard);
    }

    [When(@"^a second call waits for the gate and then takes 200 ms$")]
    public async Task WhenASecondCallWaitsForTheGateAndThenTakes200Ms()
    {
        _context.SecondInner = new FakeChatClient(_context.Time) { ResponseDelay = TimeSpan.FromMilliseconds(200) };
        Task<ChatResponse> second = _context.ClientOver(_context.SecondInner, "Second").GetResponseAsync(Messages);
        Assert.False(second.IsCompleted, "The second call should wait for the gate.");
        Assert.Empty(_context.SecondInner.Calls);

        // The second call is queued at the gate now, so this second of the fake clock is a real gate wait; the first call releases only after it.
        _context.Time.Advance(TimeSpan.FromSeconds(1));
        (_context.HoldFirst ?? throw new InvalidOperationException("No hold was created.")).SetResult();
        _ = await Task.WhenAll(_context.FirstCall ?? throw new InvalidOperationException("No first call was started."), second).WaitAsync(HangGuard);
    }

    [When(@"^the gate is resolved from the service collection$")]
    public void WhenTheGateIsResolvedFromTheServiceCollection()
    {
        ServiceProvider provider = _context.Provider ?? throw new InvalidOperationException("The mode of this scenario was not set.");
        _context.Gate = provider.GetRequiredService<IMeterGate>();
    }

    [When(@"^a second call is started after the first failed$")]
    public async Task WhenASecondCallIsStartedAfterTheFirstFailed() => await RunSecondCallAsync();

    [When(@"^a second call is started$")]
    public async Task WhenASecondCallIsStarted() => await RunSecondCallAsync();

    [Then(@"^the two metering windows do not overlap$")]
    public void ThenTheTwoMeteringWindowsDoNotOverlap()
    {
        StepReading first = _context.ReadingOf("First");
        StepReading second = _context.ReadingOf("Second");

        // The strict rule of the run assembler: windows overlap when each starts before the other stops.
        Assert.False(first.StartUtc < second.EndUtc && second.StartUtc < first.EndUtc, "The two windows overlap.");
        Assert.True(second.StartUtc >= first.EndUtc, "The second window should open after the first one closed.");
    }

    [Then(@"^neither reading has flag ""(.*)""$")]
    public void ThenNeitherReadingHasFlag(string flag)
    {
        ReadingFlags parsed = ReadingFlagNames.Parse(flag);
        Assert.False(_context.ReadingOf("First").Flags.HasFlag(parsed), $"The first reading carries the flag {flag}.");
        Assert.False(_context.ReadingOf("Second").Flags.HasFlag(parsed), $"The second reading carries the flag {flag}.");
    }

    [Then(@"^the second reading has latencyMs (\d+)$")]
    public void ThenTheSecondReadingHasLatency(int milliseconds)
    {
        StepReading second = _context.ReadingOf("Second");
        Assert.Equal(Metric.Measured(milliseconds, "ms"), second.LatencyMs);

        // The gate wait is outside the window too: 60 W for 0.2 s is 12 J, not the 72 J of a window that included the second of waiting.
        Assert.Equal(12.0, second.GrossJoules.Value ?? double.NaN, 6);
    }

    [Then(@"^it is the no-op gate$")]
    public void ThenItIsTheNoOpGate() => Assert.Same(NoOpMeterGate.Instance, _context.Gate);

    [Then(@"^the second call completes with outcome ""(.*)""$")]
    public void ThenTheSecondCallCompletesWithOutcome(string outcome)
    {
        Assert.Equal(outcome, _context.ReadingOf("Second").Outcome);
    }

    private async Task RunSecondCallAsync()
    {
        _context.SecondInner = new FakeChatClient(_context.Time);
        _context.SecondCall = _context.ClientOver(_context.SecondInner, "Second").GetResponseAsync(Messages);
        _ = await _context.SecondCall.WaitAsync(HangGuard);
    }
}