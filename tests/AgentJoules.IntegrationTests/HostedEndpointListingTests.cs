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
/// The live check behind the endpoint record of AJ-101 (REQ-018): <c>GET {Tiers:&lt;tier&gt;:BaseUrl}/models</c> through the production <see cref="ModelCatalogClient"/> (the named client of the tier, the bearer key
/// read by the NAME in <c>ApiKeyVariable</c> from the process environment) lists every <c>- model:</c> ID the committed <c>docs/endpoints.md</c> records for the tier, and every ID the committed
/// <c>config/appsettings.json</c> names for the tier (and, on the judge tier, the judge ID). The tests are skipped unless AGENTJOULES_INTEGRATION is 1 (<see cref="IntegrationFactAttribute"/>); with the gate open and
/// the key variable unset the client refuses before any request with a message that names the variable and never a value. Only counts and IDs are written to the output, never a key.
/// </summary>
[Trait("Spec", "REQ-018")]
public sealed class HostedEndpointListingTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private readonly ITestOutputHelper _output = output;

    [IntegrationFact]
    public Task The_nvidia_listing_holds_every_recorded_and_configured_model_id() => AssertListingHoldsRecordAsync(Tier.Nvidia);

    [IntegrationFact]
    public Task The_nebius_listing_holds_every_recorded_and_configured_model_id() => AssertListingHoldsRecordAsync(Tier.Nebius);

    private async Task AssertListingHoldsRecordAsync(Tier tier)
    {
        string name = TierOptions.NameOf(tier);
        AgentJoulesOptions options = ConfigurationFixtures.ShippedOptions();
        string document = ContentPaths.Resolve(ConfigurationFixtures.RepositoryRoot(), options.Paths.Endpoints, PathsOptions.EndpointsKey);
        Assert.True(EndpointsDocParser.Parse(await File.ReadAllTextAsync(document)).TryGetSection(name, out EndpointsSection? section), $"{options.Paths.Endpoints} has no section ## {name}.");
        Assert.NotEmpty(section.ModelIds);

        string[] configured = ConfiguredIds(options, name);
        Assert.NotEmpty(configured);

        // Real HTTP: the named client of the tier with the production pipeline; the key comes from the process environment by its variable name.
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
        ModelCatalog catalog = await provider.GetRequiredService<ModelCatalogClient>().ListAsync(tier, timeout.Token);

        _output.WriteLine($"{name}: {catalog.Count} models listed; {section.ModelIds.Count} recorded; {configured.Length} configured");
        string[] unlisted = [.. section.ModelIds.Concat(configured).Distinct(StringComparer.Ordinal).Where(id => !catalog.Lists(id))];
        Assert.True(unlisted.Length == 0, $"Not listed by {name}: {string.Join(", ", unlisted.Select(id => redactor.Redact(id)))}");
    }

    // Every model ID the shipped configuration names for the tier: its model, its agent models, and the judge model when the tier is the judge tier.
    private static string[] ConfiguredIds(AgentJoulesOptions options, string name)
    {
        TierOptions entry = options.Tiers[name];
        IEnumerable<string?> ids = new[] { entry.ModelId }.Concat(entry.AgentModelIds.Values);
        if (string.Equals(options.Judge.Tier, name, StringComparison.Ordinal))
        {
            ids = ids.Append(options.Judge.ModelId);
        }

        return [.. ids.Where(static id => !string.IsNullOrWhiteSpace(id)).Select(static id => id!).Distinct(StringComparer.Ordinal)];
    }
}