using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Json.Schema;

namespace AgentJoules.Reporting.Schema;

/// <summary>One failed check of a document against the results schema.</summary>
/// <param name="InstanceLocation">
/// The JSON pointer of the part of the document that failed, <c>/steps/1/cost</c> for example; empty for the document itself. It holds the member names of the document verbatim (RFC 6901 escaping
/// only, so a very long name, a control character or a bidirectional override survives): a caller that prints it must bound and clean it first (<c>TextEcho.ForMessage(location, 256)</c> and a cap on the lines per file).
/// </param>
/// <param name="Message">What the check found; it names members and rules and repeats no string of the document. A range failure states the number (for example <c>-1 should be at least 0</c>).</param>
public sealed record SchemaFailure(string InstanceLocation, string Message);

/// <summary>The kind of file a document is checked as.</summary>
public enum SchemaTarget
{
    /// <summary>A run file (<c>&lt;timestamp&gt;-&lt;tier&gt;[-baseline].json</c>): the whole schema.</summary>
    RunFile = 1,

    /// <summary>A probe file (<c>probe-&lt;timestamp&gt;.json</c>): the <c>$defs/probe</c> definition.</summary>
    Probe = 2,
}

/// <summary>
/// Checks a document against the results schema (AJ-079, REQ-015, design 3.5). The schema is the embedded resource of this assembly, never the file under <c>docs/</c>, so validation works on a machine without
/// that folder; it is parsed once for the process. The schema is the floor of the checks: it states the types, names, vocabularies, label rules and ranges; the model states what JSON Schema cannot (the energy shape
/// and hosted-tier rules, flag pairings, the pairing of the judge readings, a cost map entry in the currency of its key, a duplicate member), and <c>validate</c> runs both. No type of the schema package appears in this API.
/// </summary>
public static class SchemaValidator
{
    /// <summary>The logical name of the embedded schema resource.</summary>
    public const string ResourceName = "AgentJoules.Reporting.Schema.results-schema.json";

    /// <summary>
    /// The most values (the root, every array element and every object member) a document may hold to have its failures listed. The evaluation tree of the hierarchical output takes about a thousand times the
    /// size of the input, so a larger document is only checked for validity with the flag output (bounded, it stops at the first failure): a valid one yields no failure, an invalid one yields one failure at
    /// the root and no list. A large valid file, which a run with many repeats legitimately writes, therefore validates.
    /// </summary>
    public const int MaxValues = 100_000;

    internal const string TooLarge = "The document is invalid and holds more than 100000 values; the failures are not listed.";
    internal const string NotEvaluable = "The document could not be checked: a value is nested too deeply.";
    internal const string NotReadable = "The document holds a string the schema checks could not read (for example a lone surrogate escape).";

    private const string DefinitionsKey = "$defs";
    private const string ProbeDefinition = "probe";
    private const string NotValid = "The document does not satisfy the schema.";
    private const string Excluded = "The schema excludes this combination of members.";
    private const string SeveralAlternatives = "Exactly one alternative may hold, but several do.";
    private const string IfKeyword = "if";
    private const string NotKeyword = "not";
    private const string AnyOfKeyword = "anyOf";
    private const string OneOfKeyword = "oneOf";

    // JsonSchema.FromText registers a schema under its $id, and a second parse of the same $id is refused, so each schema is parsed once per process.
    private static readonly Lazy<JsonSchema> RunFileSchema = new(static () => JsonSchema.FromText(EmbeddedText()));
    private static readonly Lazy<JsonSchema> ProbeSchema = new(static () => JsonSchema.FromText(ProbeText()));

    /// <summary>Checks a document against the schema of a file kind.</summary>
    /// <param name="instance">The parsed document.</param>
    /// <param name="target">The kind of file the document is checked as; a run file by default.</param>
    /// <returns>
    /// The failed checks, each with the JSON pointer of the part that failed; an empty list when the document is valid. A document of more than <see cref="MaxValues"/> values that is invalid gets one failure
    /// at the root and no list; a valid one gets an empty list. A value nested too deeply to be evaluated gets one failure at the root.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is not a defined kind.</exception>
    public static IReadOnlyList<SchemaFailure> Validate(JsonElement instance, SchemaTarget target = SchemaTarget.RunFile)
    {
        JsonSchema schema = target switch
        {
            SchemaTarget.RunFile => RunFileSchema.Value,
            SchemaTarget.Probe => ProbeSchema.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(target), (int)target, "The target is not a defined kind of file."),
        };

