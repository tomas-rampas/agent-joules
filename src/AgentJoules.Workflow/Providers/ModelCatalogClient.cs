using System.Globalization;
using System.Text.Json;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;

namespace AgentJoules.Workflow.Providers;

/// <summary>
/// Lists the model IDs a tier serves (EDGE-008, design 3.4 and 3.7): <c>GET {BaseUrl}/models</c> with the bearer key of the tier, parsed as the OpenAI list
/// <c>{"data":[{"id":"..."}]}</c>. The preflight asks it before any completion call, so a typo or a retired model costs no quota and no time.
/// </summary>
/// <remarks>
/// <para>
/// The request is built by the <see cref="ProviderClientFactory"/> (<c>CreateModelsRequest</c>): its base URL, its https-or-loopback rule for a keyed tier and its bearer header are the
/// ones of the completions, reused and not copied, and the key is read there by the NAME in <c>ApiKeyVariable</c> and never reaches this class. A tier without <c>ApiKeyVariable</c> sends no
/// <c>Authorization</c> header, and a variable with no value is refused with <see cref="MissingKeyException"/> before any request. The request goes through the named <see cref="HttpClient"/> of
/// the tier (<see cref="ProviderClientFactory.HttpClientName(string)"/>), the same pipeline the completions use, so a test fake or the deny-all handler of the harness applies to it and a
/// redirect is not followed. Its time limit is <see cref="PreflightTimeoutSeconds"/> or the <c>TimeoutSeconds</c> of the tier when that is less.
/// </para>
/// <para>
/// No text of a response leaves this class raw. A failure is an <see cref="EndpointUnreachableException"/> whose message holds the tier and the route, a status number or a fixed phrase and
/// never a byte of the body (a rejected call can echo the key), and the IDs as the provider wrote them stay inside the returned <see cref="ModelCatalog"/>, which only compares them, and the IDs it shows have gone through
/// <see cref="ModelIdListFormatter.Echo(SecretRedactor, string)"/>: masked by the redactor, cut at 64 characters, control characters replaced. The body read is bounded, so an endpoint cannot make the preflight hold an unbounded answer.
/// </para>
/// </remarks>
/// <param name="factory">The provider client factory; it validates a tier and builds the request with the key in its header.</param>
/// <param name="httpClients">The factory of the named <see cref="HttpClient"/> of each tier.</param>
/// <param name="redactor">The redactor that masks every key value in provider text.</param>
public sealed class ModelCatalogClient(ProviderClientFactory factory, IHttpClientFactory httpClients, SecretRedactor redactor)
{
    /// <summary>
    /// The longest the preflight waits for the models route of one tier, in seconds: the lesser of this and <c>TimeoutSeconds</c> of the tier is the limit of the request. The completion
    /// timeout of a tier (300 seconds by default) is meant for a model that is generating; a models list that is not answered in 30 seconds means the endpoint is not usable, and the
    /// preflight must not hold a dry run for minutes. It also keeps a huge <c>TimeoutSeconds</c> out of the range of <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>.
    /// </summary>
    public const int PreflightTimeoutSeconds = 30;

    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private const int ReadChunkBytes = 16 * 1024;
    private const string Route = "GET /models";

    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    private readonly ProviderClientFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly IHttpClientFactory _httpClients = httpClients ?? throw new ArgumentNullException(nameof(httpClients));
    private readonly SecretRedactor _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));

    /// <summary>The base URL of <paramref name="tier"/>, validated by the rule of the provider client factory; the endpoint the models route is asked at.</summary>
    /// <param name="tier">A defined tier.</param>
    /// <returns>The base URL (it ends in <c>/v1</c> by convention).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a defined tier.</exception>
    /// <exception cref="ConfigurationException">The tier is not configured, or its base URL or timeout is not usable.</exception>
    public Uri EndpointOf(Tier tier) => _factory.SettingsFor(tier).Endpoint;

    // The limit of the request: the timeout of the tier, but never more than the preflight limit.
    internal static TimeSpan PreflightLimit(TimeSpan networkTimeout)
    {
        var limit = TimeSpan.FromSeconds(PreflightTimeoutSeconds);
        return networkTimeout < limit ? networkTimeout : limit;
    }

    /// <summary>Lists the model IDs <paramref name="tier"/> serves.</summary>
    /// <param name="tier">A defined tier.</param>
    /// <param name="cancellationToken">Cancels the request; a cancellation by the caller is not an unreachable endpoint.</param>
    /// <returns>The catalog of the endpoint: the exact IDs for comparison and the echoed IDs for display; empty when the endpoint listed none. Entries without a non-empty string <c>id</c> are skipped.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a defined tier.</exception>
    /// <exception cref="ConfigurationException">The tier is not configured, its base URL or timeout is not usable, or it has a key and its base URL is plain http on a host that is not loopback.</exception>
    /// <exception cref="MissingKeyException">The tier names a key variable that has no value (exit code 3).</exception>
    /// <exception cref="EndpointUnreachableException">The connection failed, the endpoint did not answer within <see cref="PreflightTimeoutSeconds"/> (or <c>TimeoutSeconds</c> when that is less), it answered a status other than success, or its answer is not a model list (exit code 5).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ModelCatalog> ListAsync(Tier tier, CancellationToken cancellationToken)
    {
        string name = TierOptions.NameOf(tier);

        // The factory builds the request with the key in its header (or none) and validates the tier; the key itself never reaches this class.
        using HttpRequestMessage request = _factory.CreateModelsRequest(tier);
        TimeSpan limit = PreflightLimit(_factory.SettingsFor(tier).NetworkTimeout);

        HttpClient client = _httpClients.CreateClient(ProviderClientFactory.HttpClientName(name));

        // The named client has an infinite timeout (the SDK network timeout is the limit of a completion), so the limit of this one request is applied here.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new EndpointUnreachableException(string.Create(CultureInfo.InvariantCulture, $"tier {name}: {Route} answered HTTP {(int)response.StatusCode}"));
            }

            byte[]? body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            List<string>? parsed = body is null ? null : ParseIds(body);
            return parsed is null ? throw new EndpointUnreachableException($"tier {name}: unreadable model list ({Route})") : new ModelCatalog(parsed, _redactor);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            throw new EndpointUnreachableException($"tier {name}: models endpoint unreachable ({Route})", failure);
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EndpointUnreachableException(
                string.Create(CultureInfo.InvariantCulture, $"tier {name}: models endpoint did not answer, timed out after {limit.TotalSeconds:0.###} s ({Route})"),
                failure);
        }
    }

    // The body, or null when it is larger than the bound.
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[ReadChunkBytes];

        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // The raw IDs of {"data":[{"id":"..."}]}, or null when the body is not that shape. The IDs are the provider's text as written; they stay inside ModelCatalog, which only compares them and shows echoed forms. Nothing of the body is kept or reported but the IDs.
    private static List<string>? ParseIds(byte[] body)
    {
        try
        {
            // JsonDocument.Parse does not accept a UTF-8 byte order mark (measured); a server may send one, so it is skipped.
            int offset = body.AsSpan().StartsWith(Utf8ByteOrderMark) ? Utf8ByteOrderMark.Length : 0;
            using JsonDocument document = JsonDocument.Parse(body.AsMemory(offset));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<string> ids = [];
            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 } text)
                {
                    ids.Add(text);
                }
            }

            return ids;
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException)
        {
            // Not JSON, or JSON with bytes that are not text: the body is not a model list. The exception is dropped on purpose; its message can quote the body.
            return null;
        }
    }
}