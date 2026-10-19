using System.Globalization;
using System.Runtime.InteropServices;

using AgentJoules.Core.Gpu;
using AgentJoules.Nvml.Interop;

namespace AgentJoules.Nvml;

/// <summary>
/// Opens an NVML device and turns every failure into an unavailable result. A missing or unloadable library, a failed
/// call or a bad index never escapes as an exception, and the reason never carries an exception message because that
/// message can list the library load paths.
/// </summary>
public sealed class NvmlGpuTelemetrySourceFactory : IGpuTelemetrySourceFactory
{
    private const string WindowsLibrary = "nvml.dll";
    private const string LinuxLibrary = "libnvidia-ml.so.1";

    private readonly INvmlApi _api;
    private readonly string _libraryName;

    /// <summary>Creates a factory over <paramref name="api"/>.</summary>
    /// <param name="api">The NVML seam.</param>
    /// <param name="platform">The platform that names the library in the not-found reason; null uses the running OS (every non-Windows platform names the Linux library).</param>
    public NvmlGpuTelemetrySourceFactory(INvmlApi api, OSPlatform? platform = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        bool windows = platform is { } given ? given == OSPlatform.Windows : OperatingSystem.IsWindows();
        _libraryName = windows ? WindowsLibrary : LinuxLibrary;
    }

    /// <summary>Opens the GPU at <paramref name="gpuIndex"/>; the caller owns and disposes the source of an available result.</summary>
    /// <remarks>
    /// Only <see cref="DllNotFoundException"/>, <see cref="EntryPointNotFoundException"/> and <see cref="BadImageFormatException"/>
    /// are mapped to an unavailable result (the documented contract of <see cref="INvmlApi"/>). Any other exception is outside
    /// that contract and propagates, but only after the session was shut down exactly once.
    /// </remarks>
    public GpuOpenResult Open(int gpuIndex)
    {
        if (gpuIndex < 0)
        {
            return GpuOpenResult.Unavailable(NoDevice(gpuIndex));
        }

        bool initialised = false;
        bool handedOff = false;
        try
        {
            NvmlReturn init = _api.Init();
            if (init != NvmlReturn.Success)
            {
                return GpuOpenResult.Unavailable(Returned("nvmlInit_v2", init));
            }

            initialised = true;
            string? failure = TryBuild(gpuIndex, out NvmlGpuTelemetrySource? source);
            if (source is null)
            {
                return GpuOpenResult.Unavailable(failure ?? NoDevice(gpuIndex));
            }

            handedOff = true;
            return GpuOpenResult.Available(source);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return GpuOpenResult.Unavailable($"NVML library not found: {_libraryName} ({ex.GetType().Name})");
        }
        finally
        {
            if (initialised && !handedOff)
            {
                ShutdownQuietly();
            }
        }
    }

    private static string NoDevice(int gpuIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"no device at index {gpuIndex}");

    private static string Returned(string function, NvmlReturn code) =>
        string.Create(CultureInfo.InvariantCulture, $"{function} returned {(int)code}");

    private static string MapMigMode(NvmlReturn code, uint current) => code switch
    {
        NvmlReturn.Success => current == 0 ? "disabled" : "enabled",
        _ => "not-supported",
    };

    private static string? CounterUnsupportedReason(NvmlReturn code) => code switch
    {
        NvmlReturn.Success => null,
        NvmlReturn.NotSupported => "counter-not-supported: NVML_ERROR_NOT_SUPPORTED",
        _ => string.Create(CultureInfo.InvariantCulture, $"counter-not-supported: {(int)code}"),
    };

    // Returns the failure reason, or null with the source set. The session stays initialised; the caller shuts it down on failure.
    private string? TryBuild(int gpuIndex, out NvmlGpuTelemetrySource? source)
    {
        source = null;

        NvmlReturn code = _api.GetCount(out uint count);
        if (code != NvmlReturn.Success)
        {
            return Returned("nvmlDeviceGetCount_v2", code);
        }

        if ((uint)gpuIndex >= count)
        {
            return NoDevice(gpuIndex);
        }

        code = _api.GetHandleByIndex((uint)gpuIndex, out nint device);
        if (code != NvmlReturn.Success)
        {
            return Returned("nvmlDeviceGetHandleByIndex_v2", code);
        }

        code = _api.GetDriverVersion(out string driverVersion);
        if (code != NvmlReturn.Success)
        {
            return Returned("nvmlSystemGetDriverVersion", code);
        }

        code = _api.GetName(device, out string name);
        if (code != NvmlReturn.Success)
        {
            return Returned("nvmlDeviceGetName", code);
        }

        code = _api.GetUuid(device, out string uuid);
        if (code != NvmlReturn.Success)
        {
            return Returned("nvmlDeviceGetUUID", code);
        }

        code = _api.GetPowerLimit(device, out uint limitMilliwatts);
        if (code != NvmlReturn.Success)
        {
            return Returned("nvmlDeviceGetEnforcedPowerLimit", code);
        }

        string? vbios = _api.GetVbios(device, out string vbiosText) == NvmlReturn.Success ? vbiosText : null;
        string? capability = _api.GetComputeCapability(device, out int major, out int minor) == NvmlReturn.Success
            ? string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}")
            : null;
        NvmlReturn migCode = _api.GetMigMode(device, out uint migCurrent, out _);

        // One counter read decides support for the life of the source.
        string? counterReason = CounterUnsupportedReason(_api.GetTotalEnergyConsumption(device, out _));

        var identity = new GpuIdentity(
            name,
            uuid,
            driverVersion,
            vbios,
            capability,
            limitMilliwatts / 1000.0,
            MapMigMode(migCode, migCurrent));
        source = new NvmlGpuTelemetrySource(_api, device, identity, counterReason);
        return null;
    }

    private void ShutdownQuietly()
    {
        try
        {
            _ = _api.Shutdown();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // The failure being reported is already the answer; a failing Shutdown adds nothing.
        }
    }
}