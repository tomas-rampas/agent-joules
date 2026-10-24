using AgentJoules.Core.Configuration;
using AgentJoules.Core.Security;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Providers;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentJoules.TestSupport;

/// <summary>
/// A container wired like the host for the preflight (design 3.7): the real registrations of the provider clients and the preflight, the options and the configuration the test
/// hands over, a <see cref="SecretRedactor"/> that holds the key values of the configuration, and a <see cref="FakeHttpMessageHandler"/> as the primary handler of every tier, so the
/// real key check, the real <see cref="ModelCatalogClient"/> and the real HTTP pipeline run and nothing leaves the process. Dispose it to dispose the container.
/// </summary>
public sealed class PreflightHost : IDisposable
{
    private readonly ServiceProvider _services;

    internal PreflightHost(ServiceProvider services)
    {
        _services = services;
    }

    /// <summary>The preflight service under test.</summary>
    public PreflightService Preflight => _services.GetRequiredService<PreflightService>();

    /// <summary>The provider client factory the catalog client builds its request with.</summary>
    public ProviderClientFactory Factory => _services.GetRequiredService<ProviderClientFactory>();

    /// <summary>The model catalog client under test (the one the preflight uses).</summary>
    public ModelCatalogClient Catalog => _services.GetRequiredService<ModelCatalogClient>();

    /// <inheritdoc />
    public void Dispose() => _services.Dispose();
}

/// <summary>Builders for the preflight tests that need no command line: the container of <see cref="PreflightHost"/>.</summary>
public static class PreflightFixtures
{
    private static readonly string[] KeyVariables = [ProviderClientFixtures.NvidiaKeyVariable, ProviderClientFixtures.NebiusKeyVariable];

    /// <summary>Builds the container: the real registrations over <paramref name="configuration"/>, one fake endpoint per tier.</summary>
    /// <param name="configuration">The configuration that holds the settings and the key variables.</param>
    /// <param name="endpoints">Fake endpoints for some tiers by lowercase tier name; a tier that has none gets a default <see cref="FakeHttpMessageHandler"/>.</param>
    /// <param name="optionsOverride">The options to serve instead of the ones loaded from <paramref name="configuration"/> (a test that needs a state the validator refuses); the test may change them after the build.</param>
    /// <returns>The host; the caller disposes it.</returns>
    public static PreflightHost Build(
        IConfiguration configuration,
        IReadOnlyDictionary<string, FakeHttpMessageHandler>? endpoints = null,
        AgentJoulesOptions? optionsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        AgentJoulesOptions options = optionsOverride ?? OptionsLoader.Load(configuration);
        var redactor = new SecretRedactor();
        foreach (string variable in KeyVariables)
        {
            _ = redactor.Register(configuration[variable]);
        }

        var services = new ServiceCollection();
        _ = services.AddSingleton(configuration);
        _ = services.AddSingleton<IOptions<AgentJoulesOptions>>(Options.Create(options));
        _ = services.AddSingleton(redactor);
        _ = services.AddAgentJoulesProviderClients().AddAgentJoulesPreflight();

        // The preflight requires a local runtime; these hosts have none to ask, whatever Local:Runtime says.
        _ = services.AddSingleton<AgentJoules.Workflow.Local.ILocalRuntime>(new AgentJoules.Workflow.Local.NoOpLocalRuntime());

        foreach (string tierName in TierOptions.KnownNames)
        {
            FakeHttpMessageHandler handler = endpoints is not null && endpoints.TryGetValue(tierName, out FakeHttpMessageHandler? given) ? given : new FakeHttpMessageHandler();
            _ = services.AddHttpClient(ProviderClientFactory.HttpClientName(tierName)).ConfigurePrimaryHttpMessageHandler(() => handler);
        }

        return new PreflightHost(services.BuildServiceProvider());
    }
}