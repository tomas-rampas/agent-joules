using System.ClientModel;
using System.Text.Json.Nodes;

using AgentJoules.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.AI;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-044: provider chat clients per tier from configuration only. Scoped to the feature because sentences such as "the exit code is 2" and "when any
/// command starts" are reused by other stories with other meanings. The state lives in <see cref="ProviderClientsContext"/>. Every request runs the
/// real OpenAI SDK and the real named <see cref="HttpClient"/> pipeline against a <see cref="FakeHttpMessageHandler"/>; the scenario that starts a
/// command runs the real CLI in process over an edited copy of the committed file, with no process environment variable set.
/// </summary>
[Binding]
[Scope(Feature = "AJ-044 Create provider chat clients per tier from configuration only")]
public sealed class ProviderClientsSteps(ProviderClientsContext context)
{
    // A step that waits on a request must fail, not hang the run, when the code under test leaks.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly ChatMessage[] Messages = [new(ChatRole.User, "Summarise the report.")];

    private readonly ProviderClientsContext _context = context;

    private JsonObject ChatBody() =>
        Assert.Single(_context.Fake.RequestsFor(FakeRoute.ChatCompletions)).Json ?? throw new InvalidOperationException("The chat request has no JSON body.");

    private async Task SendChatAsync(string agent)
    {
        TierOptions tier = _context.TierOptionsUnderTest;
        using IChatClient client = _context.Host.Factory.Create(_context.TierValue, tier.ResolveModelId(agent));
        ChatOptions options = ChatOptionsFactory.Create(tier, agent, _context.MaxOutputTokens, _context.Options.Workflow.Seed);

        _ = await client.GetResponseAsync(Messages, options).WaitAsync(HangGuard);
    }

    [Given(@"^tier ""([^""]*)"" configured with a fake base URL and task maxOutputTokens (\d+)$")]
    public void GivenATierWithAFakeBaseUrlAndALimit(string tier, int maxOutputTokens)
    {
        _context.TierName = tier;
        _context.MaxOutputTokens = maxOutputTokens;
    }

    [Given(@"^tier ""([^""]*)"" with ModelId ""([^""]*)"" and AgentModelIds Writer ""([^""]*)""$")]
    public void GivenATierWithAnAgentModelOverride(string tier, string modelId, string writerModelId)
    {
        _context.TierName = tier;
        _context.TierOptionsUnderTest.ModelId = modelId;
        _context.TierOptionsUnderTest.AgentModelIds["Writer"] = writerModelId;
    }

    [Given(@"^tier ""([^""]*)"" with ExtraBodyJson that sets one extra field$")]
    public void GivenATierWithAnExtraField(string tier)
    {
        _context.TierName = tier;
        _context.TierOptionsUnderTest.ExtraBodyJson = new JsonObject { [ProviderClientsContext.ExtraFieldName] = ProviderClientsContext.ExtraFieldValue }.ToJsonString();
    }

    [Given(@"^a fake endpoint that always answers 500$")]
    public void GivenAnEndpointThatAlwaysAnswers500()
    {
        _context.Endpoint = new FakeHttpMessageHandler();
        _context.Endpoint.AlwaysStatus(500);
    }

    [Given(@"^tier ""([^""]*)"" with SupportsSeed false$")]
    public void GivenATierThatDoesNotSupportTheSeed(string tier)
    {
        _context.TierName = tier;
        _context.TierOptionsUnderTest.SupportsSeed = false;
    }

    [Given(@"^tier ""([^""]*)"" with ExtraBodyJson ""([^""]*)""$")]
    public void GivenATierWithExtraBodyJson(string tier, string json)
    {
        _context.TierName = tier;
        _context.ExtraBodyJson = json;
    }

    [Given(@"^tier ""([^""]*)"" with MaxTokensField ""([^""]*)"" and task maxOutputTokens (\d+)$")]
    public void GivenATierWithAMaxTokensField(string tier, string field, int maxOutputTokens)
    {
        _context.TierName = tier;
        _context.TierOptionsUnderTest.MaxTokensField = field;
        _context.MaxOutputTokens = maxOutputTokens;
    }

    [When(@"^the chat client sends one request$")]
    public Task WhenTheChatClientSendsOneRequest() => SendChatAsync("Planner");

    [When(@"^one chat request is sent$")]
    public async Task WhenOneChatRequestIsSent()
    {
        try
        {
            await SendChatAsync("Planner");
        }
        catch (ClientResultException failure)
        {
            _context.Thrown = failure;
        }
    }

