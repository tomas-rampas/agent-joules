using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentJoules.Workflow;

/// <summary>Registrations of the services that the workflow of a run shares process-wide.</summary>
public static class ServiceCollectionExtensions
{
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

        return services.AddSingleton<ProviderClientFactory>();
    }
}