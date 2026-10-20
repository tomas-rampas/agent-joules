namespace AgentJoules.Core.Gpu;

/// <summary>Opens a GPU by index and reports any failure as a typed result, never as an exception.</summary>
public interface IGpuTelemetrySourceFactory
{
    /// <summary>Opens the GPU at <paramref name="gpuIndex"/>; the caller owns and disposes the source of an available result.</summary>
    GpuOpenResult Open(int gpuIndex);
}