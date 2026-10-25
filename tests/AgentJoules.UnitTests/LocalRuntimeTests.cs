using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Local;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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


    /// <summary>
    /// AJ-105 (REQ-020): the description of the local runtime in every local run file: the version, the GPU offload from <c>/api/ps</c>, the quantization level from <c>/api/show</c> (Ollama), 100 and the configured
    /// precision (NIM), the <c>partial-offload</c> run flag and the header lines of the report. Every test runs the real <c>run --tier local</c> command through <see cref="LocalRunRig"/> on a fake clock against a
    /// loopback fake that stands where the runtime stands; no live endpoint is called and no key but the fake one exists.
    /// </summary>
    [Trait("Spec", "REQ-020")]
    public sealed class Disclosure
    {
        private const long Size = 1000;
        private const string NoLoad = "the GPU offload could not be determined";

        private static string Joined(CliRunResult result) => result.Stdout + "\n" + result.Stderr;

        private static string Ps(long? size, long? vram, string tag = OllamaFixtures.StoryTag) => OllamaFixtures.Ps(OllamaFixtures.PsEntry(tag, size, vram));

        private static string Full(string tag = OllamaFixtures.StoryTag) => Ps(Size, Size, tag);

        private static JsonElement Environment(JsonDocument document) => document.RootElement.GetProperty("environment");

        private static string[] Flags(JsonElement run) => [.. run.GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()!)];

        private static void AssertExit(int expected, CliRunResult result) =>
            Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");

        // ---- the Ollama description: version, offload, quantization ----

        [Fact]
        public async Task A_full_offload_run_discloses_ollama_its_version_and_100_percent_without_the_flag()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());

            CliRunResult result = await rig.RunAsync();

            AssertExit(0, result);
            using JsonDocument document = rig.ReadRunFile();
            JsonElement environment = Environment(document);
            Assert.Equal(("ollama", "0.40.10", 100.0, "Q4_K_M"), (environment.GetProperty("localRuntime").GetString(), environment.GetProperty("runtimeVersion").GetString(), environment.GetProperty("gpuOffloadPercent").GetDouble(), environment.GetProperty("precision").GetString()));
            Assert.Empty(Flags(document.RootElement.GetProperty("run")));
            Assert.All(document.RootElement.GetProperty("environment").GetProperty("models").EnumerateArray(), static model => Assert.Equal(OllamaFixtures.StoryTag, model.GetProperty("modelId").GetString()));
            Assert.Equal(0, rig.Local.Count(FakeRoute.NimVersion));
            Assert.Empty(result.DeniedRequests);
            Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), Joined(result), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(1000L, 620L, 62.0)]
        [InlineData(10000L, 1225L, 12.3)]
        [InlineData(1000L, 1000L, 100.0)]
        [InlineData(3L, 1L, 33.3)]
        [InlineData(3L, 2L, 66.7)]
        [InlineData(1000L, 1500L, 100.0)]
        [InlineData(1000L, 0L, 0.0)]
        [InlineData(100000L, 99996L, 99.9)]
        public async Task The_offload_is_size_vram_over_size_rounded_to_one_decimal_clamped_and_never_100_for_a_partial_model(long size, long vram, double expected)
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Ps(size, vram));

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            double offload = Environment(document).GetProperty("gpuOffloadPercent").GetDouble();
            Assert.Equal(expected, offload);
            Assert.Equal(offload < 100.0, Flags(document.RootElement.GetProperty("run")).Contains(RunFlagNames.PartialOffload, StringComparer.Ordinal));
        }

        [Fact]
        public async Task A_tag_is_found_by_its_model_member_when_the_name_member_differs()
        {
            var entry = new JsonObject { ["name"] = "another-alias", ["model"] = OllamaFixtures.StoryTag, ["size"] = Size, ["size_vram"] = 500L };
            using LocalRunRig rig = LocalRunRig.Ollama(OllamaFixtures.Ps(entry.ToJsonString()));

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(50.0, Environment(document).GetProperty("gpuOffloadPercent").GetDouble());
        }

        [Fact]
        public async Task The_worst_offload_over_the_configured_tags_governs_the_disclosure()
        {
            var perAgent = new Dictionary<string, string>(StringComparer.Ordinal) { ["Writer"] = OllamaFixtures.OtherTag };
            using LocalRunRig rig = LocalRunRig.Ollama(OllamaFixtures.Ps(OllamaFixtures.PsEntry(OllamaFixtures.StoryTag, Size, Size), OllamaFixtures.PsEntry(OllamaFixtures.OtherTag, Size, 400L)), agentModelIds: perAgent);

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(40.0, Environment(document).GetProperty("gpuOffloadPercent").GetDouble());
            Assert.Contains(RunFlagNames.PartialOffload, Flags(document.RootElement.GetProperty("run")));
        }

        [Fact]
        public async Task A_second_tag_that_is_not_loaded_stops_the_run_with_exit_6_naming_that_tag()
        {
            var perAgent = new Dictionary<string, string>(StringComparer.Ordinal) { ["Writer"] = OllamaFixtures.OtherTag };
            using LocalRunRig rig = LocalRunRig.Ollama(Full(), agentModelIds: perAgent);

            CliRunResult result = await rig.RunAsync();

            AssertExit(6, result);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.Contains($"{NoLoad}: {OllamaFixtures.OtherTag} is not loaded (GET /api/ps)", Joined(result), StringComparison.Ordinal);
            Assert.Empty(rig.RunFiles());
        }

        // ---- criterion 4: no value is guessed ----

        [Fact]
        public async Task A_tag_missing_from_the_process_list_exits_6_naming_the_tag_and_the_route()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(OllamaFixtures.Ps());

            CliRunResult result = await rig.RunAsync();

            AssertExit(6, result);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.Contains($"{NoLoad}: {OllamaFixtures.StoryTag} is not loaded (GET /api/ps)", Joined(result), StringComparison.Ordinal);
            Assert.Empty(rig.RunFiles());
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), Joined(result), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Another_tag_in_the_process_list_does_not_stand_in_for_the_configured_one()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full(OllamaFixtures.OtherTag));

            CliRunResult result = await rig.RunAsync();

            AssertExit(6, result);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.Contains($"{NoLoad}: {OllamaFixtures.StoryTag} is not loaded (GET /api/ps)", Joined(result), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_size_of_0_exits_6_naming_size_0_and_no_zero_offload_is_written()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Ps(0L, 0L));

            CliRunResult result = await rig.RunAsync();

            AssertExit(6, result);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.Contains($"{NoLoad}: {OllamaFixtures.StoryTag} reports size 0 (GET /api/ps)", Joined(result), StringComparison.Ordinal);
            Assert.Empty(rig.RunFiles());
        }

        // A size that is missing, negative or not a number is not a size of 0: the message names the cause.
        [Theory]
        [InlineData(null)]
        [InlineData(-1L)]
        public async Task A_missing_or_negative_size_exits_6_naming_no_size(long? size)
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Ps(size, 0L));

            CliRunResult result = await rig.RunAsync();

            AssertExit(6, result);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.Contains($"{NoLoad}: {OllamaFixtures.StoryTag} reports no size (GET /api/ps)", Joined(result), StringComparison.Ordinal);
            Assert.Empty(rig.RunFiles());
        }

        [Fact]
        public async Task A_size_that_is_not_a_number_exits_6_naming_no_size()
        {
            var entry = new JsonObject { ["name"] = OllamaFixtures.StoryTag, ["model"] = OllamaFixtures.StoryTag, ["size"] = "big", ["size_vram"] = 500L };
            using LocalRunRig rig = LocalRunRig.Ollama(OllamaFixtures.Ps(entry.ToJsonString()));

            CliRunResult result = await rig.RunAsync();

            AssertExit(6, result);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.Contains($"{NoLoad}: {OllamaFixtures.StoryTag} reports no size (GET /api/ps)", Joined(result), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_negative_size_vram_exits_6_naming_the_negative_size_vram()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Ps(Size, -1L));

            CliRunResult result = await rig.RunAsync();

            AssertExit(6, result);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.Contains($"{NoLoad}: {OllamaFixtures.StoryTag} reports a negative size_vram (GET /api/ps)", Joined(result), StringComparison.Ordinal);
            Assert.Empty(rig.RunFiles());
        }

        [Fact]
        public async Task A_missing_size_vram_exits_6_instead_of_reading_it_as_0()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Ps(Size, null));

            CliRunResult result = await rig.RunAsync();

            AssertExit(6, result);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.Contains($"{NoLoad}: {OllamaFixtures.StoryTag} reports no size_vram (GET /api/ps)", Joined(result), StringComparison.Ordinal);
            Assert.Empty(rig.RunFiles());
        }

        [Fact]
        public async Task A_process_list_that_is_not_json_exits_6()
        {
            using LocalRunRig rig = LocalRunRig.Ollama("this is not json");

            CliRunResult result = await rig.RunAsync();

            AssertExit(6, result);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.Contains(NoLoad, Joined(result), StringComparison.Ordinal);
            Assert.Empty(rig.RunFiles());
        }

        [Fact]
        public async Task A_refused_process_list_exits_5_with_the_ollama_serve_command()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            rig.Local.SetStatuses(FakeRoute.OllamaPs, 500);

            CliRunResult result = await rig.RunAsync();

            AssertExit(5, result);
            Assert.Contains("Ollama not reachable at " + OllamaFixtures.NativeBaseUrl() + "; start it with \"ollama serve\"", Joined(result), StringComparison.Ordinal);
            Assert.Empty(rig.RunFiles());
        }

        // ---- criterion 3: the precision is the quantization level, never a guess ----

        [Theory]
        [InlineData("Q8_0")]
        [InlineData("Q4_K_M")]
        [InlineData("F16")]
        public async Task The_precision_is_the_quantization_level_of_the_show_answer(string level)
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full(), OllamaFixtures.Show(level));

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(level, Environment(document).GetProperty("precision").GetString());
            RecordedRequest show = Assert.Single(rig.Local.RequestsFor(FakeRoute.OllamaShow));
            Assert.Equal(OllamaFixtures.StoryTag, show.Json!["model"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_show_answer_without_a_quantization_level_leaves_the_precision_null_and_does_not_fall_back_to_the_nim_value_of_the_configuration()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full(), OllamaFixtures.Show(null));

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(JsonValueKind.Null, Environment(document).GetProperty("precision").ValueKind);
        }

        // ---- the NIM runtime ----

        [Fact]
        public async Task The_nim_runtime_reports_its_release_100_percent_and_the_configured_precision()
        {
            using LocalRunRig rig = LocalRunRig.Nim();

            CliRunResult result = await rig.RunAsync();

            AssertExit(0, result);
            using JsonDocument document = rig.ReadRunFile();
            JsonElement environment = Environment(document);
            Assert.Equal(("nim", "1.8.5", 100.0, "BF16"), (environment.GetProperty("localRuntime").GetString(), environment.GetProperty("runtimeVersion").GetString(), environment.GetProperty("gpuOffloadPercent").GetDouble(), environment.GetProperty("precision").GetString()));
            Assert.Empty(Flags(document.RootElement.GetProperty("run")));
            Assert.True(rig.Local.Count(FakeRoute.NimVersion) >= 1);
            Assert.Equal(0, rig.Local.Count(FakeRoute.OllamaPs) + rig.Local.Count(FakeRoute.OllamaShow) + rig.Local.Count(FakeRoute.OllamaVersion));
            Assert.Empty(result.DeniedRequests);
        }

        [Fact]
        public async Task The_nim_precision_comes_from_the_configuration_and_the_release_from_the_version_route()
        {
            using LocalRunRig rig = LocalRunRig.Nim("FP8");

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal("FP8", Environment(document).GetProperty("precision").GetString());
        }

        [Theory]
        [InlineData(503)]
        [InlineData(404)]
        public async Task A_nim_that_does_not_answer_the_version_route_exits_5_with_the_container_record(int status)
        {
            using LocalRunRig rig = LocalRunRig.Nim();
            rig.Local.SetStatuses(FakeRoute.NimVersion, status);

            CliRunResult result = await rig.RunAsync();

            AssertExit(5, result);
            Assert.Contains("NIM not reachable at http://localhost:8000/v1; start the container as recorded in docs/nim-local-attempt.md", Joined(result), StringComparison.Ordinal);
            Assert.Empty(rig.RunFiles());
        }

        [Fact]
        public async Task A_refused_nim_exits_5_with_the_container_record_before_any_completion()
        {
            using LocalRunRig rig = LocalRunRig.Nim();
            rig.Local.ConnectionRefused = true;

            CliRunResult result = await rig.RunAsync();

            AssertExit(5, result);
            Assert.Contains("NIM not reachable at http://localhost:8000/v1; start the container as recorded in docs/nim-local-attempt.md", Joined(result), StringComparison.Ordinal);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
        }

        [Fact]
        public void The_nim_runtime_is_named_nim()
        {
            Assert.Equal("nim", NimRuntime.RuntimeName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void The_nim_runtime_needs_a_precision_in_the_configuration(string? precision)
        {
            AgentJoulesOptions options = ConfigurationFixtures.ShippedOptions();
            options.Local.Runtime = LocalOptions.Nim;
            options.Tiers["local"].Precision = precision;

            ValidateOptionsResult result = new CrossFieldValidator().Validate(null, options);

            Assert.True(result.Failed);
            Assert.Contains("Tiers:local:Precision must be set when Local:Runtime is nim", result.Failures!);
        }

        [Fact]
        public void The_precision_rule_applies_to_the_nim_runtime_only()
        {
            AgentJoulesOptions options = ConfigurationFixtures.ShippedOptions();
            options.Tiers["local"].Precision = null;

            options.Local.Runtime = LocalOptions.Ollama;
            Assert.True(new CrossFieldValidator().Validate(null, options).Succeeded);

            options.Local.Runtime = LocalOptions.Nim;
            options.Tiers["local"].Precision = "BF16";
            Assert.True(new CrossFieldValidator().Validate(null, options).Succeeded);
        }

        [Fact]
        public async Task A_nim_run_without_a_precision_exits_2_before_any_request()
        {
            using LocalRunRig rig = LocalRunRig.Nim(precision: " ");

            CliRunResult result = await rig.RunAsync();

            AssertExit(2, result);
            Assert.Contains("Tiers:local:Precision must be set when Local:Runtime is nim", Joined(result), StringComparison.Ordinal);
            Assert.Equal(0, rig.Local.RequestCount);
        }

        // ---- the partial-offload run flag on every write of the tier ----

        [Fact]
        public async Task Partial_offload_sets_the_run_flag_on_every_checkpoint_and_on_the_final_write_and_the_file()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Ps(Size, 620L));
            var recorder = new RecordingOrchestrator();
            RunOrchestrationFixtures.AddServices(rig.Setup, recorder.Register);

            CliRunResult result = await rig.RunAsync("run --tier local --task incident-brief --repeat 2 --baseline-seconds 5");

            AssertExit(0, result);
            RunDocument[] writes = [.. recorder.Documents];
            Assert.Equal([RunStatus.Running, RunStatus.Running, RunStatus.Completed], writes.Select(static write => write.Run.Status));
            Assert.All(writes, static write => Assert.Equal([RunFlagNames.PartialOffload], write.Run.Flags));
            Assert.All(writes, static write => Assert.Equal(62.0, write.Environment.GpuOffloadPercent));

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal([RunFlagNames.PartialOffload], Flags(document.RootElement.GetProperty("run")));
            Assert.Equal(62.0, Environment(document).GetProperty("gpuOffloadPercent").GetDouble());
        }

        [Fact]
        public async Task A_full_offload_adds_no_flag_to_any_write()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            var recorder = new RecordingOrchestrator();
            RunOrchestrationFixtures.AddServices(rig.Setup, recorder.Register);

            AssertExit(0, await rig.RunAsync("run --tier local --task incident-brief --repeat 2 --baseline-seconds 5"));

            Assert.NotEmpty(recorder.Documents);
            Assert.All(recorder.Documents, static write => Assert.Empty(write.Run.Flags));
        }

        [Fact]
        public async Task The_partial_offload_run_flag_is_not_a_step_flag()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Ps(Size, 620L));

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            foreach (JsonElement step in RunOrchestrationFixtures.Steps(document))
            {
                Assert.DoesNotContain(RunFlagNames.PartialOffload, step.GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()));
            }
        }

        [Fact]
        public async Task A_hosted_tier_run_adds_no_offload_flag_and_keeps_the_offload_null()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nvidia"] = RunOrchestrationFixtures.Endpoint() });

            CliRunResult result = await RunCommandHarness.RunAsync("run --tier nvidia --repeat 1 --task incident-brief", setup).WaitAsync(TimeSpan.FromSeconds(60));

            AssertExit(0, result);
            using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
            Assert.Empty(Flags(document.RootElement.GetProperty("run")));
            Assert.Equal(JsonValueKind.Null, Environment(document).GetProperty("gpuOffloadPercent").ValueKind);
        }

        // ---- the AJ-080 carry: a missing offload is null, never 0 and never NaN ----

        [Fact]
        public async Task A_runtime_that_states_no_offload_leaves_it_null_and_sets_no_flag()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            Replace(rig, new LocalRuntimeInfo("custom", "1.2.3", null, null));

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            JsonElement environment = Environment(document);
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("gpuOffloadPercent").ValueKind);
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("precision").ValueKind);
            Assert.Empty(Flags(document.RootElement.GetProperty("run")));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(100.5)]
        [InlineData(-0.1)]
        public async Task A_runtime_that_states_a_value_outside_0_to_100_stops_the_run_with_exit_6_and_writes_no_file(double offload)
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            Replace(rig, new LocalRuntimeInfo("custom", "1.2.3", offload, null));

            CliRunResult result = await rig.RunAsync();

            AssertExit(6, result);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.Contains(NoLoad, Joined(result), StringComparison.Ordinal);
            Assert.Empty(rig.RunFiles());
        }

        // ---- the header of the report ----

        [Fact]
        public async Task The_header_names_the_runtime_the_offload_and_the_precision_and_has_no_flags_line_for_a_full_offload()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            AssertExit(0, await rig.RunAsync());

            string summary = await rig.SummaryAsync();

            string header = summary[..summary.IndexOf("\n| ", StringComparison.Ordinal)];
            Assert.Contains("Local runtime: ollama 0.40.10, offload 100 percent, precision Q4\\_K\\_M", header, StringComparison.Ordinal);
            Assert.DoesNotContain("Flags:", header, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_partial_offload_prints_the_offload_and_the_flags_line_in_the_summary_and_on_the_page()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Ps(Size, 620L));
            AssertExit(0, await rig.RunAsync());

            string summary = await rig.SummaryAsync();
            string html = await rig.HtmlAsync();

            string header = summary[..summary.IndexOf("\n| ", StringComparison.Ordinal)];
            Assert.Contains("Local runtime: ollama 0.40.10, offload 62 percent, precision Q4\\_K\\_M", header, StringComparison.Ordinal);
            Assert.Contains("Flags: partial-offload", header, StringComparison.Ordinal);
            Assert.Contains("Local runtime: ollama 0.40.10, offload 62 percent, precision Q4_K_M", html, StringComparison.Ordinal);
            Assert.Contains("Flags: partial-offload", html, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_fractional_offload_prints_one_decimal()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Ps(3L, 1L));
            AssertExit(0, await rig.RunAsync());

            string summary = await rig.SummaryAsync();

            Assert.Contains("offload 33.3 percent", summary, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_runtime_without_an_offload_prints_N_A_and_never_0()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            Replace(rig, new LocalRuntimeInfo("custom", "1.2.3", null, null));
            AssertExit(0, await rig.RunAsync());

            string summary = await rig.SummaryAsync();

            string header = summary[..summary.IndexOf("\n| ", StringComparison.Ordinal)];
            Assert.Contains("Local runtime: custom 1.2.3, offload N/A, precision N/A", header, StringComparison.Ordinal);
            Assert.DoesNotContain("offload 0", header, StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_nim_header_names_the_runtime_release_and_precision()
        {
            using LocalRunRig rig = LocalRunRig.Nim();
            AssertExit(0, await rig.RunAsync());

            string summary = await rig.SummaryAsync();

            Assert.Contains("Local runtime: nim 1.8.5, offload 100 percent, precision BF16", summary, StringComparison.Ordinal);
        }

        // ---- the minimum over the tags, in either order; the show request names Tiers:local:ModelId ----

        [Theory]
        [InlineData(1000L, 400L, 40.0)]
        [InlineData(400L, 1000L, 40.0)]
        public async Task The_lowest_offload_governs_whichever_tag_has_it(long storyVram, long otherVram, double expected)
        {
            var perAgent = new Dictionary<string, string>(StringComparer.Ordinal) { ["Writer"] = OllamaFixtures.OtherTag };
            using LocalRunRig rig = LocalRunRig.Ollama(OllamaFixtures.Ps(OllamaFixtures.PsEntry(OllamaFixtures.StoryTag, Size, storyVram), OllamaFixtures.PsEntry(OllamaFixtures.OtherTag, Size, otherVram)), agentModelIds: perAgent);

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(expected, Environment(document).GetProperty("gpuOffloadPercent").GetDouble());
        }

        [Fact]
        public async Task The_show_request_names_the_configured_model_id_even_when_an_agent_has_another_tag()
        {
            var perAgent = new Dictionary<string, string>(StringComparer.Ordinal) { ["Writer"] = OllamaFixtures.OtherTag };
            using LocalRunRig rig = LocalRunRig.Ollama(OllamaFixtures.Ps(OllamaFixtures.PsEntry(OllamaFixtures.OtherTag, Size, Size), OllamaFixtures.PsEntry(OllamaFixtures.StoryTag, Size, Size)), agentModelIds: perAgent);

            AssertExit(0, await rig.RunAsync());

            RecordedRequest show = Assert.Single(rig.Local.RequestsFor(FakeRoute.OllamaShow));
            Assert.Equal(OllamaFixtures.StoryTag, show.Json!["model"]!.GetValue<string>());
        }

        // ---- the local runtime serves the local tier only ----

        [Fact]
        public async Task A_cloudgpu_run_under_the_nim_runtime_sends_nothing_to_the_local_runtime_and_keeps_its_own_precision()
        {
            using LocalRunRig rig = LocalRunRig.Nim();

            await AssertCloudGpuRunAsync(rig);
        }

        [Fact]
        public async Task A_cloudgpu_run_under_the_ollama_runtime_sends_nothing_to_the_local_runtime_and_keeps_its_own_precision()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Ps(Size, 620L));

            await AssertCloudGpuRunAsync(rig);
        }

        private static async Task AssertCloudGpuRunAsync(LocalRunRig rig)
        {
            var cloud = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), Clock = rig.Time, DefaultDuration = TimeSpan.FromSeconds(1) };
            rig.Setup.Endpoints["cloudgpu"] = cloud;

            // The cloudgpu tier asks for SSH variables unless the run happens on the VM itself (CloudGpu:RequireSshVariables); the run here is a fake and has no host.
            rig.Setup.Settings["AgentJoules:CloudGpu:RequireSshVariables"] = "false";

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --repeat 1 --task incident-brief");

            AssertExit(0, result);
            Assert.Equal(0, rig.Local.RequestCount);
            Assert.True(cloud.Count(FakeRoute.ChatCompletions) > 0);
            Assert.Equal(0, cloud.Count(FakeRoute.OllamaVersion) + cloud.Count(FakeRoute.OllamaPs) + cloud.Count(FakeRoute.OllamaShow) + cloud.Count(FakeRoute.NimVersion));
            using JsonDocument document = rig.ReadRunFile();
            JsonElement environment = Environment(document);
            Assert.Equal(ConfigurationFixtures.ShippedOptions().Tiers["cloudgpu"].Precision, environment.GetProperty("precision").GetString());
            Assert.Equal("FP8", environment.GetProperty("precision").GetString());
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("localRuntime").ValueKind);
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("gpuOffloadPercent").ValueKind);
            Assert.Empty(Flags(document.RootElement.GetProperty("run")));
        }

        // ---- a run interrupted before the description names the runtime and states nothing it did not read ----

        [Fact]
        public async Task A_shutdown_inside_the_description_writes_the_runtime_name_with_a_null_precision_and_offload()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            using var shutdown = new CancellationTokenSource();
            RunOrchestrationFixtures.AddServices(rig.Setup, services =>
            {
                _ = services.RemoveAll<ILocalRuntime>();
                _ = services.AddSingleton<ILocalRuntime>(new CancellingRuntime(OllamaRuntime.RuntimeName, shutdown));
            });

            CliRunResult result = await RunOrchestrationFixtures.RunDrivenAsync(LocalRunRig.StoryCommand, rig.Setup, rig.Time, shutdown.Token).WaitAsync(TimeSpan.FromSeconds(60));

            AssertExit(5, result);
            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal("incomplete", document.RootElement.GetProperty("run").GetProperty("status").GetString());
            JsonElement environment = Environment(document);
            Assert.Equal("ollama", environment.GetProperty("localRuntime").GetString());
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("precision").ValueKind);
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("gpuOffloadPercent").ValueKind);
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("runtimeVersion").ValueKind);
            Assert.Empty(Flags(document.RootElement.GetProperty("run")));
        }

        // ---- text a document cannot read is no value (a lone surrogate escape), one test per route ----

        private const string LoneSurrogate = "\\ud800";

        private static void Answer(LocalRunRig rig, FakeRoute route, string body) =>
            rig.Local.CustomizeResponse = (answered, response) =>
            {
                if (answered == route)
                {
                    response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                }
            };

        [Fact]
        public async Task A_version_that_cannot_be_read_as_text_is_null_and_the_run_goes_on()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            Answer(rig, FakeRoute.OllamaVersion, "{\"version\":\"" + LoneSurrogate + "\"}");

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(JsonValueKind.Null, Environment(document).GetProperty("runtimeVersion").ValueKind);
            Assert.Equal("ollama", Environment(document).GetProperty("localRuntime").GetString());
        }

        [Fact]
        public async Task A_quantization_level_that_cannot_be_read_as_text_is_a_null_precision()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            Answer(rig, FakeRoute.OllamaShow, "{\"details\":{\"quantization_level\":\"" + LoneSurrogate + "\"}}");

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(JsonValueKind.Null, Environment(document).GetProperty("precision").ValueKind);
        }

        [Fact]
        public async Task A_process_list_entry_whose_names_cannot_be_read_as_text_is_not_a_loaded_tag()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            Answer(rig, FakeRoute.OllamaPs, "{\"models\":[{\"name\":\"" + LoneSurrogate + "\",\"model\":\"" + LoneSurrogate + "\",\"size\":1000,\"size_vram\":1000}]}");

            CliRunResult result = await rig.RunAsync();

            AssertExit(6, result);
            Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
            Assert.Contains($"{NoLoad}: {OllamaFixtures.StoryTag} is not loaded (GET /api/ps)", Joined(result), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_nim_release_that_cannot_be_read_as_text_is_a_null_version()
        {
            using LocalRunRig rig = LocalRunRig.Nim();
            Answer(rig, FakeRoute.NimVersion, "{\"release\":\"" + LoneSurrogate + "\"}");

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(JsonValueKind.Null, Environment(document).GetProperty("runtimeVersion").ValueKind);
            Assert.Equal("nim", Environment(document).GetProperty("localRuntime").GetString());
        }

        // ---- the bounds of what is read and of what is disclosed ----

        [Fact]
        public void The_body_cap_is_4_mebibytes()
        {
            Assert.Equal(4_194_304, LocalRuntimeHttp.MaxBodyBytes);
        }

        [Theory]
        [InlineData(4_194_304, "0.40.10")]
        [InlineData(4_194_305, null)]
        public async Task A_body_of_exactly_the_cap_is_read_and_one_byte_more_is_not(int bytes, string? expectedVersion)
        {
            const string head = "{\"version\":\"0.40.10\",\"pad\":\"";
            const string tail = "\"}";
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            string body = head + new string('a', bytes - head.Length - tail.Length) + tail;
            Assert.Equal(bytes, System.Text.Encoding.UTF8.GetByteCount(body));
            Answer(rig, FakeRoute.OllamaVersion, body);

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(expectedVersion, Environment(document).GetProperty("runtimeVersion").GetString());
        }

        [Theory]
        [InlineData(64, true)]
        [InlineData(65, false)]
        public async Task A_version_of_64_characters_is_kept_and_one_of_65_is_not(int length, bool kept)
        {
            string version = new('v', length);
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            Answer(rig, FakeRoute.OllamaVersion, new JsonObject { ["version"] = version }.ToJsonString());

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(kept ? version : null, Environment(document).GetProperty("runtimeVersion").GetString());
        }

        [Theory]
        [InlineData("0.4\u00010", null)]
        [InlineData("   ", null)]
        [InlineData("", null)]
        [InlineData("  0.40.10  ", "0.40.10")]
        public async Task A_version_with_a_control_character_or_no_text_is_null_and_surrounding_space_is_trimmed(string version, string? expected)
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Full());
            Answer(rig, FakeRoute.OllamaVersion, new JsonObject { ["version"] = version }.ToJsonString());

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(expected, Environment(document).GetProperty("runtimeVersion").GetString());
        }

        // ---- a failed step still writes the flag ----

        [Fact]
        public async Task A_failed_step_write_carries_the_partial_offload_flag()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(Ps(Size, 620L));
            rig.Local.SetStatuses(FakeRoute.ChatCompletions, 400);
            var recorder = new RecordingOrchestrator();
            RunOrchestrationFixtures.AddServices(rig.Setup, recorder.Register);

            CliRunResult result = await rig.RunAsync();

            AssertExit(5, result);
            RunDocument[] writes = [.. recorder.Documents];
            Assert.Contains(writes, static write => write.Run.Status is RunStatus.Failed or RunStatus.Partial or RunStatus.Incomplete);
            Assert.All(writes, static write => Assert.Equal([RunFlagNames.PartialOffload], write.Run.Flags));
            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal([RunFlagNames.PartialOffload], Flags(document.RootElement.GetProperty("run")));
        }

        // ---- the header never prints a partial offload as 100 and never prints a negative zero ----

        [Theory]
        [InlineData(99.95, "99.9")]
        [InlineData(99.99, "99.9")]
        [InlineData(99.9, "99.9")]
        [InlineData(100.0, "100")]
        [InlineData(62.0, "62")]
        [InlineData(33.3, "33.3")]
        [InlineData(0.0, "0")]
        public void The_header_prints_an_offload_below_100_as_at_most_99_9_and_zero_without_a_sign(double offload, string expected)
        {
            Assert.Contains(HeaderLines(offload), line => line.Contains($", offload {expected} percent, ", StringComparison.Ordinal));
        }

        [Fact]
        public void The_header_prints_a_negative_zero_offload_as_0()
        {
            Assert.Contains(HeaderLines(double.NegativeZero), static line => line.Contains(", offload 0 percent, ", StringComparison.Ordinal));
        }

        private static IReadOnlyList<string> HeaderLines(double offload)
        {
            ReportModel model = ReportAggregator.Build([ReportRunFixtures.RunOf(Tier.Local, ReportRunFixtures.NetSteps(Tier.Local, 1.0))]);
            TierReport report = Assert.Single(model.Tiers);

            return ReportHeader.Lines(model with { Tiers = [report with { GpuOffloadPercent = offload }] }, static text => text);
        }

        private sealed class CancellingRuntime(string name, CancellationTokenSource shutdown) : ILocalRuntime
        {
            public string Name => name;

            public Task CheckAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            // The host is shut down while the description is asked for: the command token is cancelled and the request ends with it.
            public Task<LocalRuntimeInfo> DescribeAsync(CancellationToken cancellationToken)
            {
                shutdown.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(LocalRuntimeInfo.Unknown);
            }

            public Task<TimeSpan?> WarmUpAsync(string modelId, CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);
        }

        private static void Replace(LocalRunRig rig, LocalRuntimeInfo description)
        {
            RunOrchestrationFixtures.AddServices(rig.Setup, services =>
            {
                _ = services.RemoveAll<ILocalRuntime>();
                _ = services.AddSingleton<ILocalRuntime>(new StaticRuntime(description));
            });
        }

        // A runtime that answers the availability check and the warm-up at once and describes itself as given.
        private sealed class StaticRuntime(LocalRuntimeInfo description) : ILocalRuntime
        {
            public string Name => description.Name ?? "static";

            public Task CheckAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<LocalRuntimeInfo> DescribeAsync(CancellationToken cancellationToken) => Task.FromResult(description);

            public Task<TimeSpan?> WarmUpAsync(string modelId, CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);
        }
    }
}