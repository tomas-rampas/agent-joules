using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.Options;

namespace AgentJoules.Workflow.Local;

/// <summary>
/// The NIM container behind the local tier (REQ-019, REQ-020, design 3.4 "Local runtimes"), selected by <c>Local:Runtime</c> <c>nim</c> (AJ-102). <see cref="CheckAvailableAsync"/> asks <c>GET &lt;BaseUrl&gt;/version</c>
/// (the <c>/v1/version</c> route of the container), so a container that is not running is reported as <c>NIM not reachable at &lt;BaseUrl&gt;; start the container as recorded in docs/nim-local-attempt.md</c> (exit code
/// 5). <see cref="DescribeAsync"/> states the <c>environment</c> of a local run (AJ-105): the version is the <c>release</c> of that answer (null when it holds none), the GPU offload is 100 by construction (a NIM
/// container serves the model from GPU memory only), and the precision is <c>Tiers:local:Precision</c>, which the configuration validator requires for this runtime, so no precision is ever guessed. <see cref="WarmUpAsync"/> (AJ-106) sends one short chat
/// completion (<c>POST &lt;BaseUrl&gt;/chat/completions</c>, the single word <c>ready</c> asked for, 8 tokens at most, temperature 0) through the plain client and returns how long it took on the injected clock.
/// </summary>
/// <remarks>
/// The request goes through the named <see cref="HttpClient"/> of the local tier, with the time limit of <see cref="OllamaRuntime"/>, and carries no key: the version route needs none. The warm-up completion is built by the factory of the models request (a key configured for the local tier travels with it as on the preflight) and runs under the full <c>TimeoutSeconds</c> of the tier on the injected clock. No text of a response leaves this class.
/// </remarks>
/// <param name="factory">The provider client factory; it validates the local tier and gives its endpoint and network timeout.</param>
/// <param name="httpClients">The factory of the named <see cref="HttpClient"/> of each tier.</param>
/// <param name="options">The options, for the configured precision of the local tier.</param>
/// <param name="time">The clock that measures the warm-up call; the system clock when null.</param>
public sealed class NimRuntime(ProviderClientFactory factory, IHttpClientFactory httpClients, IOptions<AgentJoulesOptions> options, TimeProvider? time = null) : ILocalRuntime
{
    /// <summary>The name of the runtime, the value of <c>Local:Runtime</c> that selects it.</summary>
    public const string RuntimeName = LocalOptions.Nim;

    /// <summary>The GPU offload of a NIM container in percent: by construction, the container loads the model into GPU memory.</summary>
    public const double GpuOffloadPercent = 100.0;

    private const string VersionRoute = "/version";
    private const string CompletionsRoute = "/chat/completions";
    private const string WarmUpPrompt = "Reply with the single word ready.";
    private const int WarmUpMaxTokens = 8;
    private const int MaxDisclosureLength = 64;

    private readonly ProviderClientFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly IHttpClientFactory _httpClients = httpClients ?? throw new ArgumentNullException(nameof(httpClients));
    private readonly IOptions<AgentJoulesOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public string Name => RuntimeName;

