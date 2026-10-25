using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.TestSupport;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that reports the due time of every one-shot timer that is created on it (a <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// creates one), so a test can advance the clock by exactly that time when, and only when, the code under test starts to wait. The step driver of
/// <see cref="FakeTimeDriver.RunUntilCompleteAsync"/> advances in fixed steps and so can move the clock past the end of a wait while the code under test is
/// still running; this provider lets a test assert an exact elapsed time (<see cref="FakeTimeDriver.RunWaitsToCompletionAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// Only the creation of a timer is tracked: a later <c>ITimer.Change</c> that re-arms it is not reported, so code under test that re-arms timers is not
/// driven by this provider. The driver that uses it assumes one outstanding timer at a time (a call waits on one delay at a time).
/// </para>
/// <para>
/// A periodic timer (a finite, non-zero period, as a <see cref="PeriodicTimer"/> creates) is recorded in <see cref="History"/> but not reported on
/// <see cref="DueTimes"/>: it drives a background loop (the sampling loop of the meter), not a wait the code under test blocks on, and its ticks fire as the
/// clock crosses them whoever moves it. Reporting it made the driver advance one period at whatever real-time instant it read the report, which could be after
/// the next wait had started: that wait then ended one period early and the last step of its advance landed in the code that followed it (a measurement
/// window one sampling period long, the CI failure of run 39660315559).
/// </para>
/// <para>
/// A timer that backs a <see cref="CancellationTokenSource"/> (its state is the source: <c>new CancellationTokenSource(delay, time)</c>, the time limit of a call such as the warm-up of a local runtime) is a limit and not a wait
/// the code blocks on: it is neither recorded in <see cref="History"/> nor reported on <see cref="DueTimes"/> nor handed to <see cref="TimerCreated"/>, so the driver never advances the clock to a limit that nothing waits for.
/// It still fires when a test advances the clock past it.
/// </para>
/// </remarks>
public sealed class TimerTrackingTimeProvider : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _dueTimes = Channel.CreateUnbounded<TimeSpan>();
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _history = [];

    /// <summary>
    /// The due times of the one-shot timers created since the last read, in order; an infinite due time (a timer that is created stopped) and a periodic timer are
    /// not reported.
    /// </summary>
    public ChannelReader<TimeSpan> DueTimes => _dueTimes.Reader;

    /// <summary>Every due time of a timer created so far that is not created stopped, periodic timers included, in order, whether or not it was read from <see cref="DueTimes"/>.</summary>
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

    /// <summary>A hook called with the due time and the period of every timer that is created and not created stopped (AJ-106: the operation recorder reads the meter start and the baseline wait from it); null for none. Set before the run starts.</summary>
    public Action<TimeSpan, TimeSpan>? TimerCreated { get; set; }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime != Timeout.InfiniteTimeSpan && state is not CancellationTokenSource)
        {
            lock (_gate)
            {
                _history.Add(dueTime);
            }

            // Before the due time is reported, so whatever the hook records comes before anything the driver's next step sets going.
            TimerCreated?.Invoke(dueTime, period);

            if (!IsPeriodic(period))
            {
                _ = _dueTimes.Writer.TryWrite(dueTime);
            }
        }

        return timer;
    }

    // A timer with an infinite or zero period fires once (System.Threading.Timer semantics); any other period re-arms it after every tick.
    private static bool IsPeriodic(TimeSpan period) => period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero;
}