using AgentJoules.Core.Results;

namespace AgentJoules.TestSupport;

/// <summary>What one call of a <see cref="TelemetryRig"/> did: the reading its sink received and the exception the call threw.</summary>
/// <param name="Reading">The reading the sink received, or null when the call published none.</param>
/// <param name="Thrown">The exception that left the call, or null.</param>
public sealed record TelemetryCallOutcome(StepReading? Reading, Exception? Thrown);