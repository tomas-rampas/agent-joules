using System.Text.Json.Nodes;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Providers;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class AttributionBaselineTests
{
    /// <summary>
    /// The REQ-011 cases of AJ-049, in the class that the story names (<c>AttributionBaselineTests</c>) but in a nested class of their own: xUnit takes traits from the
    /// test class and the method, not from the enclosing class, so the class-level <c>REQ-005</c> trait of the M1 tests does not tag these.
    /// </summary>
    public sealed class AgentFactoryCases
    {
        // ----- REQ-011 (AJ-049): one metered agent per name, built from a tier and a model. The provider clients are fakes, or the real SDK against a fake endpoint;
        // the clock moves only when the code under test waits, so nothing waits on the wall clock and no request leaves the process.

        private const string DesignPlanner = "Break the task into 3 to 5 numbered steps; no prose.";
        private const string DesignResearcher = "Given the task and plan, list the facts, constraints and risks the answer must respect.";
        private const string DesignWriter = "Given the task and plan, write the complete answer.";
        private const string DesignReviewer = "Given the task, the research notes and the draft, return the corrected final answer only.";

        private static StepIdentity AgentIdentity(string agent, int repeat = 1) =>
            new(agent, "model-" + agent.ToLowerInvariant(), Tier.Nvidia, "incident-brief", repeat, IsJudge: false);

        private static ChatOptions AgentOptions(string agent) =>
            ChatOptionsFactory.Create(AgentFactoryRig.FourModelTier(), agent, 512, seed: 42);

        private static ChatClientAgent ChatAgent(AIAgent agent) => Assert.IsType<ChatClientAgent>(agent);

        // ChatClientAgent.ChatOptions is internal; the agent answers its default options through GetService.
        private static ChatOptions AgentChatOptions(AIAgent agent) => Assert.IsType<ChatOptions>(agent.GetService<ChatOptions>());

        private static MeteredChatClient MeteredOf(AIAgent agent) => Assert.IsType<MeteredChatClient>(ChatAgent(agent).ChatClient);

        private static RetryLedger LedgerOf(IChatClient client) => Assert.IsType<RetryLedger>(client.GetService(typeof(RetryLedger)));

        private static FakeChatCall OnlyCallOf(CreatedClient created) => Assert.Single(Assert.IsType<FakeChatClient>(created.Client).Calls);

        // The real SDK against a fake endpoint, composed by the real provider client factory: the endpoint answers the scripted statuses on the fake clock of the rig.
        private static async Task<(AgentFactoryRig Rig, ProviderClientHost Host)> RealProviderRigAsync(params int[] statuses)
        {
            ProviderClientHost? host = null;
            AgentFactoryRig rig = await AgentFactoryRig.CreateAsync(time =>
            {
                var endpoint = new FakeHttpMessageHandler { Clock = time, DefaultDuration = TimeSpan.FromMilliseconds(10) };
                if (statuses.Length > 0)
                {
                    endpoint.SetStatuses(FakeRoute.ChatCompletions, statuses);
                }

                host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers(), handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = endpoint });
                return host.Factory;
            });

            return (rig, host!);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Pipeline_order_and_name()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();

            AIAgent agent = rig.Factory.Create(AgentIdentity("Planner"), AgentOptions("Planner"));

            ChatClientAgent chatAgent = ChatAgent(agent);
            Assert.Equal("Planner", chatAgent.Name);

            // The exact type of the client the agent holds: UseProvidedChatClientAsIs is on, so no default decorator sits above the metered client.
            MeteredChatClient metered = Assert.IsType<MeteredChatClient>(chatAgent.ChatClient);
            RateLimitRetryChatClient retry = Assert.IsType<RateLimitRetryChatClient>(metered.GetService(typeof(RateLimitRetryChatClient)));
            CreatedClient provider = Assert.Single(rig.Fakes.Created);

            Assert.Same(provider.Client, retry.GetService(typeof(FakeChatClient)));
            Assert.Same(provider.Client, metered.GetService(typeof(FakeChatClient)));
            Assert.Null(retry.GetService(typeof(MeteredChatClient)));
            Assert.Same(LedgerOf(metered), LedgerOf(retry));
            Assert.Equal(Tier.Nvidia, provider.Tier);
            Assert.Equal("model-planner", provider.ModelId);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Reading_agent_matches_owner()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            TierOptions tier = AgentFactoryRig.FourModelTier();

            IReadOnlyList<AIAgent> agents = rig.Factory.CreateAll(Tier.Nvidia, tier, AgentFactoryRig.IncidentBrief(), repeat: 1, seed: 42);

            Assert.Equal(["Planner", "Researcher", "Writer", "Reviewer"], agents.Select(agent => agent.Name));
            Assert.Equal(4, agents.Select(MeteredOf).Distinct().Count());
            Assert.Equal(4, rig.Fakes.Created.Count);

            foreach (AIAgent agent in agents)
            {
                int before = rig.Readings().Count;

                _ = await rig.RunAsync(agent);

                StepReading reading = Assert.Single(rig.Readings().Skip(before));
                Assert.Equal(agent.Name, reading.Agent);
                Assert.Equal(tier.ResolveModelId(agent.Name!), reading.ModelId);
                Assert.Equal(Tier.Nvidia, reading.Tier);
                Assert.Equal("incident-brief", reading.Task);
                Assert.Equal(1, reading.Repeat);
            }

            Assert.Equal(["Planner", "Researcher", "Writer", "Reviewer"], rig.Readings().Select(reading => reading.Agent));

            // Each agent called its own fake client, once, with its own model.
            for (int i = 0; i < agents.Count; i++)
            {
                CreatedClient created = rig.Fakes.Created[i];
                Assert.Equal(tier.ResolveModelId(agents[i].Name!), created.ModelId);
                Assert.Equal(created.ModelId, OnlyCallOf(created).Options?.ModelId);
            }
        }

        [Theory]
        [Trait("Spec", "REQ-011")]
        [InlineData(384)]
        [InlineData(512)]
        [InlineData(768)]
        public async Task Agents_carry_temperature_zero_and_the_task_limit(int maxOutputTokens)
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            TierOptions tier = AgentFactoryRig.FourModelTier();
            var task = new TaskDefinition("incident-brief", "Summarise the incident.", maxOutputTokens);

            IReadOnlyList<AIAgent> agents = rig.Factory.CreateAll(Tier.Nvidia, tier, task, repeat: 1, seed: 42);

            Assert.Equal(4, agents.Count);
            foreach (AIAgent agent in agents)
            {
                ChatOptions options = AgentChatOptions(agent);
                Assert.Equal(0f, options.Temperature);
                Assert.Equal(maxOutputTokens, options.MaxOutputTokens);
                Assert.Equal(tier.ResolveModelId(agent.Name!), options.ModelId);
                Assert.Null(options.Seed);
                Assert.Null(options.Tools);
                Assert.Null(options.ToolMode);
            }
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task The_options_reach_the_provider_client_and_the_seed_only_when_the_tier_honours_it()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            TierOptions tier = AgentFactoryRig.FourModelTier();
            tier.SupportsSeed = true;

            AIAgent writer = rig.Factory.CreateAll(Tier.Nvidia, tier, AgentFactoryRig.IncidentBrief(), repeat: 1, seed: 42)[2];
            _ = await rig.RunAsync(writer);

            ChatOptions sent = Assert.IsType<ChatOptions>(OnlyCallOf(rig.Fakes.Created[2]).Options);
            Assert.Equal(0f, sent.Temperature);
            Assert.Equal(512, sent.MaxOutputTokens);
            Assert.Equal(42, sent.Seed);
            Assert.Equal("model-writer", sent.ModelId);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Create_works_on_a_copy_of_the_options_it_was_given()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            ChatOptions given = AgentOptions("Writer");

            AIAgent first = rig.Factory.Create(AgentIdentity("Writer"), given);
            AIAgent second = rig.Factory.Create(AgentIdentity("Writer", repeat: 2), given);

            Assert.Null(given.Instructions);
            Assert.NotSame(given, AgentChatOptions(first));
            Assert.NotSame(AgentChatOptions(first), AgentChatOptions(second));
            Assert.Equal(DesignWriter, AgentChatOptions(first).Instructions);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public void The_catalog_defines_exactly_the_four_agents_with_the_design_instructions()
        {
            Assert.Equal(["Planner", "Researcher", "Writer", "Reviewer"], AgentCatalog.Names);
            Assert.Equal(["Planner", "Researcher", "Writer", "Reviewer"], AgentCatalog.All.Select(definition => definition.Name));
            Assert.Equal([DesignPlanner, DesignResearcher, DesignWriter, DesignReviewer], AgentCatalog.All.Select(definition => definition.Instructions));
            Assert.Equal(DesignPlanner, AgentCatalog.Get("Planner").Instructions);
            Assert.Equal(DesignResearcher, AgentCatalog.Get("Researcher").Instructions);
            Assert.Equal(DesignWriter, AgentCatalog.Get("Writer").Instructions);
            Assert.Equal(DesignReviewer, AgentCatalog.Get("Reviewer").Instructions);
            Assert.True(AgentCatalog.TryGet("Writer", out AgentDefinition? found));
            Assert.Equal(new AgentDefinition("Writer", DesignWriter), found);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Each_agent_runs_with_its_own_instruction()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            IReadOnlyList<AIAgent> agents = rig.Factory.CreateAll(Tier.Nvidia, AgentFactoryRig.FourModelTier(), AgentFactoryRig.IncidentBrief(), repeat: 1, seed: 42);

            foreach (AIAgent agent in agents)
            {
                _ = await rig.RunAsync(agent);
            }

            Assert.Equal(
                [DesignPlanner, DesignResearcher, DesignWriter, DesignReviewer],
                rig.Fakes.Created.Select(created => OnlyCallOf(created).Options?.Instructions));
            Assert.Equal([DesignPlanner, DesignResearcher, DesignWriter, DesignReviewer], agents.Select(agent => ChatAgent(agent).Instructions));
        }

        [Theory]
        [Trait("Spec", "REQ-011")]
        [InlineData("planner")]
        [InlineData("PLANNER")]
        [InlineData("Judge")]
        [InlineData("Judgey")]
        [InlineData(" Planner")]
        [InlineData("")]
        public async Task A_name_outside_the_catalog_is_a_configuration_error_and_no_client_is_created(string name)
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();

            ConfigurationException refused = Assert.Throws<ConfigurationException>(() => rig.Factory.Create(AgentIdentity(name), new ChatOptions()));

            Assert.Equal(ExitCode.InvalidArguments, refused.ExitCode);
            Assert.Throws<ConfigurationException>(() => AgentCatalog.Get(name));
            Assert.False(AgentCatalog.TryGet(name, out AgentDefinition? none));
            Assert.Null(none);
            Assert.Empty(rig.Fakes.Created);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task The_refusal_of_an_unknown_name_echoes_it_bounded_and_on_one_line()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            string hostile = "Judge\r\nsecond line " + new string('x', 500);

            ConfigurationException refused = Assert.Throws<ConfigurationException>(() => rig.Factory.Create(AgentIdentity(hostile), new ChatOptions()));

            Assert.DoesNotContain('\n', refused.Message);
            Assert.DoesNotContain('\r', refused.Message);
            Assert.True(refused.Message.Length < 300, refused.Message);
            Assert.Contains("Judge", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Planner", refused.Message, StringComparison.Ordinal);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task The_tier_prefix_is_put_in_front_of_every_instruction_after_a_blank_line()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            TierOptions tier = AgentFactoryRig.FourModelTier();
            tier.SystemPromptPrefix = "Answer in plain English.  \n";

            IReadOnlyList<AIAgent> agents = rig.Factory.CreateAll(Tier.Nvidia, tier, AgentFactoryRig.IncidentBrief(), repeat: 1, seed: 42);
            _ = await rig.RunAsync(agents[2]);

            const string expected = "Answer in plain English.\n\n" + DesignWriter;
            Assert.Equal(expected, ChatAgent(agents[2]).Instructions);
            Assert.Equal(expected, OnlyCallOf(rig.Fakes.Created[2]).Options?.Instructions);
            Assert.Equal("Answer in plain English.\n\n" + DesignPlanner, ChatAgent(agents[0]).Instructions);
            Assert.Equal(expected, AgentCatalog.InstructionsFor("Writer", "Answer in plain English."));
        }

        [Theory]
        [Trait("Spec", "REQ-011")]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \n")]
        public async Task A_blank_prefix_leaves_the_instruction_as_it_is(string? prefix)
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            TierOptions tier = AgentFactoryRig.FourModelTier();
            tier.SystemPromptPrefix = prefix;

            IReadOnlyList<AIAgent> agents = rig.Factory.CreateAll(Tier.Nvidia, tier, AgentFactoryRig.IncidentBrief(), repeat: 1, seed: 42);

            Assert.Equal([DesignPlanner, DesignResearcher, DesignWriter, DesignReviewer], agents.Select(agent => ChatAgent(agent).Instructions));
            Assert.Equal(DesignReviewer, AgentCatalog.InstructionsFor("Reviewer", prefix));
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_new_repeat_gets_new_client_instances_and_new_ledgers()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            TierOptions tier = AgentFactoryRig.FourModelTier();
            TaskDefinition task = AgentFactoryRig.IncidentBrief();

            IReadOnlyList<AIAgent> first = rig.Factory.CreateAll(Tier.Nvidia, tier, task, repeat: 1, seed: 42);
            IReadOnlyList<AIAgent> second = rig.Factory.CreateAll(Tier.Nvidia, tier, task, repeat: 2, seed: 42);

            MeteredChatClient[] metered = [.. first.Concat(second).Select(MeteredOf)];
            Assert.Equal(8, metered.Distinct().Count());
            Assert.Equal(8, metered.Select(LedgerOf).Distinct().Count());
            Assert.Equal(8, rig.Fakes.Created.Select(created => created.Client).Distinct().Count());
            Assert.Equal(8, first.Concat(second).Distinct().Count());
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_new_repeat_does_not_reuse_the_retry_ledger_of_the_previous_one()
        {
            (AgentFactoryRig rig, ProviderClientHost host) = await RealProviderRigAsync(429, 429, 200);
            await using (rig)
            using (host)
            {
                TierOptions tier = AgentFactoryRig.FourModelTier();
                TaskDefinition task = AgentFactoryRig.IncidentBrief();
                AIAgent firstWriter = rig.Factory.CreateAll(Tier.Nvidia, tier, task, repeat: 1, seed: 42)[2];

                _ = await rig.RunAsync(firstWriter);

                Assert.Equal(2, Assert.Single(rig.Readings()).Retries);
                Assert.Equal(2, LedgerOf(MeteredOf(firstWriter)).Retries);

                AIAgent secondWriter = rig.Factory.CreateAll(Tier.Nvidia, tier, task, repeat: 2, seed: 42)[2];

                Assert.NotSame(MeteredOf(firstWriter), MeteredOf(secondWriter));
                Assert.NotSame(LedgerOf(MeteredOf(firstWriter)), LedgerOf(MeteredOf(secondWriter)));
                Assert.Equal(0, LedgerOf(MeteredOf(secondWriter)).Retries);

                _ = await rig.RunAsync(secondWriter);

                StepReading second = rig.Readings()[^1];
                Assert.Equal(0, second.Retries);
                Assert.Equal(2, second.Repeat);
                Assert.Equal(2, LedgerOf(MeteredOf(firstWriter)).Retries);
            }
        }

        // The text of a chat message content node: a string, or an array of parts whose first part holds the text.
        private static string? ContentText(JsonNode? content) =>
            content is JsonArray parts ? parts[0]?["text"]?.GetValue<string>() : content?.GetValue<string>();

        [Theory]
        [Trait("Spec", "REQ-011")]
        [InlineData(null)]
        [InlineData("Answer in plain English.")]
        public async Task An_agent_over_the_real_provider_client_makes_one_measured_reading_with_its_name(string? prefix)
        {
            (AgentFactoryRig rig, ProviderClientHost host) = await RealProviderRigAsync();
            await using (rig)
            using (host)
            {
                TierOptions tier = AgentFactoryRig.FourModelTier();
                tier.SystemPromptPrefix = prefix;
                AIAgent planner = rig.Factory.CreateAll(Tier.Nvidia, tier, AgentFactoryRig.IncidentBrief(), repeat: 1, seed: 42)[0];

                _ = await rig.RunAsync(planner);

                StepReading reading = Assert.Single(rig.Readings());
                Assert.Equal("Planner", reading.Agent);
                Assert.Equal("model-planner", reading.ModelId);
                Assert.Equal(StepOutcomes.Ok, reading.Outcome);
                Assert.Equal(0, reading.Retries);
                Assert.Equal(MetricLabel.Measured, reading.InputTokens.Label);
                Assert.Equal(7.0, reading.InputTokens.Value);
                Assert.Equal(MetricLabel.Measured, reading.OutputTokens.Label);
                Assert.Equal(3.0, reading.OutputTokens.Value);

                RecordedRequest request = Assert.Single(host.Handler(Tier.Nvidia).RequestsFor(FakeRoute.ChatCompletions));
                Assert.NotNull(request.Json);
                Assert.Equal("model-planner", request.Json["model"]?.GetValue<string>());
                Assert.Equal(0.0, request.Json["temperature"]?.GetValue<double>());

                // The instruction is the system message, the first of the request, and it is the catalog text (after the prefix and a blank line when the tier has one).
                JsonObject first = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(request.Json["messages"])[0]);
                Assert.Equal("system", first["role"]?.GetValue<string>());
                Assert.Equal(prefix is null ? DesignPlanner : prefix + "\n\n" + DesignPlanner, ContentText(first["content"]));

                // The agents have no tools by design: nothing about tools reaches the wire.
                Assert.False(request.Json.ContainsKey("tools"));
                Assert.False(request.Json.ContainsKey("tool_choice"));
            }
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task The_container_serves_the_provider_interface_and_builds_one_factory_per_metering_context()
        {
            var collection = new ServiceCollection();
            _ = collection.AddSingleton(ProviderClientFixtures.KeyConfiguration());
            _ = collection.AddSingleton<IOptions<AgentJoulesOptions>>(Options.Create(ProviderClientFixtures.FourTiers()));
            _ = collection.AddAgentJoulesProviderClients().AddAgentJoulesAgentFactory().AddAgentJoulesAgentFactory();
            await using ServiceProvider provider = collection.BuildServiceProvider();
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();

            Assert.Same(provider.GetRequiredService<ProviderClientFactory>(), provider.GetRequiredService<IProviderClientFactory>());
            Assert.Same(RetryOptions.Default, provider.GetRequiredService<RetryOptions>());
            Assert.Same(Random.Shared, provider.GetRequiredKeyedService<Random>(ServiceCollectionExtensions.JitterKey));
            Assert.Null(provider.GetService<Random>());

            Func<MeteringServices, AgentFactory> forRun = provider.GetRequiredService<Func<MeteringServices, AgentFactory>>();
            AgentFactory one = forRun(rig.Services);
            AgentFactory other = forRun(rig.Services);

            Assert.NotSame(one, other);
            MeteredChatClient metered = MeteredOf(one.Create(AgentIdentity("Reviewer"), AgentOptions("Reviewer")));
            Assert.IsType<RetryLedger>(metered.GetService(typeof(RetryLedger)));
            Assert.IsType<RateLimitRetryChatClient>(metered.GetService(typeof(RateLimitRetryChatClient)));
            Assert.Throws<ArgumentNullException>(() => forRun(null!));
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Two_pipelines_of_a_seeded_factory_get_their_own_jitter_sources_with_reproducible_seeds()
        {
            (AgentFactoryRig rig, ProviderClientHost host) = await RealProviderRigAsync(429, 200);
            await using (rig)
            using (host)
            {
                // The factory draws one seed per pipeline from the Random it was given (seeded by the rig), so the first retry wait of the first pipeline is the first draw of
                // Random(first seed) and that of the second pipeline the first draw of Random(second seed): full jitter over the first second.
                Random seeds = new(AgentFactoryRig.RandomSeed);
                double firstWait = Math.Floor(new Random(seeds.Next()).NextDouble() * 1000.0);
                double secondWait = Math.Floor(new Random(seeds.Next()).NextDouble() * 1000.0);
                Assert.NotEqual(firstWait, secondWait);

                AIAgent first = rig.Factory.Create(AgentIdentity("Writer"), AgentOptions("Writer"));
                _ = await rig.RunAsync(first);
                host.Handler(Tier.Nvidia).SetStatuses(FakeRoute.ChatCompletions, 429, 200);
                AIAgent second = rig.Factory.Create(AgentIdentity("Writer", repeat: 2), AgentOptions("Writer"));
                _ = await rig.RunAsync(second);

                IReadOnlyList<StepReading> readings = rig.Readings();
                Assert.Equal(2, readings.Count);
                Assert.All(readings, reading => Assert.Equal(1, reading.Retries));
                Assert.InRange((double)readings[0].WaitMs, firstWait - 1.0, firstWait + 1.0);
                Assert.InRange((double)readings[1].WaitMs, secondWait - 1.0, secondWait + 1.0);
            }
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Create_refuses_options_that_carry_tools_and_creates_nothing()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            AIFunction function = AIFunctionFactory.Create(static () => "unused", "unused_tool");
            ChatOptions withTools = AgentOptions("Planner");
            withTools.Tools = [function];
            ChatOptions withMode = AgentOptions("Planner");
            withMode.ToolMode = ChatToolMode.Auto;

            _ = Assert.Throws<ArgumentException>(() => rig.Factory.Create(AgentIdentity("Planner"), withTools));
            _ = Assert.Throws<ArgumentException>(() => rig.Factory.Create(AgentIdentity("Planner"), withMode));
            Assert.Empty(rig.Fakes.Created);

            // An empty tool list is no tool: it is accepted, and the agent's options carry none.
            ChatOptions emptyTools = AgentOptions("Planner");
            emptyTools.Tools = [];
            AIAgent agent = rig.Factory.Create(AgentIdentity("Planner"), emptyTools);
            Assert.Null(AgentChatOptions(agent).ToolMode);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Create_refuses_a_judge_identity_and_creates_nothing()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            StepIdentity judge = AgentIdentity("Planner") with { IsJudge = true };

            _ = Assert.Throws<ArgumentException>(() => rig.Factory.Create(judge, AgentOptions("Planner")));
            Assert.Empty(rig.Fakes.Created);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task The_model_of_the_options_is_the_model_of_the_identity()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();

            // No model in the options: the identity's model is put on the copy, so the request names the model the reading is labelled with.
            ChatOptions noModel = new() { Temperature = 0, MaxOutputTokens = 512 };
            AIAgent agent = rig.Factory.Create(AgentIdentity("Planner"), noModel);
            Assert.Equal("model-planner", AgentChatOptions(agent).ModelId);
            Assert.Null(noModel.ModelId);
            _ = await rig.RunAsync(agent);
            Assert.Equal("model-planner", OnlyCallOf(Assert.Single(rig.Fakes.Created)).Options?.ModelId);
            Assert.Equal("model-planner", Assert.Single(rig.Readings()).ModelId);

            // Another model in the options than in the identity: refused before any client is created.
            ChatOptions other = AgentOptions("Planner");
            other.ModelId = "model-other";
            _ = Assert.Throws<ArgumentException>(() => rig.Factory.Create(AgentIdentity("Planner"), other));
            Assert.Single(rig.Fakes.Created);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public void The_jitter_source_is_a_keyed_registration_that_another_random_cannot_replace()
        {
            var collection = new ServiceCollection();
            _ = collection.AddSingleton(new Random(1));
            _ = collection.AddAgentJoulesAgentFactory();
            using ServiceProvider provider = collection.BuildServiceProvider();

            Assert.Same(Random.Shared, provider.GetRequiredKeyedService<Random>(ServiceCollectionExtensions.JitterKey));
            Assert.NotSame(Random.Shared, provider.GetRequiredService<Random>());
        }

        [Theory]
        [Trait("Spec", "REQ-011")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task A_seeded_keyed_jitter_registration_reaches_the_factory(bool replaceAfterwards)
        {
            const int seed = 77;
            (AgentFactoryRig rig, ProviderClientHost host) = await RealProviderRigAsync(429, 200);
            await using (rig)
            using (host)
            {
                var collection = new ServiceCollection();
                _ = collection.AddSingleton(rig.Providers);
                if (replaceAfterwards)
                {
                    // RemoveAll<Random>() would leave the keyed Random.Shared in place; the keyed form removes it.
                    _ = collection.AddAgentJoulesAgentFactory();
                    _ = collection.RemoveAllKeyed<Random>(ServiceCollectionExtensions.JitterKey);
                }

                _ = collection.AddKeyedSingleton(ServiceCollectionExtensions.JitterKey, new Random(seed));
                if (!replaceAfterwards)
                {
                    _ = collection.AddAgentJoulesAgentFactory();
                }

                await using ServiceProvider provider = collection.BuildServiceProvider();
                AgentFactory factory = provider.GetRequiredService<Func<MeteringServices, AgentFactory>>()(rig.Services);

                // The seeded source is not Random.Shared, so the factory draws the seed of the pipeline from it: the first retry wait is the first draw of Random(first seed).
                double expectedWait = Math.Floor(new Random(new Random(seed).Next()).NextDouble() * 1000.0);
                _ = await rig.RunAsync(factory.Create(AgentIdentity("Writer"), AgentOptions("Writer")));

                StepReading reading = Assert.Single(rig.Readings());
                Assert.Equal(1, reading.Retries);
                Assert.InRange((double)reading.WaitMs, expectedWait - 1.0, expectedWait + 1.0);
            }
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task The_factory_refuses_missing_arguments()
        {
            await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
            FakeProviderClientFactory clients = rig.Fakes;

            _ = Assert.Throws<ArgumentNullException>(() => new AgentFactory(null!, rig.Services, RetryOptions.Default, rig.Time, new Random(1)));
            _ = Assert.Throws<ArgumentNullException>(() => new AgentFactory(clients, null!, RetryOptions.Default, rig.Time, new Random(1)));
            _ = Assert.Throws<ArgumentNullException>(() => new AgentFactory(clients, rig.Services, null!, rig.Time, new Random(1)));
            _ = Assert.Throws<ArgumentNullException>(() => new AgentFactory(clients, rig.Services, RetryOptions.Default, null!, new Random(1)));
            _ = Assert.Throws<ArgumentNullException>(() => new AgentFactory(clients, rig.Services, RetryOptions.Default, rig.Time, null!));
            _ = Assert.Throws<ArgumentNullException>(() => rig.Factory.Create(null!, new ChatOptions()));
            _ = Assert.Throws<ArgumentNullException>(() => rig.Factory.Create(AgentIdentity("Planner"), null!));
            _ = Assert.Throws<ArgumentNullException>(() => rig.Factory.CreateAll(Tier.Nvidia, null!, AgentFactoryRig.IncidentBrief(), repeat: 1, seed: 42));
            _ = Assert.Throws<ArgumentNullException>(() => rig.Factory.CreateAll(Tier.Nvidia, AgentFactoryRig.FourModelTier(), null!, repeat: 1, seed: 42));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => rig.Factory.CreateAll(Tier.Nvidia, AgentFactoryRig.FourModelTier(), AgentFactoryRig.IncidentBrief(), repeat: 0, seed: 42));
            Assert.Empty(clients.Created);
        }
    }
}