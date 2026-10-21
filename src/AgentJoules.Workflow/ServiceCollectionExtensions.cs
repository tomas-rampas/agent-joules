using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Providers;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AgentJoules.Workflow;

/// <summary>Registrations of the services that the workflow of a run shares process-wide.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>The key of the jitter <see cref="Random"/> registration.</summary>
    public const string JitterKey = "agentjoules-jitter";

    /// <summary>
    /// Registers the one <see cref="IMeterGate"/> of the process, chosen by the run mode (REQ-013, design 3.3): a <see cref="SerializedMeterGate"/> for
    /// <see cref="RunMode.Serialized"/>, <see cref="NoOpMeterGate.Instance"/> for <see cref="RunMode.Concurrent"/>. Every <see cref="MeteredChatClient"/> of
    /// the run, including the one that wraps the judge, takes the gate from this registration, so the choice of the mode is the only difference between the two
    /// modes: the workflow topology is built the same way in both.
    /// </summary>
    /// <remarks>
    /// The serialized gate is created by a factory, so the container owns it and disposes it when the provider is disposed. Dispose the provider only
    /// after every metered call has finished: a waiter still queued at that point never completes (see <see cref="SerializedMeterGate"/>). The no-op gate is
    /// shared static state and is never disposed. The per-run <see cref="MeteringServices"/> record (its sink differs between the workflow and the judge) is
    /// not a registration: the orchestrator builds it around the gate resolved from here. Keyed registrations of <see cref="IMeterGate"/> do not count as
    /// a second gate. A test that needs another gate replaces this one through <c>CliHostOverrides.Services</c> with <c>RemoveAll&lt;IMeterGate&gt;()</c>
    /// followed by <c>AddSingleton</c>.
    /// </remarks>
    /// <param name="services">The collection to add the gate to.</param>
    /// <param name="mode">The run mode, <c>--mode</c> on the command line.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined <see cref="RunMode"/>.</exception>
    /// <exception cref="InvalidOperationException">An <see cref="IMeterGate"/> is already registered; there is one gate per process and a second registration would hide which mode the run uses.</exception>
    public static IServiceCollection AddAgentJoulesMeterGate(this IServiceCollection services, RunMode mode)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.Any(static descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IMeterGate))
            ? throw new InvalidOperationException("An IMeterGate is already registered. A run has one meter gate, chosen by --mode, and a second registration would leave it unclear which mode the run uses.")
            : mode switch
            {
                RunMode.Serialized => services.AddSingleton<IMeterGate>(static _ => new SerializedMeterGate()),
                RunMode.Concurrent => services.AddSingleton<IMeterGate>(NoOpMeterGate.Instance),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The run mode is not a defined value; use Serialized or Concurrent."),
            };
    }

    /// <summary>
    /// Registers the provider clients of the tiers (REQ-012, design 3.4): one named <see cref="HttpClient"/> per tier name in
    /// <see cref="TierOptions.KnownNames"/>, called <c>aj-tier-&lt;name&gt;</c>, and <see cref="ProviderClientFactory"/> as a singleton. Each named client has an
    /// infinite <see cref="HttpClient.Timeout"/> (the only time limit of a call is the SDK network timeout of the tier) and a
    /// <see cref="RequestBodyPatchHandler"/> built from the options of its tier when the client pipeline is first resolved.
    /// </summary>
    /// <remarks>
    /// The default primary handler does not follow redirects: a redirect would send the request, and with it the key header and the prompt, to a host the tier
    /// does not name. The registration uses the standard <see cref="IHttpClientBuilder"/> pipeline, so a test replaces the primary handler of a tier with
    /// <c>services.AddHttpClient(ProviderClientFactory.HttpClientName("nvidia")).ConfigurePrimaryHttpMessageHandler(...)</c> and the isolated harness can do
    /// the same for every name. The caller registers <see cref="IConfiguration"/> and <c>IOptions&lt;AgentJoulesOptions&gt;</c> (the host does); the extra
    /// body of a tier that is not valid JSON fails when its client is first created, after the configuration validator rejected it at start.
    /// </remarks>
    /// <param name="services">The collection to add the clients to.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A <see cref="ProviderClientFactory"/> is already registered; a second registration would add a second patch handler to every client.</exception>
    public static IServiceCollection AddAgentJoulesProviderClients(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(static descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(ProviderClientFactory)))
        {
            throw new InvalidOperationException("The provider clients are already registered; a second registration would patch every request body twice.");
        }

        foreach (string tierName in TierOptions.KnownNames)
        {
            services
                .AddHttpClient(ProviderClientFactory.HttpClientName(tierName))
                .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false })
                .AddHttpMessageHandler(provider =>
                {
                    AgentJoulesOptions options = provider.GetRequiredService<IOptions<AgentJoulesOptions>>().Value;
                    return RequestBodyPatchHandler.For(options.Tiers.GetValueOrDefault(tierName), tierName);
                });
        }

        // The interface is what the agent factory depends on; it resolves to the one concrete singleton, so a test replaces the interface registration
        // (RemoveAll<IProviderClientFactory>() then AddSingleton) without touching the named HTTP clients, and the concrete type stays resolvable.
        return services
            .AddSingleton<ProviderClientFactory>()
            .AddSingleton<IProviderClientFactory>(static provider => provider.GetRequiredService<ProviderClientFactory>());
    }

    /// <summary>
    /// Registers what the agent factory needs from the container (REQ-011, design 3.4): the default <see cref="RetryOptions"/> and <see cref="Random.Shared"/> (keyed by <see cref="JitterKey"/>) as
    /// singletons, and a <c>Func&lt;MeteringServices, AgentFactory&gt;</c> that builds the factory of one tier run around the metering services of that run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="AgentFactory"/> itself is not a registration. Its <see cref="MeteringServices"/> belong to one tier run (the meter reads the GPU of that
    /// tier, and the sink of the workflow is not the sink of the judge), so a singleton or scoped factory would hold the services of the wrong run. The
    /// orchestrator builds the services of a run around the <see cref="IMeterGate"/> resolved from the container and passes them to the delegate; the
    /// delegate takes the retry layer's clock from those services, so a run has one clock for its windows and its waits, and takes the provider clients, the
    /// retry policy and the jitter source from the container. This is the per-run scope of the design, made by construction instead of by a container scope.
    /// </para>
    /// <para>
    /// The jitter source is <see cref="Random.Shared"/>, which is thread-safe, registered under the key <see cref="JitterKey"/> so that another <see cref="Random"/> registered
    /// in the container for another purpose cannot silently become the jitter source; a test registers a seeded <see cref="Random"/> under the same key first, and the
    /// agent factory then draws one seed per pipeline from it (see <see cref="AgentFactory"/>). Every registration uses <c>TryAdd</c>, so one that is already there wins and a
    /// second call changes nothing: a test or the isolated harness replaces an unkeyed one with <c>RemoveAll</c> and <c>AddSingleton</c>, and the jitter source with <c>RemoveAllKeyed&lt;Random&gt;(JitterKey)</c> and <c>AddKeyedSingleton(JitterKey, seeded)</c> (<c>RemoveAll&lt;Random&gt;()</c> removes only unkeyed descriptors and would leave the unseeded source in place). <see cref="IProviderClientFactory"/> is
    /// registered by <see cref="AddAgentJoulesProviderClients(IServiceCollection)"/> and is resolved, with the retry policy and the jitter source, when the delegate
    /// itself is first resolved from the container, not when it is registered and not when it is invoked.
    /// </para>
    /// </remarks>
    /// <param name="services">The collection to add the registrations to.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddAgentJoulesAgentFactory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(RetryOptions.Default);
        services.TryAddKeyedSingleton(JitterKey, Random.Shared);
        services.TryAddSingleton<Func<MeteringServices, AgentFactory>>(static provider =>
        {
            IProviderClientFactory providerClients = provider.GetRequiredService<IProviderClientFactory>();
            RetryOptions retryOptions = provider.GetRequiredService<RetryOptions>();
            Random random = provider.GetRequiredKeyedService<Random>(JitterKey);

            return runServices => new AgentFactory(providerClients, runServices ?? throw new ArgumentNullException(nameof(runServices)), retryOptions, runServices.Time, random);
        });

        return services;
    }

    /// <summary>
    /// Registers the <see cref="PreflightService"/> (design 3.7), the <see cref="ModelCatalogClient"/> it asks the models route with and the <see cref="DryRunService"/> of <c>run --dry-run</c>,
    /// as singletons. The service reads the key variables from the registered <see cref="IConfiguration"/> and the tiers from <c>IOptions&lt;AgentJoulesOptions&gt;</c> (the host registers both)
    /// and never from the process environment, so a test that replaces the configuration controls every key.
    /// </summary>
    /// <remarks>
    /// Prerequisites, resolved when the service is first used and not checked here: the provider clients (<see cref="AddAgentJoulesProviderClients(IServiceCollection)"/>, which register the
    /// <see cref="ProviderClientFactory"/> and the named HTTP client of each tier), a registered <see cref="Core.Security.SecretRedactor"/> (the host registers the one every writer masks with)
    /// and, for the dry run, an <see cref="Core.Gpu.IGpuTelemetrySourceFactory"/> (the host registers the NVML one, the harness one that reports no GPU).
    /// The registration uses <c>TryAdd</c>: one that is already there wins and a second call changes nothing.
    /// </remarks>
    /// <param name="services">The collection to add the service to.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddAgentJoulesPreflight(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ModelCatalogClient>();
        services.TryAddSingleton<PreflightService>();
        services.TryAddSingleton<DryRunService>();
        return services;
    }
}