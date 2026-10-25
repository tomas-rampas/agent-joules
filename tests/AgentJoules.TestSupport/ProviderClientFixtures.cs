using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentJoules.TestSupport;

/// <summary>
/// A container wired like the host for the provider clients (REQ-012, design 6.1): the real registration of the named clients and the factory, with
/// a <see cref="FakeHttpMessageHandler"/> as the primary handler of every tier, so the real OpenAI SDK, the real adapter and the real body patch run
/// and no request leaves the process. Dispose it to dispose the container.
/// </summary>
public sealed class ProviderClientHost : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly Dictionary<Tier, FakeHttpMessageHandler> _handlers;

    internal ProviderClientHost(ServiceProvider services, AgentJoulesOptions options, Dictionary<Tier, FakeHttpMessageHandler> handlers)
    {
        _services = services;
        _handlers = handlers;
        Options = options;
    }

    /// <summary>The options the container serves as <c>IOptions&lt;AgentJoulesOptions&gt;</c>.</summary>
    public AgentJoulesOptions Options { get; }

    /// <summary>The factory under test.</summary>
    public ProviderClientFactory Factory => _services.GetRequiredService<ProviderClientFactory>();

    /// <summary>The HTTP client factory that holds the named client of every tier.</summary>
    public IHttpClientFactory HttpClients => _services.GetRequiredService<IHttpClientFactory>();

    /// <summary>The fake endpoint of <paramref name="tier"/>.</summary>
    /// <param name="tier">A tier.</param>
    /// <returns>The primary handler of the tier.</returns>
    public FakeHttpMessageHandler Handler(Tier tier) => _handlers[tier];

    /// <inheritdoc />
    public void Dispose() => _services.Dispose();
}

/// <summary>Builders for the provider-client tests: four tiers with four fake base URLs, a runtime-built fake key, and the host.</summary>
public static class ProviderClientFixtures
{
    /// <summary>The key variable of the nvidia tier in the fixture options.</summary>
    public const string NvidiaKeyVariable = "NVIDIA_API_KEY";

    /// <summary>The key variable of the nebius tier in the fixture options.</summary>
    public const string NebiusKeyVariable = "NEBIUS_API_KEY";

    /// <summary>A fake key built at run time from parts, so that no key-shaped literal is in the source; the fake handler never answers it and no test sends it anywhere real.</summary>
    /// <returns>The key text.</returns>
    public static string FakeKey() => string.Concat("zk7w", "2qm9", "xv4t", "j8hd");

    /// <summary>The fake base URL of <paramref name="tier"/> in <see cref="FourTiers"/>: https, one host per tier, ending in <c>/v1</c>.</summary>
    /// <param name="tier">A defined tier.</param>
    /// <returns>The base URL.</returns>
    public static string BaseUrl(Tier tier) => "https://" + TierOptions.NameOf(tier) + ".fake.test/v1";

    /// <summary>Options with the four tiers enabled, each with its own fake base URL, model <c>model-&lt;tier&gt;</c> and timeout (the hosted tiers name a key variable).</summary>
    /// <returns>New options; the caller may change them before building the host.</returns>
    public static AgentJoulesOptions FourTiers()
    {
        var options = new AgentJoulesOptions();
        int timeout = 120;
        foreach (Tier tier in new[] { Tier.Local, Tier.CloudGpu, Tier.Nvidia, Tier.Nebius })
        {
            string name = TierOptions.NameOf(tier);
            options.Tiers[name] = new TierOptions
            {
                BaseUrl = BaseUrl(tier),
                ModelId = "model-" + name,
                ApiKeyVariable = tier switch { Tier.Nvidia => NvidiaKeyVariable, Tier.Nebius => NebiusKeyVariable, _ => null },
                TimeoutSeconds = timeout++,
            };
        }

        return options;
    }

    /// <summary>The configuration that holds the fake key under both key variables.</summary>
    /// <returns>An in-memory configuration; no process environment variable is read or set.</returns>
    public static IConfiguration KeyConfiguration() =>
        ConfigurationFixtures.InMemory((NvidiaKeyVariable, FakeKey()), (NebiusKeyVariable, FakeKey()));

    /// <summary>Builds the host: the real registration over <paramref name="options"/> and <paramref name="configuration"/>, one fake endpoint per tier.</summary>
    /// <param name="options">The options to serve.</param>
    /// <param name="configuration">The configuration that holds the key variables; <see cref="KeyConfiguration"/> when null.</param>
    /// <param name="handlers">Fake endpoints to use for some tiers; a tier that has none gets a default <see cref="FakeHttpMessageHandler"/>.</param>
    /// <param name="redactor">The redactor the in-stream error detection masks provider text with; an empty one (it masks nothing) when null, because the host never runs without one and the pipeline requires it.</param>
    /// <returns>The host; the caller disposes it.</returns>
    public static ProviderClientHost Build(
        AgentJoulesOptions options,
        IConfiguration? configuration = null,
        IReadOnlyDictionary<Tier, FakeHttpMessageHandler>? handlers = null,
        SecretRedactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var all = new Dictionary<Tier, FakeHttpMessageHandler>();
        var services = new ServiceCollection();
        _ = services.AddSingleton(configuration ?? KeyConfiguration());
        _ = services.AddSingleton(redactor ?? new SecretRedactor());

        _ = services.AddSingleton<IOptions<AgentJoulesOptions>>(Options.Create(options));
        _ = services.AddAgentJoulesProviderClients();

        foreach (Tier tier in new[] { Tier.Local, Tier.CloudGpu, Tier.Nvidia, Tier.Nebius })
        {
            FakeHttpMessageHandler handler = handlers is not null && handlers.TryGetValue(tier, out FakeHttpMessageHandler? given) ? given : new FakeHttpMessageHandler();
            all[tier] = handler;
            _ = services.AddHttpClient(ProviderClientFactory.HttpClientName(TierOptions.NameOf(tier))).ConfigurePrimaryHttpMessageHandler(() => handler);
        }

        return new ProviderClientHost(services.BuildServiceProvider(), options, all);
    }
}