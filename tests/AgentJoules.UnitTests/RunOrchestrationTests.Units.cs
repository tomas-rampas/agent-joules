using System.Globalization;

using AgentJoules.Cli.Output;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Local;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class RunOrchestrationTests
{
    // A reading built from a fixture with other values where a test needs them; every other member is the fixture's. A negative net carries below-baseline, as the reading requires.
    private static StepReading Reading(
        StepReading template,
        Metric? gross = null,
        Metric? net = null,
        Metric? avgW = null,
        Metric? inputTokens = null,
        Metric? outputTokens = null,
        Metric? latency = null,
        Metric? cost = null,
        string? task = null,
        int repeat = 1,
        string? agent = null) =>
        new(
            task ?? template.Task,
            repeat,
            agent ?? template.Agent,
            template.ModelId,
            template.Tier,
            template.StartUtc,
            template.EndUtc,
            template.Outcome,
            latency ?? template.LatencyMs,
            template.TtftMs,
            inputTokens ?? template.InputTokens,
            outputTokens ?? template.OutputTokens,
            gross ?? template.GrossJoules,
            net ?? template.NetJoules,
            avgW ?? template.AvgW,
            template.PeakW,
            cost ?? template.Cost,
            template.Retries,
            template.WaitMs,
            (net ?? template.NetJoules) is { Label: MetricLabel.Measured, Value: < 0.0 } ? template.Flags | ReadingFlags.BelowBaseline : template.Flags,
            template.Method,
            template.SampleCount,
            template.SampleRateHz);

    /// <summary>AJ-056 T4a and T4: the run document assembler, pure and without a host (totals, environment, run fields, the copy of a reading with another outcome).</summary>
    [Trait("Spec", "REQ-017")]
    public sealed class Assembler
    {
        private static readonly StepReading Metered = StepReadingFixtures.Metered();

        private static TierOptions TierWith(string modelId, params (string Agent, string ModelId)[] agents)
        {
            var tier = new TierOptions { ModelId = modelId, ReasoningMode = "on", Precision = "FP8" };
            foreach ((string agent, string id) in agents)
            {
                tier.AgentModelIds[agent] = id;
            }

            return tier;
        }

        private static (MetricLabel Label, double? Value, string? Reason) Of(Metric metric) => (metric.Label, metric.Value, metric.Reason);

        [Fact]
        [Trait("Spec", "REQ-013")]
        public void Serialized_measured_totals_are_the_sums_of_the_steps()
        {
            StepReading[] steps =
            [
                Reading(Metered, gross: Metric.Measured(100.5, "J"), net: Metric.Measured(40.25, "J"), inputTokens: Metric.Measured(10, "tokens"), outputTokens: Metric.Measured(4, "tokens")),
                Reading(Metered, gross: Metric.Measured(80, "J"), net: Metric.Measured(-5, "J"), inputTokens: Metric.Measured(20, "tokens"), outputTokens: Metric.Measured(6, "tokens"), agent: "Writer"),
            ];

            Totals totals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, steps, [], wallClockMs: 1234.5);

            Assert.Equal((MetricLabel.Measured, 180.5, (string?)null), Of(totals.GrossJoules));
            Assert.Equal("J", totals.GrossJoules.Unit);
            Assert.Equal((MetricLabel.Measured, 35.25, (string?)null), Of(totals.NetJoules));
            Assert.Equal((MetricLabel.Measured, 30.0, (string?)null), Of(totals.InputTokens));
            Assert.Equal((MetricLabel.Measured, 10.0, (string?)null), Of(totals.OutputTokens));
            Assert.Equal("tokens", totals.InputTokens.Unit);
            Assert.Equal((MetricLabel.Measured, 1234.5, (string?)null), Of(totals.WallClockMs));
            Assert.Equal("ms", totals.WallClockMs.Unit);
        }

        [Fact]
        public void A_step_without_the_value_gives_the_total_its_label_and_reason_and_never_0()
        {
            StepReading[] steps = [Metered, StepReadingFixtures.NoUsage(agent: "Writer"), Metered];

            Totals totals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, steps, [], wallClockMs: 10);

            Assert.Equal((MetricLabel.NotReported, (double?)null, "provider-omitted-usage"), Of(totals.InputTokens));
            Assert.Equal((MetricLabel.NotReported, (double?)null, "provider-omitted-usage"), Of(totals.OutputTokens));

            // The energy of every step is measured, so its sum exists.
            Assert.Equal((MetricLabel.Measured, 540.0, (string?)null), Of(totals.GrossJoules));
        }

        [Fact]
        public void The_first_step_that_lacks_the_value_decides_the_label()
        {
            StepReading[] steps =
            [
                Metered,
                Reading(Metered, gross: Metric.NotApplicable("first-reason"), agent: "Researcher"),
                Reading(Metered, gross: Metric.NotReported("second-reason"), agent: "Writer"),
            ];

            Totals totals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, steps, [], wallClockMs: 10);

            Assert.Equal((MetricLabel.NotApplicable, (double?)null, "first-reason"), Of(totals.GrossJoules));
        }

        [Fact]
        public async Task A_hosted_tier_has_NA_hosted_tier_energy_totals_in_both_modes()
        {
            StepReading hosted = await StepReadingFixtures.HostedAsync(Tier.Nvidia);

            foreach (RunMode mode in new[] { RunMode.Serialized, RunMode.Concurrent })
            {
                Totals totals = RunAssembler.ComputeTotals(Tier.Nvidia, mode, [hosted, hosted], [], wallClockMs: 10);

                Assert.Equal((MetricLabel.NotApplicable, (double?)null, "hosted-tier"), Of(totals.GrossJoules));
                Assert.Equal((MetricLabel.NotApplicable, (double?)null, "hosted-tier"), Of(totals.NetJoules));
                Assert.Equal((MetricLabel.Measured, 240.0, (string?)null), Of(totals.InputTokens));
            }
        }

        [Fact]
        [Trait("Spec", "REQ-013")]
        public void A_concurrent_measured_run_reports_energy_as_not_computed_and_sums_the_tokens()
        {
            Totals totals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Concurrent, [Metered, Metered], [], wallClockMs: 10);

            Assert.Equal((MetricLabel.NotReported, (double?)null, RunAssembler.UnionNotComputedReason), Of(totals.GrossJoules));
            Assert.Equal((MetricLabel.NotReported, (double?)null, "union-not-computed"), Of(totals.NetJoules));
            Assert.Equal((MetricLabel.Measured, 240.0, (string?)null), Of(totals.InputTokens));
            Assert.Equal((MetricLabel.Measured, 160.0, (string?)null), Of(totals.OutputTokens));
        }

        [Fact]
        public void The_cost_is_summed_per_currency_when_every_step_carries_one()
        {
            StepReading[] steps =
            [
                Reading(Metered, cost: Metric.Estimated(0.1, "EUR")),
                Reading(Metered, cost: Metric.Estimated(1.0, "USD"), agent: "Researcher"),
                Reading(Metered, cost: Metric.Estimated(0.2, "EUR"), agent: "Reviewer"),
            ];

            Totals totals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, steps, [], wallClockMs: 10);

            Assert.Equal(["EUR", "USD"], totals.CostByCurrency.Keys.Order(StringComparer.Ordinal));
            Metric euro = totals.CostByCurrency["EUR"];
            Assert.Equal((MetricLabel.Estimated, "EUR"), (euro.Label, euro.Currency));
            Assert.Equal(0.3, euro.Value!.Value, precision: 12);
            Assert.Equal((MetricLabel.Estimated, 1.0, (string?)null), Of(totals.CostByCurrency["USD"]));
        }

        [Fact]
        public void A_step_without_a_cost_makes_every_currency_NA_missing_step_costs_never_a_partial_sum()
        {
            StepReading[] steps =
            [
                Reading(Metered, cost: Metric.Estimated(0.1, "EUR")),
                Reading(Metered, cost: Metric.Estimated(1.0, "USD"), agent: "Researcher"),
                Reading(Metered, cost: Metric.NotApplicable("price-not-found"), agent: "Writer"),
                Reading(Metered, cost: Metric.Estimated(0.2, "EUR"), agent: "Reviewer"),
            ];

            Totals totals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, steps, [], wallClockMs: 10);

            Assert.Equal(["EUR", "USD"], totals.CostByCurrency.Keys.Order(StringComparer.Ordinal));
            Assert.All(totals.CostByCurrency.Values, static cost => Assert.Equal((MetricLabel.NotApplicable, (double?)null, "missing-step-costs"), Of(cost)));
            Assert.Equal("missing-step-costs", RunAssembler.MissingStepCostsReason);
        }

        [Fact]
        public void The_cost_by_currency_is_empty_when_no_step_carries_a_cost()
        {
            StepReading notConfigured = Reading(Metered, cost: Metric.NotApplicable(NotConfiguredCostCalculator.Reason));

            Totals totals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, [notConfigured, notConfigured], [], wallClockMs: 10);

            Assert.Empty(totals.CostByCurrency);
        }

        [Fact]
        public void The_judge_totals_are_not_scored_and_the_emission_is_not_configured()
        {
            Totals totals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, [Metered], [], wallClockMs: 10);

            Assert.Equal((MetricLabel.NotApplicable, (double?)null, "not-scored"), Of(totals.Judge.InputTokens));
            Assert.Equal((MetricLabel.NotApplicable, (double?)null, "not-scored"), Of(totals.Judge.OutputTokens));
            Assert.Empty(totals.Judge.CostByCurrency);
            Assert.Equal((MetricLabel.NotApplicable, (double?)null, "not-configured"), Of(totals.Gco2e));
        }

        [Fact]
        public void A_run_without_steps_reports_its_sums_as_not_reported_never_0()
        {
            // No (task, repeat) started: the wall clock has no value either (fix round 1, N1).
            Totals totals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, [], [], wallClockMs: null);

            Assert.Equal((MetricLabel.NotReported, (double?)null, RunAssembler.NoStepsReason), Of(totals.GrossJoules));
            Assert.Equal((MetricLabel.NotReported, (double?)null, "no-steps"), Of(totals.InputTokens));
            Assert.Empty(totals.CostByCurrency);
            Assert.Equal((MetricLabel.NotReported, (double?)null, "no-steps"), Of(totals.WallClockMs));
        }

        [Fact]
        public void A_started_repeat_without_a_finished_step_still_measures_its_wall_clock()
        {
            Totals totals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, [], [], wallClockMs: 0.0);

            Assert.Equal((MetricLabel.Measured, 0.0, (string?)null), Of(totals.WallClockMs));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(-1.0)]
        [InlineData(double.PositiveInfinity)]
        public void A_wall_clock_that_is_negative_or_not_finite_is_refused(double wallClockMs) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, [Metered], [], wallClockMs));

        [Fact]
        public void The_concurrent_overlap_sentence_is_the_fixed_text() =>
            Assert.Equal("Per-step energy overlaps in concurrent mode and does not sum to the run total.", ReportSentences.ConcurrentOverlap);

        [Fact]
        public void A_copy_with_another_outcome_keeps_every_other_value()
        {
            StepReading original = Reading(Metered, cost: Metric.Estimated(0.5, "EUR"));

            StepReading copy = RunAssembler.WithOutcome(original, StepOutcomes.Incomplete);

            Assert.Equal(StepOutcomes.Incomplete, copy.Outcome);
            Assert.Equal(original.ToString().Replace("Outcome = ok", "Outcome = incomplete", StringComparison.Ordinal), copy.ToString());
            Assert.Equal(
                (original.Task, original.Repeat, original.Agent, original.ModelId, original.Tier, original.StartUtc, original.EndUtc, original.Flags, original.Method, original.SampleCount),
                (copy.Task, copy.Repeat, copy.Agent, copy.ModelId, copy.Tier, copy.StartUtc, copy.EndUtc, copy.Flags, copy.Method, copy.SampleCount));
            Assert.Equal((original.GrossJoules, original.NetJoules, original.InputTokens, original.Cost, original.Quality), (copy.GrossJoules, copy.NetJoules, copy.InputTokens, copy.Cost, copy.Quality));
            Assert.Equal(original.QualityReason, copy.QualityReason);
        }

        [Theory]
        [InlineData(Tier.Local, "laptop", "N/A", "not-cloudgpu")]
        [InlineData(Tier.CloudGpu, "nebius-ai-cloud", "not-reported", "not-observed")]
        [InlineData(Tier.Nvidia, "build.nvidia.com", "N/A", "not-cloudgpu")]
        [InlineData(Tier.Nebius, "nebius-token-factory", "N/A", "not-cloudgpu")]
        public void The_environment_names_the_provider_and_the_cloud_cost_of_each_tier(Tier tier, string provider, string costLabel, string costReason)
        {
            EnvironmentInfo environment = RunAssembler.BuildEnvironment(tier, TierWith("vendor/nemotron-x"), gpu: null, runtime: null, modelLoad: null);

            Assert.Equal(provider, environment.Provider);
            Assert.Equal(provider, RunAssembler.ProviderOf(tier));
            Assert.Equal((costLabel, costReason), (Label(environment.CloudCostUsd), environment.CloudCostUsd.Reason));
        }

        private static string Label(Metric metric) => metric.Label switch
        {
            MetricLabel.NotApplicable => "N/A",
            MetricLabel.NotReported => "not-reported",
            _ => metric.Label.ToString(),
        };

        [Fact]
        public void The_environment_takes_its_fields_from_the_tier_the_GPU_and_the_runtime()
        {
            var gpu = new GpuIdentity("GPU X", "GPU-uuid-1", "616.92", null, "8.6", 85.0, "disabled");
            var runtime = new LocalRuntimeInfo("ollama", "0.12.0", 87.5, "Q4_K_M");

            EnvironmentInfo environment = RunAssembler.BuildEnvironment(
                Tier.Local,
                TierWith("acme/nemotron-nano", ("Writer", "acme/qwen-writer")),
                gpu,
                runtime,
                TimeSpan.FromSeconds(3.25));

            Assert.Equal(("GPU X", "616.92", "GPU-uuid-1"), (environment.GpuName, environment.DriverVersion, environment.GpuUuid));
            Assert.Equal(("ollama", "0.12.0", 87.5, 3.25), (environment.LocalRuntime, environment.RuntimeVersion, environment.GpuOffloadPercent, environment.ModelLoadSeconds));
            Assert.Equal("Q4_K_M", environment.Precision);
            Assert.Equal(("on", 0.0), (environment.ReasoningMode, environment.Temperature));
            Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.OSDescription, environment.Os);
            Assert.Equal(Environment.Version.ToString(), environment.DotnetVersion);
            Assert.Equal(EnvironmentInfo.GpuBoardOnly, environment.EnergyScope);
            Assert.Null(environment.PowerSource);
            Assert.Equal((null, null, null, null), (environment.InstancePreset, environment.Region, environment.ContainerImage, environment.ContainerDigest));
            Assert.Equal((MetricLabel.NotReported, "not-configured"), (environment.Assumptions.ElectricityPrice.Label, environment.Assumptions.ElectricityPrice.Reason));
            Assert.Equal((MetricLabel.NotReported, "not-configured"), (environment.Assumptions.Gco2ePerKwh.Label, environment.Assumptions.Gco2ePerKwh.Reason));

            // One row per agent in catalog order; a model ID without nemotron is a baseline model.
            Assert.Equal(
                ["Planner acme/nemotron-nano False", "Researcher acme/nemotron-nano False", "Writer acme/qwen-writer True", "Reviewer acme/nemotron-nano False"],
                environment.Models.Select(static model => $"{model.Agent} {model.ModelId} {model.Baseline}"));
        }

        [Fact]
        public void Without_a_runtime_and_a_GPU_the_fields_stay_null_and_the_precision_comes_from_the_tier()
        {
            EnvironmentInfo environment = RunAssembler.BuildEnvironment(Tier.Nvidia, TierWith("vendor/nemotron-x"), gpu: null, runtime: null, modelLoad: null);

            Assert.Equal((null, null, null), (environment.GpuName, environment.DriverVersion, environment.GpuUuid));
            Assert.Equal((null, null, null, null), (environment.LocalRuntime, environment.RuntimeVersion, environment.GpuOffloadPercent, environment.ModelLoadSeconds));
            Assert.Equal("FP8", environment.Precision);

            var noPrecision = new TierOptions { ModelId = "vendor/nemotron-x" };
            Assert.Null(RunAssembler.BuildEnvironment(Tier.Nvidia, noPrecision, null, null, null).Precision);
        }

        [Fact]
        public void The_run_member_records_the_given_fields_and_nothing_else()
        {
            DateTimeOffset start = RunCheckpointFixtures.RunStartUtc;

            RunInfo run = RunAssembler.BuildRunInfo("20261022T091530Z-local", Tier.Local, ["a", "b"], RunMode.Concurrent, 2, start, RunStatus.Running);

            Assert.Equal(("20261022T091530Z-local", Tier.Local, RunMode.Concurrent, 2, start, RunStatus.Running), (run.Id, run.Tier, run.Mode, run.Repeat, run.StartUtc, run.Status));
            Assert.Equal(["a", "b"], run.Tasks);
            Assert.Empty(run.Flags);
            Assert.Equal((null, null), (run.StopReason, run.SelfJudged));
            Assert.False(string.IsNullOrWhiteSpace(run.ToolVersion));
            Assert.DoesNotContain('+', run.ToolVersion);
        }

        [Theory]
        [InlineData("1.2.3+0123abc", "1.2.3", "0123abc")]
        [InlineData("1.2.3-beta.1+deadbeef", "1.2.3-beta.1", "deadbeef")]
        [InlineData("1.2.3", "1.2.3", null)]
        [InlineData("1.2.3+", "1.2.3", null)]
        [InlineData(null, "4.5.6.7", null)]
        [InlineData("", "4.5.6.7", null)]
        public void The_tool_version_and_the_commit_come_from_the_informational_version(string? informational, string version, string? commit) =>
            Assert.Equal((version, commit), RunAssembler.ParseVersion(informational, new Version(4, 5, 6, 7)));

        [Theory]
        [InlineData("results", "C:/x/results/20261021T100000Z-nvidia.json", "results/20261021T100000Z-nvidia.json")]
        [InlineData("results/", "/x/results/20261021T100000Z-nvidia.json", "results/20261021T100000Z-nvidia.json")]
        [InlineData("custom\\out\\", "/x/custom/out/20261021T100000Z-local.json", "custom/out/20261021T100000Z-local.json")]
        [InlineData("/abs/dir", "/abs/dir/20261021T100000Z-nebius.json", "/abs/dir/20261021T100000Z-nebius.json")]
        public void The_display_path_is_out_as_given_and_the_file_name(string outputDisplay, string filePath, string expected) =>
            Assert.Equal(expected, RunAssembler.DisplayPath(outputDisplay, filePath));

        [Fact]
        public void A_document_holds_the_steps_the_totals_over_them_and_an_empty_judge()
        {
            RunInfo run = RunAssembler.BuildRunInfo("20261022T091530Z-local", Tier.Local, ["summarise"], RunMode.Serialized, 1, RunCheckpointFixtures.RunStartUtc, RunStatus.Completed);
            EnvironmentInfo environment = RunAssembler.BuildEnvironment(Tier.Local, TierWith("vendor/nemotron-x"), null, null, null);

            RunDocument document = RunAssembler.Assemble(run, environment, probe: null, baseline: null, [Metered, Metered], [], wallClockMs: 4000);

            Assert.Equal(RunDocument.CurrentSchemaVersion, document.SchemaVersion);
            Assert.Same(run, document.Run);
            Assert.Equal(2, document.Steps.Count);
            Assert.Empty(document.Judge);
            Assert.Equal((MetricLabel.Measured, 360.0, (string?)null), Of(document.Totals.GrossJoules));
            Assert.Equal((MetricLabel.Measured, 4000.0, (string?)null), Of(document.Totals.WallClockMs));
        }
    }

    /// <summary>AJ-056 T6: the step line and the closing lines of a tier, written by the console reporter on standard output.</summary>
    [Trait("Spec", "REQ-017")]
    public sealed class ConsoleLines
    {
        private static (ConsoleReporter Reporter, StringWriter Output, StringWriter Error) Reporter()
        {
            var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
            var error = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
            return (new ConsoleReporter(output, error), output, error);
        }

        [Fact]
        public void A_measured_step_prints_its_values()
        {
            (ConsoleReporter reporter, StringWriter output, StringWriter error) = Reporter();

            reporter.ReportStep(StepReadingFixtures.Metered());

            Assert.Equal("summarise r1 Planner ok netJ=60 avgW=90 in=120 out=80 latency=2000\n", output.ToString());
            Assert.Empty(error.ToString());
        }

        [Fact]
        public async Task A_missing_value_prints_its_label_never_0()
        {
            (ConsoleReporter reporter, StringWriter output, _) = Reporter();

            reporter.ReportStep(StepReadingFixtures.NoUsage(agent: "Writer"));
            reporter.ReportStep(await StepReadingFixtures.HostedAsync(Tier.Nebius, "Reviewer"));

            Assert.Equal(
                "summarise r1 Writer ok netJ=60 avgW=90 in=not-reported out=not-reported latency=2000\n"
                + "summarise r1 Reviewer ok netJ=N/A avgW=N/A in=120 out=80 latency=2000\n",
                output.ToString());
        }

        [Fact]
        public void Numbers_use_the_invariant_culture_three_decimals_for_energy_and_whole_milliseconds()
        {
            (ConsoleReporter reporter, StringWriter output, _) = Reporter();
            CultureInfo before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                reporter.ReportStep(Reading(
                    StepReadingFixtures.Metered(),
                    net: Metric.Measured(-12.34567, "J"),
                    avgW: Metric.Measured(90.5, "W"),
                    latency: Metric.Measured(1234.6, "ms"),
                    task: "api-migration",
                    repeat: 3,
                    agent: "Researcher"));
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }

            Assert.Equal("api-migration r3 Researcher ok netJ=-12.346 avgW=90.5 in=120 out=80 latency=1235\n", output.ToString());
        }

        [Fact]
        public void The_closing_lines_name_the_file_and_the_totals()
        {
            (ConsoleReporter reporter, StringWriter output, StringWriter error) = Reporter();
            var totals = new Totals(
                Metric.Measured(1234.5678, "J"),
                Metric.Measured(-3, "J"),
                Metric.Measured(120, "tokens"),
                Metric.NotReported("provider-omitted-usage"),
                new Dictionary<string, Metric> { ["EUR"] = Metric.Estimated(0.0004, "EUR"), ["USD"] = Metric.Estimated(1.25, "USD") },
                Metric.Measured(8000.4, "ms"),
                new JudgeTotals(Metric.NotApplicable("not-scored"), Metric.NotApplicable("not-scored"), new Dictionary<string, Metric>()),
                Metric.NotApplicable("not-configured"));

            reporter.ReportTierCompleted("results/20261021T100000Z-local.json", totals);

            Assert.Equal(
                "results: results/20261021T100000Z-local.json\n"
                + "totals: grossJ=1234.568 netJ=-3 in=120 out=not-reported cost=EUR 0.0004,USD 1.25 wallClockMs=8000\n",
                output.ToString());
            Assert.Empty(error.ToString());
        }

        [Fact]
        public void Totals_without_a_cost_and_with_labels_print_the_labels()
        {
            (ConsoleReporter reporter, StringWriter output, _) = Reporter();
            var totals = new Totals(
                Metric.NotApplicable("hosted-tier"),
                Metric.NotReported("union-not-computed"),
                Metric.Measured(28, "tokens"),
                Metric.Measured(12, "tokens"),
                new Dictionary<string, Metric>(),
                Metric.Measured(8000, "ms"),
                new JudgeTotals(Metric.NotApplicable("not-scored"), Metric.NotApplicable("not-scored"), new Dictionary<string, Metric>()),
                Metric.NotApplicable("not-configured"));

            reporter.ReportTierCompleted("custom/out/20261021T100000Z-nvidia.json", totals);

            Assert.Equal(
                "results: custom/out/20261021T100000Z-nvidia.json\ntotals: grossJ=N/A netJ=not-reported in=28 out=12 cost=N/A wallClockMs=8000\n",
                output.ToString());
        }

        [Fact]
        public void The_console_listener_forwards_each_event_to_its_line()
        {
            (ConsoleReporter reporter, StringWriter output, StringWriter error) = Reporter();
            var listener = new ConsoleRunListener(reporter);
            var unstable = new BaselineResult(Metric.Measured(25, "W"), Metric.Measured(5, "W"), 30, 300, 0.2, 0.1, ReadingFlags.BaselineUnstable);

            listener.StepFinished(StepReadingFixtures.Metered());
            listener.BaselineMeasured(unstable);

            Assert.StartsWith("summarise r1 Planner ok ", output.ToString(), StringComparison.Ordinal);
            Assert.Equal("Warning: idle baseline unstable (CV 0.20 > 0.10)\n", error.ToString());
        }

        [Theory]
        [InlineData(true, "the last complete checkpoint was kept")]
        [InlineData(false, "no checkpoint of this tier was written")]
        public void A_failed_shutdown_checkpoint_is_one_warning_line_on_stderr_naming_the_type(bool previousCheckpointKept, string ending)
        {
            (ConsoleReporter reporter, StringWriter output, StringWriter error) = Reporter();

            new ConsoleRunListener(reporter).CheckpointFailed(nameof(IOException), previousCheckpointKept);

            Assert.Equal($"Warning: the run file could not be written after the shutdown (IOException); {ending}\n", error.ToString());
            Assert.Empty(output.ToString());
        }

        [Fact]
        public void Totals_whose_currencies_are_NA_print_cost_NA()
        {
            (ConsoleReporter reporter, StringWriter output, _) = Reporter();
            var totals = new Totals(
                Metric.Measured(10, "J"),
                Metric.Measured(5, "J"),
                Metric.Measured(28, "tokens"),
                Metric.Measured(12, "tokens"),
                new Dictionary<string, Metric> { ["EUR"] = Metric.NotApplicable("missing-step-costs"), ["USD"] = Metric.NotApplicable("missing-step-costs") },
                Metric.NotReported("no-steps"),
                new JudgeTotals(Metric.NotApplicable("not-scored"), Metric.NotApplicable("not-scored"), new Dictionary<string, Metric>()),
                Metric.NotApplicable("not-configured"));

            reporter.ReportTierCompleted("results/x.json", totals);

            Assert.EndsWith("totals: grossJ=10 netJ=5 in=28 out=12 cost=N/A wallClockMs=not-reported\n", output.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// AJ-056 fix round 1 (security MEDIUM-1, code review M2): the sink of a tier run keeps every reading whatever its listener does, never lets a listener failure out, logs the first
    /// failure at Debug with its type only, and holds back a <c>cancelled</c> line only while the run token is cancelled.
    /// </summary>
    [Trait("Spec", "REQ-017")]
    public sealed class ListenerSink
    {
        private static StepReading Cancelled() => RunAssembler.WithOutcome(StepReadingFixtures.Metered(), StepOutcomes.Cancelled);

        [Fact]
        public void A_cancelled_reading_published_while_the_run_token_is_not_cancelled_is_told()
        {
            var inner = new InMemoryMetricsSink();
            var listener = new CapturingListener();
            var sink = new RunListenerSink(inner, listener, new CapturingLogger(), CancellationToken.None);

            sink.Publish(Cancelled());

            Assert.Equal([StepOutcomes.Cancelled], listener.Steps.Select(static step => step.Outcome));
            _ = Assert.Single(inner.Snapshot());
        }

        [Fact]
        public void A_cancelled_reading_published_while_the_run_token_is_cancelled_is_kept_and_not_told()
        {
            var inner = new InMemoryMetricsSink();
            var listener = new CapturingListener();
            var sink = new RunListenerSink(inner, listener, new CapturingLogger(), new CancellationToken(canceled: true));

            sink.Publish(Cancelled());
            sink.Publish(StepReadingFixtures.Metered());

            Assert.Equal([StepOutcomes.Ok], listener.Steps.Select(static step => step.Outcome));
            Assert.Equal(2, inner.Snapshot().Count);
        }

        [Fact]
        public void A_failing_listener_never_fails_the_publish_and_its_first_failure_is_logged_with_the_type_only()
        {
            var inner = new InMemoryMetricsSink();
            var logger = new CapturingLogger();
            var sink = new RunListenerSink(inner, new CapturingListener { Failure = new IOException("secret-detail-7f3a") }, logger, CancellationToken.None);

            Exception? first = Record.Exception(() => sink.Publish(StepReadingFixtures.Metered()));
            Exception? second = Record.Exception(() => sink.Publish(StepReadingFixtures.Metered("Writer")));

            Assert.Null(first);
            Assert.Null(second);
            Assert.Equal(2, inner.Snapshot().Count);
            string record = Assert.Single(logger.Records);
            Assert.StartsWith("Debug ", record, StringComparison.Ordinal);
            Assert.Contains(nameof(IOException), record, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-detail-7f3a", record, StringComparison.Ordinal);
        }
    }

    // A listener that records the steps it is told and, when Failure is set, throws it from every call.
    private sealed class CapturingListener : IRunListener
    {
        private readonly List<StepReading> _steps = [];

        public Exception? Failure { get; init; }

        public IReadOnlyList<StepReading> Steps
        {
            get
            {
                lock (_steps)
                {
                    return [.. _steps];
                }
            }
        }

        public void StepFinished(StepReading reading)
        {
            ThrowIfFailing();
            lock (_steps)
            {
                _steps.Add(reading);
            }
        }

        public void BaselineMeasured(BaselineResult baseline) => ThrowIfFailing();

        public void TierCompleted(string displayPath, Totals totals) => ThrowIfFailing();

        public void CheckpointFailed(string exceptionTypeName, bool previousCheckpointKept) => ThrowIfFailing();

        private void ThrowIfFailing()
        {
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    // A logger that keeps "<level> <message> <exception type or ->" for every record at every level.
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly List<string> _records = [];

        public IReadOnlyList<string> Records
        {
            get
            {
                lock (_records)
                {
                    return [.. _records];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_records)
            {
                _records.Add($"{logLevel} {formatter(state, exception)} {exception?.GetType().Name ?? "-"}");
            }
        }
    }

    /// <summary>AJ-056 T3 and T7: the no-op seams of M2 and their registration in the composition root.</summary>
    [Trait("Spec", "REQ-017")]
    public sealed class Seams
    {
        [Fact]
        public async Task The_no_op_judge_returns_the_steps_unchanged()
        {
            IReadOnlyList<StepReading> steps = [StepReadingFixtures.Metered(), StepReadingFixtures.NoUsage()];

            IReadOnlyList<StepReading> scored = await new NoOpJudgeService().ScoreAsync(steps, CancellationToken.None);

            Assert.Equal(steps, scored);
            Assert.All(scored, static step => Assert.Equal((MetricLabel.NotApplicable, "not-scored"), (step.Quality.Label, step.Quality.Reason)));
        }

        [Fact]
        public async Task The_no_op_runtime_states_nothing_and_observes_no_load()
        {
            var runtime = new NoOpLocalRuntime();

            Assert.Equal("none", runtime.Name);
            Assert.Equal(LocalRuntimeInfo.Unknown, await runtime.DescribeAsync(CancellationToken.None));
            Assert.Equal(new LocalRuntimeInfo(null, null, null, null), await runtime.DescribeAsync(CancellationToken.None));
            Assert.Null(await runtime.WarmUpAsync("any-model", CancellationToken.None));
        }

        [Fact]
        public void The_composition_root_registers_the_no_op_seams_the_cost_calculator_and_the_console_listener()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            var keys = new Cli.Hosting.KeyRegistry(new Core.Security.SecretRedactor());

            using IHost host = Cli.Hosting.AgentJoulesHostFactory.Build(null, null, new Cli.CliHostOverrides(WorkingDirectory: workspace.Root), keys, TextWriter.Null, TextWriter.Null);

            _ = Assert.IsType<NoOpJudgeService>(host.Services.GetRequiredService<IJudgeService>());
            _ = Assert.IsType<NoOpLocalRuntime>(host.Services.GetRequiredService<ILocalRuntime>());
            Assert.Same(NotConfiguredCostCalculator.Instance, host.Services.GetRequiredService<ICostCalculator>());
            _ = Assert.IsType<ConsoleRunListener>(host.Services.GetRequiredService<IRunListener>());
        }
    }
}