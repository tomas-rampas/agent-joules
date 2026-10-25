using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.Options;

namespace AgentJoules.Workflow.Local;

/// <summary>
/// The Ollama runtime behind the local tier (EDGE-009, REQ-020, design 3.4 "Local runtimes"). <see cref="CheckAvailableAsync"/> asks the native route <c>GET &lt;native base&gt;/api/version</c>, where the native base
/// is the base URL of the tier without its trailing <c>/v1</c> segment, so a runtime that is not running is reported as <c>Ollama not reachable at &lt;native base&gt;; start it with "ollama serve"</c> (exit code 5)
/// instead of as a bare connection error from the models route. <see cref="DescribeAsync"/> states what the <c>environment</c> of a local run discloses (AJ-105): the version from <c>/api/version</c>, the GPU offload
/// from <c>GET /api/ps</c> and the quantization level from <c>POST /api/show</c>. <see cref="WarmUpAsync"/> (AJ-106) loads a model before the idle baseline with <c>POST &lt;native base&gt;/api/generate</c>, an empty prompt
/// and <c>keep_alive</c> of <see cref="KeepAlive"/>, and returns how long the call took on the injected clock.
/// </summary>
/// <remarks>
/// <para>
/// Every request goes through the named <see cref="HttpClient"/> of the local tier (<see cref="ProviderClientFactory.HttpClientName(string)"/>), the pipeline the completions and the models route use, so a test
/// fake or the deny-all handler of the harness applies to it and a redirect is not followed. It carries no key: the native routes of Ollama need none. Its time limit is the lesser of the
/// <c>TimeoutSeconds</c> of the tier and <see cref="ModelCatalogClient.PreflightTimeoutSeconds"/>, the limit of the models route; the warm-up alone runs under the full <c>TimeoutSeconds</c> of the tier, counted on the injected clock (loading a large model can take longer than 30 s). No text of a response leaves this class: the messages hold the endpoint, which the
/// configuration validator keeps free of user information, query and fragment, the configured tags and a fixed phrase.
/// </para>
/// <para>
/// The GPU offload is <c>size_vram / size x 100</c> of every distinct tag the agents call (<see cref="TierOptions.ResolvedModelIds"/>: the <c>AgentModelIds</c> entry of each agent, else <c>Tiers:local:ModelId</c>; the warm-up loads the same set) in the process list, rounded to one decimal and kept within 0 to 100;
/// the runtime reports the lowest of them, because the worst offload governs the disclosure. A value below 100 is never rounded up to 100, so a model that is partly on the CPU is never presented as a full-GPU one. Nothing is
/// guessed: a tag that is not in the list, a <c>size</c> of 0, none or negative, no or a negative <c>size_vram</c>, or a list that cannot be read stops the run with <see cref="MeasurementPreconditionException"/> (exit code 6). The precision is
/// <c>details.quantization_level</c> of the answer for the first resolved tag (<c>Tiers:local:ModelId</c> when an agent calls it), and null when the answer holds none.
/// </para>
/// </remarks>
/// <param name="factory">The provider client factory; it validates the local tier and gives its endpoint and network timeout.</param>
/// <param name="httpClients">The factory of the named <see cref="HttpClient"/> of each tier.</param>
/// <param name="options">The options, for the configured tags of the local tier.</param>
/// <param name="time">The clock that measures the warm-up call; the system clock when null.</param>
public sealed class OllamaRuntime(ProviderClientFactory factory, IHttpClientFactory httpClients, IOptions<AgentJoulesOptions> options, TimeProvider? time = null) : ILocalRuntime
{
    /// <summary>The name of the runtime, the value of <c>Local:Runtime</c> that selects it.</summary>
    public const string RuntimeName = LocalOptions.Ollama;

    /// <summary>How long Ollama keeps the model loaded after the last request (<c>keep_alive</c> of the warm-up): 30 minutes, longer than the idle baseline, the tasks and the repeats of a run.</summary>
    public const string KeepAlive = "30m";

    /// <summary>The start of every message that says the GPU offload could not be determined (exit code 6).</summary>
    internal const string OffloadUndetermined = "the GPU offload could not be determined";

    private const string V1Segment = "/v1";
    private const string VersionRoute = "/api/version";
    private const string ProcessListRoute = "/api/ps";
    private const string ShowRoute = "/api/show";
    private const string GenerateRoute = "/api/generate";
    private const int MaxDisclosureLength = 64;

    private readonly ProviderClientFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly IHttpClientFactory _httpClients = httpClients ?? throw new ArgumentNullException(nameof(httpClients));
    private readonly IOptions<AgentJoulesOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

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

