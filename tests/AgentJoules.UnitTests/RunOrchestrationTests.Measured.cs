using System.Collections.Concurrent;
using System.Text.Json;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Local;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class RunOrchestrationTests
{
    private const string FallbackLine = "Energy method: sampling (counter unsupported)";

    private static string LocalModel => ConfigurationFixtures.ShippedOptions().Tiers["local"].ModelId!;

    // The measured tier on a fake clock: a scripted GPU at the given idle power behind the real NVML binding, the local endpoint and the judge tier's models route, each call taking 1 s.
    private static (RunCommandSetup Setup, TimerTrackingTimeProvider Time, ScriptedNvmlApi Gpu, FakeHttpMessageHandler Local) LocalRun(TempWorkspace workspace, Action<ScriptedNvmlApi>? script = null)
    {
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        var gpu = new ScriptedNvmlApi(time) { ConstantPowerWatts = 30.0 };
        script?.Invoke(gpu);
        FakeHttpMessageHandler local = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1));
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["local"] = local, ["nvidia"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)) });
        setup.Gpu = gpu;
        RunOrchestrationFixtures.UseClock(setup, time);
        return (setup, time, gpu, local);
    }

    private static Task<CliRunResult> RunLocalAsync(string arguments, RunCommandSetup setup, TimerTrackingTimeProvider time) =>
        RunOrchestrationFixtures.RunDrivenAsync(arguments, setup, time).WaitAsync(HangGuard);

    // ---- criterion 5: GPU port, meter start, warm-up, baseline; hosted tiers skip them ----

    [Fact]
    public async Task A_measured_tier_opens_the_GPU_starts_the_meter_warms_up_and_then_measures_the_baseline()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        (RunCommandSetup setup, TimerTrackingTimeProvider time, ScriptedNvmlApi gpu, FakeHttpMessageHandler local) = LocalRun(workspace);
        TimeSpan baseline = TimeSpan.FromSeconds(5);
        var runtime = new RecordingRuntime(gpu, time, baseline);
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<ILocalRuntime>();
            _ = services.AddSingleton<ILocalRuntime>(runtime);
        });
        string task = TaskNames[2];

        CliRunResult result = await RunLocalAsync($"run --tier local --task {task} --repeat 1 --baseline-seconds 5", setup, time);

        AssertExit(0, result);

        // The preflight opened and closed the GPU; the orchestrator opened it again, the meter had sampled before the warm-up, the baseline wait had not started, and every session was released.
        Assert.Equal([$"warm-up {LocalModel} open=True sessions=2 sampled=True baselineStarted=False", "describe"], runtime.Events);
        Assert.Contains(baseline, time.History);
        Assert.Equal(2, gpu.InitCallCount);
        Assert.False(gpu.IsInitialised);

        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        JsonElement root = document.RootElement;
        JsonElement measured = root.GetProperty("baseline");
        Assert.Equal(5.0, measured.GetProperty("seconds").GetDouble());
        Assert.Equal(("measured", 30.0), (Text(measured.GetProperty("meanW"), "label"), measured.GetProperty("meanW").GetProperty("value").GetDouble()));
        Assert.Empty(measured.GetProperty("flags").EnumerateArray());

        JsonElement probe = root.GetProperty("probe");
        Assert.Equal(gpu.Name, Text(probe, "name"));
        Assert.Equal(gpu.Uuid, Text(probe, "gpuUuid"));

        JsonElement environment = root.GetProperty("environment");
        Assert.Equal((gpu.Name, gpu.Uuid, gpu.DriverVersion), (Text(environment, "gpuName"), Text(environment, "gpuUuid"), Text(environment, "driverVersion")));
        Assert.Equal(("recording-runtime", "9.9.9", 100.0, 12.5, "Q4_K_M"), (
            Text(environment, "localRuntime"),
            Text(environment, "runtimeVersion"),
            environment.GetProperty("gpuOffloadPercent").GetDouble(),
            environment.GetProperty("modelLoadSeconds").GetDouble(),
            Text(environment, "precision")));
        Assert.Equal("laptop", Text(environment, "provider"));
        Assert.Equal(("N/A", "not-cloudgpu"), (Text(environment.GetProperty("cloudCostUsd"), "label"), Text(environment.GetProperty("cloudCostUsd"), "reason")));

        // The steps are measured: energy in the file and a number, not a label, on the console.
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
        Assert.Equal(4, steps.Length);
        Assert.All(steps, static step => Assert.Equal("measured", Text(step.GetProperty("grossJoules"), "label")));
        Assert.All(StepLines(result), line => Assert.Matches($"^{task} r1 [A-Za-z]+ ok netJ=-?[0-9.]+ avgW=[0-9.]+ in=7 out=3 latency=1000$", line));
        Assert.Equal(4, local.Count(FakeRoute.ChatCompletions));
    }

    [Fact]
    public async Task Without_a_local_runtime_the_runtime_fields_stay_null_never_0()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        (RunCommandSetup setup, TimerTrackingTimeProvider time, _, _) = LocalRun(workspace);

        CliRunResult result = await RunLocalAsync($"run --tier local --task {TaskNames[0]} --repeat 1 --baseline-seconds 2", setup, time);

        AssertExit(0, result);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        JsonElement environment = document.RootElement.GetProperty("environment");
        foreach (string absent in new[] { "localRuntime", "runtimeVersion", "gpuOffloadPercent", "modelLoadSeconds", "powerSource", "precision" })
        {
            Assert.True(environment.GetProperty(absent).ValueKind == JsonValueKind.Null, absent);
        }

        // A measured serialized run sums the energy of its steps.
        JsonElement totals = document.RootElement.GetProperty("totals");
        Assert.Equal("measured", Text(totals.GetProperty("grossJoules"), "label"));
        double sum = RunOrchestrationFixtures.Steps(document).Sum(static step => step.GetProperty("grossJoules").GetProperty("value").GetDouble());
        Assert.Equal(sum, totals.GetProperty("grossJoules").GetProperty("value").GetDouble(), precision: 9);
    }

    // AJ-072: the Cost section reaches the written document through the orchestrator (BuildEnvironment and Assemble), not only through the assembler's unit tests.
    [Fact]
    public async Task The_cost_options_reach_the_written_document_assumptions_emission_and_costs()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimeSpan baseline = TimeSpan.FromSeconds(5);
        TimerTrackingTimeProvider? clock = null;
        double? start = null;

        // 30 W until the baseline wait is over, 60 W after it: every step draws more than the idle baseline, so its net is positive and its cost exists.
        (RunCommandSetup setup, TimerTrackingTimeProvider time, _, _) = LocalRun(workspace, gpu => gpu.WattsAt = seconds =>
        {
            if (clock is null || !clock.History.Contains(baseline))
            {
                return 30.0;
            }

            start ??= seconds;
            return seconds - start.Value > baseline.TotalSeconds ? 60.0 : 30.0;
        });
        clock = time;
        setup.Settings["AgentJoules:Cost:ElectricityPricePerKwh"] = "0.25";
        setup.Settings["AgentJoules:Cost:Currency"] = "SEK";
        setup.Settings["AgentJoules:Cost:GCo2ePerKwh"] = "400";

        CliRunResult result = await RunLocalAsync($"run --tier local --task {TaskNames[0]} --repeat 1 --baseline-seconds 5", setup, time);

        AssertExit(0, result);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        JsonElement root = document.RootElement;
        JsonElement assumptions = root.GetProperty("environment").GetProperty("assumptions");
        JsonElement price = assumptions.GetProperty("electricityPrice");
        JsonElement factor = assumptions.GetProperty("gco2ePerKwh");
        Assert.Equal(("estimated", "SEK", 0.25), (Text(price, "label"), Text(price, "currency"), price.GetProperty("value").GetDouble()));
        Assert.Equal(("estimated", "gCO2e/kWh", 400.0), (Text(factor, "label"), Text(factor, "unit"), factor.GetProperty("value").GetDouble()));

        JsonElement totals = root.GetProperty("totals");
        JsonElement net = totals.GetProperty("netJoules");
        Assert.Equal("measured", Text(net, "label"));
        double netKwh = net.GetProperty("value").GetDouble() / 3.6e6;
        Assert.True(netKwh > 0.0, "the net energy of the steps is positive");

        JsonElement gco2e = totals.GetProperty("gco2e");
        Assert.Equal(("estimated", "gCO2e"), (Text(gco2e, "label"), Text(gco2e, "unit")));
        Assert.Equal(netKwh * 400.0, gco2e.GetProperty("value").GetDouble(), precision: 9);

        JsonProperty currency = Assert.Single(totals.GetProperty("costByCurrency").EnumerateObject());
        Assert.Equal("SEK", currency.Name);
        Assert.Equal("estimated", Text(currency.Value, "label"));
        Assert.Equal(netKwh * 0.25, currency.Value.GetProperty("value").GetDouble(), precision: 9);
        Assert.All(RunOrchestrationFixtures.Steps(document), static step => Assert.Equal("SEK", Text(step.GetProperty("cost"), "currency")));
    }

    [Fact]
    public async Task A_hosted_tier_never_opens_the_GPU_or_calls_the_runtime()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        var gpu = new ScriptedNvmlApi(time);
        var runtime = new RecordingRuntime(gpu, time, TimeSpan.FromSeconds(30));
        RunCommandSetup setup = NvidiaRun(workspace, RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)));
        setup.Gpu = gpu;
        RunOrchestrationFixtures.UseClock(setup, time);
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<ILocalRuntime>();
            _ = services.AddSingleton<ILocalRuntime>(runtime);
        });

        CliRunResult result = await RunAsync($"run --tier nvidia --task {TaskNames[0]} --repeat 1", setup);

        AssertExit(0, result);
        Assert.Equal(0, gpu.InitCallCount);
        Assert.Empty(runtime.Events);

        // The host's clock is the tracked one: no sampling timer and no baseline wait was ever created on it (a measured tier creates both).
        Assert.Empty(time.History);
        Assert.True(time.GetUtcNow() > RunOrchestrationFixtures.CommandStartUtc, "The run did not use the tracked clock.");
    }

    // ---- T7: the sampling-fallback line once per command ----

    [Fact]
    public async Task A_measured_run_prints_the_fallback_line_once()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        (RunCommandSetup setup, TimerTrackingTimeProvider time, _, _) = LocalRun(workspace, static gpu => gpu.CounterSupported = false);

        CliRunResult result = await RunLocalAsync($"run --tier local --task {TaskNames[0]} --repeat 1 --baseline-seconds 2", setup, time);

        AssertExit(0, result);
        _ = Assert.Single(result.StderrLines, static line => line == FallbackLine);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        Assert.All(RunOrchestrationFixtures.Steps(document), static step => Assert.Contains("sampling-fallback", step.GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString())));
    }

    [Fact]
    public async Task Two_measured_tiers_print_the_fallback_line_once()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        var gpu = new ScriptedNvmlApi(RunOrchestrationFixtures.Clock()) { CounterSupported = false };
        var endpoints = new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
        {
            ["local"] = RunOrchestrationFixtures.Endpoint(),
            ["cloudgpu"] = RunOrchestrationFixtures.Endpoint(),
            ["nvidia"] = RunOrchestrationFixtures.Endpoint(),
            ["nebius"] = RunOrchestrationFixtures.Endpoint(),
        };
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            workspace.Root,
            endpoints,
            ("AgentJoules:Tiers:cloudgpu:Enabled", "true"),
            ("AgentJoules:CloudGpu:RequireSshVariables", "false"));
        setup.Gpu = gpu;

        CliRunResult result = await RunAsync("run --tier all --dry-run", setup);

        AssertExit(0, result);
        Assert.Equal(4, RunOrchestrationFixtures.StdoutLines(result).Length);
        _ = Assert.Single(result.StderrLines, static line => line == FallbackLine);
    }

    // Records the calls of the runtime seam with what the GPU and the clock showed at that moment, and states a description and a load time of its own.
    private sealed class RecordingRuntime(ScriptedNvmlApi gpu, TimerTrackingTimeProvider time, TimeSpan baseline) : ILocalRuntime
    {
        private readonly List<string> _events = [];

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public string Name => "recording";

        public Task<LocalRuntimeInfo> DescribeAsync(CancellationToken cancellationToken)
        {
            Add("describe");
            return Task.FromResult(new LocalRuntimeInfo("recording-runtime", "9.9.9", 100.0, "Q4_K_M"));
        }

        public Task<TimeSpan?> WarmUpAsync(string modelId, CancellationToken cancellationToken)
        {
            Add($"warm-up {modelId} open={gpu.IsInitialised} sessions={gpu.InitCallCount} sampled={gpu.PowerReadCount > 0} baselineStarted={time.History.Contains(baseline)}");
            return Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(12.5));
        }

        private void Add(string entry)
        {
            lock (_events)
            {
                _events.Add(entry);
            }
        }
    }

    /// <summary>
    /// AJ-056 criterion 9 (EDGE-005, carried from AJ-030 and AJ-034): an unstable idle baseline is printed as a warning through the console reporter on the redacting standard-error writer and
    /// recorded as <c>baseline-unstable</c> in the run document, and the steps still carry their gross and net values. The scripted GPU alternates 20 W and 30 W per sampling period (coefficient
    /// of variation 0.2, above the default 0.10); the control draws a constant power.
    /// </summary>
    [Trait("Spec", "EDGE-005")]
    public sealed class UnstableBaseline
    {
        private static double Alternating(double seconds) => (long)Math.Round(seconds * 10.0) % 2 == 0 ? 20.0 : 30.0;

        [Fact]
        public async Task An_unstable_baseline_prints_the_warning_and_flags_the_run_file()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, TimerTrackingTimeProvider time, _, _) = LocalRun(workspace, static gpu => gpu.WattsAt = Alternating);

            CliRunResult result = await RunLocalAsync($"run --tier local --task {TaskNames[0]} --repeat 1 --baseline-seconds 5", setup, time);

            AssertExit(0, result);
            string warning = Assert.Single(result.StderrLines, static line => line.Contains("baseline", StringComparison.Ordinal) && line.Contains("unstable", StringComparison.Ordinal));
            Assert.Equal("Warning: idle baseline unstable (CV 0.20 > 0.10)", warning);
            Assert.DoesNotContain("unstable", result.Stdout, StringComparison.Ordinal);

            using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
            JsonElement baseline = document.RootElement.GetProperty("baseline");
            Assert.Equal(["baseline-unstable"], baseline.GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()));
            // An odd number of alternating samples: the mean is near 25 W, not exactly 25 W.
            Assert.InRange(baseline.GetProperty("meanW").GetProperty("value").GetDouble(), 24.5, 25.5);

            // Gross and net are still reported.
            Assert.All(RunOrchestrationFixtures.Steps(document), static step =>
            {
                Assert.Equal("measured", Text(step.GetProperty("grossJoules"), "label"));
                Assert.Equal("measured", Text(step.GetProperty("netJoules"), "label"));
            });
        }

        [Fact]
        public async Task A_stable_baseline_prints_no_warning_and_no_flag()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, TimerTrackingTimeProvider time, _, _) = LocalRun(workspace, static gpu => gpu.ConstantPowerWatts = 25.0);

            CliRunResult result = await RunLocalAsync($"run --tier local --task {TaskNames[0]} --repeat 1 --baseline-seconds 5", setup, time);

            AssertExit(0, result);
            Assert.DoesNotContain("unstable", result.Stderr, StringComparison.Ordinal);
            using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
            Assert.Empty(document.RootElement.GetProperty("baseline").GetProperty("flags").EnumerateArray());
        }
    }

    /// <summary>
    /// AJ-056 and REQ-013: <c>--mode</c> chooses the one meter gate of the process (the run command registers it through its service hook) and the run records the mode it ran in.
    /// </summary>
    [Trait("Spec", "REQ-013")]
    public sealed class Modes
    {
        [Theory]
        [InlineData("serialized", typeof(SerializedMeterGate))]
        [InlineData("concurrent", typeof(NoOpMeterGate))]
        public async Task The_mode_registers_its_gate_and_the_run_records_it(string mode, Type gateType)
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            RunCommandSetup setup = NvidiaRun(workspace, RunOrchestrationFixtures.Endpoint());
            int gates = -1;
            IMeterGate? resolved = null;
            RunOrchestrationFixtures.AddServices(setup, services =>
            {
                ServiceDescriptor[] registered = [.. services.Where(static descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IMeterGate))];
                gates = registered.Length;
                if (registered.Length == 1)
                {
                    ServiceDescriptor original = registered[0];
                    _ = services.Remove(original);
                    _ = services.AddSingleton<IMeterGate>(provider =>
                        resolved = original.ImplementationInstance as IMeterGate ?? (IMeterGate)original.ImplementationFactory!(provider));
                }
            });

            CliRunResult result = await RunAsync($"run --tier nvidia --task {TaskNames[0]} --repeat 1 --mode {mode}", setup);

            AssertExit(0, result);
            Assert.Equal(1, gates);
            Assert.IsType(gateType, resolved);
            using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
            Assert.Equal(mode, Text(RunOf(document), "mode"));
            Assert.Equal(4, RunOrchestrationFixtures.Steps(document).Length);
        }

        [Fact]
        [Trait("Spec", "EDGE-004")]
        public async Task A_concurrent_measured_run_totals_its_energy_over_the_union_of_the_step_windows_and_flags_no_touching_step()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, TimerTrackingTimeProvider time, _, _) = LocalRun(workspace);
            var told = new ConcurrentQueue<StepReading>();
            RecordReadings(setup, told);

            CliRunResult result = await RunLocalAsync($"run --tier local --task {TaskNames[0]} --repeat 1 --baseline-seconds 2 --mode concurrent", setup, time);

            AssertExit(0, result);
            using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
            Assert.Equal("concurrent", Text(RunOf(document), "mode"));
            JsonElement totals = document.RootElement.GetProperty("totals");
            Assert.Equal(("measured", 28.0), (Text(totals.GetProperty("inputTokens"), "label"), totals.GetProperty("inputTokens").GetProperty("value").GetDouble()));

            // The windows the meter recorded, by agent. The scripted GPU draws a constant 30 W, so the union energy is 30 W times the length of the union of
            // the windows: the sum over the merged segments of (segment stop - segment start), a segment being a maximal run of strictly overlapping windows.
            Dictionary<string, WindowSpan> windows = told.ToDictionary(static step => step.Agent, static step => step.Window ?? throw new InvalidOperationException("A reading carries no window."));
            Assert.Equal(4, windows.Count);
            double unionSeconds = UnionSecondsOf(windows.Values);
            JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
            double sum = steps.Sum(static step => step.GetProperty("grossJoules").GetProperty("value").GetDouble());
            JsonElement gross = totals.GetProperty("grossJoules");
            Assert.Equal("measured", Text(gross, "label"));
            Assert.Equal(30.0 * unionSeconds, gross.GetProperty("value").GetDouble(), precision: 6);

            // Measured (6 of 6 runs): on this fake clock driver the fan-out branches do not overlap. The fake endpoint moves the clock forward inside the call
            // (1 s per completion) before the call returns, so the first branch window closes at the instant the second one opens: the four windows follow one
            // another and only touch (Planner [t, t+1], one branch [t+1, t+2], the other [t+2, t+3], Reviewer [t+3, t+4]). Touching is not overlapping, so the
            // union is 4 s, its energy equals the sum of the four 1 s readings (120 J) and no step is flagged; the overlapping case is proved on the
            // deterministic meter (ExecutionModeTests.Concurrent_total_is_union_not_sum) and through the assembler (RunOrchestrationTests.Assembler).
            WindowSpan[] ordered = [.. windows.Values.OrderBy(static span => span.StartSeconds)];
            for (int i = 1; i < ordered.Length; i++)
            {
                Assert.Equal(ordered[i - 1].StopSeconds, ordered[i].StartSeconds);
            }

            Assert.Equal(4.0, unionSeconds, precision: 9);
            Assert.Equal(120.0, gross.GetProperty("value").GetDouble(), precision: 6);
            Assert.Equal(sum, gross.GetProperty("value").GetDouble(), precision: 6);

            // The file flags exactly the steps whose recorded window strictly overlaps another one: here none.
            foreach (JsonElement step in steps)
            {
                string agent = Text(step, "agent")!;
                bool overlaps = windows.Any(other => other.Key != agent && UnionWindowCalculator.Overlaps(other.Value, windows[agent]));
                bool shared = step.GetProperty("flags").EnumerateArray().Any(static flag => flag.GetString() == "shared-window");
                Assert.Equal(overlaps, shared);
                Assert.False(shared, agent);
            }

            // The net is the union gross minus the measured 30 W baseline times the 4 s union: 0 J, and the sum of the step nets.
            double netSum = steps.Sum(static step => step.GetProperty("netJoules").GetProperty("value").GetDouble());
            JsonElement net = totals.GetProperty("netJoules");
            Assert.Equal("measured", Text(net, "label"));
            Assert.Equal(0.0, net.GetProperty("value").GetDouble(), precision: 6);
            Assert.Equal(netSum, net.GetProperty("value").GetDouble(), precision: 6);
        }

        [Fact]
        [Trait("Spec", "EDGE-004")]
        public async Task A_concurrent_measured_run_whose_fan_out_calls_overlap_flags_both_branches_and_totals_the_union_below_the_sum()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, TimerTrackingTimeProvider time, FanOutBarrierHandler _) = ConcurrentLocalRun(workspace);
            var told = new ConcurrentQueue<StepReading>();
            RecordReadings(setup, told);

            CliRunResult result = await RunLocalAsync($"run --tier local --task {TaskNames[0]} --repeat 1 --baseline-seconds 2 --mode concurrent", setup, time);

            AssertExit(0, result);
            using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
            Dictionary<string, WindowSpan> windows = told.ToDictionary(static step => step.Agent, static step => step.Window ?? throw new InvalidOperationException("A reading carries no window."));

            // Recorded: Planner [t, t+1], Researcher and Writer both [t+1, t+2] (held until both had arrived, then released by one advance), Reviewer [t+2, t+3]. Only
            // the handler moves the clock once the calls start: the driver advances for the baseline wait alone, never for the meter's periodic sampling timer, so no stray
            // sampling period can land inside a window (TimerTrackingTimeProvider).
            Assert.All(windows.Values, static span => Assert.Equal(1.0, span.StopSeconds - span.StartSeconds, precision: 9));
            Assert.Equal(windows["Researcher"], windows["Writer"]);
            Assert.Equal(windows["Planner"].StopSeconds, windows["Researcher"].StartSeconds);
            Assert.Equal(windows["Writer"].StopSeconds, windows["Reviewer"].StartSeconds);
            double unionSeconds = UnionSecondsOf(windows.Values);
            Assert.Equal(3.0, unionSeconds, precision: 9);

            // The union is 30 W x 3 s = 90 J against a sum of four 1 s readings of 30 J each (120 J).
            JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
            double sum = steps.Sum(static step => step.GetProperty("grossJoules").GetProperty("value").GetDouble());
            JsonElement gross = document.RootElement.GetProperty("totals").GetProperty("grossJoules");
            Assert.Equal("measured", Text(gross, "label"));
            Assert.Equal(30.0 * unionSeconds, gross.GetProperty("value").GetDouble(), precision: 6);
            Assert.Equal(90.0, gross.GetProperty("value").GetDouble(), precision: 6);
            Assert.Equal(120.0, sum, precision: 6);
            Assert.True(gross.GetProperty("value").GetDouble() < sum);
            Assert.Equal(new Dictionary<string, bool> { ["Planner"] = false, ["Researcher"] = true, ["Writer"] = true, ["Reviewer"] = false }, SharedByAgent(steps));
        }

        [Fact]
        [Trait("Spec", "EDGE-004")]
        public async Task A_shutdown_of_a_concurrent_measured_run_writes_union_totals_and_the_flags_of_the_overlapping_steps()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, TimerTrackingTimeProvider time, FanOutBarrierHandler local) = ConcurrentLocalRun(workspace, holdCompletion: 4);
            var told = new ConcurrentQueue<StepReading>();
            RecordReadings(setup, told);
            using var cancellation = new CancellationTokenSource();

            try
            {
                // The Reviewer call (completion 4) is held after the two overlapping branches finished; the shutdown comes while it is in flight.
                Task<CliRunResult> run = RunOrchestrationFixtures.RunDrivenAsync($"run --tier local --task {TaskNames[0]} --repeat 1 --baseline-seconds 2 --mode concurrent", setup, time, cancellation.Token);
                await local.Held.WaitAsync(HangGuard);
                await cancellation.CancelAsync();
                CliRunResult result = await run.WaitAsync(HangGuard);
                AssertExit(5, result);
            }
            finally
            {
                // A wait that timed out must still release the held completion, which holds the run gate of the harness until its token fires.
                await cancellation.CancelAsync();
            }

            using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
            Assert.Equal("incomplete", Text(RunOf(document), "status"));
            JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
            Dictionary<string, bool> shared = SharedByAgent(steps);
            Assert.True(shared["Researcher"]);
            Assert.True(shared["Writer"]);
            Assert.False(shared["Planner"]);

            // The incomplete write takes its energy from the union of the windows of the steps it holds, never union-not-computed: here Planner [t, t+1] and the two
            // branches [t+1, t+2], so 30 W x 2 s = 60 J against a sum of 90 J for those three readings (a Reviewer reading, if its window got a length, only touches them).
            Dictionary<string, WindowSpan> windows = told.ToDictionary(static step => step.Agent, static step => step.Window ?? throw new InvalidOperationException("A reading carries no window."));
            Assert.Equal(steps.Length, windows.Count);
            double unionSeconds = UnionSecondsOf(windows.Values);
            double sum = steps.Sum(static step => step.GetProperty("grossJoules").GetProperty("value").GetDouble());
            JsonElement gross = document.RootElement.GetProperty("totals").GetProperty("grossJoules");
            Assert.Equal("measured", Text(gross, "label"));
            Assert.Equal(30.0 * unionSeconds, gross.GetProperty("value").GetDouble(), precision: 6);
            Assert.True(gross.GetProperty("value").GetDouble() < sum);
        }

        // The measured tier on the fake clock with a local endpoint whose fan-out completions overlap (FanOutBarrierHandler), the judge tier's models route as in LocalRun.
        private static (RunCommandSetup Setup, TimerTrackingTimeProvider Time, FanOutBarrierHandler Local) ConcurrentLocalRun(TempWorkspace workspace, int holdCompletion = 0)
        {
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
            var gpu = new ScriptedNvmlApi(time) { ConstantPowerWatts = 30.0 };
            var local = new FanOutBarrierHandler(RunOrchestrationFixtures.Endpoint(time), time, holdCompletion);
            RunCommandSetup setup = RunOrchestrationFixtures.Production(
                workspace.Root,
                new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["local"] = local, ["nvidia"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)) });
            setup.Gpu = gpu;
            RunOrchestrationFixtures.UseClock(setup, time);
            return (setup, time, local);
        }

        // Records the readings as the orchestrator tells them, with their in-memory windows; the console listener still prints every line.
        private static void RecordReadings(RunCommandSetup setup, ConcurrentQueue<StepReading> told) =>
            RunOrchestrationFixtures.AddServices(setup, services =>
            {
                ServiceDescriptor original = Assert.Single(services, static descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IRunListener));
                _ = services.Remove(original);
                _ = services.AddSingleton<IRunListener>(provider => new RecordingRunListener(
                    (IRunListener)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!),
                    told));
            });

        // Whether each step of the file carries shared-window, by agent.
        private static Dictionary<string, bool> SharedByAgent(JsonElement[] steps) =>
            steps.ToDictionary(
                static step => Text(step, "agent")!,
                static step => step.GetProperty("flags").EnumerateArray().Any(static flag => flag.GetString() == "shared-window"));

        // The length of the union of the spans, by a sweep over them in start order (the strict rule: a span that starts where the previous segment stopped begins a new segment).
        private static double UnionSecondsOf(IEnumerable<WindowSpan> spans)
        {
            double total = 0.0;
            double? start = null;
            double stop = 0.0;
            foreach (WindowSpan span in spans.OrderBy(static span => span.StartSeconds))
            {
                if (start is not null && span.StartSeconds < stop)
                {
                    stop = Math.Max(stop, span.StopSeconds);
                    continue;
                }

                total += start is { } open ? stop - open : 0.0;
                (start, stop) = (span.StartSeconds, span.StopSeconds);
            }

            return total + (start is { } last ? stop - last : 0.0);
        }

        // The local endpoint of a concurrent run whose fan-out calls overlap. Chat completions 2 and 3 (the two branches) wait for each other; the one that arrives
        // second moves the fake clock by one call duration, once, and then both are answered, so both windows open at the same instant and close one second later.
        // Every other completion moves the clock by one call duration itself; the fake behind it moves no clock. Completion holdCompletion (0 for none) is held
        // until its token is cancelled, like HoldingHttpMessageHandler. The waits are bounded on the real clock, so a run whose branches never meet fails instead
        // of hanging.
        private sealed class FanOutBarrierHandler(FakeHttpMessageHandler inner, FakeTimeProvider clock, int holdCompletion) : DelegatingHandler(inner)
        {
            private static readonly TimeSpan CallDuration = TimeSpan.FromSeconds(1);
            private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

            private readonly TaskCompletionSource _together = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _completions;
            private int _branches;

            // Completes when the held completion arrived.
            public Task Held => _held.Task;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true)
                {
                    int number = Interlocked.Increment(ref _completions);
                    if (number == holdCompletion)
                    {
                        _ = _held.TrySetResult();
                        await Task.Delay(MaxWait, cancellationToken).ConfigureAwait(false);
                        throw new TimeoutException($"{nameof(FanOutBarrierHandler)} held completion {number} and no cancellation came.");
                    }

                    if (number is 2 or 3)
                    {
                        if (Interlocked.Increment(ref _branches) == 2)
                        {
                            clock.Advance(CallDuration);
                            _ = _together.TrySetResult();
                        }

                        await _together.Task.WaitAsync(MaxWait, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        clock.Advance(CallDuration);
                    }
                }

                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        // Records every reading it is told and forwards every event to the listener of the command.
        private sealed class RecordingRunListener(IRunListener inner, ConcurrentQueue<StepReading> told) : IRunListener
        {
            public void StepFinished(StepReading reading)
            {
                told.Enqueue(reading);
                inner.StepFinished(reading);
            }

            public void CheckpointFailed(string exceptionTypeName, bool previousCheckpointKept, CheckpointKind kind) => inner.CheckpointFailed(exceptionTypeName, previousCheckpointKept, kind);

            public void TierFailed(string displayPath, string agent, RunStatus status) => inner.TierFailed(displayPath, agent, status);

            public void BaselineMeasured(BaselineResult baseline) => inner.BaselineMeasured(baseline);

            public void TierCompleted(string displayPath, Totals totals) => inner.TierCompleted(displayPath, totals);

            public void JudgeFailed(Tier tier, int failedSteps, int judgedSteps, IReadOnlyList<string> failureKinds) => inner.JudgeFailed(tier, failedSteps, judgedSteps, failureKinds);
        }
    }
}