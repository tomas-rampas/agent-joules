using OpenTelemetry.Metrics;

namespace AgentJoules.TestSupport;

/// <summary>One metric point an in-memory exporter holds, copied out of the exporter's live objects so a test can read it after the flush.</summary>
/// <param name="Instrument">The instrument name, for example <c>agentjoules.step.energy</c>.</param>
/// <param name="Unit">The unit of the instrument, or null.</param>
/// <param name="Type">The kind of metric the SDK built for the instrument (histogram, long sum, double sum).</param>
/// <param name="Tags">The attributes of the point.</param>
/// <param name="Sum">The sum of the recorded values (histogram sum, or the sum of a counter).</param>
/// <param name="Count">The number of measurements of a histogram point; 0 for a counter point, which does not count them.</param>
public sealed record MeasurementPoint(string Instrument, string? Unit, MetricType Type, IReadOnlyDictionary<string, object?> Tags, double Sum, long Count)
{
    /// <summary>The value of attribute <paramref name="name"/> as a string, or null when the point has no such attribute.</summary>
    /// <param name="name">The attribute name.</param>
    /// <returns>The value, or null.</returns>
    public string? Tag(string name) => Tags.TryGetValue(name, out object? value) ? value?.ToString() : null;
}