    /// <summary>
    /// The share of a model held in GPU memory in percent: <c>vram x 100 / size</c> kept within 0 to 100 and rounded to one decimal, a midpoint away from zero (12.25 gives 12.3), and never 100 while
    /// <paramref name="vram"/> is below <paramref name="size"/>. The product is formed before the division, so byte counts that give an exact decimal share (1225 of 10000 is 12.25) are not moved off the midpoint by the representation of 0.1225.
    /// </summary>
    /// <param name="size">The <c>size</c> of the loaded model in bytes; greater than 0.</param>
    /// <param name="vram">The <c>size_vram</c> in bytes; 0 or more.</param>
    /// <returns>The percentage, 0 to 100.</returns>
    internal static double OffloadPercent(double size, double vram)
    {
        double rounded = Math.Round(Math.Clamp(vram * 100.0 / size, 0.0, 100.0), 1, MidpointRounding.AwayFromZero);
        return vram < size && rounded >= 100.0 ? 99.9 : rounded;
    }

    /// <inheritdoc />
    /// <exception cref="ConfigurationException">The local tier is not configured or its base URL or timeout is not usable (exit code 2).</exception>
    /// <exception cref="EndpointUnreachableException">The connection was refused, nothing answered within the limit or the status is not a success; the message is <c>Ollama not reachable at &lt;native base&gt;; start it with "ollama serve"</c> (exit code 5).</exception>
    public async Task CheckAvailableAsync(CancellationToken cancellationToken)
    {
        (HttpClient client, string native, TimeSpan limit, string unreachable) = Prepare();
        _ = await LocalRuntimeHttp.SendAsync(client, HttpMethod.Get, new Uri(native + VersionRoute), jsonBody: null, readBody: false, limit, unreachable, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <returns>The runtime name, the version, the lowest GPU offload over the tags the agents call (<see cref="TierOptions.ResolvedModelIds"/>) and the quantization level of the first of them (<c>Tiers:local:ModelId</c> when an agent calls it); the version and the precision are null when the runtime states none.</returns>
    /// <exception cref="ConfigurationException">The local tier is not configured, has no model tag, or its base URL or timeout is not usable (exit code 2).</exception>
    /// <exception cref="EndpointUnreachableException">A request was refused, timed out or answered a status other than success (exit code 5, the <c>ollama serve</c> message).</exception>
    /// <exception cref="MeasurementPreconditionException">The process list does not state the offload of a configured tag: the tag is not listed, its <c>size</c> is 0, missing or negative, its <c>size_vram</c> is missing or negative, or the list is unreadable (exit code 6).</exception>
    public async Task<LocalRuntimeInfo> DescribeAsync(CancellationToken cancellationToken)
    {
        (HttpClient client, string native, TimeSpan limit, string unreachable) = Prepare();
        string[] tags = ConfiguredTags();

        string? version;
        using (JsonDocument? answer = await LocalRuntimeHttp.SendAsync(client, HttpMethod.Get, new Uri(native + VersionRoute), jsonBody: null, readBody: true, limit, unreachable, cancellationToken).ConfigureAwait(false))
        {
            version = answer is null ? null : LocalRuntimeHttp.Text(answer.RootElement, "version", MaxDisclosureLength);
        }

        double offload;
        using (JsonDocument? processes = await LocalRuntimeHttp.SendAsync(client, HttpMethod.Get, new Uri(native + ProcessListRoute), jsonBody: null, readBody: true, limit, unreachable, cancellationToken).ConfigureAwait(false))
        {
            offload = LowestOffload(processes, tags);
        }

        string showBody = new JsonObject { ["model"] = tags[0] }.ToJsonString();
        string? precision;
        using (JsonDocument? shown = await LocalRuntimeHttp.SendAsync(client, HttpMethod.Post, new Uri(native + ShowRoute), showBody, readBody: true, limit, unreachable, cancellationToken).ConfigureAwait(false))
        {
            precision = shown is { RootElement: { ValueKind: JsonValueKind.Object } root } && root.TryGetProperty("details", out JsonElement details)
                ? LocalRuntimeHttp.Text(details, "quantization_level", MaxDisclosureLength)
                : null;
        }

        return new LocalRuntimeInfo(RuntimeName, version, offload, precision);
    }

    /// <inheritdoc />
    /// <returns>The wall time of the load, measured on the injected clock around the call.</returns>
    /// <exception cref="ArgumentException"><paramref name="modelId"/> is null or blank.</exception>
    /// <exception cref="ConfigurationException">The local tier is not configured or its base URL or timeout is not usable (exit code 2).</exception>
    /// <exception cref="EndpointUnreachableException">The connection was refused or nothing answered within the timeout of the tier (the <c>ollama serve</c> message), or the runtime answered a status other than success (<c>the warm-up of &lt;tag&gt; failed with HTTP &lt;status&gt; at &lt;native base&gt;</c>, never the body); exit code 5.</exception>
    public async Task<TimeSpan?> WarmUpAsync(string modelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        cancellationToken.ThrowIfCancellationRequested();

        (HttpClient client, string native, _, string unreachable) = Prepare();
        TimeSpan limit = LocalRuntimeHttp.LoadLimitOf(_factory.SettingsFor(Tier.Local));
        string body = new JsonObject { ["model"] = modelId, ["prompt"] = string.Empty, ["keep_alive"] = KeepAlive, ["stream"] = false }.ToJsonString();
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(native + GenerateRoute)) { Content = LocalRuntimeHttp.JsonContent(body) };

        // A running Ollama that answers an error is not a stopped one: the message names the tag, the status and the endpoint, not "ollama serve".
        return await LocalRuntimeHttp.TimeWarmUpAsync(_time, client, request, limit, unreachable, status => LocalRuntimeHttp.WarmUpFailed(modelId, status, native), cancellationToken).ConfigureAwait(false);
    }

    // The client, the native base without a trailing slash, the time limit and the unreachable message of the local tier; the factory validates the tier.
    private (HttpClient Client, string Native, TimeSpan Limit, string Unreachable) Prepare()
    {
        ProviderClientSettings settings = _factory.SettingsFor(Tier.Local);
        string native = NativeBase(settings.Endpoint).AbsoluteUri.TrimEnd('/');
        HttpClient client = _httpClients.CreateClient(ProviderClientFactory.HttpClientName(TierOptions.NameOf(Tier.Local)));

        return (client, native, LocalRuntimeHttp.LimitOf(settings), $"Ollama not reachable at {native}; start it with \"ollama serve\"");
    }

    // The tags the agents call, TierOptions.ResolvedModelIds(): the AgentModelIds entry of each agent else Tiers:local:ModelId, each tag once (ordinal), Tiers:local:ModelId first when an agent uses it.
    private string[] ConfiguredTags()
    {
        TierOptions tier = _options.Value.Tiers.TryGetValue(TierOptions.NameOf(Tier.Local), out TierOptions? configured) && configured is not null
            ? configured
            : throw new ConfigurationException("Tiers:local is not configured");

        // The same set the warm-up loads (the orchestrator takes it from here too): the model of each agent, each tag once, Tiers:local:ModelId first when an agent uses it.
        return [.. tier.ResolvedModelIds()];
    }

    // The lowest offload over the tags, in the order of the tags; the first tag that has no usable entry ends the run, naming the tag and the route.
    private static double LowestOffload(JsonDocument? processes, string[] tags)
    {
        if (processes is not { RootElement: { ValueKind: JsonValueKind.Object } root } || !root.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new MeasurementPreconditionException($"{OffloadUndetermined}: GET {ProcessListRoute} did not answer a list of models");
        }

        double lowest = double.PositiveInfinity;
        foreach (string tag in tags)
        {
            JsonElement? entry = null;
            foreach (JsonElement candidate in models.EnumerateArray())
            {
                if (LoadedAs(candidate, "name", tag) || LoadedAs(candidate, "model", tag))
                {
                    entry = candidate;
                    break;
                }
            }

            if (entry is not { } loaded)
            {
                throw new MeasurementPreconditionException($"{OffloadUndetermined}: {tag} is not loaded (GET {ProcessListRoute})");
            }

            // Each message names the actual cause: a size that is missing, not a number or negative, a size of 0, a size_vram that is missing or not a number, a negative size_vram.
            if (!Bytes(loaded, "size", out double size) || size < 0.0)
            {
                throw new MeasurementPreconditionException($"{OffloadUndetermined}: {tag} reports no size (GET {ProcessListRoute})");
            }

            if (size == 0.0)
            {
                throw new MeasurementPreconditionException($"{OffloadUndetermined}: {tag} reports size 0 (GET {ProcessListRoute})");
            }

            if (!Bytes(loaded, "size_vram", out double vram))
            {
                throw new MeasurementPreconditionException($"{OffloadUndetermined}: {tag} reports no size_vram (GET {ProcessListRoute})");
            }

            if (vram < 0.0)
            {
                throw new MeasurementPreconditionException($"{OffloadUndetermined}: {tag} reports a negative size_vram (GET {ProcessListRoute})");
            }

            lowest = Math.Min(lowest, OffloadPercent(size, vram));
        }

        return lowest;
    }

    private static bool LoadedAs(JsonElement entry, string member, string tag)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(member, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            return string.Equals(value.GetString(), tag, StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            // An entry whose name cannot be read as text (a lone surrogate escape, invalid UTF-8) names no tag.
            return false;
        }
    }

    private static bool Bytes(JsonElement entry, string member, out double bytes)
    {
        bytes = 0.0;
        return entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(member, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out bytes) && double.IsFinite(bytes);
    }
}