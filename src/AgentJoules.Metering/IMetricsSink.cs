using AgentJoules.Core.Results;

namespace AgentJoules.Metering;

/// <summary>
/// Receives the finished reading of every metered call. A sink is told about a reading once, after the call ended, and keeps or forwards it;
/// the workflow sink and the judge sink are two separate instances, so judge exclusion is by construction (design 3.3).
/// </summary>
public interface IMetricsSink
{
    /// <summary>Publishes one finished reading; safe to call from several threads at once.</summary>
    /// <param name="reading">The reading; not null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null.</exception>
    void Publish(StepReading reading);
}