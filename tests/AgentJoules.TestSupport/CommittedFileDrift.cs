using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit.Sdk;

namespace AgentJoules.TestSupport;

/// <summary>
/// The drift check of a committed JSON file against the text the code generates for it (AJ-079 for <c>docs/results-schema.json</c>, shared with AJ-080 for its committed example documents). The two texts are
/// compared as JSON trees with <see cref="JsonNode.DeepEquals(JsonNode?, JsonNode?)"/>, so a difference in white space, line endings or member order is not drift; a member the model gained, lost or changed is. A failure
/// names the first difference by its JSON pointer and says how to regenerate the file.
/// </summary>
public static class CommittedFileDrift
{
    /// <summary>The environment variable that makes the drift test of <c>docs/results-schema.json</c> rewrite the file (AJ-079).</summary>
    public const string SchemaUpdateVariable = "AGENTJOULES_UPDATE_SCHEMA";

    /// <summary>The sentence that ends the failure of the schema drift test: the one shipped hint, shared by the unit test, the stale-schema test and the Specs scenario.</summary>
    public const string SchemaUpdateHint = $"Regenerate it with {SchemaUpdateVariable}=1 dotnet test --filter \"FullyQualifiedName~CommittedSchemaMatchesGenerated\" and commit the file.";

    /// <summary>Finds the first difference between a committed JSON text and a generated one.</summary>
    /// <param name="committedText">The text of the committed file.</param>
    /// <param name="generatedText">The text the code generates.</param>
    /// <returns>Null when the two trees are equal; otherwise a short description that starts with the JSON pointer of the first difference (<c>(root)</c> for the root itself) and never repeats a value.</returns>
    /// <exception cref="ArgumentNullException">A text is null.</exception>
    /// <exception cref="JsonException"><paramref name="generatedText"/> is not JSON (a defect of the generator, not drift).</exception>
    public static string? Difference(string committedText, string generatedText)
    {
        ArgumentNullException.ThrowIfNull(committedText);
        ArgumentNullException.ThrowIfNull(generatedText);

        JsonNode? generated = JsonNode.Parse(generatedText);
        JsonNode? committed;
        try
        {
            committed = JsonNode.Parse(committedText);
        }
        catch (JsonException)
        {
            return "(root): the committed text is not valid JSON.";
        }

        return JsonNode.DeepEquals(committed, generated) ? null : First(committed, generated, string.Empty);
    }

    /// <summary>Fails when the committed text has drifted from the generated one.</summary>
    /// <param name="committedText">The text of the committed file.</param>
    /// <param name="generatedText">The text the code generates.</param>
    /// <param name="file">The repository-relative name of the committed file, for the message.</param>
    /// <param name="updateHint">The sentence that tells how to regenerate the file, naming the update variable.</param>
    /// <exception cref="XunitException">The trees differ; the message names the file, the first difference and the update hint.</exception>
    public static void AssertNoDrift(string committedText, string generatedText, string file, string updateHint)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(updateHint);

        if (Difference(committedText, generatedText) is { } difference)
        {
            throw new XunitException(string.Create(CultureInfo.InvariantCulture, $"{file} has drifted from what the model generates, first at {difference} {updateHint}"));
        }
    }

    private static string First(JsonNode? committed, JsonNode? generated, string pointer)
    {
        string where = pointer.Length == 0 ? "(root)" : pointer;
        switch (committed, generated)
        {
            case (JsonObject left, JsonObject right):
                foreach (KeyValuePair<string, JsonNode?> member in right)
                {
                    string child = pointer + "/" + Escape(member.Key);
                    if (!left.TryGetPropertyValue(member.Key, out JsonNode? other))
                    {
                        return $"{child}: the committed file lacks this member.";
                    }

                    if (!JsonNode.DeepEquals(other, member.Value))
                    {
                        return First(other, member.Value, child);
                    }
                }

                foreach (KeyValuePair<string, JsonNode?> member in left)
                {
                    if (!right.ContainsKey(member.Key))
                    {
                        return $"{pointer}/{Escape(member.Key)}: the generated text lacks this member.";
                    }
                }

                return $"{where}: the objects differ.";
            case (JsonArray left, JsonArray right):
                if (left.Count != right.Count)
                {
                    return $"{where}: the arrays hold {left.Count} and {right.Count} elements.";
                }

                for (int index = 0; index < left.Count; index++)
                {
                    if (!JsonNode.DeepEquals(left[index], right[index]))
                    {
                        return First(left[index], right[index], pointer + "/" + index.ToString(CultureInfo.InvariantCulture));
                    }
                }

                return $"{where}: the arrays differ.";
            default:
                return $"{where}: the values differ.";
        }
    }

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}