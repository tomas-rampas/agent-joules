using System.Text.Json;

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
/// container serves the model from GPU memory only), and the precision is <c>Tiers:local:Precision</c>, which the configuration validator requires for this runtime, so no precision is ever guessed. The warm-up is the
/// no-op behaviour of M2 for now.
/// </summary>
/// <remarks>
/// The request goes through the named <see cref="HttpClient"/> of the local tier, with the time limit of <see cref="OllamaRuntime"/>, and carries no key: the version route needs none. No text of a response leaves this class.
/// </remarks>
/// <param name="factory">The provider client factory; it validates the local tier and gives its endpoint and network timeout.</param>
/// <param name="httpClients">The factory of the named <see cref="HttpClient"/> of each tier.</param>
/// <param name="options">The options, for the configured precision of the local tier.</param>
public sealed class NimRuntime(ProviderClientFactory factory, IHttpClientFactory httpClients, IOptions<AgentJoulesOptions> options) : ILocalRuntime
{
    /// <summary>The name of the runtime, the value of <c>Local:Runtime</c> that selects it.</summary>
    public const string RuntimeName = LocalOptions.Nim;

    /// <summary>The GPU offload of a NIM container in percent: by construction, the container loads the model into GPU memory.</summary>
    public const double GpuOffloadPercent = 100.0;

    private const string VersionRoute = "/version";
    private const int MaxDisclosureLength = 64;

    private readonly ProviderClientFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly IHttpClientFactory _httpClients = httpClients ?? throw new ArgumentNullException(nameof(httpClients));
    private readonly IOptions<AgentJoulesOptions> _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public string Name => RuntimeName;

    /// <inheritdoc />
    /// <exception cref="ConfigurationException">The local tier is not configured or its base URL or timeout is not usable (exit code 2).</exception>
    /// <exception cref="EndpointUnreachableException">The connection was refused, nothing answered within the limit or the status is not a success; the message is <c>NIM not reachable at &lt;BaseUrl&gt;; start the container as recorded in docs/nim-local-attempt.md</c> (exit code 5).</exception>
    public async Task CheckAvailableAsync(CancellationToken cancellationToken)
    {
        (HttpClient client, Uri version, TimeSpan limit, string unreachable) = Prepare();
        _ = await LocalRuntimeHttp.SendAsync(client, HttpMethod.Get, version, jsonBody: null, readBody: false, limit, unreachable, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <returns>The runtime name, the <c>release</c> of the version route, 100 and <c>Tiers:local:Precision</c> (null when it is blank).</returns>
    /// <exception cref="ConfigurationException">The local tier is not configured or its base URL or timeout is not usable (exit code 2).</exception>
    /// <exception cref="EndpointUnreachableException">The request was refused, timed out or answered a status other than success (exit code 5).</exception>
    public async Task<LocalRuntimeInfo> DescribeAsync(CancellationToken cancellationToken)
    {
        (HttpClient client, Uri versionUri, TimeSpan limit, string unreachable) = Prepare();
        string? release;
        using (JsonDocument? answer = await LocalRuntimeHttp.SendAsync(client, HttpMethod.Get, versionUri, jsonBody: null, readBody: true, limit, unreachable, cancellationToken).ConfigureAwait(false))
        {
            release = answer is null ? null : LocalRuntimeHttp.Text(answer.RootElement, "release", MaxDisclosureLength);
        }

        string? precision = _options.Value.Tiers.TryGetValue(TierOptions.NameOf(Tier.Local), out TierOptions? local) && local is not null && !string.IsNullOrWhiteSpace(local.Precision)
            ? local.Precision.Trim()
            : null;

        return new LocalRuntimeInfo(RuntimeName, release, GpuOffloadPercent, precision);
    }

    /// <inheritdoc />
    /// <returns>Null: the warm-up of the NIM container is filled in by AJ-106, so no load is observed yet.</returns>
    /// <exception cref="ArgumentException"><paramref name="modelId"/> is null or blank.</exception>
    public Task<TimeSpan?> WarmUpAsync(string modelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<TimeSpan?>(null);
    }

    // The client, the address of the version route, the time limit and the unreachable message of the local tier; the factory validates the tier.
    private (HttpClient Client, Uri Version, TimeSpan Limit, string Unreachable) Prepare()
    {
        ProviderClientSettings settings = _factory.SettingsFor(Tier.Local);
        string baseUrl = settings.Endpoint.AbsoluteUri.TrimEnd('/');
        HttpClient client = _httpClients.CreateClient(ProviderClientFactory.HttpClientName(TierOptions.NameOf(Tier.Local)));

        return (client, new Uri(baseUrl + VersionRoute), LocalRuntimeHttp.LimitOf(settings), $"NIM not reachable at {baseUrl}; start the container as recorded in docs/nim-local-attempt.md");
    }
}