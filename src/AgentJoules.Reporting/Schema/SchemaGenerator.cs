using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Json;

namespace AgentJoules.Reporting.Schema;

/// <summary>
/// Generates <c>docs/results-schema.json</c> from <see cref="AgentJoulesJsonContext"/> (AJ-079, REQ-015): the framework schema exporter describes the types, names, required members and
/// <c>additionalProperties: false</c>, and this class adds what the exporter cannot know: the four metric definitions and the assignment of every metric member to one of them
/// (<see cref="MetricSchemaDefs"/>), the vocabularies of the converters, the flags, the outcomes and the throttle reasons (taken from the code, never typed here), the ranges of the plain members,
/// the single definition of the step reading, the probe definition and the calibration definition (AJ-109, the file of `calibrate`). The schema is the floor of the checks on a results file: what JSON Schema cannot state (the energy shape and hosted-tier rules, the pairing
/// of the judge readings, a cost map entry in the currency of its key, a duplicate member) stays with the model, which <c>validate</c> runs as well.
/// </summary>
public static class SchemaGenerator
{
    // The draft is named by its public URI; the id is a URN so that no other URL enters the source.
    private const string DraftUri = "https://json-schema.org/draft/2020-12/schema";
    private const string SchemaId = "urn:agentjoules:results-schema:1";
    private const string StepDef = "step";
    private const string ProbeDef = "probe";
    private const string JudgedAgent = "judgedAgent";
    private const string CalibrationDef = "calibration";

    /// <summary>Generates the schema of a results file.</summary>
    /// <returns>The schema tree.</returns>
    /// <exception cref="InvalidOperationException">A metric member of the model is not assigned to a metric definition.</exception>
    public static JsonObject Generate() => Generate(MetricSchemaDefs.Mapping);

