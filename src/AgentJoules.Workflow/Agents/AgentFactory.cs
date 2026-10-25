using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.Workflow.Providers;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentJoules.Workflow.Agents;

/// <summary>
/// Creates the agents of one tier run, one metered pipeline per agent (REQ-011, design 3.3 and 3.4). The pipeline of an agent is, from the outside in,
/// <c>MeteredChatClient -> RateLimitRetryChatClient -> provider client</c>, and the <see cref="ChatClientAgent"/> that wraps it takes it as it is
/// (<see cref="ChatClientAgentOptions.UseProvidedChatClientAsIs"/>), so no decorator of the agent framework sits between the agent and the meter and every
/// call the agent makes is one metered call.
/// </summary>
/// <remarks>
/// <para>
/// Attribution is by construction: the <see cref="StepIdentity"/> is copied into the <see cref="MeteredChatClient"/> of the agent, and the agent name is the
/// <see cref="StepIdentity.Agent"/>, so a reading cannot name another agent than the one whose client produced it. Every <c>Create</c> builds a new
/// pipeline with a new <see cref="RetryLedger"/>, shared by the metered client and the retry layer of that pipeline and by nobody else: agents of other
/// steps, other repeats and other tiers never share a ledger, a client or a provider client.
/// </para>
/// <para>
/// A factory belongs to one tier run. The <see cref="MeteringServices"/> it is built from (meter, gate, sink, cost calculator, clock) are the run's: the
/// sink differs between the workflow and the judge, and the meter is the one that reads the GPU of the tier, so the orchestrator builds one factory per
/// run (see <see cref="ServiceCollectionExtensions.AddAgentJoulesAgentFactory(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>). The factory holds no
/// mutable state (the one lock guards the draw of a jitter seed) and is safe to use from several threads.
/// </para>
/// <para>
/// Jitter: <see cref="Random.Shared"/> is thread-safe, so a factory given it hands it to every pipeline. Any other <see cref="Random"/> is not thread-safe, and
/// each retry layer locks only its own gate, so pipelines that ran concurrently (Researcher and Writer do) would race on a shared instance. The factory
/// therefore uses the <see cref="Random"/> it was given only to draw one seed per pipeline, under a lock, and gives each pipeline its own <c>new Random(seed)</c>:
/// a seeded factory stays deterministic (the n-th pipeline always gets the n-th seed) and no instance is shared between pipelines.
/// </para>
/// <para>
/// The agents have no tools by design, and each is for one agent of the catalog, not a judge: <see cref="Create(StepIdentity, ChatOptions, string?)"/> refuses options that carry
/// tools, a tool mode or a model other than the identity's, and a judge identity.
/// </para>
/// <para>
/// The instruction of an agent travels in <see cref="ChatOptions.Instructions"/>: <see cref="ChatClientAgentOptions"/> of Microsoft.Agents.AI 1.23.0 has no
/// <c>Instructions</c> member (the agent reads its instructions from the chat options it is given). The agent merges them into every request it sends, so the
/// provider client gets them as the system message. The agent that <c>Create</c> returns is the <see cref="OpenTelemetryAgent"/> around the <see cref="ChatClientAgent"/> (<c>UseOpenTelemetry</c>, source <c>AgentJoules</c>, REQ-014): ask it for the inner agent with <c>GetService&lt;ChatClientAgent&gt;()</c>. Sensitive data is off, so no prompt or answer text reaches a span.
/// </para>
/// </remarks>
/// <param name="providerClients">Makes the provider chat client at the bottom of each pipeline.</param>
/// <param name="services">The metering services of the run: meter, gate, sink, cost calculator and clock.</param>
/// <param name="retryOptions">The 429 retry policy of every pipeline.</param>
/// <param name="time">The clock the retry waits are taken on; the clock of <paramref name="services"/> in a run, a fake clock in a test.</param>
/// <param name="random">The source of the retry jitter: <see cref="Random.Shared"/> in a run, which every pipeline then uses directly, or a seeded instance in a test, from which the factory draws one seed per pipeline (see the remarks).</param>
/// <exception cref="ArgumentNullException">An argument is null.</exception>
public sealed class AgentFactory(IProviderClientFactory providerClients, MeteringServices services, RetryOptions retryOptions, TimeProvider time, Random random)
{
    private readonly IProviderClientFactory _providerClients = providerClients ?? throw new ArgumentNullException(nameof(providerClients));
    private readonly MeteringServices _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly RetryOptions _retryOptions = retryOptions ?? throw new ArgumentNullException(nameof(retryOptions));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly Random _random = random ?? throw new ArgumentNullException(nameof(random));
    private readonly Lock _seedGate = new();

