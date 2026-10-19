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
}