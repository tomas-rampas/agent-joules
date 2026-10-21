using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Energy.Testing;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-042 scenario (usage reporting), handed to the step class by Reqnroll context injection. It owns the fake clock, the
/// deterministic meter, the sink, the cost calculator M2 ships and the task limit; the fake chat client moves the fake clock itself, so
/// nothing in it waits on the wall clock.
/// </summary>
public sealed class UsageReportingContext : IDisposable
{
    private DeterministicEnergyMeter? _meter;

    /// <summary>The scenario clock; the fake chat client advances it.</summary>
    public FakeTimeProvider Time { get; } = new();

    /// <summary>The sink that receives the readings of the scenario.</summary>
    public InMemoryMetricsSink Sink { get; } = new();

    /// <summary>The fake inner client of the scenario.</summary>
    public FakeChatClient? Inner { get; set; }

    /// <summary>The output limit of the task the scenario meters, or null when the task has none; it reaches the decorator as the options of the call.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>The identity of the agent a scenario meters, on the local tier.</summary>
    public static StepIdentity Identity { get; } = new("Planner", "model-a", Tier.Local, "summarise", 1, IsJudge: false);

    /// <summary>The options of a call: the task's output limit when it has one, otherwise none.</summary>
    /// <returns>The options, or null.</returns>
    public ChatOptions? Options() => MaxOutputTokens is { } limit ? new ChatOptions { MaxOutputTokens = limit } : null;

    /// <summary>Builds the client under test over the fake client, with the deterministic meter, a gate that admits everyone, the scenario sink and the M2 cost calculator.</summary>
    /// <returns>The metered client.</returns>
    /// <exception cref="InvalidOperationException">No fake client was set, or the client was already built; a scenario has one meter.</exception>
    public async Task<MeteredChatClient> CreateClientAsync()
    {
        FakeChatClient inner = Inner ?? throw new InvalidOperationException("No fake client was set.");
        if (_meter is not null)
        {
            throw new InvalidOperationException("The client of this scenario was already built.");
        }

        _meter = new DeterministicEnergyMeter(Time, static _ => 100.0, sampleRateHz: 10.0, counterSupported: true, MeterMethodSetting.Auto);
        await _meter.StartAsync(CancellationToken.None);
        return new MeteredChatClient(inner, Identity, new MeteringServices(_meter, NoOpMeterGate.Instance, Sink, NotConfiguredCostCalculator.Instance, TestInstruments.Create(), Time));
    }

    /// <summary>The one reading the sink holds.</summary>
    /// <returns>The reading.</returns>
    /// <exception cref="InvalidOperationException">The sink does not hold exactly one reading.</exception>
    public StepReading SingleReading()
    {
        IReadOnlyList<StepReading> readings = Sink.Snapshot();
        return readings.Count == 1
            ? readings[0]
            : throw new InvalidOperationException($"The sink holds {readings.Count} readings, not exactly 1.");
    }

    /// <inheritdoc />
    public void Dispose() => _meter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
}