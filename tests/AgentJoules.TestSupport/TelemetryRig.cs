using System.Diagnostics.Metrics;
using System.Globalization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Energy.Testing;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AgentJoules.TestSupport;

/// <summary>
/// An in-memory OpenTelemetry metrics pipeline over the real <see cref="AgentJoulesInstruments"/> (REQ-014, design 6.1): the meter factory is the one of a service container
/// (<c>AddMetrics()</c>), the meter provider listens to the meter <c>AgentJoules</c> and exports to a list, and <see cref="CallAsync"/> runs one metered fake call whose reading
/// reaches the instruments through <see cref="MeteredChatClient"/>. A meter provider sees every meter of that name in the process, including the ones other tests create in
/// parallel, so each rig names its agent uniquely and <see cref="Flush"/> returns only the points of that agent.
/// </summary>
public sealed class TelemetryRig : IDisposable
{
    /// <summary>The name of the meter, and so the first part of the name of every instrument.</summary>
    public const string MeterName = "AgentJoules";

    private readonly ServiceProvider _services;
    private readonly MeterProvider _provider;
    private readonly List<Metric> _exported = [];

    private TelemetryRig(string agent)
    {
        Agent = agent;
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        // An isolated configuration: the SDK would otherwise build one from the process environment (OTEL_SDK_DISABLED, OTEL_RESOURCE_ATTRIBUTES and the like) and change what the rig sees.
        _provider = Sdk.CreateMeterProviderBuilder()
            .ConfigureServices(static services => services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()))
            .AddMeter(MeterName)
            .AddInMemoryExporter(_exported)
            .Build();
        MeterFactory = _services.GetRequiredService<IMeterFactory>();
        Instruments = new AgentJoulesInstruments(MeterFactory);
    }

    /// <summary>The agent name every call of this rig carries: unique per rig, so the points of parallel tests do not mix.</summary>
    public string Agent { get; }

    /// <summary>The model every call of this rig carries.</summary>
    public string Model => "model-" + Agent;

    /// <summary>The instruments under test.</summary>
    public AgentJoulesInstruments Instruments { get; }

    /// <summary>The meter factory of the container the instruments were created from; the scope of their meter.</summary>
    public IMeterFactory MeterFactory { get; }

    /// <summary>Creates a rig with a new agent name.</summary>
    /// <returns>The rig; the caller disposes it.</returns>
    public static TelemetryRig Create() => new("Agent-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    /// <summary>Runs one buffered call of the agent of this rig through a <see cref="MeteredChatClient"/> built over the instruments of this rig.</summary>
    /// <param name="call">What the call looks like; a call with a scripted <see cref="TelemetryCall.Failure"/> ends in that failure, which this method swallows.</param>
    /// <returns>The reading the call published to its sink.</returns>
    /// <exception cref="InvalidOperationException">The call did not publish exactly one reading, or a call without a scripted failure threw.</exception>
    public async Task<StepReading> CallAsync(TelemetryCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        TelemetryCallOutcome outcome = await TryCallAsync(call).ConfigureAwait(false);
        return outcome.Thrown is not null && call.Failure is null
            ? throw new InvalidOperationException("The call threw although no failure was scripted.", outcome.Thrown)
            : outcome.Reading ?? throw new InvalidOperationException("The call did not publish a reading.");
    }

    /// <summary>Runs one buffered call like <see cref="CallAsync"/> and reports what it threw instead of swallowing or wrapping it.</summary>
    /// <param name="call">What the call looks like.</param>
    /// <returns>The exception the call threw, or null, and the reading the sink received, or null when it received none.</returns>
    /// <exception cref="InvalidOperationException">The sink received more than one reading.</exception>
    public async Task<TelemetryCallOutcome> TryCallAsync(TelemetryCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        var time = new FakeTimeProvider();
        bool hosted = call.Tier is Tier.Nvidia or Tier.Nebius;
        IEnergyMeter meter = hosted
            ? new UnmeteredEnergyMeter(time)
            : new DeterministicEnergyMeter(time, _ => call.Watts, sampleRateHz: 10.0, counterSupported: true, MeterMethodSetting.Auto, baselineMeanWatts: call.BaselineWatts);
        await using (meter.ConfigureAwait(false))
        {
            await meter.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var sink = new InMemoryMetricsSink();
            var services = new MeteringServices(meter, NoOpMeterGate.Instance, sink, call.Cost, Instruments, time);
            var identity = new StepIdentity(Agent, Model, call.Tier, "summarise", 1, IsJudge: false);
            using var inner = new FakeChatClient(time) { ResponseDelay = call.Duration, ResponseUsage = call.Usage, Failure = call.Failure };
            using var client = new MeteredChatClient(inner, identity, services);

            Exception? thrown = null;
            try
            {
                _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Summarise the report.")]).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                thrown = failure;
            }

            IReadOnlyList<StepReading> readings = sink.Snapshot();
            return readings.Count > 1
                ? throw new InvalidOperationException("The call published more than one reading.")
                : new TelemetryCallOutcome(readings.Count == 1 ? readings[0] : null, thrown);
        }
    }

    /// <summary>Flushes the meter provider and returns the points of this rig's agent.</summary>
    /// <returns>A copy of the exported points of the instruments named <c>agentjoules.step.*</c> that carry this rig's agent name.</returns>
    /// <exception cref="TimeoutException">The flush did not finish in 30 seconds.</exception>
    public IReadOnlyList<MeasurementPoint> Flush()
    {
        return _provider.ForceFlush(30_000)
            ? Read([.. _exported], Agent)
            : throw new TimeoutException("The meter provider did not flush within 30 seconds.");
    }

    /// <summary>Copies the points of the instruments named <c>agentjoules.step.*</c> that carry the agent name <paramref name="agent"/> out of exported metrics.</summary>
    /// <param name="metrics">The metrics an in-memory exporter holds.</param>
    /// <param name="agent">The value of the <c>gen_ai.agent.name</c> attribute to keep.</param>
    /// <returns>The points, a copy that stays valid after the exporter is disposed.</returns>
    /// <exception cref="InvalidOperationException">A metric is not a histogram or a sum.</exception>
    public static IReadOnlyList<MeasurementPoint> Read(IEnumerable<Metric> metrics, string agent)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(agent);

        List<MeasurementPoint> points = [];
        foreach (Metric metric in metrics.Distinct())
        {
            if (!metric.Name.StartsWith("agentjoules.step.", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
            {
                var tags = new Dictionary<string, object?>();
                foreach (KeyValuePair<string, object?> tag in point.Tags)
                {
                    tags[tag.Key] = tag.Value;
                }

                if (!tags.TryGetValue("gen_ai.agent.name", out object? tagged) || !string.Equals(tagged as string, agent, StringComparison.Ordinal))
                {
                    continue;
                }

                (double sum, long count) = metric.MetricType switch
                {
                    MetricType.Histogram => (point.GetHistogramSum(), point.GetHistogramCount()),
                    MetricType.LongSum => ((double)point.GetSumLong(), 0L),
                    MetricType.DoubleSum => (point.GetSumDouble(), 0L),
                    _ => throw new InvalidOperationException($"The metric type {metric.MetricType} of {metric.Name} is not one of the kinds the instruments create."),
                };
                points.Add(new MeasurementPoint(metric.Name, metric.Unit, metric.MetricType, tags, sum, count));
            }
        }

        return points;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _provider.Dispose();
        _services.Dispose();
    }
}