using System.ClientModel;
using System.Text;
using System.Text.Json.Nodes;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Providers;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-044 (REQ-012): the provider chat clients are created per tier from configuration only. Every test runs the real OpenAI SDK, the real
/// Microsoft.Extensions.AI adapter, the real named <see cref="HttpClient"/> pipeline and the real body patch against a
/// <see cref="FakeHttpMessageHandler"/> as the primary handler, so no request leaves the process and the recorded request is what would be on the wire.
/// </summary>
[Trait("Spec", "REQ-012")]
public sealed class ProviderClientTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly ChatMessage[] Hello = [new(ChatRole.User, "hello")];
    private static readonly Tier[] AllTiers = [Tier.Local, Tier.CloudGpu, Tier.Nvidia, Tier.Nebius];

    private static TierOptions TierOf(ProviderClientHost host, Tier tier) => host.Options.Tiers[TierOptions.NameOf(tier)];

    private static async Task<RecordedRequest> SendOneAsync(ProviderClientHost host, Tier tier, string agent = "Planner", int maxOutputTokens = 512)
    {
        TierOptions options = TierOf(host, tier);
        using IChatClient client = host.Factory.Create(tier, options.ResolveModelId(agent));

        int before = host.Handler(tier).Count(FakeRoute.ChatCompletions);

        _ = await client.GetResponseAsync(Hello, ChatOptionsFactory.Create(options, agent, maxOutputTokens, host.Options.Workflow.Seed)).WaitAsync(HangGuard);

        // Exactly one new request per call: the SDK retry is off and nothing else sends.
        IReadOnlyList<RecordedRequest> after = host.Handler(tier).RequestsFor(FakeRoute.ChatCompletions);
        Assert.Equal(before + 1, after.Count);
        return after[^1];
    }

    private static JsonObject BodyOf(RecordedRequest request) => request.Json ?? throw new InvalidOperationException("The recorded request has no JSON body.");

    // ----- criterion 1, 2, 9: the tier's own URL, no SDK retry, the timeouts

    [Fact]
    public async Task Each_tier_uses_its_own_base_url()
    {
        using ProviderClientHost host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers());

        foreach (Tier tier in AllTiers)
        {
            RecordedRequest request = await SendOneAsync(host, tier);

            Uri expected = new(ProviderClientFixtures.BaseUrl(tier));
            Assert.Equal(expected.Host, request.Uri.Host);
            Assert.Equal(Uri.UriSchemeHttps, request.Uri.Scheme);
            Assert.Equal("/v1/chat/completions", request.Uri.AbsolutePath);
            Assert.Equal(TierOptions.NameOf(tier) + ".fake.test", request.Uri.Host);
        }

        // Four distinct hosts, and each fake endpoint saw exactly the request of its own tier.
        Assert.All(AllTiers, tier => Assert.Equal(1, host.Handler(tier).RequestCount));
    }

    [Fact]
    public async Task Sdk_retry_is_disabled()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AlwaysStatus(500);
        using ProviderClientHost host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers(), handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = handler });
        using IChatClient client = host.Factory.Create(Tier.Nvidia, "model-nvidia");

        ClientResultException failure = await Assert.ThrowsAsync<ClientResultException>(() => client.GetResponseAsync(Hello).WaitAsync(HangGuard));

        Assert.Equal(500, failure.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void Http_timeout_infinite_network_timeout_set()
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        using ProviderClientHost host = ProviderClientFixtures.Build(options);

        foreach (Tier tier in AllTiers)
        {
            string name = TierOptions.NameOf(tier);
            using HttpClient http = host.HttpClients.CreateClient(ProviderClientFactory.HttpClientName(name));
            ProviderClientSettings settings = host.Factory.SettingsFor(tier);

            Assert.Equal("aj-tier-" + name, ProviderClientFactory.HttpClientName(name));
            Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
            Assert.Equal(TimeSpan.FromSeconds(options.Tiers[name].TimeoutSeconds), settings.NetworkTimeout);
            Assert.Equal(new Uri(ProviderClientFixtures.BaseUrl(tier)), settings.Endpoint);
            Assert.Equal(0, settings.MaxRetries);
        }

        // The four timeouts differ, so a client cannot pass by reading another tier's value.
        Assert.Equal(4, AllTiers.Select(tier => host.Factory.SettingsFor(tier).NetworkTimeout).Distinct().Count());
    }

    // ----- criterion 3, 4, 8: the request body

    [Fact]
    public async Task Request_body_has_temperature_zero_and_the_task_limit()
    {
        using ProviderClientHost host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers());

        JsonObject body = BodyOf(await SendOneAsync(host, Tier.Nvidia, maxOutputTokens: 512));

        // The exact property names of OpenAI 2.14.0 with Microsoft.Extensions.AI.OpenAI 10.10.1, read from the recorded body.
        Assert.Equal(0.0, body["temperature"]!.GetValue<double>());
        Assert.Equal(512, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal("model-nvidia", body["model"]!.GetValue<string>());
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.False(body.ContainsKey("stream"));
        Assert.False(body.ContainsKey("seed"));
        Assert.True(body["messages"] is JsonArray);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Seed_is_sent_only_when_the_tier_supports_it(bool supportsSeed)
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nebius"].SupportsSeed = supportsSeed;
        options.Workflow.Seed = 1234;
        using ProviderClientHost host = ProviderClientFixtures.Build(options);

        JsonObject body = BodyOf(await SendOneAsync(host, Tier.Nebius));

        Assert.Equal(supportsSeed, body.ContainsKey("seed"));
        if (supportsSeed)
        {
            Assert.Equal(1234, body["seed"]!.GetValue<int>());
        }
    }

    [Theory]
    [InlineData("max_completion_tokens", "max_tokens")]
    [InlineData("max_tokens", "max_completion_tokens")]
    public async Task Max_tokens_field_name_comes_from_configuration(string configured, string other)
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nebius"].MaxTokensField = configured;
        using ProviderClientHost host = ProviderClientFixtures.Build(options);

        JsonObject body = BodyOf(await SendOneAsync(host, Tier.Nebius, maxOutputTokens: 384));

        Assert.Equal(384, body[configured]!.GetValue<int>());
        Assert.False(body.ContainsKey(other));
        Assert.Equal(0.0, body["temperature"]!.GetValue<double>());
    }

    // ----- criterion 5: ExtraBodyJson

    [Fact]
    public async Task Extra_body_is_merged_into_the_completion_request_only()
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nvidia"].ExtraBodyJson = """{"extra_field":"extra-value","nested":{"flag":false}}""";
        using ProviderClientHost host = ProviderClientFixtures.Build(options);

        JsonObject body = BodyOf(await SendOneAsync(host, Tier.Nvidia));
        using HttpClient http = host.HttpClients.CreateClient(ProviderClientFactory.HttpClientName("nvidia"));
        using HttpResponseMessage models = await http.GetAsync(new Uri(ProviderClientFixtures.BaseUrl(Tier.Nvidia) + "/models")).WaitAsync(HangGuard);

        Assert.Equal("extra-value", body["extra_field"]!.GetValue<string>());
        Assert.False(body["nested"]!["flag"]!.GetValue<bool>());
        Assert.Equal(0.0, body["temperature"]!.GetValue<double>());

        Assert.True(models.IsSuccessStatusCode);
        RecordedRequest modelsRequest = Assert.Single(host.Handler(Tier.Nvidia).RequestsFor(FakeRoute.Models));
        Assert.Equal("GET", modelsRequest.Method);
        Assert.Null(modelsRequest.Body);
        Assert.Null(modelsRequest.ContentLength);
    }

    [Fact]
    public async Task Extra_body_of_one_tier_does_not_reach_another_tier()
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nvidia"].ExtraBodyJson = """{"extra_field":"extra-value"}""";
        using ProviderClientHost host = ProviderClientFixtures.Build(options);

        JsonObject nvidia = BodyOf(await SendOneAsync(host, Tier.Nvidia));
        JsonObject nebius = BodyOf(await SendOneAsync(host, Tier.Nebius));

        Assert.True(nvidia.ContainsKey("extra_field"));
        Assert.False(nebius.ContainsKey("extra_field"));
    }

    [Fact]
    public void Unparseable_extra_body_json_is_refused_with_exit_code_2_and_without_echoing_the_text()
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nvidia"].ExtraBodyJson = "{not json";
        using ProviderClientHost host = ProviderClientFixtures.Build(options);

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => host.Factory.Create(Tier.Nvidia, "model-nvidia"));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.DoesNotContain("not json", failure.Message, StringComparison.Ordinal);
    }

    // ----- RequestBodyPatchHandler on its own

    private static async Task<RecordedRequest> PostThroughAsync(string? extra, string field, string body, string path = "/v1/chat/completions")
    {
        var fake = new FakeHttpMessageHandler();
        using var invoker = new HttpMessageInvoker(new RequestBodyPatchHandler(extra, field) { InnerHandler = fake });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://patch.fake.test" + path))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        // The answer is not asserted: the fake answers 404 for a path it has no route for, and the test is about the recorded request.
        using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None).WaitAsync(HangGuard);

        return Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Patch_merges_objects_recursively_and_the_extra_body_wins_scalar_conflicts_and_replaces_arrays()
    {
        const string original = """{"a":{"b":1,"c":2,"deep":{"x":1}},"list":[1,2,3],"keep":true,"scalar":"old"}""";
        const string extra = """{"a":{"c":20,"d":4,"deep":{"y":2}},"list":[9],"scalar":{"now":"object"},"gone":null,"added":"yes"}""";

        JsonObject merged = BodyOf(await PostThroughAsync(extra, "max_completion_tokens", original));

        Assert.Equal(1, merged["a"]!["b"]!.GetValue<int>());
        Assert.Equal(20, merged["a"]!["c"]!.GetValue<int>());
        Assert.Equal(4, merged["a"]!["d"]!.GetValue<int>());
        Assert.Equal(1, merged["a"]!["deep"]!["x"]!.GetValue<int>());
        Assert.Equal(2, merged["a"]!["deep"]!["y"]!.GetValue<int>());
        int[] list = [.. merged["list"]!.AsArray().Select(node => node!.GetValue<int>())];
        Assert.Equal([9], list);
        Assert.True(merged["keep"]!.GetValue<bool>());
        Assert.Equal("object", merged["scalar"]!["now"]!.GetValue<string>());
        Assert.True(merged.ContainsKey("gone"));
        Assert.Null(merged["gone"]);
        Assert.Equal("yes", merged["added"]!.GetValue<string>());
    }

    [Fact]
    public async Task Patch_is_repeatable_because_the_extra_object_is_cloned_per_request()
    {
        var fake = new FakeHttpMessageHandler();
        using var invoker = new HttpMessageInvoker(new RequestBodyPatchHandler("""{"o":{"k":1},"arr":[1]}""", "max_completion_tokens") { InnerHandler = fake });

        for (int i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://patch.fake.test/v1/chat/completions"))
            {
                Content = new StringContent("""{"o":{"j":2}}""", Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None).WaitAsync(HangGuard);
        }

        Assert.Equal(3, fake.RequestCount);
        Assert.All(fake.Requests, recorded => Assert.Equal("""{"o":{"j":2,"k":1},"arr":[1]}""", recorded.Body));
    }

    [Fact]
    public async Task Patch_renames_the_sdk_cap_to_max_tokens_and_the_extra_body_cannot_override_it()
    {
        const string original = """{"model":"m","max_completion_tokens":384}""";

        JsonObject renamed = BodyOf(await PostThroughAsync(null, "max_tokens", original));
        JsonObject noCap = BodyOf(await PostThroughAsync(null, "max_tokens", """{"model":"m"}"""));

        Assert.Equal(384, renamed["max_tokens"]!.GetValue<int>());
        Assert.False(renamed.ContainsKey("max_completion_tokens"));
        Assert.False(noCap.ContainsKey("max_tokens"));
    }

    [Fact]
    public async Task Patch_leaves_every_other_request_untouched()
    {
        const string original = """{"input":"x","max_completion_tokens":5}""";

        RecordedRequest embeddings = await PostThroughAsync("""{"extra":1}""", "max_tokens", original, "/v1/embeddings");
        RecordedRequest suffixOnly = await PostThroughAsync("""{"extra":1}""", "max_tokens", original, "/v1/chat/completions/other");

        Assert.Equal(original, embeddings.Body);
        Assert.Equal(original, suffixOnly.Body);
    }

    [Fact]
    public async Task Patch_ignores_a_query_string_and_keeps_the_media_type_and_a_correct_byte_length()
    {
        string accented = "caf" + char.ConvertFromUtf32(0xE9);
        string original = "{\"text\":\"" + accented + "\"}";

        RecordedRequest recorded = await PostThroughAsync("""{"extra":1}""", "max_completion_tokens", original, "/v1/chat/completions?api-version=1");

        Assert.Equal("application/json", recorded.MediaType);
        Assert.Equal(Encoding.UTF8.GetByteCount(recorded.Body!), recorded.ContentLength);
        Assert.Equal(accented, BodyOf(recorded)["text"]!.GetValue<string>());
        Assert.Equal(1, BodyOf(recorded)["extra"]!.GetValue<int>());
    }

    [Fact]
    public async Task Patch_without_work_does_not_touch_the_body_and_a_non_json_body_fails_without_echoing_it()
    {
        RecordedRequest untouched = await PostThroughAsync(null, "max_completion_tokens", "not json at all");
        Assert.Equal("not json at all", untouched.Body);

        var fake = new FakeHttpMessageHandler();
        using var invoker = new HttpMessageInvoker(new RequestBodyPatchHandler("""{"extra":1}""", "max_completion_tokens") { InnerHandler = fake });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://patch.fake.test/v1/chat/completions"))
        {
            Content = new StringContent("secret prompt text", Encoding.UTF8, "application/json"),
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(request, CancellationToken.None));

        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, fake.RequestCount);
    }

    [Fact]
    public void Patch_handler_refuses_a_bad_extra_object_or_field_name_without_echoing_them()
    {
        ConfigurationException notJson = Assert.Throws<ConfigurationException>(() => new RequestBodyPatchHandler("{secret", "max_tokens"));
        ConfigurationException notObject = Assert.Throws<ConfigurationException>(() => new RequestBodyPatchHandler("[1,2]", "max_tokens"));
        ConfigurationException badField = Assert.Throws<ConfigurationException>(() => new RequestBodyPatchHandler(null, "limit_of_tokens"));

        Assert.DoesNotContain("secret", notJson.Message, StringComparison.Ordinal);
        Assert.Equal(ExitCode.InvalidArguments, notObject.ExitCode);
        Assert.DoesNotContain("limit_of_tokens", badField.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new RequestBodyPatchHandler(null, " "));

        // A lone surrogate escape in a member name (assembled here so the file stays ASCII) is refused like any other bad object, and its text is not shown.
        string loneSurrogate = "{\"a" + "\\u" + "d800" + "\":1}";
        ConfigurationException surrogate = Assert.Throws<ConfigurationException>(() => new RequestBodyPatchHandler(loneSurrogate, "max_tokens"));
        Assert.Equal(ExitCode.InvalidArguments, surrogate.ExitCode);
        Assert.DoesNotContain("d800", surrogate.Message, StringComparison.Ordinal);
        using RequestBodyPatchHandler blank = new("   ", "max_tokens");
        using RequestBodyPatchHandler forAbsentTier = RequestBodyPatchHandler.For(null);
    }

    [Fact]
    public void Patch_applies_on_the_synchronous_send_path_too()
    {
        var inner = new SyncRecordingHandler();
        using var invoker = new HttpMessageInvoker(new RequestBodyPatchHandler("""{"extra":1}""", "max_tokens") { InnerHandler = inner });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://patch.fake.test/v1/chat/completions"))
        {
            Content = new StringContent("""{"max_completion_tokens":7}""", Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = invoker.Send(request, CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode);
        JsonObject body = JsonNode.Parse(inner.Body!)!.AsObject();
        Assert.Equal(7, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(1, body["extra"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_completion_tokens"));
    }

    private sealed class SyncRecordingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    // ----- criterion 6, 7: per-agent model and reasoning mode

    [Fact]
    public void Per_agent_model_id_overrides_the_tier_model_id_and_an_agent_without_an_entry_uses_it()
    {
        var tier = new TierOptions { ModelId = "model-a" };
        tier.AgentModelIds["Writer"] = "model-b";

        Assert.Equal("model-b", tier.ResolveModelId("Writer"));
        Assert.Equal("model-b", tier.ResolveModelId("writer"));
        Assert.Equal("model-a", tier.ResolveModelId("Planner"));
        Assert.Equal("model-a", tier.ResolveModelId("Reviewer"));

        // A dictionary that replaced the default one may compare names with case; the resolution still matches without regard to case.
        var strict = new TierOptions { ModelId = "model-a", AgentModelIds = new Dictionary<string, string>(StringComparer.Ordinal) { ["Writer"] = "model-b" } };
        Assert.Equal("model-b", strict.ResolveModelId("WRITER"));

        // A blank entry is not a model: the tier model is used.
        tier.AgentModelIds["Researcher"] = "  ";
        Assert.Equal("model-a", tier.ResolveModelId("Researcher"));
    }

    [Fact]
    public void Model_resolution_fails_with_exit_code_2_when_nothing_names_a_model()
    {
        var tier = new TierOptions();
        tier.AgentModelIds["Writer"] = "model-b";

        Assert.Equal("model-b", tier.ResolveModelId("Writer"));
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => tier.ResolveModelId("Planner"));
        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Throws<ArgumentException>(() => tier.ResolveModelId(" "));
    }

    [Fact]
    public async Task Per_agent_model_reaches_the_request_body()
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        TierOptions nebius = options.Tiers["nebius"];
        nebius.ModelId = "model-a";
        nebius.AgentModelIds["Writer"] = "model-b";
        using ProviderClientHost host = ProviderClientFixtures.Build(options);

        JsonObject planner = BodyOf(await SendOneAsync(host, Tier.Nebius, "Planner"));
        JsonObject writer = BodyOf(await SendOneAsync(host, Tier.Nebius, "Writer"));

        Assert.Equal("model-a", planner["model"]!.GetValue<string>());
        Assert.Equal("model-b", writer["model"]!.GetValue<string>());
    }

    [Fact]
    public void Chat_options_hold_temperature_zero_the_task_limit_the_resolved_model_and_a_seed_only_when_supported()
    {
        var tier = new TierOptions { ModelId = "model-a", SupportsSeed = true };
        tier.AgentModelIds["Writer"] = "model-b";

        ChatOptions writer = ChatOptionsFactory.Create(tier, "Writer", 768, 42);
        ChatOptions planner = ChatOptionsFactory.Create(tier, "Planner", 512, 42);
        tier.SupportsSeed = false;
        ChatOptions noSeed = ChatOptionsFactory.Create(tier, "Planner", 512, 42);

        Assert.Equal(0f, writer.Temperature);
        Assert.Equal(768, writer.MaxOutputTokens);
        Assert.Equal("model-b", writer.ModelId);
        Assert.Equal(42L, writer.Seed);
        Assert.Equal("model-a", planner.ModelId);
        Assert.Null(noSeed.Seed);
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatOptionsFactory.Create(tier, "Planner", 0, 42));
        Assert.Throws<ArgumentNullException>(() => ChatOptionsFactory.Create(null!, "Planner", 1, 42));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("on")]
    [InlineData("unsupported")]
    public void Reasoning_mode_of_the_environment_is_taken_from_the_tier_configuration(string mode)
    {
        // The shipped nvidia tier carries the reasoning-off toggle (AJ-108); a mode other than off has to replace it, or ReasoningMode would disagree with it.
        string toggle = mode switch
        {
            "on" => """{"chat_template_kwargs":{"enable_thinking":true}}""",
            "unsupported" => "{}",
            _ => """{"chat_template_kwargs":{"enable_thinking":false}}""",
        };
        IConfiguration configuration = ConfigurationFixtures.FromJsonFile(
            ConfigurationFixtures.AppSettingsPath(),
            (AgentJoulesOptions.SectionName + ":Tiers:nvidia:ReasoningMode", mode),
            (AgentJoulesOptions.SectionName + ":Tiers:nvidia:ExtraBodyJson", toggle));

        AgentJoulesOptions options = OptionsLoader.Load(configuration);

        Assert.Equal(mode, TierEnvironmentFacts.From(options.Tiers["nvidia"]).ReasoningMode);
        Assert.Equal("off", TierEnvironmentFacts.From(options.Tiers["nebius"]).ReasoningMode);
    }

    [Fact]
    public void Environment_facts_refuse_a_reasoning_mode_the_validator_would_have_rejected()
    {
        Assert.Throws<ArgumentException>(() => TierEnvironmentFacts.From(new TierOptions { ReasoningMode = "maybe" }));
        Assert.Throws<ArgumentNullException>(() => TierEnvironmentFacts.From(null!));
    }

    // ----- the key

    [Fact]
    public async Task Key_is_read_by_variable_name_and_sent_as_a_bearer_token_but_never_recorded()
    {
        string key = ProviderClientFixtures.FakeKey();
        using ProviderClientHost host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers());

        RecordedRequest request = await SendOneAsync(host, Tier.Nvidia);

        Assert.True(request.HasBearerToken);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        string everything = request.ToString() + request.Body + request.Uri.OriginalString;
        Assert.DoesNotContain(key, everything, StringComparison.Ordinal);
        for (int start = 0; start + 4 <= key.Length; start++)
        {
            Assert.DoesNotContain(key.Substring(start, 4), everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Tier_without_a_key_variable_sends_a_placeholder_and_needs_no_configuration_value()
    {
        using ProviderClientHost host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers(), ConfigurationFixtures.InMemory());

        RecordedRequest local = await SendOneAsync(host, Tier.Local);

        Assert.Equal("Bearer", local.AuthorizationScheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Tier_whose_key_variable_has_no_value_is_refused_with_exit_code_3_before_any_request(string? value)
    {
        IConfiguration configuration = value is null
            ? ConfigurationFixtures.InMemory((ProviderClientFixtures.NebiusKeyVariable, ProviderClientFixtures.FakeKey()))
            : ConfigurationFixtures.InMemory((ProviderClientFixtures.NvidiaKeyVariable, value), (ProviderClientFixtures.NebiusKeyVariable, ProviderClientFixtures.FakeKey()));
        using ProviderClientHost host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers(), configuration);

        MissingKeyException failure = Assert.Throws<MissingKeyException>(() => host.Factory.Create(Tier.Nvidia, "model-nvidia"));

        Assert.Equal(ExitCode.MissingKey, failure.ExitCode);
        Assert.Contains(MissingKeyMessage.UserSecretsAlternative, failure.Message, StringComparison.Ordinal);
        Assert.Equal([ProviderClientFixtures.NvidiaKeyVariable], failure.Variables);
        Assert.Contains(ProviderClientFixtures.NvidiaKeyVariable, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.Handler(Tier.Nvidia).RequestCount);

        // The other hosted tier has its key and is not affected.
        using IChatClient other = host.Factory.Create(Tier.Nebius, "model-nebius");
    }

    [Theory]
    [InlineData("hyphen")]
    [InlineData("lowercase")]
    [InlineData("mixed")]
    [InlineData("digit-first")]
    public void A_key_variable_name_that_is_not_a_name_is_not_echoed_when_its_value_is_missing(string shape)
    {
        string key = ProviderClientFixtures.FakeKey();
        string notAName = shape switch
        {
            "hyphen" => "typed-" + key,
            "lowercase" => key,
            "mixed" => "Typed" + key,
            _ => "9" + key.ToUpperInvariant(),
        };
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nvidia"].ApiKeyVariable = notAName;
        using ProviderClientHost host = ProviderClientFixtures.Build(options, ConfigurationFixtures.InMemory());

        MissingKeyException failure = Assert.Throws<MissingKeyException>(() => host.Factory.Create(Tier.Nvidia, "model-nvidia"));

        Assert.Equal(ExitCode.MissingKey, failure.ExitCode);
        Assert.DoesNotContain(notAName, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(notAName, failure.Variables);
        Assert.DoesNotContain(key, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not shown", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NVIDIA_API_KEY", true)]
    [InlineData("_K1", true)]
    [InlineData("nvidia_api_key", false)]
    [InlineData("A-B", false)]
    [InlineData("1ABC", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Variable_name_rule_is_shared_by_the_validator_and_the_factory(string? value, bool expected)
    {
        Assert.Equal(expected, VariableNames.IsValid(value));
        if (value is not null)
        {
            AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
            options.Tiers["nvidia"].ApiKeyVariable = value;

            // The validator applies the stricter allowlist (a name that ends in _API_KEY, AJ-052); the factory echoes any name that is shaped like one.
            Assert.Equal(VariableNames.IsKeyVariable(value), !ValidatorFailures(options).Any(line => line.Contains("ApiKeyVariable", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void A_key_is_refused_on_plain_http_unless_the_host_is_loopback()
    {
        string key = ProviderClientFixtures.FakeKey();
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nvidia"].BaseUrl = "http://nvidia.fake.test/v1";
        options.Tiers["nebius"].BaseUrl = "http://localhost:8080/v1";
        options.Tiers["local"].BaseUrl = "http://local.fake.test/v1";
        using ProviderClientHost host = ProviderClientFixtures.Build(options);

        ConfigurationException refused = Assert.Throws<ConfigurationException>(() => host.Factory.Create(Tier.Nvidia, "model-nvidia"));

        Assert.Equal(ExitCode.InvalidArguments, refused.ExitCode);
        Assert.Contains("Tiers:nvidia:BaseUrl", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key, refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.Handler(Tier.Nvidia).RequestCount);

        // Loopback with a key, and plain http without a key, are allowed.
        using IChatClient loopback = host.Factory.Create(Tier.Nebius, "model-nebius");
        using IChatClient keyless = host.Factory.Create(Tier.Local, "model-local");
    }

    // ----- the factory's own checks and the registration

    [Fact]
    public void Factory_refuses_an_unconfigured_tier_a_bad_base_url_a_bad_timeout_an_undefined_tier_and_a_blank_model()
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        _ = options.Tiers.Remove("nebius");
        options.Tiers["local"].BaseUrl = null;
        options.Tiers["cloudgpu"].TimeoutSeconds = 0;
        using ProviderClientHost host = ProviderClientFixtures.Build(options);

        ConfigurationException missingTier = Assert.Throws<ConfigurationException>(() => host.Factory.Create(Tier.Nebius, "m"));
        ConfigurationException missingUrl = Assert.Throws<ConfigurationException>(() => host.Factory.Create(Tier.Local, "m"));
        ConfigurationException badTimeout = Assert.Throws<ConfigurationException>(() => host.Factory.Create(Tier.CloudGpu, "m"));

        Assert.Contains("Tiers:nebius", missingTier.Message, StringComparison.Ordinal);
        Assert.Contains("Tiers:local:BaseUrl", missingUrl.Message, StringComparison.Ordinal);
        Assert.Contains("Tiers:cloudgpu:TimeoutSeconds", badTimeout.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Factory.Create((Tier)0, "m"));
        Assert.Throws<ArgumentException>(() => host.Factory.Create(Tier.Nvidia, " "));
    }

    [Fact]
    public void Registration_is_idempotent_by_refusal_and_checks_its_argument()
    {
        var services = new ServiceCollection();
        _ = services.AddAgentJoulesProviderClients();

        Assert.Throws<InvalidOperationException>(() => services.AddAgentJoulesProviderClients());
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddAgentJoulesProviderClients(null!));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ProviderClientFactory));
    }

    // ----- the real SDK parses the fake SSE: usage and time to first token through the metering decorator

    [Fact]
    public async Task Metered_stream_through_the_real_sdk_reports_the_usage_and_the_time_to_the_first_token()
    {
        var handler = new FakeHttpMessageHandler { PromptTokens = 11, CompletionTokens = 2, StreamDeltas = ["Hel", "lo"], ResponseDelay = TimeSpan.FromMilliseconds(20) };
        using ProviderClientHost host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers(), handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = handler });
        var sink = new InMemoryMetricsSink();
        await using var meter = new UnmeteredEnergyMeter(TimeProvider.System);
        await meter.StartAsync(CancellationToken.None);
        var services = new MeteringServices(meter, NoOpMeterGate.Instance, sink, NotConfiguredCostCalculator.Instance, TestInstruments.Create(), TimeProvider.System);
        var identity = new StepIdentity("Planner", "model-nvidia", Tier.Nvidia, "summarise", 1, IsJudge: false);
        using IChatClient client = new ChatClientBuilder(host.Factory.Create(Tier.Nvidia, "model-nvidia")).UseMetering(identity, services).Build();
        StringBuilder text = new();

        using var guard = new CancellationTokenSource(HangGuard);
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Hello).WithCancellation(guard.Token))
        {
            _ = text.Append(update.Text);
        }

        StepReading reading = Assert.Single(sink.Snapshot());
        RecordedRequest request = Assert.Single(handler.RequestsFor(FakeRoute.ChatCompletions));
        Assert.True(request.Json!["stream"]!.GetValue<bool>());
        Assert.Equal("Hello", text.ToString());
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
        Assert.Equal(MetricLabel.Measured, reading.InputTokens.Label);
        Assert.Equal(11.0, reading.InputTokens.Value);
        Assert.Equal(MetricLabel.Measured, reading.OutputTokens.Label);
        Assert.Equal(2.0, reading.OutputTokens.Value);
        Assert.Equal(MetricLabel.Measured, reading.TtftMs.Label);
        Assert.True(reading.TtftMs.Value > 0.0, "the first token arrives after the call started");
    }

    // ----- the NetworkTimeout reaches the SDK (behavioural, criterion 9)

    [Fact]
    public async Task Network_timeout_of_the_tier_ends_a_call_that_the_endpoint_holds_back()
    {
        var handler = new FakeHttpMessageHandler { ResponseDelay = TimeSpan.FromSeconds(20) };
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nvidia"].TimeoutSeconds = 1;
        using ProviderClientHost host = ProviderClientFixtures.Build(options, handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = handler });
        using IChatClient client = host.Factory.Create(Tier.Nvidia, "model-nvidia");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // No token of its own: the only thing that can end this call is the network timeout the factory gave the SDK.
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync(Hello).WaitAsync(HangGuard));

        clock.Stop();
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(10));
        Assert.Equal(1, handler.RequestCount);
    }

    // ----- ExtraBodyJson: duplicates, reserved members, one reading shared by the validator and the handler

    private static string[] ValidatorFailures(AgentJoulesOptions options) =>
        [.. new CrossFieldValidator().Validate(null, options).Failures ?? []];

    [Fact]
    public void Extra_body_with_a_repeated_member_name_is_refused_by_the_validator_and_the_handler_without_echoing_it()
    {
        const string duplicated = """{"member_x":1,"member_x":2}""";
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nvidia"].ExtraBodyJson = duplicated;

        string[] failures = ValidatorFailures(options);
        ConfigurationException refused = Assert.Throws<ConfigurationException>(() => new RequestBodyPatchHandler(duplicated, "max_tokens", "Tiers:nvidia:ExtraBodyJson"));

        string failure = Assert.Single(failures);
        Assert.Contains("Tiers:nvidia:ExtraBodyJson", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("member_x", failure, StringComparison.Ordinal);
        Assert.Contains("repeat", failure, StringComparison.Ordinal);
        Assert.Contains("Tiers:nvidia:ExtraBodyJson", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("member_x", refused.Message, StringComparison.Ordinal);
        Assert.False(ConfigurationJson.TryParseObject(duplicated, out JsonObject? none));
        Assert.Null(none);
        Assert.True(ConfigurationJson.TryParseObject("""{"a":{"b":1},"c":[1,{"b":2}]}""", out JsonObject? fine));
        Assert.NotNull(fine);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("messages")]
    [InlineData("stream")]
    [InlineData("stream_options")]
    [InlineData("temperature")]
    [InlineData("seed")]
    [InlineData("max_completion_tokens")]
    [InlineData("max_tokens")]
    public void Extra_body_may_not_set_a_reserved_member_in_the_validator_or_the_handler(string member)
    {
        string json = "{\"" + member + "\":1}";
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nebius"].ExtraBodyJson = json;

        string failure = Assert.Single(ValidatorFailures(options));
        ConfigurationException refused = Assert.Throws<ConfigurationException>(() => RequestBodyPatchHandler.For(options.Tiers["nebius"], "nebius"));

        Assert.Contains("Tiers:nebius:ExtraBodyJson", failure, StringComparison.Ordinal);
        Assert.Contains(member, failure, StringComparison.Ordinal);
        Assert.Contains("Tiers:nebius:ExtraBodyJson", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ExitCode.InvalidArguments, refused.ExitCode);
    }

    [Fact]
    public void Extra_body_may_set_a_nested_member_with_a_reserved_name()
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nvidia"].ExtraBodyJson = """{"chat_template_kwargs":{"model":"x","stream":true}}""";

        Assert.Empty(ValidatorFailures(options));
        using RequestBodyPatchHandler handler = RequestBodyPatchHandler.For(options.Tiers["nvidia"], "nvidia");

        // The reasoning-off toggle of the design (ADR-12) is the use this setting is for, and nested names of the comparability fields stay allowed.
        options.Tiers["nvidia"].ExtraBodyJson = """{"chat_template_kwargs":{"enable_thinking":false,"temperature":1}}""";
        Assert.Empty(ValidatorFailures(options));
        using RequestBodyPatchHandler reasoningOff = RequestBodyPatchHandler.For(options.Tiers["nvidia"], "nvidia");
    }

    // ----- one endpoint rule for the validator and the factory

    [Theory]
    [InlineData("https://host.fake.test/v1", true, EndpointProblem.None)]
    [InlineData("http://localhost:8080/v1", true, EndpointProblem.None)]
    [InlineData("http://host.fake.test/v1", true, EndpointProblem.PlainHttpOffLoopback)]
    [InlineData("http://host.fake.test/v1", false, EndpointProblem.None)]
    [InlineData("file:///v1", true, EndpointProblem.Shape)]
    [InlineData("file:///v1", false, EndpointProblem.Shape)]
    [InlineData("ftp://host.fake.test/v1", false, EndpointProblem.Shape)]
    [InlineData("https://user:pw@host.fake.test/v1", false, EndpointProblem.Shape)]
    [InlineData("https://host.fake.test/v1?x=1", false, EndpointProblem.Shape)]
    [InlineData("https://host.fake.test/v1#frag", false, EndpointProblem.Shape)]
    public void Endpoint_rule_is_one_function_for_every_caller(string url, bool requireHttps, EndpointProblem expected)
    {
        Assert.Equal(expected, EndpointRules.Check(new Uri(url), requireHttps));
        Assert.Equal(EndpointProblem.Shape, EndpointRules.Check(null, requireHttps));
        Assert.Equal(EndpointProblem.Shape, EndpointRules.Check(new Uri("relative/path", UriKind.Relative), requireHttps));
    }

    [Theory]
    [InlineData("file:///v1")]
    [InlineData("https://user:pw@nvidia.fake.test/v1")]
    [InlineData("https://nvidia.fake.test/v1?api-version=1")]
    public void Factory_refuses_a_base_url_the_validator_refuses_before_any_request(string url)
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Tiers["nvidia"].BaseUrl = url;
        using ProviderClientHost host = ProviderClientFixtures.Build(options);

        ConfigurationException refused = Assert.Throws<ConfigurationException>(() => host.Factory.Create(Tier.Nvidia, "model-nvidia"));
        ConfigurationException settings = Assert.Throws<ConfigurationException>(() => host.Factory.SettingsFor(Tier.Nvidia));
        string[] validator = ValidatorFailures(options);

        Assert.Contains("Tiers:nvidia:BaseUrl", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pw", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Tiers:nvidia:BaseUrl", settings.Message, StringComparison.Ordinal);
        Assert.Contains(validator, line => line.Contains("Tiers:nvidia:BaseUrl", StringComparison.Ordinal));
        Assert.Equal(0, host.Handler(Tier.Nvidia).RequestCount);
    }

    // ----- no redirects by default

    [Fact]
    public void Default_primary_handler_of_every_tier_client_does_not_follow_redirects()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton(ProviderClientFixtures.KeyConfiguration());
        _ = services.AddSingleton<IOptions<AgentJoulesOptions>>(Options.Create(ProviderClientFixtures.FourTiers()));
        _ = services.AddSingleton(new AgentJoules.Core.Security.SecretRedactor());
        _ = services.AddAgentJoulesProviderClients();
        using ServiceProvider provider = services.BuildServiceProvider();
        IOptionsMonitor<HttpClientFactoryOptions> monitor = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        foreach (string tierName in TierOptions.KnownNames)
        {
            string name = ProviderClientFactory.HttpClientName(tierName);
            HttpMessageHandlerBuilder builder = provider.GetRequiredService<HttpMessageHandlerBuilder>();
            builder.Name = name;
            foreach (Action<HttpMessageHandlerBuilder> action in monitor.Get(name).HttpMessageHandlerBuilderActions)
            {
                action(builder);
            }

            using SocketsHttpHandler primary = Assert.IsType<SocketsHttpHandler>(builder.PrimaryHandler);
            Assert.False(primary.AllowAutoRedirect);

            // The body patch of the tier and the detection of an in-stream error (AJ-104), in this order.
            Assert.Collection(
                builder.AdditionalHandlers,
                static first => Assert.IsType<RequestBodyPatchHandler>(first),
                static second => Assert.IsType<StreamErrorDetectingHandler>(second));
        }
    }

    // ----- the tier names

    [Fact]
    public void Tier_name_helper_matches_the_json_spelling_of_every_tier()
    {
        foreach (Tier tier in AllTiers)
        {
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(tier), "\"" + TierOptions.NameOf(tier) + "\"");
        }

        Assert.Equal(TierOptions.KnownNames, AllTiers.Select(TierOptions.NameOf));
    }
}