    [When(@"^clients for ""([^""]*)"" and ""([^""]*)"" are created$")]
    public async Task WhenClientsForTwoAgentsAreCreated(string first, string second)
    {
        foreach (string agent in new[] { first, second })
        {
            await SendChatAsync(agent);
            _context.ModelByAgent[agent] = _context.Fake.RequestsFor(FakeRoute.ChatCompletions)[^1].Json!["model"]!.GetValue<string>();
        }
    }

    [When(@"^a completion request and a models request are sent$")]
    public async Task WhenACompletionAndAModelsRequestAreSent()
    {
        await SendChatAsync("Planner");

        using HttpClient http = _context.Host.HttpClients.CreateClient(ProviderClientFactory.HttpClientName(_context.TierName));
        using HttpResponseMessage models = await http.GetAsync(new Uri(ProviderClientFixtures.BaseUrl(_context.TierValue) + "/models")).WaitAsync(HangGuard);
        Assert.True(models.IsSuccessStatusCode);
    }

    [When(@"^any command starts$")]
    public async Task WhenAnyCommandStarts()
    {
        _context.WriteEditedCopy();
        CliHostOverrides overrides = CliHarness.Overrides([]) with { WorkingDirectory = _context.Workspace!.Root };

        _context.Run = await CliHarness.RunAsync("probe", overrides);
    }

    [Then(@"^the recorded body has temperature 0 and the limit (\d+)$")]
    public void ThenTheBodyHasTemperatureZeroAndTheLimit(int limit)
    {
        JsonObject body = ChatBody();

        Assert.Equal(0.0, body["temperature"]!.GetValue<double>());
        Assert.Equal(limit, body["max_completion_tokens"]!.GetValue<int>());
    }

    [Then(@"^""([^""]*)"" uses ""([^""]*)"" and ""([^""]*)"" uses ""([^""]*)""$")]
    public void ThenEachAgentUsesItsModel(string first, string firstModel, string second, string secondModel)
    {
        Assert.Equal(firstModel, _context.ModelByAgent[first]);
        Assert.Equal(secondModel, _context.ModelByAgent[second]);
    }

    [Then(@"^the completion body contains the extra field$")]
    public void ThenTheCompletionBodyContainsTheExtraField()
    {
        JsonObject body = ChatBody();

        Assert.Equal(ProviderClientsContext.ExtraFieldValue, body[ProviderClientsContext.ExtraFieldName]!.GetValue<string>());
        Assert.Equal(0.0, body["temperature"]!.GetValue<double>());
    }

    [Then(@"^the models request has no body change$")]
    public void ThenTheModelsRequestHasNoBodyChange()
    {
        RecordedRequest models = Assert.Single(_context.Fake.RequestsFor(FakeRoute.Models));

        Assert.Equal("GET", models.Method);
        Assert.Null(models.Body);
        Assert.Null(models.ContentLength);
    }

    [Then(@"^the handler recorded exactly (\d+) requests?$")]
    public void ThenTheHandlerRecordedExactlyRequests(int count)
    {
        ClientResultException failure = Assert.IsType<ClientResultException>(_context.Thrown);

        Assert.Equal(500, failure.Status);
        Assert.Equal(count, _context.Fake.RequestCount);
    }

    [Then(@"^the recorded body has no seed field$")]
    public void ThenTheBodyHasNoSeedField()
    {
        JsonObject body = ChatBody();

        Assert.False(body.ContainsKey("seed"));
        Assert.Equal(0.0, body["temperature"]!.GetValue<double>());
    }

    [Then(@"^the exit code is 2$")]
    public void ThenTheExitCodeIsTwo()
    {
        CliRunResult run = _context.Run ?? throw new InvalidOperationException("No command was started.");

        Assert.Equal(2, run.ExitCode);
        Assert.Equal(string.Empty, run.Stdout);
        Assert.Contains(run.ErrorLines, line => line.Contains("Tiers:" + _context.TierName + ":ExtraBodyJson", StringComparison.Ordinal));
    }

    [Then(@"^the recorded body has the property ""([^""]*)"" with value (\d+)$")]
    public void ThenTheBodyHasThePropertyWithValue(string property, int value)
    {
        JsonObject body = ChatBody();

        Assert.Equal(value, body[property]!.GetValue<int>());
    }
}