    /// <summary>
    /// True (the default) puts the <see cref="MeteredChatClient"/> at the top of every pipeline. False builds the pipeline of a workload that nobody meters (the calibration of AJ-109, whose one window is opened by
    /// the service itself): the retry layer wraps the provider client and nothing opens a window, takes the gate or publishes a reading, so the services of the factory are never used.
    /// </summary>
    public bool Metered { get; init; } = true;

    /// <summary>Creates the agent <see cref="StepIdentity.Agent"/> with the catalog instruction and no tier prefix.</summary>
    /// <param name="identity">Who the agent is and where it runs; the agent name must be in the <see cref="AgentCatalog"/>.</param>
    /// <param name="chatOptions">The default options of the agent's calls (temperature, output cap, model); copied, the caller keeps its instance.</param>
    /// <returns>An <see cref="OpenTelemetryAgent"/> named <see cref="StepIdentity.Agent"/> around a <see cref="ChatClientAgent"/> over a new metered pipeline.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">See <see cref="Create(StepIdentity, ChatOptions, string?)"/>: a judge identity, options with tools or a tool mode, or a model that is not the identity's.</exception>
    /// <exception cref="ConfigurationException">The agent name is not in the catalog (exit code 2), or the provider client cannot be created.</exception>
    public AIAgent Create(StepIdentity identity, ChatOptions chatOptions) => Create(identity, chatOptions, systemPromptPrefix: null);

    /// <summary>Creates the agent <see cref="StepIdentity.Agent"/> with the catalog instruction preceded by <paramref name="systemPromptPrefix"/>.</summary>
    /// <param name="identity">Who the agent is and where it runs; the agent name must be in the <see cref="AgentCatalog"/>.</param>
    /// <param name="chatOptions">The default options of the agent's calls; copied, the caller keeps its instance. Its <see cref="ChatOptions.Instructions"/> is replaced by the instruction of the catalog; its <see cref="ChatOptions.ModelId"/> is set to the model of <paramref name="identity"/> when null.</param>
    /// <param name="systemPromptPrefix"><c>Tiers:&lt;tier&gt;:SystemPromptPrefix</c>, or null (see <see cref="AgentCatalog.InstructionsFor"/> for the exact join).</param>
    /// <returns>An <see cref="OpenTelemetryAgent"/> named <see cref="StepIdentity.Agent"/> around a <see cref="ChatClientAgent"/> over a new metered pipeline.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> or <paramref name="chatOptions"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="identity"/> is a judge identity (the judge is not an agent of the catalog and builds its own client); <paramref name="chatOptions"/> carries tools (<see cref="ChatOptions.Tools"/> not empty) or a <see cref="ChatOptions.ToolMode"/> (the agents have no tools by design, and a tool definition would reach the provider request); or its <see cref="ChatOptions.ModelId"/> is not the model of <paramref name="identity"/> (the reading's model and the request's model could differ). Nothing was created.</exception>
    /// <exception cref="ConfigurationException">The agent name is not in the catalog (exit code 2; nothing was created), or the provider client cannot be created.</exception>
    public AIAgent Create(StepIdentity identity, ChatOptions chatOptions, string? systemPromptPrefix)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(chatOptions);

        if (identity.IsJudge)
        {
            throw new ArgumentException("A judge is not an agent of the catalog; the judge service builds its own client.", nameof(identity));
        }

        // The name is checked before anything is created, so a refused name leaves no provider client behind.
        string instructions = AgentCatalog.InstructionsFor(identity.Agent, systemPromptPrefix);
        ChatOptions options = chatOptions.Clone();
        if (options.Tools is { Count: > 0 } || options.ToolMode is not null)
        {
            throw new ArgumentException("The options of an agent must not carry tools or a tool mode; the agents of the workflow have none.", nameof(chatOptions));
        }

