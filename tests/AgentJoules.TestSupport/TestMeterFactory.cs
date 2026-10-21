using System.Diagnostics.Metrics;

namespace AgentJoules.TestSupport;

/// <summary>
/// A minimal <see cref="IMeterFactory"/> for the tests that build <c>AgentJoulesInstruments</c> without a service container: it creates every meter with itself as the scope, as the
/// factory of the container does, and disposes them with itself. The tests of the instruments themselves use the container's factory (<c>AddMetrics()</c>).
/// </summary>
public sealed class TestMeterFactory : IMeterFactory
{
    private readonly Lock _gate = new();
    private readonly List<Meter> _meters = [];

    /// <inheritdoc />
    public Meter Create(MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Scope ??= this;
        var meter = new Meter(options);
        lock (_gate)
        {
            _meters.Add(meter);
        }

        return meter;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (Meter meter in _meters)
            {
                meter.Dispose();
            }

            _meters.Clear();
        }
    }
}