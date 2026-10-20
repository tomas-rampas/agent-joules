using System.Text;
using System.Text.Json.Nodes;

namespace AgentJoules.TestSupport;

/// <summary>
/// Builds the body of a streaming chat completion the way the OpenAI endpoints and build.nvidia.com send it (design 6.1): one <c>data: {...}</c>
/// event per <c>chat.completion.chunk</c> object, each ended by a blank line, and the closing <c>data: [DONE]</c>. A normal stream is a role chunk, text
/// delta chunks, a chunk with a finish reason, an optional chunk with empty <c>choices</c> that holds the usage (<c>prompt_tokens</c>,
/// <c>completion_tokens</c>, <c>total_tokens</c>) and the terminator. The real OpenAI SDK and Microsoft.Extensions.AI.OpenAI parse it, so a test
/// that reads a usage from it proves the parsing path of production code.
/// </summary>
public sealed class SseBuilder
{
    private const long Created = 1_700_000_000;

    private readonly StringBuilder _body = new();
    private readonly string _model;
    private readonly string _id;

    /// <summary>Creates an empty stream for <paramref name="model"/>.</summary>
    /// <param name="model">The <c>model</c> every chunk carries.</param>
    /// <param name="id">The <c>id</c> every chunk carries.</param>
    public SseBuilder(string model = "fake-model", string id = "chatcmpl-fake")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        _model = model;
        _id = id;
    }

    /// <summary>Adds the first chunk: the assistant role with empty content.</summary>
    /// <returns>This builder.</returns>
    public SseBuilder Role() => Chunk(new JsonObject { ["role"] = "assistant", ["content"] = string.Empty }, finishReason: null);

    /// <summary>Adds a chunk with a text delta.</summary>
    /// <param name="delta">The text of the delta.</param>
    /// <returns>This builder.</returns>
    public SseBuilder Text(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        return Chunk(new JsonObject { ["content"] = delta }, finishReason: null);
    }

    /// <summary>Adds the chunk with an empty delta and a finish reason.</summary>
    /// <param name="reason">The finish reason, for example <c>stop</c> or <c>length</c>.</param>
    /// <returns>This builder.</returns>
    public SseBuilder Finish(string reason = "stop")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return Chunk([], reason);
    }

    /// <summary>Adds the usage chunk: <c>choices</c> is empty and <c>usage</c> holds the counts, as a server does when the request asked for usage in the stream.</summary>
    /// <param name="promptTokens">The <c>prompt_tokens</c>.</param>
    /// <param name="completionTokens">The <c>completion_tokens</c>.</param>
    /// <returns>This builder.</returns>
    public SseBuilder Usage(int promptTokens, int completionTokens)
    {
        JsonObject chunk = Envelope([]);
        chunk["usage"] = new JsonObject
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = promptTokens + completionTokens,
        };

        return Event(chunk.ToJsonString());
    }

    /// <summary>Adds the terminator <c>data: [DONE]</c>.</summary>
    /// <returns>This builder.</returns>
    public SseBuilder Done() => Event("[DONE]");

    /// <summary>The stream so far: every event followed by a blank line.</summary>
    /// <returns>The body text.</returns>
    public string Build() => _body.ToString();

    /// <summary>Builds a complete stream: role, one chunk per delta, the finish chunk, the usage chunk when both counts are given, and the terminator.</summary>
    /// <param name="model">The <c>model</c> of every chunk.</param>
    /// <param name="deltas">The text deltas, in order.</param>
    /// <param name="promptTokens">The <c>prompt_tokens</c>, or null to send no usage chunk.</param>
    /// <param name="completionTokens">The <c>completion_tokens</c>, or null to send no usage chunk.</param>
    /// <returns>The body text.</returns>
    public static string Standard(string model, IEnumerable<string> deltas, int? promptTokens, int? completionTokens)
    {
        ArgumentNullException.ThrowIfNull(deltas);

        SseBuilder builder = new SseBuilder(model).Role();
        foreach (string delta in deltas)
        {
            builder.Text(delta);
        }

        builder.Finish();
        if (promptTokens is { } prompt && completionTokens is { } completion)
        {
            builder.Usage(prompt, completion);
        }

        return builder.Done().Build();
    }

    private SseBuilder Chunk(JsonObject delta, string? finishReason)
    {
        var choice = new JsonObject
        {
            ["index"] = 0,
            ["delta"] = delta,
            ["finish_reason"] = finishReason,
        };

        return Event(Envelope(new JsonArray(choice)).ToJsonString());
    }

    private JsonObject Envelope(JsonArray choices) =>
        new()
        {
            ["id"] = _id,
            ["object"] = "chat.completion.chunk",
            ["created"] = Created,
            ["model"] = _model,
            ["choices"] = choices,
        };

    private SseBuilder Event(string data)
    {
        _ = _body.Append("data: ").Append(data).Append("\n\n");
        return this;
    }
}