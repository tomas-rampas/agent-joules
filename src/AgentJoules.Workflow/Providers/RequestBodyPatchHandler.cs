using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;

namespace AgentJoules.Workflow.Providers;

/// <summary>
/// The HTTP handler that adapts the body of a chat completion request to the endpoint of one tier (REQ-012, design 3.4, ADR-12). It acts only on a
/// <c>POST</c> whose path ends in <c>/chat/completions</c>; every other request (<c>GET .../models</c>, an embeddings call) passes through without its
/// body being read.
/// </summary>
/// <remarks>
/// <para>
/// Two changes, in this order. First, when <c>Tiers:&lt;tier&gt;:MaxTokensField</c> is <c>max_tokens</c>, the property the OpenAI SDK writes for the output
/// cap (<c>max_completion_tokens</c>) is renamed to <c>max_tokens</c>; with the other value the body keeps the SDK name. Second, the
/// <c>ExtraBodyJson</c> object is deep-merged into the body: an object member present on both sides is merged member by member, in every other case
/// (a scalar, an array, an object against a non-object, a null) the member of the extra object replaces the member of the body, so an array is replaced
/// as a whole and the extra object wins every conflict, including a cap the rename just wrote.
/// </para>
/// <para>
/// The body is read, changed and written again as one buffer with <see cref="JsonNode"/>, and the replacement content carries the media type and
/// every other content header of the original and a length that the content computes itself. The body may carry a prompt and the request headers
/// carry the key, so nothing here logs, stores or echoes either: a failure names the problem, never the content. A tier with no extra object and
/// the default field name never has its body read. Both <c>Send</c> and <c>SendAsync</c> are overridden, so a synchronous call cannot bypass the
/// patch.
/// </para>
/// </remarks>
public sealed class RequestBodyPatchHandler : DelegatingHandler
{
    private const string ChatCompletionsSuffix = "/chat/completions";
    private const string SdkCapField = "max_completion_tokens";

    private readonly JsonObject? _extraBody;
    private readonly string _maxTokensField;

