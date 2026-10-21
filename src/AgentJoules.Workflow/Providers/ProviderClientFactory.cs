using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using OpenAI;

namespace AgentJoules.Workflow.Providers;

/// <summary>
/// Creates the chat client of a tier from configuration only (REQ-012, design 3.4): no URL, no model ID and no key is written in code. The client is
/// the OpenAI SDK pointed at <c>Tiers:&lt;tier&gt;:BaseUrl</c> and adapted to <see cref="IChatClient"/>; everything a tier needs to differ in (the
/// base URL, the model, the key, the timeout) comes from its options.
/// </summary>
/// <remarks>
/// <para>
/// The SDK retry is switched off (<see cref="ClientRetryPolicy"/> with 0 retries): the rate-limit retry of a run is its own layer inside the
/// measurement window, so a hidden SDK retry would send the request again without being counted. The <see cref="HttpClient"/> comes from the named
/// registration <c>aj-tier-&lt;tier&gt;</c> (see <see cref="ServiceCollectionExtensions.AddAgentJoulesProviderClients(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>)
/// with an infinite <see cref="HttpClient.Timeout"/>, so the only time limit of a call is the SDK <c>NetworkTimeout</c>, <c>TimeoutSeconds</c> of the tier;
/// the 100 second default of the <see cref="HttpClient"/> would cut a call the configured 300 seconds allow. The body adapters of the tier
/// (<see cref="RequestBodyPatchHandler"/>) sit in that named pipeline, and a test or the isolated harness replaces its primary handler.
/// </para>
/// <para>
/// The key is read by the NAME in <c>ApiKeyVariable</c> from <see cref="IConfiguration"/> when a client is created and is handed to the SDK
/// credential; it is never held in an options object, a field or a message (design 5.1). A tier without <c>ApiKeyVariable</c> (the local and
/// cloudgpu tiers) sends the placeholder <c>no-key</c>, which their endpoints do not check. A tier whose <c>ApiKeyVariable</c> names a variable that has
/// no value (absent, empty or white space) is refused with <see cref="ExitCode.MissingKey"/> before any request: sending the placeholder to a hosted
/// endpoint would only produce a 401 that hides the real cause, and a missing key must never pass for a measured failure. The message names the
/// variable, never a value. A tier that has a key is refused unless its base URL is https or its host is loopback: the configuration validator checks
/// the same rule (<see cref="EndpointRules"/>) at start, and this is the check at the moment the key would be sent. A base URL that is not an absolute
/// http or https URL without user info, query or fragment is refused for every tier.
/// </para>
/// </remarks>
/// <param name="httpClients">The factory of the named <see cref="HttpClient"/> of each tier.</param>
/// <param name="options">The bound and validated options; the tiers are read when a client is created.</param>
/// <param name="configuration">The configuration that holds the key variables (the process environment, user secrets or an in-memory override).</param>
public sealed class ProviderClientFactory(IHttpClientFactory httpClients, IOptions<AgentJoulesOptions> options, IConfiguration configuration) : IProviderClientFactory
{
    /// <summary>The prefix of the name of the <see cref="HttpClient"/> of a tier; the full name is the prefix and the lowercase tier name.</summary>
    public const string HttpClientNamePrefix = "aj-tier-";

    private const string NoKey = "no-key";
    private const int SdkMaxRetries = 0;

    private readonly IHttpClientFactory _httpClients = httpClients ?? throw new ArgumentNullException(nameof(httpClients));
    private readonly IOptions<AgentJoulesOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    /// <summary>The name of the <see cref="HttpClient"/> registration of <paramref name="tierName"/>.</summary>
    /// <param name="tierName">The lowercase tier name, one of <see cref="TierOptions.KnownNames"/>.</param>
    /// <returns><c>aj-tier-</c> and the name.</returns>
    public static string HttpClientName(string tierName) => HttpClientNamePrefix + tierName;

    /// <summary>
    /// The transport settings a client of <paramref name="tier"/> gets. They are read from a fresh <see cref="OpenAIClientOptions"/> built by the same private
    /// method that builds the options <see cref="Create"/> hands to the SDK (without the transport), so the endpoint and the network timeout cannot drift from
    /// what a client gets; <see cref="ProviderClientSettings.MaxRetries"/> is the constant the retry policy is built from, because the policy itself cannot be read back.
    /// </summary>
    /// <param name="tier">A defined tier.</param>
    /// <returns>The endpoint, the network timeout and the retry count.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a defined tier.</exception>
    /// <exception cref="ConfigurationException">The tier is not configured, or its base URL or timeout is not usable.</exception>
    public ProviderClientSettings SettingsFor(Tier tier)
    {
        OpenAIClientOptions built = BuildOptions(TierOptions.NameOf(tier), transport: null);

        return new ProviderClientSettings(built.Endpoint!, built.NetworkTimeout!.Value, SdkMaxRetries);
    }

