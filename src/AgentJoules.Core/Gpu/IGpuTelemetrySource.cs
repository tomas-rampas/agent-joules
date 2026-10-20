namespace AgentJoules.Core.Gpu;

/// <summary>
/// One opened GPU: its identity, the energy-counter state decided when it was opened, and the three reads the meter
/// uses. Disposing it ends the NVML session; the three Read members then throw <see cref="ObjectDisposedException"/>
/// while <see cref="Identity"/> and the counter state stay readable. <see cref="ObjectDisposedException"/> derives from
/// <see cref="InvalidOperationException"/>, so a caller that skips a failed read must catch
/// <see cref="ObjectDisposedException"/> first.
/// </summary>
public interface IGpuTelemetrySource : IDisposable
{
    /// <summary>Name, UUID, driver, VBIOS, compute capability, power limit and MIG mode, read once at open.</summary>
    GpuIdentity Identity { get; }

    /// <summary>True when the cumulative energy counter answered the single probe read at open.</summary>
    bool EnergyCounterSupported { get; }

    /// <summary>Why the counter is unsupported, for example <c>counter-not-supported: NVML_ERROR_NOT_SUPPORTED</c>; null when supported.</summary>
    string? EnergyCounterUnsupportedReason { get; }

    /// <summary>The instantaneous board power in watts; never returns 0 for a failed read.</summary>
    /// <exception cref="ObjectDisposedException">The source was disposed.</exception>
    /// <exception cref="InvalidOperationException">NVML returned a non-Success code.</exception>
    double ReadPowerWatts();

    /// <summary>The cumulative energy in millijoules since driver load; null when the counter is unsupported.</summary>
    /// <exception cref="ObjectDisposedException">The source was disposed.</exception>
    /// <exception cref="InvalidOperationException">The counter is supported but NVML returned a non-Success code.</exception>
    double? ReadEnergyMillijoules();

    /// <summary>The current clock-event (throttle) bits, or <see cref="ThrottleReasons.Unsupported"/> when the driver cannot report them.</summary>
    /// <exception cref="ObjectDisposedException">The source was disposed.</exception>
    /// <exception cref="InvalidOperationException">NVML returned a non-Success code other than NotSupported.</exception>
    ThrottleReasons ReadThrottleReasons();
}