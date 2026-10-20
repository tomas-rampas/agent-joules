using AgentJoules.Core.Cli;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Agents;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-049: one metered agent per name, built from a tier and a model. Scoped to the feature because sentences such as "the sink holds 4 readings" are reused by
/// other stories with other meanings. The state lives in <see cref="AgentFactoryContext"/>; the provider clients are fakes (or the real SDK against a fake
/// endpoint for the retry scenario) and the clock moves only when the code under test waits.
/// </summary>
[Binding]
[Scope(Feature = "AJ-049 Create one metered agent per name from a tier and model")]
public sealed class AgentFactorySteps(AgentFactoryContext context)
{
    private const int Seed = 42;

    private readonly AgentFactoryContext _context = context;

    [Given(@"^the agent factory(?: with fake provider clients)?$")]
    public async Task GivenTheAgentFactory() => await _context.UseFakeProvidersAsync();

    [Given(@"^the four agents created over fake chat clients$")]
    public async Task GivenTheFourAgentsOverFakeClients()
    {
        await _context.UseFakeProvidersAsync();
        _ = _context.CreateAll(repeat: 1);
    }

    [Given(@"^the task ""([^""]*)"" with maxOutputTokens (\d+)$")]
    public async Task GivenTheTask(string name, int maxOutputTokens)
    {
        await _context.UseFakeProvidersAsync();
        _context.Task = new TaskDefinition(name, "Summarise the incident for the on-call engineer.", maxOutputTokens);
    }

    [Given(@"^agents created for repeat 1 whose Writer recorded 2 retries$")]
    public async Task GivenAgentsForRepeatOneWhoseWriterRetried()
    {
        await _context.UseRateLimitedEndpointAsync();
        _context.FirstRepeat.AddRange(_context.CreateAll(repeat: 1));

        AIAgent writer = _context.FirstRepeat.Single(agent => agent.Name == AgentCatalog.Writer);
        _ = await _context.RequireRig().RunAsync(writer);

        Assert.Equal(2, _context.RequireRig().Readings().Single().Retries);
    }

    [When(@"^agents ""([^""]*)"", ""([^""]*)"", ""([^""]*)"" and ""([^""]*)"" are created for one task and repeat$")]
    public void WhenAgentsAreCreated(string first, string second, string third, string fourth)
    {
        AgentFactoryRig rig = _context.RequireRig();
        foreach (string name in new[] { first, second, third, fourth })
        {
            _context.Agents.Add(rig.Factory.Create(IdentityOf(name), OptionsOf(name)));
        }
    }

    [When(@"^each agent answers one prompt$")]
    public async Task WhenEachAgentAnswers()
    {
        AgentFactoryRig rig = _context.RequireRig();
        foreach (AIAgent agent in _context.Agents)
        {
            int before = rig.Readings().Count;
            _ = await rig.RunAsync(agent);

            IReadOnlyList<StepReading> after = rig.Readings();
            Assert.Equal(before + 1, after.Count);
            _context.Produced.Add((agent.Name!, after[^1]));
        }
    }

    [When(@"^an agent is created$")]
    public void WhenAnAgentIsCreated() =>
        _context.Agents.Add(_context.RequireRig().Factory.CreateAll(Tier.Nvidia, _context.TierOptions, _context.Task, repeat: 1, Seed)[0]);

    [When(@"^an agent named ""([^""]*)"" is requested from the catalog$")]
    public void WhenAnAgentIsRequestedFromTheCatalog(string name) =>
        _context.Thrown = Record.Exception(() => _context.RequireRig().Factory.Create(IdentityOf(name), new ChatOptions()));

    [When(@"^agents are created for repeat 2$")]
    public void WhenAgentsAreCreatedForRepeatTwo() => _ = _context.CreateAll(repeat: 2);

    [Then(@"^there are 4 distinct metered client instances$")]
    public void ThenThereAreFourDistinctMeteredClients()
    {
        MeteredChatClient[] clients = [.. _context.Agents.Select(AgentFactoryContext.MeteredOf)];

        Assert.Equal(4, clients.Length);
        Assert.Equal(4, clients.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Then(@"^the sink holds 4 readings and each reading agent name equals the agent that produced it$")]
    public void ThenEachReadingNamesItsAgent()
    {
        IReadOnlyList<StepReading> readings = _context.RequireRig().Readings();

        Assert.Equal(4, readings.Count);
        Assert.Equal(4, _context.Produced.Count);
        Assert.All(_context.Produced, produced => Assert.Equal(produced.Agent, produced.Reading.Agent));
        Assert.Equal(AgentCatalog.Names, readings.Select(reading => reading.Agent));
    }

    [Then(@"^its chat options have temperature 0 and max output tokens 512$")]
    public void ThenItsChatOptionsAreFixed()
    {
        // ChatClientAgent.ChatOptions is internal; the agent answers its default options through GetService.
        ChatOptions options = Assert.IsType<ChatOptions>(Assert.Single(_context.Agents).GetService<ChatOptions>());

        Assert.Equal(0f, options.Temperature);
        Assert.Equal(512, options.MaxOutputTokens);
    }

    [Then(@"^creation fails with a configuration error$")]
    public void ThenCreationFailsWithAConfigurationError()
    {
        ConfigurationException failure = Assert.IsType<ConfigurationException>(_context.Thrown);

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Empty(_context.Agents);
        Assert.Empty(_context.RequireRig().Fakes.Created);
    }

    [Then(@"^the new Writer client starts with 0 retries$")]
    public async Task ThenTheNewWriterStartsWithZeroRetries()
    {
        MeteredChatClient previous = AgentFactoryContext.MeteredOf(_context.FirstRepeat.Single(agent => agent.Name == AgentCatalog.Writer));
        AIAgent writer = _context.Agents.Single(agent => agent.Name == AgentCatalog.Writer);
        MeteredChatClient current = AgentFactoryContext.MeteredOf(writer);
        RetryLedger previousLedger = Assert.IsType<RetryLedger>(previous.GetService(typeof(RetryLedger)));
        RetryLedger currentLedger = Assert.IsType<RetryLedger>(current.GetService(typeof(RetryLedger)));

        Assert.NotSame(previous, current);
        Assert.NotSame(previousLedger, currentLedger);
        Assert.Equal(2, previousLedger.Retries);
        Assert.Equal(0, currentLedger.Retries);

        _ = await _context.RequireRig().RunAsync(writer);

        Assert.Equal(0, _context.RequireRig().Readings()[^1].Retries);
    }

    private StepIdentity IdentityOf(string name) =>
        new(name, _context.TierOptions.ResolveModelId(name), Tier.Nvidia, _context.Task.Name, Repeat: 1, IsJudge: false);

    private ChatOptions OptionsOf(string name) =>
        ChatOptionsFactory.Create(_context.TierOptions, name, _context.Task.MaxOutputTokens, Seed);
}