using System.Text.Json;

using Json.Schema;

namespace AgentJoules.TestSupport;

/// <summary>
/// The price-file pieces that the unit tests and the BDD steps of AJ-070 (REQ-009) share: a builder for one row object as JSON text, and the committed
/// <c>config/prices.schema.json</c> loaded once for the whole test process. A schema with an <c>$id</c> registers itself in a global registry and a second
/// <c>JsonSchema.FromText</c> of the same <c>$id</c> is refused, which is why the schema is held in a process-wide <see cref="Lazy{T}"/>.
/// </summary>
public static class PriceFileFixtures
{
    /// <summary>The tier of the default row, as a JSON string value.</summary>
    public const string TierNebius = "\"nebius\"";

    /// <summary>The plain text of the tier of the default row.</summary>
    public const string Tier = "nebius";

    /// <summary>The model ID of the default row, as a JSON string value.</summary>
    public const string ModelValue = "\"vendor/model-a\"";

    /// <summary>The plain text of the model ID of the default row.</summary>
    public const string ModelId = "vendor/model-a";

    /// <summary>The source URL of the default row, as a JSON string value.</summary>
    public const string SourceValue = "\"https://example.invalid/prices\"";

    /// <summary>The retrieval date of the default row, as a JSON string value.</summary>
    public const string DateValue = "\"2026-10-22\"";

    // Parsed once for the process: see the class remarks.
    private static readonly Lazy<JsonSchema> CommittedSchemaOnce = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(ConfigurationFixtures.RepositoryRoot(), "config", "prices.schema.json"))));

    /// <summary>
    /// Builds one row object as JSON text. Every parameter is a raw JSON value, placed as it is (a string needs its quotes, see <see cref="Quote"/>); a null
    /// parameter leaves that member out, and the text <c>null</c> writes a JSON null.
    /// </summary>
    /// <param name="tier">The raw value of <c>tier</c>.</param>
    /// <param name="modelId">The raw value of <c>modelId</c>.</param>
    /// <param name="inputUsdPer1M">The raw value of <c>inputUsdPer1M</c>.</param>
    /// <param name="outputUsdPer1M">The raw value of <c>outputUsdPer1M</c>.</param>
    /// <param name="sourceUrl">The raw value of <c>sourceUrl</c>.</param>
    /// <param name="retrievedDate">The raw value of <c>retrievedDate</c>.</param>
    /// <param name="extra">Raw members appended after the six, each starting with a comma, for example <c>,"extra":1</c>.</param>
    /// <returns>The row as JSON text.</returns>
    public static string Row(
        string? tier = TierNebius,
        string? modelId = ModelValue,
        string? inputUsdPer1M = "0.06",
        string? outputUsdPer1M = "0.24",
        string? sourceUrl = SourceValue,
        string? retrievedDate = DateValue,
        string extra = "")
    {
        var members = new List<string>();
        Add(members, "tier", tier);
        Add(members, "modelId", modelId);
        Add(members, "inputUsdPer1M", inputUsdPer1M);
        Add(members, "outputUsdPer1M", outputUsdPer1M);
        Add(members, "sourceUrl", sourceUrl);
        Add(members, "retrievedDate", retrievedDate);

        return "{" + string.Join(",", members) + extra + "}";
    }

    /// <summary>Writes <paramref name="text"/> as a JSON string value: quoted, with quotes, control characters and non-ASCII characters escaped.</summary>
    /// <param name="text">The plain text.</param>
    /// <returns>The raw JSON string value.</returns>
    public static string Quote(string text) => JsonSerializer.Serialize(text);

    /// <summary>Evaluates a document against the committed price schema with the format keywords (date) enforced, as design 3.5 describes for <c>OutputFormat.List</c>.</summary>
    /// <param name="json">The document as JSON text.</param>
    /// <returns>The evaluation result.</returns>
    public static EvaluationResults Evaluate(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using JsonDocument document = JsonDocument.Parse(json);
        return CommittedSchemaOnce.Value.Evaluate(document.RootElement.Clone(), new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
    }

    /// <summary>The instance locations and error texts of the failed checks of an <see cref="OutputFormat.List"/> result, one per line.</summary>
    /// <param name="result">The evaluation result.</param>
    /// <returns>The findings as text.</returns>
    public static string Findings(EvaluationResults result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return string.Join(
            "\n",
            (result.Details ?? []).Where(detail => !detail.IsValid)
                .Select(detail => detail.InstanceLocation + " " + (detail.Errors is null ? string.Empty : string.Join(" ", detail.Errors.Values))));
    }

    private static void Add(List<string> members, string name, string? rawValue)
    {
        if (rawValue is not null)
        {
            members.Add($"\"{name}\":{rawValue}");
        }
    }
}