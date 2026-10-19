using System.Text;

using AgentJoules.Nvml.Interop;

namespace AgentJoules.Nvml;

/// <summary>
/// The real <see cref="INvmlApi"/> over the native library. Return codes are cast to <see cref="NvmlReturn"/>
/// (an unnamed code passes through as its integer); a missing driver surfaces as <see cref="DllNotFoundException"/> on the first call.
/// </summary>
public sealed class NvmlApi : INvmlApi
{
    private const int DriverVersionBufferSize = 80;
    private const int DeviceStringBufferSize = 96;

    private readonly ClocksEventReader _clocks = new(
        NvmlNative.DeviceGetCurrentClocksEventReasons,
        NvmlNative.DeviceGetCurrentClocksThrottleReasons);

    public NvmlApi()
    {
        NvmlLibraryResolver.EnsureRegistered();
    }

    public NvmlReturn Init() => (NvmlReturn)NvmlNative.NvmlInit();

    public NvmlReturn Shutdown() => (NvmlReturn)NvmlNative.NvmlShutdown();

    public NvmlReturn GetHandleByIndex(uint index, out nint device) =>
        (NvmlReturn)NvmlNative.DeviceGetHandleByIndex(index, out device);

    public NvmlReturn GetPowerUsage(nint device, out uint milliwatts) =>
        (NvmlReturn)NvmlNative.DeviceGetPowerUsage(device, out milliwatts);

    public NvmlReturn GetTotalEnergyConsumption(nint device, out ulong millijoules) =>
        (NvmlReturn)NvmlNative.DeviceGetTotalEnergyConsumption(device, out millijoules);

    public NvmlReturn GetClocksEventReasons(nint device, out ulong reasons) => _clocks.Read(device, out reasons);

    public NvmlReturn GetMigMode(nint device, out uint current, out uint pending) =>
        (NvmlReturn)NvmlNative.DeviceGetMigMode(device, out current, out pending);

    public NvmlReturn GetCount(out uint count) => (NvmlReturn)NvmlNative.DeviceGetCount(out count);

    public NvmlReturn GetName(nint device, out string name)
    {
        Span<byte> buffer = stackalloc byte[DeviceStringBufferSize];
        var result = (NvmlReturn)NvmlNative.DeviceGetName(device, buffer, (uint)buffer.Length);
        name = result == NvmlReturn.Success ? DecodeUtf8(buffer) : string.Empty;
        return result;
    }

    public NvmlReturn GetUuid(nint device, out string uuid)
    {
        Span<byte> buffer = stackalloc byte[DeviceStringBufferSize];
        var result = (NvmlReturn)NvmlNative.DeviceGetUuid(device, buffer, (uint)buffer.Length);
        uuid = result == NvmlReturn.Success ? DecodeUtf8(buffer) : string.Empty;
        return result;
    }

    public NvmlReturn GetVbios(nint device, out string vbios)
    {
        Span<byte> buffer = stackalloc byte[DeviceStringBufferSize];
        var result = (NvmlReturn)NvmlNative.DeviceGetVbiosVersion(device, buffer, (uint)buffer.Length);
        vbios = result == NvmlReturn.Success ? DecodeUtf8(buffer) : string.Empty;
        return result;
    }

    public NvmlReturn GetComputeCapability(nint device, out int major, out int minor) =>
        (NvmlReturn)NvmlNative.DeviceGetCudaComputeCapability(device, out major, out minor);

    public NvmlReturn GetPowerLimit(nint device, out uint milliwatts) =>
        (NvmlReturn)NvmlNative.DeviceGetEnforcedPowerLimit(device, out milliwatts);

    public NvmlReturn GetDriverVersion(out string version)
    {
        Span<byte> buffer = stackalloc byte[DriverVersionBufferSize];
        var result = (NvmlReturn)NvmlNative.SystemGetDriverVersion(buffer, (uint)buffer.Length);
        version = result == NvmlReturn.Success ? DecodeUtf8(buffer) : string.Empty;
        return result;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> buffer)
    {
        int end = buffer.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? buffer : buffer[..end]);
    }
}