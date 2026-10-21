using AgentJoules.Core.Results;

namespace AgentJoules.Metering;

/// <summary>
/// An <see cref="IMetricsSink"/> that keeps every reading it receives in a list, in the order the publishes reached it. The orchestrator
/// creates one for the workflow and one for the judge of every tier run; two instances are what keep a judge reading out of the workflow
/// results (design 3.3).
/// </summary>
public sealed class InMemoryMetricsSink : IMetricsSink
{
    private readonly Lock _gate = new();
    private readonly List<StepReading> _readings = [];

    /// <inheritdoc />
    public void Publish(StepReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        lock (_gate)
        {
            _readings.Add(reading);
        }
    }

    /// <summary>The readings published so far, in publish order.</summary>
    /// <returns>A copy: readings published later do not appear in it, and changing it does not change the sink.</returns>
    public IReadOnlyList<StepReading> Snapshot()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(_readings.ToArray());
        }
    }
}