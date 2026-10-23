using System.Runtime.InteropServices;

using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;

namespace AgentJoules.Core.Energy;

/// <summary>
/// The energy meter of design 3.2: one background loop samples the GPU at the configured rate into a buffer, and a window is a
/// time slice of that buffer with an exact edge sample at each end.
/// </summary>
/// <remarks>
/// <para>
/// Threading. One lock guards the sample buffer, the counters and the lifecycle flags. Inside that lock a sample takes its
/// timestamp, reads the source and appends, so samples are in timestamp order and the source is never called concurrently. The
/// loop is one private async method started by <see cref="StartAsync"/> and awaited by <see cref="DisposeAsync"/>; no thread is
/// created by the meter. <see cref="StartAsync"/>, <see cref="OpenWindow"/>, <see cref="IMeterWindow.Close"/> and the counters may be
/// called from any thread; a window is closed once, and a second concurrent <see cref="IMeterWindow.Close"/> loses with an
/// <see cref="InvalidOperationException"/>. A window's slice is copied under the lock and its reading is computed after the lock is
/// released, so a window never holds the lock while it calculates and no span over the buffer outlives the lock. The lock is
/// re-entrant, and a time provider may fire a due timer callback on the thread that reads its clock (<c>FakeTimeProvider</c> does),
/// which can resume the loop inline while <see cref="OpenWindow"/> or <see cref="IMeterWindow.Close"/> holds the lock and reads the
/// timestamp. The loop therefore drops a tick that arrives on a thread that already holds the lock: the edge sample of the caller
/// covers that instant, and a nested sample at a later time would otherwise make the caller's own sample look like a clock that
/// went backwards.
/// </para>
/// <para>
/// Timestamps. A sample's time is <see cref="TimeProvider.GetElapsedTime(long)"/> since the meter started, in seconds, never the
/// nominal interval. The buffer is strictly increasing: a sample taken at the same instant as the last one replaces it (one sample
/// per instant, so an edge sample supersedes a tick that landed on the same instant), and a sample earlier than the last one is a
/// clock that went backwards, which throws <see cref="InvalidOperationException"/> from <see cref="OpenWindow"/> and
/// <see cref="IMeterWindow.Close"/> and skips the tick in the loop. The buffer is not trimmed (M1 and M2), so the union of any windows of the run can
/// still be computed from it.
/// </para>
/// <para>
/// Failed reads. A power read that throws <see cref="InvalidOperationException"/> (or returns a value that is not a finite,
/// non-negative number) skips that sample, never records 0 W, and increments <see cref="SkippedReadCount"/>; the loop continues. A
/// throttle read that throws <see cref="InvalidOperationException"/> keeps the power sample with
/// <see cref="ThrottleReasons.Unsupported"/> reasons and increments <see cref="ThrottleReadFailures"/>. Every sample, both window
/// edges included, reads the throttle bits. A counter read that throws or returns nothing leaves that window mark missing, so the
/// window's gross is not-reported with the reason <c>counter-not-read</c> and never 0. <see cref="ObjectDisposedException"/> derives from
/// <see cref="InvalidOperationException"/>; it is caught first, ends the loop, and from <see cref="OpenWindow"/> and
/// <see cref="IMeterWindow.Close"/> reaches the caller.
/// </para>
/// <para>
/// Windows. Opening and closing each take an immediate sample and, under the counter method, an immediate counter read. The
/// counter delta is computed in <see cref="double"/> millijoules and converted to <see cref="long"/> with a range guard; a window
/// whose delta does not fit has no counter delta. A window that closes at the instant it opened has no duration and cannot be
/// computed: <see cref="IMeterWindow.Close"/> throws <see cref="InvalidOperationException"/> naming the zero duration and the window
/// stays open. Overlapping windows each report their own slice. Closing a window after the meter was disposed throws
/// <see cref="ObjectDisposedException"/>. The timer period and the reading's sample rate both come from
/// <see cref="Capabilities"/>, and a meter built from fallback capabilities flags every window.
/// </para>
/// <para>
/// Union. Under the counter method every counter mark a window edge takes, at <see cref="OpenWindow"/> and at <see cref="IMeterWindow.Close"/>, is also
/// recorded with its instant and its edge (open or close) under the lock, a failed read as a mark without a value, so a segment boundary can tell "not
/// read" from "no such edge" (both read <c>counter-not-read</c>) and a segment start never takes the close mark of a window that touches it; the marks are
/// never trimmed either. An edge whose instant is earlier than the last recorded mark is a clock that went backwards and throws
/// <see cref="InvalidOperationException"/> before anything is read, as an edge sample earlier than the last sample does, so the marks stay in time order.
/// <see cref="ComputeUnion"/> checks the windows first, copies the marks or only the samples inside the merged segments and the stored baseline under the
/// lock, and computes with <see cref="UnionWindowCalculator"/> after the lock is released, over that copy in place. It works while the loop runs and after the windows were closed, and also after the
/// loop stopped on a fault, because the samples and marks of windows that were closed before it are still valid; it refuses only a meter that was not
/// started or was disposed.
/// </para>
/// <para>
/// Baseline. <see cref="MeasureBaselineAsync"/> takes an edge sample under the lock (the same path as opening a window), waits the
/// duration on the injected <see cref="TimeProvider"/> without holding the lock while the loop keeps sampling, takes an edge sample at
/// the end, and computes the baseline from the slice from start to stop inside the same locked section that took the stop sample and
/// stores the result; the baseline therefore has both edges even when the duration is shorter than one sampling period. The wait is
/// in whole milliseconds (<see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> truncates), so a duration below 1 ms is
/// rejected and the <c>Seconds</c> of the result is the window actually measured, stop minus start. The meter holds one baseline: the
/// mean of the most recently completed measurement, stored under the lock when that mean is reported and cleared to nothing when it is
/// not (fewer than two samples, or a mean of 0 W or less), so a second measurement replaces the first, overlapping measurements leave
/// the one whose stop sample was taken last, and a failed one never leaves a stale floor behind. Every window closed afterwards passes
/// the stored mean as its baseline, so its net is computed against it; a window closed while none is stored has no net
/// (<c>no-baseline</c>), and a window already closed is unaffected. A measurement that is rejected or cancelled throws and changes
/// nothing stored. The wait also ends when the loop ends for any reason (disposal, a disposed source, a fault): the loop cancels a
/// meter-owned token in a <c>finally</c>, and a wait interrupted that way throws <see cref="ObjectDisposedException"/> after disposal
/// and <see cref="InvalidOperationException"/> ("The sampling loop stopped.", with the fault as inner exception when there is one, and an <see cref="ObjectDisposedException"/> naming the source type when the source was disposed under the meter)
/// otherwise, without waiting for the rest of the duration.
/// </para>
/// <para>
/// Ownership. The meter never disposes the <see cref="IGpuTelemetrySource"/> it was given; the caller that opened it disposes it
/// after the meter, and <see cref="DisposeAsync"/> guarantees that no read happens once it has returned.
/// </para>
/// <para>
/// Live forwarding (REQ-032, design 3.10). With a <see cref="PowerForwarding"/> the loop hands the samples it stored to the live sink as <see cref="PowerEvent"/>s and reads nothing
/// extra: only loop ticks whose sample was stored are forwarded (an edge sample of a window or a baseline, a skipped read and a dropped tick never are, so no event says 0 W for a
/// read that failed). The first stored sample goes, and then one whose gap to the last forwarded one is at least the effective interval, <c>ceil(IntervalMs / period) * period</c>, minus half a
/// period, compared in timestamp ticks; an interval shorter than the period therefore forwards every stored sample. The decision and the timestamp are taken under the lock, and the
/// event is emitted after the lock is released through <see cref="LiveEventSinkExtensions.EmitSafely"/>, so a sink never runs under the meter lock and cannot stop the loop.
/// </para>
/// <para>
/// Loop failure. The loop skips only <see cref="InvalidOperationException"/> (a failed read, a clock that went backwards) and ends on
/// <see cref="ObjectDisposedException"/>. Any other exception from the source (the real binding can raise
/// <see cref="EntryPointNotFoundException"/> or <see cref="BadImageFormatException"/>) ends the loop and faults its task: from then on
/// <see cref="OpenWindow"/> and <see cref="IMeterWindow.Close"/> throw <see cref="InvalidOperationException"/> ("The sampling loop
/// stopped.") with that exception as the inner exception, because a window served from a dead loop would be a two-edge trapezoid
/// labelled as measured. <see cref="LoopFault"/> returns the exception; <see cref="DisposeAsync"/> does not rethrow it, observes it,
/// and is a no-op when called again.
/// </para>
/// <para>
/// Limits. The sampling rate must give a timer period between 1 ms and <c>uint.MaxValue - 1</c> ms (above 1000 Hz or below about
/// 0.0000002 Hz the constructor throws). A power value above <c>uint.MaxValue / 1000.0</c> W, the ceiling of NVML's milliwatt count,
/// is unusable and skipped like a failed read. A counter difference below zero never rounds to 0: it is floored, so it is at most -1
/// and, within the range of a <see cref="long"/>, the reading says <c>counter-went-backwards</c>; a difference below
/// <see cref="long.MinValue"/> millijoules has no delta and reads <c>counter-not-read</c>.
/// </para>
/// <para>
/// Testing with <c>FakeTimeProvider</c> (10.11.0, measured). <c>Advance</c> sets the final time first and then fires every due timer
/// callback inline on the calling thread, so the loop takes each tick before <c>Advance</c> returns: a single 2.0 s advance at 10 Hz
/// makes exactly 21 reads (the start sample and 20 ticks) on the caller's thread, all of the ticks stamped at the final time, and
/// the meter stores one sample for them. That holds for this loop (a continuation without a synchronization context); a different
/// time provider may run the ticks elsewhere, so tests also wait on the fake's read counter before they assert.
/// </para>
/// </remarks>
public sealed class SampledEnergyMeter : IEnergyMeter
{
    // PeriodicTimer accepts periods from 1 ms up to uint.MaxValue - 1 ms.
    private const double MinPeriodMilliseconds = 1.0;
    private const double MaxPeriodMilliseconds = uint.MaxValue - 1.0;

