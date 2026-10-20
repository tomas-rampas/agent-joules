using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Providers;

using Microsoft.Agents.AI;

namespace AgentJoules.TestSupport;

/// <summary>
/// An <see cref="AgentFactory"/> over a fake clock, an unmetered meter, a no-op gate (or the gate the caller gives it) and one in-memory sink (REQ-011, design 6.1), plus the fixtures the
/// agent factory tests share: a tier whose four agents have four model IDs and a task. The clock moves only when the code under test waits
/// (<see cref="RunAsync"/>), so the retry waits of a pipeline cost no wall-clock time; the jitter is a seeded <see cref="Random"/>.
/// </summary>
public sealed class AgentFactoryRig : IAsyncDisposable
{
    /// <summary>The seed of the jitter <see cref="Random"/>.</summary>
    public const int RandomSeed = 4711;

    private readonly UnmeteredEnergyMeter _meter;

    private AgentFactoryRig(TimerTrackingTimeProvider time, UnmeteredEnergyMeter meter, InMemoryMetricsSink sink, MeteringServices services, IProviderClientFactory providers, AgentFactory factory)
    {
        Time = time;
        _meter = meter;
        Sink = sink;
        Services = services;
        Providers = providers;
        Factory = factory;
    }

    /// <summary>The fake clock of the rig.</summary>
    public TimerTrackingTimeProvider Time { get; }

    /// <summary>The sink every agent of the rig publishes its readings to.</summary>
    public InMemoryMetricsSink Sink { get; }

    /// <summary>The metering services every agent of the rig shares: the meter, the no-op gate, the sink, the cost calculator and the clock.</summary>
    public MeteringServices Services { get; }

    /// <summary>The provider clients the factory was built over.</summary>
    public IProviderClientFactory Providers { get; }

    /// <summary>The factory under test.</summary>
    public AgentFactory Factory { get; }

    /// <summary>The provider factory as a <see cref="FakeProviderClientFactory"/>.</summary>
    /// <exception cref="InvalidOperationException">The rig was built over another provider factory.</exception>
    public FakeProviderClientFactory Fakes =>
        Providers as FakeProviderClientFactory ?? throw new InvalidOperationException("The rig was not built over a FakeProviderClientFactory.");

    /// <summary>The options of a tier whose agents have four model IDs, <c>model-planner</c> and so on, and the default model <c>model-default</c>.</summary>
    /// <returns>New options; the caller may change them.</returns>
    public static TierOptions FourModelTier()
    {
        var options = new TierOptions { ModelId = "model-default" };
        foreach (string name in AgentCatalog.Names)
        {
            options.AgentModelIds[name] = "model-" + name.ToLowerInvariant();
        }

        return options;
    }

    /// <summary>The task of the BDD scenarios: <c>incident-brief</c> with an output cap of 512.</summary>
    /// <returns>A new task.</returns>
    public static TaskDefinition IncidentBrief() => new("incident-brief", "Summarise the incident for the on-call engineer.", 512);

    /// <summary>Builds a rig over fake provider clients.</summary>
    /// <returns>The rig; the caller disposes it.</returns>
    public static Task<AgentFactoryRig> CreateAsync() => CreateAsync(static time => new FakeProviderClientFactory(time));

    /// <summary>Builds a rig over the provider clients <paramref name="providers"/> makes from the rig clock.</summary>
    /// <param name="providers">Builds the provider factory from the clock of the rig, which the fake endpoints of the provider clients share.</param>
    /// <param name="retry">The retry policy, or null for the default.</param>
    /// <param name="gate">The meter gate every agent of the rig shares, or null for <see cref="NoOpMeterGate.Instance"/> (concurrent mode). The caller owns a gate it passes and disposes it after the rig.</param>
    /// <returns>The rig; the caller disposes it.</returns>
    public static async Task<AgentFactoryRig> CreateAsync(Func<TimerTrackingTimeProvider, IProviderClientFactory> providers, RetryOptions? retry = null, IMeterGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var time = new TimerTrackingTimeProvider();
        var meter = new UnmeteredEnergyMeter(time);
        await meter.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var sink = new InMemoryMetricsSink();
        var services = new MeteringServices(meter, gate ?? NoOpMeterGate.Instance, sink, NotConfiguredCostCalculator.Instance, time);
        IProviderClientFactory clients = providers(time);
        var factory = new AgentFactory(clients, services, retry ?? RetryOptions.Default, time, new Random(RandomSeed));

        return new AgentFactoryRig(time, meter, sink, services, clients, factory);
    }

    /// <summary>Runs <paramref name="agent"/> on one prompt, moving the fake clock through every wait of the call.</summary>
    /// <param name="agent">The agent to run.</param>
    /// <param name="prompt">The user message.</param>
    /// <returns>The response of the agent.</returns>
    /// <exception cref="TimeoutException">The call neither finished nor waited for 30 seconds of real time.</exception>
    public async Task<AgentResponse> RunAsync(AIAgent agent, string prompt = "Summarise the report.")
    {
        ArgumentNullException.ThrowIfNull(agent);

        Task<AgentResponse> run = agent.RunAsync(prompt);
        await FakeTimeDriver.RunWaitsToCompletionAsync(run, Time).ConfigureAwait(false);
        return await run.ConfigureAwait(false);
    }

    /// <summary>The readings of the sink, in the order they were published.</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<StepReading> Readings() => Sink.Snapshot();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _meter.DisposeAsync();
}