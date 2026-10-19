using System.Runtime.InteropServices;

namespace AgentJoules.Nvml.Interop;

/// <summary>
/// The raw NVML entry points (design 3.1). Each declaration returns the NVML return code as an int and cites
/// the NVML API reference for its signature; units are milliwatts (power) and millijoules (energy).
/// Character buffers are caller-owned UTF-8 spans, decoded up to the first NUL by <see cref="NvmlApi"/>.
/// </summary>
internal static partial class NvmlNative
{
    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlInitializationAndCleanup.html
    [LibraryImport("nvml", EntryPoint = "nvmlInit_v2")]
    internal static partial int NvmlInit();

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlInitializationAndCleanup.html
    [LibraryImport("nvml", EntryPoint = "nvmlShutdown")]
    internal static partial int NvmlShutdown();

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlSystemQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlSystemGetDriverVersion")]
    internal static partial int SystemGetDriverVersion(Span<byte> version, uint length);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetCount_v2")]
    internal static partial int DeviceGetCount(out uint deviceCount);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    internal static partial int DeviceGetHandleByIndex(uint index, out nint device);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetName")]
    internal static partial int DeviceGetName(nint device, Span<byte> name, uint length);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetUUID")]
    internal static partial int DeviceGetUuid(nint device, Span<byte> uuid, uint length);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetVbiosVersion")]
    internal static partial int DeviceGetVbiosVersion(nint device, Span<byte> version, uint length);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetCudaComputeCapability")]
    internal static partial int DeviceGetCudaComputeCapability(nint device, out int major, out int minor);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    internal static partial int DeviceGetEnforcedPowerLimit(nint device, out uint limit);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetPowerUsage")]
    internal static partial int DeviceGetPowerUsage(nint device, out uint power);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetTotalEnergyConsumption")]
    internal static partial int DeviceGetTotalEnergyConsumption(nint device, out ulong energy);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlMultiInstanceGPU.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetMigMode")]
    internal static partial int DeviceGetMigMode(nint device, out uint currentMode, out uint pendingMode);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetCurrentClocksEventReasons")]
    internal static partial int DeviceGetCurrentClocksEventReasons(nint device, out ulong clocksEventReasons);

    // https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html
    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")]
    internal static partial int DeviceGetCurrentClocksThrottleReasons(nint device, out ulong clocksThrottleReasons);
}