    // Task.Delay waits in whole milliseconds, so this is the shortest baseline duration with a distinct stop instant.
    private static readonly TimeSpan MinBaselineDuration = TimeSpan.FromMilliseconds(1.0);

    // NVML reports power as an unsigned 32-bit milliwatt count, so no real reading exceeds this many watts.
    private const double MaxWatts = uint.MaxValue / 1000.0;

    private const string WattUnit = "W";

    private readonly IGpuTelemetrySource _source;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _period;
    private readonly Lock _gate = new();
    private readonly List<PowerSample> _samples = [];

    // Every counter mark a window edge took under the counter method, in time order (guarded by the lock); empty under the sampling method.
    private readonly List<CounterMark> _counterMarks = [];

    // Cancelled when the sampling loop ends for any reason or the meter is disposed; it ends a pending baseline wait. It is never
    // waited on, so it holds no handle and needs no disposal.
    private readonly CancellationTokenSource _waitInterrupt = new();

    private long _startTimestamp;
    private bool _started;
    private int _disposed;
    private int _skippedReads;
    private int _throttleReadFailures;
    private double? _baselineMeanWatts;
    private PeriodicTimer? _timer;
    private Task _loop = Task.CompletedTask;

    // The live forwarding of stored loop samples (null: none), and the shortest gap between two forwarded samples: the effective interval, ceil(IntervalMs / period) * period, minus half a period,
    // so a sample that lands a little early on a jittery timer still goes. The timestamp of the last forwarded sample is guarded by the lock.
    private readonly PowerForwarding? _forwarding;
    private readonly TimeSpan _forwardGap;
    private long? _lastForwardTimestamp;

