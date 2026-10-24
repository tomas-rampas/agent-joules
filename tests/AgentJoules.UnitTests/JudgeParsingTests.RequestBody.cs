using System.Text.Json.Nodes;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class JudgeParsingTests
{
    // ---- M3 exit (peer M-2): the judge request is bounded and carries the system prompt prefix of the judge tier; the real SDK, adapter and body patch over a fake endpoint ----

    private const int ConfiguredCap = 48;
    private const string Prefix = "/no_think";

    // Scores one step through a real provider client over the fake endpoint of the judge tier and returns the one recorded judge request.
    private static async Task<RecordedRequest> JudgeRequestAsync(Tier judgeTier, string? prefix, string maxTokensField = "max_completion_tokens", int? cap = null, bool viaRegistration = false)
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        string tierName = TierOptions.NameOf(judgeTier);
        options.Tiers[tierName].SystemPromptPrefix = prefix;
        options.Tiers[tierName].MaxTokensField = maxTokensField;
        options.Judge.Tier = tierName;
        options.Judge.ModelId = "model-" + tierName;
        if (cap is { } given)
        {
            options.Judge.MaxOutputTokens = given;
        }

        using ProviderClientHost host = ProviderClientFixtures.Build(options);
        var time = new TimerTrackingTimeProvider();
        await using var meter = new UnmeteredEnergyMeter(time);
        await meter.StartAsync(CancellationToken.None);
        var services = new MeteringServices(meter, NoOpMeterGate.Instance, new InMemoryMetricsSink(), NotConfiguredCostCalculator.Instance, TestInstruments.Create(), time);

        IJudgeService judge;
        ServiceProvider? provider = null;
        if (viaRegistration)
        {
            provider = new ServiceCollection()
                .AddSingleton<IOptions<AgentJoulesOptions>>(Options.Create(options))
                .AddSingleton<IProviderClientFactory>(host.Factory)
                .AddSingleton(new SecretRedactor())
                .AddAgentJoulesJudge(ConfigurationFixtures.RepositoryRoot())
                .BuildServiceProvider();
            judge = provider.GetRequiredService<IJudgeService>();
        }
        else
        {
            judge = new JudgeService(judgeTier, "model-" + tierName, JudgeRubric, host.Factory, new SecretRedactor(), RetryOptions.Default, new Random(4711), cap ?? ConfiguredCap, prefix);
        }

        using (provider)
        {
            _ = await judge.ScoreAsync(new JudgeRequest(Tier.Local, [JudgeStep()], JudgeTasks, services), CancellationToken.None).WaitAsync(JudgeHangGuard);
        }

        return Assert.Single(host.Handler(judgeTier).RequestsFor(FakeRoute.ChatCompletions));
    }

    private static JsonArray MessagesOf(RecordedRequest request) => (JsonArray)request.Json!["messages"]!;

    [Fact]
    [Trait("Spec", "REQ-010")]
    public async Task The_judge_request_carries_the_cap_and_the_prefix_as_the_system_message()
    {
        RecordedRequest request = await JudgeRequestAsync(Tier.Nvidia, Prefix);

        JsonObject body = request.Json!;
        Assert.Equal(ConfiguredCap, body["max_completion_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.Equal(0.0, body["temperature"]!.GetValue<double>());
        Assert.Equal("model-nvidia", body["model"]!.GetValue<string>());
        JsonArray messages = MessagesOf(request);
        Assert.Equal(2, messages.Count);
        Assert.Equal(("system", Prefix), (messages[0]!["role"]!.GetValue<string>(), messages[0]!["content"]!.GetValue<string>()));
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
        Assert.EndsWith(ClosingLine, messages[1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-010")]
    public async Task A_tier_that_names_max_tokens_gets_max_tokens_on_the_judge_request()
    {
        RecordedRequest request = await JudgeRequestAsync(Tier.Nebius, Prefix, maxTokensField: "max_tokens");

        JsonObject body = request.Json!;
        Assert.Equal(ConfiguredCap, body["max_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_completion_tokens"));
        Assert.Equal("system", MessagesOf(request)[0]!["role"]!.GetValue<string>());
    }

    [Theory]
    [Trait("Spec", "REQ-010")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public async Task A_judge_tier_without_a_prefix_sends_the_user_message_alone(string? prefix)
    {
        RecordedRequest request = await JudgeRequestAsync(Tier.Nvidia, prefix);

        JsonArray messages = MessagesOf(request);
        Assert.Equal("user", Assert.Single(messages)!["role"]!.GetValue<string>());
        Assert.Equal(ConfiguredCap, request.Json!["max_completion_tokens"]!.GetValue<int>());
    }

    [Fact]
    [Trait("Spec", "REQ-010")]
    public async Task The_prefix_loses_its_trailing_white_space_like_the_prefix_of_an_agent_instruction()
    {
        RecordedRequest request = await JudgeRequestAsync(Tier.Nvidia, Prefix + "\n\n");

        Assert.Equal(Prefix, MessagesOf(request)[0]!["content"]!.GetValue<string>());
    }

    [Theory]
    [Trait("Spec", "REQ-010")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_registration_hands_the_option_and_the_prefix_of_Judge_Tier_to_the_judge(bool configured)
    {
        RecordedRequest request = await JudgeRequestAsync(Tier.Nvidia, Prefix, cap: configured ? 77 : null, viaRegistration: true);

        // Registered from the options: 77 when Judge:MaxOutputTokens is set, otherwise the shipped default.
        Assert.Equal(configured ? 77 : JudgeOptions.DefaultMaxOutputTokens, request.Json!["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal(Prefix, MessagesOf(request)[0]!["content"]!.GetValue<string>());
    }

    [Theory]
    [Trait("Spec", "REQ-010")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(32769)]
    public void The_judge_refuses_a_cap_outside_1_to_32768(int cap)
    {
        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JudgeService(Tier.Nvidia, JudgeModel, JudgeRubric, new FakeProviderClientFactory(new TimerTrackingTimeProvider(), static (_, _) => throw new InvalidOperationException()), new SecretRedactor(), RetryOptions.Default, new Random(4711), cap));

        Assert.Equal("maxOutputTokens", failure.ParamName);
    }

    // ---- M3 exit (peer M-1): the equality fact is null, not false, when no step was judged ----

    [Fact]
    [Trait("Spec", "REQ-010")]
    public async Task Self_judged_is_true_for_a_judged_step_of_the_judge_model_and_false_for_another()
    {
        await using JudgeRig rig = await JudgeRig.CreateAsync("4");

        JudgeResult same = await rig.ScoreAsync([JudgeStep(model: JudgeModel)]);
        JudgeResult other = await rig.ScoreAsync([JudgeStep()]);

        Assert.True(same.SelfJudged);
        Assert.False(other.SelfJudged);
    }

    [Fact]
    [Trait("Spec", "REQ-010")]
    public async Task Self_judged_is_null_when_no_step_was_judged()
    {
        await using JudgeRig rig = await JudgeRig.CreateAsync("4");
        await using JudgeRig blank = await JudgeRig.CreateAsync("4", judgeModel: "  ");

        // A step of the judge model that did not finish ok is not judged, so the equality says nothing; neither does a blank model or no step at all.
        JudgeResult notOk = await rig.ScoreAsync([JudgeStep(outcome: StepOutcomes.Error, model: JudgeModel)]);
        JudgeResult empty = await rig.ScoreAsync([]);
        JudgeResult blankModel = await blank.ScoreAsync([JudgeStep()]);

        Assert.Equal((0, null), (notOk.JudgedSteps, notOk.SelfJudged));
        Assert.Equal((0, null), (empty.JudgedSteps, empty.SelfJudged));
        Assert.Null(blankModel.SelfJudged);
        Assert.Equal((1, 1), (blankModel.JudgedSteps, blankModel.FailedSteps));
    }

    [Fact]
    [Trait("Spec", "REQ-010")]
    public async Task The_no_op_judge_makes_no_self_judging_statement()
    {
        JudgeResult result = await new NoOpJudgeService().ScoreAsync(new JudgeRequest(Tier.Local, [JudgeStep()], JudgeTasks, null!), CancellationToken.None);

        Assert.Null(result.SelfJudged);
        Assert.Equal(0, result.JudgedSteps);
    }

    [Fact]
    [Trait("Spec", "REQ-010")]
    public void The_default_cap_is_32_and_the_judge_agent_name_is_one_constant()
    {
        Assert.Equal(32, JudgeOptions.DefaultMaxOutputTokens);
        Assert.Equal("Judge", TierOptions.JudgeAgentName);
        Assert.DoesNotContain(TierOptions.JudgeAgentName, TierOptions.KnownAgentNames);
    }
}