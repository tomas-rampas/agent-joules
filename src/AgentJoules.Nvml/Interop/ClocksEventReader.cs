namespace AgentJoules.Nvml.Interop;

/// <summary>A native clocks-event export; returns the NVML return code and the reason bitmask through <paramref name="reasons"/>.</summary>
internal delegate int NvmlClocksExport(nint device, out ulong reasons);

/// <summary>
/// Reads the clocks-event reasons through the current export and falls back to the deprecated export on drivers
/// that lack the new name. A missing export surfaces as <see cref="NvmlReturn.NotSupported"/>, never as
/// <see cref="EntryPointNotFoundException"/>. Any other exception (for example <see cref="DllNotFoundException"/>) propagates
/// and leaves the learned state untouched, so the next read retries. The state is a single volatile field with no lock:
/// concurrent first reads may probe more than once but all converge on the same state.
/// </summary>
/// <param name="current">The current export (nvmlDeviceGetCurrentClocksEventReasons).</param>
/// <param name="deprecated">The deprecated export (nvmlDeviceGetCurrentClocksThrottleReasons).</param>
internal sealed class ClocksEventReader(NvmlClocksExport current, NvmlClocksExport deprecated)
{
    private readonly NvmlClocksExport _current = current;
    private readonly NvmlClocksExport _deprecated = deprecated;

    // The export that worked, learned on the first read so a missing export throws at most once.
    private volatile ExportState _state = ExportState.Unknown;

    private enum ExportState
    {
        Unknown,
        Current,
        Deprecated,
        Missing,
    }

    /// <summary>Reads the reasons; <paramref name="reasons"/> is 0 unless the result is <see cref="NvmlReturn.Success"/>.</summary>
    /// <param name="device">The borrowed device handle.</param>
    /// <param name="reasons">The clocks-event reason bitmask.</param>
    /// <returns>The NVML return code, or <see cref="NvmlReturn.NotSupported"/> when neither export exists.</returns>
    public NvmlReturn Read(nint device, out ulong reasons)
    {
        reasons = 0;
        ExportState state = _state;

        if (state is ExportState.Unknown or ExportState.Current)
        {
            try
            {
                var result = (NvmlReturn)_current(device, out reasons);
                reasons = result == NvmlReturn.Success ? reasons : 0;
                _state = ExportState.Current;
                return result;
            }
            catch (EntryPointNotFoundException)
            {
                // An older driver without the renamed export: fall through to the deprecated name.
                reasons = 0;
            }
        }

        if (state is ExportState.Unknown or ExportState.Deprecated)
        {
            try
            {
                var result = (NvmlReturn)_deprecated(device, out reasons);
                reasons = result == NvmlReturn.Success ? reasons : 0;
                _state = ExportState.Deprecated;
                return result;
            }
            catch (EntryPointNotFoundException)
            {
                reasons = 0;
                _state = ExportState.Missing;
            }
        }

        return NvmlReturn.NotSupported;
    }
}