        options.ModelId ??= identity.ModelId;
        if (!string.Equals(options.ModelId, identity.ModelId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The model of the options is not the model of the identity; the reading and the request must name the same model.", nameof(chatOptions));
        }

        options.Instructions = instructions;

        // One ledger per pipeline, shared by the metered client and the retry layer. The first Use is the outermost layer: the builder applies the factories
        // in reverse order, so the retry layer wraps the provider client and the metered client wraps the retry layer.
        RetryLedger ledger = new();
        Random jitter = ReferenceEquals(_random, Random.Shared) ? Random.Shared : new Random(NextSeed());
        IChatClient provider = _providerClients.Create(identity.Tier, identity.ModelId);
        IChatClient pipeline;
        try
        {
            ChatClientBuilder builder = new(provider);
            if (Metered)
            {
                _ = builder.Use(inner => new MeteredChatClient(inner, identity, _services, ledger));
            }

            pipeline = builder
                .Use(inner => new RateLimitRetryChatClient(inner, ledger, _retryOptions, _time, jitter))
                .Build();
        }
        catch
        {
            provider.Dispose();
            throw;
        }

        var chatAgent = new ChatClientAgent(pipeline, new ChatClientAgentOptions
        {
            Name = identity.Agent,
            ChatOptions = options,
            UseProvidedChatClientAsIs = true,
        });

        // The wrapper creates the spans of the agent on the activity source AgentJoules (REQ-014). Sensitive data is switched off explicitly: its default is off, but the default
        // also follows the environment variable OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT, and an explicit value wins over it, so no prompt or answer text can reach a span.
        return chatAgent.AsBuilder()
            .UseOpenTelemetry(sourceName: "AgentJoules", configure: static telemetry => telemetry.EnableSensitiveData = false)
            .Build();
    }

    /// <summary>
    /// Creates the four agents of the catalog for one (tier, task, repeat), in catalog order, each with its own pipeline. This is the call that builds the
    /// workflow of a repeat: the identity of each agent is its catalog name, the model <see cref="TierOptions.ResolveModelId(string)"/> picks for it, the tier, the task name and
    /// the repeat; the options are those of <see cref="ChatOptionsFactory"/> (temperature 0, the task's output cap, the per-agent model, the seed only for a tier that honours it);
    /// the instruction is the catalog's, preceded by <see cref="TierOptions.SystemPromptPrefix"/>.
    /// </summary>
    /// <param name="tier">The tier the agents run on.</param>
    /// <param name="tierOptions">The options of that tier.</param>
    /// <param name="task">The task of the run; its output cap is the cap of every call.</param>
    /// <param name="repeat">The repeat number, from 1.</param>
    /// <param name="seed">The request seed (<c>Workflow:Seed</c>), sent only to a tier whose <see cref="TierOptions.SupportsSeed"/> is true.</param>
    /// <returns>The agents Planner, Researcher, Writer and Reviewer; a call with another repeat returns four other agents with four other pipelines.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tierOptions"/> or <paramref name="task"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="repeat"/> is below 1.</exception>
    /// <exception cref="ConfigurationException">An agent has no model ID, or its provider client cannot be created.</exception>
    public IReadOnlyList<AIAgent> CreateAll(Tier tier, TierOptions tierOptions, TaskDefinition task, int repeat, int seed)
    {
        ArgumentNullException.ThrowIfNull(tierOptions);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentOutOfRangeException.ThrowIfLessThan(repeat, 1);

        List<AIAgent> agents = new(AgentCatalog.All.Count);
        foreach (AgentDefinition definition in AgentCatalog.All)
        {
            ChatOptions options = ChatOptionsFactory.Create(tierOptions, definition.Name, task.MaxOutputTokens, seed);
            var identity = new StepIdentity(definition.Name, tierOptions.ResolveModelId(definition.Name), tier, task.Name, repeat, IsJudge: false);
            agents.Add(Create(identity, options, tierOptions.SystemPromptPrefix));
        }

        return agents.AsReadOnly();
    }

    // The only use of a non-shared Random of the factory: one draw per pipeline, under a lock, because the instance is not thread-safe.
    private int NextSeed()
    {
        lock (_seedGate)
        {
            return _random.Next();
        }
    }
}