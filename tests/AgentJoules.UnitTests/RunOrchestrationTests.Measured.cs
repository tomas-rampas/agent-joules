using System.Text.Json;

using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Local;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        public async Task A_concurrent_measured_run_reports_its_energy_totals_as_not_computed_and_still_sums_the_tokens()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, TimerTrackingTimeProvider time, _, _) = LocalRun(workspace);

            CliRunResult result = await RunLocalAsync($"run --tier local --task {TaskNames[0]} --repeat 1 --baseline-seconds 2 --mode concurrent", setup, time);

            AssertExit(0, result);
            using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
            JsonElement totals = document.RootElement.GetProperty("totals");
            Assert.Equal(("not-reported", "union-not-computed"), (Text(totals.GetProperty("grossJoules"), "label"), Text(totals.GetProperty("grossJoules"), "reason")));
            Assert.Equal(("not-reported", "union-not-computed"), (Text(totals.GetProperty("netJoules"), "label"), Text(totals.GetProperty("netJoules"), "reason")));
            Assert.Equal(("measured", 28.0), (Text(totals.GetProperty("inputTokens"), "label"), totals.GetProperty("inputTokens").GetProperty("value").GetDouble()));
            Assert.Equal("concurrent", Text(RunOf(document), "mode"));
        }
    }
}