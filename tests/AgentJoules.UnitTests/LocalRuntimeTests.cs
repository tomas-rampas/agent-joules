using AgentJoules.TestSupport;
using AgentJoules.Workflow.Local;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-104 (EDGE-009): the Ollama availability check of the local tier and the exact fix commands. Every test runs the real <c>run</c> command through <see cref="RunCommandHarness"/> against a loopback
/// fake that stands where Ollama stands (<see cref="OllamaFixtures"/>); the shipped configuration selects <c>nim</c>, so each test sets <c>Local:Runtime</c> itself. No live endpoint is called,
/// no key but the fake one exists, and a request to <c>/chat/completions</c> is a failure of the test.
/// </summary>
[Trait("Spec", "EDGE-009")]
public sealed class LocalRuntimeTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static string Joined(CliRunResult result) => result.Stdout + "\n" + result.Stderr;

    private static string NotReachable(string nativeBase) => $"Ollama not reachable at {nativeBase}; start it with \"ollama serve\"";

    private static Task<CliRunResult> DryRunAsync(RunCommandSetup setup, string tier = "local") =>
        RunCommandHarness.RunAsync($"run --tier {tier} --dry-run", setup).WaitAsync(HangGuard);

    private static void AssertNoCompletionAndNoKey(FakeHttpMessageHandler local, CliRunResult result)
    {
        Assert.Equal(0, local.Count(FakeRoute.ChatCompletions));
        Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), Joined(result), StringComparison.Ordinal);
        Assert.Empty(result.DeniedRequests);
    }

    // ---- the native base: the tier base URL without its /v1 segment ----

    [Theory]
    [InlineData("local.fake.test/v1", "local.fake.test")]
    [InlineData("local.fake.test/v1/", "local.fake.test")]
    [InlineData("local.fake.test", "local.fake.test")]
    [InlineData("local.fake.test/", "local.fake.test")]
    [InlineData("local.fake.test/proxy/v1", "local.fake.test/proxy")]
    [InlineData("local.fake.test/v10", "local.fake.test/v10")]
    [InlineData("local.fake.test/api", "local.fake.test/api")]
    public void NativeBase_removes_a_trailing_v1_segment_only(string baseUrl, string expected)
    {
        Uri given = new(Uri.UriSchemeHttp + Uri.SchemeDelimiter + baseUrl);

        Uri native = OllamaRuntime.NativeBase(given);

        Assert.Equal(Uri.UriSchemeHttp + Uri.SchemeDelimiter + expected, native.AbsoluteUri.TrimEnd('/'));
    }

    [Fact]
    public void The_runtime_is_named_ollama()
    {
        Assert.Equal("ollama", OllamaRuntime.RuntimeName);
        Assert.Equal("none", new NoOpLocalRuntime().Name);
    }

    // ---- criterion 1: the runtime does not answer -> exit 5 with the endpoint and ollama serve ----

    [Fact]
    public async Task A_refused_connection_exits_5_naming_the_endpoint_and_ollama_serve_before_the_model_list_is_asked()
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint(OllamaFixtures.StoryTag);
        local.ConnectionRefused = true;

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local));

        Assert.True(result.ExitCode == 5, $"Expected exit code 5 but got {result.ExitCode}; stderr: {result.Stderr}");
        Assert.Contains(NotReachable(OllamaFixtures.NativeBaseUrl()), Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        Assert.Equal(1, local.Count(FakeRoute.OllamaVersion));
        Assert.Equal(0, local.Count(FakeRoute.Models));
        AssertNoCompletionAndNoKey(local, result);
    }

    [Fact]
    public async Task An_endpoint_that_never_answers_within_the_configured_timeout_exits_5_with_ollama_serve()
    {
        var local = new FakeHttpMessageHandler { ModelIds = [OllamaFixtures.StoryTag], OllamaStyleModels = true, ResponseDelay = TimeSpan.FromSeconds(20) };

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local, timeoutSeconds: 1));

        Assert.True(result.ExitCode == 5, $"Expected exit code 5 but got {result.ExitCode}; stderr: {result.Stderr}");
        Assert.Contains(NotReachable(OllamaFixtures.NativeBaseUrl()), Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        Assert.Equal(0, local.Count(FakeRoute.Models));
        AssertNoCompletionAndNoKey(local, result);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task A_status_other_than_success_from_the_version_route_exits_5_with_ollama_serve(int status)
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint(OllamaFixtures.StoryTag);
        local.SetStatuses(FakeRoute.OllamaVersion, status);

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local));

        Assert.True(result.ExitCode == 5, $"Expected exit code 5 but got {result.ExitCode}; stderr: {result.Stderr}");
        Assert.Contains(NotReachable(OllamaFixtures.NativeBaseUrl()), Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        AssertNoCompletionAndNoKey(local, result);
    }

    // ---- criterion 2: the tag is not pulled -> exit 4 with the exact pull command ----

    [Fact]
    public async Task A_missing_tag_exits_4_with_the_pull_command_of_the_configured_tag()
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint("acme-other:1b", "acme-third:7b");

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local));

        Assert.True(result.ExitCode == 4, $"Expected exit code 4 but got {result.ExitCode}; stderr: {result.Stderr}");
        Assert.Contains("ollama pull " + OllamaFixtures.StoryTag, Joined(result), StringComparison.Ordinal);
        Assert.Equal(1, local.Count(FakeRoute.OllamaVersion));
        Assert.Equal(1, local.Count(FakeRoute.Models));
        AssertNoCompletionAndNoKey(local, result);
    }

    [Fact]
    public async Task The_pull_command_names_the_configured_tag_and_not_a_constant()
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint(OllamaFixtures.StoryTag);

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local, modelTag: OllamaFixtures.OtherTag));

        Assert.True(result.ExitCode == 4, $"Expected exit code 4 but got {result.ExitCode}; stderr: {result.Stderr}");
        string output = Joined(result);
        Assert.Contains("ollama pull " + OllamaFixtures.OtherTag, output, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama pull " + OllamaFixtures.StoryTag, output, StringComparison.Ordinal);
        AssertNoCompletionAndNoKey(local, result);
    }

    [Fact]
    public async Task Two_missing_tags_give_two_pull_commands_in_the_order_of_the_check()
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint("acme-other:1b");
        var perAgent = new Dictionary<string, string>(StringComparer.Ordinal) { ["Writer"] = OllamaFixtures.OtherTag };

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local, agentModelIds: perAgent));

        Assert.True(result.ExitCode == 4, $"Expected exit code 4 but got {result.ExitCode}; stderr: {result.Stderr}");
        string line = Assert.Single(result.ErrorLines);
        int first = line.IndexOf("ollama pull " + OllamaFixtures.StoryTag, StringComparison.Ordinal);
        int second = line.IndexOf("ollama pull " + OllamaFixtures.OtherTag, StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, $"Expected the two pull commands in order in: {line}");
        Assert.Equal(2, line.Split("ollama pull ", StringSplitOptions.None).Length - 1);
        AssertNoCompletionAndNoKey(local, result);
    }

    [Fact]
    public async Task A_tag_that_is_present_is_not_in_the_pull_commands_of_the_other_missing_tag()
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint(OllamaFixtures.StoryTag);
        var perAgent = new Dictionary<string, string>(StringComparer.Ordinal) { ["Writer"] = OllamaFixtures.OtherTag };

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local, agentModelIds: perAgent));

        Assert.Equal(4, result.ExitCode);
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains("ollama pull " + OllamaFixtures.OtherTag, line, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama pull " + OllamaFixtures.StoryTag, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_hf_tag_prints_the_full_pull_command_and_not_the_cut_echo()
    {
        string tag = "hf.co/" + new string('a', 76 - "hf.co/".Length - ":Q4_K_M".Length) + ":Q4_K_M";
        Assert.Equal(76, tag.Length);
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint("acme-other:1b");

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local, modelTag: tag));

        Assert.Equal(4, result.ExitCode);
        string line = Assert.Single(result.ErrorLines);
        Assert.EndsWith("; fix: ollama pull " + tag + " (ModelNotFoundException)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("must be pulled by hand", line, StringComparison.Ordinal);
        AssertNoCompletionAndNoKey(local, result);
    }

    [Theory]
    [InlineData("evil;rm -rf x")]
    [InlineData("a$(whoami)b")]
    [InlineData("has space:1b")]
    [InlineData("-leading-dash")]
    [InlineData("back`tick`")]
    [InlineData("qwen3:8b\n")]
    public async Task A_tag_outside_the_Ollama_name_grammar_is_named_without_a_command(string tag)
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint("acme-other:1b");

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local, modelTag: tag));

        Assert.Equal(4, result.ExitCode);
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains("must be pulled by hand", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama pull", Joined(result), StringComparison.Ordinal);
        AssertNoCompletionAndNoKey(local, result);
    }

    [Fact]
    public async Task Two_missing_tags_that_share_their_first_64_characters_both_get_their_command()
    {
        string stem = "hf.co/" + new string('a', 64 - "hf.co/".Length) + "-GGUF";
        string first = stem + ":Q4_K_M";
        string second = stem + ":Q8_0";
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint("acme-other:1b");
        var perAgent = new Dictionary<string, string>(StringComparer.Ordinal) { ["Writer"] = second };

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local, modelTag: first, agentModelIds: perAgent));

        Assert.Equal(4, result.ExitCode);
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains("ollama pull " + first + "; ollama pull " + second + " (", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(148, true)]
    [InlineData(149, false)]
    [InlineData(161, false)]
    public async Task The_command_limit_of_160_characters_sends_a_longer_tag_to_the_hand_pull_list(int length, bool command)
    {
        string tag = new('a', length);
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint("acme-other:1b");

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local, modelTag: tag));

        Assert.Equal(4, result.ExitCode);
        string line = Assert.Single(result.ErrorLines);
        Assert.Equal(command, line.Contains("ollama pull " + tag, StringComparison.Ordinal));
        Assert.Equal(!command, line.Contains("must be pulled by hand", StringComparison.Ordinal));
        Assert.DoesNotContain(tag, line.Replace(tag[..64] + "...", string.Empty, StringComparison.Ordinal).Replace("ollama pull " + tag, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tag_that_holds_a_registered_key_is_masked_and_gets_no_command()
    {
        string key = ProviderClientFixtures.FakeKey();
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint("acme-other:1b");

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local, modelTag: "acme/" + key));

        Assert.Equal(4, result.ExitCode);
        string output = Joined(result);
        Assert.DoesNotContain(key, output, StringComparison.Ordinal);
        Assert.Contains("must be pulled by hand", output, StringComparison.Ordinal);
        Assert.Contains("acme/****", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama pull", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_valid_tag_and_an_invalid_one_give_one_command_and_one_hand_pull()
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint("acme-other:1b");
        var perAgent = new Dictionary<string, string>(StringComparer.Ordinal) { ["Writer"] = "bad;tag" };

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local, agentModelIds: perAgent));

        Assert.Equal(4, result.ExitCode);
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains("ollama pull " + OllamaFixtures.StoryTag, line, StringComparison.Ordinal);
        Assert.Contains("bad;tag must be pulled by hand", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama pull bad", line, StringComparison.Ordinal);
    }

    // ---- the positive path ----

    [Fact]
    public async Task A_running_Ollama_with_the_tag_passes_the_dry_run()
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint(OllamaFixtures.StoryTag);

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local));

        Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
        Assert.Equal([$"local {OllamaFixtures.LocalBaseUrl()} {OllamaFixtures.StoryTag} OK"], result.Stdout.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0));
        Assert.Equal(1, local.Count(FakeRoute.OllamaVersion));
        Assert.Equal(1, local.Count(FakeRoute.Models));
        Assert.Empty(result.ErrorLines);
        AssertNoCompletionAndNoKey(local, result);
    }

    [Fact]
    public async Task The_configured_tag_is_found_among_twenty_five_listed_tags()
    {
        string[] tags = [.. Enumerable.Range(1, 12).Select(static index => $"acme-model-{index:00}:7b"), OllamaFixtures.StoryTag, .. Enumerable.Range(13, 12).Select(static index => $"acme-model-{index:00}:7b")];
        Assert.Equal(25, tags.Length);
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint(tags);

        CliRunResult result = await DryRunAsync(OllamaFixtures.Setup(local));

        Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
        AssertNoCompletionAndNoKey(local, result);
    }

    // ---- the check belongs to the local tier with the ollama runtime only ----

    [Fact]
    public async Task A_hosted_tier_dry_run_never_asks_the_version_route()
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint(OllamaFixtures.StoryTag);
        RunCommandSetup setup = OllamaFixtures.Setup(local);

        CliRunResult result = await DryRunAsync(setup, tier: "nvidia");

        Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
        Assert.Equal(0, local.RequestCount);
        Assert.Empty(result.DeniedRequests);
    }

    [Fact]
    public async Task With_the_ollama_runtime_an_unlisted_hosted_model_exits_4_without_a_pull_command()
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint(OllamaFixtures.StoryTag);
        RunCommandSetup setup = OllamaFixtures.Setup(local);
        setup.Settings["AgentJoules:Tiers:nvidia:ModelId"] = "acme/not-listed-model";

        CliRunResult result = await DryRunAsync(setup, tier: "nvidia");

        Assert.True(result.ExitCode == 4, $"Expected exit code 4 but got {result.ExitCode}; stderr: {result.Stderr}");
        Assert.Contains("acme/not-listed-model", Joined(result), StringComparison.Ordinal);
        Assert.DoesNotContain("ollama", Joined(result), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, local.RequestCount);
    }

    [Fact]
    public async Task The_nim_runtime_skips_the_Ollama_check_and_keeps_the_generic_model_list_check()
    {
        FakeHttpMessageHandler local = OllamaFixtures.Endpoint(OllamaFixtures.StoryTag);

        CliRunResult passed = await DryRunAsync(OllamaFixtures.Setup(local, runtime: "nim"));

        Assert.True(passed.ExitCode == 0, $"Expected exit code 0 but got {passed.ExitCode}; stderr: {passed.Stderr}");
        Assert.Equal(0, local.Count(FakeRoute.OllamaVersion));
        Assert.Equal(1, local.Count(FakeRoute.Models));

        // A tag that is not listed is the generic ModelNotFoundException of every tier: no Ollama command is suggested for a NIM container.
        FakeHttpMessageHandler missing = OllamaFixtures.Endpoint("acme-other:1b");
        CliRunResult failed = await DryRunAsync(OllamaFixtures.Setup(missing, runtime: "nim"));

        Assert.Equal(4, failed.ExitCode);
        Assert.Equal(0, missing.Count(FakeRoute.OllamaVersion));
        Assert.DoesNotContain("ollama", Joined(failed), StringComparison.OrdinalIgnoreCase);
    }
}