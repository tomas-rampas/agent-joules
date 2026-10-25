using System.ClientModel;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Xunit;
using Xunit.Abstractions;

namespace AgentJoules.IntegrationTests;

/// <summary>
/// The live check behind the streaming usage record of AJ-108 (REQ-018, EDGE-007, design 6.3): one streaming chat call per tier through the production provider client (the named client of the tier over the shipped
/// configuration, so the tier's body patch, its token cap field and its system prompt prefix apply; the key is read by the NAME in <c>ApiKeyVariable</c> from the process environment) with a cap of 16 output tokens. The
/// OpenAI adapter asks for <c>stream_options.include_usage</c> on every streaming call (asserted offline by <c>StreamingUsageRequestTests</c>). Every tier must report usage in the final update (the committed record of each says reported); the nvidia tier
/// is the saturated free-tier worker (AJ-104), so an in-stream <c>ResourceExhausted</c> error chunk or a 429 or 503 answer ends its test without a failure and with the reason in the output, and a
/// stream without usage fails the test for every tier. xUnit 2 has no dynamic skip, so "skipped" here means the test returns early and says why. The tests are skipped unless AGENTJOULES_INTEGRATION is 1
/// (<see cref="IntegrationFactAttribute"/>). Only counts are written to the output, never a key or a reply text.
/// </summary>
[Trait("Spec", "REQ-018")]
[Trait("Spec", "EDGE-007")]
public sealed class StreamingUsageTests(ITestOutputHelper output)
{
    private const int OutputCap = 16;
    private const string Agent = "Planner";

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    private readonly ITestOutputHelper _output = output;

    [IntegrationFact]
    public Task The_local_tier_reports_usage_in_the_final_streaming_update() => AssertStreamingUsageAsync(Tier.Local, usageRequired: true);

    [IntegrationFact]
    public Task The_nvidia_tier_records_its_streaming_usage_behaviour() => AssertStreamingUsageAsync(Tier.Nvidia, usageRequired: true, toleratesSaturation: true);

    [IntegrationFact]
    public Task The_nebius_tier_reports_usage_in_the_final_streaming_update() => AssertStreamingUsageAsync(Tier.Nebius, usageRequired: true);

    private async Task AssertStreamingUsageAsync(Tier tier, bool usageRequired, bool toleratesSaturation = false)
    {
        string name = TierOptions.NameOf(tier);
        AgentJoulesOptions options = ConfigurationFixtures.ShippedOptions();
        TierOptions entry = options.Tiers[name];

        // Real HTTP: the named client of the tier with the production pipeline; the key comes from the process environment by its variable name.
        IConfiguration environment = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var redactor = new SecretRedactor();
        redactor.Register(entry.ApiKeyVariable is null ? null : environment[entry.ApiKeyVariable]);

        var services = new ServiceCollection();
        _ = services.AddSingleton(environment);
        _ = services.AddSingleton<IOptions<AgentJoulesOptions>>(Options.Create(options));
        _ = services.AddSingleton(redactor);
        _ = services.AddAgentJoulesProviderClients();
        await using ServiceProvider provider = services.BuildServiceProvider();

        using IChatClient client = provider.GetRequiredService<IProviderClientFactory>().Create(tier, entry.ResolveModelId(Agent));
        List<ChatMessage> messages = [new ChatMessage(ChatRole.User, "What is 2+2? Answer with the number only.")];
        ChatOptions chatOptions = ChatOptionsFactory.Create(entry, Agent, OutputCap, options.Workflow.Seed);

        // As a run does: the tier prefix is joined to the instruction of the agent, not sent as a message of its own.
        chatOptions.Instructions = AgentCatalog.InstructionsFor(Agent, entry.SystemPromptPrefix);

        UsageDetails? usage = null;
        bool finalUpdateHasUsage = false;
        int updates = 0;
        using var timeout = new CancellationTokenSource(Limit);
        try
        {
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, chatOptions, timeout.Token))
            {
                updates++;
                UsageContent? carried = update.Contents.OfType<UsageContent>().LastOrDefault();
                finalUpdateHasUsage = carried is not null;
                usage = carried?.Details ?? usage;
            }
        }
        catch (ProviderErrorException failure) when (toleratesSaturation && failure.Message.Contains("ResourceExhausted", StringComparison.Ordinal))
        {
            _output.WriteLine($"{name}: skipped, the endpoint answered with an in-stream error chunk (HTTP {failure.HttpStatus}, code {failure.ErrorCode}): {redactor.Redact(failure.Message)}");
            return;
        }
        catch (ClientResultException failure) when (toleratesSaturation && failure.Status is 429 or 503)
        {
            _output.WriteLine($"{name}: skipped, the endpoint answered HTTP {failure.Status} (a saturated worker)");
            return;
        }

        Assert.True(updates > 0, $"The {name} stream held no update.");
        if (usage is null || !finalUpdateHasUsage)
        {
            _output.WriteLine($"{name}: Streaming usage: not-reported ({updates} updates)");
            Assert.False(usageRequired, $"The final update of the {name} stream carries no usage although the tier is expected to report it.");
            return;
        }

        _output.WriteLine($"{name}: Streaming usage: reported ({updates} updates); input {usage.InputTokenCount}, output {usage.OutputTokenCount}");
        Assert.True(usage.InputTokenCount > 0, $"The {name} usage reports no input tokens.");
        Assert.True(usage.OutputTokenCount > 0, $"The {name} usage reports no output tokens.");
    }
}