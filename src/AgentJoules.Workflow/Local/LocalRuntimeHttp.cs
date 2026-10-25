using System.Text;
using System.Text.Json;

using AgentJoules.Core.Cli;
using AgentJoules.Workflow.Providers;

namespace AgentJoules.Workflow.Local;

/// <summary>
/// The one request path of the local runtimes (<see cref="OllamaRuntime"/>, <see cref="NimRuntime"/>): a request through the named <see cref="HttpClient"/> of the local tier, bounded by the time limit of the
/// preflight, whose failure is an <see cref="EndpointUnreachableException"/> that holds the fixed message of the runtime and never a text of the response.
/// </summary>
/// <remarks>
/// The named client has an infinite timeout (the SDK network timeout is the limit of a completion), so the limit of each request is applied here. The request carries no key. A response body is read up to
/// <see cref="MaxBodyBytes"/>; a body that is larger, is not JSON or ends early is reported as no document (null), because what the caller does with a missing answer differs by call.
/// </remarks>
internal static class LocalRuntimeHttp
{
    /// <summary>The most bytes of a response body that are read: an answer of <c>/api/show</c> with its license and template is well below it.</summary>
    internal const int MaxBodyBytes = 4 * 1024 * 1024;

    private const int ReadChunkBytes = 16 * 1024;

    /// <summary>Sends one request and reads its JSON body when asked to.</summary>
    /// <param name="client">The named client of the local tier.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="uri">The absolute address.</param>
    /// <param name="jsonBody">The JSON text of a request body, or null for none.</param>
    /// <param name="readBody">True to read and parse the body; false to look at the status only (the body is not read).</param>
    /// <param name="limit">The time limit of the request.</param>
    /// <param name="unreachable">The message of the <see cref="EndpointUnreachableException"/>.</param>
    /// <param name="cancellationToken">Cancels the request; a cancellation by the caller is not an unreachable runtime.</param>
    /// <returns>The parsed body (the caller disposes it), or null when the body was not read, is too large or is not JSON.</returns>
    /// <exception cref="EndpointUnreachableException">The connection was refused, nothing answered within the limit or the status is not a success.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    internal static async Task<JsonDocument?> SendAsync(HttpClient client, HttpMethod method, Uri uri, string? jsonBody, bool readBody, TimeSpan limit, string unreachable, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return !response.IsSuccessStatusCode
                ? throw new EndpointUnreachableException(unreachable)
                : readBody ? await ParseAsync(response, timeout.Token).ConfigureAwait(false) : null;
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

    // A bounded read: a body past the cap is not parsed. The stream errors (IOException, HttpRequestException) belong to the caller's translation; a JSON syntax error is a missing answer.
    private static async Task<JsonDocument?> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffered = new MemoryStream();
        byte[] chunk = new byte[ReadChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffered.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffered.Write(chunk, 0, read);
        }

        buffered.Position = 0;
        try
        {
            return JsonDocument.Parse(buffered);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a string member of a JSON object for a disclosure: trimmed, at most <paramref name="maxLength"/> characters, no control character; anything else is not a value (null), never a guess.</summary>
    /// <param name="element">The element that should be an object holding the member.</param>
    /// <param name="name">The member name.</param>
    /// <param name="maxLength">The longest accepted text.</param>
    /// <returns>The text, or null when the member is absent, not a string, cannot be read as text, is blank, is too long or holds a control character.</returns>
    internal static string? Text(JsonElement element, string name, int maxLength)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement member) || member.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string text;
        try
        {
            text = (member.GetString() ?? string.Empty).Trim();
        }
        catch (InvalidOperationException)
        {
            // A string the document cannot transcode (a lone surrogate escape, invalid UTF-8) is no value.
            return null;
        }

        return text.Length is 0 || text.Length > maxLength || text.Any(char.IsControl) ? null : text;
    }

    /// <summary>The time limit of a request of the local tier: the lesser of the tier's timeout and the preflight limit.</summary>
    /// <param name="settings">The settings of the local tier.</param>
    /// <returns>The limit.</returns>
    internal static TimeSpan LimitOf(ProviderClientSettings settings) => ModelCatalogClient.PreflightLimit(settings.NetworkTimeout);
}