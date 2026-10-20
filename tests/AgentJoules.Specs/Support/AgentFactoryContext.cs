using AgentJoules.Core.Configuration;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.TestSupport;

using Microsoft.Agents.AI;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-049 scenario (the agent factory), handed to the step class by Reqnroll context injection. It owns the rig (the factory over a fake
/// clock, an unmetered meter and one sink), the optional fake endpoint behind the real provider clients, and what the steps created and observed. Nothing in it
/// waits on the wall clock and no request leaves the process.
/// </summary>
public sealed class AgentFactoryContext : IDisposable
{
    private ProviderClientHost? _host;

    /// <summary>The rig, built by a Given step.</summary>
    public AgentFactoryRig? Rig { get; private set; }

    /// <summary>The task of the scenario; the default is <c>incident-brief</c> with a cap of 512.</summary>
    public TaskDefinition Task { get; set; } = AgentFactoryRig.IncidentBrief();

    /// <summary>The options of the tier the agents are created for.</summary>
    public TierOptions TierOptions { get; } = AgentFactoryRig.FourModelTier();

    /// <summary>The agents the last creation step made, in the order they were made.</summary>
    public List<AIAgent> Agents { get; } = [];

    /// <summary>The agents of the first repeat, kept by the scenario that creates a second repeat.</summary>
    public List<AIAgent> FirstRepeat { get; } = [];

    /// <summary>The agent that produced each reading, in the order the readings were published.</summary>
    public List<(string Agent, StepReading Reading)> Produced { get; } = [];

    /// <summary>The exception a creation step ended with, or null.</summary>
    public Exception? Thrown { get; set; }

    /// <summary>The rig of the scenario.</summary>
    /// <exception cref="InvalidOperationException">No Given step built it.</exception>
    public AgentFactoryRig RequireRig() => Rig ?? throw new InvalidOperationException("No Given step built the agent factory.");

    /// <summary>Builds the rig over fake provider clients.</summary>
    /// <returns>A task that completes when the rig exists.</returns>
    public async Task UseFakeProvidersAsync() => Rig = await AgentFactoryRig.CreateAsync();

    /// <summary>Builds the rig over the real provider clients and an endpoint that answers 429, 429 and then 200 to the chat route.</summary>
    /// <returns>A task that completes when the rig exists.</returns>
    public async Task UseRateLimitedEndpointAsync() =>
        Rig = await AgentFactoryRig.CreateAsync(time =>
        {
            var endpoint = new FakeHttpMessageHandler { Clock = time, DefaultDuration = TimeSpan.FromMilliseconds(10) };
            endpoint.SetStatuses(FakeRoute.ChatCompletions, 429, 429, 200);
            _host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers(), handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = endpoint });
            return _host.Factory;
        });

    /// <summary>Creates the four agents of the catalog for repeat <paramref name="repeat"/> on the nvidia tier.</summary>
    /// <param name="repeat">The repeat number, from 1.</param>
    /// <returns>The agents in catalog order; also stored in <see cref="Agents"/>.</returns>
    public IReadOnlyList<AIAgent> CreateAll(int repeat)
    {
        Agents.Clear();
        Agents.AddRange(RequireRig().Factory.CreateAll(Tier.Nvidia, TierOptions, Task, repeat, seed: 42));
        return Agents;
    }

    /// <summary>The metered client at the top of the pipeline of <paramref name="agent"/>.</summary>
    /// <param name="agent">An agent made by the factory.</param>
    /// <returns>The client.</returns>
    /// <exception cref="InvalidOperationException">The agent is not a <see cref="ChatClientAgent"/> whose client is a <see cref="MeteredChatClient"/>.</exception>
    public static MeteredChatClient MeteredOf(AIAgent agent) =>
        (agent as ChatClientAgent)?.ChatClient as MeteredChatClient
        ?? throw new InvalidOperationException("The agent is not a ChatClientAgent over a MeteredChatClient.");

    /// <inheritdoc />
    public void Dispose()
    {
        Rig?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _host?.Dispose();
    }
}