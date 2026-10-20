using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Energy;

/// <summary>
/// The <see cref="IEnergyMeter"/> of the hosted tiers (nvidia, nebius): their GPU is not ours, so nothing is measured, and every
/// reading's energy metrics are N/A with the reason <c>hosted-tier</c> (design 3.2). It exists so that the metered client has one shape for
/// every tier: it opens and closes a window per call and gets an <see cref="EnergyReading"/> back, and the N/A comes from here instead of
/// from a branch in the client.
/// </summary>
/// <remarks>
/// <para>
/// It starts no thread and no timer and reads no sample. The only thing a window has is its length, read from the <see cref="TimeProvider"/>
/// given to the constructor, so the reading's duration is real and its span counts seconds since <see cref="StartAsync"/> like the span of
/// every other meter.
/// </para>
/// <para>
/// <see cref="Capabilities"/> uses <see cref="EnergyMethod.Unmetered"/> with the counter unsupported for the reason <c>hosted-tier</c> and
/// no sampling rate. <see cref="MeasureBaselineAsync"/> is an error here and not a baseline that is not reported: hosted tiers skip the
/// baseline (design 3.4), and a caller that asks for one has a bug that a not-reported value would hide. A window that has no duration
/// (closed at the instant it opened, or the clock went backwards) throws <see cref="InvalidOperationException"/> and stays open, as the
/// contract of <see cref="IMeterWindow.Close"/> says; the clock of a real call always advances.
/// </para>
/// </remarks>
public sealed class UnmeteredEnergyMeter : IEnergyMeter
{
    private static readonly MeterCapabilities HostedCapabilities =
        new(EnergyMethod.Unmetered, counterSupported: false, MeterCapabilities.HostedTierReason, sampleRateHz: 0.0, isFallback: false);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private long _startTimestamp;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a meter that is not started yet.</summary>
    /// <param name="timeProvider">The clock that gives a window its length; a fake clock in tests.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is null.</exception>
    public UnmeteredEnergyMeter(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public MeterCapabilities Capabilities => HostedCapabilities;

    /// <inheritdoc />
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

    /// <summary>A hosted tier has no baseline: this always fails, so a caller that asks for one is found instead of served a made-up value.</summary>
    /// <param name="duration">Ignored.</param>
    /// <param name="maxCoefficientOfVariation">Ignored.</param>
    /// <param name="cancellationToken">Ignored.</param>
    /// <returns>A task that is already faulted with <see cref="InvalidOperationException"/>, or with <see cref="ObjectDisposedException"/> once the meter is disposed.</returns>
    public ValueTask<BaselineResult> MeasureBaselineAsync(TimeSpan duration, double maxCoefficientOfVariation, CancellationToken cancellationToken)
    {
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }
        }
        catch (ObjectDisposedException disposed)
        {
            // Like every error of this meter, the disposal is carried by the returned task.
            return ValueTask.FromException<BaselineResult>(disposed);
        }

        return ValueTask.FromException<BaselineResult>(
            new InvalidOperationException("A hosted tier has no idle baseline: its GPU is not measured, so the baseline is skipped."));
    }

    /// <inheritdoc />
    public IMeterWindow OpenWindow()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                throw new InvalidOperationException("The meter was not started.");
            }

            double start = ElapsedSeconds();
            return new Window(this, start);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    // Callers hold _gate.
    private double ElapsedSeconds() => _timeProvider.GetElapsedTime(_startTimestamp).TotalSeconds;

    private sealed class Window(UnmeteredEnergyMeter owner, double startSeconds) : IMeterWindow
    {
        private readonly UnmeteredEnergyMeter _owner = owner;
        private readonly double _startSeconds = startSeconds;
        private double _stopSeconds = startSeconds;
        private bool _closed;

        public WindowSpan Span
        {
            get
            {
                lock (_owner._gate)
                {
                    return new WindowSpan(_startSeconds, _stopSeconds);
                }
            }
        }

        public EnergyReading Close()
        {
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
                if (_closed)
                {
                    throw new InvalidOperationException("The window was already closed.");
                }

                double stop = _owner.ElapsedSeconds();
                if (!(stop > _startSeconds))
                {
                    throw new InvalidOperationException("The window has no duration: the clock did not advance since it was opened, or it went backwards.");
                }

                _stopSeconds = stop;
                _closed = true;
                Metric notApplicable = Metric.NotApplicable(MeterCapabilities.HostedTierReason);
                return new EnergyReading(
                    grossJoules: notApplicable,
                    netJoules: notApplicable,
                    averageWatts: notApplicable,
                    peakWatts: notApplicable,
                    durationSeconds: stop - _startSeconds,
                    method: EnergyMethod.Unmetered,
                    sampleCount: 0,
                    sampleRateHz: 0.0,
                    flags: ReadingFlags.None,
                    throttleReasons: null);
            }
        }
    }
}