    /// <inheritdoc />
    /// <exception cref="ConfigurationException">The local tier is not configured or its base URL or timeout is not usable (exit code 2).</exception>
    /// <exception cref="EndpointUnreachableException">The connection was refused, nothing answered within the limit or the status is not a success; the message is <c>NIM not reachable at &lt;BaseUrl&gt;; start the container as recorded in docs/nim-local-attempt.md</c> (exit code 5).</exception>
    public async Task CheckAvailableAsync(CancellationToken cancellationToken)
    {
        (HttpClient client, string baseUrl, TimeSpan limit, string unreachable) = Prepare();
        _ = await LocalRuntimeHttp.SendAsync(client, HttpMethod.Get, new Uri(baseUrl + VersionRoute), jsonBody: null, readBody: false, limit, unreachable, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <returns>The runtime name, the <c>release</c> of the version route, 100 and <c>Tiers:local:Precision</c> (null when it is blank).</returns>
    /// <exception cref="ConfigurationException">The local tier is not configured or its base URL or timeout is not usable (exit code 2).</exception>
    /// <exception cref="EndpointUnreachableException">The request was refused, timed out or answered a status other than success (exit code 5).</exception>
    public async Task<LocalRuntimeInfo> DescribeAsync(CancellationToken cancellationToken)
    {
        (HttpClient client, string baseUrl, TimeSpan limit, string unreachable) = Prepare();
        string? release;
        using (JsonDocument? answer = await LocalRuntimeHttp.SendAsync(client, HttpMethod.Get, new Uri(baseUrl + VersionRoute), jsonBody: null, readBody: true, limit, unreachable, cancellationToken).ConfigureAwait(false))
        {
            release = answer is null ? null : LocalRuntimeHttp.Text(answer.RootElement, "release", MaxDisclosureLength);
        }

        string? precision = _options.Value.Tiers.TryGetValue(TierOptions.NameOf(Tier.Local), out TierOptions? local) && local is not null && !string.IsNullOrWhiteSpace(local.Precision)
            ? local.Precision.Trim()
            : null;

        return new LocalRuntimeInfo(RuntimeName, release, GpuOffloadPercent, precision);
    }

    /// <inheritdoc />
    /// <returns>The wall time of the one short chat completion, measured on the injected clock around the call.</returns>
    /// <exception cref="ArgumentException"><paramref name="modelId"/> is null or blank.</exception>
    /// <exception cref="ConfigurationException">The local tier is not configured or its base URL or timeout is not usable (exit code 2).</exception>
    /// <exception cref="MissingKeyException">The local tier names a key variable that has no value (exit code 3), as on the models request.</exception>
    /// <exception cref="EndpointUnreachableException">The connection was refused or nothing answered within the timeout of the tier (the container record message), or the container answered a status other than success (<c>the warm-up of &lt;model&gt; failed with HTTP &lt;status&gt; at &lt;BaseUrl&gt;</c>, never the body); exit code 5.</exception>
    public async Task<TimeSpan?> WarmUpAsync(string modelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        cancellationToken.ThrowIfCancellationRequested();

        (HttpClient client, string baseUrl, _, string unreachable) = Prepare();
        TimeSpan limit = LocalRuntimeHttp.LoadLimitOf(_factory.SettingsFor(Tier.Local));
        var message = new JsonObject { ["role"] = "user", ["content"] = WarmUpPrompt };
        string body = new JsonObject { ["model"] = modelId, ["messages"] = new JsonArray(message), ["max_tokens"] = WarmUpMaxTokens, ["temperature"] = 0 }.ToJsonString();

        // Built by the factory of the models request: a key configured for the local tier travels with the warm-up exactly as it does on the preflight.
        HttpRequestMessage request = _factory.CreateRequest(Tier.Local, HttpMethod.Post, CompletionsRoute);
        request.Content = LocalRuntimeHttp.JsonContent(body);

        return await LocalRuntimeHttp.TimeWarmUpAsync(_time, client, request, limit, unreachable, status => LocalRuntimeHttp.WarmUpFailed(modelId, status, baseUrl), cancellationToken).ConfigureAwait(false);
    }

    // The client, the configured base URL without a trailing slash, the time limit and the unreachable message of the local tier; the factory validates the tier.
    private (HttpClient Client, string BaseUrl, TimeSpan Limit, string Unreachable) Prepare()
    {
        ProviderClientSettings settings = _factory.SettingsFor(Tier.Local);
        string baseUrl = settings.Endpoint.AbsoluteUri.TrimEnd('/');
        HttpClient client = _httpClients.CreateClient(ProviderClientFactory.HttpClientName(TierOptions.NameOf(Tier.Local)));

        return (client, baseUrl, LocalRuntimeHttp.LimitOf(settings), $"NIM not reachable at {baseUrl}; start the container as recorded in docs/nim-local-attempt.md");
    }
}