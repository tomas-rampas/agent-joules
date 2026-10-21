using Microsoft.Extensions.DependencyInjection;

namespace AgentJoules.TestSupport;

/// <summary>
/// Counts registrations of a service collection by the name of the service type (AJ-051). The provider builders the OpenTelemetry SDK registers are internal types, so an exporter is
/// counted through the interface it registers: each exporter adds exactly one <see cref="MeterProviderConfigurations"/> or <see cref="TracerProviderConfigurations"/> registration.
/// </summary>
public static class ServiceRegistrations
{
    /// <summary>The name of the interface each metrics exporter and reader registers once.</summary>
    public const string MeterProviderConfigurations = "IConfigureMeterProviderBuilder";

    /// <summary>The name of the interface each trace exporter registers once.</summary>
    public const string TracerProviderConfigurations = "IConfigureTracerProviderBuilder";

    /// <summary>The number of registrations whose service type is named <paramref name="interfaceName"/>.</summary>
    /// <param name="services">The collection.</param>
    /// <param name="interfaceName">The simple name of the service type.</param>
    /// <returns>The count.</returns>
    public static int Count(IServiceCollection services, string interfaceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.Count(descriptor => descriptor.ServiceType.Name == interfaceName);
    }
}