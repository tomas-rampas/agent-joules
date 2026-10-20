using System.CommandLine;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using AgentJoules.Cli;
using AgentJoules.Cli.Hosting;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Graph;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using Xunit;

using CoreMetric = AgentJoules.Core.Labels.Metric;
using OtelMetric = OpenTelemetry.Metrics.Metric;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-051 (REQ-014, design 3.3 "Instruments", 3.7, 6.1): the five instruments of the meter <c>AgentJoules</c>, the activity source of the agents and the exporters the telemetry setup
/// registers. The instruments are read through an in-memory metric exporter; a meter provider sees every meter of that name in the process, so every rig uses an agent name of its own and
/// reads only its own points. The exporters are checked on two sides: the console one by what it prints (it writes to the console static and has no writer to inject), the OTLP one
/// by what it adds to the service collection (building its provider would send data to the endpoint).
/// </summary>
public sealed class TelemetryTests
{
    private const string Energy = "agentjoules.step.energy";
    private const string Power = "agentjoules.step.power";
    private const string Tokens = "agentjoules.step.tokens";
    private const string Latency = "agentjoules.step.latency";
    private const string Cost = "agentjoules.step.cost";
    private const string AgentTag = "gen_ai.agent.name";
    private const string TierTag = "aj.tier";
    private const string ModelTag = "gen_ai.request.model";
    private const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string MetricsEndpointKey = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";
    private const string TracesEndpointKey = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";
    private const string GeneralHeaders = "OTEL_EXPORTER_OTLP_HEADERS";
    private const string MetricsHeaders = "OTEL_EXPORTER_OTLP_METRICS_HEADERS";
    private const string TracesHeaders = "OTEL_EXPORTER_OTLP_TRACES_HEADERS";
    private const string RecordingBoom = "the listener refused the measurement";

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly string[] FiveInstruments = [Energy, Power, Tokens, Latency, Cost];

    private static TelemetryCall Priced(Tier tier = Tier.Local) => new() { Tier = tier, Cost = new FixedCostCalculator(CoreMetric.Estimated(0.01, "EUR")) };

    private static MeasurementPoint[] Of(IReadOnlyList<MeasurementPoint> points, string instrument) => [.. points.Where(point => point.Instrument == instrument)];

    private static void AssertKind(IReadOnlyList<MeasurementPoint> points, string instrument, string? unit, MetricType type)
    {
        MeasurementPoint[] found = Of(points, instrument);
        Assert.NotEmpty(found);
        // An instrument without a unit exports the empty string.
        Assert.All(found, point => Assert.Equal((unit ?? string.Empty, type), (point.Unit ?? string.Empty, point.Type)));
    }

    private static string[] Keys(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];

    private static MeasurementPoint Only(IReadOnlyList<MeasurementPoint> points, string instrument, string dimension, string value) =>
        Assert.Single(Of(points, instrument), point => point.Tag(dimension) == value);

    private static IConfiguration Config(string? endpoint) =>
        new ConfigurationBuilder().AddInMemoryCollection(endpoint is null ? [] : [new KeyValuePair<string, string?>(EndpointKey, endpoint)]).Build();

    private static ServiceCollection Telemetry(bool console, string? endpoint)
    {
        var services = new ServiceCollection();
        _ = services.AddMetrics();
        _ = TelemetrySetup.Add(services, Config(endpoint), console);
        return services;
    }

    // The provider builders the SDK registers are internal types, so the registrations are counted by interface name: each exporter adds exactly one.
    private static int Registrations(IServiceCollection services, string interfaceName) => services.Count(descriptor => descriptor.ServiceType.Name == interfaceName);

    private static bool HasOtlpOptions(IServiceCollection services) => services.Any(descriptor => descriptor.ServiceType == typeof(IOptionsFactory<OtlpExporterOptions>));

