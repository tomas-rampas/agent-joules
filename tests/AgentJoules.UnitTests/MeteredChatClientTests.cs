using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>AJ-040: the per-call reading (<see cref="StepReading"/>) and the metrics sinks that receive it (REQ-008).</summary>
public sealed class MeteredChatClientTests
{
    private static readonly DateTimeOffset Start = StepReadingFixtures.StartUtc;

    // Mirrors the options of AgentJoulesJsonContext (general defaults, camelCase, compact, nulls written, duplicates refused) plus the setting
    // that makes a missing constructor parameter without a default an error, which the context of the run document must also set.
    private static readonly JsonSerializerOptions Context = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
    };

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Reading_carries_every_required_field()
    {
        DateTimeOffset end = Start.AddMilliseconds(1500);
        StepReading reading = new(
            task: "summarise",
            repeat: 2,
            agent: "Researcher",
            modelId: "llama-3.1-8b",
            tier: Tier.CloudGpu,
            startUtc: Start,
            endUtc: end,
            outcome: StepOutcomes.Ok,
            latencyMs: Metric.Measured(1500, "ms"),
            ttftMs: Metric.Measured(210, "ms"),
            inputTokens: Metric.Measured(321, "tokens"),
            outputTokens: Metric.Measured(123, "tokens"),
            grossJoules: Metric.Measured(150, "J"),
            netJoules: Metric.Measured(50, "J"),
            avgW: Metric.Measured(100, "W"),
            peakW: Metric.Measured(120, "W"),
            cost: Metric.Estimated(0.002, "USD"),
            retries: 1,
            waitMs: 750,
            flags: ReadingFlags.LowResolution,
            method: EnergyMethod.Counter,
            sampleCount: 16,
            sampleRateHz: 10.0);

        Assert.Equal("Researcher", reading.Agent);
        Assert.Equal("llama-3.1-8b", reading.ModelId);
        Assert.Equal(Tier.CloudGpu, reading.Tier);
        Assert.Equal(Start, reading.StartUtc);
        Assert.Equal(end, reading.EndUtc);
        Assert.Equal(1500.0, reading.LatencyMs.Value);
        Assert.Equal(210.0, reading.TtftMs.Value);
        Assert.Equal(321.0, reading.InputTokens.Value);
        Assert.Equal(123.0, reading.OutputTokens.Value);
        Assert.Equal(150.0, reading.GrossJoules.Value);
        Assert.Equal(50.0, reading.NetJoules.Value);
        Assert.Equal(100.0, reading.AvgW.Value);
        Assert.Equal(120.0, reading.PeakW.Value);
        Assert.Equal(MetricLabel.Estimated, reading.Cost.Label);
        Assert.Equal(1, reading.Retries);
        Assert.Equal(750L, reading.WaitMs);
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
        Assert.Equal(ReadingFlags.LowResolution, reading.Flags);
        Assert.Equal("summarise", reading.Task);
        Assert.Equal(2, reading.Repeat);
        Assert.Equal(EnergyMethod.Counter, reading.Method);
        Assert.Equal(16, reading.SampleCount);
        Assert.Equal(10.0, reading.SampleRateHz);

        // The later stories fill quality and the derived metrics; until then they say so instead of holding a number.
        Assert.Equal(Metric.NotApplicable("not-scored"), reading.Quality);
        Assert.Equal("not-scored", reading.QualityReason);
        Assert.Equal(Metric.NotApplicable("not-scored"), reading.JoulesPerQualityPoint);
        Assert.Equal(Metric.NotApplicable("not-scored"), reading.CostPerQualityPoint);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_scored_reading_keeps_the_quality_it_was_given_and_no_invented_reason()
    {
        StepReading reading = Make(quality: Metric.ModelJudged(4));

        Assert.Equal(MetricLabel.ModelJudged, reading.Quality.Label);
        Assert.Null(reading.QualityReason);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Reading_serializes_every_required_field_under_its_schema_name()
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(StepReadingFixtures.Metered(), Context));
        string[] names = [.. document.RootElement.EnumerateObject().Select(property => property.Name)];

        string[] required =
        [
            "task", "repeat", "agent", "modelId", "tier", "startUtc", "endUtc", "outcome", "retries", "waitMs", "latencyMs", "ttftMs",
            "inputTokens", "outputTokens", "grossJoules", "netJoules", "avgW", "peakW", "method", "sampleCount", "sampleRateHz", "cost",
            "quality", "qualityReason", "joulesPerQualityPoint", "costPerQualityPoint", "flags", "outputSha256", "outputChars", "throttleReasons",
        ];
        Assert.Empty(required.Except(names));
        Assert.Empty(names.Except(required));
        Assert.Equal("local", document.RootElement.GetProperty("tier").GetString());
        Assert.Equal("sampling", document.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Reading_never_serializes_the_output_text_and_survives_a_round_trip()
    {
        StepReading reading = StepReadingFixtures.Metered();
        string hash = new('a', 64);
        StepReading withText = new(
            reading.Task, reading.Repeat, reading.Agent, reading.ModelId, reading.Tier, reading.StartUtc, reading.EndUtc, reading.Outcome,
            reading.LatencyMs, reading.TtftMs, reading.InputTokens, reading.OutputTokens, reading.GrossJoules, reading.NetJoules,
            reading.AvgW, reading.PeakW, reading.Cost, 0, 0L, ReadingFlags.None, method: reading.Method, sampleCount: reading.SampleCount,
            sampleRateHz: reading.SampleRateHz, outputSha256: hash, outputChars: 11, outputText: "secret text");

        string json = JsonSerializer.Serialize(withText, Context);

        Assert.DoesNotContain("secret text", json, StringComparison.Ordinal);
        Assert.DoesNotContain("outputText", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("secret text", withText.OutputText);
        StepReading back = JsonSerializer.Deserialize<StepReading>(json, Context)!;
        Assert.Null(back.OutputText);
        Assert.Equal(11, back.OutputChars);
        Assert.Equal(hash, back.OutputSha256);
        Assert.Equal(reading.GrossJoules, back.GrossJoules);
        Assert.Equal(reading.Flags, back.Flags);
        Assert.Equal(reading.Method, back.Method);
        Assert.Equal(reading.StartUtc, back.StartUtc);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void ToString_never_prints_the_output_text()
    {
        StepReading reading = StepReadingFixtures.Metered();
        StepReading withText = new(
            reading.Task, reading.Repeat, reading.Agent, reading.ModelId, reading.Tier, reading.StartUtc, reading.EndUtc, reading.Outcome,
            reading.LatencyMs, reading.TtftMs, reading.InputTokens, reading.OutputTokens, reading.GrossJoules, reading.NetJoules,
            reading.AvgW, reading.PeakW, reading.Cost, 0, 0L, ReadingFlags.None, method: reading.Method, sampleCount: reading.SampleCount,
            sampleRateHz: reading.SampleRateHz, outputChars: 27, outputText: "MARKER-9f3a-model-output");

        string text = withText.ToString();

        Assert.DoesNotContain("MARKER-9f3a", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OutputText", text, StringComparison.Ordinal);
        Assert.Contains("OutputChars = 27", text, StringComparison.Ordinal);
        Assert.Contains("Agent = Planner", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Reading_with_an_unknown_member_is_rejected()
    {
        // Only the first brace is the reading's own; an unknown member inside a nested metric would be rejected by the metric converter.
        string json = JsonSerializer.Serialize(StepReadingFixtures.Metered(), Context);
        string tampered = json.Insert(json.IndexOf('{', StringComparison.Ordinal) + 1, "\"surprise\":1,");

        Assert.Equal(1, CountOf(tampered, "surprise"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StepReading>(tampered, Context));
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData("retries")]
    [InlineData("waitMs")]
    [InlineData("flags")]
    [InlineData("startUtc")]
    [InlineData("endUtc")]
    [InlineData("cost")]
    public void A_document_that_omits_a_required_member_is_rejected(string member)
    {
        JsonObject document = JsonNode.Parse(JsonSerializer.Serialize(StepReadingFixtures.Metered(), Context))!.AsObject();
        Assert.True(document.Remove(member));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StepReading>(document.ToJsonString(), Context));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_timestamp_that_is_not_set_is_rejected()
    {
        Assert.Equal("startUtc", Assert.Throws<ArgumentException>(() => Make(start: default(DateTimeOffset), end: Start)).ParamName);
        Assert.Equal("endUtc", Assert.Throws<ArgumentException>(() => Make(end: default(DateTimeOffset))).ParamName);
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData("\"local, cloudgpu\"")]
    [InlineData("\" local\"")]
    [InlineData("\"Local\"")]
    [InlineData("1")]
    public void A_document_with_a_lenient_tier_spelling_is_rejected_even_when_the_options_add_the_enum_converter(string tierToken)
    {
        JsonSerializerOptions lenient = new(Context);
        lenient.Converters.Add(new JsonStringEnumConverter());
        JsonObject document = JsonNode.Parse(JsonSerializer.Serialize(StepReadingFixtures.Metered(), Context))!.AsObject();
        document["tier"] = JsonNode.Parse(tierToken);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StepReading>(document.ToJsonString(), lenient));
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData("\"counter, sampling\"")]
    [InlineData("\" counter\"")]
    [InlineData("\"Counter\"")]
    [InlineData("1")]
    public void A_document_with_a_lenient_method_spelling_is_rejected_even_when_the_options_add_the_enum_converter(string methodToken)
    {
        JsonSerializerOptions lenient = new(Context);
        lenient.Converters.Add(new JsonStringEnumConverter());
        JsonObject document = JsonNode.Parse(JsonSerializer.Serialize(StepReadingFixtures.Metered(), Context))!.AsObject();
        document["method"] = JsonNode.Parse(methodToken);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StepReading>(document.ToJsonString(), lenient));
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData(Tier.Nvidia)]
    [InlineData(Tier.Nebius)]
    public async Task Hosted_tier_reading_has_energy_NA(Tier tier)
    {
        StepReading reading = await StepReadingFixtures.HostedAsync(tier);

        Metric[] energy = [reading.GrossJoules, reading.NetJoules, reading.AvgW, reading.PeakW];
        Assert.All(energy, metric =>
        {
            Assert.Null(metric.Value);
            Assert.Equal(MetricLabel.NotApplicable, metric.Label);
            Assert.Equal("hosted-tier", metric.Reason);
            Assert.Equal(Metric.NotApplicable("hosted-tier"), metric);
        });
        Assert.Null(reading.Method);
        Assert.Null(reading.SampleCount);
        Assert.Null(reading.SampleRateHz);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Sink_returns_readings_in_publish_order()
    {
        InMemoryMetricsSink sink = new();

        foreach (string agent in new[] { "Planner", "Researcher", "Writer" })
        {
            sink.Publish(StepReadingFixtures.Metered(agent));
        }

        Assert.Equal(["Planner", "Researcher", "Writer"], sink.Snapshot().Select(reading => reading.Agent));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task Sink_is_thread_safe()
    {
        const int publisherCount = 100;
        InMemoryMetricsSink sink = new();
        using Barrier barrier = new(publisherCount);

        // Every publisher waits at the barrier, so all 100 start the publish together and really contend for the sink.
        Task[] publishers =
        [
            .. Enumerable.Range(0, publisherCount).Select(index => Task.Factory.StartNew(
                () =>
                {
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)));
                    sink.Publish(StepReadingFixtures.Metered(agent: $"Agent{index}"));
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)),
        ];
        await Task.WhenAll(publishers);

        IReadOnlyList<StepReading> snapshot = sink.Snapshot();
        Assert.Equal(publisherCount, snapshot.Count);
        Assert.Equal(publisherCount, snapshot.Select(reading => reading.Agent).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task Sink_is_thread_safe_with_publishers_and_a_reader_running_together()
    {
        InMemoryMetricsSink sink = new();
        using CancellationTokenSource stop = new();
        Task reader = Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    int count = sink.Snapshot().Count;
                    Assert.InRange(count, 0, 100);
                }
            },
            CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() => sink.Publish(StepReadingFixtures.Metered(agent: $"Agent{index}")))));
        await stop.CancelAsync();
        await reader;

        Assert.Equal(100, sink.Snapshot().Count);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Snapshot_is_a_copy_that_later_publishes_do_not_change()
    {
        InMemoryMetricsSink sink = new();
        sink.Publish(StepReadingFixtures.Metered("Planner"));

        IReadOnlyList<StepReading> first = sink.Snapshot();
        sink.Publish(StepReadingFixtures.Metered("Writer"));

        Assert.Single(first);
        Assert.Equal(2, sink.Snapshot().Count);
        Assert.Empty(new InMemoryMetricsSink().Snapshot());
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Sink_rejects_a_null_reading()
    {
        Assert.Equal("reading", Assert.Throws<ArgumentNullException>(() => new InMemoryMetricsSink().Publish(null!)).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Judge_sink_is_separate()
    {
        InMemoryMetricsSink workflowSink = new();
        InMemoryMetricsSink judgeSink = new();

        judgeSink.Publish(StepReadingFixtures.Metered("Judge"));

        Assert.NotSame(workflowSink, judgeSink);
        Assert.Empty(workflowSink.Snapshot());
        Assert.Equal("Judge", Assert.Single(judgeSink.Snapshot()).Agent);
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData("""{"value":0,"label":"N/A","reason":"x"}""")]
    [InlineData("""{"value":0.0,"label":"N/A","reason":"x"}""")]
    [InlineData("""{"value":0,"label":"not-reported","reason":"x"}""")]
    [InlineData("""{"value":1.5,"label":"not-reported","reason":"x"}""")]
    public void Missing_value_is_never_zero(string json)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<Metric>(json));

        Assert.Equal("value", failure.ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_reading_whose_value_is_missing_holds_null_and_a_measured_zero_stays_zero()
    {
        StepReading reading = Make(latency: Metric.NotReported("provider-sent-no-timing"), outputTokens: Metric.Measured(0, "tokens"));

        Assert.Null(reading.LatencyMs.Value);
        Assert.Equal(0.0, reading.OutputTokens.Value);
        Assert.Contains("\"latencyMs\":{\"value\":null,\"label\":\"not-reported\"", JsonSerializer.Serialize(reading, Context), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_buffered_call_has_a_ttft_that_is_NA_and_never_not_reported()
    {
        StepReading buffered = Make(ttft: Metric.NotApplicable("buffered-call"));

        Assert.Equal(Metric.NotApplicable("buffered-call"), buffered.TtftMs);
        Assert.Equal("ttftMs", Assert.Throws<ArgumentException>(() => Make(ttft: Metric.NotReported("buffered-call"))).ParamName);
        Assert.Equal("ttftMs", Assert.Throws<ArgumentException>(() => Make(ttft: Metric.EstimatedQuantity(5, "ms"))).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void An_end_before_the_start_is_rejected_and_a_zero_length_step_is_legal()
    {
        Assert.Equal("endUtc", Assert.Throws<ArgumentException>(() => Make(end: Start.AddTicks(-1))).ParamName);
        Assert.Equal(Start, Make(end: Start).EndUtc);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_timestamp_that_is_not_utc_is_rejected()
    {
        DateTimeOffset local = new(2026, 10, 20, 14, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal("startUtc", Assert.Throws<ArgumentException>(() => Make(start: local, end: local.AddSeconds(1))).ParamName);
        Assert.Equal("endUtc", Assert.Throws<ArgumentException>(() => Make(end: Start.ToOffset(TimeSpan.FromHours(1)).AddSeconds(1))).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_negative_retry_count_or_wait_is_rejected_and_zero_is_legal()
    {
        Assert.Equal("retries", Assert.Throws<ArgumentException>(() => Make(retries: -1)).ParamName);
        Assert.Equal("waitMs", Assert.Throws<ArgumentException>(() => Make(waitMs: -1)).ParamName);
        StepReading zero = Make(retries: 0, waitMs: 0);
        Assert.Equal(0, zero.Retries);
        Assert.Equal(0L, zero.WaitMs);
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData("OK")]
    [InlineData("failed")]
    [InlineData("")]
    [InlineData(null)]
    public void An_outcome_outside_the_vocabulary_is_rejected(string? outcome)
    {
        Assert.Equal("outcome", Assert.Throws<ArgumentException>(() => Make(outcome: outcome!)).ParamName);
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData(StepOutcomes.Ok)]
    [InlineData(StepOutcomes.Cancelled)]
    [InlineData(StepOutcomes.Error)]
    [InlineData(StepOutcomes.Incomplete)]
    public void Every_outcome_of_the_vocabulary_is_accepted(string outcome)
    {
        Assert.Equal(outcome, Make(outcome: outcome).Outcome);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Identity_fields_are_checked()
    {
        Assert.Equal("agent", Assert.Throws<ArgumentException>(() => Make(agent: " ")).ParamName);
        Assert.Equal("task", Assert.Throws<ArgumentException>(() => Make(task: "")).ParamName);
        Assert.Equal("repeat", Assert.Throws<ArgumentException>(() => Make(repeat: 0)).ParamName);
        Assert.Equal("tier", Assert.Throws<ArgumentException>(() => Make(tier: (Tier)0)).ParamName);
        Assert.Equal("tier", Assert.Throws<ArgumentException>(() => Make(tier: (Tier)9)).ParamName);
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData(Tier.Nvidia)]
    [InlineData(Tier.Nebius)]
    public void A_hosted_tier_reading_cannot_carry_measured_energy(Tier tier)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => Make(tier: tier, gross: Metric.Measured(5, "J")));

        Assert.Equal("grossJoules", failure.ParamName);
        Assert.Equal("method", Assert.Throws<ArgumentException>(() => Make(tier: tier, method: EnergyMethod.Sampling)).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_metered_tier_reading_may_report_energy_as_not_reported()
    {
        StepReading reading = Make(tier: Tier.Local, gross: Metric.NotReported("meter-failed"));

        Assert.Equal(MetricLabel.NotReported, reading.GrossJoules.Label);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void The_unmetered_method_is_never_stored_in_a_reading()
    {
        Assert.Equal("method", Assert.Throws<ArgumentException>(() => Make(method: EnergyMethod.Unmetered)).ParamName);
        Assert.Equal("method", Assert.Throws<ArgumentException>(() => Make(method: (EnergyMethod)0)).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_null_metric_is_rejected()
    {
        StepReading ok = Make();

        ArgumentNullException failure = Assert.Throws<ArgumentNullException>(() => new StepReading(
            ok.Task, ok.Repeat, ok.Agent, ok.ModelId, ok.Tier, ok.StartUtc, ok.EndUtc, ok.Outcome,
            null!, ok.TtftMs, ok.InputTokens, ok.OutputTokens, ok.GrossJoules, ok.NetJoules, ok.AvgW, ok.PeakW, ok.Cost, 0, 0L, ReadingFlags.None));

        Assert.Equal("latencyMs", failure.ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Output_fingerprint_and_flags_are_checked()
    {
        Assert.Equal("outputSha256", Assert.Throws<ArgumentException>(() => Make(sha: "ABC")).ParamName);
        Assert.Equal("outputSha256", Assert.Throws<ArgumentException>(() => Make(sha: new string('A', 64))).ParamName);
        Assert.Equal("outputChars", Assert.Throws<ArgumentException>(() => Make(chars: -1)).ParamName);
        Assert.Equal("flags", Assert.Throws<ArgumentException>(() => Make(flags: (ReadingFlags)(1 << 30))).ParamName);
        Assert.Equal(new string('0', 64), Make(sha: new string('0', 64), chars: 0).OutputSha256);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Throttle_reasons_are_copied_and_must_not_be_blank()
    {
        List<string> source = ["SwPowerCap"];
        StepReading reading = Make(throttle: source, flags: ReadingFlags.Throttled);
        source.Add("HwSlowdown");

        Assert.Equal(["SwPowerCap"], reading.ThrottleReasons);
        Assert.Equal("throttleReasons", Assert.Throws<ArgumentException>(() => Make(throttle: [" "])).ParamName);
    }

    // The eleven malformed readings the review probe got accepted: each now fails with the parameter it violates.

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_measured_negative_net_without_the_below_baseline_flag_is_rejected()
    {
        Assert.Equal("flags", Assert.Throws<ArgumentException>(() => Make(net: Metric.Measured(-5, "J"))).ParamName);
        Assert.Equal(ReadingFlags.BelowBaseline, Make(net: Metric.Measured(-5, "J"), flags: ReadingFlags.BelowBaseline).Flags);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void The_below_baseline_flag_with_a_positive_net_is_rejected()
    {
        Assert.Equal("flags", Assert.Throws<ArgumentException>(() => Make(net: Metric.Measured(5, "J"), flags: ReadingFlags.BelowBaseline)).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void The_throttled_flag_without_throttle_reasons_is_rejected()
    {
        Assert.Equal("flags", Assert.Throws<ArgumentException>(() => Make(flags: ReadingFlags.Throttled)).ParamName);
        Assert.Equal("flags", Assert.Throws<ArgumentException>(() => Make(throttle: ["SwPowerCap"])).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_throttle_name_outside_the_five_names_is_rejected()
    {
        Assert.Equal("throttleReasons", Assert.Throws<ArgumentException>(() => Make(throttle: ["Bogus"], flags: ReadingFlags.Throttled)).ParamName);
        Assert.Equal("throttleReasons", Assert.Throws<ArgumentException>(() => Make(throttle: ["HwSlowdown", "SwPowerCap"], flags: ReadingFlags.Throttled)).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void The_sampling_fallback_flag_with_the_counter_method_is_rejected()
    {
        Assert.Equal("flags", Assert.Throws<ArgumentException>(() => Make(flags: ReadingFlags.SamplingFallback, method: EnergyMethod.Counter)).ParamName);
        Assert.Equal(ReadingFlags.SamplingFallback, Make(flags: ReadingFlags.SamplingFallback, method: EnergyMethod.Sampling).Flags);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Gross_energy_with_the_unit_W_is_rejected()
    {
        Assert.Equal("grossJoules", Assert.Throws<ArgumentException>(() => Make(gross: Metric.Measured(30, "W"))).ParamName);
        Assert.Equal("avgW", Assert.Throws<ArgumentException>(() => Make(avg: Metric.Measured(30, "J"))).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Gross_energy_labelled_estimated_is_rejected()
    {
        Assert.Equal("grossJoules", Assert.Throws<ArgumentException>(() => Make(gross: Metric.EstimatedQuantity(30, "J"))).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Measured_gross_energy_without_a_method_is_rejected()
    {
        Assert.Equal("method", Assert.Throws<ArgumentException>(() => Make(withoutMethod: true)).ParamName);
        Assert.Equal("method", Assert.Throws<ArgumentException>(() => Make(tier: Tier.CloudGpu, withoutMethod: true)).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_local_tier_reading_with_the_hosted_tier_reason_is_rejected()
    {
        Metric hosted = Metric.NotApplicable("hosted-tier");

        ArgumentException failure = Assert.Throws<ArgumentException>(() => Make(tier: Tier.Local, gross: hosted, net: hosted, avg: hosted, peak: hosted));

        Assert.Equal("grossJoules", failure.ParamName);
        Assert.Equal("peakW", Assert.Throws<ArgumentException>(() => Make(tier: Tier.CloudGpu, peak: hosted)).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_hosted_tier_reading_with_another_NA_reason_is_rejected()
    {
        Metric failed = Metric.NotApplicable("meter-failed");

        Assert.Equal("grossJoules", Assert.Throws<ArgumentException>(() => Make(tier: Tier.Nvidia, gross: failed)).ParamName);
        Assert.Equal("netJoules", Assert.Throws<ArgumentException>(() => Make(tier: Tier.Nebius, net: failed)).ParamName);
        Assert.Equal("avgW", Assert.Throws<ArgumentException>(() => Make(tier: Tier.Nvidia, avg: Metric.NotReported("hosted-tier"))).ParamName);
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData(ReadingFlags.BelowBaseline | ReadingFlags.Throttled | ReadingFlags.SamplingFallback)]
    [InlineData(ReadingFlags.LowResolution)]
    [InlineData(ReadingFlags.SamplingFallback)]
    [InlineData(ReadingFlags.BelowBaseline)]
    [InlineData(ReadingFlags.BaselineUnstable)]
    public void A_hosted_tier_reading_with_an_energy_flag_is_rejected(ReadingFlags flags)
    {
        Assert.Equal("flags", Assert.Throws<ArgumentException>(() => Make(tier: Tier.Nvidia, flags: flags)).ParamName);
    }

    // Defaults describe a valid local-tier reading with measured energy; a hosted tier gets N/A energy with the hosted-tier reason and no method.
    private static StepReading Make(
        Tier tier = Tier.Local,
        string agent = "Planner",
        string task = "summarise",
        int repeat = 1,
        string outcome = StepOutcomes.Ok,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        int retries = 0,
        long waitMs = 0,
        Metric? latency = null,
        Metric? ttft = null,
        Metric? outputTokens = null,
        Metric? gross = null,
        Metric? net = null,
        Metric? avg = null,
        Metric? peak = null,
        Metric? quality = null,
        EnergyMethod? method = null,
        bool withoutMethod = false,
        ReadingFlags flags = ReadingFlags.None,
        string? sha = null,
        int? chars = null,
        IReadOnlyList<string>? throttle = null)
    {
        bool hosted = tier is Tier.Nvidia or Tier.Nebius;
        DateTimeOffset begin = start ?? Start;
        EnergyMethod? effectiveMethod = hosted || withoutMethod ? method : method ?? EnergyMethod.Sampling;
        return new StepReading(
            task: task,
            repeat: repeat,
            agent: agent,
            modelId: "model-a",
            tier: tier,
            startUtc: begin,
            endUtc: end ?? begin.AddSeconds(1),
            outcome: outcome,
            latencyMs: latency ?? Metric.Measured(1000, "ms"),
            ttftMs: ttft ?? Metric.NotApplicable("buffered-call"),
            inputTokens: Metric.Measured(10, "tokens"),
            outputTokens: outputTokens ?? Metric.Measured(20, "tokens"),
            grossJoules: gross ?? (hosted ? Metric.NotApplicable("hosted-tier") : Metric.Measured(30, "J")),
            netJoules: net ?? (hosted ? Metric.NotApplicable("hosted-tier") : Metric.Measured(10, "J")),
            avgW: avg ?? (hosted ? Metric.NotApplicable("hosted-tier") : Metric.Measured(30, "W")),
            peakW: peak ?? (hosted ? Metric.NotApplicable("hosted-tier") : Metric.Measured(40, "W")),
            cost: Metric.NotReported("not-priced"),
            retries: retries,
            waitMs: waitMs,
            flags: flags,
            method: effectiveMethod,
            throttleReasons: throttle,
            quality: quality,
            outputSha256: sha,
            outputChars: chars);
    }

    private static int CountOf(string text, string fragment)
    {
        int count = 0;
        for (int at = text.IndexOf(fragment, StringComparison.Ordinal); at >= 0; at = text.IndexOf(fragment, at + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}