using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;

namespace AgentJoules.Reporting.Schema;

/// <summary>The rule a metric member adds to the definition it refers to (AJ-079 P-11).</summary>
internal enum MetricRefinement
{
    /// <summary>None: the member is exactly the definition.</summary>
    None = 0,

    /// <summary>A step cost, and the cost per quality point derived from it (user decision 9, 2026-10-24): an estimate has a currency, no unit, and is not negative.</summary>
    StepCost,

    /// <summary>A value of a cost map: N/A or an estimate (never not-reported), with a currency, no unit, not negative.</summary>
    CostMapValue,

    /// <summary>The judge score: a model-judged value is a whole number from 1 to 5 without a unit and without a currency.</summary>
    Quality,

    /// <summary>The joules per quality point: a model-judged value that is not negative, without a unit and without a currency.</summary>
    JoulesPerQualityPoint,
}

/// <summary>The definition a metric member refers to and the refinement it adds.</summary>
/// <param name="Def">The name of the definition in <c>$defs</c>.</param>
/// <param name="Refinement">What the member adds to the definition.</param>
internal sealed record MetricTarget(string Def, MetricRefinement Refinement);

/// <summary>
/// The four metric definitions of the results schema and the table that assigns every metric member of the model to one of them (AJ-079, design 3.5). A definition states the qualifier rules of
/// <see cref="Metric"/>: N/A and not-reported hold a null value, a reason and no unit or currency; a valued label holds a number and no reason; a measured or an estimated value has exactly one of a unit
/// and a currency; a model-judged value has at most one. The definitions differ in the labels they allow; a member that is stricter than its definition adds a refinement.
/// </summary>
internal static class MetricSchemaDefs
{
    /// <summary>The definition of a metric that is measured, N/A or not-reported.</summary>
    internal const string MeasuredDef = "metricMeasured";

    /// <summary>The definition of a metric that is estimated, N/A or not-reported.</summary>
    internal const string EstimatedDef = "metricEstimated";

    /// <summary>The definition of a metric that is model-judged, N/A or not-reported.</summary>
    internal const string JudgedDef = "metricJudged";

    /// <summary>The definition of a metric with any of the five labels; no member of the M3 model refers to it.</summary>
    internal const string AnyDef = "metricAny";

    private static readonly MetricTarget Measured = new(MeasuredDef, MetricRefinement.None);
    private static readonly MetricTarget Estimated = new(EstimatedDef, MetricRefinement.None);

    /// <summary>
    /// The assignment of every metric member to its definition, keyed by the declaring type and the JSON name. A map member (<c>costByCurrency</c>) is assigned the target of its values. The generator
    /// fails when a metric member of the model is not in this table, so a new member cannot reach the schema unchecked.
    /// </summary>
    internal static IReadOnlyDictionary<(Type Declaring, string JsonName), MetricTarget> Mapping { get; } = BuildMapping();

    /// <summary>Builds the schema node a metric member is replaced by.</summary>
    /// <param name="target">The assignment of the member.</param>
    /// <returns>A reference to the definition, or an <c>allOf</c> of the reference and the refinement.</returns>
    internal static JsonNode Reference(MetricTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        JsonObject reference = Ref(target.Def);
        return target.Refinement == MetricRefinement.None
            ? reference
            : new JsonObject { ["allOf"] = new JsonArray(reference, Refinement(target.Refinement)) };
    }

    /// <summary>Builds the four definitions in their fixed order.</summary>
    /// <returns>An object that holds <c>metricMeasured</c>, <c>metricEstimated</c>, <c>metricJudged</c> and <c>metricAny</c>.</returns>
    internal static JsonObject Definitions() =>
        new()
        {
            [MeasuredDef] = Definition([MetricLabelText.Measured], MetricLabelText.Measured),
            [EstimatedDef] = Definition([MetricLabelText.Estimated], MetricLabelText.Estimated),
            [JudgedDef] = Definition([MetricLabelText.ModelJudged], MetricLabelText.ModelJudged),
            [AnyDef] = Definition([MetricLabelText.Measured, MetricLabelText.Estimated, MetricLabelText.ModelJudged], null),
        };

