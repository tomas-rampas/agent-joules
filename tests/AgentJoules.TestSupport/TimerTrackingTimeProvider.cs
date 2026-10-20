using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.TestSupport;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that reports the due time of every timer that is created on it (a <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// creates one), so a test can advance the clock by exactly that time when, and only when, the code under test starts to wait. The step driver of
/// <see cref="FakeTimeDriver.RunUntilCompleteAsync"/> advances in fixed steps and so can move the clock past the end of a wait while the code under test is
/// still running; this provider lets a test assert an exact elapsed time (<see cref="FakeTimeDriver.RunWaitsToCompletionAsync"/>).
/// </summary>
/// <remarks>
/// Only the creation of a timer is tracked: a later <c>ITimer.Change</c> that re-arms it is not reported, so code under test that re-arms timers is not
/// driven by this provider. The driver that uses it assumes one outstanding timer at a time (a call waits on one delay at a time).
/// </remarks>
public sealed class TimerTrackingTimeProvider : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _dueTimes = Channel.CreateUnbounded<TimeSpan>();
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _history = [];

    /// <summary>The due times of the timers created since the last read, in order; an infinite due time (a timer that is created stopped) is not reported.</summary>
    public ChannelReader<TimeSpan> DueTimes => _dueTimes.Reader;

    /// <summary>Every due time reported so far, in order, whether or not it was read from <see cref="DueTimes"/>.</summary>
    public IReadOnlyList<TimeSpan> History
    {
        get
        {
            lock (_gate)
            {
                return [.. _history];
            }
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_gate)
            {
                _history.Add(dueTime);
            }

            _ = _dueTimes.Writer.TryWrite(dueTime);
        }

        return timer;
    }
}