    private static string UniqueAgent() => "Agent-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    // ----- the instruments

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Five_instruments_with_three_attributes()
    {
        using TelemetryRig rig = TelemetryRig.Create();

        StepReading reading = await rig.CallAsync(Priced());
        IReadOnlyList<MeasurementPoint> points = rig.Flush();

        // One local call: gross 120 J (60 W for 2 s), net 80 J (the 20 W baseline times 2 s taken off), average 60 W, 120 and 45 tokens, 2000 ms, 0.01 EUR.
        Assert.Equal(120.0, reading.GrossJoules.Value);
        Assert.Equal(80.0, reading.NetJoules.Value);
        Assert.Equal(FiveInstruments.Order(StringComparer.Ordinal), points.Select(point => point.Instrument).Distinct().Order(StringComparer.Ordinal));
        foreach (MeasurementPoint point in points)
        {
            Assert.Equal(rig.Agent, point.Tag(AgentTag));
            Assert.Equal("local", point.Tag(TierTag));
            Assert.Equal(rig.Model, point.Tag(ModelTag));
        }

        MeasurementPoint gross = Only(points, Energy, "aj.energy.kind", "gross");
        MeasurementPoint net = Only(points, Energy, "aj.energy.kind", "net");
        Assert.Equal(2, Of(points, Energy).Length);
        Assert.Equal(120.0, gross.Sum, 6);
        Assert.Equal(80.0, net.Sum, 6);
        Assert.Equal(1, gross.Count);
        Assert.Equal(1, net.Count);

        MeasurementPoint power = Assert.Single(Of(points, Power));
        Assert.Equal(60.0, power.Sum, 6);
        Assert.Equal(1, power.Count);

        Assert.Equal(2, Of(points, Tokens).Length);
        Assert.Equal(120.0, Only(points, Tokens, "aj.token.type", "input").Sum);
        Assert.Equal(45.0, Only(points, Tokens, "aj.token.type", "output").Sum);

        MeasurementPoint latency = Assert.Single(Of(points, Latency));
        Assert.Equal(2000.0, latency.Sum, 6);
        Assert.Equal(1, latency.Count);

        MeasurementPoint cost = Assert.Single(Of(points, Cost));
        Assert.Equal(0.01, cost.Sum, 9);
        Assert.Equal("EUR", cost.Tag("aj.currency"));

        // Each point carries its three attributes and at most the one dimension of its instrument: nothing else (no prompt, no output, no task) is an attribute.
        Assert.All(Of(points, Energy), point => Assert.Equal(Keys([AgentTag, "aj.energy.kind", ModelTag, TierTag]), Keys(point.Tags.Keys)));
        Assert.All(Of(points, Tokens), point => Assert.Equal(Keys([AgentTag, "aj.token.type", ModelTag, TierTag]), Keys(point.Tags.Keys)));
        Assert.All(Of(points, Cost), point => Assert.Equal(Keys([AgentTag, "aj.currency", ModelTag, TierTag]), Keys(point.Tags.Keys)));
        Assert.All(Of(points, Power).Concat(Of(points, Latency)), point => Assert.Equal(Keys([AgentTag, ModelTag, TierTag]), Keys(point.Tags.Keys)));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Instrument_names_units_and_kinds_are_the_design_ones()
    {
        using TelemetryRig rig = TelemetryRig.Create();

        _ = await rig.CallAsync(Priced());
        IReadOnlyList<MeasurementPoint> points = rig.Flush();

        AssertKind(points, Energy, "J", MetricType.Histogram);
        AssertKind(points, Power, "W", MetricType.Histogram);
        AssertKind(points, Tokens, "{token}", MetricType.LongSum);
        AssertKind(points, Latency, "ms", MetricType.Histogram);
        AssertKind(points, Cost, null, MetricType.DoubleSum);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Null_metrics_not_recorded()
    {
        using TelemetryRig rig = TelemetryRig.Create();

        // A hosted tier: energy and power are N/A (hosted-tier), the response has no usage (tokens not-reported), and the M2 cost calculator answers N/A.
        StepReading reading = await rig.CallAsync(new TelemetryCall { Tier = Tier.Nvidia, Usage = null });
        IReadOnlyList<MeasurementPoint> points = rig.Flush();

        Assert.Null(reading.GrossJoules.Value);
        Assert.Null(reading.NetJoules.Value);
        Assert.Null(reading.AvgW.Value);
        Assert.Null(reading.InputTokens.Value);
        Assert.Null(reading.OutputTokens.Value);
        Assert.Null(reading.Cost.Value);

        // The in-memory exporter holds the latency of the call and nothing else: no zero stands in for a value that does not exist.
        MeasurementPoint latency = Assert.Single(points);
        Assert.Equal(Latency, latency.Instrument);
        Assert.Equal("nvidia", latency.Tag(TierTag));
        Assert.Equal(2000.0, latency.Sum, 6);
        Assert.Empty(Of(points, Energy));
        Assert.Empty(Of(points, Power));
        Assert.Empty(Of(points, Tokens));
        Assert.Empty(Of(points, Cost));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Call_without_usage_records_energy_but_no_token_measurement()
    {
        using TelemetryRig rig = TelemetryRig.Create();

        _ = await rig.CallAsync(new TelemetryCall { Usage = null });
        IReadOnlyList<MeasurementPoint> points = rig.Flush();

        Assert.Empty(Of(points, Tokens));
        Assert.Empty(Of(points, Cost));
        Assert.Equal(2, Of(points, Energy).Length);
        Assert.Single(Of(points, Power));
        Assert.Single(Of(points, Latency));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Hosted_tier_priced_call_records_tokens_cost_and_latency_only()
    {
        using TelemetryRig rig = TelemetryRig.Create();

        _ = await rig.CallAsync(Priced(Tier.Nebius));
        IReadOnlyList<MeasurementPoint> points = rig.Flush();

        Assert.Equal([Cost, Latency, Tokens], points.Select(point => point.Instrument).Distinct().Order(StringComparer.Ordinal));
        Assert.All(points, point => Assert.Equal("nebius", point.Tag(TierTag)));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task One_reported_token_count_is_recorded_alone()
    {
        using TelemetryRig rig = TelemetryRig.Create();

        _ = await rig.CallAsync(new TelemetryCall { Usage = new UsageDetails { InputTokenCount = 7 } });
        IReadOnlyList<MeasurementPoint> points = rig.Flush();

        MeasurementPoint input = Assert.Single(Of(points, Tokens));
        Assert.Equal("input", input.Tag("aj.token.type"));
        Assert.Equal(7.0, input.Sum);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task A_measured_zero_is_recorded_and_a_missing_value_is_not()
    {
        using TelemetryRig rig = TelemetryRig.Create();

        _ = await rig.CallAsync(new TelemetryCall { Usage = new UsageDetails { InputTokenCount = 0, OutputTokenCount = 3 } });
        IReadOnlyList<MeasurementPoint> points = rig.Flush();

        Assert.Equal(0.0, Only(points, Tokens, "aj.token.type", "input").Sum);
        Assert.Equal(3.0, Only(points, Tokens, "aj.token.type", "output").Sum);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Negative_net_energy_is_not_recorded_and_gross_is()
    {
        using TelemetryRig rig = TelemetryRig.Create();

        // 60 W against a 100 W baseline for 2 s: gross 120 J, net -80 J (flagged below-baseline). A histogram of joules takes no negative value.
        StepReading reading = await rig.CallAsync(new TelemetryCall { BaselineWatts = 100.0 });
        IReadOnlyList<MeasurementPoint> points = rig.Flush();

        Assert.Equal(-80.0, reading.NetJoules.Value);
        Assert.Equal(120.0, Assert.Single(Of(points, Energy)).Sum, 6);
        Assert.Equal("gross", Assert.Single(Of(points, Energy)).Tag("aj.energy.kind"));
        Assert.Single(Of(points, Power));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Failed_call_still_records_the_measurements_it_has()
    {
        using TelemetryRig rig = TelemetryRig.Create();

        StepReading reading = await rig.CallAsync(new TelemetryCall { Failure = new HttpRequestException("provider down") });
        IReadOnlyList<MeasurementPoint> points = rig.Flush();

        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal(2, Of(points, Energy).Length);
        Assert.Single(Of(points, Latency));
        Assert.Empty(Of(points, Tokens));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Calls_of_one_agent_add_up_in_the_same_series()
    {
        using TelemetryRig rig = TelemetryRig.Create();

        _ = await rig.CallAsync(new TelemetryCall());
        _ = await rig.CallAsync(new TelemetryCall());
        IReadOnlyList<MeasurementPoint> points = rig.Flush();

        MeasurementPoint latency = Assert.Single(Of(points, Latency));
        Assert.Equal(2, latency.Count);
        Assert.Equal(4000.0, latency.Sum, 6);
        Assert.Equal(240.0, Only(points, Tokens, "aj.token.type", "input").Sum);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void Record_refuses_a_null_reading_and_the_factory_a_null_factory()
    {
        using TelemetryRig rig = TelemetryRig.Create();

        _ = Assert.Throws<ArgumentNullException>(() => rig.Instruments.Record(null!));
        _ = Assert.Throws<ArgumentNullException>(() => new AgentJoulesInstruments(null!));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void Metering_services_without_instruments_are_refused_by_the_metered_client()
    {
        using var inner = new FakeChatClient(new FakeTimeProvider());
        MeteringServices services = new(
            new UnmeteredEnergyMeter(TimeProvider.System),
            NoOpMeterGate.Instance,
            new InMemoryMetricsSink(),
            NotConfiguredCostCalculator.Instance,
            null!,
            TimeProvider.System);

        ArgumentNullException refused = Assert.Throws<ArgumentNullException>(() => new MeteredChatClient(inner, new StepIdentity("Planner", "m", Tier.Nvidia, "t", 1, false), services));
        Assert.Contains("Instruments", refused.ParamName ?? refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task A_recording_failure_after_a_failed_call_rides_on_the_exception_of_the_call()
    {
        using TelemetryRig rig = TelemetryRig.Create();
        using MeterListener listener = RefusingListener(rig.MeterFactory);

        TelemetryCallOutcome outcome = await rig.TryCallAsync(new TelemetryCall { Failure = new HttpRequestException("provider down") });

        // The call's own exception is the one that is thrown; the failure of the instruments is attached to it, and the reading reached the sink first.
        HttpRequestException thrown = Assert.IsType<HttpRequestException>(outcome.Thrown);
        InvalidOperationException attached = Assert.IsType<InvalidOperationException>(thrown.Data[MeteredChatClient.ReadingFailureKey]);
        Assert.Equal(RecordingBoom, attached.Message);
        Assert.NotNull(outcome.Reading);
        Assert.Equal(StepOutcomes.Error, outcome.Reading.Outcome);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task A_recording_failure_after_a_successful_call_surfaces()
    {
        using TelemetryRig rig = TelemetryRig.Create();
        using MeterListener listener = RefusingListener(rig.MeterFactory);

        TelemetryCallOutcome outcome = await rig.TryCallAsync(new TelemetryCall());

        // A benchmark must not return ok without its telemetry: the call fails, after the reading was published.
        InvalidOperationException thrown = Assert.IsType<InvalidOperationException>(outcome.Thrown);
        Assert.Equal(RecordingBoom, thrown.Message);
        Assert.NotNull(outcome.Reading);
        Assert.Equal(StepOutcomes.Ok, outcome.Reading.Outcome);
    }

    // A listener on the instruments of one factory whose measurement callbacks throw: Record is the only code that reaches them.
    private static MeterListener RefusingListener(IMeterFactory factory)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscription) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    subscription.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException(RecordingBoom));
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException(RecordingBoom));
        listener.Start();
        return listener;
    }

    // ----- the telemetry setup

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void Console_exporter_prints_the_instruments_and_never_the_spans()
    {
        string agent = UniqueAgent();
        string operation = "operation-" + agent;
        List<Activity> spans = [];
        using var capture = new ConsoleCapture();

        ServiceCollection services = Telemetry(console: true, endpoint: null);
        _ = services.AddOpenTelemetry().WithTracing(builder => builder.AddInMemoryExporter(spans));
        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            MeterProvider meters = provider.GetRequiredService<MeterProvider>();
            TracerProvider tracer = provider.GetRequiredService<TracerProvider>();
            new AgentJoulesInstruments(provider.GetRequiredService<IMeterFactory>()).Record(StepReadingFixtures.Metered(agent));
            using (var source = new ActivitySource("AgentJoules"))
            using (Activity? span = source.StartActivity(operation))
            {
                Assert.NotNull(span);
            }

            Assert.True(meters.ForceFlush(30_000));
            Assert.True(tracer.ForceFlush(30_000));
        }

        string printed = capture.Text;
        Assert.Contains("agentjoules.step.latency", printed, StringComparison.Ordinal);
        Assert.Contains(agent, printed, StringComparison.Ordinal);

        // The span reached the exporter that was added (so the tracer provider did record it), and the console printed none of it.
        Assert.Contains(spans, span => span.OperationName == operation);
        Assert.DoesNotContain("Activity.TraceId", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(operation, printed, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void Console_exporter_is_absent_when_disabled()
    {
        string agent = UniqueAgent();
        using var capture = new ConsoleCapture();

        using (ServiceProvider provider = Telemetry(console: false, endpoint: null).BuildServiceProvider())
        {
            MeterProvider meters = provider.GetRequiredService<MeterProvider>();
            new AgentJoulesInstruments(provider.GetRequiredService<IMeterFactory>()).Record(StepReadingFixtures.Metered(agent));
            Assert.True(meters.ForceFlush(30_000));
        }

        Assert.DoesNotContain(agent, capture.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("agentjoules.step", capture.Text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void The_console_exporter_adds_one_metrics_reader_and_no_trace_exporter()
    {
        ServiceCollection off = Telemetry(console: false, endpoint: null);
        ServiceCollection on = Telemetry(console: true, endpoint: null);

        Assert.Equal(Registrations(off, "IConfigureMeterProviderBuilder") + 1, Registrations(on, "IConfigureMeterProviderBuilder"));
        Assert.Equal(Registrations(off, "IConfigureTracerProviderBuilder"), Registrations(on, "IConfigureTracerProviderBuilder"));
        Assert.False(HasOtlpOptions(on));
    }

    [Theory]
    [Trait("Spec", "REQ-014")]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("http://127.0.0.1:4317", true)]
    [InlineData("http://localhost:4317", true)]
    [InlineData("http://[::1]:4317", true)]
    [InlineData("https://collector.example:4318/", true)]
    [InlineData("https://203.0.113.7:4317", true)]
    [InlineData("http://example.com:4317", true)]
    [InlineData("http://otel-collector:4317", true)]
    public void Otlp_exporter_is_added_only_for_an_unset_or_valid_endpoint(string? endpoint, bool expected)
    {
        ServiceCollection without = Telemetry(console: false, endpoint: null);
        ServiceCollection actual = Telemetry(console: false, endpoint);
        int added = expected ? 1 : 0;

        // One exporter for the metrics and one for the traces, and nothing else: the options of the OTLP exporter exist exactly when it was added.
        Assert.Equal(Registrations(without, "IConfigureMeterProviderBuilder") + added, Registrations(actual, "IConfigureMeterProviderBuilder"));
        Assert.Equal(Registrations(without, "IConfigureTracerProviderBuilder") + added, Registrations(actual, "IConfigureTracerProviderBuilder"));
        Assert.Equal(expected, HasOtlpOptions(actual));
        Assert.False(HasOtlpOptions(without));
    }

    [Theory]
    [Trait("Spec", "REQ-014")]
    [InlineData("collector")]
    [InlineData("/v1/traces")]
    [InlineData("file:///var/otlp")]
    [InlineData("ftp://collector:21")]
    [InlineData("localhost:4317")]
    [InlineData("http://u:p@127.0.0.1:4317")]
    [InlineData("https://u:p@collector.example:4318")]
    [InlineData("https://collector.example:4318/?token=abc")]
    [InlineData("https://collector.example:4318/#frag")]
    public void A_set_but_invalid_endpoint_fails_with_exit_code_2_and_never_echoes_the_value(string endpoint)
    {
        var services = new ServiceCollection();

        ConfigurationException thrown = Assert.Throws<ConfigurationException>(() => TelemetrySetup.Add(services, Config(endpoint), consoleEnabled: true));

        // The message names the variable and the rule, and not a character of the value, which can carry credentials; nothing was registered before the refusal.
        Assert.Contains(EndpointKey, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("https", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("u:p", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(services);
        Assert.Equal(ExitCode.InvalidArguments, thrown.ExitCode);
        Assert.Equal(2, ExitCodeMapper.Map(thrown, shutdownRequested: false));
    }

    [Theory]
    [Trait("Spec", "REQ-014")]
    [InlineData(MetricsEndpointKey)]
    [InlineData(TracesEndpointKey)]
    public void A_signal_specific_endpoint_is_checked_like_the_general_one(string key)
    {
        // The exporter honours these keys over the general one, so a value there must not bypass the shape check of the general key.
        IConfiguration configuration = ConfigurationFixtures.InMemory((EndpointKey, "https://collector.example:4317"), (key, "https://u:p@collector.example:4317"));
        var services = new ServiceCollection();

        ConfigurationException thrown = Assert.Throws<ConfigurationException>(() => TelemetrySetup.Add(services, configuration, consoleEnabled: false));

        Assert.Contains(key, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("u:p", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    [Theory]
    [Trait("Spec", "REQ-014")]
    [InlineData(EndpointKey, GeneralHeaders)]
    [InlineData(EndpointKey, MetricsHeaders)]
    [InlineData(EndpointKey, TracesHeaders)]
    [InlineData(MetricsEndpointKey, GeneralHeaders)]
    [InlineData(MetricsEndpointKey, MetricsHeaders)]
    [InlineData(TracesEndpointKey, GeneralHeaders)]
    [InlineData(TracesEndpointKey, TracesHeaders)]
    public void Cleartext_off_loopback_is_refused_when_headers_apply_and_the_message_names_both_keys(string endpointKey, string headersKey)
    {
        // The headers can carry a token, so they must not travel in the clear to a host that is not loopback. Nothing of either value is shown.
        IConfiguration configuration = ConfigurationFixtures.InMemory((endpointKey, "http://example.com:4317"), (headersKey, "x-test=sentinel"));
        var services = new ServiceCollection();

        ConfigurationException thrown = Assert.Throws<ConfigurationException>(() => TelemetrySetup.Add(services, configuration, consoleEnabled: false));

        Assert.Contains(endpointKey, thrown.Message, StringComparison.Ordinal);
        Assert.Contains(headersKey, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(services);
        Assert.Equal(2, ExitCodeMapper.Map(thrown, shutdownRequested: false));
    }

    [Theory]
    [Trait("Spec", "REQ-014")]
    [InlineData(EndpointKey, "http://127.0.0.1:4317", GeneralHeaders)]
    [InlineData(EndpointKey, "http://localhost:4317", GeneralHeaders)]
    [InlineData(EndpointKey, "https://example.com:4317", GeneralHeaders)]
    [InlineData(MetricsEndpointKey, "http://example.com:4317", TracesHeaders)]
    [InlineData(TracesEndpointKey, "http://example.com:4317", MetricsHeaders)]
    [InlineData(EndpointKey, "http://example.com:4317", "OTEL_EXPORTER_OTLP_HEADERS_UNRELATED")]
    public void Headers_that_do_not_travel_to_a_cleartext_remote_endpoint_are_allowed(string endpointKey, string endpoint, string headersKey)
    {
        IConfiguration configuration = ConfigurationFixtures.InMemory((endpointKey, endpoint), (headersKey, "x-test=sentinel"));
        var services = new ServiceCollection();

        _ = TelemetrySetup.Add(services, configuration, consoleEnabled: false);

        Assert.NotEmpty(services);
    }

    [Theory]
    [Trait("Spec", "REQ-014")]
    [InlineData(TracesEndpointKey, 0, 1)]
    [InlineData(MetricsEndpointKey, 1, 0)]
    public void A_signal_specific_endpoint_alone_adds_the_exporter_of_that_signal_only(string key, int metrics, int traces)
    {
        var without = new ServiceCollection();
        _ = TelemetrySetup.Add(without, Config(null), consoleEnabled: false);
        var alone = new ServiceCollection();
        _ = TelemetrySetup.Add(alone, ConfigurationFixtures.InMemory((key, "https://collector.example:4317")), consoleEnabled: false);

        Assert.Equal(Registrations(without, "IConfigureMeterProviderBuilder") + metrics, Registrations(alone, "IConfigureMeterProviderBuilder"));
        Assert.Equal(Registrations(without, "IConfigureTracerProviderBuilder") + traces, Registrations(alone, "IConfigureTracerProviderBuilder"));
        Assert.True(HasOtlpOptions(alone));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void Otlp_options_bind_from_the_isolated_configuration()
    {
        // One isolated configuration holds the endpoint, the protocol and the headers: the SDK must bind all three from it and never from the process environment.
        IConfiguration isolated = ConfigurationFixtures.InMemory(
            (EndpointKey, "http://127.0.0.1:4318"),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            ("OTEL_EXPORTER_OTLP_HEADERS", "x-test=sentinel"));
        var services = new ServiceCollection();
        _ = TelemetrySetup.Add(services, isolated, consoleEnabled: false);

        // Only the options object is resolved: resolving an exporter or a provider would start one that has an endpoint.
        using ServiceProvider provider = services.BuildServiceProvider();
        OtlpExporterOptions options = provider.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().CurrentValue;

        Assert.Equal(new Uri("http://127.0.0.1:4318"), options.Endpoint);
        Assert.Equal(OtlpExportProtocol.HttpProtobuf, options.Protocol);
        Assert.Equal("x-test=sentinel", options.Headers);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void The_given_configuration_is_registered_only_when_the_container_has_none()
    {
        IConfiguration given = Config(null);
        IConfiguration hosts = Config(null);

        var bare = new ServiceCollection();
        _ = TelemetrySetup.Add(bare, given, consoleEnabled: false);
        var host = new ServiceCollection();
        _ = host.AddSingleton(hosts);
        _ = TelemetrySetup.Add(host, given, consoleEnabled: false);

        using ServiceProvider bareProvider = bare.BuildServiceProvider();
        using ServiceProvider hostProvider = host.BuildServiceProvider();
        Assert.Same(given, bareProvider.GetRequiredService<IConfiguration>());
        Assert.Same(hosts, hostProvider.GetRequiredService<IConfiguration>());
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Only_step_instruments_leave_the_host_provider()
    {
        List<OtelMetric> items = [];
        var services = new ServiceCollection();
        _ = services.AddMetrics();
        _ = TelemetrySetup.Add(services, Config(null), consoleEnabled: false);
        _ = services.ConfigureOpenTelemetryMeterProvider(builder => builder.AddInMemoryExporter(items));

        // The agent wrapper creates a meter of the same name with the gen_ai.client.* instruments of the agent framework; the host must not export them.
        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            _ = provider.GetRequiredService<MeterProvider>();
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync().WaitAsync(HangGuard);
            AIAgent agent = rig.Factory.Create(PlannerIdentity(), PlannerOptions());
            _ = await rig.RunAsync(agent, "prompt").WaitAsync(HangGuard);
            Assert.NotEmpty(rig.Readings());
        }

        // The provider is disposed: its final collect has run, so the in-memory list holds what the host would have exported.
        string[] names = [.. items.Select(metric => metric.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.StartsWith("agentjoules.step.", name, StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.StartsWith("gen_ai.", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void An_instrument_of_ours_outside_the_step_family_is_not_dropped()
    {
        string name = "agentjoules.future." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        List<OtelMetric> items = [];
        var services = new ServiceCollection();
        _ = TelemetrySetup.Add(services, Config(null), consoleEnabled: false);
        _ = services.ConfigureOpenTelemetryMeterProvider(builder => builder.AddInMemoryExporter(items));

        // Only the instruments of the agent middleware are dropped: a later instrument of this program on the meter AgentJoules must not vanish silently.
        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            _ = provider.GetRequiredService<MeterProvider>();
            using var meter = new Meter(AgentJoulesInstruments.MeterName);
            Counter<long> counter = meter.CreateCounter<long>(name);
            counter.Add(1);
        }

        Assert.Contains(items, metric => metric.Name == name);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Workflow_spans_carry_no_message_content()
    {
        (string[] content, string everything) = await RunWorkflowForSpansAsync(sensitive: false);

        Assert.All(content, text => Assert.DoesNotContain(text, everything, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Workflow_spans_carry_message_content_only_when_sensitive_data_is_on()
    {
        // The control of the test above: with the internal switch of the factory on, the same run does put the output of the Writer into the spans,
        // so the absence checked there is not an artefact of the capture. The switch is internal and no configuration reaches it.
        (_, string everything) = await RunWorkflowForSpansAsync(sensitive: true);

        Assert.Contains(WorkflowRig.OutputOf("Writer"), everything, StringComparison.Ordinal);
    }

    // Runs the workflow of a rig once and returns the content that must stay out of the spans (the task prompt and the output of every agent) with every string the workflow spans of the run hold.
    private static async Task<(string[] Content, string Everything)> RunWorkflowForSpansAsync(bool sensitive)
    {
        await using WorkflowRig rig = await WorkflowRig.CreateAsync().WaitAsync(HangGuard);
        string[] content = [WorkflowRig.TaskPrompt, .. AgentCatalog.Names.Select(WorkflowRig.OutputOf)];

        // Spans of parallel tests share the source names, so the spans of this run are the ones in the trace of a root created here.
        using var rootSource = new ActivitySource("AgentJoules.Tests." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        List<Activity> stopped = [];
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("Microsoft.Agents.AI.Workflows", StringComparison.Ordinal) || source.Name == rootSource.Name,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                lock (stopped)
                {
                    stopped.Add(span);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        ActivityTraceId trace;
        using (Activity root = rootSource.StartActivity("root") ?? throw new InvalidOperationException("The root activity was not sampled."))
        {
            trace = root.TraceId;
            if (sensitive)
            {
                Task run = new AgentJoulesWorkflowFactory(rig.Agents, rig.Definition) { EnableSensitiveData = true }.RunAsync(CancellationToken.None);
                await FakeTimeDriver.RunWaitsToCompletionAsync(run, rig.Time).WaitAsync(HangGuard);
                await run.WaitAsync(HangGuard);
            }
            else
            {
                _ = await rig.RunAsync().WaitAsync(HangGuard);
            }
        }

        Activity[] spans;
        lock (stopped)
        {
            spans = [.. stopped.Where(span => span.TraceId == trace && span.Source.Name.StartsWith("Microsoft.Agents.AI.Workflows", StringComparison.Ordinal))];
        }

        // The run did produce workflow spans, so a result about their content is not an artefact of a disabled source.
        Assert.NotEmpty(spans);
        return (content, Everything(spans));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void Otlp_and_console_are_independent()
    {
        ServiceCollection neither = Telemetry(console: false, endpoint: null);
        ServiceCollection both = Telemetry(console: true, endpoint: "http://127.0.0.1:4317");

        Assert.Equal(Registrations(neither, "IConfigureMeterProviderBuilder") + 2, Registrations(both, "IConfigureMeterProviderBuilder"));
        Assert.Equal(Registrations(neither, "IConfigureTracerProviderBuilder") + 1, Registrations(both, "IConfigureTracerProviderBuilder"));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void Traces_listen_to_the_agentjoules_source_and_the_workflow_sources_only()
    {
        string tag = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string[] recorded = ["AgentJoules", "Microsoft.Agents.AI.Workflows", "Microsoft.Agents.AI.Workflows.Executors." + tag];
        string unrelated = "Other.Source." + tag;
        List<Activity> spans = [];

        ServiceCollection services = Telemetry(console: false, endpoint: null);
        _ = services.AddOpenTelemetry().WithTracing(builder => builder.AddInMemoryExporter(spans));
        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            TracerProvider tracer = provider.GetRequiredService<TracerProvider>();
            foreach (string name in recorded.Append(unrelated))
            {
                using var source = new ActivitySource(name);
                using Activity? span = source.StartActivity("span-" + name);
                Assert.Equal(name != unrelated, span is not null);
            }

            Assert.True(tracer.ForceFlush(30_000));
        }

        Assert.All(recorded, name => Assert.Contains(spans, span => span.OperationName == "span-" + name));
        Assert.DoesNotContain(spans, span => span.OperationName == "span-" + unrelated);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void The_meter_provider_listens_to_agentjoules_and_flushes_when_the_provider_is_disposed()
    {
        string agent = UniqueAgent();
        List<OtelMetric> exported = [];

        ServiceCollection services = Telemetry(console: false, endpoint: null);
        _ = services.AddOpenTelemetry().WithMetrics(builder => builder.AddInMemoryExporter(exported));
        ServiceProvider provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<MeterProvider>();
        new AgentJoulesInstruments(provider.GetRequiredService<IMeterFactory>()).Record(StepReadingFixtures.Metered(agent));

        // The host disposes its service provider on stop: that final collect is the flush.
        provider.Dispose();

        IReadOnlyList<MeasurementPoint> points = TelemetryRig.Read([.. exported], agent);
        Assert.Equal(FiveInstruments.Where(name => name != Cost).Order(StringComparer.Ordinal), points.Select(point => point.Instrument).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void Telemetry_setup_refuses_null_arguments()
    {
        var services = new ServiceCollection();

        _ = Assert.Throws<ArgumentNullException>(() => TelemetrySetup.Add(null!, Config(null), true));
        _ = Assert.Throws<ArgumentNullException>(() => TelemetrySetup.Add(services, null!, true));
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void No_console_telemetry_is_a_boolean_flag_that_defaults_to_off()
    {
        Option<bool> option = TelemetryOptions.CreateNoConsoleTelemetry();
        var run = new Command("run") { option };

        Assert.Equal("--no-console-telemetry", option.Name);
        Assert.False(run.Parse([]).GetValue(option));
        ParseResult parsed = run.Parse(["--no-console-telemetry"]);
        Assert.Empty(parsed.Errors);
        Assert.True(parsed.GetValue(option));

        // Every command gets an option of its own, because the same parse tree cannot own one option twice under different parents.
        Assert.NotSame(option, TelemetryOptions.CreateNoConsoleTelemetry());
    }

    // ----- the agents

    private static StepIdentity PlannerIdentity() => new("Planner", "model-planner", Tier.Nvidia, "incident-brief", 1, IsJudge: false);

    private static ChatOptions PlannerOptions() => ChatOptionsFactory.Create(AgentFactoryRig.FourModelTier(), "Planner", 512, seed: 42);

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Agent_is_the_OpenTelemetry_wrapper_of_the_chat_agent_over_the_metered_pipeline()
    {
        await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();

        AIAgent agent = rig.Factory.Create(PlannerIdentity(), PlannerOptions());

        OpenTelemetryAgent wrapper = Assert.IsType<OpenTelemetryAgent>(agent);
        ChatClientAgent chat = Assert.IsType<ChatClientAgent>(agent.GetService<ChatClientAgent>());
        Assert.Equal("Planner", wrapper.Name);
        Assert.Equal("Planner", chat.Name);
        Assert.IsType<MeteredChatClient>(chat.ChatClient);
        Assert.NotNull(agent.GetService<ChatOptions>());
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Agent_spans_use_the_AgentJoules_source_and_carry_no_prompt_or_answer_text()
    {
        string marker = "MARK-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        (List<Activity> spans, OpenTelemetryAgent wrapper) = await RunPlannerAsync(marker, sensitive: null);

        Assert.False(wrapper.EnableSensitiveData);
        Assert.Contains(spans, span => span.Source.Name == "AgentJoules");
        Assert.DoesNotContain(marker, Everything(spans), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public async Task Control_enabling_sensitive_data_would_put_the_prompt_text_in_the_spans()
    {
        string marker = "MARK-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        // The control of the test above: with the flag on, the same call does put the marker into the spans, so the absence checked there is not an artefact of the capture.
        (List<Activity> spans, OpenTelemetryAgent wrapper) = await RunPlannerAsync(marker, sensitive: true);

        Assert.True(wrapper.EnableSensitiveData);
        Assert.Contains(marker, Everything(spans), StringComparison.Ordinal);
    }

    private static async Task<(List<Activity> Spans, OpenTelemetryAgent Wrapper)> RunPlannerAsync(string marker, bool? sensitive)
    {
        await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync(time => new FakeProviderClientFactory(time, (_, _) => new FakeChatClient(time) { ResponseText = "answer " + marker }));
        AIAgent agent = rig.Factory.Create(PlannerIdentity(), PlannerOptions());
        OpenTelemetryAgent wrapper = Assert.IsType<OpenTelemetryAgent>(agent.GetService<OpenTelemetryAgent>());
        if (sensitive is { } value)
        {
            wrapper.EnableSensitiveData = value;
        }

        // Spans of parallel tests share the source name, so the spans of this call are the ones in the trace of a root created here.
        using var rootSource = new ActivitySource("AgentJoules.Tests." + marker);
        List<Activity> stopped = [];
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "AgentJoules" || source.Name == rootSource.Name,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                lock (stopped)
                {
                    stopped.Add(span);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        ActivityTraceId trace;
        using (Activity root = rootSource.StartActivity("root") ?? throw new InvalidOperationException("The root activity was not sampled."))
        {
            trace = root.TraceId;
            _ = await rig.RunAsync(agent, "prompt " + marker);
        }

        lock (stopped)
        {
            return ([.. stopped.Where(span => span.TraceId == trace)], wrapper);
        }
    }

    // Every string a span holds: name, tags, baggage and the tags of its events.
    private static string Everything(IEnumerable<Activity> spans)
    {
        var text = new StringBuilder();
        foreach (Activity span in spans)
        {
            _ = text.Append(span.DisplayName).Append('\n');
            foreach (KeyValuePair<string, object?> tag in span.TagObjects)
            {
                _ = text.Append(tag.Key).Append('=').Append(Convert.ToString(tag.Value, CultureInfo.InvariantCulture)).Append('\n');
            }

            foreach (ActivityEvent activityEvent in span.Events)
            {
                _ = text.Append(activityEvent.Name).Append('\n');
                foreach (KeyValuePair<string, object?> tag in activityEvent.Tags)
                {
                    _ = text.Append(tag.Key).Append('=').Append(Convert.ToString(tag.Value, CultureInfo.InvariantCulture)).Append('\n');
                }
            }
        }

        return text.ToString();
    }

    [Fact]
    [Trait("Spec", "REQ-014")]
    public void UseOpenTelemetry_is_called_in_the_agent_factory_of_the_source_tree()
    {
        string root = ConfigurationFixtures.RepositoryRoot();
        string[] hits = [.. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("UseOpenTelemetry", StringComparison.Ordinal))];

        string factory = Assert.Single(hits, path => Path.GetFileName(path) == "AgentFactory.cs");
        Assert.Matches(new Regex(@"UseOpenTelemetry\(\s*sourceName:\s*""AgentJoules""", RegexOptions.None, TimeSpan.FromSeconds(5)), File.ReadAllText(factory));
    }
}