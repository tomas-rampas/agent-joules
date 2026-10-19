using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

using Xunit;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-005")]
public sealed class MetricTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Reads "NaN" and "Infinity" as number literals, so the constructor (not the reader) is what has to reject them.
    private static readonly JsonSerializerOptions NamedLiteralOptions = new(Options)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly JsonSerializerOptions IgnoreNullOptions = new(Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] BelowBaselineAndThrottled = ["below-baseline", "throttled"];

    private static readonly string[] FlagVocabulary =
    [
        "sampling-fallback",
        "low-resolution",
        "shared-window",
        "baseline-unstable",
        "below-baseline",
        "throttled",
        "partial-offload",
    ];

    private static string Serialize(Metric metric) => JsonSerializer.Serialize(metric, Options);

    private static string[] PropertyNames(Metric metric)
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(metric));
        return [.. document.RootElement.EnumerateObject().Select(property => property.Name)];
    }

    private static Metric Deserialize(string json, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<Metric>(json, options ?? Options) ?? throw new InvalidOperationException("The JSON document deserialised to null.");

    // The converter lets the constructor's ArgumentException surface unwrapped: no JsonException wrapper, no inner-exception tolerance.
    private static ArgumentException AssertRejectedWithArgumentException(Action action)
    {
        Exception failure = Assert.ThrowsAny<Exception>(action);

        return Assert.IsType<ArgumentException>(failure, exactMatch: false);
    }

    // The one validating constructor; the compiler-generated private copy constructor (the record is sealed) takes a Metric and is skipped.
    private static ConstructorInfo ValidatingConstructor() =>
        Assert.Single(
            typeof(Metric).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance),
            constructor => constructor.GetParameters().Length == 5);

    private static void AssertRejectedWithJsonException(string json)
    {
        Assert.Throws<JsonException>(() => Deserialize(json));
    }

    // ---- Factories: label, value, unit, currency, reason ----

    [Fact]
    public void Measured_WithValueAndUnit_SetsLabelValueAndUnit()
    {
        Metric metric = Metric.Measured(180, "J");

        Assert.Equal(180.0, metric.Value);
        Assert.Equal(MetricLabel.Measured, metric.Label);
        Assert.Equal("J", metric.Unit);
        Assert.Null(metric.Currency);
        Assert.Null(metric.Reason);
    }

    [Fact]
    public void Estimated_WithValueAndCurrency_SetsLabelValueAndCurrency()
    {
        Metric metric = Metric.Estimated(0.42, "USD");

        Assert.Equal(0.42, metric.Value);
        Assert.Equal(MetricLabel.Estimated, metric.Label);
        Assert.Equal("USD", metric.Currency);
        Assert.Null(metric.Unit);
        Assert.Null(metric.Reason);
    }

    [Fact]
    public void ModelJudged_WithValue_SetsLabelAndValueOnly()
    {
        Metric metric = Metric.ModelJudged(7);

        Assert.Equal(7.0, metric.Value);
        Assert.Equal(MetricLabel.ModelJudged, metric.Label);
        Assert.Null(metric.Unit);
        Assert.Null(metric.Currency);
        Assert.Null(metric.Reason);
    }

    [Fact]
    public void NotApplicable_WithReason_HasNoValueAndKeepsTheReason()
    {
        Metric metric = Metric.NotApplicable("no-gpu-in-this-tier");

        Assert.Null(metric.Value);
        Assert.Equal(MetricLabel.NotApplicable, metric.Label);
        Assert.Equal("no-gpu-in-this-tier", metric.Reason);
        Assert.Null(metric.Unit);
        Assert.Null(metric.Currency);
    }

    [Fact]
    public void NotReported_WithReason_HasNoValueAndKeepsTheReason()
    {
        Metric metric = Metric.NotReported("provider-omitted-usage");

        Assert.Null(metric.Value);
        Assert.Equal(MetricLabel.NotReported, metric.Label);
        Assert.Equal("provider-omitted-usage", metric.Reason);
        Assert.Null(metric.Unit);
        Assert.Null(metric.Currency);
    }

    // ---- Criterion 5: a missing value is never zero, a measured zero is legal ----

    [Fact]
    public void NotReported_Value_IsNullAndNeverZero()
    {
        Metric metric = Metric.NotReported("x");

        Assert.Null(metric.Value);
        Assert.False(metric.Value.HasValue);
    }

    [Fact]
    public void NotApplicable_Value_IsNullAndNeverZero()
    {
        Metric metric = Metric.NotApplicable("x");

        Assert.Null(metric.Value);
    }

    [Fact]
    public void Measured_Zero_IsLegalAndSerializesAsZero()
    {
        Metric metric = Metric.Measured(0, "J");

        Assert.Equal(0.0, metric.Value);
        Assert.Equal(MetricLabel.Measured, metric.Label);
        Assert.Equal("""{"value":0,"label":"measured","unit":"J"}""", Serialize(metric));
    }

    [Fact]
    public void Measured_NegativeValue_IsLegal()
    {
        // A net energy below the baseline is kept, not clamped (AJ-026); the label object must not forbid it.
        Metric metric = Metric.Measured(-15, "J");

        Assert.Equal(-15.0, metric.Value);
    }

    // ---- Criterion 1: constructor rejections reached through the factories (each pins the offending parameter) ----

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Measured_WithNonFiniteValue_ThrowsArgumentException(double value)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => Metric.Measured(value, "J"));

        Assert.Equal("value", failure.ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Estimated_WithNonFiniteValue_ThrowsArgumentException(double value)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => Metric.Estimated(value, "USD"));

        Assert.Equal("value", failure.ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ModelJudged_WithNonFiniteValue_ThrowsArgumentException(double value)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => Metric.ModelJudged(value));

        Assert.Equal("value", failure.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Measured_WithoutUnit_ThrowsArgumentException(string? unit)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => Metric.Measured(1, unit!));

        Assert.Equal("unit", failure.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Estimated_WithoutCurrency_ThrowsArgumentException(string? currency)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => Metric.Estimated(1, currency!));

        Assert.Equal("currency", failure.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NotApplicable_WithoutReason_ThrowsArgumentException(string? reason)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => Metric.NotApplicable(reason!));

        Assert.Equal("reason", failure.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NotReported_WithoutReason_ThrowsArgumentException(string? reason)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => Metric.NotReported(reason!));

        Assert.Equal("reason", failure.ParamName);
    }

    // ---- Criterion 1: constructor rejections only reachable through the converter or reflection ----

    [Theory]
    [InlineData("""{"value":0,"label":"N/A","reason":"x"}""", "value")]
    [InlineData("""{"value":1.5,"label":"N/A","reason":"x"}""", "value")]
    [InlineData("""{"value":0,"label":"not-reported","reason":"x"}""", "value")]
    [InlineData("""{"value":1.5,"label":"not-reported","reason":"x"}""", "value")]
    [InlineData("""{"value":null,"label":"measured","unit":"J"}""", "value")]
    [InlineData("""{"value":null,"label":"estimated","currency":"USD"}""", "value")]
    [InlineData("""{"value":null,"label":"model-judged"}""", "value")]
    [InlineData("""{"value":null,"label":"N/A","reason":""}""", "reason")]
    [InlineData("""{"value":null,"label":"N/A","reason":"   "}""", "reason")]
    [InlineData("""{"value":null,"label":"N/A"}""", "reason")]
    [InlineData("""{"value":null,"label":"not-reported","reason":""}""", "reason")]
    [InlineData("""{"value":null,"label":"not-reported"}""", "reason")]
    [InlineData("""{"value":1,"label":"measured"}""", "unit")]
    [InlineData("""{"value":1,"label":"measured","unit":""}""", "unit")]
    [InlineData("""{"value":1,"label":"measured","unit":"  "}""", "unit")]
    [InlineData("""{"value":1,"label":"estimated"}""", "currency")]
    [InlineData("""{"value":1,"label":"estimated","currency":""}""", "currency")]
    [InlineData("""{"value":1,"label":"measured","unit":"J","reason":"x"}""", "reason")]
    [InlineData("""{"value":1,"label":"estimated","currency":"USD","reason":"x"}""", "reason")]
    [InlineData("""{"value":1,"label":"model-judged","reason":"x"}""", "reason")]
    [InlineData("""{"value":null,"label":"N/A","reason":"x","unit":"J"}""", "unit")]
    [InlineData("""{"value":null,"label":"not-reported","reason":"x","unit":"J"}""", "unit")]
    [InlineData("""{"value":1,"label":"model-judged","unit":"  "}""", "unit")]
    [InlineData("""{"value":1,"label":"measured","currency":"  "}""", "currency")]
    [InlineData("""{"value":1,"label":"estimated","unit":""}""", "unit")]
    [InlineData("""{"value":1,"label":"estimated","unit":"gCO2e","currency":"USD"}""", "unit")]
    [InlineData("""{"value":null,"label":"not-reported","reason":"x","currency":"USD"}""", "currency")]
    [InlineData("""{"value":1,"label":"estimated","currency":"USD","unit":"J"}""", "unit")]
    [InlineData("""{"value":null,"label":"N/A","reason":"x","currency":"USD"}""", "currency")]
    [InlineData("""{"value":1,"label":"measured","unit":"J","currency":"USD"}""", "currency")]
    [InlineData("""{"value":1,"label":"model-judged","currency":"USD"}""", "currency")]
    public void Deserialize_ViolatingTheInvariants_ThrowsTheUnwrappedArgumentException(string json, string parameter)
    {
        ArgumentException failure = AssertRejectedWithArgumentException(() => Deserialize(json));

        Assert.Equal(parameter, failure.ParamName);
    }

    [Fact]
    public void Constructor_WithUndefinedLabel_ThrowsArgumentExceptionEchoingOnlyTheInteger()
    {
        ConstructorInfo constructor = ValidatingConstructor();

        TargetInvocationException wrapper = Assert.Throws<TargetInvocationException>(
            () => constructor.Invoke([1.0, (MetricLabel)99, null, null, null]));

        ArgumentException failure = Assert.IsType<ArgumentException>(wrapper.InnerException);
        Assert.Equal("label", failure.ParamName);
        Assert.Contains("99", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithDefaultLabel_IsRejectedBecauseZeroIsNotALabel()
    {
        ConstructorInfo constructor = ValidatingConstructor();

        TargetInvocationException wrapper = Assert.Throws<TargetInvocationException>(
            () => constructor.Invoke([1.0, default(MetricLabel), null, null, null]));

        Assert.Equal("label", Assert.IsType<ArgumentException>(wrapper.InnerException).ParamName);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void Deserialize_NonFiniteNamedLiteral_ThrowsJsonException(string literal)
    {
        // The converter accepts only a number or null token, so the string "NaN" never reaches the constructor, even when the caller enables named literals.
        string json = $$"""{"value":"{{literal}}","label":"measured","unit":"J"}""";

        Assert.Throws<JsonException>(() => Deserialize(json, NamedLiteralOptions));
    }

    [Fact]
    public void Deserialize_ValueBeyondDoubleRange_IsRejected()
    {
        // The reader turns the overflowing literal into infinity; the constructor's finite check is what rejects it.
        ArgumentException failure = AssertRejectedWithArgumentException(() => Deserialize("""{"value":1e999,"label":"measured","unit":"J"}"""));

        Assert.Equal("value", failure.ParamName);
    }

    [Fact]
    public void Deserialize_UnknownLabel_Throws()
    {
        Assert.Throws<JsonException>(() => Deserialize("""{"value":1,"label":"guessed","unit":"J"}"""));
    }

    // ---- Criterion 1: documents the converter rejects as malformed (JsonException) ----

    [Theory]
    [InlineData("""{"value":1,"unit":"J"}""")]
    [InlineData("""{"value":1,"label":0,"unit":"J"}""")]
    [InlineData("""{"value":1,"label":1,"unit":"J"}""")]
    [InlineData("""{"value":1,"label":9,"unit":"J"}""")]
    [InlineData("""{"value":1,"label":"4","unit":"J"}""")]
    [InlineData("""{"value":1,"label":"","unit":"J"}""")]
    [InlineData("""{"value":1,"label":"estimated, model-judged","unit":"J"}""")]
    [InlineData("""{"value":1,"label":" measured","unit":"J"}""")]
    [InlineData("""{"value":1,"label":"Measured","unit":"J"}""")]
    [InlineData("""{"value":1,"label":"n/a","unit":"J"}""")]
    [InlineData("""{"value":1,"label":null,"unit":"J"}""")]
    [InlineData("""{"label":"N/A","reason":"x"}""")]
    [InlineData("""{"label":"measured","unit":"J"}""")]
    [InlineData("""{"Value":5,"label":"N/A","reason":"x"}""")]
    [InlineData("""{"value":null,"Label":"N/A","reason":"x"}""")]
    [InlineData("""{"value":1,"value":2,"label":"measured","unit":"J"}""")]
    [InlineData("""{"value":1,"label":"measured","label":"measured","unit":"J"}""")]
    [InlineData("""{"value":1,"label":"measured","unit":"J","unit":"J"}""")]
    [InlineData("""{"value":1,"label":"measured","unit":"J","extra":true}""")]
    [InlineData("""{"value":"1","label":"measured","unit":"J"}""")]
    [InlineData("""{"value":true,"label":"measured","unit":"J"}""")]
    [InlineData("""{"value":{},"label":"measured","unit":"J"}""")]
    [InlineData("""{"value":1,"label":"measured","unit":5}""")]
    [InlineData("""[]""")]
    [InlineData("""5""")]
    public void Deserialize_MalformedDocument_ThrowsJsonException(string json)
    {
        AssertRejectedWithJsonException(json);
    }

    // ---- Criterion 3: serialization ----

    [Fact]
    public void Serialize_Measured_WritesValueLabelAndUnit()
    {
        Metric metric = Metric.Measured(180, "J");

        Assert.Equal("""{"value":180,"label":"measured","unit":"J"}""", Serialize(metric));
        string[] expectedNames = ["value", "label", "unit"];
        Assert.Equal(expectedNames, PropertyNames(metric));
    }

    [Fact]
    public void Serialize_Estimated_WritesValueLabelAndCurrency()
    {
        Metric metric = Metric.Estimated(0.5, "USD");

        Assert.Equal("""{"value":0.5,"label":"estimated","currency":"USD"}""", Serialize(metric));
        string[] expectedNames = ["value", "label", "currency"];
        Assert.Equal(expectedNames, PropertyNames(metric));
    }

    [Fact]
    public void Serialize_ModelJudged_WritesOnlyValueAndLabel()
    {
        Metric metric = Metric.ModelJudged(7);

        Assert.Equal("""{"value":7,"label":"model-judged"}""", Serialize(metric));
        string[] expectedNames = ["value", "label"];
        Assert.Equal(expectedNames, PropertyNames(metric));
    }

    [Fact]
    public void Serialize_NotApplicable_WritesNullValueLabelAndReason()
    {
        Metric metric = Metric.NotApplicable("no-gpu-in-this-tier");

        Assert.Equal("""{"value":null,"label":"N/A","reason":"no-gpu-in-this-tier"}""", Serialize(metric));
        string[] expectedNames = ["value", "label", "reason"];
        Assert.Equal(expectedNames, PropertyNames(metric));
    }

    [Fact]
    public void Serialize_NotReported_WritesNullValueLabelAndReason()
    {
        Metric metric = Metric.NotReported("provider-omitted-usage");

        Assert.Equal("""{"value":null,"label":"not-reported","reason":"provider-omitted-usage"}""", Serialize(metric));
        string[] expectedNames = ["value", "label", "reason"];
        Assert.Equal(expectedNames, PropertyNames(metric));
    }

    [Fact]
    public void Serialize_WithGlobalIgnoreWhenWritingNull_StillWritesTheNullValue()
    {
        string json = JsonSerializer.Serialize(Metric.NotReported("x"), IgnoreNullOptions);

        Assert.Equal("""{"value":null,"label":"not-reported","reason":"x"}""", json);
    }

    [Fact]
    public void MetricLabel_Members_CarryExactlyTheFiveLabelStrings()
    {
        var labelStrings = typeof(MetricLabel)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(
                field => field.Name,
                field => field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name);

        Assert.Equal(5, labelStrings.Count);
        Assert.Equal("measured", labelStrings["Measured"]);
        Assert.Equal("estimated", labelStrings["Estimated"]);
        Assert.Equal("model-judged", labelStrings["ModelJudged"]);
        Assert.Equal("N/A", labelStrings["NotApplicable"]);
        Assert.Equal("not-reported", labelStrings["NotReported"]);
    }

    [Fact]
    public void RoundTrip_EveryFactoryMetric_ComesBackEqual()
    {
        Metric[] metrics =
        [
            Metric.Measured(180, "J"),
            Metric.Measured(0, "J"),
            Metric.Estimated(0.5, "USD"),
            Metric.ModelJudged(7),
            Metric.NotApplicable("no-gpu-in-this-tier"),
            Metric.NotReported("provider-omitted-usage"),
        ];

        foreach (Metric metric in metrics)
        {
            Assert.Equal(metric, Deserialize(Serialize(metric)));
        }
    }

    [Fact]
    public void Deserialize_NotApplicableDocument_GivesANotApplicableMetric()
    {
        Metric metric = Deserialize("""{"value":null,"label":"N/A","reason":"x"}""");

        Assert.Equal(MetricLabel.NotApplicable, metric.Label);
        Assert.Null(metric.Value);
        Assert.Equal("x", metric.Reason);
        Assert.Equal(Metric.NotApplicable("x"), metric);
    }

    // ---- Unit or currency: measured and estimated carry exactly one, model-judged an optional unit ----

    [Fact]
    public void MeasuredCost_SerializesTheCurrencyAndNoUnit()
    {
        Metric metric = Metric.MeasuredCost(0.3, "USD");

        Assert.Equal(MetricLabel.Measured, metric.Label);
        Assert.Equal("USD", metric.Currency);
        Assert.Null(metric.Unit);
        Assert.Equal("""{"value":0.3,"label":"measured","currency":"USD"}""", JsonSerializer.Serialize(metric));
        Assert.Equal(metric, Deserialize(JsonSerializer.Serialize(metric)));
    }

    [Fact]
    public void EstimatedQuantity_SerializesTheUnitAndNoCurrency()
    {
        Metric metric = Metric.EstimatedQuantity(12.5, "gCO2e");

        Assert.Equal(MetricLabel.Estimated, metric.Label);
        Assert.Equal("gCO2e", metric.Unit);
        Assert.Null(metric.Currency);
        Assert.Equal("""{"value":12.5,"label":"estimated","unit":"gCO2e"}""", JsonSerializer.Serialize(metric));
        Assert.Equal(metric, Deserialize(JsonSerializer.Serialize(metric)));
    }

    [Fact]
    public void ModelJudged_WithAUnit_CarriesTheUnitAndRoundTrips()
    {
        Metric metric = Metric.ModelJudged(4, "quality-points");

        Assert.Equal(MetricLabel.ModelJudged, metric.Label);
        Assert.Equal("quality-points", metric.Unit);
        Assert.Null(metric.Currency);
        Assert.Equal("""{"value":4,"label":"model-judged","unit":"quality-points"}""", JsonSerializer.Serialize(metric));
        Assert.Equal(metric, Deserialize(JsonSerializer.Serialize(metric)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MeasuredCost_WithoutCurrency_ThrowsArgumentExceptionNamingTheCurrency(string? currency)
    {
        ArgumentException failure = Assert.ThrowsAny<ArgumentException>(() => Metric.MeasuredCost(1, currency!));

        Assert.Equal("currency", failure.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void EstimatedQuantity_WithoutUnit_ThrowsArgumentExceptionNamingTheUnit(string? unit)
    {
        ArgumentException failure = Assert.ThrowsAny<ArgumentException>(() => Metric.EstimatedQuantity(1, unit!));

        Assert.Equal("unit", failure.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ModelJudged_WithABlankUnit_ThrowsArgumentExceptionNamingTheUnit(string? unit)
    {
        ArgumentException failure = Assert.ThrowsAny<ArgumentException>(() => Metric.ModelJudged(1, unit!));

        Assert.Equal("unit", failure.ParamName);
    }

    [Theory]
    [InlineData("""{"value":1,"label":"measured","unit":"J","currency":"USD"}""", "currency")]
    [InlineData("""{"value":1,"label":"measured"}""", "unit")]
    [InlineData("""{"value":1,"label":"estimated","unit":"gCO2e","currency":"USD"}""", "unit")]
    [InlineData("""{"value":1,"label":"estimated"}""", "currency")]
    public void Deserialize_MeasuredOrEstimatedWithBothOrNeitherUnitAndCurrency_ThrowsNamingTheParameter(string json, string parameter)
    {
        Assert.Equal(parameter, AssertRejectedWithArgumentException(() => Deserialize(json)).ParamName);
    }

    // ---- Echoed document text is bounded and neutralised ----

    [Fact]
    public void Deserialize_UnknownPropertyNameOf1000Characters_ThrowsAJsonExceptionWithABoundedMessage()
    {
        string json = $$"""{"value":1,"label":"measured","unit":"J","{{new string('p', 1000)}}":1}""";

        JsonException failure = Assert.Throws<JsonException>(() => Deserialize(json));

        Assert.DoesNotContain(new string('p', 65), failure.Message, StringComparison.Ordinal);
        Assert.Contains(new string('p', 64) + "...", failure.Message, StringComparison.Ordinal);
        Assert.True(failure.Message.Length < 300, $"The message is {failure.Message.Length} characters long.");
    }

    [Fact]
    public void Deserialize_UnknownLabelWithControlAndFormatCharacters_ThrowsAJsonExceptionWithoutThem()
    {
        // ESC, U+009B (C1 control), U+202E (right-to-left override) and U+2028 (line separator); JSON-escaped by the serializer.
        char[] hostile = ['\u001b', (char)0x9B, (char)0x202E, (char)0x2028];
        string labelJson = JsonSerializer.Serialize("bad" + new string(hostile) + "label");
        string json = $$"""{"value":1,"label":{{labelJson}},"unit":"J"}""";

        JsonException failure = Assert.Throws<JsonException>(() => Deserialize(json));

        Assert.Contains("bad????label", failure.Message, StringComparison.Ordinal);
        Assert.All(hostile, character => Assert.DoesNotContain(character, failure.Message));
    }

    [Fact]
    public void Deserialize_UnknownLabelOf1000Characters_ThrowsAJsonExceptionWithABoundedMessage()
    {
        string json = $$"""{"value":1,"label":"{{new string('l', 1000)}}","unit":"J"}""";

        JsonException failure = Assert.Throws<JsonException>(() => Deserialize(json));

        Assert.DoesNotContain(new string('l', 65), failure.Message, StringComparison.Ordinal);
        Assert.True(failure.Message.Length < 300, $"The message is {failure.Message.Length} characters long.");
    }

    // ---- Criterion 3: the shape comes from the converter, not from the caller's options ----

    [Fact]
    public void Serialize_WithPlainOptions_WritesTheExactShapes()
    {
        var plain = new JsonSerializerOptions();

        Assert.Equal("""{"value":180,"label":"measured","unit":"J"}""", JsonSerializer.Serialize(Metric.Measured(180, "J"), plain));
        Assert.Equal("""{"value":0.5,"label":"estimated","currency":"USD"}""", JsonSerializer.Serialize(Metric.Estimated(0.5, "USD"), plain));
        Assert.Equal("""{"value":7,"label":"model-judged"}""", JsonSerializer.Serialize(Metric.ModelJudged(7), plain));
        Assert.Equal("""{"value":null,"label":"N/A","reason":"x"}""", JsonSerializer.Serialize(Metric.NotApplicable("x"), plain));
        Assert.Equal("""{"value":null,"label":"not-reported","reason":"x"}""", JsonSerializer.Serialize(Metric.NotReported("x"), plain));
    }

    [Theory]
    [InlineData(JsonIgnoreCondition.WhenWritingDefault)]
    [InlineData(JsonIgnoreCondition.WhenWritingNull)]
    public void Serialize_WithACallerIgnoreCondition_StillWritesValueAndLabel(JsonIgnoreCondition condition)
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = condition };

        Assert.Equal("""{"value":null,"label":"N/A","reason":"x"}""", JsonSerializer.Serialize(Metric.NotApplicable("x"), options));
        Assert.Equal("""{"value":0,"label":"measured","unit":"J"}""", JsonSerializer.Serialize(Metric.Measured(0, "J"), options));
    }

    [Fact]
    public void RoundTrip_WithPlainOptions_ComesBackEqual()
    {
        var plain = new JsonSerializerOptions();
        Metric[] metrics = [Metric.Measured(180, "J"), Metric.Estimated(0.5, "USD"), Metric.ModelJudged(7), Metric.NotApplicable("x"), Metric.NotReported("y")];

        foreach (Metric metric in metrics)
        {
            Metric? back = JsonSerializer.Deserialize<Metric>(JsonSerializer.Serialize(metric, plain), plain);

            Assert.Equal(metric, back);
        }
    }

    [Fact]
    public void RoundTrip_ThroughASourceGeneratedContext_ComesBackEqual()
    {
        Metric[] metrics =
        [
            Metric.Measured(180, "J"),
            Metric.Measured(0, "J"),
            Metric.Estimated(0.5, "USD"),
            Metric.ModelJudged(7),
            Metric.NotApplicable("no-gpu-in-this-tier"),
            Metric.NotReported("provider-omitted-usage"),
        ];

        foreach (Metric metric in metrics)
        {
            string json = JsonSerializer.Serialize(metric, MetricTestJsonContext.Default.Metric);

            Assert.Equal(metric, JsonSerializer.Deserialize(json, MetricTestJsonContext.Default.Metric));
        }
    }

    [Fact]
    public void SourceGeneratedContext_NotApplicableWithAValue_ThrowsTheUnwrappedArgumentException()
    {
        Exception failure = Assert.ThrowsAny<Exception>(
            () => JsonSerializer.Deserialize("""{"value":0,"label":"N/A","reason":"x"}""", MetricTestJsonContext.Default.Metric));

        Assert.Equal("value", Assert.IsType<ArgumentException>(failure, exactMatch: false).ParamName);
    }

    [Fact]
    public void SourceGeneratedContext_MissingLabel_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize("""{"value":1,"unit":"J"}""", MetricTestJsonContext.Default.Metric));
    }

    [Fact]
    public void ConverterLabelStrings_EqualTheMetricLabelMemberNames()
    {
        // The converter keeps its own ordered table; every enum member's [JsonStringEnumMemberName] must equal what the converter writes for it.
        var plain = new JsonSerializerOptions();

        foreach (MetricLabel label in Enum.GetValues<MetricLabel>())
        {
            Metric metric = label switch
            {
                MetricLabel.Measured => Metric.Measured(1, "J"),
                MetricLabel.Estimated => Metric.Estimated(1, "USD"),
                MetricLabel.ModelJudged => Metric.ModelJudged(1),
                MetricLabel.NotApplicable => Metric.NotApplicable("x"),
                MetricLabel.NotReported => Metric.NotReported("x"),
                _ => throw new InvalidOperationException($"Unhandled label {label}."),
            };
            string expected = typeof(MetricLabel).GetField(label.ToString())!.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()!.Name;

            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(metric, plain));
            Assert.Equal(expected, document.RootElement.GetProperty("label").GetString());
            Assert.Equal(label, Deserialize(JsonSerializer.Serialize(metric, plain)).Label);
        }
    }

    [Fact]
    public void MetricLabel_IsNumberedFromOneSoTheDefaultIsNotALabel()
    {
        Assert.Equal(1, (int)MetricLabel.Measured);
        Assert.Equal(5, (int)MetricLabel.NotReported);
        Assert.False(Enum.IsDefined(default(MetricLabel)));
    }

    // ---- Criterion 2: the factories are the only public entry points ----

    [Fact]
    public void Metric_HasNoPublicConstructor()
    {
        Assert.Empty(typeof(Metric).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void Metric_PublicStaticMethods_AreExactlyTheSevenFactoriesAndTheModelJudgedOverload()
    {
        string[] factories = [.. typeof(Metric)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)];

        string[] expectedNames = ["Estimated", "EstimatedQuantity", "Measured", "MeasuredCost", "ModelJudged", "ModelJudged", "NotApplicable", "NotReported"];
        Assert.Equal(expectedNames, factories);
    }

    [Fact]
    public void Metric_Properties_HaveNoPublicSetter()
    {
        // An init or set accessor would let a caller change the label after the constructor validated it (design-review N-01).
        Assert.All(typeof(Metric).GetProperties(BindingFlags.Public | BindingFlags.Instance), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void Metric_IsASealedRecord()
    {
        Assert.True(typeof(Metric).IsSealed);
        Assert.NotNull(typeof(Metric).GetMethod("<Clone>$"));
    }

    // ---- Criterion 4: reading flags ----

    [Theory]
    [InlineData(ReadingFlags.SamplingFallback, "sampling-fallback")]
    [InlineData(ReadingFlags.LowResolution, "low-resolution")]
    [InlineData(ReadingFlags.SharedWindow, "shared-window")]
    [InlineData(ReadingFlags.BaselineUnstable, "baseline-unstable")]
    [InlineData(ReadingFlags.BelowBaseline, "below-baseline")]
    [InlineData(ReadingFlags.Throttled, "throttled")]
    [InlineData(ReadingFlags.PartialOffload, "partial-offload")]
    public void ToStrings_SingleFlag_ReturnsItsVocabularyString(ReadingFlags flag, string expected)
    {
        string[] expectedNames = [expected];
        Assert.Equal(expectedNames, ReadingFlagNames.ToStrings(flag));
    }

    [Fact]
    public void ToStrings_BelowBaselineAndThrottled_ReturnsBothInBitOrder()
    {
        Assert.Equal(BelowBaselineAndThrottled, ReadingFlagNames.ToStrings(ReadingFlags.BelowBaseline | ReadingFlags.Throttled));
        Assert.Equal(BelowBaselineAndThrottled, ReadingFlagNames.ToStrings(ReadingFlags.Throttled | ReadingFlags.BelowBaseline));
    }

    [Fact]
    public void ToStrings_AllSevenFlags_ReturnsTheWholeVocabularyInBitOrder()
    {
        ReadingFlags all = ReadingFlags.SamplingFallback | ReadingFlags.LowResolution | ReadingFlags.SharedWindow
            | ReadingFlags.BaselineUnstable | ReadingFlags.BelowBaseline | ReadingFlags.Throttled | ReadingFlags.PartialOffload;

        Assert.Equal(FlagVocabulary, ReadingFlagNames.ToStrings(all));
    }

    [Fact]
    public void ToStrings_None_ReturnsAnEmptyList()
    {
        IReadOnlyList<string> names = ReadingFlagNames.ToStrings(ReadingFlags.None);

        Assert.NotNull(names);
        Assert.Empty(names);
    }

    [Theory]
    [InlineData("sampling-fallback", ReadingFlags.SamplingFallback)]
    [InlineData("low-resolution", ReadingFlags.LowResolution)]
    [InlineData("shared-window", ReadingFlags.SharedWindow)]
    [InlineData("baseline-unstable", ReadingFlags.BaselineUnstable)]
    [InlineData("below-baseline", ReadingFlags.BelowBaseline)]
    [InlineData("throttled", ReadingFlags.Throttled)]
    [InlineData("partial-offload", ReadingFlags.PartialOffload)]
    public void Parse_EveryVocabularyName_ReturnsItsFlag(string name, ReadingFlags expected)
    {
        Assert.Equal(expected, ReadingFlagNames.Parse(name));
        Assert.True(ReadingFlagNames.TryParse(name, out ReadingFlags flag));
        Assert.Equal(expected, flag);
        string[] expectedNames = [name];
        Assert.Equal(expectedNames, ReadingFlagNames.ToStrings(expected));
    }

    [Theory]
    [InlineData("overheated")]
    [InlineData("")]
    public void Parse_UnknownName_ThrowsArgumentExceptionNamingTheInput(string name)
    {
        ArgumentException failure = Assert.ThrowsAny<ArgumentException>(() => ReadingFlagNames.Parse(name));

        Assert.Contains(name, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("overheated")]
    [InlineData("")]
    [InlineData("Throttled")]
    public void TryParse_UnknownName_ReturnsFalseAndNone(string name)
    {
        Assert.False(ReadingFlagNames.TryParse(name, out ReadingFlags flag));
        Assert.Equal(ReadingFlags.None, flag);
    }

    [Fact]
    public void TryParse_Null_ReturnsFalseAndNone()
    {
        Assert.False(ReadingFlagNames.TryParse(null, out ReadingFlags flag));
        Assert.Equal(ReadingFlags.None, flag);
    }

    [Fact]
    public void Parse_Null_ThrowsArgumentNullException()
    {
        ArgumentNullException failure = Assert.Throws<ArgumentNullException>(() => ReadingFlagNames.Parse(null!));

        Assert.Equal("name", failure.ParamName);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(-1)]
    [InlineData(255)]
    [InlineData(int.MinValue)]
    public void ToStrings_WithABitOutsideTheVocabulary_ThrowsArgumentOutOfRangeException(int raw)
    {
        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => ReadingFlagNames.ToStrings((ReadingFlags)raw));

        Assert.Equal("flags", failure.ParamName);
    }

    [Fact]
    public void ToStrings_KnownFlagCombinedWithAnUndefinedBit_ThrowsInsteadOfDroppingTheBit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadingFlagNames.ToStrings(ReadingFlags.Throttled | (ReadingFlags)128));
    }

    [Fact]
    public void Parse_UnknownNameOf1000Characters_EchoesAtMost64CharactersAndAnEllipsis()
    {
        string input = new('a', 1000);

        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReadingFlagNames.Parse(input));

        Assert.Contains(new string('a', 64) + "...", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 65), failure.Message, StringComparison.Ordinal);
        Assert.True(failure.Message.Length < 200, $"The message is {failure.Message.Length} characters long.");
    }

    [Fact]
    public void Parse_UnknownNameWithASurrogatePairStraddlingTheCut_LeavesNoLoneSurrogate()
    {
        // The emoji occupies indexes 63 and 64, so a plain cut at 64 characters would keep its high surrogate and drop the low one.
        string input = new string('a', 63) + char.ConvertFromUtf32(0x1F600) + "tail";

        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReadingFlagNames.Parse(input));

        Assert.Contains(new string('a', 63) + "...", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(failure.Message, char.IsSurrogate);
    }

    [Fact]
    public void Parse_UnknownNameOfExactly64Characters_IsEchoedWholeWithoutAnEllipsis()
    {
        string input = new('b', 64);

        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReadingFlagNames.Parse(input));

        Assert.Contains(input, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("...", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownNameWithControlCharacters_EchoesThemAsQuestionMarks()
    {
        const char rightToLeftOverride = (char)0x202E;
        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReadingFlagNames.Parse("bad\u001b[31m\nname\u007f\0\u009b" + rightToLeftOverride));

        // U+009B is a C1 control (a one-character CSI) and U+202E a format character (right-to-left override); both are neutralised too.
        Assert.Contains("bad?[31m?name????", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u009b', failure.Message);
        Assert.DoesNotContain(rightToLeftOverride, failure.Message);
        Assert.DoesNotContain(failure.Message, char.IsControl);
    }

    [Fact]
    public void ReadingFlags_IsAFlagsEnumWithSevenDistinctPowerOfTwoMembersAndNone()
    {
        Assert.NotNull(typeof(ReadingFlags).GetCustomAttribute<FlagsAttribute>());
        Assert.Equal(0, (int)ReadingFlags.None);

        int[] values = [.. Enum.GetValues<ReadingFlags>().Select(flag => (int)flag).Where(value => value != 0)];

        Assert.Equal(7, values.Length);
        Assert.Equal(7, values.Distinct().Count());
        Assert.All(values, value => Assert.True(value > 0 && (value & (value - 1)) == 0, $"{value} is not a power of two."));
        string[] expectedNames = ["BaselineUnstable", "BelowBaseline", "LowResolution", "PartialOffload", "SamplingFallback", "SharedWindow", "Throttled"];

        Assert.Equal(
            expectedNames,
            Enum.GetValues<ReadingFlags>().Where(flag => flag != ReadingFlags.None).Select(flag => flag.ToString()).Order(StringComparer.Ordinal));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Metric))]
internal sealed partial class MetricTestJsonContext : JsonSerializerContext
{
}