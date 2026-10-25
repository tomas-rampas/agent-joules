using System.Text.Json;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class AttributionBaselineTests
{
    /// <summary>
    /// The REQ-021 cases of AJ-106 (order warm-up, idle baseline and repeats in local runs), in the class that the story names (<c>AttributionBaselineTests</c>) but in a nested class of their own so that the trait is
    /// on them. Every test runs the real <c>run --tier local</c> command through <see cref="LocalRunRig"/> on a fake clock against loopback fakes; the order of the operations comes from the rig's
    /// <see cref="OperationRecorder"/>. No live endpoint is called, no key but the fake one exists and the power source is a fake that never reads the host.
    /// </summary>
    [Trait("Spec", "REQ-021")]
    public sealed class WarmupBaselineRepeatCases
    {
        private const long Size = 1000;
        private const string ShortBaselineWarning = "Warning: baseline shorter than 30 s is not publishable";
        private const int AgentsPerRepeat = 4;

        private static LocalRunRig OllamaRig() => LocalRunRig.Ollama(OllamaFixtures.Ps(OllamaFixtures.PsEntry(OllamaFixtures.StoryTag, Size, Size)));

        private static void AssertExit(int expected, CliRunResult result) =>
            Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");

        private static string[] Flags(JsonDocument document) =>
            [.. document.RootElement.GetProperty("run").GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()!)];

        private static int Occurrences(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        // ---- criterion 1: the order ----

        [Fact]
        public async Task The_ollama_order_is_probe_meter_start_warm_up_baseline_then_the_task_calls()
        {
            using LocalRunRig rig = OllamaRig();

            AssertExit(0, await rig.RunAsync("run --tier local --task incident-brief --repeat 1 --baseline-seconds 30"));

            Assert.Equal("probe, meter-start, warm-up, baseline, task-call x4", rig.Recorder.Describe());
            Assert.Equal(["probe", "meter-start", "warm-up", "baseline", "task-call", "task-call", "task-call", "task-call"], rig.Recorder.Operations);
        }

        [Fact]
        public async Task The_nim_order_is_the_same()
        {
            using LocalRunRig rig = LocalRunRig.Nim();

            AssertExit(0, await rig.RunAsync("run --tier local --task incident-brief --repeat 1 --baseline-seconds 30"));

            Assert.Equal("probe, meter-start, warm-up, baseline, task-call x4", rig.Recorder.Describe());
        }

        [Fact]
        public async Task The_warm_up_and_the_baseline_happen_once_per_tier_run_not_once_per_repeat()
        {
            using LocalRunRig rig = OllamaRig();

            AssertExit(0, await rig.RunAsync("run --tier local --task incident-brief --repeat 3"));

            Assert.Equal("probe, meter-start, warm-up, baseline, task-call x12", rig.Recorder.Describe());
        }

        [Fact]
        public async Task The_warm_up_adds_nothing_to_the_steps_the_judge_readings_or_the_totals()
        {
            using LocalRunRig rig = OllamaRig();

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            JsonElement root = document.RootElement;
            JsonElement[] steps = [.. root.GetProperty("steps").EnumerateArray()];
            Assert.Equal(AgentsPerRepeat, steps.Length);
            Assert.Equal(0, root.GetProperty("judge").GetArrayLength());
            double perStep = steps.Sum(static step => step.GetProperty("inputTokens").GetProperty("value").GetDouble());
            Assert.Equal(perStep, root.GetProperty("totals").GetProperty("inputTokens").GetProperty("value").GetDouble());
            Assert.Equal(steps.Sum(static step => step.GetProperty("outputTokens").GetProperty("value").GetDouble()), root.GetProperty("totals").GetProperty("outputTokens").GetProperty("value").GetDouble());
            Assert.Equal(AgentsPerRepeat, rig.Recorder.Count(OperationRecorder.TaskCall));
            Assert.Equal(1, rig.Recorder.Count(OperationRecorder.WarmUp));
        }

        [Fact]
        public async Task The_production_judge_scores_exactly_the_four_steps_and_never_the_warm_up_call()
        {
            using LocalRunRig rig = OllamaRig();
            rig.Local.SetAnswers(FakeRoute.OllamaGenerate, FakeAnswer.Ok(TimeSpan.FromSeconds(7.5)));

            // The judge tier answers its buffered calls with a score; its endpoint sees the judge calls only (the warm-up goes to the local endpoint).
            var judgeTier = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), Clock = rig.Time, DefaultDuration = TimeSpan.FromSeconds(1), ChatText = "4" };
            rig.Setup.Endpoints["nvidia"] = judgeTier;
            rig.Setup.UseProductionJudge = true;

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            JsonElement[] steps = [.. document.RootElement.GetProperty("steps").EnumerateArray()];
            JsonElement[] judged = [.. document.RootElement.GetProperty("judge").EnumerateArray()];
            Assert.Equal(AgentsPerRepeat, steps.Length);
            Assert.Equal(AgentsPerRepeat, judged.Length);
            Assert.Equal(AgentsPerRepeat, judgeTier.Count(FakeRoute.ChatCompletions));
            Assert.DoesNotContain(judgeTier.RequestsFor(FakeRoute.ChatCompletions), static request => request.Body!.Contains("single word ready", StringComparison.Ordinal));

            // Every judge reading starts after the last step ended, so none sits at the time of the warm-up, which ended before the first step began.
            DateTimeOffset lastStepEnd = steps.Max(static step => step.GetProperty("endUtc").GetDateTimeOffset());
            DateTimeOffset firstStepStart = steps.Min(static step => step.GetProperty("startUtc").GetDateTimeOffset());
            DateTimeOffset runStart = document.RootElement.GetProperty("run").GetProperty("startUtc").GetDateTimeOffset();
            Assert.All(judged, reading => Assert.True(reading.GetProperty("startUtc").GetDateTimeOffset() >= lastStepEnd));
            Assert.True(firstStepStart - runStart >= TimeSpan.FromSeconds(7.5), "A step began before the warm-up of 7.5 s had ended.");
        }

        [Fact]
        public async Task A_hosted_tier_records_no_probe_no_warm_up_and_no_baseline_and_never_calls_the_local_endpoint()
        {
            using LocalRunRig rig = OllamaRig();

            AssertExit(0, await rig.RunAsync("run --tier nvidia --task incident-brief --repeat 1 --baseline-seconds 5"));

            Assert.Equal("task-call x4", rig.Recorder.Describe());
            Assert.Empty(rig.Local.Requests);
        }

        // ---- criterion 2: the baseline length and the load time in the file ----

        [Theory]
        [InlineData(30)]
        [InlineData(45)]
        [InlineData(10)]
        public async Task Baseline_seconds_in_the_file_equals_the_option(int seconds)
        {
            using LocalRunRig rig = OllamaRig();

            AssertExit(0, await rig.RunAsync($"run --tier local --task incident-brief --repeat 1 --baseline-seconds {seconds}"));

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal((double)seconds, document.RootElement.GetProperty("baseline").GetProperty("seconds").GetDouble());
        }

        [Fact]
        public async Task Without_the_option_the_baseline_is_the_configured_30_seconds()
        {
            using LocalRunRig rig = OllamaRig();

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(30.0, document.RootElement.GetProperty("baseline").GetProperty("seconds").GetDouble());
            Assert.DoesNotContain(RunFlagNames.NonPublishable, Flags(document));
        }

        [Fact]
        public async Task Model_load_seconds_is_numeric_in_a_local_file_and_absent_in_a_hosted_one()
        {
            using LocalRunRig local = OllamaRig();
            AssertExit(0, await local.RunAsync());
            using JsonDocument localFile = local.ReadRunFile();
            Assert.Equal(JsonValueKind.Number, localFile.RootElement.GetProperty("environment").GetProperty("modelLoadSeconds").ValueKind);

            using LocalRunRig hosted = OllamaRig();
            AssertExit(0, await hosted.RunAsync("run --tier nvidia --task incident-brief --repeat 1"));
            using JsonDocument hostedFile = hosted.ReadRunFile();
            Assert.Equal(JsonValueKind.Null, hostedFile.RootElement.GetProperty("environment").GetProperty("modelLoadSeconds").ValueKind);
        }

        // ---- criterion 2: the power source ----

        [Theory]
        [InlineData("ac")]
        [InlineData("battery")]
        [InlineData("unknown")]
        public async Task The_power_source_of_the_detector_is_written_to_the_environment_and_read_once(string value)
        {
            using LocalRunRig rig = OllamaRig();
            rig.Setup.PowerSource.Value = value;

            AssertExit(0, await rig.RunAsync("run --tier local --task incident-brief --repeat 2"));

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(value, document.RootElement.GetProperty("environment").GetProperty("powerSource").GetString());
            Assert.Equal(1, rig.Setup.PowerSource.Calls);
        }

        [Fact]
        public async Task The_harness_default_power_source_in_the_file_is_unknown()
        {
            using LocalRunRig rig = OllamaRig();

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(PowerSources.Unknown, document.RootElement.GetProperty("environment").GetProperty("powerSource").GetString());
        }

        [Fact]
        public async Task A_hosted_tier_records_the_power_source_of_the_machine_that_runs_the_client()
        {
            using LocalRunRig rig = OllamaRig();
            rig.Setup.PowerSource.Value = PowerSources.Battery;

            AssertExit(0, await rig.RunAsync("run --tier nvidia --task incident-brief --repeat 1"));

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(PowerSources.Battery, document.RootElement.GetProperty("environment").GetProperty("powerSource").GetString());
        }

        [Fact]
        public async Task A_detector_value_outside_the_vocabulary_is_written_as_unknown()
        {
            using LocalRunRig rig = OllamaRig();
            rig.Setup.PowerSource.Value = "charging";

            AssertExit(0, await rig.RunAsync());

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(PowerSources.Unknown, document.RootElement.GetProperty("environment").GetProperty("powerSource").GetString());
        }

        [Fact]
        public async Task The_power_source_is_on_the_header_line_of_the_report()
        {
            using LocalRunRig rig = OllamaRig();
            rig.Setup.PowerSource.Value = PowerSources.Battery;
            AssertExit(0, await rig.RunAsync());

            string summary = await rig.SummaryAsync();

            Assert.Contains("power source battery", summary[..summary.IndexOf("\n| ", StringComparison.Ordinal)], StringComparison.Ordinal);
        }

        // ---- criterion 3: repeats ----

        [Fact]
        public async Task Three_repeats_of_one_task_write_twelve_steps_with_repeat_1_2_and_3_four_times_each()
        {
            using LocalRunRig rig = OllamaRig();

            AssertExit(0, await rig.RunAsync("run --tier local --task incident-brief --repeat 3"));

            using JsonDocument document = rig.ReadRunFile();
            int[] repeats = [.. document.RootElement.GetProperty("steps").EnumerateArray().Select(static step => step.GetProperty("repeat").GetInt32())];
            Assert.Equal(12, repeats.Length);
            Assert.Equal([(1, 4), (2, 4), (3, 4)], repeats.GroupBy(static repeat => repeat).OrderBy(static group => group.Key).Select(static group => (group.Key, group.Count())));
            Assert.Equal(3, document.RootElement.GetProperty("run").GetProperty("repeat").GetInt32());
        }

        [Fact]
        public async Task The_default_repeat_is_three()
        {
            using LocalRunRig rig = OllamaRig();

            AssertExit(0, await rig.RunAsync("run --tier local --task incident-brief"));

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(3, document.RootElement.GetProperty("run").GetProperty("repeat").GetInt32());
            Assert.Equal(3 * AgentsPerRepeat, document.RootElement.GetProperty("steps").GetArrayLength());
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        public async Task A_repeat_below_1_exits_2_before_any_host_is_built(string repeat)
        {
            using LocalRunRig rig = OllamaRig();

            CliRunResult result = await rig.RunAsync($"run --tier local --repeat {repeat}");

            AssertExit(2, result);
            Assert.Contains("--repeat must be 1 or more", result.Stdout + "\n" + result.Stderr, StringComparison.Ordinal);
            Assert.Equal(0, rig.Setup.HostBuilds);
            Assert.Empty(rig.Local.Requests);
        }

        // ---- criterion 4: the short baseline ----

        [Fact]
        public async Task A_baseline_below_30_s_warns_once_runs_and_flags_every_write_of_the_tier()
        {
            using LocalRunRig rig = OllamaRig();
            var writes = new RecordingOrchestrator();
            RunOrchestrationFixtures.AddServices(rig.Setup, writes.Register);

            CliRunResult result = await rig.RunAsync("run --tier local --task incident-brief --repeat 2 --baseline-seconds 10");

            AssertExit(0, result);
            Assert.Equal(1, Occurrences(result.Stderr, ShortBaselineWarning));
            Assert.Equal(string.Empty, string.Concat(result.Stdout.Split('\n').Where(static line => line.Contains("shorter than 30 s", StringComparison.Ordinal))));
            RunDocument[] documents = [.. writes.Documents];
            Assert.Equal([RunStatus.Running, RunStatus.Running, RunStatus.Completed], documents.Select(static write => write.Run.Status));
            Assert.All(documents, static write => Assert.Equal([RunFlagNames.NonPublishable], write.Run.Flags));

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal([RunFlagNames.NonPublishable], Flags(document));
            Assert.Equal(10.0, document.RootElement.GetProperty("baseline").GetProperty("seconds").GetDouble());
        }

        [Theory]
        [InlineData(1, true)]
        [InlineData(29, true)]
        [InlineData(30, false)]
        [InlineData(31, false)]
        public async Task The_non_publishable_flag_and_the_warning_follow_the_30_s_limit(int seconds, bool isShort)
        {
            using LocalRunRig rig = OllamaRig();

            CliRunResult result = await rig.RunAsync($"run --tier local --task incident-brief --repeat 1 --baseline-seconds {seconds}");

            AssertExit(0, result);
            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(isShort, Flags(document).Contains(RunFlagNames.NonPublishable));
            Assert.Equal(isShort ? 1 : 0, Occurrences(result.Stderr + "\n" + result.Stdout, ShortBaselineWarning));
        }

        [Fact]
        public async Task A_configured_baseline_below_30_s_is_flagged_and_warned_like_the_option()
        {
            using LocalRunRig rig = OllamaRig();
            rig.Setup.Settings["AgentJoules:Baseline:Seconds"] = "12";

            CliRunResult result = await rig.RunAsync("run --tier local --task incident-brief --repeat 1");

            AssertExit(0, result);
            using JsonDocument document = rig.ReadRunFile();
            Assert.Contains(RunFlagNames.NonPublishable, Flags(document));
            Assert.Equal(1, Occurrences(result.Stderr, ShortBaselineWarning));
        }

        [Fact]
        public async Task A_short_baseline_with_a_partial_offload_carries_both_flags_in_the_order_of_the_vocabulary()
        {
            using LocalRunRig rig = LocalRunRig.Ollama(OllamaFixtures.Ps(OllamaFixtures.PsEntry(OllamaFixtures.StoryTag, Size, 620L)));

            AssertExit(0, await rig.RunAsync("run --tier local --task incident-brief --repeat 1 --baseline-seconds 10"));

            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal([RunFlagNames.PartialOffload, RunFlagNames.NonPublishable], Flags(document));
        }

        [Fact]
        public async Task A_hosted_tier_ignores_the_baseline_option_no_warning_no_flag_no_baseline()
        {
            using LocalRunRig rig = OllamaRig();

            CliRunResult result = await rig.RunAsync("run --tier nvidia --task incident-brief --repeat 1 --baseline-seconds 10");

            AssertExit(0, result);
            Assert.DoesNotContain("shorter than 30 s", result.Stdout + "\n" + result.Stderr, StringComparison.Ordinal);
            using JsonDocument document = rig.ReadRunFile();
            Assert.DoesNotContain(RunFlagNames.NonPublishable, Flags(document));
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("baseline").ValueKind);
            Assert.Equal(0, rig.Recorder.Count(OperationRecorder.Baseline));
        }

        [Fact]
        public async Task A_dry_run_with_a_short_baseline_prints_no_warning_and_writes_no_file()
        {
            using LocalRunRig rig = OllamaRig();

            CliRunResult result = await rig.RunAsync("run --tier local --dry-run --baseline-seconds 10");

            AssertExit(0, result);
            Assert.DoesNotContain("shorter than 30 s", result.Stdout + "\n" + result.Stderr, StringComparison.Ordinal);
            Assert.Empty(rig.RunFiles());
        }

        [Fact]
        public async Task The_latest_file_of_the_tier_is_never_a_short_baseline_run_even_when_it_is_newer()
        {
            using LocalRunRig rig = OllamaRig();
            AssertExit(0, await rig.RunAsync("run --tier local --task incident-brief --repeat 1 --baseline-seconds 10"));
            string flagged = Assert.Single(rig.RunFiles());
            string folder = Path.GetDirectoryName(flagged)!;
            const string olderName = "20261021T090000Z-local.json";
            File.WriteAllText(Path.Combine(folder, olderName), RunDocumentFixtures.CommittedText(Tier.Local));
            Assert.True(string.CompareOrdinal(olderName, Path.GetFileName(flagged)) < 0, "The valid file must be the older name");

            string? latest = RunFileSelector.Latest(folder, Tier.Local);

            Assert.Equal(olderName, latest is null ? null : Path.GetFileName(latest));
        }
    }
}