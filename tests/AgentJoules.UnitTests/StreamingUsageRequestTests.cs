using System.Text.Json.Nodes;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Agents;

using Microsoft.Extensions.AI;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-108 (REQ-018, EDGE-007): what the production stack puts on the wire for a streaming call and for the reasoning toggle of each tier. Every test runs the real OpenAI SDK, the real Microsoft.Extensions.AI adapter, the real
/// named <see cref="HttpClient"/> pipeline and the real body patch against a <see cref="FakeHttpMessageHandler"/> as the primary handler, so no request leaves the process. The toggle values come from the shipped
/// <c>config/appsettings.json</c> (copied onto the fixture tiers, whose URLs are fake), so a change of the file is a change of what these tests see.
/// </summary>
[Trait("Spec", "REQ-018")]
public sealed class StreamingUsageRequestTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly ChatMessage[] Hello = [new(ChatRole.User, "What is 2+2?")];
    private const int OutputCap = 16;

    // Four fixture tiers that carry the toggle settings of the shipped configuration.
    private static ProviderClientHost HostWithShippedToggles(out AgentJoulesOptions options)
    {
        AgentJoulesOptions shipped = ConfigurationFixtures.ShippedOptions();
        options = ProviderClientFixtures.FourTiers();
        foreach (string name in TierOptions.KnownNames)
        {
            options.Tiers[name].ExtraBodyJson = shipped.Tiers[name].ExtraBodyJson;
            options.Tiers[name].SystemPromptPrefix = shipped.Tiers[name].SystemPromptPrefix;
            options.Tiers[name].ReasoningMode = shipped.Tiers[name].ReasoningMode;
            options.Tiers[name].MaxTokensField = shipped.Tiers[name].MaxTokensField;
        }

        return ProviderClientFixtures.Build(options);
    }

    // One streaming call through the production client of the tier; the instruction of the agent carries the tier prefix, as in a run.
    private static async Task<JsonObject> StreamOnceAsync(ProviderClientHost host, AgentJoulesOptions options, Tier tier)
    {
        TierOptions entry = options.Tiers[TierOptions.NameOf(tier)];
        using IChatClient client = host.Factory.Create(tier, entry.ResolveModelId("Planner"));
        ChatOptions chatOptions = ChatOptionsFactory.Create(entry, "Planner", OutputCap, options.Workflow.Seed);
        chatOptions.Instructions = AgentCatalog.InstructionsFor("Planner", entry.SystemPromptPrefix);

        using var guard = new CancellationTokenSource(HangGuard);
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Hello, chatOptions).WithCancellation(guard.Token))
        {
            _ = update;
        }

        RecordedRequest request = Assert.Single(host.Handler(tier).RequestsFor(FakeRoute.ChatCompletions));
        return request.Json ?? throw new InvalidOperationException("The recorded request has no JSON body.");
    }

    private static string[] SystemMessagesOf(JsonObject body) =>
        [.. ((JsonArray)body["messages"]!).Where(static message => message!["role"]!.GetValue<string>() == "system").Select(static message => message!["content"]!.ToJsonString())];

    [Theory]
    [InlineData(Tier.Local)]
    [InlineData(Tier.Nvidia)]
    [InlineData(Tier.Nebius)]
    public async Task A_streaming_call_asks_for_usage_in_the_final_chunk(Tier tier)
    {
        using ProviderClientHost host = HostWithShippedToggles(out AgentJoulesOptions options);

        JsonObject body = await StreamOnceAsync(host, options, tier);

        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(Tier.Nvidia)]
    [InlineData(Tier.Nebius)]
    public async Task A_hosted_call_carries_the_enable_thinking_toggle_and_no_no_think_system_message(Tier tier)
    {
        using ProviderClientHost host = HostWithShippedToggles(out AgentJoulesOptions options);

        JsonObject body = await StreamOnceAsync(host, options, tier);

        Assert.False(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.All(SystemMessagesOf(body), static content => Assert.DoesNotContain("/no_think", content, StringComparison.Ordinal));
        Assert.Equal(0.0, body["temperature"]!.GetValue<double>());
        Assert.Equal(OutputCap, body["max_completion_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_local_call_carries_the_detailed_thinking_off_system_message_and_no_body_toggle()
    {
        using ProviderClientHost host = HostWithShippedToggles(out AgentJoulesOptions options);

        JsonObject body = await StreamOnceAsync(host, options, Tier.Local);

        Assert.False(body.ContainsKey("chat_template_kwargs"));
        string system = Assert.Single(SystemMessagesOf(body));
        Assert.StartsWith("\"detailed thinking off", system, StringComparison.Ordinal);

        // The local server rejects max_completion_tokens (found live): the cap travels as max_tokens, once.
        Assert.Equal(OutputCap, body["max_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_completion_tokens"));
    }

    [Fact]
    public async Task The_toggle_of_one_hosted_tier_does_not_reach_the_local_tier()
    {
        using ProviderClientHost host = HostWithShippedToggles(out AgentJoulesOptions options);

        JsonObject nvidia = await StreamOnceAsync(host, options, Tier.Nvidia);
        JsonObject local = await StreamOnceAsync(host, options, Tier.Local);

        Assert.True(nvidia.ContainsKey("chat_template_kwargs"));
        Assert.False(local.ContainsKey("chat_template_kwargs"));
    }
}