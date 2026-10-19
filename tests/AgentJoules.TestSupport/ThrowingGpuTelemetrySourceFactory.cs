using AgentJoules.Core.Gpu;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentJoules.TestSupport;

/// <summary>An <see cref="IGpuTelemetrySourceFactory"/> whose <see cref="Open"/> throws a given exception: the service override of the AJ-034 exit-code scenarios.</summary>
public sealed class ThrowingGpuTelemetrySourceFactory(Exception exception) : IGpuTelemetrySourceFactory
{
    private readonly Exception _exception = exception ?? throw new ArgumentNullException(nameof(exception));

    /// <summary>A <c>CliHostOverrides.Services</c> callback that replaces the GPU factory registration with one that throws <paramref name="exception"/>.</summary>
    public static Action<IServiceCollection> AsOverride(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return services =>
        {
            services.RemoveAll<IGpuTelemetrySourceFactory>();
            services.AddSingleton<IGpuTelemetrySourceFactory>(new ThrowingGpuTelemetrySourceFactory(exception));
        };
    }

    /// <inheritdoc />
    public GpuOpenResult Open(int gpuIndex) => throw _exception;
}