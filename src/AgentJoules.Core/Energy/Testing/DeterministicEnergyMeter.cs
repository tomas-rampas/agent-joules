using System.Globalization;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Gpu;

namespace AgentJoules.Core.Energy.Testing;

/// <summary>
/// An <see cref="IEnergyMeter"/> that computes every reading from a power function and a fake clock, with no background work, so tests of
/// later milestones get exact joules with no timing noise (REQ-005, design 3.2).
/// </summary>
/// <remarks>
/// <para>
/// A test double. It exists for the tests of the middleware, the reports and the dashboard; production code never builds it. It starts no
/// thread and no timer and holds no handle (only a private lock around its small state): nothing happens between calls, and time moves
/// only when the <see cref="TimeProvider"/> given to the constructor is advanced (a <c>FakeTimeProvider</c> in tests). Its
/// <see cref="Capabilities"/> come from <see cref="MeterMethodSelector"/> and every reading from <see cref="ReadingCalculator"/>, the same
/// two pieces the real <see cref="SampledEnergyMeter"/> uses, so a reading of the fake and a reading of the real meter over the same
/// power differ only in what produced the samples.
/// </para>
/// <para>
/// The grid rule. <see cref="StartAsync"/> records time zero. A window that opens at <c>a</c> seconds and closes at <c>b</c> seconds
/// (both read from the clock, in seconds since time zero) gets one synthesized sample at <c>a</c>, one at every grid instant
/// <c>k / sampleRateHz</c> (k a whole number) with <c>a &lt; k / sampleRateHz &lt; b</c>, and one at <c>b</c>, in increasing time order. An
/// edge that lies on the grid (within 1e-9 of a sampling period, and always when the grid instant is not strictly inside the window in
/// double arithmetic) is one sample, never two, so a 300 ms window at 10 Hz has the four samples 0, 0.1, 0.2 and 0.3 s, a 150 ms
/// window has 0, 0.1 and 0.15 s, and the 3 s reference window has 31. The number of samples of the last closed window is
/// <see cref="LastWindowSampleCount"/> and equals the <c>SampleCount</c> of its reading.
/// </para>
/// <para>
/// The power function is called exactly once per synthesized sample, in time order, with the sample's time in seconds since time zero;
/// it is never called outside <see cref="IMeterWindow.Close"/> and <see cref="MeasureBaselineAsync"/>. It runs under the meter's lock,
/// so it must not call the meter or wait on a thread that does. It must return a finite power from 0 W to 4,294,967.295 W (the NVML
/// milliwatt ceiling the real meter also applies); anything else throws <see cref="InvalidOperationException"/> naming the instant,
/// where the real meter would skip the read. Every sample has the throttle value <c>supported, nothing active</c>: the fake never flags
/// <c>throttled</c> and its readings carry an empty <c>throttleReasons</c> list.
/// </para>
/// <para>
/// Exactness. With the counter method the gross energy is the trapezoid integral of the window's samples in millijoules, rounded to a
/// whole number, as a real counter delta is; it is therefore exact up to whole-millijoule rounding (a constant 60 W over 3 s is exactly
/// 180000 mJ, 180 J, and any power that is linear between the grid instants is exact to the millijoule). With the sampling method the
/// gross energy is the same integral computed by <see cref="PowerIntegrator"/> in joules, exact for a constant power to within double
/// rounding (1e-9 J).
/// </para>
/// <para>
/// The baseline rule. <see cref="MeasureBaselineAsync"/> returns a task that is already complete and never waits on or advances the
/// clock. It synthesizes the samples of the virtual interval from now to now plus <c>duration</c> (whole milliseconds, as the real meter
/// waits) by the grid rule (both edges included), computes the statistics with <see cref="BaselineCalculator"/> and keeps the mean as
/// the baseline of every window closed afterwards. A mean that is not reported clears the stored baseline, and the constructor's
/// <c>baselineMeanWatts</c> is replaced by the first measurement. Because the clock does not move, a test that needs a different power
/// while measuring and while running a window gives the power function a break (for example <c>t =&gt; t &lt;= 30 ? 20 : 60</c>) and
/// advances the clock past it before the window.
/// </para>
/// <para>
/// The sample cap. A window or a baseline interval that needs more than 1,000,000 samples (27.8 hours at 10 Hz, 32 MB) is refused, so a
/// mistake in a test cannot exhaust memory: exactly 1,000,000 samples are accepted. <see cref="IMeterWindow.Close"/> throws
/// <see cref="InvalidOperationException"/> and <see cref="MeasureBaselineAsync"/> faults with <see cref="ArgumentOutOfRangeException"/>
/// for <c>duration</c>; a baseline is refused from its length alone, before any sample is taken, and may be refused when its exact
/// count would have fitted by one sample.
/// </para>
/// <para>
/// Differences from <see cref="SampledEnergyMeter"/>: the fake reads no source, so it cannot skip a read or lose a sampling loop, and
/// its baseline completes at once instead of waiting. An exception thrown by the power function itself leaves the call that evaluated it
/// unchanged: <see cref="IMeterWindow.Close"/> throws it and the window stays open. <see cref="MeasureBaselineAsync"/> carries an
/// <see cref="ArgumentException"/> or <see cref="InvalidOperationException"/> from the function in its returned task, and an
/// <see cref="OperationCanceledException"/> from it as a faulted task of that exact type while the caller's token is cancelled; every
/// other exception from the function (and an <see cref="OperationCanceledException"/> while the token is not cancelled) leaves the call.
/// </para>
/// </remarks>
public sealed class DeterministicEnergyMeter : IEnergyMeter
{
    // An edge within this many sampling periods of a grid instant counts as on the grid, so 0.3 s at 10 Hz is not 3.0000000000000004 periods.
    private const double GridEpsilon = 1e-9;