    /// <summary>A reference to a definition of the schema.</summary>
    /// <param name="name">The name in <c>$defs</c>.</param>
    /// <returns>The <c>$ref</c> object.</returns>
    internal static JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };

    private static Dictionary<(Type Declaring, string JsonName), MetricTarget> BuildMapping()
    {
        var map = new Dictionary<(Type Declaring, string JsonName), MetricTarget>();

        Add(map, typeof(StepReading), Measured, nameof(StepReading.LatencyMs), nameof(StepReading.TtftMs), nameof(StepReading.InputTokens), nameof(StepReading.OutputTokens), nameof(StepReading.GrossJoules), nameof(StepReading.NetJoules), nameof(StepReading.AvgW), nameof(StepReading.PeakW));
        Add(map, typeof(StepReading), new MetricTarget(EstimatedDef, MetricRefinement.StepCost), nameof(StepReading.Cost), nameof(StepReading.CostPerQualityPoint));
        Add(map, typeof(StepReading), new MetricTarget(JudgedDef, MetricRefinement.Quality), nameof(StepReading.Quality));
        Add(map, typeof(StepReading), new MetricTarget(JudgedDef, MetricRefinement.JoulesPerQualityPoint), nameof(StepReading.JoulesPerQualityPoint));

        Add(map, typeof(Totals), Measured, nameof(Totals.GrossJoules), nameof(Totals.NetJoules), nameof(Totals.InputTokens), nameof(Totals.OutputTokens), nameof(Totals.WallClockMs));
        Add(map, typeof(Totals), new MetricTarget(EstimatedDef, MetricRefinement.CostMapValue), nameof(Totals.CostByCurrency));
        Add(map, typeof(Totals), Estimated, nameof(Totals.Gco2e));

        Add(map, typeof(JudgeTotals), Measured, nameof(JudgeTotals.InputTokens), nameof(JudgeTotals.OutputTokens));
        Add(map, typeof(JudgeTotals), new MetricTarget(EstimatedDef, MetricRefinement.CostMapValue), nameof(JudgeTotals.CostByCurrency));

        Add(map, typeof(BaselineResult), Measured, nameof(BaselineResult.MeanW), nameof(BaselineResult.StdDevW));
        Add(map, typeof(EnvironmentInfo), Measured, nameof(EnvironmentInfo.CloudCostUsd));
        Add(map, typeof(EnvironmentAssumptions), Estimated, nameof(EnvironmentAssumptions.ElectricityPrice), nameof(EnvironmentAssumptions.Gco2ePerKwh));

        return map;
    }

    private static void Add(Dictionary<(Type Declaring, string JsonName), MetricTarget> map, Type declaring, MetricTarget target, params string[] members)
    {
        foreach (string member in members)
        {
            map.Add((declaring, JsonNamingPolicy.CamelCase.ConvertName(member)), target);
        }
    }

    // One definition: the label set, then the rules that hold for every label (absent or valued), then the qualifier rule of the label the definition is about (none for the definition of all labels,
    // which carries the rules of every valued label).
    private static JsonObject Definition(string[] valuedLabels, string? ruleLabel)
    {
        var labels = new JsonArray();
        foreach (string label in valuedLabels)
        {
            labels.Add(label);
        }

        labels.Add(MetricLabelText.NotApplicable);
        labels.Add(MetricLabelText.NotReported);

        var rules = new JsonArray { AbsenceRule() };
        if (ruleLabel is null)
        {
            rules.Add(ExactlyOneQualifierRule(MetricLabelText.Measured));
            rules.Add(ExactlyOneQualifierRule(MetricLabelText.Estimated));
            rules.Add(AtMostOneQualifierRule());
        }
        else if (ruleLabel == MetricLabelText.ModelJudged)
        {
            rules.Add(AtMostOneQualifierRule());
        }
        else
        {
            rules.Add(ExactlyOneQualifierRule(ruleLabel));
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["value"] = new JsonObject { ["type"] = new JsonArray("number", "null") },
                ["label"] = new JsonObject { ["enum"] = labels },
                ["reason"] = NonEmptyString(),
                ["unit"] = NonEmptyString(),
                ["currency"] = NonEmptyString(),
            },
            ["required"] = new JsonArray("value", "label"),
            ["additionalProperties"] = false,
            ["allOf"] = rules,
        };
    }

    // N/A and not-reported: a null value, a non-empty reason, no unit and no currency. Any other label: a number and no reason.
    private static JsonObject AbsenceRule() =>
        new()
        {
            ["if"] = new JsonObject
            {
                ["properties"] = new JsonObject { ["label"] = new JsonObject { ["enum"] = new JsonArray(MetricLabelText.NotApplicable, MetricLabelText.NotReported) } },
                ["required"] = new JsonArray("label"),
            },
            ["then"] = new JsonObject
            {
                ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "null" } },
                ["required"] = new JsonArray("reason"),
                ["not"] = new JsonObject { ["anyOf"] = new JsonArray(Requires("unit"), Requires("currency")) },
            },
            ["else"] = new JsonObject
            {
                ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "number" } },
                ["not"] = Requires("reason"),
            },
        };

    // A measured or an estimated metric has exactly one of a unit and a currency.
    private static JsonObject ExactlyOneQualifierRule(string label) =>
        new()
        {
            ["if"] = LabelIs(label),
            ["then"] = new JsonObject { ["oneOf"] = new JsonArray(Requires("unit"), Requires("currency")) },
        };

    // A model-judged metric has at most one of a unit and a currency.
    private static JsonObject AtMostOneQualifierRule() =>
        new()
        {
            ["if"] = LabelIs(MetricLabelText.ModelJudged),
            ["then"] = new JsonObject { ["not"] = Requires("unit", "currency") },
        };

    private static JsonObject Refinement(MetricRefinement refinement) =>
        refinement switch
        {
            MetricRefinement.StepCost => new JsonObject
            {
                ["if"] = LabelIs(MetricLabelText.Estimated),
                ["then"] = EstimateInACurrency(),
            },
            MetricRefinement.CostMapValue => new JsonObject
            {
                ["properties"] = new JsonObject { ["label"] = new JsonObject { ["enum"] = new JsonArray(MetricLabelText.Estimated, MetricLabelText.NotApplicable) } },
                ["if"] = LabelIs(MetricLabelText.Estimated),
                ["then"] = EstimateInACurrency(),
            },
            MetricRefinement.Quality => new JsonObject
            {
                ["if"] = LabelIs(MetricLabelText.ModelJudged),
                ["then"] = new JsonObject
                {
                    ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5 } },
                    ["not"] = new JsonObject { ["anyOf"] = new JsonArray(Requires("unit"), Requires("currency")) },
                },
            },
            MetricRefinement.JoulesPerQualityPoint => new JsonObject
            {
                ["if"] = LabelIs(MetricLabelText.ModelJudged),
                ["then"] = new JsonObject
                {
                    ["properties"] = new JsonObject { ["value"] = NotNegative() },
                    ["not"] = new JsonObject { ["anyOf"] = new JsonArray(Requires("unit"), Requires("currency")) },
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(refinement), (int)refinement, "The refinement has no schema."),
        };

    // A valued cost: a currency, no unit, not negative.
    private static JsonObject EstimateInACurrency() =>
        new()
        {
            ["properties"] = new JsonObject { ["value"] = NotNegative() },
            ["required"] = new JsonArray("currency"),
            ["not"] = Requires("unit"),
        };

    private static JsonObject LabelIs(string label) =>
        new()
        {
            ["properties"] = new JsonObject { ["label"] = new JsonObject { ["const"] = label } },
            ["required"] = new JsonArray("label"),
        };

    private static JsonObject Requires(params string[] members)
    {
        var names = new JsonArray();
        foreach (string member in members)
        {
            names.Add(member);
        }

        return new JsonObject { ["required"] = names };
    }

    private static JsonObject NotNegative() => new() { ["minimum"] = 0 };

    private static JsonObject NonEmptyString() => new() { ["type"] = "string", ["minLength"] = 1 };
}