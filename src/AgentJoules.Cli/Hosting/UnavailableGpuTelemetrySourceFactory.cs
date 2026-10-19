using AgentJoules.Core.Gpu;

namespace AgentJoules.Cli.Hosting;

/// <summary>The default GPU factory under test overrides: no GPU is reachable unless a test registers a fake (deny-by-default, design 3.6).</summary>
internal sealed class UnavailableGpuTelemetrySourceFactory : IGpuTelemetrySourceFactory
{
    public GpuOpenResult Open(int gpuIndex) => GpuOpenResult.Unavailable("no GPU is reachable under test overrides");
}