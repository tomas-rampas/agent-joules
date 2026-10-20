using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Energy.Testing;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-046 scenario (the process-wide meter gate chosen by the run mode), handed to the step class by Reqnroll context injection. It
/// owns the fake clock, the deterministic meter at a constant 60 W, the sink and the service provider that holds the gate of the scenario; the fake chat
/// client moves the fake clock itself, so nothing in it waits on the wall clock.
/// </summary>
public sealed class SerializedGateContext : IDisposable
{
    private DeterministicEnergyMeter? _meter;

    /// <summary>The scenario clock; the fake chat client advances it.</summary>
    public FakeTimeProvider Time { get; } = new();

    /// <summary>The sink that receives the readings of the scenario.</summary>
    public InMemoryMetricsSink Sink { get; } = new();

    /// <summary>The provider built from a service collection with the gate registered for the mode of the scenario, or null before the mode is set.</summary>
    public ServiceProvider? Provider { get; private set; }

    /// <summary>The gate resolved from the service collection, or null before a step resolved it.</summary>
    public IMeterGate? Gate { get; set; }

    /// <summary>Completes the hold of the first call, or null when the scenario holds no call.</summary>
    public TaskCompletionSource? HoldFirst { get; set; }

    /// <summary>The first call of the scenario.</summary>
    public Task<ChatResponse>? FirstCall { get; set; }

    /// <summary>The second call of the scenario.</summary>
    public Task<ChatResponse>? SecondCall { get; set; }

    /// <summary>The fake inner client of the first call.</summary>
    public FakeChatClient? FirstInner { get; set; }

    /// <summary>The fake inner client of the second call.</summary>
    public FakeChatClient? SecondInner { get; set; }

    /// <summary>The cancellation source of the first call, or null when the scenario does not cancel.</summary>
    public CancellationTokenSource? Cancellation { get; set; }

    /// <summary>Builds the provider of the scenario with the gate of the mode, starts the meter and resolves the gate.</summary>
    /// <param name="mode">The run mode of the scenario.</param>
    /// <returns>A task that completes when the scenario is ready.</returns>
    /// <exception cref="InvalidOperationException">The mode was already set; a scenario has one mode.</exception>
    public async Task UseModeAsync(RunMode mode)
    {
        if (Provider is not null)
        {
            throw new InvalidOperationException("The mode of this scenario was already set.");
        }

        Provider = new ServiceCollection().AddAgentJoulesMeterGate(mode).BuildServiceProvider();
        Gate = Provider.GetRequiredService<IMeterGate>();
        _meter = new DeterministicEnergyMeter(Time, static _ => 60.0, sampleRateHz: 10.0, counterSupported: true, MeterMethodSetting.Auto);
        await _meter.StartAsync(CancellationToken.None);
    }

    /// <summary>Creates a client for an agent over a fake client, sharing the gate of the scenario.</summary>
    /// <param name="inner">The fake client the calls go to.</param>
    /// <param name="agent">The agent name the readings carry.</param>
    /// <param name="isJudge">True for a judge client.</param>
    /// <returns>The metered client.</returns>
    /// <exception cref="InvalidOperationException">The mode of the scenario was not set.</exception>
    public MeteredChatClient ClientOver(IChatClient inner, string agent, bool isJudge = false)
    {
        DeterministicEnergyMeter meter = _meter ?? throw new InvalidOperationException("The mode of this scenario was not set.");
        IMeterGate gate = Gate ?? throw new InvalidOperationException("No gate was resolved.");
        return new MeteredChatClient(
            inner,
            new StepIdentity(agent, "model-a", Tier.Local, "summarise", 1, isJudge),
            new MeteringServices(meter, gate, Sink, NotConfiguredCostCalculator.Instance, Time));
    }

    /// <summary>The one reading of an agent.</summary>
    /// <param name="agent">The agent name.</param>
    /// <returns>The reading.</returns>
    /// <exception cref="InvalidOperationException">The sink does not hold exactly one reading of the agent.</exception>
    public StepReading ReadingOf(string agent) => Sink.Snapshot().Single(reading => reading.Agent == agent);

    /// <inheritdoc />
    public void Dispose()
    {
        HoldFirst?.TrySetResult();
        Cancellation?.Dispose();
        _meter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Provider?.Dispose();
    }
}