    /// <summary>Creates a meter that is not started; it neither samples nor reads the source until <see cref="StartAsync"/>.</summary>
    /// <param name="source">The opened GPU; the caller keeps ownership and disposes it after the meter.</param>
    /// <param name="capabilities">The method, the counter state and the sampling rate; the timer period is derived from the rate.</param>
    /// <param name="timeProvider">The clock for timestamps and for the sampling timer.</param>
    /// <param name="forwarding">
    /// Where the loop forwards the samples it stored for the live dashboard, and how often; null (the default) forwards nothing. Forwarding never reads the source.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="forwarding"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <c>Capabilities.SampleRateHz</c> gives a timer period (1000 / rate milliseconds) that is not finite, shorter than 1 ms (above
    /// 1000 Hz) or longer than a timer can wait (<c>uint.MaxValue - 1</c> ms); nothing is read from the source in that case.
    /// </exception>
    public SampledEnergyMeter(IGpuTelemetrySource source, MeterCapabilities capabilities, TimeProvider timeProvider, PowerForwarding? forwarding = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // Checked in milliseconds before any conversion to a TimeSpan, which would overflow for a tiny rate.
        double periodMilliseconds = 1000.0 / capabilities.SampleRateHz;
        if (!double.IsFinite(periodMilliseconds) || periodMilliseconds < MinPeriodMilliseconds || periodMilliseconds > MaxPeriodMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                capabilities.SampleRateHz,
                "Capabilities.SampleRateHz must give a timer period of at least 1 ms and at most uint.MaxValue - 1 ms (1000 / rate milliseconds).");
        }