    // A test double for tests: a window that would need more samples than this is a mistake, not a measurement (32 MB of samples).
    private const double MaxSamples = 1_000_000.0;

    // PeriodicTimer accepts periods from 1 ms up to uint.MaxValue - 1 ms; the fake applies the real meter's rate and duration limits.
    private const double MinPeriodMilliseconds = 1.0;
    private const double MaxPeriodMilliseconds = uint.MaxValue - 1.0;

    // NVML reports power as an unsigned 32-bit milliwatt count, so no real reading exceeds this many watts.
    private const double MaxWatts = uint.MaxValue / 1000.0;

    // 2^63: the first double above every long.
    private const double LongUpperBound = 9223372036854775808.0;

    private const string CounterOffReason = "counter-not-supported: switched off in the deterministic meter";

    private static readonly TimeSpan MinBaselineDuration = TimeSpan.FromMilliseconds(1.0);

    // The value of a read that was supported and had nothing active.
    private static readonly ThrottleReasons Quiet = new(0UL, true);

    private readonly TimeProvider _timeProvider;
    private readonly Func<double, double> _wattsAt;
    private readonly Lock _gate = new();

    private long _startTimestamp;
    private bool _started;
    private bool _disposed;
    private double? _baselineMeanWatts;

    /// <summary>Creates a meter that is not started.</summary>
    /// <param name="timeProvider">The clock; the meter never waits on it, so a test advances it directly.</param>
    /// <param name="wattsAt">
    /// The power in watts at a time in seconds since <see cref="StartAsync"/>; called once per synthesized sample, returning a finite
    /// value from 0 to 4,294,967.295 W. It runs under the meter's lock, so it must not call the meter or wait on a thread that does.
    /// </param>
    /// <param name="sampleRateHz">
    /// The nominal sampling rate in hertz; positive and finite, with a period (1000 / rate milliseconds) from 1 ms to <c>uint.MaxValue - 1</c> ms
    /// like the real meter. It sets the grid and the <c>low-resolution</c> threshold.
    /// </param>
    /// <param name="counterSupported">True when the simulated GPU has an energy counter.</param>
    /// <param name="method">The parsed <c>Meter:Method</c>; <see cref="MeterMethodSetting.Auto"/> with the counter off is a sampling fallback.</param>
    /// <param name="baselineMeanWatts">The idle baseline in watts that windows subtract until a measurement replaces it; <c>null</c> for none, otherwise finite and positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> or <paramref name="wattsAt"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sampleRateHz"/> is not positive and finite or gives a period outside 1 ms to <c>uint.MaxValue - 1</c> ms,
    /// <paramref name="baselineMeanWatts"/> is not null and not positive and finite, or <paramref name="method"/> is not a defined value.
    /// </exception>
    /// <exception cref="MeasurementPreconditionException"><paramref name="method"/> is <see cref="MeterMethodSetting.Counter"/> and <paramref name="counterSupported"/> is false.</exception>
    public DeterministicEnergyMeter(
        TimeProvider timeProvider,
        Func<double, double> wattsAt,
        double sampleRateHz,
        bool counterSupported,
        MeterMethodSetting method,
        double? baselineMeanWatts = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(wattsAt);

        // Checked here, before the selector, so that a bad rate or method is an argument error and not a configuration error.
        double periodMilliseconds = 1000.0 / sampleRateHz;
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0 || periodMilliseconds < MinPeriodMilliseconds || periodMilliseconds > MaxPeriodMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRateHz),
                sampleRateHz,
                "The sample rate must be positive and finite and give a period (1000 / rate milliseconds) of at least 1 ms and at most uint.MaxValue - 1 ms.");
        }

        if (baselineMeanWatts is { } baseline && (!double.IsFinite(baseline) || baseline <= 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(baselineMeanWatts), baselineMeanWatts, "The baseline mean must be null, or positive and finite.");
        }

        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "The method must be a defined MeterMethodSetting.");
        }

        if (!MeterMethodSelector.TrySelect(method, counterSupported, counterSupported ? null : CounterOffReason, sampleRateHz, out MeterCapabilities? capabilities))
        {
            throw new MeasurementPreconditionException(
                "The deterministic meter was built with Meter:Method=counter but with counter support switched off. Use auto or sampling, or switch the counter on.");
        }

        _timeProvider = timeProvider;
        _wattsAt = wattsAt;
        Capabilities = capabilities;
        _baselineMeanWatts = baselineMeanWatts;
    }

    /// <inheritdoc />
    public MeterCapabilities Capabilities { get; }

    /// <summary>The number of samples of the most recently closed window (<c>SampleCount</c> of its reading); 0 until a window closes.</summary>
    public int LastWindowSampleCount
    {
        get
        {
            // Read under the lock, like BaselineMeanWatts, so a read never races the close that writes it.
            lock (_gate)
            {
                return field;
            }
        }

        private set;
    }

    /// <summary>The baseline in watts that windows closed now subtract: the constructor's value until a measurement replaces it; <c>null</c> when there is none.</summary>
    public double? BaselineMeanWatts
    {
        get
        {
            lock (_gate)
            {
                return _baselineMeanWatts;
            }
        }
    }

    /// <summary>Records time zero, the instant every sample time is measured from.</summary>
    /// <param name="cancellationToken">Checked before anything happens.</param>
    /// <exception cref="InvalidOperationException">The meter was already started.</exception>
    /// <exception cref="ObjectDisposedException">The meter was disposed.</exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled.</exception>
    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The meter was already started.");
            }

            _startTimestamp = _timeProvider.GetTimestamp();
            _started = true;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Measures the idle baseline over the virtual interval from now to now plus <paramref name="duration"/>, without waiting and without
    /// moving the clock (see the baseline rule of the class remarks). Every error of the meter itself is carried by the returned task,
    /// which is already complete, exactly as with the real meter. An exception from the power function is carried the same way when it is
    /// an <see cref="ArgumentException"/> or an <see cref="InvalidOperationException"/>, or an <see cref="OperationCanceledException"/>
    /// while <paramref name="cancellationToken"/> is cancelled; any other exception from the function leaves the call unchanged.
    /// </summary>
    /// <param name="duration">
    /// The length of the virtual interval in whole milliseconds (a fraction of a millisecond is dropped, as the real meter waits); at least
    /// 1 ms and at most <c>uint.MaxValue - 1</c> ms, short enough that the interval needs at most 1,000,000 samples, and short enough
    /// that the end of the interval is a valid <see cref="TimeSpan"/> from time zero.
    /// </param>
    /// <param name="maxCoefficientOfVariation">The stability threshold; finite and positive.</param>
    /// <param name="cancellationToken">Checked before anything happens.</param>
    /// <returns>A task that is already complete; its result is the baseline, and its mean replaces the stored one (or clears it when not reported).</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// (Faulting the task.) <paramref name="duration"/> is outside its range or needs more than 1,000,000 samples at the meter's rate, or
    /// <paramref name="maxCoefficientOfVariation"/> is not finite and positive.
    /// </exception>
    /// <exception cref="InvalidOperationException">(Faulting the task.) The meter was not started, or the power function returned a value outside 0 to 4,294,967.295 W or not finite.</exception>
    /// <exception cref="ObjectDisposedException">(Faulting the task.) The meter was disposed.</exception>
    /// <exception cref="OperationCanceledException">(Faulting the task, as the real meter does.) The token was already cancelled; the awaited type is exactly <see cref="OperationCanceledException"/>.</exception>
    public ValueTask<BaselineResult> MeasureBaselineAsync(TimeSpan duration, double maxCoefficientOfVariation, CancellationToken cancellationToken)
    {
        // ObjectDisposedException is an InvalidOperationException, so one clause carries both. A cancellation is carried only while the
        // caller's token is cancelled, and as a fault so that the awaited type is OperationCanceledException (a canceled task would throw
        // TaskCanceledException); one thrown by the power function under a live token is not the caller's and leaves the call.
        try
        {
            return ValueTask.FromResult(MeasureBaseline(duration, maxCoefficientOfVariation, cancellationToken));
        }
        catch (ArgumentException failure)
        {
            return ValueTask.FromException<BaselineResult>(failure);
        }
        catch (InvalidOperationException failure)
        {
            return ValueTask.FromException<BaselineResult>(failure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromException<BaselineResult>(new OperationCanceledException(cancellationToken));
        }
    }

    /// <summary>Opens a window at the current time of the clock.</summary>
    /// <returns>The open window; its owner closes it exactly once.</returns>
    /// <exception cref="InvalidOperationException">The meter was not started.</exception>
    /// <exception cref="ObjectDisposedException">The meter was disposed.</exception>
    public IMeterWindow OpenWindow()
    {
        lock (_gate)
        {
            ThrowIfNotRunning();
            return new Window(this, ElapsedSeconds());
        }
    }

    /// <summary>Marks the meter disposed; nothing else is held, so there is nothing to release. Safe to call twice.</summary>
    /// <returns>A task that is already complete.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private BaselineResult MeasureBaseline(TimeSpan duration, double maxCoefficientOfVariation, CancellationToken cancellationToken)
    {
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

        // Whole milliseconds, as the real meter waits; a fraction of a millisecond is dropped.
        long wholeMilliseconds = (long)Math.Floor(duration.TotalMilliseconds);
        double seconds = wholeMilliseconds / 1000.0;

        // Decided from the length alone: an interval of that length holds at most ceil(length x rate) grid instants strictly inside it,
        // so an interval that passes here can never be refused by the synthesis below.
        if (!(Math.Ceiling(seconds * Capabilities.SampleRateHz) + 2.0 <= MaxSamples))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                string.Create(CultureInfo.InvariantCulture, $"The baseline interval needs more than {MaxSamples} samples at {Capabilities.SampleRateHz} Hz; the deterministic meter refuses it."));
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfNotRunning();

            // Both edges come from the clock's ticks, like a window's, so an edge on the grid stays on it at long fake times (a double sum
            // of start and length can land one rounding step past a grid instant).
            TimeSpan elapsed = _timeProvider.GetElapsedTime(_startTimestamp);
            if (wholeMilliseconds > (long.MaxValue - elapsed.Ticks) / TimeSpan.TicksPerMillisecond)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(duration),
                    duration,
                    "The baseline interval would end beyond the largest TimeSpan counted from time zero.");
            }

            double start = elapsed.TotalSeconds;
            double stop = TimeSpan.FromTicks(elapsed.Ticks + (wholeMilliseconds * TimeSpan.TicksPerMillisecond)).TotalSeconds;
            PowerSample[] samples = Synthesize(start, stop, "interval");
            BaselineResult baseline = BaselineCalculator.Compute(samples, start, stop, maxCoefficientOfVariation);

            // One baseline per meter, as in the real meter: a measured mean replaces it and one that is not reported clears it.
            _baselineMeanWatts = baseline.MeanW.Value;
            return baseline;
        }
    }

    // Called under the lock.
    private void ThrowIfNotRunning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            throw new InvalidOperationException("The meter was not started.");
        }
    }

    // Called under the lock.
    private double ElapsedSeconds() => _timeProvider.GetElapsedTime(_startTimestamp).TotalSeconds;

    // Called under the lock. The samples of [start, stop] by the grid rule: the start edge, the grid instants strictly inside, the stop edge.
    private PowerSample[] Synthesize(double start, double stop, string what)
    {
        double rate = Capabilities.SampleRateHz;
        double first = (start * rate) + GridEpsilon;
        double last = (stop * rate) - GridEpsilon;

        // An allocation guard only: the floating span bounds the count from above by one, and the exact decision is made below, after the
        // clamp. It exists so an absurd window cannot reach the long conversions and the array below.
        double span = Math.Ceiling(last) - Math.Floor(first);
        if (!(span <= MaxSamples + 2.0))
        {
            throw TooManySamples(what, start, stop, rate);
        }

        // Grid indices from the first instant after the start edge to the last before the stop edge; the tolerance above keeps an edge that
        // lies on the grid out, and the loops keep any instant that is not strictly inside in double arithmetic out (large elapsed times).
        long firstIndex = (long)Math.Floor(first) + 1;
        long lastIndex = (long)Math.Ceiling(last) - 1;
        while (firstIndex <= lastIndex && firstIndex / rate <= start)
        {
            firstIndex++;
        }

        while (lastIndex >= firstIndex && lastIndex / rate >= stop)
        {
            lastIndex--;
        }

        long inner = Math.Max(0, lastIndex - firstIndex + 1);
        if (inner + 2 > MaxSamples)
        {
            throw TooManySamples(what, start, stop, rate);
        }

        int count = (int)inner;
        PowerSample[] samples = new PowerSample[count + 2];
        samples[0] = SampleAt(start);
        for (int i = 0; i < count; i++)
        {
            samples[i + 1] = SampleAt((firstIndex + i) / rate);
        }

        samples[count + 1] = SampleAt(stop);
        return samples;
    }

    private static InvalidOperationException TooManySamples(string what, double start, double stop, double rate) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"The {what} from {start} s to {stop} s needs more than {MaxSamples} samples at {rate} Hz; the deterministic meter refuses it."));

    private PowerSample SampleAt(double seconds)
    {
        double watts = _wattsAt(seconds);
        return double.IsFinite(watts) && watts >= 0.0 && watts <= MaxWatts
            ? new PowerSample(seconds, watts, Quiet)
            : throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The power function must return a finite power from 0 W to {MaxWatts} W, but it returned {watts} W at {seconds} s."));
    }

    private EnergyReading CloseWindow(Window window)
    {
        lock (_gate)
        {
            if (window.IsClosed)
            {
                throw new InvalidOperationException("The window was already closed.");
            }

            ThrowIfNotRunning();
            double start = window.StartSeconds;
            double stop = ElapsedSeconds();
            if (stop < start)
            {
                throw new InvalidOperationException("The clock went backwards: the window would end before it started.");
            }

            if (stop == start)
            {
                throw new InvalidOperationException("The window has no duration: it was closed at the same instant it was opened (zero duration).");
            }

            PowerSample[] samples = Synthesize(start, stop, "window");
            long? counterDelta = Capabilities.Method == EnergyMethod.Counter ? CounterDelta(samples, start, stop) : null;
            EnergyReading reading = ReadingCalculator.Compute(new ReadingInput(
                Samples: samples,
                WindowStartSeconds: start,
                WindowStopSeconds: stop,
                Method: Capabilities.Method,
                CounterDeltaMillijoules: counterDelta,
                BaselineMeanWatts: _baselineMeanWatts,
                SampleRateHz: Capabilities.SampleRateHz,
                IsSamplingFallback: Capabilities.IsFallback));

            // Nothing above changed the meter, so a window whose function threw stays open and can be closed again.
            window.MarkClosed(stop);
            LastWindowSampleCount = samples.Length;
            return reading;
        }
    }

    // What a counter read at both edges would show: the trapezoid integral of the samples in millijoules, rounded to a whole number.
    private static long CounterDelta(PowerSample[] samples, double start, double stop)
    {
        double joules = PowerIntegrator.IntegrateJoules(samples, start, stop)
            ?? throw new InvalidOperationException("A window always has its two edge samples.");
        double millijoules = Math.Round(joules * 1000.0);
        return double.IsFinite(millijoules) && Math.Abs(millijoules) < LongUpperBound
            ? (long)millijoules
            : throw new InvalidOperationException("The window's energy does not fit a counter delta of a long number of millijoules.");
    }

    private WindowSpan SpanOf(Window window)
    {
        lock (_gate)
        {
            return new WindowSpan(window.StartSeconds, window.StopSeconds);
        }
    }

    private sealed class Window(DeterministicEnergyMeter owner, double startSeconds) : IMeterWindow
    {
        public double StartSeconds { get; } = startSeconds;

        // Written under the owner's lock only.
        public double StopSeconds { get; private set; } = startSeconds;

        public bool IsClosed { get; private set; }

        public WindowSpan Span => owner.SpanOf(this);

        public EnergyReading Close() => owner.CloseWindow(this);

        public void MarkClosed(double stopSeconds)
        {
            StopSeconds = stopSeconds;
            IsClosed = true;
        }
    }
}