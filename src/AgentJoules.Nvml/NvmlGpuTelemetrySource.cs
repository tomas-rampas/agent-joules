using System.Globalization;

using AgentJoules.Core.Gpu;
using AgentJoules.Nvml.Interop;

namespace AgentJoules.Nvml;

/// <summary>
/// One open NVML session on one device. It owns the Init and Shutdown pair (the device handle is borrowed, so no
/// SafeHandle): <see cref="Dispose"/> calls Shutdown exactly once. Every read takes one private lock after the
/// dispose check, so concurrent reads never overlap and none touches NVML after Shutdown. The factory constructs it.
/// </summary>
public sealed class NvmlGpuTelemetrySource : IGpuTelemetrySource
{
    private readonly INvmlApi _api;
    private readonly nint _device;
    private readonly Lock _gate = new();
    private int _disposed;

    /// <summary>
    /// Wraps a session the factory initialised; <paramref name="counterUnsupportedReason"/> is null when the energy counter is
    /// supported. Internal because Dispose calls Shutdown, which is only correct for a session whose Init this source owns.
    /// </summary>
    internal NvmlGpuTelemetrySource(INvmlApi api, nint device, GpuIdentity identity, string? counterUnsupportedReason)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(identity);
        _api = api;
        _device = device;
        Identity = identity;
        EnergyCounterUnsupportedReason = counterUnsupportedReason;
    }

    /// <inheritdoc />
    public GpuIdentity Identity { get; }

    /// <inheritdoc />
    public bool EnergyCounterSupported => EnergyCounterUnsupportedReason is null;

    /// <inheritdoc />
    public string? EnergyCounterUnsupportedReason { get; }

    /// <inheritdoc />
    public double ReadPowerWatts()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            ThrowIfDisposed();
            NvmlReturn code = _api.GetPowerUsage(_device, out uint milliwatts);
            return code == NvmlReturn.Success ? milliwatts / 1000.0 : throw Failed(nameof(INvmlApi.GetPowerUsage), code);
        }
    }

    /// <inheritdoc />
    public double? ReadEnergyMillijoules()
    {
        ThrowIfDisposed();
        if (!EnergyCounterSupported)
        {
            return null;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            NvmlReturn code = _api.GetTotalEnergyConsumption(_device, out ulong millijoules);
            return code == NvmlReturn.Success ? millijoules : throw Failed(nameof(INvmlApi.GetTotalEnergyConsumption), code);
        }
    }

    /// <inheritdoc />
    public ThrottleReasons ReadThrottleReasons()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            ThrowIfDisposed();
            NvmlReturn code = _api.GetClocksEventReasons(_device, out ulong bits);
            return code switch
            {
                NvmlReturn.Success => new ThrottleReasons(bits, true),
                NvmlReturn.NotSupported => ThrottleReasons.Unsupported,
                _ => throw Failed(nameof(INvmlApi.GetClocksEventReasons), code),
            };
        }
    }

    /// <summary>
    /// Ends the NVML session; later calls, from any thread, do nothing. A missing library or export reported by Shutdown
    /// is swallowed so that nothing but the documented exceptions leaves this library.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The lock makes Shutdown wait for a read that is already in flight.
        lock (_gate)
        {
            try
            {
                _ = _api.Shutdown();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // The source is disposed either way; a failing Shutdown leaves nothing to clean up here.
            }
        }
    }

    private static InvalidOperationException Failed(string member, NvmlReturn code) =>
        new(string.Create(CultureInfo.InvariantCulture, $"NVML {member} returned {(int)code}."));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}