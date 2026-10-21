using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-052 scenario (exit 3 before any call when an API key is missing), handed to the step class by Reqnroll context injection. It holds the configuration
/// override the harness run gets (fake keys and fake base URLs, never a process variable), the fake endpoint of each tier, and the result. The scenarios run the stand-in
/// <see cref="RunCommandStandIn"/> through the real command line harness: the stand-in runs the real <c>PreflightService</c> key check from the built host and, outside a dry
/// run, makes one request through the real provider pipeline to the endpoint the scenario registered, so a refused connection and a request count are measured, not simulated.
/// The stand-in is used until AJ-054 builds the real <c>run</c> command; <see cref="RunAsync"/> fails the scenario once the application lists one.
/// </summary>
public sealed class MissingKeysContext
{
    /// <summary>The configuration key of the nebius base URL the scenarios set.</summary>
    public const string NebiusBaseUrlKey = "AgentJoules:Tiers:nebius:BaseUrl";

    private readonly Dictionary<string, HttpMessageHandler> _endpoints = new(StringComparer.Ordinal);

    /// <summary>The configuration override of the run; keys are root keys (<c>NVIDIA_API_KEY</c>), settings are full keys (<c>AgentJoules:Tiers:nvidia:BaseUrl</c>).</summary>
    public Dictionary<string, string?> Settings { get; } = new(StringComparer.Ordinal)
    {
        ["AgentJoules:Tiers:nvidia:BaseUrl"] = ProviderClientFixtures.BaseUrl(Tier.Nvidia),
        [NebiusBaseUrlKey] = ProviderClientFixtures.BaseUrl(Tier.Nebius),
    };

    /// <summary>The recording fake endpoint of the scenarios that count requests; it answers like a provider. Its models route lists every model ID the shipped configuration names, so the model check of the preflight (AJ-053) finds each configured ID.</summary>
    public FakeHttpMessageHandler Handler { get; } = new() { ModelIds = ConfigurationFixtures.ShippedModelIds() };

    /// <summary>The endpoint that refuses the connection and echoes the credential it saw, or null when the scenario has none.</summary>
    public CredentialEchoingRefusalHandler? Refusal { get; private set; }

    /// <summary>The runtime-built <c>nvapi-</c> key of the leak scenario, or null.</summary>
    public string? NvidiaKey { get; private set; }

    /// <summary>The result of the harness run, or null before it ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>A key built at run time from parts; no key-shaped literal is in the source.</summary>
    /// <returns>The key text.</returns>
    public static string FakeKey() => ProviderClientFixtures.FakeKey();

    /// <summary>Puts a fake key under the root key <paramref name="variable"/>.</summary>
    /// <param name="variable">The key variable name, for example <c>NVIDIA_API_KEY</c>.</param>
    public void SetKey(string variable) => Settings[variable] = FakeKey();

    /// <summary>Puts a runtime-built <c>nvapi-</c> key under <c>NVIDIA_API_KEY</c> and keeps it in <see cref="NvidiaKey"/>; the nvidia endpoint lists the models and refuses the completion request, echoing the key.</summary>
    public void UseLeakingNvidiaKey()
    {
        NvidiaKey = string.Concat("nvapi-", FakeKey(), "_Qd5R8hC1");
        Settings["NVIDIA_API_KEY"] = NvidiaKey;

        // The models route is answered (the preflight of AJ-053 asks it first) and the completion request is refused with the echoed key, so the chat path and its failure text are exercised.
        Refusal = new CredentialEchoingRefusalHandler(ConfigurationFixtures.ShippedModelIds());
        _endpoints["nvidia"] = Refusal;
    }

    /// <summary>Routes the named client of <paramref name="tierName"/> to the recording fake endpoint <see cref="Handler"/>.</summary>
    /// <param name="tierName">The lowercase tier name.</param>
    public void RouteToFake(string tierName) => _endpoints[tierName] = Handler;

    /// <summary>Routes every tier that has no endpoint yet to the recording fake endpoint <see cref="Handler"/>, so the model check of the preflight (AJ-053) never reaches the deny-all handler.</summary>
    public void RouteUnroutedToFake()
    {
        foreach (string tierName in TierOptions.KnownNames.Where(tierName => !_endpoints.ContainsKey(tierName)))
        {
            RouteToFake(tierName);
        }
    }

    /// <summary>True when the named client of <paramref name="tierName"/> has a registered endpoint.</summary>
    /// <param name="tierName">The lowercase tier name.</param>
    /// <returns>Whether an endpoint is registered.</returns>
    public bool HasRoute(string tierName) => _endpoints.ContainsKey(tierName);

    /// <summary>
    /// Runs <paramref name="arguments"/> through the harness with the stand-in <c>run</c> command, the real preflight and provider-client registrations, the configuration
    /// override of the scenario and the endpoints it registered; a client with no registered endpoint falls through to the deny-all handler of the harness.
    /// </summary>
    /// <param name="arguments">The command line, for example <c>run --tier all --dry-run</c>.</param>
    /// <returns>A task that completes when the command ended and <see cref="Result"/> is set.</returns>
    /// <exception cref="Xunit.Sdk.XunitException">The application lists a <c>run</c> command of its own (AJ-054 must rewire these scenarios).</exception>
    public async Task RunAsync(string arguments)
    {
        Result = await StandInRunner.RunAsync(arguments, "AJ-052 scenarios 1 to 8", Handler, Settings, _endpoints);
    }
}