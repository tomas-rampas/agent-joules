using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AgentJoules.Cli;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>AJ-043 (REQ-012): the tier sections bind from the committed file, every key is overridable, and an invalid tier configuration exits 2 naming the key.</summary>
[Trait("Spec", "REQ-012")]
public sealed class TierConfigurationTests
{
    private static readonly string[] TierNames = ["local", "cloudgpu", "nvidia", "nebius"];

    private static string Key(string suffix) => AgentJoulesOptions.SectionName + ConfigurationPath.KeyDelimiter + suffix;

    // Keys are given relative to the AgentJoules section, as the story writes them (Tiers:nvidia:ModelId).
    private static IConfiguration OnTopOfTheFile(params (string Key, string Value)[] pairs) =>
        ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath(), [.. pairs.Select(pair => (Key(pair.Key), pair.Value))]);

    private static IConfiguration Alone(params (string Key, string Value)[] pairs) =>
        ConfigurationFixtures.InMemory([.. pairs.Select(pair => (Key(pair.Key), pair.Value))]);

    // The committed file, edited and read back as JSON: a removed key is absent, which an in-memory null does not achieve for a key bound from a file.
    private static IConfiguration EditedFile(Action<JsonObject> edit)
    {
        JsonObject root = JsonNode.Parse(File.ReadAllText(ConfigurationFixtures.AppSettingsPath()))!.AsObject();
        edit(root[AgentJoulesOptions.SectionName]!.AsObject());

        var stream = new MemoryStream(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(root.ToJsonString()));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    private static ConfigurationException Rejected(IConfiguration configuration) =>
        Assert.Throws<ConfigurationException>(() => OptionsLoader.Load(configuration));

    private static ConfigurationException Rejected(params (string Key, string Value)[] pairs) => Rejected(OnTopOfTheFile(pairs));

    private static void AssertNames(ConfigurationException failure, string key, string valueThatMustNotAppear)
    {
        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal(2, (int)failure.ExitCode);
        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(valueThatMustNotAppear, failure.Message, StringComparison.Ordinal);
    }

    // A value that looks like a key is built at run time, so no key pattern is committed.
    private static string KeyLikeValue() => string.Concat("nvapi-", Guid.NewGuid().ToString("N"));

    // ---- criterion 1: the committed file ----

    [Fact]
    public void Committed_config_binds()
    {
        AgentJoulesOptions options = OptionsLoader.Load(ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath()));

        Assert.Equal(4, options.Tiers.Count);
        foreach (string name in TierNames)
        {
            Assert.True(options.Tiers.TryGetValue(name, out TierOptions? tier), "Tiers:" + name + " was not bound");
            Assert.False(string.IsNullOrWhiteSpace(tier.BaseUrl), "Tiers:" + name + ":BaseUrl is empty");
            Assert.True(Uri.TryCreate(tier.BaseUrl, UriKind.Absolute, out _), "Tiers:" + name + ":BaseUrl is not absolute");
            Assert.False(string.IsNullOrWhiteSpace(tier.ModelId), "Tiers:" + name + ":ModelId is empty");
            Assert.Equal("off", tier.ReasoningMode);
            Assert.Empty(tier.AgentModelIds);
        }

        Assert.False(options.Tiers["cloudgpu"].Enabled);
        Assert.True(options.Tiers["local"].Enabled);
        Assert.True(options.Tiers["nvidia"].Enabled);
        Assert.True(options.Tiers["nebius"].Enabled);

        Assert.Null(options.Tiers["local"].ApiKeyVariable);
        Assert.Null(options.Tiers["cloudgpu"].ApiKeyVariable);
        Assert.Equal("NVIDIA_API_KEY", options.Tiers["nvidia"].ApiKeyVariable);
        Assert.Equal("NEBIUS_API_KEY", options.Tiers["nebius"].ApiKeyVariable);
        Assert.Equal("FP8", options.Tiers["cloudgpu"].Precision);
        Assert.Equal("free-tier-no-price", options.Tiers["nvidia"].MissingPriceReason);

        Assert.Equal("nvidia", options.Judge.Tier);
        Assert.False(string.IsNullOrWhiteSpace(options.Judge.ModelId));
        Assert.Equal(42, options.Workflow.Seed);
    }

    [Fact]
    public void The_committed_file_holds_the_four_tier_sections_in_the_fixed_order()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ConfigurationFixtures.AppSettingsPath()));
        JsonElement root = document.RootElement.GetProperty(AgentJoulesOptions.SectionName);

        Assert.Equal(TierNames, root.GetProperty("Tiers").EnumerateObject().Select(property => property.Name));

        // The existing sections stay where AJ-033 put them.
        Assert.Equal(0, root.GetProperty("Gpu").GetProperty("Index").GetInt32());
        Assert.Equal("auto", root.GetProperty("Meter").GetProperty("Method").GetString());
        Assert.Equal(30.0, root.GetProperty("Baseline").GetProperty("Seconds").GetDouble());

        foreach (string name in TierNames)
        {
            JsonElement tier = root.GetProperty("Tiers").GetProperty(name);
            foreach (string key in new[] { "BaseUrl", "ModelId", "AgentModelIds", "Enabled", "ReasoningMode" })
            {
                Assert.True(tier.TryGetProperty(key, out _), "Tiers:" + name + ":" + key + " is missing from the committed file");
            }
        }
    }

    [Fact]
    public void The_committed_file_holds_only_variable_names_never_a_key_value()
    {
        string text = File.ReadAllText(ConfigurationFixtures.AppSettingsPath());
        using JsonDocument document = JsonDocument.Parse(text);

        // Criterion 5: the only property whose name mentions a key is ApiKeyVariable, and its value is the NAME of an environment variable.
        List<(string Name, JsonElement Value)> keyProperties = [];
        Collect(document.RootElement, keyProperties);

        Assert.NotEmpty(keyProperties);
        foreach ((string name, JsonElement value) in keyProperties)
        {
            Assert.Equal("ApiKeyVariable", name);
            Assert.Matches("^[A-Z][A-Z0-9_]*$", value.GetString());
        }

        Assert.DoesNotContain("nvapi-", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", text, StringComparison.OrdinalIgnoreCase);

        static void Collect(JsonElement element, List<(string Name, JsonElement Value)> found)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add((property.Name, property.Value));
                }

                Collect(property.Value, found);
            }
        }
    }

    // ---- criterion 4: no endpoint URL in source ----

    [Fact]
    public void No_endpoint_url_is_written_in_source()
    {
        // The criterion 4 pipeline, grep -rnE "\"https?://" src --include=*.cs | grep -vE "www\.w3\.org/2000/svg|json-schema\.org", as a file scan.
        // The folder and its files must exist: a scan of nothing would pass.
        string source = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "src");
        Assert.True(Directory.Exists(source), "src does not exist");
        string[] files = [.. Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)];
        Assert.NotEmpty(files);

        var url = new Regex("\"https?://", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        var allowed = new Regex(@"www\.w3\.org/2000/svg|json-schema\.org", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

        string[] offenders =
        [
            .. files.SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (line, index))
                .Where(entry => url.IsMatch(entry.line) && !allowed.IsMatch(entry.line))
                .Select(entry => Path.GetRelativePath(source, file) + ":" + (entry.index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))),
        ];

        Assert.Empty(offenders);
    }

    // ---- criterion 2: overrides ----

    [Fact]
    public void An_environment_style_key_changes_the_bound_model_id()
    {
        const string name = "AGENTJOULES__Tiers__nvidia__ModelId";
        IConfiguration configuration = ConfigurationFixtures.FromJsonFile(
            ConfigurationFixtures.AppSettingsPath(),
            (ConfigurationFixtures.NormaliseEnvironmentKey(name), "model-b"));

        AgentJoulesOptions options = OptionsLoader.Load(configuration);

        Assert.Equal("model-b", options.Tiers["nvidia"].ModelId);

        // The rest of the file is untouched.
        Assert.Equal(OptionsLoader.Load(ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath())).Tiers["nebius"].ModelId, options.Tiers["nebius"].ModelId);
        Assert.Equal(4, options.Tiers.Count);
    }

    [Fact]
    public void An_environment_style_key_enables_the_cloudgpu_tier()
    {
        IConfiguration configuration = ConfigurationFixtures.FromJsonFile(
            ConfigurationFixtures.AppSettingsPath(),
            (ConfigurationFixtures.NormaliseEnvironmentKey("AGENTJOULES__Tiers__cloudgpu__Enabled"), "true"));

        Assert.True(OptionsLoader.Load(configuration).Tiers["cloudgpu"].Enabled);
    }

    [Theory]
    [InlineData("AGENTJOULES__Tiers__nvidia__ReasoningMode", "on")]
    [InlineData("agentjoules__tiers__nvidia__reasoningmode", "on")]
    [InlineData("AGENTJOULES__Tiers__NVIDIA__ReasoningMode", "on")]
    public void An_environment_style_key_matches_without_regard_to_case(string name, string value)
    {
        // The shipped nvidia tier carries the reasoning-off toggle (AJ-108), and ReasoningMode must agree with it: the toggle is overridden through the same spelling of the key, so the two keys prove the case rule together.
        string toggleName = name.Replace("ReasoningMode", "ExtraBodyJson", StringComparison.OrdinalIgnoreCase);
        IConfiguration configuration = ConfigurationFixtures.FromJsonFile(
            ConfigurationFixtures.AppSettingsPath(),
            (ConfigurationFixtures.NormaliseEnvironmentKey(name), value),
            (ConfigurationFixtures.NormaliseEnvironmentKey(toggleName), """{"chat_template_kwargs":{"enable_thinking":true}}"""));

        AgentJoulesOptions options = OptionsLoader.Load(configuration);

        Assert.Equal(4, options.Tiers.Count);
        Assert.Equal("on", options.Tiers["nvidia"].ReasoningMode);
    }

    [Fact]
    public void Every_scalar_key_of_a_tier_is_overridable()
    {
        AgentJoulesOptions options = OptionsLoader.Load(OnTopOfTheFile(
            ("Tiers:nvidia:BaseUrl", "https://example.test/v1"),
            ("Tiers:nvidia:ModelId", "model-c"),
            ("Tiers:nvidia:ApiKeyVariable", "OTHER_API_KEY"),
            ("Tiers:nvidia:Enabled", "false"),
            ("Tiers:nvidia:Precision", "BF16"),
            ("Tiers:nvidia:ReasoningMode", "unsupported"),
            ("Tiers:nvidia:ExtraBodyJson", "{\"a\":1}"),
            ("Tiers:nvidia:SystemPromptPrefix", "Be brief."),
            ("Tiers:nvidia:SupportsSeed", "true"),
            ("Tiers:nvidia:MissingPriceReason", "no-row"),
            ("Tiers:nvidia:TimeoutSeconds", "12"),
            ("Tiers:nvidia:MaxTokensField", "max_tokens"),
            ("Tiers:nvidia:AgentModelIds:Writer", "writer-model"),
            ("Judge:Tier", "nebius"),
            ("Judge:ModelId", "judge-model"),
            ("Workflow:Seed", "7")));

        TierOptions tier = options.Tiers["nvidia"];
        Assert.Equal("https://example.test/v1", tier.BaseUrl);
        Assert.Equal("model-c", tier.ModelId);
        Assert.Equal("OTHER_API_KEY", tier.ApiKeyVariable);
        Assert.False(tier.Enabled);
        Assert.Equal("BF16", tier.Precision);
        Assert.Equal("unsupported", tier.ReasoningMode);
        Assert.Equal("{\"a\":1}", tier.ExtraBodyJson);
        Assert.Equal("Be brief.", tier.SystemPromptPrefix);
        Assert.True(tier.SupportsSeed);
        Assert.Equal("no-row", tier.MissingPriceReason);
        Assert.Equal(12, tier.TimeoutSeconds);
        Assert.Equal("max_tokens", tier.MaxTokensField);
        Assert.Equal("writer-model", tier.AgentModelIds["Writer"]);
        Assert.Equal("writer-model", tier.AgentModelIds["writer"]);
        Assert.Equal("nebius", options.Judge.Tier);
        Assert.Equal("judge-model", options.Judge.ModelId);
        Assert.Equal(7, options.Workflow.Seed);
    }

    // ---- the option types ----

    [Fact]
    public void A_new_options_object_holds_the_documented_tier_defaults_and_validates()
    {
        var options = new AgentJoulesOptions();

        Assert.Empty(options.Tiers);
        Assert.Equal("nvidia", options.Judge.Tier);
        Assert.Null(options.Judge.ModelId);
        Assert.Equal(42, options.Workflow.Seed);
        OptionsLoader.ValidateOrThrow(options);

        var tier = new TierOptions();
        Assert.Null(tier.BaseUrl);
        Assert.Null(tier.ModelId);
        Assert.Empty(tier.AgentModelIds);
        Assert.Null(tier.ApiKeyVariable);
        Assert.True(tier.Enabled);
        Assert.Null(tier.Precision);
        Assert.Equal("off", tier.ReasoningMode);
        Assert.Null(tier.ExtraBodyJson);
        Assert.Null(tier.SystemPromptPrefix);
        Assert.False(tier.SupportsSeed);
        Assert.Equal("price-not-found", tier.MissingPriceReason);
        Assert.Equal(300, tier.TimeoutSeconds);
        Assert.Equal("max_completion_tokens", tier.MaxTokensField);
    }

    [Fact]
    public void The_tier_dictionaries_match_names_without_regard_to_case()
    {
        var options = new AgentJoulesOptions();
        options.Tiers["NVIDIA"] = new TierOptions();
        options.Tiers["nvidia"].AgentModelIds["PLANNER"] = "model-a";

        Assert.Single(options.Tiers);
        Assert.Equal("model-a", options.Tiers["Nvidia"].AgentModelIds["planner"]);
    }

    [Theory]
    [InlineData(typeof(TierOptions))]
    [InlineData(typeof(JudgeOptions))]
    [InlineData(typeof(WorkflowOptions))]
    public void The_tier_option_types_are_sealed_mutable_classes_with_plain_get_and_set(Type type)
    {
        Assert.True(type.IsClass);
        Assert.True(type.IsSealed);
        Assert.Null(type.GetMethod("<Clone>$"));
        Assert.Null(type.GetCustomAttribute<RequiredMemberAttribute>());

        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(properties);
        foreach (PropertyInfo property in properties)
        {
            Assert.NotNull(property.GetMethod);
            MethodInfo? setter = property.SetMethod;
            Assert.NotNull(setter);
            Assert.True(setter.IsPublic, property.Name + " needs a public setter");
            Assert.DoesNotContain(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
            Assert.Null(property.GetCustomAttribute<RequiredMemberAttribute>());
        }
    }

    [Fact]
    public void The_root_options_hold_tiers_judge_and_workflow_with_plain_get_and_set()
    {
        foreach (string name in new[] { "Tiers", "Judge", "Workflow" })
        {
            PropertyInfo property = typeof(AgentJoulesOptions).GetProperty(name)!;
            Assert.NotNull(property);
            Assert.True(property.SetMethod!.IsPublic);
            Assert.DoesNotContain(typeof(IsExternalInit), property.SetMethod.ReturnParameter.GetRequiredCustomModifiers());
        }

        Assert.Equal(typeof(Dictionary<string, TierOptions>), typeof(AgentJoulesOptions).GetProperty("Tiers")!.PropertyType);
    }

    [Fact]
    public void The_known_tier_names_are_the_serialized_names_of_the_tier_enum_in_run_order()
    {
        string[] fromEnum = [.. Enum.GetValues<Tier>().Order().Select(tier => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(tier))!)];

        Assert.Equal(fromEnum, TierOptions.KnownNames);
        Assert.Equal(TierNames, TierOptions.KnownNames);
    }

    [Fact]
    public void The_typed_key_list_of_a_tier_covers_every_non_string_scalar_option()
    {
        string[] expected =
        [
            .. typeof(TierOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType != typeof(string) && !property.PropertyType.IsClass)
                .Select(property => property.Name),
        ];

        Assert.NotEmpty(expected);
        Assert.Equal(expected.Order(StringComparer.Ordinal), ConfigurationShape.TierTypedKeyNames.Order(StringComparer.Ordinal));
    }

    // ---- criterion 3: invalid configuration exits 2 and names the key ----

    [Theory]
    [InlineData("maybe")]
    [InlineData("ON")]
    [InlineData(" off")]
    public void A_reasoning_mode_outside_off_on_unsupported_is_rejected_naming_the_key(string mode)
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:ReasoningMode", mode));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("Tiers:nvidia:ReasoningMode must be off, on or unsupported; the value is not shown", failure.Message);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("on")]
    [InlineData("unsupported")]
    public void Each_reasoning_mode_word_validates(string mode)
    {
        // The shipped nvidia tier carries the reasoning-off toggle (AJ-108); a mode other than off has to replace it, or ReasoningMode would disagree with it.
        string toggle = mode switch
        {
            "on" => """{"chat_template_kwargs":{"enable_thinking":true}}""",
            "unsupported" => "{}",
            _ => """{"chat_template_kwargs":{"enable_thinking":false}}""",
        };

        Assert.Equal(mode, OptionsLoader.Load(OnTopOfTheFile(("Tiers:nvidia:ReasoningMode", mode), ("Tiers:nvidia:ExtraBodyJson", toggle))).Tiers["nvidia"].ReasoningMode);
    }

    [Fact]
    public void An_enabled_tier_without_a_base_url_is_rejected_naming_the_key()
    {
        ConfigurationException failure = Rejected(EditedFile(section => Assert.True(section["Tiers"]!["nebius"]!.AsObject().Remove("BaseUrl"))));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("Tiers:nebius:BaseUrl must be set when the tier is enabled", failure.Message);
    }

    [Fact]
    public void An_enabled_tier_without_a_model_id_is_rejected_naming_the_key()
    {
        ConfigurationException failure = Rejected(EditedFile(section => Assert.True(section["Tiers"]!["nvidia"]!.AsObject().Remove("ModelId"))));

        Assert.Equal("Tiers:nvidia:ModelId must be set when the tier is enabled", failure.Message);
    }

    [Fact]
    public void A_tier_that_is_only_declared_is_enabled_and_so_needs_a_base_url_and_a_model_id()
    {
        ConfigurationException failure = Rejected(Alone(("Tiers:local:Precision", "FP8")));

        Assert.Contains("Tiers:local:BaseUrl", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Tiers:local:ModelId", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_tier_needs_neither_a_base_url_nor_a_model_id()
    {
        AgentJoulesOptions options = OptionsLoader.Load(EditedFile(section =>
        {
            JsonObject cloudgpu = section["Tiers"]!["cloudgpu"]!.AsObject();
            Assert.True(cloudgpu.Remove("BaseUrl"));
            Assert.True(cloudgpu.Remove("ModelId"));
        }));

        Assert.False(options.Tiers["cloudgpu"].Enabled);
        Assert.Null(options.Tiers["cloudgpu"].BaseUrl);
    }

    [Fact]
    public void A_disabled_tier_with_a_base_url_must_still_hold_a_valid_one()
    {
        ConfigurationException failure = Rejected(("Tiers:cloudgpu:BaseUrl", "not a url"));

        AssertNames(failure, "Tiers:cloudgpu:BaseUrl", "not a url");
    }

    [Theory]
    [InlineData("/v1")]
    [InlineData("localhost:11434/v1")]
    [InlineData("ftp://example.test/v1")]
    [InlineData("example.test")]
    public void A_base_url_that_is_not_an_absolute_http_or_https_url_is_rejected_naming_the_key(string baseUrl)
    {
        ConfigurationException failure = Rejected(("Tiers:nebius:BaseUrl", baseUrl));

        AssertNames(failure, "Tiers:nebius:BaseUrl", baseUrl);
        Assert.Equal("Tiers:nebius:BaseUrl must be an absolute http or https URL without user info, query or fragment; the value is not shown", failure.Message);
    }

    [Theory]
    [InlineData("https://user@example.test/v1")]
    [InlineData("https://user:secret@example.test/v1")]
    [InlineData("https://example.test/v1?api=1")]
    [InlineData("https://example.test/v1#part")]
    public void A_base_url_with_user_info_a_query_or_a_fragment_is_rejected_without_echoing_it(string baseUrl)
    {
        ConfigurationException failure = Rejected(("Tiers:nebius:BaseUrl", baseUrl));

        AssertNames(failure, "Tiers:nebius:BaseUrl", baseUrl);
        Assert.Equal("Tiers:nebius:BaseUrl must be an absolute http or https URL without user info, query or fragment; the value is not shown", failure.Message);
    }

    [Fact]
    public void A_key_pasted_as_the_user_info_of_a_base_url_is_never_echoed()
    {
        string key = KeyLikeValue();

        ConfigurationException failure = Rejected(("Tiers:nebius:BaseUrl", "https://" + key + "@example.test/v1"));

        AssertNames(failure, "Tiers:nebius:BaseUrl", key);
    }

    [Theory]
    [InlineData("http://example.test/v1")]
    [InlineData("http://203.0.113.7:8000/v1")]
    public void Plain_http_to_a_host_that_is_not_loopback_is_rejected_naming_the_key(string baseUrl)
    {
        ConfigurationException failure = Rejected(("Tiers:nebius:BaseUrl", baseUrl));

        AssertNames(failure, "Tiers:nebius:BaseUrl", baseUrl);
        Assert.Equal("Tiers:nebius:BaseUrl must be https unless the host is loopback", failure.Message);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:8000/v1")]
    [InlineData("http://[::1]:8000/v1")]
    [InlineData("https://example.test/v1")]
    public void Https_and_plain_http_to_a_loopback_host_are_accepted(string baseUrl)
    {
        Assert.Equal(baseUrl, OptionsLoader.Load(OnTopOfTheFile(("Tiers:nebius:BaseUrl", baseUrl))).Tiers["nebius"].BaseUrl);
    }

    [Fact]
    public void A_judge_tier_naming_an_unknown_tier_is_rejected_naming_the_key()
    {
        ConfigurationException failure = Rejected(("Judge:Tier", "bogus"));

        AssertNames(failure, "Judge:Tier", "bogus");
    }

    [Fact]
    public void A_judge_tier_that_is_known_but_not_configured_is_rejected_when_tiers_holds_entries()
    {
        ConfigurationException failure = Rejected(Alone(
            ("Tiers:local:BaseUrl", "https://example.test/v1"),
            ("Tiers:local:ModelId", "model-a"),
            ("Judge:Tier", "nebius")));

        Assert.Equal("Judge:Tier names a tier that Tiers does not hold", failure.Message);
    }

    [Fact]
    public void A_known_judge_tier_validates_while_no_tier_is_configured_at_all()
    {
        Assert.Equal("local", OptionsLoader.Load(Alone(("Judge:Tier", "local"))).Judge.Tier);
    }

    [Fact]
    public void A_judge_tier_of_a_disabled_tier_validates()
    {
        Assert.Equal("cloudgpu", OptionsLoader.Load(OnTopOfTheFile(("Judge:Tier", "cloudgpu"))).Judge.Tier);
    }

    [Fact]
    public void An_unknown_tier_section_name_is_rejected_naming_the_key_path()
    {
        ConfigurationException failure = Rejected(("Tiers:bogus:BaseUrl", "https://example.test/v1"));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("Tiers:bogus is not a known tier; use local, cloudgpu, nvidia or nebius", failure.Message);
    }

    [Fact]
    public void An_unknown_tier_section_name_is_rejected_by_the_validator_too_for_an_object_built_in_code()
    {
        var options = new AgentJoulesOptions();
        options.Tiers["bogus"] = new TierOptions { BaseUrl = "https://example.test/v1", ModelId = "model-a" };

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.ValidateOrThrow(options));

        Assert.Equal("Tiers:bogus is not a known tier; use local, cloudgpu, nvidia or nebius", failure.Message);
    }

    [Fact]
    public void A_hostile_tier_name_carries_no_control_character_into_the_message()
    {
        var options = new AgentJoulesOptions();
        options.Tiers[string.Concat((char)27, "[31m", (char)10, "name")] = new TierOptions();

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.ValidateOrThrow(options));

        Assert.DoesNotContain((char)27, failure.Message);
        Assert.Single(failure.Message.Split('\n'));
    }

    [Fact]
    public void A_null_tier_entry_is_rejected_naming_the_key()
    {
        var options = new AgentJoulesOptions();
        options.Tiers["nvidia"] = null!;

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.ValidateOrThrow(options));

        Assert.Contains("Tiers:nvidia", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("{\"a\":")]
    public void An_extra_body_that_is_not_a_json_object_is_rejected_naming_the_key(string json)
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:ExtraBodyJson", json));

        AssertNames(failure, "Tiers:nvidia:ExtraBodyJson", json);
        Assert.Equal("Tiers:nvidia:ExtraBodyJson must be a JSON object; the value is not shown", failure.Message);
    }

    [Fact]
    public void An_extra_body_that_is_a_json_object_validates()
    {
        const string json = "{\"chat_template_kwargs\":{\"enable_thinking\":false}}";

        Assert.Equal(json, OptionsLoader.Load(OnTopOfTheFile(("Tiers:nvidia:ExtraBodyJson", json))).Tiers["nvidia"].ExtraBodyJson);
    }

    [Theory]
    [InlineData("max_output")]
    [InlineData("MAX_TOKENS")]
    public void A_max_tokens_field_outside_the_two_names_is_rejected_naming_the_key(string field)
    {
        ConfigurationException failure = Rejected(("Tiers:nebius:MaxTokensField", field));

        AssertNames(failure, "Tiers:nebius:MaxTokensField", field);
        Assert.Equal("Tiers:nebius:MaxTokensField must be max_completion_tokens or max_tokens; the value is not shown", failure.Message);
    }

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("max_completion_tokens")]
    public void Each_max_tokens_field_name_validates(string field)
    {
        Assert.Equal(field, OptionsLoader.Load(OnTopOfTheFile(("Tiers:nebius:MaxTokensField", field))).Tiers["nebius"].MaxTokensField);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_timeout_of_zero_or_less_is_rejected_naming_the_key(string seconds)
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:TimeoutSeconds", seconds));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("Tiers:nvidia:TimeoutSeconds must be greater than 0", failure.Message);
    }

    [Theory]
    [InlineData("Tiers:nvidia:TimeoutSeconds", "soon", "an integer")]
    [InlineData("Tiers:nvidia:TimeoutSeconds", "1.5", "an integer")]
    [InlineData("Tiers:nvidia:Enabled", "maybe", "true or false")]
    [InlineData("Tiers:nvidia:SupportsSeed", "yes", "true or false")]
    [InlineData("Workflow:Seed", "forty-two", "an integer")]
    public void A_value_of_the_wrong_type_names_the_key_and_never_echoes_the_value(string key, string value, string expectedType)
    {
        ConfigurationException failure = Rejected((key, value));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal($"{key} must be {expectedType}; the value is not shown", failure.Message);
    }

    [Fact]
    public void An_unknown_key_under_a_tier_is_rejected_naming_the_key_path()
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:Bogus", "x"));

        Assert.Equal("Tiers:nvidia:Bogus is not a known option; check the spelling", failure.Message);
    }

    [Fact]
    public void A_tier_key_that_is_set_but_empty_is_rejected_naming_the_key()
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:ModelId", string.Empty));

        Assert.Equal("Tiers:nvidia:ModelId is set but empty", failure.Message);
    }

    [Fact]
    public void A_value_given_for_a_whole_tier_is_rejected()
    {
        ConfigurationException failure = Rejected(Alone(("Tiers:nvidia", "x")));

        Assert.Equal("Tiers:nvidia is a section and must hold keys, not a value; the value is not shown", failure.Message);
    }

    [Fact]
    public void A_value_given_for_the_whole_tiers_section_is_rejected()
    {
        ConfigurationException failure = Rejected(Alone(("Tiers", "x")));

        Assert.Equal("Tiers is a section and must hold keys, not a value; the value is not shown", failure.Message);
    }

    [Fact]
    public void A_section_given_for_a_single_tier_value_is_rejected()
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:ModelId:Inner", "x"));

        Assert.Equal("Tiers:nvidia:ModelId must be a single value, not a section", failure.Message);
    }

    [Fact]
    public void An_agent_model_id_for_a_known_agent_binds()
    {
        AgentJoulesOptions options = OptionsLoader.Load(OnTopOfTheFile(
            ("Tiers:nvidia:AgentModelIds:Planner", "planner-model"),
            ("Tiers:nvidia:AgentModelIds:Reviewer", "reviewer-model")));

        Assert.Equal(2, options.Tiers["nvidia"].AgentModelIds.Count);
        Assert.Equal("planner-model", options.Tiers["nvidia"].AgentModelIds["Planner"]);
    }

    [Fact]
    public void An_agent_model_id_for_an_unknown_agent_is_rejected_naming_the_key_path()
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:AgentModelIds:Judge", "model-a"));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("Tiers:nvidia:AgentModelIds:Judge is not a known agent; use Planner, Researcher, Writer or Reviewer", failure.Message);
    }

    [Fact]
    public void An_empty_agent_model_id_is_rejected_naming_the_key_path()
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:AgentModelIds:Writer", string.Empty));

        Assert.Equal("Tiers:nvidia:AgentModelIds:Writer is set but empty", failure.Message);
    }

    [Fact]
    public void A_blank_agent_model_id_in_an_object_built_in_code_is_rejected_naming_the_key_path()
    {
        var options = new AgentJoulesOptions();
        options.Tiers["nvidia"] = new TierOptions { BaseUrl = "https://example.test/v1", ModelId = "model-a" };
        options.Tiers["nvidia"].AgentModelIds["Writer"] = "  ";

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.ValidateOrThrow(options));

        Assert.Equal("Tiers:nvidia:AgentModelIds:Writer must be set", failure.Message);
    }

    [Fact]
    public void A_section_given_for_an_agent_model_id_is_rejected()
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:AgentModelIds:Writer:Inner", "x"));

        Assert.Equal("Tiers:nvidia:AgentModelIds:Writer must be a single value, not a section", failure.Message);
    }

    [Theory]
    [InlineData("has-a-hyphen-01234")]
    [InlineData("has space")]
    [InlineData("1STARTS_WITH_DIGIT")]
    public void An_api_key_variable_that_is_not_a_variable_name_is_rejected_without_echoing_it(string value)
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:ApiKeyVariable", value));

        AssertNames(failure, "Tiers:nvidia:ApiKeyVariable", value);
    }

    [Fact]
    public void A_key_like_value_in_any_checked_field_is_never_echoed()
    {
        foreach (string key in new[]
        {
            "Tiers:nvidia:ReasoningMode",
            "Tiers:nvidia:BaseUrl",
            "Tiers:nvidia:ExtraBodyJson",
            "Tiers:nvidia:MaxTokensField",
            "Tiers:nvidia:ApiKeyVariable",
            "Tiers:nvidia:TimeoutSeconds",
            "Tiers:nvidia:Enabled",
            "Judge:Tier",
            "Workflow:Seed",
        })
        {
            string value = KeyLikeValue();

            ConfigurationException failure = Rejected((key, value));

            AssertNames(failure, key, value);
        }
    }

    [Fact]
    public void Several_tier_failures_are_reported_together_one_line_each()
    {
        ConfigurationException failure = Rejected(
            ("Tiers:nvidia:ReasoningMode", "maybe"),
            ("Tiers:nebius:MaxTokensField", "x"),
            ("Tiers:local:TimeoutSeconds", "0"),
            ("Judge:Tier", "bogus"));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        string[] lines = failure.Message.Split('\n');
        Assert.Equal(4, lines.Length);
        foreach (string key in new[] { "Tiers:nvidia:ReasoningMode", "Tiers:nebius:MaxTokensField", "Tiers:local:TimeoutSeconds", "Judge:Tier" })
        {
            Assert.Single(lines, line => line.Contains(key, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_two_validators_each_hold_their_part_of_the_tier_rules()
    {
        var options = new AgentJoulesOptions();
        options.Tiers["nvidia"] = new TierOptions { ReasoningMode = "maybe" };

        // The flat validator has nothing to say about a dictionary; the cross-field validator reports the tier once.
        Assert.True(new FlatSectionsValidator().Validate(null, options).Succeeded);
        var cross = new CrossFieldValidator().Validate(null, options);

        Assert.False(cross.Succeeded);
        Assert.Contains(cross.Failures!, line => line.Contains("Tiers:nvidia:ReasoningMode", StringComparison.Ordinal));
    }

    // ---- the exit 2 path through the real CLI host (T5) ----

    private static async Task<CliRunResult> ProbeAsync(Action<JsonObject>? edit)
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        if (edit is not null)
        {
            JsonObject root = JsonNode.Parse(File.ReadAllText(workspace.ConfigPath))!.AsObject();
            edit(root[AgentJoulesOptions.SectionName]!.AsObject());
            workspace.WriteConfig(root.ToJsonString());
        }

        CliHostOverrides overrides = CliHarness.Overrides() with { WorkingDirectory = workspace.Root };
        return await CliHarness.RunAsync("probe", overrides);
    }

    [Fact]
    public async Task The_committed_file_starts_a_command_with_exit_0()
    {
        CliRunResult result = await ProbeAsync(edit: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.ErrorLines);
    }

    [Fact]
    public async Task A_bad_reasoning_mode_stops_the_command_with_exit_2_naming_the_key_and_not_the_value()
    {
        CliRunResult result = await ProbeAsync(section => section["Tiers"]!["nvidia"]!["ReasoningMode"] = "maybe");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.ErrorLines, line => line.Contains("Tiers:nvidia:ReasoningMode", StringComparison.Ordinal));
        Assert.DoesNotContain("maybe", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Stdout);
    }

    [Fact]
    public async Task A_missing_base_url_stops_the_command_with_exit_2_naming_the_key()
    {
        CliRunResult result = await ProbeAsync(section => Assert.True(section["Tiers"]!["nebius"]!.AsObject().Remove("BaseUrl")));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.ErrorLines, line => line.Contains("Tiers:nebius:BaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_judge_tier_stops_the_command_with_exit_2_naming_the_key()
    {
        CliRunResult result = await ProbeAsync(section => section["Judge"]!["Tier"] = "bogus");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.ErrorLines, line => line.Contains("Judge:Tier", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_tier_section_stops_the_command_with_exit_2_naming_the_key_path()
    {
        CliRunResult result = await ProbeAsync(section => section["Tiers"]!["bogus"] = new JsonObject { ["BaseUrl"] = "https://example.test/v1" });

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.ErrorLines, line => line.Contains("Tiers:bogus", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_wrong_typed_tier_value_stops_the_command_with_exit_2_naming_the_key()
    {
        CliRunResult result = await ProbeAsync(section => section["Tiers"]!["nvidia"]!["TimeoutSeconds"] = "soon");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.ErrorLines, line => line.Contains("Tiers:nvidia:TimeoutSeconds", StringComparison.Ordinal));
        Assert.DoesNotContain("soon", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_integer_workflow_seed_stops_the_command_with_exit_2_naming_the_key()
    {
        CliRunResult result = await ProbeAsync(section => section["Workflow"]!["Seed"] = "forty-two");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.ErrorLines, line => line.Contains("Workflow:Seed", StringComparison.Ordinal));
        Assert.DoesNotContain("forty-two", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_host_options_hold_every_section_of_the_configuration()
    {
        // A section that the host forgot to copy would keep its default here: each value below differs from the default and from the committed file.
        AgentJoulesOptions? seen = null;
        CliHostOverrides overrides = CliHarness.Overrides(
        [
            ("AgentJoules:Workflow:Seed", "7"),
            ("AgentJoules:Judge:Tier", "nebius"),
            ("AgentJoules:Judge:ModelId", "judge-model"),
            ("AgentJoules:Tiers:nvidia:ModelId", "model-b"),
            ("AgentJoules:Tiers:nvidia:AgentModelIds:Writer", "writer-model"),
            ("AgentJoules:Baseline:Seconds", "12.5"),
            ("AgentJoules:Gpu:Index", "3"),
            ("AgentJoules:Meter:SampleRateHz", "20"),
        ]) with
        {
            OnHostStarted = host => seen = host.Services.GetRequiredService<IOptions<AgentJoulesOptions>>().Value,
        };

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(seen);
        Assert.Equal(7, seen.Workflow.Seed);
        Assert.Equal("nebius", seen.Judge.Tier);
        Assert.Equal("judge-model", seen.Judge.ModelId);
        Assert.Equal(4, seen.Tiers.Count);
        Assert.Equal("model-b", seen.Tiers["nvidia"].ModelId);
        Assert.Equal("writer-model", seen.Tiers["nvidia"].AgentModelIds["Writer"]);
        Assert.False(seen.Tiers["cloudgpu"].Enabled);
        Assert.Equal(12.5, seen.Baseline.Seconds);
        Assert.Equal(3, seen.Gpu.Index);
        Assert.Equal(20.0, seen.Meter.SampleRateHz);
    }

    // ---- the host-free bind into an existing object ----

    [Fact]
    public void Binding_into_an_existing_object_fills_every_section_and_keeps_the_rest()
    {
        var target = new AgentJoulesOptions();
        target.Workflow.Seed = 5;

        OptionsLoader.Bind(OnTopOfTheFile(("Judge:Tier", "local")), target);

        Assert.Equal(4, target.Tiers.Count);
        Assert.Equal("local", target.Judge.Tier);
        Assert.Equal(42, target.Workflow.Seed);
    }

    [Fact]
    public void Binding_into_an_existing_object_applies_the_same_checks()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.Bind(Alone(("Tiers:bogus:BaseUrl", "x")), new AgentJoulesOptions()));

        Assert.Equal("Tiers:bogus is not a known tier; use local, cloudgpu, nvidia or nebius", failure.Message);
        Assert.Throws<ArgumentNullException>(() => OptionsLoader.Bind(Alone(), null!));
    }

    // ---- a key pasted where a name belongs is never echoed beyond 32 characters ----

    private static string SeventyCharacterKey() => string.Concat("nvapi-", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));

    private static void AssertNoKeyMaterial(string message, string key)
    {
        Assert.Equal(70, key.Length);
        Assert.Contains("a key segment of 70 characters", message, StringComparison.Ordinal);
        Assert.DoesNotContain(key[..33], message, StringComparison.Ordinal);
        Assert.DoesNotContain(key[^10..], message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_used_as_an_agent_name_is_not_echoed_by_the_shape_check()
    {
        string key = SeventyCharacterKey();

        ConfigurationException failure = Rejected(("Tiers:nvidia:AgentModelIds:" + key, "model-a"));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        AssertNoKeyMaterial(failure.Message, key);
        Assert.Contains("is not a known agent", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_used_as_an_agent_name_is_not_echoed_by_the_validator()
    {
        string key = SeventyCharacterKey();
        var options = new AgentJoulesOptions();
        options.Tiers["nvidia"] = new TierOptions { BaseUrl = "https://example.test/v1", ModelId = "model-a" };
        options.Tiers["nvidia"].AgentModelIds[key] = "model-a";

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.ValidateOrThrow(options));

        AssertNoKeyMaterial(failure.Message, key);
    }

    [Fact]
    public void A_key_used_as_a_tier_name_is_not_echoed_by_the_shape_check_or_the_validator()
    {
        string key = SeventyCharacterKey();

        ConfigurationException shape = Rejected(("Tiers:" + key + ":BaseUrl", "https://example.test/v1"));
        AssertNoKeyMaterial(shape.Message, key);

        var options = new AgentJoulesOptions();
        options.Tiers[key] = new TierOptions();
        ConfigurationException validator = Assert.Throws<ConfigurationException>(() => OptionsLoader.ValidateOrThrow(options));
        AssertNoKeyMaterial(validator.Message, key);
    }

    // ---- values that are not valid text or not a plain variable name ----

    [Fact]
    public void An_extra_body_with_a_lone_surrogate_is_rejected_instead_of_crashing_the_validator()
    {
        string json = string.Concat("{\"a\":\"", (char)0xD800, "\"}");
        var options = new AgentJoulesOptions();
        options.Tiers["nvidia"] = new TierOptions { BaseUrl = "https://example.test/v1", ModelId = "model-a", ExtraBodyJson = json };

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.ValidateOrThrow(options));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("Tiers:nvidia:ExtraBodyJson must be a JSON object; the value is not shown", failure.Message);
    }

    [Theory]
    [InlineData("MixedCaseVariable")]
    [InlineData("lowercase_name")]
    public void An_api_key_variable_that_is_not_upper_case_is_rejected_without_echoing_it(string value)
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:ApiKeyVariable", value));

        AssertNames(failure, "Tiers:nvidia:ApiKeyVariable", value);
    }

    [Fact]
    public void An_api_key_variable_is_at_most_64_characters()
    {
        string longest = new string('A', 56) + "_API_KEY";
        string tooLong = new string('A', 57) + "_API_KEY";

        Assert.Equal(longest, OptionsLoader.Load(OnTopOfTheFile(("Tiers:nvidia:ApiKeyVariable", longest))).Tiers["nvidia"].ApiKeyVariable);
        ConfigurationException failure = Rejected(("Tiers:nvidia:ApiKeyVariable", tooLong));

        AssertNames(failure, "Tiers:nvidia:ApiKeyVariable", tooLong);
        Assert.Equal("_X1_API_KEY", OptionsLoader.Load(OnTopOfTheFile(("Tiers:nvidia:ApiKeyVariable", "_X1_API_KEY"))).Tiers["nvidia"].ApiKeyVariable);
    }
}