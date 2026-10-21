using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Energy.Testing;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-047 scenario (the <c>shared-window</c> flag and the union-window totals of a concurrent run), handed to the step class by Reqnroll
/// context injection. It owns the fake clock, the deterministic meter at a constant 60 W, the sink, the service provider that holds the gate of the mode,
/// and the steps and judge readings the run is assembled from; nothing in it waits on the wall clock.
/// </summary>
public sealed class ConcurrentUnionContext : IDisposable
{
    private DeterministicEnergyMeter? _meter;
    private ServiceProvider? _provider;

    /// <summary>The scenario clock; the steps advance it.</summary>
    public FakeTimeProvider Time { get; } = new();

    /// <summary>The sink that receives the readings of the metered calls of the scenario.</summary>
    public InMemoryMetricsSink Sink { get; } = new();

    /// <summary>The tier the run is assembled for; local unless a step names another.</summary>
    public Tier Tier { get; set; } = Tier.Local;

    /// <summary>The mode the run is assembled for.</summary>
    public RunMode Mode { get; set; } = RunMode.Concurrent;

    /// <summary>The workflow steps the run is assembled from.</summary>
    public IReadOnlyList<StepReading> Steps { get; set; } = [];

    /// <summary>The judge readings the run is assembled with; never part of the step totals.</summary>
    public IReadOnlyList<StepReading> Judge { get; set; } = [];

    /// <summary>The assembled document, or null before the When step.</summary>
    public RunDocument? Document { get; private set; }

    /// <summary>The deterministic meter of the scenario.</summary>
    /// <exception cref="InvalidOperationException">The scenario has no meter.</exception>
    public DeterministicEnergyMeter Meter => _meter ?? throw new InvalidOperationException("The scenario has no meter.");

    /// <summary>The union function the assembler is given: the meter's, or null when the scenario has no meter.</summary>
    public Func<IReadOnlyList<WindowSpan>, UnionEnergy>? Union => _meter is null ? null : _meter.ComputeUnion;

    /// <summary>Registers the gate of <paramref name="mode"/> and starts a deterministic meter at a constant 60 W on the scenario clock.</summary>
    /// <param name="mode">The run mode of the scenario.</param>
    /// <returns>A task that completes when the meter is started.</returns>
    /// <exception cref="InvalidOperationException">The mode was already set.</exception>
    public async Task UseModeAsync(RunMode mode)
    {
        if (_provider is not null)
        {
            throw new InvalidOperationException("The mode of this scenario was already set.");
        }

        Mode = mode;
        _provider = new ServiceCollection().AddAgentJoulesMeterGate(mode).BuildServiceProvider();
        _meter = new DeterministicEnergyMeter(Time, static _ => 60.0, sampleRateHz: 10.0, counterSupported: true, MeterMethodSetting.Auto);
        await _meter.StartAsync(CancellationToken.None);
    }

    /// <summary>Creates a client for an agent over a fake client, with the gate of the mode and the meter of the scenario.</summary>
    /// <param name="inner">The fake client the calls go to.</param>
    /// <param name="agent">The agent name the readings carry.</param>
    /// <returns>The metered client.</returns>
    /// <exception cref="InvalidOperationException">The mode of the scenario was not set.</exception>
    public MeteredChatClient ClientOver(IChatClient inner, string agent)
    {
        ServiceProvider provider = _provider ?? throw new InvalidOperationException("The mode of this scenario was not set.");
        return new MeteredChatClient(
            inner,
            new StepIdentity(agent, "model-a", Tier, "summarise", 1, IsJudge: false),
            new MeteringServices(Meter, provider.GetRequiredService<IMeterGate>(), Sink, NotConfiguredCostCalculator.Instance, TestInstruments.Create(), Time));
    }

    /// <summary>Assembles the run document of the scenario from its steps and judge readings, with the meter's union function when there is a meter.</summary>
    /// <returns>The document.</returns>
    public RunDocument Assemble()
    {
        RunInfo run = RunAssembler.BuildRunInfo("20261020T120000Z-" + TierOptions.NameOf(Tier), Tier, ["summarise"], Mode, 1, StepReadingFixtures.StartUtc, RunStatus.Running);
        EnvironmentInfo environment = RunAssembler.BuildEnvironment(Tier, new TierOptions { ModelId = "model-a", ReasoningMode = "on" }, gpu: null, runtime: null, modelLoad: null);
        Document = RunAssembler.Assemble(run, environment, probe: null, baseline: null, Steps, Judge, wallClockMs: 2000.0, Union);
        return Document;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _meter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _provider?.Dispose();
    }
}