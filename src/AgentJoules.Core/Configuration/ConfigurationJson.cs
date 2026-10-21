using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentJoules.Core.Configuration;

/// <summary>What is wrong with a JSON object read from configuration; the words of the messages belong to the callers, which name their own key path.</summary>
public enum JsonObjectProblem
{
    /// <summary>Nothing: the text is a JSON object whose member names are all different.</summary>
    None = 0,

    /// <summary>The text is not valid JSON or its root is not an object.</summary>
    NotAnObject = 1,

    /// <summary>The root object (or an object inside it) repeats a member name.</summary>
    RepeatedMember = 2,
}

/// <summary>
/// The one reading of a JSON object that comes from configuration (<c>Tiers:&lt;tier&gt;:ExtraBodyJson</c>), shared by the validator that rejects it at start
/// and the request body patch that merges it, so the two cannot disagree about what is valid. A repeated member name is not valid: the parser of the
/// tree would otherwise accept the text and fail on the first merge, in the middle of a measured call.
/// </summary>
public static class ConfigurationJson
{
    /// <summary>The members of a chat request body that <c>ExtraBodyJson</c> may not set at the top level: the call itself (the model of the agent, the prompt), how the answer is read (streaming and its usage), and the comparability fields every call must carry as the task sets them (temperature 0, the seed, the output cap under either name). <c>ExtraBodyJson</c> is for the reasoning toggle of a tier (ADR-12), nothing else.</summary>
    public static IReadOnlyList<string> ReservedBodyMembers { get; } = ["model", "messages", "stream", "stream_options", "temperature", "seed", "max_completion_tokens", "max_tokens"];

    /// <summary>Reads <paramref name="json"/> as a JSON object whose member names are all different.</summary>
    /// <param name="json">The text to read.</param>
    /// <param name="value">The object, or null when there is a problem.</param>
    /// <returns>The problem, or <see cref="JsonObjectProblem.None"/>. The text is never echoed: the message of a parser can quote it.</returns>
    public static JsonObjectProblem Check(string json, out JsonObject? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonObjectProblem.NotAnObject;
        }

        try
        {
            value = JsonNode.Parse(json, nodeOptions: null, new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject;
            return value is not null ? JsonObjectProblem.None : JsonObjectProblem.NotAnObject;
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            // The strict parse failed. Parsing again with duplicates allowed tells a repeated name from text that is not JSON at all.
            value = null;
            return AcceptsRepeats(json) ? JsonObjectProblem.RepeatedMember : JsonObjectProblem.NotAnObject;
        }
    }

    /// <summary>Reads <paramref name="json"/> as a JSON object whose member names are all different (see <see cref="Check"/>).</summary>
    /// <param name="json">The text to read.</param>
    /// <param name="value">The object, or null when the text is not valid.</param>
    /// <returns>True when <paramref name="value"/> holds the object.</returns>
    public static bool TryParseObject(string json, out JsonObject? value) => Check(json, out value) == JsonObjectProblem.None;

    /// <summary>The first member of <paramref name="value"/> whose name is one of <see cref="ReservedBodyMembers"/>, or null.</summary>
    /// <param name="value">The object to look at.</param>
    /// <returns>The reserved name (a fixed word, never configured text), or null.</returns>
    public static string? FirstReservedMember(JsonObject value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return ReservedBodyMembers.FirstOrDefault(name => value.ContainsKey(name));
    }

    private static bool AcceptsRepeats(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = true });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            return false;
        }
    }
}