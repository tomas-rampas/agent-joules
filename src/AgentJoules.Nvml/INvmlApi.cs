using AgentJoules.Nvml.Interop;

namespace AgentJoules.Nvml;

/// <summary>
/// The seam over the NVML functions the meter uses (design 3.1). The real implementation wraps the
/// native library; tests substitute a scripted fake. Every member returns the NVML return code and
/// reports its value through an out parameter that is only meaningful on <see cref="NvmlReturn.Success"/>.
/// </summary>
/// <remarks>
/// Members may throw only <see cref="DllNotFoundException"/>, <see cref="EntryPointNotFoundException"/> or
/// <see cref="BadImageFormatException"/> (the real binding failing to load or resolve the library); every other
/// failure is reported through <see cref="NvmlReturn"/>. Device handles are borrowed: they are valid only
/// between a successful <see cref="Init"/> and the matching <see cref="Shutdown"/>.
/// </remarks>
public interface INvmlApi
{
    NvmlReturn Init();

    NvmlReturn Shutdown();

    NvmlReturn GetHandleByIndex(uint index, out nint device);

    NvmlReturn GetPowerUsage(nint device, out uint milliwatts);

    NvmlReturn GetTotalEnergyConsumption(nint device, out ulong millijoules);

    /// <summary>
    /// Reads the clocks-event reason bitmask. A driver that lacks the clocks-event export reports
    /// <see cref="NvmlReturn.NotSupported"/>; it never surfaces as <see cref="EntryPointNotFoundException"/>.
    /// </summary>
    NvmlReturn GetClocksEventReasons(nint device, out ulong reasons);

    NvmlReturn GetMigMode(nint device, out uint current, out uint pending);

    NvmlReturn GetCount(out uint count);

    NvmlReturn GetName(nint device, out string name);

    NvmlReturn GetUuid(nint device, out string uuid);

    NvmlReturn GetVbios(nint device, out string vbios);

    NvmlReturn GetComputeCapability(nint device, out int major, out int minor);

    NvmlReturn GetPowerLimit(nint device, out uint milliwatts);

    NvmlReturn GetDriverVersion(out string version);
}