        _source = source;
        Capabilities = capabilities;
        _timeProvider = timeProvider;
        _period = TimeSpan.FromMilliseconds(periodMilliseconds);
        _forwarding = forwarding;
        if (forwarding is not null)
        {
            // The effective interval is the smallest whole multiple of the period at or above the interval, in ticks: a sample can only be forwarded on a tick, so 150 ms at
            // 10 Hz forwards every second sample (200 ms), never every one. TimeSpan.FromMilliseconds truncates the period to whole ticks (1000 / 6 ms is 1,666,666), so the
            // division allows one tick of slack per period: an interval that is an exact multiple of such a period takes that many periods, not one more.
            long periodTicks = _period.Ticks;
            long intervalTicks = TimeSpan.FromMilliseconds(forwarding.IntervalMs).Ticks;
            long periods = Math.Max(1L, (intervalTicks + periodTicks) / (periodTicks + 1));
            _forwardGap = TimeSpan.FromTicks((periods * periodTicks) - (periodTicks / 2));
        }
    }

    /// <inheritdoc />
    public MeterCapabilities Capabilities { get; }

    /// <summary>The number of samples in the buffer, taken under the meter lock, so it also waits for a sample in progress.</summary>
    public int SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>The number of samples skipped because a power read failed, the clock went backwards in the loop, or the value was not usable.</summary>
    public int SkippedReadCount
    {
        get
        {
            lock (_gate)
            {
                return _skippedReads;
            }
        }
    }

    /// <summary>
    /// The exception that stopped the sampling loop (an exception of a type the loop does not skip, such as
    /// <see cref="EntryPointNotFoundException"/> from the NVML binding), or null while the loop is running or ended normally. Observing
    /// it here counts as observing the loop's fault, so a caller that reports it at exit leaves no unobserved task exception.
    /// </summary>
    public Exception? LoopFault
    {
        get
        {
            lock (_gate)
            {
                return _loop.IsFaulted ? _loop.Exception?.InnerException : null;
            }
        }
    }

    /// <summary>The number of throttle reads that failed; each of those samples carries <see cref="ThrottleReasons.Unsupported"/>.</summary>
    public int ThrottleReadFailures
    {
        get
        {
            lock (_gate)
            {
                return _throttleReadFailures;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_started)
            {
                throw new InvalidOperationException("The meter was already started.");
            }

            // The timer comes first so that nothing is stored when it cannot be created.
            PeriodicTimer timer = new(_period, _timeProvider);
            try
            {
                _startTimestamp = _timeProvider.GetTimestamp();
                _ = TrySample(ElapsedSeconds());
            }
            catch
            {
                timer.Dispose();
                throw;
            }

            _timer = timer;
            _loop = RunLoopAsync(timer);
            _started = true;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public IMeterWindow OpenWindow()
    {
        lock (_gate)
        {
            ThrowIfNotRunning();
            double seconds = ElapsedSeconds();
            ThrowIfBeforeLastMark(seconds);
            _ = TrySample(seconds);
            double? mark = ReadCounterMark();
            RecordCounterMark(seconds, mark, CounterEdge.Open);
            return new Window(this, seconds, mark);
        }
    }

    /// <inheritdoc />
    public UnionEnergy ComputeUnion(IReadOnlyList<WindowSpan> windows)
    {
        // The windows are checked before the state, so a rejected list leaves the meter untouched (as MeasureBaselineAsync checks its arguments first).
        WindowSpan[] segments = UnionWindowCalculator.Merge(windows);
        PowerSample[] inside = [];
        CounterMark[] marks = [];
        double? baselineMeanWatts;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_started)
            {
                throw new InvalidOperationException("The meter was not started; call StartAsync first.");
            }

            // Only copies while the lock is held; a span over a list would not survive its growth, and the computation runs after the lock is released.
            baselineMeanWatts = _baselineMeanWatts;
            if (Capabilities.Method == EnergyMethod.Counter)
            {
                marks = [.. _counterMarks];
            }
            else
            {
                inside = CopySamplesInside(segments);
            }
        }

        return Capabilities.Method == EnergyMethod.Counter
            ? UnionWindowCalculator.ComputeFromCounterMarks(segments, marks, baselineMeanWatts)
            : UnionWindowCalculator.ComputeFromSamples(segments, inside, baselineMeanWatts);
    }

    /// <inheritdoc />
    public async ValueTask<BaselineResult> MeasureBaselineAsync(
        TimeSpan duration,
        double maxCoefficientOfVariation,
        CancellationToken cancellationToken)
    {
        // Arguments are checked before any sample is taken, so a rejected call leaves the meter and the source untouched.
        // Task.Delay waits in whole milliseconds, so a shorter duration would complete at once with the edges at the same instant.
        if (duration < MinBaselineDuration || duration.TotalMilliseconds > MaxPeriodMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "The baseline duration must be at least 1 ms and no longer than a timer can wait (uint.MaxValue - 1 ms).");
        }

        if (!double.IsFinite(maxCoefficientOfVariation) || maxCoefficientOfVariation <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCoefficientOfVariation),
                maxCoefficientOfVariation,
                "The coefficient-of-variation threshold must be finite and positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        double start;
        CancellationToken interrupt;
        Task loop;
        lock (_gate)
        {
            ThrowIfNotRunning();
            interrupt = _waitInterrupt.Token;
            loop = _loop;
            start = ElapsedSeconds();
            _ = TrySample(start);
        }

        // The wait is on the injected time provider, so a test advances a fake clock and nothing waits on the wall clock. The lock is
        // not held: the sampling loop keeps collecting meanwhile. The loop ending for any reason cancels the interrupt token and ends the
        // wait too.
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interrupt);
        try
        {
            await Task.Delay(duration, _timeProvider, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: the exception must carry the caller's token, not the internal linked one.
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException)
        {
            // The loop ended. Its task completes after the interrupt is cancelled, so wait for it before asking why: a fault is only
            // visible on the finished task.
            await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            lock (_gate)
            {
                ThrowIfNotRunning();
            }

            // Not disposed and not faulted: the loop ended because the source was disposed under the meter. The inner exception names
            // the source type only, so the cause is diagnosable without any device data.
            throw new InvalidOperationException("The sampling loop stopped.", new ObjectDisposedException(_source.GetType().Name));
        }

        lock (_gate)
        {
            // A meter disposed or stopped during the wait refuses here; nothing is stored then.
            ThrowIfNotRunning();
            double stop = ElapsedSeconds();
            if (stop <= start)
            {
                throw new InvalidOperationException("The baseline window has no duration: the clock did not advance or went backwards between its edge samples (zero or negative duration).");
            }

            _ = TrySample(stop);

            // Computed and stored inside the lock that took the stop sample, over a span of the buffer that cannot change meanwhile, so
            // the stored baseline is always that of the measurement whose stop sample was taken last. One baseline per meter: this
            // replaces the previous one, and a baseline whose mean is not reported clears it, so a later window's net is not reported
            // instead of being computed against a floor that was not measured.
            int low = LowerBound(start);
            int high = UpperBound(stop);
            BaselineResult baseline = BaselineCalculator.Compute(
                CollectionsMarshal.AsSpan(_samples)[low..high],
                start,
                stop,
                maxCoefficientOfVariation);
            _baselineMeanWatts = baseline.MeanW.Value;
            return baseline;
        }
    }

    /// <summary>
    /// Stops the sampling loop and waits for it; safe to call twice and concurrently. No source read happens after it returns. The
    /// source stays open: its owner disposes it.
    /// </summary>
    /// <returns>A task that completes when the loop has ended.</returns>
    public async ValueTask DisposeAsync()
    {
        _ = Interlocked.Exchange(ref _disposed, 1);

        // StartAsync checks the flag under this lock, so it either ran before this point and the snapshot sees its timer and loop,
        // or it runs after and fails.
        PeriodicTimer? timer;
        Task loop;
        lock (_gate)
        {
            timer = _timer;
            loop = _loop;
        }

        // Disposing the timer completes the pending WaitForNextTickAsync with false, so the loop ends by itself. A loop that already
        // faulted is not rethrown here (LoopFault reports it); reading Exception observes it, so no unobserved fault remains, and a
        // second call finds a completed task and does nothing.
        // The interrupt is cancelled here too, so a pending baseline wait ends even when the loop was never started.
        timer?.Dispose();
        _waitInterrupt.Cancel();
        await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _ = loop.Exception;
    }

    private async Task RunLoopAsync(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                // A tick delivered inside the meter's own locked section is dropped: the caller's edge sample covers that instant.
                if (_gate.IsHeldByCurrentThread)
                {
                    continue;
                }

                PowerSample? forward = null;
                lock (_gate)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    try
                    {
                        long timestamp = _timeProvider.GetTimestamp();
                        if (TrySample(_timeProvider.GetElapsedTime(_startTimestamp, timestamp).TotalSeconds) && ShouldForward(timestamp))
                        {
                            _lastForwardTimestamp = timestamp;
                            forward = _samples[^1];
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // The source was disposed under the meter: nothing more can be read.
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                        // The clock went backwards; the tick is skipped like a failed read.
                        _skippedReads++;
                    }
                }

                // Emitted after the lock is released, so a live sink never runs under the meter lock and never touches a window; EmitSafely drops whatever the sink throws.
                if (forward is { } sample)
                {
                    _forwarding!.Sink.EmitSafely(new PowerEvent(_timeProvider.GetUtcNow(), false, Metric.Measured(sample.Watts, WattUnit), sample.Seconds));
                }
            }
        }
        finally
        {
            // However the loop ends (disposal, a disposed source, a fault), a pending baseline wait must not run on to its full
            // duration. The lock is not held here, so a continuation that runs inline can take it.
            _waitInterrupt.Cancel();
        }
    }

    // Whether the loop sample stored at the timestamp is forwarded: the first one is, then one whose gap to the last forwarded sample reaches the forwarding gap. The caller holds the lock.
    private bool ShouldForward(long timestamp) =>
        _forwarding is not null
        && (_lastForwardTimestamp is not { } last || _timeProvider.GetElapsedTime(last, timestamp) >= _forwardGap);

    // The caller holds the lock.
    private void ThrowIfNotRunning()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_started)
        {
            throw new InvalidOperationException("The meter was not started; call StartAsync first.");
        }

        // A loop that stopped on an unexpected exception would leave later windows with only their two edge samples, labelled as if
        // they were sampled, so no new window is served from a dead loop.
        if (_loop.IsFaulted)
        {
            throw new InvalidOperationException("The sampling loop stopped.", _loop.Exception?.InnerException);
        }
    }

    // The caller holds the lock.
    private double ElapsedSeconds() => _timeProvider.GetElapsedTime(_startTimestamp).TotalSeconds;

    // Takes one sample at the given instant; the caller holds the lock and took the timestamp inside it. Returns whether a sample
    // was stored. Throws InvalidOperationException before any read when the clock went backwards.
    private bool TrySample(double seconds)
    {
        int count = _samples.Count;
        if (count > 0 && seconds < _samples[count - 1].Seconds)
        {
            throw new InvalidOperationException("The clock went backwards: a sample cannot be earlier than the previous one.");
        }

        double watts;
        try
        {
            watts = _source.ReadPowerWatts();
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            _skippedReads++;
            return false;
        }

        if (!double.IsFinite(watts) || watts < 0.0 || watts > MaxWatts)
        {
            _skippedReads++;
            return false;
        }

        ThrottleReasons reasons;
        try
        {
            reasons = _source.ReadThrottleReasons();
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            _throttleReadFailures++;
            reasons = ThrottleReasons.Unsupported;
        }

        PowerSample sample = new(seconds, watts, reasons);
        if (count > 0 && seconds == _samples[count - 1].Seconds)
        {
            _samples[count - 1] = sample;
        }
        else
        {
            _samples.Add(sample);
        }

        return true;
    }

    // The caller holds the lock. Null when the method is sampling or the counter did not give a usable value.
    private double? ReadCounterMark()
    {
        if (Capabilities.Method != EnergyMethod.Counter)
        {
            return null;
        }

        try
        {
            double? mark = _source.ReadEnergyMillijoules();
            return mark is { } value && double.IsFinite(value) ? value : null;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // A counter that went backwards must never round to 0 and read as an idle window: any negative difference becomes at most -1. The rule is shared with
    // the segments of a union, and a segment reads the open mark at its start and the close mark at its stop, so a window and the union of that one window
    // give the same delta and two touching windows unite to the sum of their deltas.
    private static long? CounterDelta(double? openMark, double? closeMark) => UnionWindowCalculator.CounterDeltaOf(openMark, closeMark);

    // An edge earlier than the last counter mark is a clock that went backwards, refused like a sample earlier than the last sample, before anything is read; the
    // last sample alone cannot catch it when the edge sample at that mark was a failed read. Without the check the marks would leave time order and every later
    // union would be refused. The caller holds the lock.
    private void ThrowIfBeforeLastMark(double seconds)
    {
        if (_counterMarks.Count > 0 && seconds < _counterMarks[^1].Seconds)
        {
            throw new InvalidOperationException("The clock went backwards: a window edge cannot be earlier than the previous counter mark.");
        }
    }

    // The caller holds the lock and took the instant inside it, after the edge sample, so the marks stay in time order. Only the counter method takes marks.
    private void RecordCounterMark(double seconds, double? mark, CounterEdge edge)
    {
        if (Capabilities.Method == EnergyMethod.Counter)
        {
            _counterMarks.Add(new CounterMark(seconds, mark, edge));
        }
    }

    // The samples inside the merged segments (both edges of every segment included), in time order, copied in one array; a sample at the instant where
    // two touching segments meet is copied once. The samples between the segments are not copied. The caller holds the lock.
    private PowerSample[] CopySamplesInside(WindowSpan[] segments)
    {
        var ranges = new (int Low, int High)[segments.Length];
        int count = 0;
        int previousHigh = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            int low = Math.Max(LowerBound(segments[i].StartSeconds), previousHigh);
            int high = Math.Max(UpperBound(segments[i].StopSeconds), low);
            ranges[i] = (low, high);
            count += high - low;
            previousHigh = high;
        }

        var copy = new PowerSample[count];
        ReadOnlySpan<PowerSample> buffer = CollectionsMarshal.AsSpan(_samples);
        int next = 0;
        foreach ((int low, int high) in ranges)
        {
            buffer[low..high].CopyTo(copy.AsSpan(next));
            next += high - low;
        }

        return copy;
    }

    // Index of the first sample at or after the given time. The caller holds the lock.
    private int LowerBound(double seconds)
    {
        int low = 0;
        int high = _samples.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (_samples[mid].Seconds < seconds)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    // Index of the first sample after the given time. The caller holds the lock.
    private int UpperBound(double seconds)
    {
        int low = 0;
        int high = _samples.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (_samples[mid].Seconds <= seconds)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private EnergyReading CloseWindow(Window window)
    {
        PowerSample[] slice;
        double stop;
        double? closeMark;
        double? baselineMeanWatts;
        lock (_gate)
        {
            if (window.IsClosed)
            {
                throw new InvalidOperationException("The window was already closed.");
            }

            ThrowIfNotRunning();
            stop = ElapsedSeconds();
            if (stop < window.StartSeconds)
            {
                throw new InvalidOperationException("The clock went backwards: the window would end before it started.");
            }

            if (stop == window.StartSeconds)
            {
                throw new InvalidOperationException("The window has no duration: it was closed at the same instant it was opened (zero duration).");
            }

            ThrowIfBeforeLastMark(stop);
            _ = TrySample(stop);
            closeMark = ReadCounterMark();
            RecordCounterMark(stop, closeMark, CounterEdge.Close);
            window.MarkClosed(stop);
            baselineMeanWatts = _baselineMeanWatts;

            // The slice is copied while the lock is held; a span over the list would not survive its growth.
            int low = LowerBound(window.StartSeconds);
            int high = UpperBound(stop);
            slice = [.. CollectionsMarshal.AsSpan(_samples)[low..high]];
        }

        return ReadingCalculator.Compute(new ReadingInput(
            Samples: slice,
            WindowStartSeconds: window.StartSeconds,
            WindowStopSeconds: stop,
            Method: Capabilities.Method,
            CounterDeltaMillijoules: CounterDelta(window.OpenMark, closeMark),
            BaselineMeanWatts: baselineMeanWatts,
            SampleRateHz: Capabilities.SampleRateHz,
            IsSamplingFallback: Capabilities.IsFallback));
    }

    private sealed class Window(SampledEnergyMeter owner, double startSeconds, double? openMark) : IMeterWindow
    {
        // Guarded by the owner's lock.
        private double _stopSeconds = startSeconds;

        public double StartSeconds { get; } = startSeconds;

        public double? OpenMark { get; } = openMark;

        public bool IsClosed { get; private set; }

        public WindowSpan Span
        {
            get
            {
                lock (owner._gate)
                {
                    return new WindowSpan(StartSeconds, _stopSeconds);
                }
            }
        }

        public EnergyReading Close() => owner.CloseWindow(this);

        public void MarkClosed(double stopSeconds)
        {
            _stopSeconds = stopSeconds;
            IsClosed = true;
        }
    }
}