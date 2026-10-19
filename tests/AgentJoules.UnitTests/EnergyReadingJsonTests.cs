using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>The JSON shape of <see cref="EnergyReading"/>, its constructor checks (which deserialisation runs) and its two enum converters.</summary>
[Trait("Spec", "REQ-005")]
public sealed class EnergyReadingJsonTests
{
    private const double Tolerance = 1e-9;

    private static readonly JsonSerializerOptions ReadingOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions PlainEnumOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private static PowerSample Sample(double seconds, double watts) =>
        new(seconds, watts, ThrottleReasons.Unsupported);

    private static PowerSample[] Constant(int count, double watts) =>
        [.. Enumerable.Range(0, count).Select(i => Sample(i / 10.0, watts))];

    private static EnergyReading Compute(
        IReadOnlyList<PowerSample> samples,
        double stop = 3.0,
        EnergyMethod method = EnergyMethod.Sampling,
        long? delta = null,
        double? baseline = null,
        bool fallback = false) =>
        ReadingCalculator.Compute(new ReadingInput(samples, 0.0, stop, method, delta, baseline, 10.0, fallback));

    private static EnergyReading FullReading() => Compute(Constant(31, 60.0), baseline: 20.0);

    private static EnergyReading Build(
        double duration = 3.0,
        EnergyMethod method = EnergyMethod.Sampling,
        int count = 31,
        double rate = 10.0,
        ReadingFlags flags = ReadingFlags.None,
        IReadOnlyList<string>? reasons = null,
        Metric? gross = null,
        Metric? net = null,
        Metric? average = null,
        Metric? peak = null) =>
        new(
            gross ?? Metric.Measured(180.0, "J"),
            net ?? Metric.Measured(120.0, "J"),
            average ?? Metric.Measured(60.0, "W"),
            peak ?? Metric.Measured(60.0, "W"),
            duration,
            method,
            count,
            rate,
            flags,
            reasons);

    // Serialises a valid reading, lets the caller replace properties of the document, and returns the text.
    private static string Mutated(params (string Property, string? RawJson)[] changes)
    {
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(FullReading(), ReadingOptions))!.AsObject();
        foreach ((string property, string? rawJson) in changes)
        {
            root[property] = rawJson is null ? null : JsonNode.Parse(rawJson);
        }

