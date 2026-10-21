using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Graph;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.TestSupport;

/// <summary>
/// The four agents of the catalog over four scripted chat clients on one fake clock, one in-memory sink and one meter gate, plus the workflow factory that runs
/// them (REQ-011, design 6.1). By default every client streams two updates: the text <see cref="OutputOf"/> after 100 ms of fake time and an empty update that
/// carries the usage. The clock moves only when a fake client yields or the code under test waits, so no run waits on the wall clock, and no request leaves the
/// process: a client is a <see cref="FakeChatClient"/> or, for the failing-endpoint rig, the real SDK client over a <see cref="FakeHttpMessageHandler"/>.
/// </summary>
public sealed class WorkflowRig : IAsyncDisposable
{
    /// <summary>The name of the task of the rig.</summary>
    public const string TaskName = "energy-explainer";

    /// <summary>The prompt of the task of the rig.</summary>
    public const string TaskPrompt = "Explain to a developer how the energy of a GPU workload is measured.";

    private readonly AgentFactoryRig _rig;
    private readonly SerializedMeterGate? _gate;
    private readonly ProviderClientHost? _host;

    private WorkflowRig(AgentFactoryRig rig, SerializedMeterGate? gate, ProviderClientHost? host, TaskDefinition definition, IReadOnlyList<AIAgent> agents)
    {
        _rig = rig;
        _gate = gate;
        _host = host;
        Definition = definition;
        Agents = agents;
    }

    /// <summary>The agents Planner, Researcher, Writer and Reviewer in catalog order, each over its own metered pipeline.</summary>
    public IReadOnlyList<AIAgent> Agents { get; }

    /// <summary>The task the workflow runs.</summary>
    public TaskDefinition Definition { get; }

    /// <summary>The fake clock of the rig.</summary>
    public TimerTrackingTimeProvider Time => _rig.Time;

    /// <summary>The sink every agent publishes its readings to.</summary>
    public InMemoryMetricsSink Sink => _rig.Sink;

    /// <summary>The meter gate of the rig: a <see cref="SerializedMeterGate"/> in serialized mode, otherwise <see cref="NoOpMeterGate.Instance"/>.</summary>
    public IMeterGate Gate => _rig.Services.Gate;

    /// <summary>The fake endpoint behind the real provider client of the failing-endpoint rig.</summary>
    /// <exception cref="InvalidOperationException">The rig has no real provider client.</exception>
    public FakeHttpMessageHandler Endpoint =>
        _host?.Handler(Tier.Nvidia) ?? throw new InvalidOperationException("The rig has no real provider client.");

    /// <summary>The text the default client of <paramref name="agent"/> streams.</summary>
    /// <param name="agent">An agent name of the catalog.</param>
    /// <returns>The lowercase agent name followed by <c>-output</c>.</returns>
    public static string OutputOf(string agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.ToLowerInvariant() + "-output";
    }

    /// <summary>The model ID the rig gives <paramref name="agent"/>, <c>model-planner</c> and so on.</summary>
    /// <param name="agent">An agent name of the catalog.</param>
    /// <returns>The model ID.</returns>
    public static string ModelOf(string agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return "model-" + agent.ToLowerInvariant();
    }

