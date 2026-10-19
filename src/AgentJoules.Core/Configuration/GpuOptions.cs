namespace AgentJoules.Core.Configuration;

/// <summary>The <c>Gpu</c> section of <c>AgentJoules</c>: which GPU is measured.</summary>
/// <remarks>A mutable class with plain get and set accessors so that the configuration binding generator binds it.</remarks>
public sealed class GpuOptions
{
    /// <summary>Key <c>Gpu:Index</c>, default 0: the NVML index of the measured GPU; 0 or greater.</summary>
    [MinimumValue(0, ErrorMessage = "Gpu:Index must be 0 or greater")]
    public int Index { get; set; }
}