        return root.ToJsonString();
    }

    private const string NetBelowZeroJson = "{\"value\":-5,\"label\":\"measured\",\"unit\":\"J\"}";

    [Fact]
    public void TheReadingSerialisesToTheDocumentedShape()
    {
        EnergyReading reading = FullReading();

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(reading, ReadingOptions));
        JsonElement root = document.RootElement;

        string[] names = [.. root.EnumerateObject().Select(p => p.Name)];
        string[] expectedNames =
        [
            "grossJoules", "netJoules", "avgW", "peakW", "durationSeconds", "method", "sampleCount", "sampleRateHz", "flags", "throttleReasons",
        ];
        Assert.Equal(expectedNames, names);
        Assert.Equal("measured", root.GetProperty("grossJoules").GetProperty("label").GetString());
        Assert.Equal(180.0, root.GetProperty("grossJoules").GetProperty("value").GetDouble(), Tolerance);
        Assert.Equal("J", root.GetProperty("grossJoules").GetProperty("unit").GetString());
        Assert.Equal(120.0, root.GetProperty("netJoules").GetProperty("value").GetDouble(), Tolerance);
        Assert.Equal("W", root.GetProperty("avgW").GetProperty("unit").GetString());
        Assert.Equal(60.0, root.GetProperty("peakW").GetProperty("value").GetDouble(), Tolerance);
        Assert.Equal(3.0, root.GetProperty("durationSeconds").GetDouble(), Tolerance);
        Assert.Equal("sampling", root.GetProperty("method").GetString());
        Assert.Equal(31, root.GetProperty("sampleCount").GetInt32());
        Assert.Equal(10.0, root.GetProperty("sampleRateHz").GetDouble(), Tolerance);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("flags").ValueKind);
        Assert.Equal(0, root.GetProperty("flags").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("throttleReasons").ValueKind);
    }

    [Fact]
    public void TheDefaultOptionsWriteTheStrictMethodStringAndTheNormativeMetricNames()
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(FullReading()));
        JsonElement root = document.RootElement;

        Assert.Equal("sampling", root.GetProperty("Method").GetString());
        Assert.Equal("W", root.GetProperty("avgW").GetProperty("unit").GetString());
        Assert.Equal("W", root.GetProperty("peakW").GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("Flags").ValueKind);
    }

    [Fact]
    public void ANotReportedMetricSerialisesItsReasonAndANullValue()
    {
        EnergyReading reading = Compute(Constant(31, 60.0));

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(reading, ReadingOptions));
        JsonElement net = document.RootElement.GetProperty("netJoules");

        Assert.Equal("not-reported", net.GetProperty("label").GetString());
        Assert.Equal("no-baseline", net.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, net.GetProperty("value").ValueKind);
    }

    [Fact]
    public void TheFlagsOfAReadingSerialiseAsAnArrayOfVocabularyStringsInBitOrder()
    {
        PowerSample[] samples = [Sample(0.0, 15.0), Sample(0.1, 15.0), Sample(0.15, 15.0)];
        EnergyReading reading = Compute(samples, stop: 0.15, baseline: 20.0, fallback: true);

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(reading, ReadingOptions));

        string?[] flags = [.. document.RootElement.GetProperty("flags").EnumerateArray().Select(e => e.GetString())];
        string?[] expected = ["sampling-fallback", "low-resolution", "below-baseline"];
        Assert.Equal(expected, flags);
    }

    [Theory]
    [InlineData(ReadingFlags.None, "[]")]
    [InlineData(ReadingFlags.BelowBaseline, "[\"below-baseline\"]")]
    [InlineData(ReadingFlags.BelowBaseline | ReadingFlags.Throttled, "[\"below-baseline\",\"throttled\"]")]
    public void ReadingFlagsSerialiseAsAnArrayOfVocabularyStrings(ReadingFlags flags, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(flags));
    }

    [Fact]
    public void TheFlagsAndTheMethodOfAReadingStayStrictEvenWhenTheOptionsCarryAnEnumConverterAndNoNamingPolicy()
    {
        // A converter in the options outranks the attribute on the enum type, so the reading pins its own on the properties.
        EnergyReading reading = Build(flags: ReadingFlags.Throttled, method: EnergyMethod.Counter);

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(reading, PlainEnumOptions));

        Assert.Equal("throttled", Assert.Single(document.RootElement.GetProperty("Flags").EnumerateArray()).GetString());
        Assert.Equal("counter", document.RootElement.GetProperty("Method").GetString());
    }

    [Fact]
    public void AReadingRoundTripsThroughJsonWithTheCamelCaseOptions()
    {
        PowerSample[] samples = [Sample(0.0, 15.0), Sample(0.1, 15.0), Sample(0.15, 15.0)];
        EnergyReading reading = Compute(samples, stop: 0.15, baseline: 20.0, fallback: true);

        EnergyReading? copy = JsonSerializer.Deserialize<EnergyReading>(JsonSerializer.Serialize(reading, ReadingOptions), ReadingOptions);

        Assert.Equal(reading, copy);
    }

    [Fact]
    public void AReadingRoundTripsThroughJsonWithTheDefaultOptions()
    {
        EnergyReading reading = FullReading();

        EnergyReading? copy = JsonSerializer.Deserialize<EnergyReading>(JsonSerializer.Serialize(reading));

        Assert.Equal(reading, copy);
    }

    [Fact]
    public void ANotReportedReadingRoundTripsThroughJson()
    {
        EnergyReading reading = Compute(Constant(31, 60.0), method: EnergyMethod.Counter, delta: -5_000L);

        EnergyReading? copy = JsonSerializer.Deserialize<EnergyReading>(JsonSerializer.Serialize(reading, ReadingOptions), ReadingOptions);

        Assert.Equal(reading, copy);
    }

    [Fact]
    public void AReadingWithThrottleReasonsRoundTripsTheirNames()
    {
        EnergyReading reading = Build(reasons: ["sw-power-cap", "hw-thermal-slowdown"]);

        EnergyReading? copy = JsonSerializer.Deserialize<EnergyReading>(JsonSerializer.Serialize(reading, ReadingOptions), ReadingOptions);

        Assert.NotNull(copy);
        string[] expected = ["sw-power-cap", "hw-thermal-slowdown"];
        Assert.Equal(expected, copy.ThrottleReasons);
    }

    [Theory]
    [InlineData("[]", ReadingFlags.None)]
    [InlineData("[\"throttled\",\"below-baseline\"]", ReadingFlags.BelowBaseline | ReadingFlags.Throttled)]
    public void ReadingFlagsReadAnArrayOfVocabularyStringsInAnyOrder(string json, ReadingFlags expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<ReadingFlags>(json));
    }

    [Theory]
    [InlineData("[\"no-such-flag\"]")]
    [InlineData("[\"Below-Baseline\"]")]
    [InlineData("[\"below-baseline\",\"below-baseline\"]")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[true]")]
    [InlineData("[[\"throttled\"]]")]
    [InlineData("[{}]")]
    [InlineData("\"below-baseline\"")]
    [InlineData("16")]
    [InlineData("null")]
    [InlineData("{}")]
    public void ReadingFlagsRejectAnythingButADuplicateFreeArrayOfKnownNames(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReadingFlags>(json));
    }

    [Fact]
    public void AnUnknownFlagNameInTheRejectionMessageIsBoundedAndHasNoControlCharacters()
    {
        string json = JsonSerializer.Serialize(new[] { "bad\nflag" + new string('x', 200) });

        JsonException failure = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReadingFlags>(json));

        Assert.DoesNotContain("\n", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 100), failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EnergyMethod.Counter, "counter")]
    [InlineData(EnergyMethod.Sampling, "sampling")]
    public void TheMethodSerialisesAsItsLowerCaseNameWithOrWithoutAnEnumConverterInTheOptions(EnergyMethod method, string expected)
    {
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(method));
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(method, PlainEnumOptions));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("7")]
    [InlineData("\"turbo\"")]
    [InlineData("null")]
    public void TheMethodReadsNothingButItsTwoNames(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnergyMethod>(json));
    }

    [Theory]
    [InlineData("\"counter\"", EnergyMethod.Counter)]
    [InlineData("\"sampling\"", EnergyMethod.Sampling)]
    public void TheMethodReadsItsTwoNames(string json, EnergyMethod expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<EnergyMethod>(json));
    }

    [Theory]
    [InlineData("method", "0")]
    [InlineData("method", "7")]
    [InlineData("method", "\"turbo\"")]
    [InlineData("method", "null")]
    [InlineData("flags", "[\"no-such-flag\"]")]
    [InlineData("flags", "\"throttled\"")]
    [InlineData("sampleCount", "\"many\"")]
    public void AMalformedTokenInAReadingDocumentIsAJsonException(string property, string rawJson)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnergyReading>(Mutated((property, rawJson)), ReadingOptions));
    }

    // The constructor checks run on deserialisation and surface unwrapped, as an ArgumentException (a null metric is its subclass).
    [Theory]
    [InlineData("grossJoules", "null")]
    [InlineData("netJoules", "null")]
    [InlineData("avgW", "null")]
    [InlineData("peakW", "null")]
    [InlineData("durationSeconds", "-3")]
    [InlineData("durationSeconds", "0")]
    [InlineData("sampleCount", "-1")]
    [InlineData("sampleRateHz", "0")]
    [InlineData("sampleRateHz", "-5")]
    [InlineData("throttleReasons", "[\"sw-power-cap\",\"\"]")]
    [InlineData("throttleReasons", "[\" \"]")]
    [InlineData("throttleReasons", "[null]")]
    public void ADocumentThatViolatesAnInvariantIsAnArgumentException(string property, string rawJson)
    {
        Assert.ThrowsAny<ArgumentException>(() => JsonSerializer.Deserialize<EnergyReading>(Mutated((property, rawJson)), ReadingOptions));
    }

    [Fact]
    public void AnEmptyDocumentIsAnArgumentExceptionBecauseItsMetricsAreMissing()
    {
        Assert.ThrowsAny<ArgumentException>(() => JsonSerializer.Deserialize<EnergyReading>("{}", ReadingOptions));
    }

    [Theory]
    [InlineData(0, "grossJoules")]
    [InlineData(1, "netJoules")]
    [InlineData(2, "averageWatts")]
    [InlineData(3, "peakWatts")]
    public void ANullMetricIsRejected(int index, string parameter)
    {
        Metric[] metrics = [Metric.Measured(1.0, "J"), Metric.Measured(1.0, "J"), Metric.Measured(1.0, "W"), Metric.Measured(1.0, "W")];
        metrics[index] = null!;

        var failure = Assert.Throws<ArgumentNullException>(
            () => new EnergyReading(metrics[0], metrics[1], metrics[2], metrics[3], 3.0, EnergyMethod.Sampling, 31, 10.0, ReadingFlags.None, null));

        Assert.Equal(parameter, failure.ParamName);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonPositiveOrNonFiniteDurationIsRejected(double duration)
    {
        Assert.Equal("durationSeconds", Assert.Throws<ArgumentException>(() => Build(duration: duration)).ParamName);
    }

    [Theory]
    [InlineData((EnergyMethod)0)]
    [InlineData((EnergyMethod)7)]
    public void AnUndefinedMethodIsRejectedByTheConstructor(EnergyMethod method)
    {
        Assert.Equal("method", Assert.Throws<ArgumentException>(() => Build(method: method)).ParamName);
    }

    [Fact]
    public void ANegativeSampleCountIsRejectedAndZeroIsLegal()
    {
        Assert.Equal("sampleCount", Assert.Throws<ArgumentException>(() => Build(count: -1)).ParamName);
        Assert.Equal(0, Build(count: 0).SampleCount);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonPositiveOrNonFiniteSampleRateIsRejectedByTheConstructor(double rate)
    {
        Assert.Equal("sampleRateHz", Assert.Throws<ArgumentException>(() => Build(rate: rate)).ParamName);
    }

    [Fact]
    public void FlagsWithABitOutsideTheVocabularyAreRejected()
    {
        Assert.Equal("flags", Assert.Throws<ArgumentException>(() => Build(flags: (ReadingFlags)1024)).ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void AThrottleReasonThatIsNullOrBlankIsRejected(string? reason)
    {
        Assert.Equal("throttleReasons", Assert.Throws<ArgumentException>(() => Build(reasons: ["sw-power-cap", reason!])).ParamName);
    }

    [Fact]
    public void TheThrottleReasonsAreCopiedSoALaterChangeToTheSourceListDoesNotReachTheReading()
    {
        List<string> reasons = ["sw-power-cap"];
        EnergyReading reading = Build(reasons: reasons);

        reasons.Add("hw-slowdown");
        reasons[0] = "changed";

        string[] expected = ["sw-power-cap"];
        Assert.Equal(expected, reading.ThrottleReasons);
    }

    [Fact]
    public void AnEmptyThrottleReasonListMeansSupportedWithNothingActiveAndStaysDistinctFromNull()
    {
        Assert.NotNull(Build(reasons: []).ThrottleReasons);
        Assert.Empty(Build(reasons: []).ThrottleReasons!);
        Assert.Null(Build(reasons: null).ThrottleReasons);
    }

    [Fact]
    public void AMeasuredNegativeNetWithItsFlagIsAccepted()
    {
        EnergyReading reading = Build(net: Metric.Measured(-5.0, "J"), flags: ReadingFlags.BelowBaseline);

        Assert.Equal(-5.0, reading.NetJoules.Value);
        Assert.True(reading.Flags.HasFlag(ReadingFlags.BelowBaseline));
    }

    [Fact]
    public void AMeasuredNegativeNetWithoutTheBelowBaselineFlagIsRejected()
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => Build(net: Metric.Measured(-5.0, "J")));

        Assert.Equal("flags", failure.ParamName);
        Assert.Contains("BelowBaseline", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBelowBaselineFlagWithoutAMeasuredNegativeNetIsRejected()
    {
        foreach (Metric net in new[] { Metric.Measured(120.0, "J"), Metric.Measured(0.0, "J"), Metric.NotReported("no-baseline"), Metric.NotApplicable("n/a") })
        {
            ArgumentException failure = Assert.Throws<ArgumentException>(() => Build(net: net, flags: ReadingFlags.BelowBaseline));

            Assert.Equal("flags", failure.ParamName);
            Assert.Contains("BelowBaseline", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSamplingFallbackFlagRequiresTheSamplingMethod()
    {
        Assert.Equal(ReadingFlags.SamplingFallback, Build(flags: ReadingFlags.SamplingFallback, method: EnergyMethod.Sampling).Flags);

        ArgumentException failure = Assert.Throws<ArgumentException>(() => Build(flags: ReadingFlags.SamplingFallback, method: EnergyMethod.Counter));

        Assert.Equal("flags", failure.ParamName);
        Assert.Contains("SamplingFallback", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "grossJoules", "W")]
    [InlineData(1, "netJoules", "W")]
    [InlineData(2, "averageWatts", "J")]
    [InlineData(3, "peakWatts", "J")]
    public void AMeasuredMetricWithTheWrongUnitIsRejected(int index, string parameter, string wrongUnit)
    {
        Metric wrong = Metric.Measured(1.0, wrongUnit);

        ArgumentException failure = Assert.Throws<ArgumentException>(() => BuildWith(index, wrong));

        Assert.Equal(parameter, failure.ParamName);
        Assert.Contains("unit", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "grossJoules")]
    [InlineData(1, "netJoules")]
    [InlineData(2, "averageWatts")]
    [InlineData(3, "peakWatts")]
    public void AMeasuredMetricWithACurrencyInsteadOfAUnitIsRejected(int index, string parameter)
    {
        Assert.Equal(parameter, Assert.Throws<ArgumentException>(() => BuildWith(index, Metric.MeasuredCost(1.0, "USD"))).ParamName);
    }

    [Theory]
    [InlineData(0, "grossJoules", "J")]
    [InlineData(1, "netJoules", "J")]
    [InlineData(2, "averageWatts", "W")]
    [InlineData(3, "peakWatts", "W")]
    public void AnEstimatedOrModelJudgedMetricIsRejected(int index, string parameter, string unit)
    {
        foreach (Metric wrongLabel in new[] { Metric.EstimatedQuantity(1.0, unit), Metric.ModelJudged(1.0, unit) })
        {
            ArgumentException failure = Assert.Throws<ArgumentException>(() => BuildWith(index, wrongLabel));

            Assert.Equal(parameter, failure.ParamName);
            Assert.Contains("measured, not-reported or N/A", failure.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ANotReportedOrNotApplicableMetricIsAccepted(int index)
    {
        Assert.NotNull(BuildWith(index, Metric.NotReported("insufficient-samples")));
        Assert.NotNull(BuildWith(index, Metric.NotApplicable("not applicable")));
    }

    // Builds a valid reading with one of its four metrics replaced; the net stays non-negative so no flag is needed.
    private static EnergyReading BuildWith(int index, Metric replacement) => index switch
    {
        0 => Build(gross: replacement),
        1 => Build(net: replacement),
        2 => Build(average: replacement),
        _ => Build(peak: replacement),
    };

    // The same rules through deserialisation: each surfaces as an unwrapped ArgumentException.
    [Theory]
    [InlineData("netJoules", "{\"value\":-5,\"label\":\"measured\",\"unit\":\"J\"}", null)]
    [InlineData("flags", "[\"below-baseline\"]", null)]
    [InlineData("method", "\"counter\"", "[\"sampling-fallback\"]")]
    [InlineData("grossJoules", "{\"value\":180,\"label\":\"measured\",\"unit\":\"W\"}", null)]
    [InlineData("netJoules", "{\"value\":120,\"label\":\"measured\",\"unit\":\"W\"}", null)]
    [InlineData("avgW", "{\"value\":60,\"label\":\"measured\",\"unit\":\"J\"}", null)]
    [InlineData("peakW", "{\"value\":60,\"label\":\"measured\",\"currency\":\"USD\"}", null)]
    [InlineData("grossJoules", "{\"value\":180,\"label\":\"estimated\",\"unit\":\"J\"}", null)]
    [InlineData("avgW", "{\"value\":60,\"label\":\"estimated\",\"unit\":\"W\"}", null)]
    [InlineData("peakW", "{\"value\":60,\"label\":\"model-judged\",\"unit\":\"W\"}", null)]
    public void ADocumentThatBreaksACrossFieldRuleIsAnArgumentException(string property, string rawJson, string? flagsJson)
    {
        string json = flagsJson is null ? Mutated((property, rawJson)) : Mutated((property, rawJson), ("flags", flagsJson));

        Assert.ThrowsAny<ArgumentException>(() => JsonSerializer.Deserialize<EnergyReading>(json, ReadingOptions));
    }

    [Fact]
    public void ADocumentWithANegativeNetAndItsFlagIsAccepted()
    {
        string json = Mutated(("netJoules", NetBelowZeroJson), ("flags", "[\"below-baseline\"]"));

        EnergyReading? reading = JsonSerializer.Deserialize<EnergyReading>(json, ReadingOptions);

        Assert.NotNull(reading);
        Assert.Equal(-5.0, reading.NetJoules.Value);
    }

    [Fact]
    public void AFullReadingWithThreeFlagsRoundTripsThroughTheSourceGeneratedContext()
    {
        PowerSample[] samples = [Sample(0.0, 15.0), Sample(0.1, 15.0), Sample(0.15, 15.0)];
        EnergyReading reading = Compute(samples, stop: 0.15, baseline: 20.0, fallback: true);
        Assert.Equal(ReadingFlags.SamplingFallback | ReadingFlags.LowResolution | ReadingFlags.BelowBaseline, reading.Flags);

        string json = JsonSerializer.Serialize(reading, EnergyReadingTestJsonContext.Default.EnergyReading);
        EnergyReading? copy = JsonSerializer.Deserialize(json, EnergyReadingTestJsonContext.Default.EnergyReading);

        Assert.Equal(reading, copy);
    }

    [Fact]
    public void ACounterWentBackwardsReadingRoundTripsThroughTheSourceGeneratedContext()
    {
        EnergyReading reading = Compute(Constant(31, 60.0), method: EnergyMethod.Counter, delta: -5_000L);

        string json = JsonSerializer.Serialize(reading, EnergyReadingTestJsonContext.Default.EnergyReading);
        EnergyReading? copy = JsonSerializer.Deserialize(json, EnergyReadingTestJsonContext.Default.EnergyReading);

        Assert.Equal(reading, copy);
        Assert.Contains("counter-went-backwards", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadingWithTwoThrottleReasonsRoundTripsThroughTheSourceGeneratedContext()
    {
        string[] reasons = ["sw-power-cap", "hw-thermal-slowdown"];
        EnergyReading reading = Build(reasons: reasons);

        string json = JsonSerializer.Serialize(reading, EnergyReadingTestJsonContext.Default.EnergyReading);
        EnergyReading? copy = JsonSerializer.Deserialize(json, EnergyReadingTestJsonContext.Default.EnergyReading);

        Assert.NotNull(copy);
        Assert.Equal(reasons, copy.ThrottleReasons);
    }

    [Theory]
    [InlineData("method", "1")]
    [InlineData("flags", "\"BelowBaseline\"")]
    [InlineData("flags", "[16]")]
    public void TheSourceGeneratedContextRejectsAMalformedTokenWithAJsonException(string property, string rawJson)
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize(Mutated((property, rawJson)), EnergyReadingTestJsonContext.Default.EnergyReading));
    }

    [Fact]
    public void TheMethodNameIsMatchedCaseSensitivelyBecauseItIsAnExplicitMemberName()
    {
        // Measured on .NET 10: a member with [JsonStringEnumMemberName] is matched by that exact name, so "Sampling" is not "sampling".
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize(Mutated(("method", "\"Sampling\"")), EnergyReadingTestJsonContext.Default.EnergyReading));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnergyReading>(Mutated(("method", "\"Counter\"")), ReadingOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnergyMethod>("\"Counter\""));
    }

    [Fact]
    public void TheSourceGeneratedContextRunsTheConstructorChecksAsAnArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => JsonSerializer.Deserialize(Mutated(("grossJoules", null)), EnergyReadingTestJsonContext.Default.EnergyReading));
        Assert.ThrowsAny<ArgumentException>(
            () => JsonSerializer.Deserialize("{}", EnergyReadingTestJsonContext.Default.EnergyReading));
    }

    [Fact]
    public void AHolderWithoutThePropertyAttributeStillWritesTheFlagsAsAnArrayThroughTheSourceGeneratedContext()
    {
        string json = JsonSerializer.Serialize(new FlagsHolder(ReadingFlags.BelowBaseline | ReadingFlags.Throttled), EnergyReadingTestJsonContext.Default.FlagsHolder);

        Assert.Equal("{\"flags\":[\"below-baseline\",\"throttled\"]}", json);
        Assert.Equal(
            ReadingFlags.BelowBaseline | ReadingFlags.Throttled,
            JsonSerializer.Deserialize(json, EnergyReadingTestJsonContext.Default.FlagsHolder)!.Flags);
    }
}

/// <summary>A record with a flags member and no attribute on it, to measure how the type-level converter behaves under source generation.</summary>
/// <param name="Flags">The flags.</param>
internal sealed record FlagsHolder(ReadingFlags Flags);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EnergyReading))]
[JsonSerializable(typeof(FlagsHolder))]
internal sealed partial class EnergyReadingTestJsonContext : JsonSerializerContext;