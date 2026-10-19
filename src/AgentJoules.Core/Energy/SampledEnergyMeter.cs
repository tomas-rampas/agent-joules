using System.Runtime.InteropServices;

using AgentJoules.Core.Gpu;

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
/// <see cref="IMeterWindow.Close"/> and skips the tick in the loop. The buffer is not trimmed in M1.
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
/// Ownership. The meter never disposes the <see cref="IGpuTelemetrySource"/> it was given; the caller that opened it disposes it
/// after the meter, and <see cref="DisposeAsync"/> guarantees that no read happens once it has returned.
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

    // NVML reports power as an unsigned 32-bit milliwatt count, so no real reading exceeds this many watts.
    private const double MaxWatts = uint.MaxValue / 1000.0;

    // 2^63: the first double above every long.
    private const double LongUpperBound = 9223372036854775808.0;

    private readonly IGpuTelemetrySource _source;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _period;
    private readonly Lock _gate = new();
    private readonly List<PowerSample> _samples = [];

    private long _startTimestamp;
    private bool _started;
    private int _disposed;
    private int _skippedReads;
    private int _throttleReadFailures;
    private PeriodicTimer? _timer;
    private Task _loop = Task.CompletedTask;

    /// <summary>Creates a meter that is not started; it neither samples nor reads the source until <see cref="StartAsync"/>.</summary>
    /// <param name="source">The opened GPU; the caller keeps ownership and disposes it after the meter.</param>
    /// <param name="capabilities">The method, the counter state and the sampling rate; the timer period is derived from the rate.</param>
    /// <param name="timeProvider">The clock for timestamps and for the sampling timer.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <c>Capabilities.SampleRateHz</c> gives a timer period (1000 / rate milliseconds) that is not finite, shorter than 1 ms (above
    /// 1000 Hz) or longer than a timer can wait (<c>uint.MaxValue - 1</c> ms); nothing is read from the source in that case.
    /// </exception>
    public SampledEnergyMeter(IGpuTelemetrySource source, MeterCapabilities capabilities, TimeProvider timeProvider)
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
            _ = TrySample(seconds);
            double? mark = ReadCounterMark();
            return new Window(this, seconds, mark);
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
        timer?.Dispose();
        await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _ = loop.Exception;
    }

    private async Task RunLoopAsync(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            // A tick delivered inside the meter's own locked section is dropped: the caller's edge sample covers that instant.
            if (_gate.IsHeldByCurrentThread)
            {
                continue;
            }

            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                try
                {
                    _ = TrySample(ElapsedSeconds());
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
        }
    }

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

    private static long? CounterDelta(double? openMark, double? closeMark)
    {
        if (openMark is not { } open || closeMark is not { } close)
        {
            return null;
        }

        // A counter that went backwards must never round to 0 and read as an idle window: any negative difference becomes at most -1.
        double difference = close - open;
        double delta = difference < 0.0 ? Math.Floor(difference) : Math.Round(difference);
        return double.IsFinite(delta) && delta >= long.MinValue && delta < LongUpperBound ? (long)delta : null;
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

            _ = TrySample(stop);
            closeMark = ReadCounterMark();
            window.MarkClosed(stop);

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
            BaselineMeanWatts: null,
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