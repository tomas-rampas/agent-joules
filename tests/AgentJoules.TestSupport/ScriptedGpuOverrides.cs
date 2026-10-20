using AgentJoules.Cli;
using AgentJoules.Core.Gpu;
using AgentJoules.Nvml;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentJoules.TestSupport;

/// <summary>
/// The <see cref="CliHostOverrides"/> that put a <see cref="ScriptedNvmlApi"/> behind the real <see cref="NvmlGpuTelemetrySourceFactory"/>, so a CLI
/// run reads the scripted GPU through the whole binding. A missing NVML loader is the scripted API in missing-library mode.
/// </summary>
public static class ScriptedGpuOverrides
{
    /// <summary>Builds overrides whose GPU factory opens <paramref name="api"/> and whose in-memory configuration holds <paramref name="configuration"/>.</summary>
    /// <param name="api">The scripted NVML session the factory reads.</param>
    /// <param name="configuration">Full in-memory keys such as <c>AgentJoules:Meter:Method</c>; none when null.</param>
    public static CliHostOverrides Create(ScriptedNvmlApi api, IEnumerable<(string Key, string? Value)>? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(api);

        return new CliHostOverrides(
            Services: services =>
            {
                services.RemoveAll<IGpuTelemetrySourceFactory>();
                services.AddSingleton<IGpuTelemetrySourceFactory>(new NvmlGpuTelemetrySourceFactory(api));
            },
            Configuration: (configuration ?? []).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
}