    /// <summary>Creates a handler; the inner handler is set by the pipeline that hosts it.</summary>
    /// <param name="extraBodyJson">The <c>ExtraBodyJson</c> of the tier, or null or blank for none; must be a JSON object.</param>
    /// <param name="maxTokensField">The <c>MaxTokensField</c> of the tier: <c>max_completion_tokens</c> or <c>max_tokens</c>.</param>
    /// <param name="keyPath">The configuration key the extra object came from, named by a failure message; the default is the bare key name.</param>
    /// <exception cref="ArgumentException"><paramref name="maxTokensField"/> is null or blank.</exception>
    /// <exception cref="ConfigurationException"><paramref name="maxTokensField"/> is not one of <see cref="TierOptions.MaxTokensFields"/>, or <paramref name="extraBodyJson"/> is not a JSON object, repeats a member name, or sets a member of <see cref="ConfigurationJson.ReservedBodyMembers"/> (the message does not show the text).</exception>
    public RequestBodyPatchHandler(string? extraBodyJson, string maxTokensField, string keyPath = "ExtraBodyJson")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maxTokensField);

        if (!TierOptions.MaxTokensFields.Contains(maxTokensField, StringComparer.Ordinal))
        {
            throw new ConfigurationException("MaxTokensField must be max_completion_tokens or max_tokens");
        }

        _maxTokensField = maxTokensField;
        _extraBody = string.IsNullOrWhiteSpace(extraBodyJson) ? null : ParseExtraBody(extraBodyJson, keyPath);
    }

    /// <summary>Creates the handler of one tier from its options.</summary>
    /// <remarks>The key path of a failure message is <c>Tiers:&lt;tierName&gt;:ExtraBodyJson</c> when <paramref name="tierName"/> is given.</remarks>
    /// <param name="tier">The options of the tier, or null for a tier that is not configured (the defaults: no extra object, the SDK field name).</param>
    /// <param name="tierName">The tier name, for the key path of a failure message, or null.</param>
    /// <returns>The handler.</returns>
    /// <exception cref="ConfigurationException">The extra object or the field name is invalid.</exception>
    public static RequestBodyPatchHandler For(TierOptions? tier, string? tierName = null) =>
        tier is null
            ? new RequestBodyPatchHandler(null, SdkCapField)
            : new RequestBodyPatchHandler(tier.ExtraBodyJson, tier.MaxTokensField, tierName is null ? "ExtraBodyJson" : "Tiers:" + tierName + ":ExtraBodyJson");

    /// <summary>Deep-merges <paramref name="patch"/> into <paramref name="target"/> (see the class remarks for the rules); <paramref name="patch"/> is not changed or adopted, its nodes are cloned.</summary>
    /// <param name="target">The object that receives the members.</param>
    /// <param name="patch">The object that holds the members to merge.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void Merge(JsonObject target, JsonObject patch)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(patch);

        foreach ((string name, JsonNode? value) in patch)
        {
            if (value is JsonObject patchObject && target[name] is JsonObject targetObject)
            {
                Merge(targetObject, patchObject);
            }
            else
            {
                target[name] = value?.DeepClone();
            }
        }
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (NeedsPatch(request))
        {
            HttpContent original = request.Content!;
            byte[] body = await original.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            Replace(request, original, Patch(body));
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (NeedsPatch(request))
        {
            HttpContent original = request.Content!;
            using var buffer = new MemoryStream();
            using (Stream stream = original.ReadAsStream(cancellationToken))
            {
                stream.CopyTo(buffer);
            }

            Replace(request, original, Patch(buffer.ToArray()));
        }

        return base.Send(request, cancellationToken);
    }

    private static JsonObject ParseExtraBody(string json, string keyPath)
    {
        // Shared with the validator (ConfigurationJson): invalid JSON, a non-object and a repeated member name are one failure, and the text is never echoed.
        JsonObjectProblem problem = ConfigurationJson.Check(json, out JsonObject? extra);
        if (problem != JsonObjectProblem.None)
        {
            throw new ConfigurationException(keyPath + (problem == JsonObjectProblem.RepeatedMember ? " must not repeat a member name" : " must be a JSON object") + "; the value is not shown");
        }

        // The second line of defence behind the validator: the call itself (model, messages, streaming) is not the configuration's to change.
        return ConfigurationJson.FirstReservedMember(extra!) is { } reserved
            ? throw new ConfigurationException(keyPath + " must not set the member " + reserved)
            : extra!;
    }

    private static bool IsChatCompletion(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post
        && request.RequestUri is { } uri
        && uri.AbsolutePath.EndsWith(ChatCompletionsSuffix, StringComparison.Ordinal);

    private bool NeedsPatch(HttpRequestMessage request) =>
        request.Content is not null
        && IsChatCompletion(request)
        && (_extraBody is not null || !string.Equals(_maxTokensField, SdkCapField, StringComparison.Ordinal));

    private byte[] Patch(byte[] body)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject
                ?? throw new InvalidOperationException("The chat completion request body is not a JSON object, so it cannot be adapted to the tier.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The chat completion request body is not valid JSON, so it cannot be adapted to the tier.");
        }

        if (!string.Equals(_maxTokensField, SdkCapField, StringComparison.Ordinal) && root.Remove(SdkCapField, out JsonNode? cap))
        {
            root[_maxTokensField] = cap;
        }

        if (_extraBody is not null)
        {
            Merge(root, _extraBody);
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static void Replace(HttpRequestMessage request, HttpContent original, byte[] patched)
    {
        var replacement = new ByteArrayContent(patched);
        foreach (KeyValuePair<string, IEnumerable<string>> header in original.Headers)
        {
            if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        replacement.Headers.ContentType ??= new MediaTypeHeaderValue("application/json");
        request.Content = replacement;
        original.Dispose();
    }
}