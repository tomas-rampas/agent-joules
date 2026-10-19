using AgentJoules.Core.Gpu;

namespace AgentJoules.Core.Energy;

/// <summary>
/// One instantaneous power reading.
/// </summary>
/// <param name="Seconds">Seconds since meter start, derived from <c>TimeProvider.GetTimestamp()</c>; never a nominal interval.</param>
/// <param name="Watts">The power in watts; finite and not negative.</param>
/// <param name="Reasons">The clock-event (throttle) bits read with the sample.</param>
public readonly record struct PowerSample(double Seconds, double Watts, ThrottleReasons Reasons);