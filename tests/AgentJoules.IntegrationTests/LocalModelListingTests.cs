using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Reporting.Endpoints;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Xunit;
using Xunit.Abstractions;

namespace AgentJoules.IntegrationTests;

/// <summary>
/// The live check behind the local model record of AJ-107 (REQ-018, REQ-020, EDGE-009, REQ-012, design 6.3), whatever the local runtime is: <c>GET {Tiers:local:BaseUrl}/models</c> through the production
/// <see cref="ModelCatalogClient"/> (the named client of the local tier over the shipped configuration) lists the model the configuration names for the local tier, and that model appears as a <c>- model:</c> line of the
/// <c>## local</c> section of the committed <c>docs/endpoints.md</c>. The record is checked before the request, so a missing line fails without a call. The test is skipped unless AGENTJOULES_INTEGRATION is 1
/// (<see cref="IntegrationFactAttribute"/>), with the message of the gate. Only counts and the model ID are written to the output, never a key.
/// </summary>
[Trait("Spec", "REQ-018")]
[Trait("Spec", "REQ-020")]
[Trait("Spec", "EDGE-009")]
[Trait("Spec", "REQ-012")]
public sealed class LocalModelListingTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private readonly ITestOutputHelper _output = output;

    [IntegrationFact]
    public async Task The_local_endpoint_lists_the_configured_model_and_the_endpoints_record_holds_it()
    {
        string name = TierOptions.NameOf(Tier.Local);
        AgentJoulesOptions options = ConfigurationFixtures.ShippedOptions();
        string modelId = options.Tiers[name].ModelId ?? throw new Xunit.Sdk.XunitException($"Tiers:{name}:ModelId is not set in the shipped configuration.");

        string document = ContentPaths.Resolve(ConfigurationFixtures.RepositoryRoot(), options.Paths.Endpoints, PathsOptions.EndpointsKey);
        Assert.True(EndpointsDocParser.Parse(await File.ReadAllTextAsync(document)).TryGetSection(name, out EndpointsSection? section), $"{options.Paths.Endpoints} has no section ## {name}.");
        Assert.True(section.Lists(modelId), $"The section ## {name} of {options.Paths.Endpoints} has no \"- model:\" line for the configured model {modelId}.");

        // Real HTTP: the named client of the local tier with the production pipeline; a key variable, when the tier names one, is read from the process environment by its name.
        IConfiguration environment = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var redactor = new SecretRedactor();
        string? variable = options.Tiers[name].ApiKeyVariable;
        redactor.Register(variable is null ? null : environment[variable]);

        var services = new ServiceCollection();
        _ = services.AddSingleton(environment);
        _ = services.AddSingleton<IOptions<AgentJoulesOptions>>(Options.Create(options));
        _ = services.AddSingleton(redactor);
        _ = services.AddAgentJoulesProviderClients();
        _ = services.AddSingleton<ModelCatalogClient>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        using var timeout = new CancellationTokenSource(Limit);
        ModelCatalog catalog = await provider.GetRequiredService<ModelCatalogClient>().ListAsync(Tier.Local, timeout.Token);

        _output.WriteLine($"{name}: {catalog.Count} models listed; configured model {redactor.Redact(modelId)}");
        Assert.True(catalog.Lists(modelId), $"The {name} endpoint does not list the configured model {redactor.Redact(modelId)}.");
    }
}