    /// <summary>Creates the chat client of <paramref name="tier"/> for <paramref name="modelId"/>.</summary>
    /// <param name="tier">A defined tier.</param>
    /// <param name="modelId">The model ID of the call; the agent factory takes it from <see cref="TierOptions.ResolveModelId(string)"/>.</param>
    /// <returns>A chat client with no retry and an infinite <see cref="HttpClient"/> timeout; the caller owns nothing it must dispose.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a defined tier.</exception>
    /// <exception cref="ArgumentException"><paramref name="modelId"/> is null or blank.</exception>
    /// <exception cref="ConfigurationException">The tier is not configured, its base URL or timeout is not usable, or it has a key and its base URL is plain http on a host that is not loopback.</exception>
    /// <exception cref="MissingKeyException">The tier names a key variable that has no value (exit code 3, the message of the preflight key check).</exception>
    public IChatClient Create(Tier tier, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        string name = TierOptions.NameOf(tier);
        string? key = ReadKey(name);
        OpenAIClientOptions clientOptions = BuildOptions(name, _httpClients.CreateClient(HttpClientName(name)));

        RequireSecureEndpoint(name, key, clientOptions.Endpoint);

        return new OpenAIClient(new ApiKeyCredential(key ?? NoKey), clientOptions).GetChatClient(modelId).AsIChatClient();
    }

    // A tier that has a key is refused unless its base URL is https or its host is loopback: the key would travel in clear text to a host the user did not name. The chat client and the
    // models request of the preflight both send the key, so both call this one rule.
    private static void RequireSecureEndpoint(string name, string? key, Uri? endpoint)
    {
        if (key is not null && EndpointRules.Check(endpoint, requireHttpsOffLoopback: true) != EndpointProblem.None)
        {
            throw new ConfigurationException("Tiers:" + name + ":BaseUrl must be https unless the host is loopback when the tier has an API key");
        }
    }

    // The one place the SDK options of a tier are built: Create passes the transport, SettingsFor leaves it out and reads the rest back.
    private OpenAIClientOptions BuildOptions(string name, HttpClient? transport)
    {
        TierOptions tierOptions = TierFor(name);

        Uri.TryCreate(tierOptions.BaseUrl, UriKind.Absolute, out Uri? endpoint);
        if (EndpointRules.Check(endpoint, requireHttpsOffLoopback: false) != EndpointProblem.None)
        {
            throw new ConfigurationException("Tiers:" + name + ":BaseUrl must be an absolute http or https URL without user info, query or fragment; the value is not shown");
        }

        if (tierOptions.TimeoutSeconds <= 0)
        {
            throw new ConfigurationException("Tiers:" + name + ":TimeoutSeconds must be greater than 0");
        }

        var built = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            RetryPolicy = new ClientRetryPolicy(SdkMaxRetries),
            NetworkTimeout = TimeSpan.FromSeconds(tierOptions.TimeoutSeconds),
        };

        if (transport is not null)
        {
            built.Transport = new HttpClientPipelineTransport(transport);
        }

        return built;
    }

    private TierOptions TierFor(string name) =>
        _options.Value.Tiers.TryGetValue(name, out TierOptions? tierOptions) && tierOptions is not null
            ? tierOptions
            : throw new ConfigurationException("Tiers:" + name + " is not configured");

    /// <summary>
    /// Builds the request of the preflight model check (EDGE-008) for <paramref name="tier"/>: <c>GET {BaseUrl}/models</c> with the key of the tier as a bearer token, or without an
    /// <c>Authorization</c> header for a tier that has no <c>ApiKeyVariable</c>. The base URL, the timeout and the rule that a key travels only over https (or to a loopback host) are those of
    /// <see cref="Create"/>; the key is read here and put in the header of this one request, so it never leaves this class as a value.
    /// </summary>
    /// <param name="tier">A defined tier.</param>
    /// <returns>The request; the caller disposes it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a defined tier.</exception>
    /// <exception cref="ConfigurationException">The tier is not configured, its base URL or timeout is not usable, or it has a key and its base URL is plain http on a host that is not loopback.</exception>
    /// <exception cref="MissingKeyException">The tier names a key variable that has no value (exit code 3).</exception>
    internal HttpRequestMessage CreateModelsRequest(Tier tier)
    {
        string name = TierOptions.NameOf(tier);
        string? key = ReadKey(name);
        ProviderClientSettings settings = SettingsFor(tier);
        RequireSecureEndpoint(name, key, settings.Endpoint);

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(settings.Endpoint.AbsoluteUri.TrimEnd('/') + "/models"));
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return request;
    }

    // The value of the key variable of the tier, or null for a tier without one. Read at the moment of use and never stored; it is handed to the SDK credential or to one request header and nowhere else.
    private string? ReadKey(string name)
    {
        string? variable = TierFor(name).ApiKeyVariable;
        if (variable is null)
        {
            return null;
        }

        string? key = _configuration[variable];
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        // The same message as the preflight key check (MissingKeyMessage). A name that is not shaped like a variable name (the validator's rule, VariableNames) might be a key
        // typed into the wrong setting, so neither the message nor the exception holds it.
        string[] shown = [VariableNames.IsValid(variable) ? variable : MissingKeyMessage.HiddenName];
        throw new MissingKeyException(MissingKeyMessage.Format(shown), shown);
    }
}