    /// <summary>A streaming client that yields <see cref="OutputOf"/> after <paramref name="firstUpdateDelay"/> and then an empty update with usage.</summary>
    /// <param name="time">The clock the client moves.</param>
    /// <param name="agent">The agent the client serves; its output text is streamed.</param>
    /// <param name="firstUpdateDelay">The fake time before the text, 100 ms when null.</param>
    /// <returns>A new client.</returns>
    public static FakeChatClient StreamingClient(FakeTimeProvider time, string agent, TimeSpan? firstUpdateDelay = null) =>
        new(time)
        {
            Updates =
            [
                new FakeUpdate(OutputOf(agent), firstUpdateDelay ?? TimeSpan.FromMilliseconds(100)),
                new FakeUpdate(string.Empty, TimeSpan.Zero, new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 }),
            ],
        };

    /// <summary>Builds a rig over scripted clients.</summary>
    /// <param name="client">Makes the client of an agent from its name and the clock of the rig, or null for <see cref="StreamingClient"/> for every agent.</param>
    /// <param name="serialized">True for serialized mode (one <see cref="SerializedMeterGate"/>), false for concurrent mode.</param>
    /// <param name="taskName">The name of the task.</param>
    /// <returns>The rig; the caller disposes it.</returns>
    public static Task<WorkflowRig> CreateAsync(Func<string, TimerTrackingTimeProvider, IChatClient>? client = null, bool serialized = false, string taskName = TaskName) =>
        BuildAsync(client, serialized, taskName, endpointAgent: null, status: 0);

    /// <summary>Builds a rig whose client for <paramref name="agent"/> is the real provider client over a fake endpoint that answers <paramref name="status"/> to every request; the other agents use the default fake clients.</summary>
    /// <param name="agent">The agent whose endpoint fails.</param>
    /// <param name="status">The HTTP status the endpoint answers, 500 by default.</param>
    /// <param name="serialized">True for serialized mode.</param>
    /// <param name="taskName">The name of the task.</param>
    /// <returns>The rig; the caller disposes it.</returns>
    public static Task<WorkflowRig> CreateWithFailingEndpointAsync(string agent, int status = 500, bool serialized = false, string taskName = TaskName)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return BuildAsync(client: null, serialized, taskName, agent, status);
    }

    /// <summary>Runs the workflow once, moving the fake clock through every wait of the run.</summary>
    /// <param name="cancellationToken">The run token.</param>
    /// <returns>The final output.</returns>
    /// <exception cref="TimeoutException">The run neither finished nor waited for 30 seconds of real time.</exception>
    public async Task<FinalOutput> RunAsync(CancellationToken cancellationToken = default)
    {
        var factory = new AgentJoulesWorkflowFactory(Agents, Definition);
        Task<FinalOutput> run = factory.RunAsync(cancellationToken);
        await FakeTimeDriver.RunWaitsToCompletionAsync(run, _rig.Time).ConfigureAwait(false);
        return await run.ConfigureAwait(false);
    }

    /// <summary>The client at the bottom of the pipeline of <paramref name="agent"/>.</summary>
    /// <param name="agent">An agent name of the catalog.</param>
    /// <returns>The client the provider factory handed out for the model of the agent.</returns>
    public IChatClient ClientOf(string agent) =>
        _rig.Fakes.Created.Single(created => created.ModelId == ModelOf(agent)).Client;

    /// <summary>The fake client of <paramref name="agent"/>.</summary>
    /// <param name="agent">An agent name of the catalog.</param>
    /// <returns>The client as a <see cref="FakeChatClient"/>.</returns>
    /// <exception cref="InvalidCastException">The client of the agent is not a fake.</exception>
    public FakeChatClient FakeOf(string agent) => (FakeChatClient)ClientOf(agent);

    /// <summary>The readings of the sink, in the order they were published.</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<StepReading> Readings() => _rig.Readings();

    /// <summary>The readings of <paramref name="agent"/>, in the order they were published.</summary>
    /// <param name="agent">An agent name of the catalog.</param>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<StepReading> ReadingsOf(string agent) =>
        [.. _rig.Readings().Where(reading => reading.Agent == agent)];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _rig.DisposeAsync().ConfigureAwait(false);
        _host?.Dispose();
        _gate?.Dispose();
    }

    private static async Task<WorkflowRig> BuildAsync(Func<string, TimerTrackingTimeProvider, IChatClient>? client, bool serialized, string taskName, string? endpointAgent, int status)
    {
        ProviderClientHost? host = null;
        SerializedMeterGate? gate = serialized ? new SerializedMeterGate() : null;
        AgentFactoryRig rig = await AgentFactoryRig.CreateAsync(
            time => new FakeProviderClientFactory(time, (tier, modelId) =>
            {
                string agent = AgentCatalog.Names.Single(name => ModelOf(name) == modelId);
                if (endpointAgent is not null && agent == endpointAgent)
                {
                    if (host is null)
                    {
                        var endpoint = new FakeHttpMessageHandler { Clock = time, DefaultDuration = TimeSpan.FromMilliseconds(10) };
                        endpoint.AlwaysStatus(status);
                        host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers(), handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = endpoint });
                    }

                    return host.Factory.Create(tier, modelId);
                }

                return client is null ? StreamingClient(time, agent) : client(agent, time);
            }),
            retry: null,
            gate);

        var definition = new TaskDefinition(taskName, TaskPrompt, 384);
        IReadOnlyList<AIAgent> agents = rig.Factory.CreateAll(Tier.Nvidia, AgentFactoryRig.FourModelTier(), definition, repeat: 1, seed: 42);
        return new WorkflowRig(rig, gate, host, definition, agents);
    }
}