using System.Text.Json;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class CloudGpuTests
{
    /// <summary>
    /// AJ-122 (EDGE-017): the 4-hour and USD 25 stop. The guard decides on a <see cref="FakeTimeProvider"/>; the run tests run the real <c>run --tier cloudgpu</c> command on a fake clock against a fake endpoint that counts
    /// completions. A nested class so that the <c>Spec=EDGE-017</c> trait does not tag the other cloudgpu tests.
    /// </summary>
    [Trait("Spec", "EDGE-017")]
    public sealed class BudgetGuard
    {
        private const string Task = "incident-brief";

        private static readonly DateTimeOffset Start = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

        private static BudgetDecision Check(double? cost, TimeSpan elapsed, Action<CloudGpuOptions>? configure = null, bool tierStartKnown = true)
        {
            var clock = new FakeTimeProvider(Start);
            var options = new CloudGpuOptions { ObservedCostUsd = cost, TierStartUtc = tierStartKnown ? Start : null };
            configure?.Invoke(options);
            clock.Advance(elapsed);
            return new CloudGpuBudgetGuard(clock, options).Check();
        }

        private static void AssertExit(int expected, CliRunResult result) =>
            Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");

        // ---- the decision (criterion 1) ----

        [Fact]
        public void A_run_under_both_limits_continues()
        {
            BudgetDecision decision = Check(12.40, TimeSpan.FromHours(1));

            Assert.False(decision.Stop);
            Assert.Null(decision.Reason);
            Assert.Null(decision.Note);
        }

        [Fact]
        public void One_second_before_four_hours_still_continues()
        {
            Assert.False(Check(6.00, new TimeSpan(3, 59, 59)).Stop);
        }

        [Fact]
        public void Just_under_25_USD_still_continues()
        {
            Assert.False(Check(24.99, TimeSpan.Zero).Stop);
        }

        [Fact]
        public void Exactly_25_USD_stops_with_cost_cap()
        {
            BudgetDecision decision = Check(25.00, TimeSpan.Zero);

            Assert.True(decision.Stop);
            Assert.Equal("cost-cap", decision.Reason);
        }

        [Fact]
        public void Exactly_four_hours_stop_with_time_box()
        {
            BudgetDecision decision = Check(6.00, TimeSpan.FromHours(4));

            Assert.True(decision.Stop);
            Assert.Equal("time-box", decision.Reason);
        }

        [Fact]
        public void The_cost_cap_wins_when_both_limits_hold()
        {
            BudgetDecision decision = Check(25.50, TimeSpan.FromHours(4));

            Assert.True(decision.Stop);
            Assert.Equal("cost-cap", decision.Reason);
        }

        [Fact]
        public void The_limits_come_from_the_options()
        {
            Assert.Equal("cost-cap", Check(10.0, TimeSpan.Zero, static options => options.CostStopUsd = 10.0).Reason);
            Assert.Equal("time-box", Check(0.0, TimeSpan.FromHours(2), static options => options.TimeBoxHours = 2.0).Reason);
        }

        [Fact]
        public void The_decision_follows_the_clock_between_two_checks()
        {
            var clock = new FakeTimeProvider(Start);
            var guard = new CloudGpuBudgetGuard(clock, new CloudGpuOptions { ObservedCostUsd = 1.0, TierStartUtc = Start });

            Assert.False(guard.Check().Stop);
            clock.Advance(TimeSpan.FromHours(4));
            Assert.Equal("time-box", guard.Check().Reason);
        }

        [Fact]
        public void A_cost_that_was_not_reported_is_not_a_reached_cap()
        {
            Assert.False(Check(null, TimeSpan.FromHours(1)).Stop);
        }

        [Fact]
        public void A_missing_tier_start_enforces_no_time_box_and_says_so()
        {
            BudgetDecision decision = Check(6.00, TimeSpan.FromHours(10), tierStartKnown: false);

            Assert.False(decision.Stop);
            Assert.Equal("TierStartUtc not set: the 4-hour box is not enforced", decision.Note);
        }

        [Fact]
        public void A_missing_tier_start_still_stops_at_the_cost_cap()
        {
            BudgetDecision decision = Check(26.0, TimeSpan.Zero, tierStartKnown: false);

            Assert.Equal("cost-cap", decision.Reason);
            Assert.Equal(CloudGpuBudgetGuard.TierStartNotSetNote, decision.Note);
        }

        // ---- the validation (criterion 3) ----

        [Fact]
        public void A_stop_threshold_above_30_fails_validation_naming_the_key()
        {
            AgentJoulesOptions options = ConfigurationFixtures.ShippedOptions();
            options.CloudGpu.CostStopUsd = 31.0;

            var result = new CrossFieldValidator().Validate(null, options);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static failure => failure.Contains("CloudGpu:CostStopUsd", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(25.0)]
        [InlineData(30.0)]
        public void A_stop_threshold_up_to_30_passes_validation(double threshold)
        {
            AgentJoulesOptions options = ConfigurationFixtures.ShippedOptions();
            options.CloudGpu.CostStopUsd = threshold;

            Assert.True(new CrossFieldValidator().Validate(null, options).Succeeded);
        }

        [Fact]
        public void The_shipped_configuration_stops_at_25_USD_and_four_hours()
        {
            CloudGpuOptions shipped = ConfigurationFixtures.ShippedOptions().CloudGpu;

            Assert.Equal(25.0, shipped.CostStopUsd);
            Assert.Equal(4.0, shipped.TimeBoxHours);
            Assert.Null(shipped.ObservedCostUsd);
            Assert.Null(shipped.TierStartUtc);
        }

        [Fact]
        public async Task A_stop_threshold_of_31_exits_2_naming_the_key_on_the_command()
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.Set("CostStopUsd", "31");

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --dry-run");

            AssertExit(2, result);
            Assert.Contains("CloudGpu:CostStopUsd", result.Stderr, StringComparison.Ordinal);
        }

        // ---- before the run (criterion 2, design 464) ----

        [Fact]
        public async Task An_observed_cost_of_25_USD_exits_6_before_any_completion_and_names_the_reason()
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.SetObservedCost(25.00);
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart);

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --repeat 1 --task " + Task);

            AssertExit(6, result);
            Assert.Contains("cost-cap", result.Stderr, StringComparison.Ordinal);
            Assert.Equal(0, rig.Completions);
            Assert.Empty(rig.RunFiles());

            // Nothing of the run happened: no meter, no warm-up, no baseline, no task call.
            Assert.Empty(rig.Recorder.Operations);
        }

        [Fact]
        public async Task A_time_box_that_ran_out_before_the_run_exits_6_with_the_reason()
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.SetObservedCost(6.00);
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart - TimeSpan.FromHours(5));

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --repeat 1 --task " + Task);

            AssertExit(6, result);
            Assert.Contains("time-box", result.Stderr, StringComparison.Ordinal);
            Assert.Equal(0, rig.Completions);
            Assert.Empty(rig.Recorder.Operations);
        }

        [Fact]
        public async Task A_run_under_both_limits_completes_and_prints_no_budget_line()
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.SetObservedCost(12.40);
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart);

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --repeat 2 --task " + Task);

            AssertExit(0, result);
            Assert.DoesNotContain("Budget stop", result.Stdout + result.Stderr, StringComparison.Ordinal);
            using JsonDocument document = rig.ReadRunFile();
            JsonElement run = document.RootElement.GetProperty("run");
            Assert.Equal("completed", run.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, run.GetProperty("stopReason").ValueKind);
        }

        [Fact]
        public async Task A_missing_tier_start_warns_once_and_the_run_completes()
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.SetObservedCost(1.00);

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --repeat 2 --task " + Task);

            AssertExit(0, result);
            Assert.Single(result.StderrLines, static line => line == "Warning: " + CloudGpuBudgetGuard.TierStartNotSetNote);
            Assert.DoesNotContain(CloudGpuBudgetGuard.TierStartNotSetNote, result.Stdout, StringComparison.Ordinal);
        }

        // ---- between iterations (criterion 2) ----

        [Fact]
        public async Task A_time_box_that_expires_after_repeat_2_leaves_a_partial_file_and_exits_0()
        {
            int perRepeat = await CloudGpuBudgetRig.CompletionsPerRepeatAsync(Task);
            Assert.True(perRepeat > 0);
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.SetObservedCost(6.00);
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart);
            rig.Set("Provider", "other-cloud");
            rig.Set("InstancePreset", "gpu-h100-1x");
            rig.Set("Region", "eu-north1");
            rig.Set("ContainerImage", "registry.example/nim/llm");
            rig.Set("ContainerDigest", "sha256:abc123");
            rig.AdvanceAtCompletion(2 * perRepeat, TimeSpan.FromHours(4));

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --repeat 3 --task " + Task);

            AssertExit(0, result);
            Assert.Equal(2 * perRepeat, rig.Completions);
            using JsonDocument document = rig.ReadRunFile();
            JsonElement run = document.RootElement.GetProperty("run");
            Assert.Equal("partial", run.GetProperty("status").GetString());
            Assert.Equal("time-box", run.GetProperty("stopReason").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("steps").EnumerateArray().Select(static step => step.GetProperty("repeat").GetInt32()).Distinct().Count());
            Assert.Contains("Warning: Budget stop (time-box): no further work started; copy the results and delete the VM now", result.StderrLines);
            Assert.Equal("Warning: Budget stop (time-box): no further work started; copy the results and delete the VM now", result.StderrLines.Last(static line => line.Length > 0));
            Assert.DoesNotContain(result.StderrLines, static line => line.Contains("tiers skipped", StringComparison.Ordinal));

            // The environment of the written file (the wiring for AJ-127).
            JsonElement environment = document.RootElement.GetProperty("environment");
            Assert.Equal("other-cloud", environment.GetProperty("provider").GetString());
            Assert.Equal("gpu-h100-1x", environment.GetProperty("instancePreset").GetString());
            Assert.Equal("eu-north1", environment.GetProperty("region").GetString());
            Assert.Equal("registry.example/nim/llm", environment.GetProperty("containerImage").GetString());
            Assert.Equal("sha256:abc123", environment.GetProperty("containerDigest").GetString());
            JsonElement cost = environment.GetProperty("cloudCostUsd");
            Assert.Equal(6.0, cost.GetProperty("value").GetDouble());
            Assert.Equal("measured", cost.GetProperty("label").GetString());
        }

        // ---- a stop ends the whole plan (EDGE-017) ----

        [Fact]
        public async Task A_budget_stop_in_the_cloudgpu_tier_starts_no_later_tier_and_names_the_skipped_ones()
        {
            int perRepeat = await CloudGpuBudgetRig.CompletionsPerRepeatAsync(Task);
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.EnableAllTiers();
            rig.SetObservedCost(6.00);
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart);
            rig.AdvanceAtCompletion(perRepeat, TimeSpan.FromHours(4));

            CliRunResult result = await rig.RunAsync("run --tier all --repeat 3 --task " + Task);

            AssertExit(0, result);
            string[] tiers = [.. rig.RunFiles().Select(static file => Path.GetFileNameWithoutExtension(file).Split('-')[^1]).Order(StringComparer.Ordinal)];
            Assert.Equal(["cloudgpu", "local"], tiers);
            Assert.Equal(perRepeat, rig.Completions);
            string[] lines = [.. result.StderrLines.Where(static line => line.Length > 0)];
            Assert.Equal("Warning: Budget stop: tiers skipped: nvidia, nebius", lines[^2]);
            Assert.Equal("Warning: Budget stop (time-box): no further work started; copy the results and delete the VM now", lines[^1]);
        }

        // ---- the configuration of the budget (AJ-122 fix round 1) ----

        [Theory]
        [InlineData("2026-10-20T09:00:00Z")]
        [InlineData("2026-10-20T09:00:00+02:00")]
        [InlineData("2026-10-20T09:00:00.5-05:00")]
        public async Task A_tier_start_with_an_offset_is_accepted(string value)
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.Set("TierStartUtc", value);

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --dry-run");

            AssertExit(0, result);
        }

        [Theory]
        [InlineData("2026-10-25T09:00:00")]
        [InlineData("2026-10-25")]
        [InlineData("not-a-time")]
        [InlineData("09:00Z")]
        public async Task A_tier_start_without_an_offset_or_not_a_time_exits_2_naming_the_key_only(string value)
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.Set("TierStartUtc", value);

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --dry-run");

            AssertExit(2, result);
            Assert.Contains("CloudGpu:TierStartUtc must be a date and time with an offset such as 2026-10-25T09:00:00Z; the value is not shown", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(value, result.Stderr.Replace("2026-10-25T09:00:00Z", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_tier_start_in_the_future_exits_2_before_any_work()
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.SetObservedCost(1.00);
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart + TimeSpan.FromDays(365));

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --repeat 1 --task " + Task);

            AssertExit(2, result);
            Assert.Contains("CloudGpu:TierStartUtc is in the future; the value is not shown", result.Stderr, StringComparison.Ordinal);
            Assert.Equal(0, rig.Completions);
            Assert.Empty(rig.RunFiles());
        }

        [Fact]
        public async Task A_tier_start_in_the_future_exits_2_on_a_dry_run_too()
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart + TimeSpan.FromMinutes(2));

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --dry-run");

            AssertExit(2, result);
            Assert.Contains("CloudGpu:TierStartUtc", result.Stderr, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_tier_start_in_the_future_is_ignored_when_the_cloudgpu_tier_is_not_selected()
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart + TimeSpan.FromMinutes(2));

            CliRunResult result = await rig.RunAsync("run --tier nvidia --dry-run");

            AssertExit(0, result);
        }

        [Fact]
        public async Task A_bad_tier_start_and_another_bad_value_are_both_named_at_once()
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.Set("TierStartUtc", "2026-10-25T09:00:00");
            rig.Set("CostStopUsd", "abc");

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --dry-run");

            AssertExit(2, result);
            Assert.Contains("CloudGpu:TierStartUtc", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("CloudGpu:CostStopUsd", result.Stderr, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_listener_that_fails_on_the_budget_lines_changes_neither_the_files_nor_the_exit_code()
        {
            int perRepeat = await CloudGpuBudgetRig.CompletionsPerRepeatAsync(Task);
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.EnableAllTiers();
            rig.SetObservedCost(6.00);
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart);
            rig.AdvanceAtCompletion(perRepeat, TimeSpan.FromHours(4));
            string detail = "console-detail-" + Guid.NewGuid().ToString("N")[..12];
            rig.DecorateListener(inner => new BudgetLinesThrow(inner, detail));

            CliRunResult result = await rig.RunAsync("run --tier all --repeat 3 --task " + Task);

            AssertExit(0, result);
            string[] tiers = [.. rig.RunFiles().Select(static file => Path.GetFileNameWithoutExtension(file).Split('-')[^1]).Order(StringComparer.Ordinal)];
            Assert.Equal(["cloudgpu", "local"], tiers);
            Assert.DoesNotContain(detail, result.Stdout + result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(result.StderrLines, static line => line.Contains("Budget stop", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_tier_start_a_few_seconds_ahead_of_the_clock_is_tolerated()
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart + TimeSpan.FromSeconds(30));

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --dry-run");

            AssertExit(0, result);
        }

        [Theory]
        [InlineData("4.0001", 2)]
        [InlineData("1e308", 2)]
        [InlineData("0", 2)]
        [InlineData("4", 0)]
        [InlineData("2.5", 0)]
        public async Task The_time_box_is_greater_than_0_and_at_most_4_hours(string hours, int expectedExit)
        {
            using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
            rig.Set("TimeBoxHours", hours);

            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --dry-run");

            AssertExit(expectedExit, result);
            if (expectedExit == 2)
            {
                Assert.Contains("CloudGpu:TimeBoxHours", result.Stderr, StringComparison.Ordinal);
            }
        }

        /// <summary>The run listener of the command, except that the two budget lines throw an <see cref="IOException"/> whose message is a sentinel.</summary>
        private sealed class BudgetLinesThrow(IRunListener inner, string detail) : IRunListener
        {
            public void StepFinished(StepReading reading) => inner.StepFinished(reading);

            public void CheckpointFailed(string exceptionTypeName, bool previousCheckpointKept, CheckpointKind kind) => inner.CheckpointFailed(exceptionTypeName, previousCheckpointKept, kind);

            public void BaselineMeasured(BaselineResult baseline) => inner.BaselineMeasured(baseline);

            public void SettleStarted(double seconds) => inner.SettleStarted(seconds);

            public void TierCompleted(string displayPath, Totals totals) => inner.TierCompleted(displayPath, totals);

            public void JudgeFailed(Tier tier, int failedSteps, int judgedSteps, IReadOnlyList<string> failureKinds) => inner.JudgeFailed(tier, failedSteps, judgedSteps, failureKinds);

            public void TierFailed(string displayPath, string agent, RunStatus status) => inner.TierFailed(displayPath, agent, status);

            public void BudgetNote(string note) => inner.BudgetNote(note);

            public void BudgetTiersSkipped(IReadOnlyList<string> tiers) => throw new IOException(detail);

            public void BudgetStopped(string reason) => throw new IOException(detail);
        }
    }
}