using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Energy.Testing;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>AJ-040: the per-call reading (<see cref="StepReading"/>) and the metrics sinks that receive it (REQ-008); AJ-041: the metered chat client that publishes exactly one reading per call (REQ-007).</summary>
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

    // ---- AJ-041: the metered chat client, one reading per call (REQ-007) ----

    private static readonly IReadOnlyList<ChatMessage> Prompt = [new ChatMessage(ChatRole.User, "Summarise the report.")];

    // A test that waits on a held call or on the gate must fail, not hang the run, when the code under test leaks; this is the system clock, not the fake one.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task Buffered_call_yields_one_reading()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        DateTimeOffset before = rig.Time.GetUtcNow();
        FakeChatClient inner = new(rig.Time) { ResponseText = "Hello world", ResponseDelay = TimeSpan.FromMilliseconds(1500) };
        MeteredChatClient client = rig.ClientOver(inner, "Planner");

        ChatResponse response = await client.GetResponseAsync(Prompt);

        Assert.Equal("Hello world", response.Text);
        _ = Assert.Single(inner.Calls);
        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(MetricLabel.NotApplicable, reading.TtftMs.Label);
        Assert.Equal("buffered-call", reading.TtftMs.Reason);
        Assert.Null(reading.TtftMs.Value);
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
        Assert.Equal("Planner", reading.Agent);
        Assert.Equal(Metric.Measured(1500, "ms"), reading.LatencyMs);
        Assert.Equal(before, reading.StartUtc);
        Assert.Equal(before.AddMilliseconds(1500), reading.EndUtc);

        // No usage came back (tokens not-reported, cost N/A through the calculator M2 ships), and the retry numbers are the ones AJ-045 fills in.
        Assert.Equal(Metric.NotReported("provider-omitted-usage"), reading.InputTokens);
        Assert.Equal(Metric.NotReported("provider-omitted-usage"), reading.OutputTokens);
        Assert.Equal(Metric.NotApplicable("cost-not-configured"), reading.Cost);
        Assert.Equal(0, reading.Retries);
        Assert.Equal(0L, reading.WaitMs);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_local_window_is_mapped_into_the_reading_with_its_method_samples_and_rate()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseDelay = TimeSpan.FromMilliseconds(1500) }, "Planner");

        _ = await client.GetResponseAsync(Prompt).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(MetricLabel.Measured, reading.GrossJoules.Label);
        Assert.Equal("J", reading.GrossJoules.Unit);
        Assert.Equal(150.0, reading.GrossJoules.Value ?? double.NaN, 6);
        Assert.Equal(100.0, reading.AvgW.Value ?? double.NaN, 6);
        Assert.Equal(100.0, reading.PeakW.Value ?? double.NaN, 6);
        Assert.Equal(rig.Meter.Capabilities.Method, reading.Method);
        Assert.Equal(10.0, reading.SampleRateHz);
        Assert.True(reading.SampleCount > 0);
        Assert.Equal(ReadingFlags.None, reading.Flags);
    }

    [Theory]
    [Trait("Spec", "REQ-007")]
    [InlineData(Tier.Nvidia)]
    [InlineData(Tier.Nebius)]
    public async Task An_unmetered_window_of_a_hosted_tier_is_mapped_without_method_samples_or_rate(Tier tier)
    {
        await using MeterRig rig = await MeterRig.CreateAsync(tier);
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time), "Planner", tier);

        _ = await client.GetResponseAsync(Prompt).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(tier, reading.Tier);
        Assert.All(
            [reading.GrossJoules, reading.NetJoules, reading.AvgW, reading.PeakW],
            metric => Assert.Equal(Metric.NotApplicable("hosted-tier"), metric));
        Assert.Null(reading.Method);
        Assert.Null(reading.SampleCount);
        Assert.Null(reading.SampleRateHz);
        Assert.Null(reading.ThrottleReasons);
        Assert.Equal(ReadingFlags.None, reading.Flags);
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task Streaming_ttft_matches_first_update_delay()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time)
        {
            Updates = [new FakeUpdate("The ", TimeSpan.FromMilliseconds(300)), new FakeUpdate("report", TimeSpan.FromMilliseconds(100))],
        };
        MeteredChatClient client = rig.ClientOver(inner, "Writer");

        (int count, string text) = await ConsumeAsync(client);

        Assert.Equal(2, count);
        Assert.Equal("The report", text);
        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
        Assert.Equal("Writer", reading.Agent);
        Assert.Equal(MetricLabel.Measured, reading.TtftMs.Label);
        Assert.Equal("ms", reading.TtftMs.Unit);
        Assert.InRange(reading.TtftMs.Value ?? double.NaN, 280.0, 320.0);
        Assert.Equal(300.0, reading.TtftMs.Value ?? double.NaN, 6);
        Assert.Equal(Metric.Measured(400, "ms"), reading.LatencyMs);
        Assert.Equal(MetricLabel.Measured, reading.GrossJoules.Label);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task Streaming_ttft_counts_reasoning_text_and_skips_updates_without_text()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        UsageDetails usage = new() { InputTokenCount = 10, OutputTokenCount = 2 };
        FakeChatClient inner = new(rig.Time)
        {
            Updates =
            [
                new FakeUpdate(string.Empty, TimeSpan.FromMilliseconds(50), usage),
                new FakeUpdate("thinking", TimeSpan.FromMilliseconds(150), Reasoning: true),
                new FakeUpdate("answer", TimeSpan.FromMilliseconds(100)),
            ],
        };

        _ = await ConsumeAsync(rig.ClientOver(inner, "Planner"));

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(200.0, reading.TtftMs.Value ?? double.NaN, 6);

        // Only the answer text is the output; the reasoning text is not part of it.
        Assert.Equal("answer", reading.OutputText);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_stream_that_ends_without_text_has_a_ttft_that_is_NA_and_never_zero()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time) { Updates = [new FakeUpdate(string.Empty, TimeSpan.FromMilliseconds(200))] };

        _ = await ConsumeAsync(rig.ClientOver(inner, "Planner"));

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
        Assert.Equal(MetricLabel.NotApplicable, reading.TtftMs.Label);
        Assert.Equal("no-content-update", reading.TtftMs.Reason);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task Builds_via_ChatClientBuilder_Use()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time);
        StepIdentity identity = MeterRig.IdentityOf("Planner", Tier.Local);
        MeteringServices services = rig.Services;

        IChatClient built = new ChatClientBuilder(inner).Use(next => new MeteredChatClient(next, identity, services)).Build();
        _ = await built.GetResponseAsync(Prompt).WaitAsync(HangGuard);

        Assert.IsType<MeteredChatClient>(built);
        Assert.Equal("Planner", Assert.Single(rig.Sink.Snapshot()).Agent);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task UseMetering_adds_the_decorator_as_the_outermost_client_of_the_pipeline()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time);

        ChatClientBuilder builder = new(inner);
        Assert.Same(builder, builder.UseMetering(MeterRig.IdentityOf("Researcher", Tier.Local), rig.Services));
        IChatClient built = builder.Build();
        _ = await built.GetResponseAsync(Prompt).WaitAsync(HangGuard);

        MeteredChatClient metered = Assert.IsType<MeteredChatClient>(built);
        Assert.Same(inner, metered.GetService(typeof(FakeChatClient)));
        Assert.Equal("Researcher", Assert.Single(rig.Sink.Snapshot()).Agent);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task The_constructor_and_the_extension_reject_null_arguments()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time);
        StepIdentity identity = MeterRig.IdentityOf("Planner", Tier.Local);

        _ = Assert.Throws<ArgumentNullException>(() => new MeteredChatClient(inner, identity, null!));
        _ = Assert.Throws<ArgumentNullException>(() => new MeteredChatClient(inner, null!, rig.Services));
        _ = Assert.Throws<ArgumentNullException>(() => new MeteredChatClient(null!, identity, rig.Services));
        _ = Assert.Throws<ArgumentNullException>(() => new ChatClientBuilder(inner).UseMetering(identity, null!));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(() => rig.ClientOver(inner, "Planner").GetResponseAsync(null!));
        Assert.Empty(rig.Sink.Snapshot());
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task The_output_fingerprint_is_the_lowercase_sha256_of_the_utf8_text_on_both_paths()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        string text = "caf" + (char)0xE9 + " says hello";
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        FakeChatClient buffered = new(rig.Time) { ResponseText = text };
        FakeChatClient streaming = new(rig.Time)
        {
            Updates = [new FakeUpdate("caf" + (char)0xE9, TimeSpan.FromMilliseconds(10)), new FakeUpdate(" says hello", TimeSpan.FromMilliseconds(10))],
        };

        _ = await rig.ClientOver(buffered, "Planner").GetResponseAsync(Prompt).WaitAsync(HangGuard);
        _ = await ConsumeAsync(rig.ClientOver(streaming, "Writer"));

        IReadOnlyList<StepReading> readings = rig.Sink.Snapshot();
        Assert.Equal(2, readings.Count);
        Assert.All(readings, reading =>
        {
            Assert.Equal(expected, reading.OutputSha256);
            Assert.Equal(text.Length, reading.OutputChars);
            Assert.Equal(text, reading.OutputText);
        });
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_cancelled_stream_yields_one_cancelled_reading_and_rethrows()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time) { Updates = [.. Enumerable.Range(0, 5).Select(static i => new FakeUpdate($"part {i} ", TimeSpan.FromMilliseconds(50)))] };
        MeteredChatClient client = rig.ClientOver(inner, "Planner");
        using CancellationTokenSource cancellation = new();
        int seen = 0;

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Prompt, cancellationToken: cancellation.Token))
            {
                Assert.NotNull(update);
                seen++;
                if (seen == 2)
                {
                    await cancellation.CancelAsync();
                }
            }
        });

        Assert.Equal(2, seen);
        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Cancelled, reading.Outcome);
        Assert.Equal("part 0 part 1 ", reading.OutputText);
        Assert.Equal(MetricLabel.Measured, reading.TtftMs.Label);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_stream_that_throws_after_two_updates_yields_one_error_reading_and_rethrows_the_same_exception()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        InvalidDataException failure = new("The stream broke.");
        FakeChatClient inner = new(rig.Time)
        {
            Updates = [.. Enumerable.Range(0, 5).Select(static i => new FakeUpdate($"part {i} ", TimeSpan.FromMilliseconds(50)))],
            FailAfterUpdates = 2,
            Failure = failure,
        };
        MeteredChatClient client = rig.ClientOver(inner, "Planner");
        int seen = 0;

        InvalidDataException thrown = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Prompt))
            {
                Assert.NotNull(update);
                seen++;
            }
        });

        Assert.Same(failure, thrown);
        Assert.Equal(2, seen);
        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal("part 0 part 1 ", reading.OutputText);
        // Two updates of 50 ms, then the failure delay of the fake (100 ms by default).
        Assert.Equal(Metric.Measured(200, "ms"), reading.LatencyMs);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task Abandoning_the_enumerator_early_yields_one_cancelled_reading_when_it_is_disposed()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time) { Updates = [.. Enumerable.Range(0, 5).Select(static i => new FakeUpdate($"part {i} ", TimeSpan.FromMilliseconds(50)))] };
        MeteredChatClient client = rig.ClientOver(inner, "Planner");

        IAsyncEnumerator<ChatResponseUpdate> enumerator = client.GetStreamingResponseAsync(Prompt).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Empty(rig.Sink.Snapshot());
        await enumerator.DisposeAsync();

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Cancelled, reading.Outcome);
        Assert.Equal("part 0 ", reading.OutputText);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task Leaving_the_loop_early_yields_one_cancelled_reading()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time) { Updates = [.. Enumerable.Range(0, 5).Select(static i => new FakeUpdate($"part {i} ", TimeSpan.FromMilliseconds(50)))] };
        MeteredChatClient client = rig.ClientOver(inner, "Planner");

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Prompt))
        {
            Assert.NotNull(update);
            break;
        }

        Assert.Equal(StepOutcomes.Cancelled, Assert.Single(rig.Sink.Snapshot()).Outcome);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_buffered_call_that_fails_yields_one_error_reading_and_rethrows()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        InvalidDataException failure = new("The provider broke.");
        FakeChatClient inner = new(rig.Time) { Failure = failure, ResponseDelay = TimeSpan.FromMilliseconds(500) };

        InvalidDataException thrown = await Assert.ThrowsAsync<InvalidDataException>(() => rig.ClientOver(inner, "Planner").GetResponseAsync(Prompt).WaitAsync(HangGuard));

        Assert.Same(failure, thrown);
        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal(MetricLabel.NotApplicable, reading.TtftMs.Label);
        Assert.Null(reading.OutputSha256);
        Assert.Null(reading.OutputChars);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_buffered_call_cancelled_by_the_caller_yields_one_cancelled_reading()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChatClient inner = new(rig.Time) { Hold = never.Task };
        using CancellationTokenSource cancellation = new();

        Task<ChatResponse> call = rig.ClientOver(inner, "Planner").GetResponseAsync(Prompt, cancellationToken: cancellation.Token);
        Assert.False(call.IsCompleted);
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(HangGuard));
        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Cancelled, reading.Outcome);
        Assert.Equal(Metric.Measured(250, "ms"), reading.LatencyMs);
    }

    [Theory]
    [Trait("Spec", "REQ-007")]
    [InlineData(Tier.Local, 0, StepOutcomes.Error)]
    [InlineData(Tier.Local, 1, StepOutcomes.Incomplete)]
    [InlineData(Tier.CloudGpu, 0, StepOutcomes.Incomplete)]
    [InlineData(Tier.Nvidia, 0, StepOutcomes.Error)]
    [InlineData(Tier.Nebius, 1, StepOutcomes.Incomplete)]
    public async Task A_dropped_connection_in_a_stream_is_incomplete_on_cloudgpu_or_after_the_first_update_and_an_error_otherwise(Tier tier, int updatesBeforeDrop, string expected)
    {
        await using MeterRig rig = await MeterRig.CreateAsync(tier);
        FakeChatClient inner = new(rig.Time)
        {
            Updates = [new FakeUpdate("part 0 ", TimeSpan.FromMilliseconds(50)), new FakeUpdate("part 1 ", TimeSpan.FromMilliseconds(50))],
            FailAfterUpdates = updatesBeforeDrop,
            Failure = new IOException("The connection was closed."),
        };

        _ = await Assert.ThrowsAsync<IOException>(() => ConsumeAsync(rig.ClientOver(inner, "Planner", tier)));

        Assert.Equal(expected, Assert.Single(rig.Sink.Snapshot()).Outcome);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public void The_classifier_maps_no_error_and_a_caller_cancellation()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Assert.Equal(StepOutcomes.Ok, OutcomeClassifier.Classify(null, Tier.Local, firstUpdateSeen: true, CancellationToken.None));
        Assert.Equal(StepOutcomes.Cancelled, OutcomeClassifier.Classify(new OperationCanceledException(cancelled.Token), Tier.Local, false, cancelled.Token));
        Assert.Equal(StepOutcomes.Cancelled, OutcomeClassifier.Classify(new TaskCanceledException(), Tier.CloudGpu, true, cancelled.Token));

        // A cancellation the caller did not ask for (a timeout of the HTTP client, say) is an error.
        Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(new TaskCanceledException(), Tier.Local, true, CancellationToken.None));
        Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(new OperationCanceledException(), Tier.Nvidia, false, CancellationToken.None));
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public void The_classifier_treats_a_transport_failure_without_a_status_as_incomplete_only_on_cloudgpu_or_after_the_first_update()
    {
        Exception[] transport =
        [
            new IOException("closed"),
            new SocketException((int)SocketError.ConnectionReset),
            new HttpRequestException("The connection ended."),
            new HttpRequestException("wrapped", new IOException("closed")),
            new InvalidOperationException("outer", new IOException("closed")),
            new InvalidOperationException("outer", new HttpRequestException("inner", new SocketException((int)SocketError.ConnectionReset))),
        ];

        foreach (Exception error in transport)
        {
            Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(error, Tier.Local, false, CancellationToken.None));
            Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(error, Tier.Nvidia, false, CancellationToken.None));
            Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(error, Tier.Nebius, false, CancellationToken.None));
            Assert.Equal(StepOutcomes.Incomplete, OutcomeClassifier.Classify(error, Tier.CloudGpu, false, CancellationToken.None));
            Assert.Equal(StepOutcomes.Incomplete, OutcomeClassifier.Classify(error, Tier.Local, true, CancellationToken.None));
            Assert.Equal(StepOutcomes.Incomplete, OutcomeClassifier.Classify(error, Tier.Nebius, true, CancellationToken.None));
        }
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public void The_classifier_treats_an_http_status_error_and_every_other_failure_as_an_error_on_every_tier()
    {
        Exception[] errors =
        [
            new HttpRequestException("Too many requests.", inner: null, HttpStatusCode.TooManyRequests),
            new HttpRequestException("outer", new HttpRequestException("inner", inner: null, HttpStatusCode.BadGateway)),
            new IOException("closed", new HttpRequestException("inner", inner: null, HttpStatusCode.ServiceUnavailable)),
            new InvalidOperationException("broken"),
            new TimeoutException("slow"),
            new InvalidDataException("bad"),
        ];

        foreach (Exception error in errors)
        {
            foreach (Tier tier in Enum.GetValues<Tier>())
            {
                Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(error, tier, true, CancellationToken.None));
                Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(error, tier, false, CancellationToken.None));
            }
        }
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task The_agent_name_comes_from_the_constructor_and_overlapping_clients_never_mix_their_readings()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        TaskCompletionSource releaseBoth = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MeteredChatClient planner = rig.ClientOver(new FakeChatClient(rig.Time) { Hold = releaseBoth.Task }, "Planner");
        MeteredChatClient writer = rig.ClientOver(new FakeChatClient(rig.Time) { Hold = releaseBoth.Task }, "Writer");

        Task<ChatResponse> first = planner.GetResponseAsync(Prompt);
        Task<ChatResponse> second = writer.GetResponseAsync(Prompt);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        releaseBoth.SetResult();
        _ = await Task.WhenAll(first, second).WaitAsync(HangGuard);

        IReadOnlyList<StepReading> readings = rig.Sink.Snapshot();
        Assert.Equal(2, readings.Count);
        Assert.Equal(["Planner", "Writer"], readings.Select(static reading => reading.Agent).Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_second_buffered_call_while_one_is_in_flight_throws_and_leaves_the_first_alone()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChatClient inner = new(rig.Time) { Hold = release.Task };
        MeteredChatClient client = rig.ClientOver(inner, "Planner");

        Task<ChatResponse> first = client.GetResponseAsync(Prompt);
        Assert.False(first.IsCompleted);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Prompt).WaitAsync(HangGuard));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ConsumeAsync(client));
        Assert.Empty(rig.Sink.Snapshot());
        Assert.False(first.IsCompleted);

        release.SetResult();
        _ = await first.WaitAsync(HangGuard);
        _ = Assert.Single(rig.Sink.Snapshot());
        _ = Assert.Single(inner.Calls);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_stream_call_in_flight_rejects_a_second_call_until_the_stream_is_disposed()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time) { Updates = [new FakeUpdate("part 0 ", TimeSpan.FromMilliseconds(50)), new FakeUpdate("part 1 ", TimeSpan.FromMilliseconds(50))] };
        MeteredChatClient client = rig.ClientOver(inner, "Planner");

        IAsyncEnumerator<ChatResponseUpdate> first = client.GetStreamingResponseAsync(Prompt).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Prompt).WaitAsync(HangGuard));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ConsumeAsync(client));

        await first.DisposeAsync();
        _ = Assert.Single(rig.Sink.Snapshot());

        // The slot is free again: a later call is metered normally.
        _ = await ConsumeAsync(client);
        Assert.Equal(2, rig.Sink.Snapshot().Count);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task The_one_in_flight_slot_is_free_again_after_a_call_that_failed()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient failing = new(rig.Time) { Failure = new InvalidDataException("The provider broke.") };
        MeteredChatClient client = rig.ClientOver(failing, "Planner");

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetResponseAsync(Prompt).WaitAsync(HangGuard));
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetResponseAsync(Prompt).WaitAsync(HangGuard));

        Assert.Equal(2, rig.Sink.Snapshot().Count);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task Cancellation_while_waiting_for_the_gate_yields_no_reading()
    {
        using SerializedMeterGate gate = new();
        await using MeterRig rig = await MeterRig.CreateAsync(gate: gate);
        FakeChatClient inner = new(rig.Time);
        MeteredChatClient client = rig.ClientOver(inner, "Planner");
        IAsyncDisposable held = await gate.AcquireAsync(CancellationToken.None);
        using CancellationTokenSource cancellation = new();

        Task<ChatResponse> waiting = client.GetResponseAsync(Prompt, cancellationToken: cancellation.Token);
        Assert.False(waiting.IsCompleted);
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(HangGuard));
        Assert.Empty(rig.Sink.Snapshot());
        Assert.Empty(inner.Calls);

        // The waiter took nothing from the gate and left the client's slot free.
        await held.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
        _ = await client.GetResponseAsync(Prompt).WaitAsync(HangGuard);
        _ = Assert.Single(rig.Sink.Snapshot());
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task The_time_spent_waiting_for_the_gate_is_not_part_of_the_latency()
    {
        using SerializedMeterGate gate = new();
        await using MeterRig rig = await MeterRig.CreateAsync(gate: gate);
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseDelay = TimeSpan.FromMilliseconds(1000) }, "Planner");
        IAsyncDisposable held = await gate.AcquireAsync(CancellationToken.None);

        Task<ChatResponse> waiting = client.GetResponseAsync(Prompt);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(waiting.IsCompleted);
        await held.DisposeAsync();
        _ = await waiting.WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(Metric.Measured(1000, "ms"), reading.LatencyMs);
        Assert.Equal(100.0, reading.GrossJoules.Value ?? double.NaN, 6);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task The_gate_is_released_after_a_failed_buffered_call_a_failed_stream_and_an_abandoned_stream()
    {
        using SerializedMeterGate gate = new();
        await using MeterRig rig = await MeterRig.CreateAsync(gate: gate);
        FakeChatClient failing = new(rig.Time) { Failure = new InvalidDataException("broke") };
        FakeChatClient failingStream = new(rig.Time)
        {
            Updates = [new FakeUpdate("part 0 ", TimeSpan.FromMilliseconds(50))],
            FailAfterUpdates = 0,
            Failure = new InvalidDataException("broke"),
        };
        FakeChatClient endless = new(rig.Time) { Updates = [new FakeUpdate("part 0 ", TimeSpan.FromMilliseconds(50)), new FakeUpdate("part 1 ", TimeSpan.FromMilliseconds(50))] };

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => rig.ClientOver(failing, "A").GetResponseAsync(Prompt).WaitAsync(HangGuard));
        Assert.Equal(1, gate.CurrentCount);
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => ConsumeAsync(rig.ClientOver(failingStream, "B")));
        Assert.Equal(1, gate.CurrentCount);
        IAsyncEnumerator<ChatResponseUpdate> abandoned = rig.ClientOver(endless, "C").GetStreamingResponseAsync(Prompt).GetAsyncEnumerator();
        Assert.True(await abandoned.MoveNextAsync());
        Assert.Equal(0, gate.CurrentCount);
        await abandoned.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
        Assert.Equal(3, rig.Sink.Snapshot().Count);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task The_gate_is_released_when_opening_the_window_fails_and_no_reading_is_published()
    {
        using SerializedMeterGate gate = new();
        await using MeterRig rig = await MeterRig.CreateAsync(gate: gate, startMeter: false);
        FakeChatClient inner = new(rig.Time);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.ClientOver(inner, "Planner").GetResponseAsync(Prompt).WaitAsync(HangGuard));
        Assert.Equal(1, gate.CurrentCount);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => ConsumeAsync(rig.ClientOver(inner, "Planner")));

        Assert.Equal(1, gate.CurrentCount);
        Assert.Empty(rig.Sink.Snapshot());
        Assert.Empty(inner.Calls);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_caller_cancellation_on_a_clock_that_did_not_move_surfaces_the_cancellation_and_publishes_no_reading()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChatClient inner = new(rig.Time) { Hold = never.Task };
        using CancellationTokenSource cancellation = new();

        Task<ChatResponse> call = rig.ClientOver(inner, "Planner").GetResponseAsync(Prompt, cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();

        // The window has no length (the clock did not move), so closing it fails; that must not replace the cancellation.
        OperationCanceledException thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(HangGuard));
        Assert.Empty(rig.Sink.Snapshot());
        _ = Assert.IsType<InvalidOperationException>(thrown.Data[MeteredChatClient.MeterCloseFailureKey]);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_meter_failure_after_a_failed_call_keeps_the_calls_exception_attaches_the_meter_failure_and_frees_the_gate()
    {
        using SerializedMeterGate gate = new();
        await using MeterRig rig = await MeterRig.CreateAsync(gate: gate);
        InvalidDataException failure = new("The provider broke.");
        FakeChatClient inner = new(rig.Time) { Failure = failure, ResponseDelay = TimeSpan.Zero };

        InvalidDataException thrown = await Assert.ThrowsAsync<InvalidDataException>(() => rig.ClientOver(inner, "Planner").GetResponseAsync(Prompt).WaitAsync(HangGuard));

        Assert.Same(failure, thrown);
        _ = Assert.IsType<InvalidOperationException>(thrown.Data[MeteredChatClient.MeterCloseFailureKey]);
        Assert.Empty(rig.Sink.Snapshot());
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_meter_failure_after_a_successful_call_surfaces_and_frees_the_gate_and_the_slot()
    {
        using SerializedMeterGate gate = new();
        await using MeterRig rig = await MeterRig.CreateAsync(gate: gate);
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseDelay = TimeSpan.Zero }, "Planner");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Prompt).WaitAsync(HangGuard));
        Assert.Equal(1, gate.CurrentCount);

        // The slot is free as well: the second call fails the same way and not with the in-flight message.
        InvalidOperationException second = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Prompt).WaitAsync(HangGuard));
        Assert.DoesNotContain("in flight", second.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Sink.Snapshot());
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_failure_while_observing_an_update_is_an_error_and_never_a_cancellation()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        ScriptedStreamClient inner = new(() => NullUpdateAsync(rig.Time));

        _ = await Assert.ThrowsAsync<NullReferenceException>(() => ConsumeAsync(rig.ClientOver(inner, "Planner")));

        Assert.Equal(StepOutcomes.Error, Assert.Single(rig.Sink.Snapshot()).Outcome);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_clean_stream_whose_inner_enumerator_fails_to_dispose_is_an_error_and_the_exception_reaches_the_caller()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        ScriptedStreamClient inner = new(() => new DisposeFailsStream(rig.Time));

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => ConsumeAsync(rig.ClientOver(inner, "Planner")));

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal("text", reading.OutputText);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task When_the_stream_fails_and_the_inner_dispose_fails_too_the_caller_receives_the_first_exception_with_the_second_attached()
    {
        using SerializedMeterGate gate = new();
        await using MeterRig rig = await MeterRig.CreateAsync(gate: gate);
        InvalidOperationException first = new("The stream broke.");
        ScriptedStreamClient inner = new(() => new DisposeFailsStream(rig.Time, first));

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ConsumeAsync(rig.ClientOver(inner, "Planner")));

        Assert.Same(first, thrown);
        InvalidDataException second = Assert.IsType<InvalidDataException>(thrown.Data[MeteredChatClient.InnerDisposeFailureKey]);
        Assert.Equal("The enumerator failed to dispose.", second.Message);
        Assert.Equal(StepOutcomes.Error, Assert.Single(rig.Sink.Snapshot()).Outcome);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_transport_drop_after_a_role_only_update_is_incomplete_on_a_hosted_tier()
    {
        await using MeterRig rig = await MeterRig.CreateAsync(Tier.Nvidia);
        FakeChatClient inner = new(rig.Time)
        {
            Updates = [new FakeUpdate(string.Empty, TimeSpan.FromMilliseconds(50)), new FakeUpdate("text", TimeSpan.FromMilliseconds(50))],
            FailAfterUpdates = 1,
            Failure = new IOException("The connection was closed."),
        };

        _ = await Assert.ThrowsAsync<IOException>(() => ConsumeAsync(rig.ClientOver(inner, "Planner", Tier.Nvidia)));

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Incomplete, reading.Outcome);

        // The time to the first token stays content-based: no text arrived, so there is none.
        Assert.Equal(Metric.NotApplicable("no-content-update"), reading.TtftMs);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_consumer_that_throws_between_yields_leaves_one_cancelled_reading_and_its_own_exception()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time) { Updates = [new FakeUpdate("part 0 ", TimeSpan.FromMilliseconds(50)), new FakeUpdate("part 1 ", TimeSpan.FromMilliseconds(50))] };
        MeteredChatClient client = rig.ClientOver(inner, "Planner");

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Prompt))
            {
                Assert.NotNull(update);
                throw new InvalidOperationException("The consumer failed.");
            }
        });

        Assert.Equal("The consumer failed.", thrown.Message);
        Assert.Equal(StepOutcomes.Cancelled, Assert.Single(rig.Sink.Snapshot()).Outcome);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task Cancellation_through_WithCancellation_yields_one_cancelled_reading()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time) { Updates = [.. Enumerable.Range(0, 5).Select(static i => new FakeUpdate($"part {i} ", TimeSpan.FromMilliseconds(50)))] };
        MeteredChatClient client = rig.ClientOver(inner, "Planner");
        using CancellationTokenSource cancellation = new();
        int seen = 0;

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Prompt).WithCancellation(cancellation.Token))
            {
                Assert.NotNull(update);
                seen++;
                if (seen == 2)
                {
                    await cancellation.CancelAsync();
                }
            }
        });

        Assert.Equal(2, seen);
        Assert.Equal(StepOutcomes.Cancelled, Assert.Single(rig.Sink.Snapshot()).Outcome);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task An_ok_call_with_empty_output_has_zero_chars_and_the_sha256_of_the_empty_string()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseText = string.Empty }, "Planner");

        _ = await client.GetResponseAsync(Prompt).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
        Assert.Equal(0, reading.OutputChars);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", reading.OutputSha256);
        Assert.Equal(string.Empty, reading.OutputText);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public async Task A_stream_that_was_obtained_but_never_enumerated_starts_no_call_and_leaves_the_slot_and_the_gate_free()
    {
        using SerializedMeterGate gate = new();
        await using MeterRig rig = await MeterRig.CreateAsync(gate: gate);
        FakeChatClient inner = new(rig.Time);
        MeteredChatClient client = rig.ClientOver(inner, "Planner");

        IAsyncEnumerable<ChatResponseUpdate> unused = client.GetStreamingResponseAsync(Prompt);

        Assert.NotNull(unused);
        Assert.Equal(1, gate.CurrentCount);
        Assert.Empty(rig.Sink.Snapshot());
        _ = await client.GetResponseAsync(Prompt).WaitAsync(HangGuard);
        _ = Assert.Single(rig.Sink.Snapshot());
        Assert.DoesNotContain(inner.Calls, static call => call.Streaming);
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public void The_classifier_follows_every_member_of_an_aggregate_and_gives_up_past_its_node_limit()
    {
        AggregateException cutAndUnauthorized = new(new IOException("closed"), new HttpRequestException("auth", inner: null, HttpStatusCode.Unauthorized));
        AggregateException cutOnly = new(new InvalidOperationException("other"), new IOException("closed"));
        Exception shallow = new IOException("closed");
        for (int i = 0; i < 10; i++)
        {
            shallow = new InvalidOperationException("wrap", shallow);
        }

        Exception deep = new IOException("closed");
        for (int i = 0; i < 70; i++)
        {
            deep = new InvalidOperationException("wrap", deep);
        }

        Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(cutAndUnauthorized, Tier.CloudGpu, false, CancellationToken.None));
        Assert.Equal(StepOutcomes.Incomplete, OutcomeClassifier.Classify(cutOnly, Tier.CloudGpu, false, CancellationToken.None));
        Assert.Equal(StepOutcomes.Incomplete, OutcomeClassifier.Classify(shallow, Tier.CloudGpu, false, CancellationToken.None));
        Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(deep, Tier.CloudGpu, false, CancellationToken.None));
    }

    [Fact]
    [Trait("Spec", "REQ-007")]
    public void A_cancellation_caused_by_a_timeout_is_an_error_even_when_the_caller_token_is_cancelled_too()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(new TaskCanceledException("timed out", new TimeoutException()), Tier.Local, true, cancelled.Token));
        Assert.Equal(StepOutcomes.Cancelled, OutcomeClassifier.Classify(new TaskCanceledException("cancelled"), Tier.Local, true, cancelled.Token));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> NullUpdateAsync(FakeTimeProvider time)
    {
        time.Advance(TimeSpan.FromMilliseconds(50));
        await Task.Yield();
        yield return null!;
    }

    // An inner client whose stream is whatever the test builds; its buffered path is not used.
    private sealed class ScriptedStreamClient(Func<IAsyncEnumerable<ChatResponseUpdate>> stream) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            stream();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // One update of text, then the end of the stream (or a failing MoveNextAsync when one is given), and a DisposeAsync that fails.
    private sealed class DisposeFailsStream(FakeTimeProvider time, Exception? moveNextFailure = null) : IAsyncEnumerable<ChatResponseUpdate>, IAsyncEnumerator<ChatResponseUpdate>
    {
        private bool _served;

        public ChatResponseUpdate Current { get; private set; } = new();

        public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync()
        {
            if (_served)
            {
                return ValueTask.FromResult(false);
            }

            _served = true;
            if (moveNextFailure is not null)
            {
                time.Advance(TimeSpan.FromMilliseconds(50));
                return ValueTask.FromException<bool>(moveNextFailure);
            }

            time.Advance(TimeSpan.FromMilliseconds(50));
            Current = new ChatResponseUpdate(ChatRole.Assistant, "text");
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidDataException("The enumerator failed to dispose."));
    }

    // ---- AJ-042: usage is measured when reported and not-reported when omitted, never 0 (REQ-008, EDGE-007) ----

    private static UsageDetails Usage(long? input, long? output) => new() { InputTokenCount = input, OutputTokenCount = output };

    private static ChatOptions Limit(int maxOutputTokens) => new() { MaxOutputTokens = maxOutputTokens };

    private static async Task ConsumeStreamAsync(MeteredChatClient client, ChatOptions? options = null)
    {
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Prompt, options))
        {
            _ = update;
        }
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task Usage_is_recorded_as_measured()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseUsage = Usage(120, 45) }, "Planner");

        _ = await client.GetResponseAsync(Prompt).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(Metric.Measured(120, "tokens"), reading.InputTokens);
        Assert.Equal(Metric.Measured(45, "tokens"), reading.OutputTokens);
        string json = JsonSerializer.Serialize(reading, Context);
        Assert.Contains("\"inputTokens\":{\"value\":120,\"label\":\"measured\",\"unit\":\"tokens\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"outputTokens\":{\"value\":45,\"label\":\"measured\",\"unit\":\"tokens\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task Streaming_usage_is_recorded_as_measured()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time)
        {
            Updates = [new FakeUpdate("The ", TimeSpan.FromMilliseconds(100)), new FakeUpdate("report", TimeSpan.FromMilliseconds(100), Usage(120, 45))],
        };

        await ConsumeStreamAsync(rig.ClientOver(inner, "Writer")).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(Metric.Measured(120, "tokens"), reading.InputTokens);
        Assert.Equal(Metric.Measured(45, "tokens"), reading.OutputTokens);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    [Trait("Spec", "EDGE-007")]
    public async Task No_usage_buffered_is_not_reported()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time), "Planner");

        _ = await client.GetResponseAsync(Prompt).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        AssertTokensNotReported(reading);
        string json = JsonSerializer.Serialize(reading, Context);
        Assert.Contains("\"inputTokens\":{\"value\":null,\"label\":\"not-reported\",\"reason\":\"provider-omitted-usage\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"outputTokens\":{\"value\":null,\"label\":\"not-reported\",\"reason\":\"provider-omitted-usage\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    [Trait("Spec", "EDGE-007")]
    public async Task No_usage_streaming_is_not_reported()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time)
        {
            Updates = [new FakeUpdate("The ", TimeSpan.FromMilliseconds(100)), new FakeUpdate("report", TimeSpan.FromMilliseconds(100))],
        };

        await ConsumeStreamAsync(rig.ClientOver(inner, "Writer")).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
        AssertTokensNotReported(reading);
    }

    [Fact]
    [Trait("Spec", "EDGE-007")]
    public async Task Cost_is_NA_with_a_null_value_when_tokens_are_not_reported_and_never_0()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time), "Planner");

        _ = await client.GetResponseAsync(Prompt).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(MetricLabel.NotApplicable, reading.Cost.Label);
        Assert.Null(reading.Cost.Value);
        Assert.Equal("cost-not-configured", reading.Cost.Reason);
        Assert.Contains("\"cost\":{\"value\":null,\"label\":\"N/A\",\"reason\":\"cost-not-configured\"}", JsonSerializer.Serialize(reading, Context), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_provider_reported_zero_stays_measured_zero_on_both_paths()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        MeteredChatClient buffered = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseUsage = Usage(0, 0) }, "Planner");
        _ = await buffered.GetResponseAsync(Prompt).WaitAsync(HangGuard);
        FakeChatClient streamingInner = new(rig.Time) { Updates = [new FakeUpdate("x", TimeSpan.FromMilliseconds(10), Usage(0, 0))] };
        await ConsumeStreamAsync(rig.ClientOver(streamingInner, "Writer")).WaitAsync(HangGuard);

        IReadOnlyList<StepReading> readings = rig.Sink.Snapshot();
        Assert.Equal(2, readings.Count);
        Assert.All(
            readings,
            reading =>
            {
                Assert.Equal(Metric.Measured(0, "tokens"), reading.InputTokens);
                Assert.Equal(Metric.Measured(0, "tokens"), reading.OutputTokens);
                Assert.Equal(MetricLabel.Measured, reading.InputTokens.Label);
                Assert.Equal(0.0, reading.OutputTokens.Value);
            });
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData(120L, null)]
    [InlineData(null, 45L)]
    public async Task Each_count_is_mapped_on_its_own(long? input, long? output)
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseUsage = Usage(input, output) }, "Planner");

        _ = await client.GetResponseAsync(Prompt).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(input is { } i ? Metric.Measured(i, "tokens") : Metric.NotReported("provider-omitted-usage"), reading.InputTokens);
        Assert.Equal(output is { } o ? Metric.Measured(o, "tokens") : Metric.NotReported("provider-omitted-usage"), reading.OutputTokens);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_negative_count_is_not_reported_as_invalid_and_never_measured()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseUsage = Usage(-1, 5) }, "Planner");

        _ = await client.GetResponseAsync(Prompt).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(Metric.NotReported("provider-invalid-usage"), reading.InputTokens);
        Assert.Equal(Metric.Measured(5, "tokens"), reading.OutputTokens);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_stream_that_repeats_cumulative_usage_reports_the_last_values_not_their_sum()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time)
        {
            Updates =
            [
                new FakeUpdate("a ", TimeSpan.FromMilliseconds(10), Usage(11, 0)),
                new FakeUpdate("b ", TimeSpan.FromMilliseconds(10), Usage(11, 1)),
                new FakeUpdate("c", TimeSpan.FromMilliseconds(10), Usage(11, 2)),
                new FakeUpdate(string.Empty, TimeSpan.FromMilliseconds(10)),
            ],
        };

        await ConsumeStreamAsync(rig.ClientOver(inner, "Writer")).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(Metric.Measured(11, "tokens"), reading.InputTokens);
        Assert.Equal(Metric.Measured(2, "tokens"), reading.OutputTokens);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_stream_that_fails_after_its_usage_keeps_the_usage_it_saw_on_the_error_reading()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time)
        {
            Updates = [new FakeUpdate("a", TimeSpan.FromMilliseconds(10), Usage(7, 3))],
            Failure = new InvalidDataException("The stream broke."),
            FailAfterUpdates = 1,
        };

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => ConsumeStreamAsync(rig.ClientOver(inner, "Writer")).WaitAsync(HangGuard));

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal(Metric.Measured(7, "tokens"), reading.InputTokens);
        Assert.Equal(Metric.Measured(3, "tokens"), reading.OutputTokens);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_failed_buffered_call_reports_its_tokens_as_not_reported()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time) { Failure = new InvalidDataException("The call broke.") };

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => rig.ClientOver(inner, "Planner").GetResponseAsync(Prompt).WaitAsync(HangGuard));

        AssertTokensNotReported(Assert.Single(rig.Sink.Snapshot()));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task Max_tokens_exceeded_flag()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseUsage = Usage(120, 600) }, "Planner");

        _ = await client.GetResponseAsync(Prompt, Limit(512)).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.True(reading.Flags.HasFlag(ReadingFlags.MaxTokensExceeded));
        Assert.Contains("max-tokens-exceeded", ReadingFlagNames.ToStrings(reading.Flags));
        Assert.Equal(Metric.Measured(600, "tokens"), reading.OutputTokens);
        Assert.Contains("\"flags\":[\"max-tokens-exceeded\"]", JsonSerializer.Serialize(reading, Context), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task Max_tokens_exceeded_flag_on_the_streaming_path()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time) { Updates = [new FakeUpdate("long answer", TimeSpan.FromMilliseconds(10), Usage(120, 600))] };

        await ConsumeStreamAsync(rig.ClientOver(inner, "Writer"), Limit(512)).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.True(reading.Flags.HasFlag(ReadingFlags.MaxTokensExceeded));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task The_max_tokens_flag_is_added_to_the_energy_flags_of_the_window_and_never_replaces_them()
    {
        // A window shorter than two sample periods carries the low-resolution flag; the step flag joins it.
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time) { ResponseUsage = Usage(10, 600), ResponseDelay = TimeSpan.FromMilliseconds(10) };

        _ = await rig.ClientOver(inner, "Planner").GetResponseAsync(Prompt, Limit(512)).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.True(reading.Flags.HasFlag(ReadingFlags.MaxTokensExceeded));
        Assert.True(reading.Flags.HasFlag(ReadingFlags.LowResolution));
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData(512, 512L, false)]
    [InlineData(512, 100L, false)]
    [InlineData(0, 1L, true)]
    [InlineData(512, 513L, true)]
    public async Task Only_output_tokens_above_the_limit_raise_the_flag_and_a_limit_of_zero_is_a_limit(int limit, long output, bool flagged)
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseUsage = Usage(10, output) }, "Planner");

        _ = await client.GetResponseAsync(Prompt, Limit(limit)).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(flagged, reading.Flags.HasFlag(ReadingFlags.MaxTokensExceeded));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task Unreported_output_tokens_or_no_limit_leave_the_max_tokens_flag_absent()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();

        // No usage with a limit; usage with no options; usage with options that have no limit.
        _ = await rig.ClientOver(new FakeChatClient(rig.Time), "A").GetResponseAsync(Prompt, Limit(512)).WaitAsync(HangGuard);
        _ = await rig.ClientOver(new FakeChatClient(rig.Time) { ResponseUsage = Usage(10, 9999) }, "B").GetResponseAsync(Prompt).WaitAsync(HangGuard);
        _ = await rig.ClientOver(new FakeChatClient(rig.Time) { ResponseUsage = Usage(10, 9999) }, "C").GetResponseAsync(Prompt, new ChatOptions()).WaitAsync(HangGuard);

        IReadOnlyList<StepReading> readings = rig.Sink.Snapshot();
        Assert.Equal(3, readings.Count);
        Assert.All(readings, reading => Assert.False(reading.Flags.HasFlag(ReadingFlags.MaxTokensExceeded)));
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData(Tier.Nvidia)]
    [InlineData(Tier.Nebius)]
    public async Task A_hosted_tier_reading_may_carry_the_max_tokens_flag_because_it_names_no_measurement(Tier tier)
    {
        await using MeterRig rig = await MeterRig.CreateAsync(tier);
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseUsage = Usage(10, 600) }, "Planner", tier);

        _ = await client.GetResponseAsync(Prompt, Limit(512)).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(tier, reading.Tier);
        Assert.Equal(ReadingFlags.MaxTokensExceeded, reading.Flags);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task The_cost_of_a_step_comes_only_from_the_cost_calculator_and_is_computed_from_the_call()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        RecordingCostCalculator calculator = new(Metric.Estimated(0.25, "USD"));
        rig.Cost = calculator;
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseUsage = Usage(120, 45), ResponseDelay = TimeSpan.FromMilliseconds(1500) }, "Planner");

        _ = await client.GetResponseAsync(Prompt).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(Metric.Estimated(0.25, "USD"), reading.Cost);
        RecordingCostCalculator.Call call = Assert.Single(calculator.Calls);
        Assert.Equal(MeterRig.IdentityOf("Planner", Tier.Local), call.Identity);
        Assert.Equal(TokenCounts.From(120, 45), call.Tokens);
        Assert.Equal(1.5, call.WallSeconds, 6);
        Assert.Equal(reading.GrossJoules, call.Energy.GrossJoules);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task The_calculator_sees_not_reported_tokens_when_the_provider_sent_no_usage()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        RecordingCostCalculator calculator = new(Metric.NotApplicable("no-token-basis"));
        rig.Cost = calculator;

        _ = await rig.ClientOver(new FakeChatClient(rig.Time), "Planner").GetResponseAsync(Prompt).WaitAsync(HangGuard);

        Assert.Equal(TokenCounts.NotReported, Assert.Single(calculator.Calls).Tokens);
        Assert.Equal("no-token-basis", Assert.Single(rig.Sink.Snapshot()).Cost.Reason);
    }

    [Fact]
    [Trait("Spec", "EDGE-007")]
    public async Task The_cost_calculator_is_required_by_the_decorator()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        MeteringServices withoutCost = rig.Services with { Cost = null! };

        _ = Assert.Throws<ArgumentNullException>(() => new MeteredChatClient(new FakeChatClient(rig.Time), MeterRig.IdentityOf("Planner", Tier.Local), withoutCost));
    }

    [Fact]
    [Trait("Spec", "EDGE-007")]
    public async Task The_not_configured_calculator_answers_NA_cost_not_configured_for_every_input()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        EnergyReading energy = window.Close();
        NotConfiguredCostCalculator calculator = NotConfiguredCostCalculator.Instance;

        foreach (TokenCounts tokens in new[] { TokenCounts.NotReported, TokenCounts.From(0, 0), TokenCounts.From(1_000_000, 2_000_000) })
        {
            foreach (Tier tier in Enum.GetValues<Tier>())
            {
                Metric cost = calculator.Compute(MeterRig.IdentityOf("Planner", tier), tokens, energy, 1.0);

                Assert.Equal(Metric.NotApplicable("cost-not-configured"), cost);
                Assert.Null(cost.Value);
            }
        }

        _ = Assert.Throws<ArgumentNullException>(() => calculator.Compute(null!, TokenCounts.NotReported, energy, 1.0));
        _ = Assert.Throws<ArgumentNullException>(() => calculator.Compute(MeterRig.IdentityOf("Planner", Tier.Local), null!, energy, 1.0));
        _ = Assert.Throws<ArgumentNullException>(() => calculator.Compute(MeterRig.IdentityOf("Planner", Tier.Local), TokenCounts.NotReported, null!, 1.0));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void TokenCounts_maps_each_count_by_its_label_and_never_invents_a_zero()
    {
        TokenCounts none = TokenCounts.From(null, null);
        Assert.Equal(Metric.NotReported("provider-omitted-usage"), none.InputTokens);
        Assert.Equal(Metric.NotReported("provider-omitted-usage"), none.OutputTokens);
        Assert.Null(none.InputCount);
        Assert.Null(none.OutputCount);

        TokenCounts zero = TokenCounts.From(0, 0);
        Assert.Equal(Metric.Measured(0, "tokens"), zero.InputTokens);
        Assert.Equal(0L, zero.OutputCount);

        Assert.True(TokenCounts.From(1, 600).ExceedsOutputLimit(512));
        Assert.False(TokenCounts.From(1, 512).ExceedsOutputLimit(512));
        Assert.False(TokenCounts.From(1, null).ExceedsOutputLimit(0));
        Assert.False(TokenCounts.From(1, 600).ExceedsOutputLimit(null));
        Assert.Equal(TokenCounts.NotReported, TokenCounts.From(null, null));
    }

    [Fact]
    [Trait("Spec", "EDGE-007")]
    public void The_no_usage_fixture_is_a_well_formed_reading_with_not_reported_tokens_and_NA_cost()
    {
        StepReading reading = StepReadingFixtures.NoUsage();

        Assert.Equal(Metric.NotReported("provider-omitted-usage"), reading.InputTokens);
        Assert.Equal(Metric.NotReported("provider-omitted-usage"), reading.OutputTokens);
        Assert.Equal(Metric.NotApplicable("cost-not-configured"), reading.Cost);
        string json = JsonSerializer.Serialize(reading, Context);
        Assert.Contains("\"inputTokens\":{\"value\":null,\"label\":\"not-reported\",\"reason\":\"provider-omitted-usage\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"cost\":{\"value\":null,\"label\":\"N/A\",\"reason\":\"cost-not-configured\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_trailing_usage_chunk_with_both_counts_null_keeps_the_earlier_counts_and_the_max_tokens_flag()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time)
        {
            Updates =
            [
                new FakeUpdate("a ", TimeSpan.FromMilliseconds(10), Usage(11, 600)),
                new FakeUpdate(string.Empty, TimeSpan.FromMilliseconds(10), Usage(null, null)),
            ],
        };

        await ConsumeStreamAsync(rig.ClientOver(inner, "Writer"), Limit(512)).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(Metric.Measured(11, "tokens"), reading.InputTokens);
        Assert.Equal(Metric.Measured(600, "tokens"), reading.OutputTokens);
        Assert.True(reading.Flags.HasFlag(ReadingFlags.MaxTokensExceeded));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task Each_count_of_a_stream_is_replaced_on_its_own_by_a_later_non_null_count()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time)
        {
            Updates =
            [
                new FakeUpdate("a ", TimeSpan.FromMilliseconds(10), Usage(11, null)),
                new FakeUpdate("b ", TimeSpan.FromMilliseconds(10), Usage(null, 2)),
                new FakeUpdate("c", TimeSpan.FromMilliseconds(10), Usage(null, 5)),
            ],
        };

        await ConsumeStreamAsync(rig.ClientOver(inner, "Writer")).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(Metric.Measured(11, "tokens"), reading.InputTokens);
        Assert.Equal(Metric.Measured(5, "tokens"), reading.OutputTokens);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_final_text_less_usage_update_is_captured_and_does_not_move_the_time_to_the_first_token()
    {
        // The shape of an OpenAI stream with include_usage: the last chunk has no choices, only usage.
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time)
        {
            Updates =
            [
                new FakeUpdate("The ", TimeSpan.FromMilliseconds(300)),
                new FakeUpdate("report", TimeSpan.FromMilliseconds(100)),
                new FakeUpdate(string.Empty, TimeSpan.FromMilliseconds(50), Usage(11, 2)),
            ],
        };

        await ConsumeStreamAsync(rig.ClientOver(inner, "Writer")).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(Metric.Measured(300, "ms"), reading.TtftMs);
        Assert.Equal(Metric.Measured(11, "tokens"), reading.InputTokens);
        Assert.Equal(Metric.Measured(2, "tokens"), reading.OutputTokens);
        Assert.Equal("The report", reading.OutputText);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_consumer_that_stops_reading_after_a_usage_update_gets_a_cancelled_reading_with_the_usage_kept()
    {
        await using MeterRig rig = await MeterRig.CreateAsync();
        FakeChatClient inner = new(rig.Time)
        {
            Updates =
            [
                new FakeUpdate("a ", TimeSpan.FromMilliseconds(10)),
                new FakeUpdate(string.Empty, TimeSpan.FromMilliseconds(10), Usage(7, 3)),
                new FakeUpdate("b", TimeSpan.FromMilliseconds(10)),
            ],
        };
        MeteredChatClient client = rig.ClientOver(inner, "Writer");

        await StopAfterUsageAsync(client).WaitAsync(HangGuard);

        StepReading reading = Assert.Single(rig.Sink.Snapshot());
        Assert.Equal(StepOutcomes.Cancelled, reading.Outcome);
        Assert.Equal(Metric.Measured(7, "tokens"), reading.InputTokens);
        Assert.Equal(Metric.Measured(3, "tokens"), reading.OutputTokens);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_failing_cost_calculator_after_a_failed_buffered_call_keeps_the_calls_exception_attaches_the_failure_and_frees_the_gate()
    {
        using SerializedMeterGate gate = new();
        await using MeterRig rig = await MeterRig.CreateAsync(gate: gate);
        InvalidOperationException calculatorFailure = new("The price table broke.");
        rig.Cost = new ThrowingCostCalculator(calculatorFailure);
        TimeoutException failure = new("The provider timed out.");
        FakeChatClient inner = new(rig.Time) { Failure = failure };

        TimeoutException thrown = await Assert.ThrowsAsync<TimeoutException>(() => rig.ClientOver(inner, "Planner").GetResponseAsync(Prompt).WaitAsync(HangGuard));

        Assert.Same(failure, thrown);
        Assert.Same(calculatorFailure, thrown.Data[MeteredChatClient.ReadingFailureKey]);
        Assert.Empty(rig.Sink.Snapshot());
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_failing_cost_calculator_after_a_failed_stream_keeps_the_calls_exception_attaches_the_failure_and_frees_the_gate()
    {
        using SerializedMeterGate gate = new();
        await using MeterRig rig = await MeterRig.CreateAsync(gate: gate);
        InvalidOperationException calculatorFailure = new("The price table broke.");
        rig.Cost = new ThrowingCostCalculator(calculatorFailure);
        InvalidDataException failure = new("The stream broke.");
        FakeChatClient inner = new(rig.Time)
        {
            Updates = [new FakeUpdate("a", TimeSpan.FromMilliseconds(10))],
            Failure = failure,
            FailAfterUpdates = 1,
        };

        InvalidDataException thrown = await Assert.ThrowsAsync<InvalidDataException>(() => ConsumeStreamAsync(rig.ClientOver(inner, "Writer")).WaitAsync(HangGuard));

        Assert.Same(failure, thrown);
        Assert.Same(calculatorFailure, thrown.Data[MeteredChatClient.ReadingFailureKey]);
        Assert.Empty(rig.Sink.Snapshot());
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_failing_cost_calculator_after_a_successful_call_surfaces_and_frees_the_gate_and_the_slot()
    {
        using SerializedMeterGate gate = new();
        await using MeterRig rig = await MeterRig.CreateAsync(gate: gate);
        InvalidOperationException calculatorFailure = new("The price table broke.");
        rig.Cost = new ThrowingCostCalculator(calculatorFailure);
        MeteredChatClient client = rig.ClientOver(new FakeChatClient(rig.Time), "Planner");

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Prompt).WaitAsync(HangGuard));

        Assert.Same(calculatorFailure, thrown);
        Assert.Empty(rig.Sink.Snapshot());
        Assert.Equal(1, gate.CurrentCount);

        // The slot is free as well: the second call fails the same way and not with the in-flight message.
        InvalidOperationException second = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Prompt).WaitAsync(HangGuard));
        Assert.Same(calculatorFailure, second);
    }

    private static async Task StopAfterUsageAsync(MeteredChatClient client)
    {
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Prompt))
        {
            if (update.Contents.OfType<UsageContent>().Any())
            {
                break;
            }
        }
    }

    // A calculator that always fails, to prove that a failure of the reading never replaces the exception of the call.
    private sealed class ThrowingCostCalculator(Exception failure) : ICostCalculator
    {
        public Metric Compute(StepIdentity identity, TokenCounts tokens, EnergyReading energy, double wallSeconds) => throw failure;
    }

    private static void AssertTokensNotReported(StepReading reading)
    {
        foreach (Metric tokens in new[] { reading.InputTokens, reading.OutputTokens })
        {
            Assert.Equal(MetricLabel.NotReported, tokens.Label);
            Assert.Null(tokens.Value);
            Assert.Equal("provider-omitted-usage", tokens.Reason);
        }

        Assert.Equal(MetricLabel.NotApplicable, reading.Cost.Label);
        Assert.Null(reading.Cost.Value);
        Assert.False(reading.Flags.HasFlag(ReadingFlags.MaxTokensExceeded));
    }

    // Records what the decorator hands to the calculator and answers one fixed metric.
    private sealed class RecordingCostCalculator(Metric answer) : ICostCalculator
    {
        private readonly List<Call> _calls = [];

        public IReadOnlyList<Call> Calls => _calls;

        public Metric Compute(StepIdentity identity, TokenCounts tokens, EnergyReading energy, double wallSeconds)
        {
            _calls.Add(new Call(identity, tokens, energy, wallSeconds));
            return answer;
        }

        public sealed record Call(StepIdentity Identity, TokenCounts Tokens, EnergyReading Energy, double WallSeconds);
    }

    private static Task<(int Count, string Text)> ConsumeAsync(MeteredChatClient client) => ConsumeCoreAsync(client).WaitAsync(HangGuard);

    private static async Task<(int Count, string Text)> ConsumeCoreAsync(MeteredChatClient client)
    {
        int count = 0;
        StringBuilder text = new();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Prompt))
        {
            count++;
            _ = text.Append(update.Text);
        }

        return (count, text.ToString());
    }

    // The services of one test: a fake clock, a sink, a meter fitting the tier (deterministic for the measured tiers, unmetered for the hosted
    // ones) and a gate (the one that admits everyone unless the test brings its own). Nothing in a test waits on the wall clock.
    private sealed class MeterRig : IAsyncDisposable
    {
        private MeterRig(FakeTimeProvider time, InMemoryMetricsSink sink, IEnergyMeter meter, IMeterGate gate)
        {
            Time = time;
            Sink = sink;
            Meter = meter;
            Gate = gate;
        }

        public FakeTimeProvider Time { get; }

        public InMemoryMetricsSink Sink { get; }

        public IEnergyMeter Meter { get; }

        public IMeterGate Gate { get; }

        // The cost calculator the services carry; a test replaces it before it builds a client.
        public ICostCalculator Cost { get; set; } = NotConfiguredCostCalculator.Instance;

        public AgentJoulesInstruments Instruments { get; set; } = TestInstruments.Create();

        public MeteringServices Services => new(Meter, Gate, Sink, Cost, Instruments, Time);

        public static async Task<MeterRig> CreateAsync(Tier tier = Tier.Local, IMeterGate? gate = null, bool startMeter = true)
        {
            FakeTimeProvider time = new();
            IEnergyMeter meter = tier is Tier.Nvidia or Tier.Nebius
                ? new UnmeteredEnergyMeter(time)
                : new DeterministicEnergyMeter(time, static _ => 100.0, sampleRateHz: 10.0, counterSupported: true, MeterMethodSetting.Auto);
            if (startMeter)
            {
                await meter.StartAsync(CancellationToken.None);
            }

            return new MeterRig(time, new InMemoryMetricsSink(), meter, gate ?? NoOpMeterGate.Instance);
        }

        public static StepIdentity IdentityOf(string agent, Tier tier) => new(agent, "model-a", tier, "summarise", 1, IsJudge: false);

        public MeteredChatClient ClientOver(IChatClient inner, string agent, Tier tier = Tier.Local) => new(inner, IdentityOf(agent, tier), Services);

        public ValueTask DisposeAsync() => Meter.DisposeAsync();
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