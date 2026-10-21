using System.Diagnostics;

using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-004")]
public sealed class FakeTimeDriverTests
{
    private static async Task FaultAfterDelayAsync(FakeTimeProvider fake)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300), fake);
        throw new InvalidOperationException("scripted fault");
    }

    [Fact]
    public async Task RunUntilCompleteAsync_OneSecondDelay_FinishesInUnder200MsOfRealTime()
    {
        var fake = new FakeTimeProvider();
        var step = TimeSpan.FromMilliseconds(100);
        DateTimeOffset start = fake.GetUtcNow();
        Task delay = Task.Delay(TimeSpan.FromSeconds(1), fake);

        var stopwatch = Stopwatch.StartNew();
        await FakeTimeDriver.RunUntilCompleteAsync(delay, fake, step);
        stopwatch.Stop();

        TimeSpan advanced = fake.GetUtcNow() - start;
        Assert.True(delay.IsCompletedSuccessfully);
        Assert.True(advanced >= TimeSpan.FromSeconds(1), $"fake time advanced only {advanced}");
        Assert.True(advanced <= TimeSpan.FromSeconds(1) + step, $"fake time overshot to {advanced}");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(200), $"took {stopwatch.ElapsedMilliseconds} ms of real time");
    }

    [Fact]
    public async Task RunUntilCompleteAsync_FaultedTask_PropagatesTheFault()
    {
        var fake = new FakeTimeProvider();
        Task faulted = FaultAfterDelayAsync(fake);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FakeTimeDriver.RunUntilCompleteAsync(faulted, fake, TimeSpan.FromMilliseconds(100)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RunUntilCompleteAsync_NonPositiveStep_ThrowsArgumentOutOfRange(int milliseconds)
    {
        var fake = new FakeTimeProvider();
        Task delay = Task.Delay(TimeSpan.FromSeconds(1), fake);
        var step = TimeSpan.FromMilliseconds(milliseconds);

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = FakeTimeDriver.RunUntilCompleteAsync(delay, fake, step); });
    }

    [Fact]
    public async Task RunUntilCompleteAsync_NeverCompletingTask_ThrowsTimeoutExceptionAfterMaxSteps()
    {
        var fake = new FakeTimeProvider();
        Task never = new TaskCompletionSource().Task;
        DateTimeOffset start = fake.GetUtcNow();

        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => FakeTimeDriver.RunUntilCompleteAsync(never, fake, TimeSpan.FromMilliseconds(100), maxSteps: 10));

        Assert.Equal(TimeSpan.FromSeconds(1), fake.GetUtcNow() - start);
        Assert.Contains("00:00:01", thrown.Message, StringComparison.Ordinal);
    }

    // The sampling loop of a measured run is a periodic timer that nothing waits on: the driver used to advance its period at whatever instant it read the report,
    // and when that came after the next wait had started, the wait ended one period early and the rest of its advance landed in the code after it.
    [Fact]
    public async Task RunWaitsToCompletionAsync_PeriodicTimer_IsRecordedButNeverAdvancedFor()
    {
        var fake = new TimerTrackingTimeProvider();
        DateTimeOffset start = fake.GetUtcNow();
        TimeSpan period = TimeSpan.FromMilliseconds(100);
        using var sampling = new PeriodicTimer(period, fake);

        Assert.Equal([period], fake.History);
        Assert.False(fake.DueTimes.TryRead(out _));

        // The wait is the only advance: 2 s in steps of one period, and no period of the sampling timer on top of it, whatever the interleaving.
        Task wait = Task.Delay(TimeSpan.FromSeconds(2), fake);
        await FakeTimeDriver.RunWaitsToCompletionAsync(wait, fake, maxStep: period);

        Assert.Equal(TimeSpan.FromSeconds(2), fake.GetUtcNow() - start);
        Assert.Equal([period, TimeSpan.FromSeconds(2)], fake.History);
        Assert.False(fake.DueTimes.TryRead(out _));

        // The ticks still fired as the wait moved the clock.
        Assert.True(await sampling.WaitForNextTickAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }
}