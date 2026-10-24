using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Providers;

namespace AgentJoules.Workflow.Local;

/// <summary>
/// The Ollama runtime behind the local tier (EDGE-009, design 3.4 "Local runtimes"): this story gives it the availability check and nothing else. <see cref="CheckAvailableAsync"/> asks the native
/// route <c>GET &lt;native base&gt;/api/version</c>, where the native base is the base URL of the tier without its trailing <c>/v1</c> segment, so a runtime that is not running is reported as
/// <c>Ollama not reachable at &lt;native base&gt;; start it with "ollama serve"</c> (exit code 5) instead of as a bare connection error from the models route. The description of the runtime and the
/// warm-up are the no-op behaviour of M2 for now.
/// </summary>
/// <remarks>
/// The request goes through the named <see cref="HttpClient"/> of the local tier (<see cref="ProviderClientFactory.HttpClientName(string)"/>), the pipeline the completions and the models route use, so a test
/// fake or the deny-all handler of the harness applies to it and a redirect is not followed. It carries no key: the native route of Ollama needs none. Its time limit is the lesser of the
/// <c>TimeoutSeconds</c> of the tier and <see cref="ModelCatalogClient.PreflightTimeoutSeconds"/>, the limit of the models route. No text of a response leaves this class: the message holds the
/// endpoint, which the configuration validator keeps free of user information, query and fragment, and a fixed phrase.
/// </remarks>
/// <param name="factory">The provider client factory; it validates the local tier and gives its endpoint and network timeout.</param>
/// <param name="httpClients">The factory of the named <see cref="HttpClient"/> of each tier.</param>
public sealed class OllamaRuntime(ProviderClientFactory factory, IHttpClientFactory httpClients) : ILocalRuntime
{
    /// <summary>The name of the runtime, the value of <c>Local:Runtime</c> that selects it.</summary>
    public const string RuntimeName = LocalOptions.Ollama;

    private const string V1Segment = "/v1";
    private const string VersionRoute = "/api/version";

    private readonly ProviderClientFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly IHttpClientFactory _httpClients = httpClients ?? throw new ArgumentNullException(nameof(httpClients));

    /// <inheritdoc />
    public string Name => RuntimeName;

    /// <summary>
    /// The native base of an Ollama install from the base URL of its tier: the same address without a trailing <c>/v1</c> path segment (<c>/v1/</c> too); a base URL that does not end in that segment
    /// (<c>/proxy</c>, <c>/v10</c>) is returned as it is, and a query or fragment is dropped.
    /// </summary>
    /// <param name="baseUrl">The <c>BaseUrl</c> of the tier.</param>
    /// <returns>The native base; its path is <c>/</c> when nothing is left of the path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="baseUrl"/> is null.</exception>
    public static Uri NativeBase(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        var builder = new UriBuilder(baseUrl) { Query = string.Empty, Fragment = string.Empty };
        string path = builder.Path.TrimEnd('/');
        builder.Path = path.EndsWith(V1Segment, StringComparison.Ordinal) ? path[..^V1Segment.Length] : path;
        return builder.Uri;
    }

    /// <inheritdoc />
    /// <exception cref="ConfigurationException">The local tier is not configured or its base URL or timeout is not usable (exit code 2).</exception>
    /// <exception cref="EndpointUnreachableException">The connection was refused, nothing answered within the limit or the status is not a success; the message is <c>Ollama not reachable at &lt;native base&gt;; start it with "ollama serve"</c> (exit code 5).</exception>
    public async Task CheckAvailableAsync(CancellationToken cancellationToken)
    {
        ProviderClientSettings settings = _factory.SettingsFor(Tier.Local);
        string native = NativeBase(settings.Endpoint).AbsoluteUri.TrimEnd('/');
        string unreachable = $"Ollama not reachable at {native}; start it with \"ollama serve\"";
        TimeSpan limit = ModelCatalogClient.PreflightLimit(settings.NetworkTimeout);

        HttpClient client = _httpClients.CreateClient(ProviderClientFactory.HttpClientName(TierOptions.NameOf(Tier.Local)));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(native + VersionRoute));

        // The named client has an infinite timeout (the SDK network timeout is the limit of a completion), so the limit of this one request is applied here.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new EndpointUnreachableException(unreachable);
            }
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            throw new EndpointUnreachableException(unreachable, failure);
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EndpointUnreachableException(unreachable, failure);
        }
    }

    /// <inheritdoc />
    /// <returns><see cref="LocalRuntimeInfo.Unknown"/>: the description of Ollama (version, offload, precision) is filled in by AJ-105.</returns>
    public Task<LocalRuntimeInfo> DescribeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LocalRuntimeInfo.Unknown);
    }

    /// <inheritdoc />
    /// <returns>Null: the warm-up of Ollama is filled in by AJ-106, so no load is observed yet.</returns>
    /// <exception cref="ArgumentException"><paramref name="modelId"/> is null or blank.</exception>
    public Task<TimeSpan?> WarmUpAsync(string modelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<TimeSpan?>(null);
    }
}