    /// <summary>Generates the text that is committed as <c>docs/results-schema.json</c>: indented by two spaces, LF line endings, ASCII, and one final LF.</summary>
    /// <returns>The schema text.</returns>
    /// <exception cref="InvalidOperationException">A metric member of the model is not assigned to a metric definition.</exception>
    public static string GenerateText()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        return Generate().ToJsonString(options) + "\n";
    }

    /// <summary>Generates the schema with a given assignment of the metric members (the tests remove an entry to see generation fail).</summary>
    /// <param name="mapping">The assignment of the metric members to the metric definitions.</param>
    /// <returns>The schema tree.</returns>
    /// <exception cref="InvalidOperationException">A metric member of the model is not in <paramref name="mapping"/>.</exception>
    internal static JsonObject Generate(IReadOnlyDictionary<(Type Declaring, string JsonName), MetricTarget> mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var options = new JsonSchemaExporterOptions
        {
            TransformSchemaNode = (context, node) => Transform(context, node, mapping),
        };

        return (JsonObject)AgentJoulesJsonContext.Default.RunDocument.GetJsonSchemaAsNode(options);
    }

    // The exporter calls this for every node, the children first and the root last.
    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node, IReadOnlyDictionary<(Type Declaring, string JsonName), MetricTarget> mapping)
    {
        JsonNode refined = Refined(context, node, mapping);
        return context.Path.IsEmpty && refined is JsonObject root ? Assemble(root, mapping) : refined;
    }

    // The refinement of one node: the metric, vocabulary and range rules of its members. Shared by the run-file export and the export of the calibration definition (AJ-109).
    private static JsonNode Refined(JsonSchemaExporterContext context, JsonNode node, IReadOnlyDictionary<(Type Declaring, string JsonName), MetricTarget> mapping)
    {
        if (node is not JsonObject schema)
        {
            return node;
        }

        JsonTypeInfo info = context.TypeInfo;
        if (info.Kind == JsonTypeInfoKind.Enumerable)
        {
            RequireNonNullItems(schema);
        }
        else if (info.Kind == JsonTypeInfoKind.Object && schema["properties"] is JsonObject properties)
        {
            foreach (JsonPropertyInfo property in info.Properties)
            {
                if (properties.ContainsKey(property.Name))
                {
                    Refine(context, property, properties, mapping);
                }
            }
        }

        return schema;
    }

    private static void Refine(JsonSchemaExporterContext context, JsonPropertyInfo property, JsonObject properties, IReadOnlyDictionary<(Type Declaring, string JsonName), MetricTarget> mapping)
    {
        string name = property.Name;
        Type declaring = property.DeclaringType;
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        bool nullable = type != property.PropertyType;

        if (type == typeof(Metric))
        {
            properties[name] = MetricSchemaDefs.Reference(TargetOf(context, declaring, name, mapping));
            return;
        }

        if (type == typeof(IReadOnlyDictionary<string, Metric>))
        {
            JsonObject map = (JsonObject)properties[name]!;
            map["additionalProperties"] = MetricSchemaDefs.Reference(TargetOf(context, declaring, name, mapping));
            return;
        }

        if (type == typeof(Tier))
        {
            properties[name] = Choice(EnumNames<Tier>(static _ => true), nullable);
        }
        else if (type == typeof(RunMode))
        {
            properties[name] = Choice(EnumNames<RunMode>(static _ => true), nullable);
        }
        else if (type == typeof(RunStatus))
        {
            properties[name] = Choice(EnumNames<RunStatus>(static _ => true), nullable);
        }
        else if (type == typeof(EnergyMethod))
        {
            // A step stores counter or sampling; unmetered is the method of a window that measured nothing and is never written.
            properties[name] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["enum"] = new JsonArray([.. EnumNames<EnergyMethod>(static method => method != EnergyMethod.Unmetered).Select(static text => (JsonNode?)JsonValue.Create(text)), null]),
                ["default"] = null,
            };
        }
        else if (type == typeof(ReadingFlags))
        {
            properties[name] = UniqueStrings(ReadingFlagVocabulary(), nullable);
        }
        else
        {
            RefineMember(declaring, name, (JsonObject)properties[name]!);
        }
    }

    private static MetricTarget TargetOf(JsonSchemaExporterContext context, Type declaring, string name, IReadOnlyDictionary<(Type Declaring, string JsonName), MetricTarget> mapping) =>
        mapping.TryGetValue((declaring, name), out MetricTarget? target)
            ? target
            : throw new InvalidOperationException($"The metric member {declaring.Name}.{name} at #/{string.Join('/', context.Path.ToArray())}/properties/{name} is not assigned to a metric definition; add it to {nameof(MetricSchemaDefs)}.");

    // The ranges and vocabularies of the plain members. The model checks them as well; the schema states what a reader of the file can see without the model.
    private static void RefineMember(Type declaring, string name, JsonObject member)
    {
        if (declaring == typeof(RunDocument) && name == "schemaVersion")
        {
            member["const"] = RunDocument.CurrentSchemaVersion;
        }
        else if (declaring == typeof(RunInfo))
        {
            switch (name)
            {
                case "toolVersion":
                case "gitCommit":
                    member["minLength"] = 1;
                    break;
                case "repeat":
                    member["minimum"] = 1;
                    break;
                case "flags":
                    // The run flags may be null on read (the file always holds an array); an element is one of the vocabulary and none repeats.
                    member["items"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(RunFlagNames.All) };
                    member["uniqueItems"] = true;
                    break;
                default:
                    break;
            }
        }
        else if (declaring == typeof(CalibrationDocument))
        {
            RefineCalibrationMember(name, member);
        }
        else if (declaring == typeof(CalibrationEnvironment) && name == "gpuOffloadPercent")
        {
            member["minimum"] = 0;
            member["maximum"] = 100;
        }
        else if (declaring == typeof(EnvironmentInfo) && name == "energyScope")
        {
            member["const"] = EnvironmentInfo.GpuBoardOnly;
        }
        else if (declaring == typeof(EnvironmentInfo) && name == "gpuOffloadPercent")
        {
            // A share; the model also requires it to be finite, which JSON Schema cannot state.
            member["minimum"] = 0;
            member["maximum"] = 100;
        }
        else if (declaring == typeof(StepReading))
        {
            RefineStepMember(name, member);
        }
        else if (declaring == typeof(BaselineResult))
        {
            RefineBaselineMember(name, member);
        }
    }

    // The ranges BaselineResult enforces on construction and on read.
    private static void RefineBaselineMember(string name, JsonObject member)
    {
        switch (name)
        {
            case "seconds":
            case "maxCoefficientOfVariation":
                member["exclusiveMinimum"] = 0;
                break;
            case "sampleCount":
            case "coefficientOfVariation":
                member["minimum"] = 0;
                break;
            default:
                break;
        }
    }

    private static void RefineStepMember(string name, JsonObject member)
    {
        switch (name)
        {
            case "task":
            case "agent":
            case "modelId":
                member["minLength"] = 1;
                break;
            case JudgedAgent:
                // Present, it names an agent; a workflow step does not have it (see the steps list), so it is never null.
                member["type"] = "string";
                member.Remove("default");
                member["minLength"] = 1;
                break;
            case "repeat":
                member["minimum"] = 1;
                break;
            case "retries":
            case "waitMs":
            case "sampleCount":
            case "outputChars":
                member["minimum"] = 0;
                break;
            case "sampleRateHz":
                member["exclusiveMinimum"] = 0;
                break;
            case "outcome":
                member["enum"] = Strings(OutcomeVocabulary());
                break;
            case "outputSha256":
                // In .NET a dollar sign also matches before a final line feed, so the lookahead keeps "64 digits and a line feed" out.
                member["pattern"] = "^[0-9a-f]{64}$(?!\\n)";
                break;
            case "throttleReasons":
                member["items"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(ThrottleReasons.AllNames) };
                member["uniqueItems"] = true;
                break;
            default:
                break;
        }
    }

    // The model rejects a null element of any list (tasks, flags, steps, judge, models, throttle reasons), but the exporter, which sees a reference type, allows it.
    private static void RequireNonNullItems(JsonObject list)
    {
        if (list["items"] is JsonObject items)
        {
            WithoutNull(items);
        }
    }

    private static void WithoutNull(JsonObject schema)
    {
        if (schema["type"] is not JsonArray types)
        {
            return;
        }

        string[] kept = [.. types.Select(static type => type!.GetValue<string>()).Where(static type => type != "null")];
        schema["type"] = kept.Length == 1 ? kept[0] : new JsonArray([.. kept.Select(static type => (JsonNode?)JsonValue.Create(type))]);
    }

    // The root: the order of the keys, the draft and the id, the single definition of the step, the probe definition and the metric definitions.
    private static JsonObject Assemble(JsonObject exported, IReadOnlyDictionary<(Type Declaring, string JsonName), MetricTarget> mapping)
    {
        JsonObject properties = (JsonObject)exported["properties"]!;
        JsonObject steps = (JsonObject)properties["steps"]!;
        JsonObject judge = (JsonObject)properties["judge"]!;

        JsonObject step = (JsonObject)steps["items"]!.DeepClone();
        WithoutNull(step);
        steps["items"] = new JsonObject { ["allOf"] = new JsonArray(MetricSchemaDefs.Ref(StepDef), new JsonObject { ["not"] = new JsonObject { ["required"] = new JsonArray(JudgedAgent) } }) };
        judge["items"] = new JsonObject { ["allOf"] = new JsonArray(MetricSchemaDefs.Ref(StepDef), new JsonObject { ["required"] = new JsonArray(JudgedAgent) }) };

        JsonObject probe = (JsonObject)AgentJoulesJsonContext.Default.ProbeResult.GetJsonSchemaAsNode();
        WithoutNull(probe);
        properties["probe"] = new JsonObject { ["anyOf"] = new JsonArray(MetricSchemaDefs.Ref(ProbeDef), new JsonObject { ["type"] = "null" }) };

        JsonObject definitions = MetricSchemaDefs.Definitions();
        definitions[StepDef] = step;
        definitions[ProbeDef] = probe;
        definitions[CalibrationDef] = Calibration(mapping);

        var root = new JsonObject
        {
            ["$schema"] = DraftUri,
            ["$id"] = SchemaId,
        };

        foreach (KeyValuePair<string, JsonNode?> entry in exported)
        {
            root[entry.Key] = entry.Value?.DeepClone();
        }

        WithoutNull(root);
        root["$defs"] = definitions;
        return root;
    }

    // The definition of a calibration file (AJ-109): the exporter describes the document and its environment, the same refinement assigns the three totals to the measured-metric definition and states the ranges, and
    // the probe member refers to the probe definition of the run files and adds that a calibration found a GPU.
    private static JsonObject Calibration(IReadOnlyDictionary<(Type Declaring, string JsonName), MetricTarget> mapping)
    {
        var options = new JsonSchemaExporterOptions
        {
            TransformSchemaNode = (context, node) => Refined(context, node, mapping),
        };

        JsonObject calibration = (JsonObject)AgentJoulesJsonContext.Default.CalibrationDocument.GetJsonSchemaAsNode(options);
        WithoutNull(calibration);
        JsonObject properties = (JsonObject)calibration["properties"]!;
        properties["probe"] = new JsonObject
        {
            ["allOf"] = new JsonArray(
                MetricSchemaDefs.Ref(ProbeDef),
                new JsonObject { ["properties"] = new JsonObject { ["gpuPresent"] = new JsonObject { ["const"] = true } } }),
        };

        return calibration;
    }

    // The ranges CalibrationDocument enforces on construction and on read.
    private static void RefineCalibrationMember(string name, JsonObject member)
    {
        switch (name)
        {
            case "task":
            case "toolVersion":
            case "gitCommit":
                member["minLength"] = 1;
                break;
            case "seconds":
            case "repeats":
                member["minimum"] = 1;
                break;
            case "sampleCount":
                member["minimum"] = 0;
                break;
            case "windowSeconds":
            case "sampleRateHz":
                member["exclusiveMinimum"] = 0;
                break;
            default:
                break;
        }
    }

    // A member of a nullable enumeration type accepts null as well as its names.
    private static JsonObject Choice(IEnumerable<string> names, bool nullable = false) =>
        nullable
            ? new JsonObject { ["type"] = new JsonArray("string", "null"), ["enum"] = new JsonArray([.. names.Select(static name => (JsonNode?)JsonValue.Create(name)), null]) }
            : new JsonObject { ["type"] = "string", ["enum"] = Strings(names) };

    private static JsonObject UniqueStrings(IEnumerable<string> names, bool nullable) =>
        new()
        {
            ["type"] = nullable ? new JsonArray("array", "null") : "array",
            ["items"] = Choice(names),
            ["uniqueItems"] = true,
        };

    private static JsonArray Strings(IEnumerable<string> names) => new([.. names.Select(static name => (JsonNode?)JsonValue.Create(name))]);

    // The strings the converters write, from the JsonStringEnumMemberName attributes that the converters document.
    private static string[] EnumNames<T>(Func<T, bool> include)
        where T : struct, Enum =>
        [.. Enum.GetValues<T>().Where(include).Select(static value =>
            typeof(T).GetField(value.ToString())?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
            ?? throw new InvalidOperationException($"{typeof(T).Name}.{value} has no JSON name."))];

    private static IReadOnlyList<string> ReadingFlagVocabulary()
    {
        ReadingFlags all = Enum.GetValues<ReadingFlags>().Aggregate(ReadingFlags.None, static (mask, flag) => mask | flag);
        return ReadingFlagNames.ToStrings(all);
    }

    // The outcome strings are the string constants of StepOutcomes, in declaration order.
    private static string[] OutcomeVocabulary() =>
        [.. typeof(StepOutcomes).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .OrderBy(static field => field.MetadataToken)
            .Select(static field => (string)field.GetRawConstantValue()!)];
}