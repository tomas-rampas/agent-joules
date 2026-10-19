namespace AgentJoules.Core.Gpu;

/// <summary>
/// The clock-event (throttle) bits one read returned. The bit-to-name mapping is added by AJ-028.
/// </summary>
/// <param name="Bits">The raw NVML clock-event bitmask; 0 when unsupported.</param>
/// <param name="Supported">False when the driver cannot report clock events.</param>
public readonly record struct ThrottleReasons(ulong Bits, bool Supported)
{
    /// <summary>The value of a read on a driver that cannot report clock events.</summary>
    public static ThrottleReasons Unsupported => new(0UL, false);
}