        bool large = ExceedsValueCap(instance);
        try
        {
            // The flag output decides validity; it is bounded and stops at the first failure (it is not free: a large file still allocates in proportion to the values it reaches). Only an invalid document of at most MaxValues values gets the hierarchical output, which keeps the structure of the evaluation: the flat list
            // reports every failed subschema, including the condition of an if, the subschema of a not and the alternative of an anyOf or oneOf that did not decide the outcome, and those are not failures of
            // the document. A larger invalid document gets one failure at the root. The date-time format is asserted, not only annotated.
            if (schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.Flag, RequireFormatValidation = true }).IsValid)
            {
                return [];
            }

            if (large)
            {
                return [new SchemaFailure(string.Empty, TooLarge)];
            }

            EvaluationResults results = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical, RequireFormatValidation = true });
            if (results.IsValid)
            {
                return [];
            }

            var failures = new List<SchemaFailure>();
            Collect(results, failures);

            return [.. failures.Distinct()];
        }
        catch (JsonException)
        {
            // The enum keyword of the schema package serialises the instance with a depth limit of 64, so a value nested deeper than that cannot be evaluated; the text of the exception is not passed on.
            return [new SchemaFailure(string.Empty, NotEvaluable)];
        }
        catch (InvalidOperationException)
        {
            // The schema package raises this exception for a string it cannot read as text, for example one that holds a lone surrogate escape (a member name, an enum value, a pattern); the cause is not narrowed down and its text is not passed on.
            return [new SchemaFailure(string.Empty, NotReadable)];
        }
    }

    // Counts the values without recursion (a deep document must not exhaust the stack) and stops as soon as the count passes the cap.
    private static bool ExceedsValueCap(JsonElement root)
    {
        var containers = new Stack<JsonElement>();
        containers.Push(root);
        int count = 1;
        while (containers.Count > 0)
        {
            JsonElement container = containers.Pop();
            if (container.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in container.EnumerateArray())
                {
                    if (!Admit(item, containers, ref count))
                    {
                        return true;
                    }
                }
            }
            else if (container.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty member in container.EnumerateObject())
                {
                    if (!Admit(member.Value, containers, ref count))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool Admit(JsonElement value, Stack<JsonElement> containers, ref int count)
    {
        count++;
        if (count > MaxValues)
        {
            return false;
        }

        if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            containers.Push(value);
        }

        return true;
    }

    // Reports the checks that made a node invalid. A node that is invalid without a failed check below it is reported as itself.
    private static void Collect(EvaluationResults node, List<SchemaFailure> failures)
    {
        int before = failures.Count;
        string location = node.InstanceLocation.ToString();
        if (node.Errors is not null)
        {
            foreach (string message in node.Errors.Values)
            {
                failures.Add(new SchemaFailure(location, message));
            }
        }

        string basePath = node.EvaluationPath.ToString();
        var combinators = new Dictionary<string, (string Keyword, List<EvaluationResults> Failed, int ValidBranches)>(StringComparer.Ordinal);
        var plain = new List<EvaluationResults>();
        bool excluded = false;
        foreach (EvaluationResults child in node.Details ?? [])
        {
            string[] relative = RelativePath(basePath, child.EvaluationPath.ToString());
            int at = KeywordIndex(relative);
            if (at < 0)
            {
                if (!child.IsValid)
                {
                    plain.Add(child);
                }

                continue;
            }

            string keyword = relative[at];
            if (keyword == IfKeyword)
            {
                continue;
            }

            if (keyword == NotKeyword)
            {
                // The result of a not carries the outcome of its subschema, so a subschema that holds means that the negation failed.
                excluded |= child.IsValid && relative.Length == at + 1;
                continue;
            }

            string key = string.Join('/', relative[..(at + 1)]);
            (string Keyword, List<EvaluationResults> Failed, int ValidBranches) group = combinators.GetValueOrDefault(key, (keyword, [], 0));
            if (child.IsValid)
            {
                group.ValidBranches += relative.Length == at + 2 ? 1 : 0;
            }
            else
            {
                group.Failed.Add(child);
            }

            combinators[key] = group;
        }

        foreach (EvaluationResults child in plain)
        {
            Collect(child, failures);
        }

        if (excluded)
        {
            failures.Add(new SchemaFailure(location, Excluded));
        }

        foreach ((string keyword, List<EvaluationResults> failed, int validBranches) in combinators.Values)
        {
            // An anyOf is decided by its branches only when none is valid; a oneOf when none is valid (their failures say why) or when several are.
            if (keyword == OneOfKeyword && validBranches > 1)
            {
                failures.Add(new SchemaFailure(location, SeveralAlternatives));
            }
            else if (validBranches == 0)
            {
                foreach (EvaluationResults child in failed)
                {
                    Collect(child, failures);
                }
            }
        }

        if (failures.Count == before)
        {
            failures.Add(new SchemaFailure(location, NotValid));
        }
    }

    private static string[] RelativePath(string basePath, string path) =>
        path.StartsWith(basePath, StringComparison.Ordinal) ? path[basePath.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries) : path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    // The index of the first keyword that decides how a subschema counts: if, not, anyOf or oneOf. A segment after "properties" is the name of a member, not a keyword.
    private static int KeywordIndex(string[] relative)
    {
        for (int index = 0; index < relative.Length; index++)
        {
            if (relative[index] is IfKeyword or NotKeyword or AnyOfKeyword or OneOfKeyword && (index == 0 || relative[index - 1] != "properties"))
            {
                return index;
            }
        }

        return -1;
    }

    private static string EmbeddedText()
    {
        using Stream stream = typeof(SchemaValidator).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The assembly does not hold the embedded resource {ResourceName}.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    // The probe definition stands alone (it refers to no other definition), so it is evaluated as a schema of its own with the draft of the whole.
    private static string ProbeText()
    {
        JsonObject root = (JsonObject)JsonNode.Parse(EmbeddedText())!;
        JsonObject probe = (JsonObject)((JsonObject)root[DefinitionsKey]!)[ProbeDefinition]!.DeepClone();
        var standalone = new JsonObject { ["$schema"] = root["$schema"]!.DeepClone() };
        foreach (KeyValuePair<string, JsonNode?> entry in probe)
        {
            standalone[entry.Key] = entry.Value?.DeepClone();
        }

        return standalone.ToJsonString();
    }
}