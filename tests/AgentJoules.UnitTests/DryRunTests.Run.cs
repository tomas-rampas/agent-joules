using System.Runtime.InteropServices;
using System.Text.Json;

using AgentJoules.Cli.Hosting;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Runs;
using AgentJoules.Dashboard;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Providers;
using AgentJoules.Workflow.Runs;
using AgentJoules.Workflow.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class DryRunTests
{
    /// <summary>
    /// AJ-054 (REQ-017, REQ-014, EDGE-001): the <c>run</c> command, its options, its parse errors (exit 2 before any host), the task set it loads from <c>Paths:Tasks</c>, the dry run
    /// (keys, models and the GPU of a measured tier, one line per tier, no completion call, no file), the order of the checks outside a dry run (keys, writers, models, GPU, then the
    /// orchestrator), the composition root and the telemetry the command registers. A nested class, so that the <c>Spec=EDGE-011</c> trait of <see cref="DryRunTests"/> does not tag these
    /// tests; the helpers are the private static members of <see cref="DryRunTests"/>.
    /// </summary>
    [Trait("Spec", "REQ-017")]
    public sealed class Run
    {
        private static readonly string[] NineOptions = ["--tier", "--task", "--mode", "--out", "--baseline-seconds", "--repeat", "--dry-run", "--dashboard", "--dashboard-port"];

        private static string LocalBaseUrl => Shipped.Tiers["local"].BaseUrl!;

        private static string[] TaskNames => [.. TaskSetLoader.Load(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunCommandHarness.TasksFolder)).Select(static task => task.Name)];

        private static string[] StdoutLines(CliRunResult result) => [.. result.Stdout.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0)];

        private static string LibraryName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "nvml.dll" : "libnvidia-ml.so.1";

        // The tiers of --tier all on the shipped configuration (local, nvidia, nebius): both keys, a fake models route per tier that lists every shipped ID, and a scripted GPU for local.
        private static (RunCommandSetup Setup, Dictionary<string, FakeHttpMessageHandler> Handlers) AllTiers(ScriptedNvmlApi? gpu = null)
        {
            var handlers = new Dictionary<string, FakeHttpMessageHandler>(StringComparer.Ordinal)
            {
                ["local"] = Lists(ConfigurationFixtures.ShippedModelIds()),
                ["nvidia"] = Lists(ConfigurationFixtures.ShippedModelIds()),
                ["nebius"] = Lists(ConfigurationFixtures.ShippedModelIds()),
            };

            RunCommandSetup setup = Setup(
                Settings(("NVIDIA_API_KEY", Key()), ("NEBIUS_API_KEY", Key())),
                handlers.ToDictionary(static pair => pair.Key, static pair => (HttpMessageHandler)pair.Value, StringComparer.Ordinal),
                gpu ?? ScriptedGpu());
            return (setup, handlers);
        }

        // One hosted tier (nvidia, also the judge tier) with its key and a fake endpoint that lists the shipped IDs.
        private static (RunCommandSetup Setup, FakeHttpMessageHandler Nvidia) NvidiaOnly(params (string Key, string? Value)[] extra)
        {
            FakeHttpMessageHandler nvidia = Lists(ConfigurationFixtures.ShippedModelIds());
            RunCommandSetup setup = Setup(Settings([("NVIDIA_API_KEY", Key()), .. extra]), new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });
            return (setup, nvidia);
        }

        // The local tier with a scripted GPU, and the judge tier (nvidia) with its key, so a dry run passes the key check.
        private static (RunCommandSetup Setup, FakeHttpMessageHandler Local) LocalTier(ScriptedNvmlApi? gpu, params (string Key, string? Value)[] extra)
        {
            FakeHttpMessageHandler local = Lists(ConfigurationFixtures.ShippedModelIds());
            RunCommandSetup setup = Setup(
                Settings([("NVIDIA_API_KEY", Key()), .. extra]),
                new Dictionary<string, HttpMessageHandler> { ["local"] = local, ["nvidia"] = Lists(JudgeModel) },
                gpu);
            return (setup, local);
        }

        private static Task<CliRunResult> RunAsync(string arguments, RunCommandSetup setup) => RunCommandHarness.RunAsync(arguments, setup).WaitAsync(HangGuard);

        // ---- criterion 1: the nine options (REQ-017 with the two dashboard options of REQ-032, AJ-091 T-1) ----

        [Fact]
        public async Task Help_lists_nine_options()
        {
            CliRunResult result = await CliHarness.RunAsync("run --help").WaitAsync(HangGuard);

            Assert.Equal(0, result.ExitCode);
            string[] tokens = result.Stdout.Split([' ', '\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries);
            Assert.All(NineOptions, option => Assert.Contains(option, tokens));
            Assert.Contains("--config", tokens);
            Assert.Contains("--no-console-telemetry", tokens);

            // The default of --dashboard-port is asserted here and through DashboardServer.DefaultPort only (P-79).
            Assert.Contains($"[default: {DashboardServer.DefaultPort}]", result.Stdout, StringComparison.Ordinal);
            Assert.Equal(5178, DashboardServer.DefaultPort);
            Assert.Empty(result.Stderr);
        }

        [Fact]
        public async Task The_root_help_lists_the_run_command()
        {
            CliRunResult result = await CliHarness.RunAsync("--help").WaitAsync(HangGuard);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(result.Stdout.Split('\n'), static line => line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim().Split(' ')[0] == "run");
        }

        // ---- criterion 2: one line per tier with the endpoint, the matched IDs and OK ----

        [Fact]
        public async Task Tier_all_dry_run_prints_the_endpoint_the_matched_ids_and_OK_per_enabled_tier_in_order()
        {
            (RunCommandSetup setup, _) = AllTiers();

            CliRunResult result = await RunAsync("run --tier all --dry-run", setup);

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Equal(
                [
                    $"local {LocalBaseUrl} {ModelOf("local")} OK",
                    $"nvidia {NvidiaBaseUrl} {ModelOf("nvidia")} OK",
                    $"nebius {NebiusBaseUrl} {ModelOf("nebius")} OK",
                ],
                StdoutLines(result));
            Assert.Empty(result.ErrorLines);
            Assert.Empty(result.DeniedRequests);
            Assert.Equal(0, setup.Orchestrator.Runs);
        }

        [Fact]
        public async Task The_matched_ids_of_a_tier_are_joined_by_a_comma_in_configuration_order()
        {
            FakeHttpMessageHandler nebius = Lists(ModelOf("nebius"), "acme/writer-model", "acme/unused");
            RunCommandSetup setup = Setup(
                Settings(("NVIDIA_API_KEY", Key()), ("NEBIUS_API_KEY", Key()), ("AgentJoules:Tiers:nebius:AgentModelIds:Writer", "acme/writer-model")),
                new Dictionary<string, HttpMessageHandler> { ["nebius"] = nebius, ["nvidia"] = Lists(JudgeModel) });

            CliRunResult result = await RunAsync("run --tier nebius --dry-run", setup);

            // The judge tier (nvidia) was asked too, but only the selected tier has a line.
            Assert.Equal(0, result.ExitCode);
            Assert.Equal([$"nebius {NebiusBaseUrl} {ModelOf("nebius")},acme/writer-model OK"], StdoutLines(result));
        }

        // ---- criterion 3: a dry run never calls a completions route ----

        [Fact]
        public async Task Dry_run_never_calls_completions()
        {
            (RunCommandSetup setup, Dictionary<string, FakeHttpMessageHandler> handlers) = AllTiers();

            CliRunResult result = await RunAsync("run --tier all --dry-run", setup);

            Assert.Equal(0, result.ExitCode);
            RecordedRequest[] requests = [.. handlers.Values.SelectMany(static handler => handler.Requests)];
            Assert.DoesNotContain(requests, static request => request.Uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal));
            Assert.All(handlers.Values, static handler => Assert.Equal(1, handler.Count(FakeRoute.Models)));
            Assert.Equal(0, setup.Orchestrator.Runs);

            // The negative control: the same counting sees the one completion request of a run outside --dry-run.
            (RunCommandSetup control, FakeHttpMessageHandler nvidia) = NvidiaOnly();
            CliRunResult measured = await RunAsync("run --tier nvidia", control);

            Assert.Equal(0, measured.ExitCode);
            Assert.Single(nvidia.Requests, static request => request.Uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal));
            Assert.Equal(1, control.Orchestrator.Runs);
        }

        // ---- criterion 4: invalid values are parse errors, exit 2 before any host ----

        // The rule texts of the parser and of the run command's validators; each row asserts the one it must fail for, so an unrecognized command cannot pass it.
        private const string NotAmongTheValues = "Must be one of";
        private const string RepeatRule = "--repeat must be 1 or more";
        private const string BaselineRule = "--baseline-seconds must be greater than 0 and at most 4294967.294, the rule of Baseline:Seconds";
        private const string OutRule = "--out must not be blank";

        [Theory]
        [InlineData("run --tier bogus", NotAmongTheValues)]
        [InlineData("run --tier nvidia --mode bogus", NotAmongTheValues)]
        [InlineData("run --tier nvidia --repeat 0", RepeatRule)]
        [InlineData("run --tier nvidia --baseline-seconds -1", BaselineRule)]
        [InlineData("run --tier nvidia --baseline-seconds 0", BaselineRule)]
        [InlineData("run --tier nvidia --baseline-seconds 4294968", BaselineRule)]
        [InlineData("run --tier nvidia --mode Serialized", NotAmongTheValues)]
        [InlineData("run --tier nvidia --mode parallel", NotAmongTheValues)]
        [InlineData("run --tier nvidia --repeat -2", RepeatRule)]
        [InlineData("run --tier nvidia --repeat two", "--repeat must be a whole number")]
        [InlineData("run --tier nvidia --baseline-seconds two", "--baseline-seconds must be a whole number of seconds")]
        [InlineData("run --tier nvidia --dry-run --repeat 0", RepeatRule)]
        [InlineData("run --tier nvidia --out \"\"", OutRule)]
        [InlineData("run --tier nvidia --out \"   \"", OutRule)]
        [InlineData("run --dry-run", "'--tier' is required")]
        public async Task Invalid_values_exit_2_before_any_host_and_any_request(string arguments, string rule)
        {
            (RunCommandSetup setup, FakeHttpMessageHandler nvidia) = NvidiaOnly();

            CliRunResult result = await RunAsync(arguments, setup);

            Assert.Equal(2, result.ExitCode);
            Assert.Equal(0, setup.HostBuilds);
            Assert.Equal(0, nvidia.RequestCount);
            Assert.Equal(0, setup.Orchestrator.Runs);
            Assert.Empty(result.Stdout);
            Assert.Contains(rule, result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("Unrecognized command", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("System.", result.Stderr, StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_tier_rule_lists_the_allowed_values_with_spaces_and_no_replaced_tab()
        {
            CliRunResult result = await RunAsync("run --tier bogus", NvidiaOnly().Setup);

            Assert.Equal(2, result.ExitCode);
            string line = Assert.Single(result.StderrLines, static line => line.Length > 0);
            Assert.StartsWith("error: ", line, StringComparison.Ordinal);
            Assert.EndsWith("Must be one of: 'local' 'cloudgpu' 'nvidia' 'nebius' 'all' (ParseError)", line, StringComparison.Ordinal);
            Assert.DoesNotContain('?', line);
        }

        // ---- --baseline-seconds: the key Baseline:Seconds unless the flag is given ----

        [Fact]
        public async Task Without_the_flag_the_baseline_is_Baseline_Seconds_and_the_flag_wins_over_it()
        {
            (RunCommandSetup setup, _) = NvidiaOnly(("AgentJoules:Baseline:Seconds", "60"));

            CliRunResult keyOnly = await RunAsync("run --tier nvidia", setup);

            Assert.Equal(0, keyOnly.ExitCode);
            Assert.Equal(60.0, setup.Orchestrator.LastPlan!.BaselineSeconds);

            CliRunResult flag = await RunAsync("run --tier nvidia --baseline-seconds 45", setup);

            Assert.Equal(0, flag.ExitCode);
            Assert.Equal(45.0, setup.Orchestrator.LastPlan!.BaselineSeconds);
        }

        // ---- the key check comes before the writers ----

        [Fact]
        public async Task A_missing_key_exits_3_before_the_writers_even_when_out_cannot_be_written()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            _ = workspace.WriteFile("occupied", "this path is a file");
            FakeHttpMessageHandler nebius = Lists(ConfigurationFixtures.ShippedModelIds());
            RunCommandSetup setup = Setup(Settings(("NVIDIA_API_KEY", Key())), new Dictionary<string, HttpMessageHandler> { ["nebius"] = nebius });
            setup.WorkingDirectory = workspace.Root;

            CliRunResult occupied = await RunAsync("run --tier nebius --out occupied", setup);
            CliRunResult defaultOut = await RunAsync("run --tier nebius", setup);

            Assert.Equal(3, occupied.ExitCode);
            Assert.EndsWith("(MissingKeyException)", Assert.Single(occupied.ErrorLines), StringComparison.Ordinal);
            Assert.Equal(3, defaultOut.ExitCode);
            Assert.False(Directory.Exists(Path.Combine(workspace.Root, "results")));
            Assert.Equal(0, nebius.RequestCount);
            Assert.Equal(0, setup.Orchestrator.Runs);
        }

        // ---- the shipped model IDs (the live checks of 2026-10-21 and 2026-10-24) ----

        [Fact]
        public void The_shipped_nvidia_and_judge_model_is_the_ID_the_live_endpoint_lists()
        {
            // The id the live endpoint listed and answered on 2026-10-24 (the user's decision 2 after the M3 exit);
            // the previously shipped nvidia/nemotron-nano-3-30b-a3b is listed but refused for the account (HTTP 404).
            const string verified = "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning";

            Assert.Equal(verified, ModelOf("nvidia"));
            Assert.Equal(verified, JudgeModel);

            // The committed fixtures of 2026-01-01 keep the id they were recorded with; it is data, not the shipped configuration.
            Assert.Equal("nvidia/nemotron-nano-3-30b-a3b", RunCheckpointFixtures.NemotronModel);
        }

        // ---- the task set: --task and Paths:Tasks ----

        [Fact]
        public async Task An_unknown_task_exits_2_listing_the_available_names_and_a_known_task_passes()
        {
            (RunCommandSetup setup, FakeHttpMessageHandler nvidia) = NvidiaOnly();

            CliRunResult unknown = await RunAsync("run --tier nvidia --dry-run --task bogus", setup);

            Assert.Equal(2, unknown.ExitCode);
            string error = Assert.Single(unknown.ErrorLines);
            Assert.Contains("unknown task bogus", error, StringComparison.Ordinal);
            Assert.All(TaskNames, name => Assert.Contains(name, error, StringComparison.Ordinal));
            Assert.Equal(0, nvidia.RequestCount);

            CliRunResult known = await RunAsync("run --tier nvidia --dry-run --task " + TaskNames[1], setup);
            Assert.Equal(0, known.ExitCode);
        }

        [Fact]
        public async Task A_missing_tasks_folder_exits_2_naming_only_the_key()
        {
            using TempWorkspace workspace = TempWorkspace.Create();
            (RunCommandSetup setup, FakeHttpMessageHandler nvidia) = NvidiaOnly();
            setup.WorkingDirectory = workspace.Root;

            CliRunResult result = await RunAsync("run --tier nvidia --dry-run", setup);

            Assert.Equal(2, result.ExitCode);
            string error = Assert.Single(result.ErrorLines);
            Assert.Contains("Paths:Tasks", error, StringComparison.Ordinal);
            Assert.DoesNotContain(workspace.Root, result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFileName(workspace.Root), result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, nvidia.RequestCount);
        }

        [Theory]
        [InlineData("../tasks")]
        [InlineData("tasks/../../outside")]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData("<absolute>")]
        public async Task A_rooted_or_climbing_Paths_Tasks_is_refused_naming_only_the_key(string value)
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            string configured = value == "<absolute>" ? Path.Combine(workspace.Root, RunCommandHarness.TasksFolder) : value;
            (RunCommandSetup setup, FakeHttpMessageHandler nvidia) = NvidiaOnly(("AgentJoules:Paths:Tasks", configured));
            setup.WorkingDirectory = workspace.Root;

            CliRunResult result = await RunAsync("run --tier nvidia --dry-run", setup);

            Assert.Equal(2, result.ExitCode);
            string error = Assert.Single(result.ErrorLines);
            Assert.Contains("Paths:Tasks", error, StringComparison.Ordinal);
            if (configured.Length > 2)
            {
                Assert.DoesNotContain(configured, result.Stderr, StringComparison.Ordinal);
            }

            Assert.Equal(0, nvidia.RequestCount);
        }

        [Fact]
        public async Task Paths_Tasks_resolves_against_the_content_root()
        {
            using TempWorkspace workspace = TempWorkspace.Create();
            _ = RunCommandHarness.ProvisionTasks(workspace.Root, Path.Combine("custom", "set"));
            (RunCommandSetup setup, _) = NvidiaOnly(("AgentJoules:Paths:Tasks", "custom/set"));
            setup.WorkingDirectory = workspace.Root;

            CliRunResult result = await RunAsync("run --tier nvidia", setup);

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Equal(TaskNames, setup.Orchestrator.LastPlan!.Tasks.Select(static task => task.Name));
        }

        // ---- what the command hands to the orchestrator ----

        [Fact]
        public async Task Run_hands_the_defaults_and_every_task_to_the_orchestrator()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, _) = NvidiaOnly();
            setup.WorkingDirectory = workspace.Root;

            CliRunResult result = await RunAsync("run --tier nvidia", setup);

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            RunPlan plan = Assert.IsType<RunPlan>(setup.Orchestrator.LastPlan);
            Assert.Equal(1, setup.Orchestrator.Runs);
            Assert.Equal([Tier.Nvidia], plan.Tiers);
            Assert.Equal(TaskNames, plan.Tasks.Select(static task => task.Name));
            Assert.Equal(RunMode.Serialized, plan.Mode);
            Assert.Equal(3, plan.Repeat);
            Assert.Equal(30.0, plan.BaselineSeconds);
            Assert.Null(plan.Keys.MissingJudgeVariable);
            Assert.Equal(["nvidia"], plan.Models.Tiers.Select(static entry => entry.Tier));

            IRunCheckpointWriter writer = Assert.Single(plan.Writers.Values);
            Assert.Equal(Path.Combine(workspace.Root, "results"), Path.GetDirectoryName(writer.FilePath));
            Assert.True(RunFileName.IsValid(Path.GetFileName(writer.FilePath)), writer.FilePath);
            Assert.EndsWith("-nvidia.json", writer.FilePath, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Run_hands_the_parsed_options_and_the_selected_task_to_the_orchestrator()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, _) = NvidiaOnly();
            setup.WorkingDirectory = workspace.Root;

            CliRunResult result = await RunAsync("run --tier nvidia --task " + TaskNames[1] + " --mode concurrent --repeat 2 --baseline-seconds 5 --out custom/out", setup);

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            RunPlan plan = setup.Orchestrator.LastPlan!;
            Assert.Equal([TaskNames[1]], plan.Tasks.Select(static task => task.Name));
            Assert.Equal(RunMode.Concurrent, plan.Mode);
            Assert.Equal(2, plan.Repeat);
            Assert.Equal(5.0, plan.BaselineSeconds);
            Assert.Equal(Path.Combine(workspace.Root, "custom", "out"), Path.GetDirectoryName(plan.Writers[Tier.Nvidia].FilePath));
        }

        [Fact]
        public async Task An_absolute_out_is_kept()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            using TempWorkspace elsewhere = TempWorkspace.CreateEmpty();
            (RunCommandSetup setup, _) = NvidiaOnly();
            setup.WorkingDirectory = workspace.Root;

            CliRunResult result = await RunAsync($"run --tier nvidia --out \"{elsewhere.Root}\"", setup);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(Path.GetFullPath(elsewhere.Root), Path.GetDirectoryName(setup.Orchestrator.LastPlan!.Writers[Tier.Nvidia].FilePath));
        }

        [WindowsFact]
        public async Task A_drive_relative_out_is_joined_to_the_content_root()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, _) = NvidiaOnly();
            setup.WorkingDirectory = workspace.Root;
            string driveRelative = Path.GetPathRoot(workspace.Root)![..2] + "results";

            CliRunResult result = await RunAsync("run --tier nvidia --out " + driveRelative, setup);

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Equal(Path.Combine(workspace.Root, "results"), Path.GetDirectoryName(setup.Orchestrator.LastPlan!.Writers[Tier.Nvidia].FilePath));
        }

        // ---- the order of the host composition under the harness (security note on the hook) ----

        [Fact]
        public void A_command_hook_cannot_replace_the_isolation_defaults_of_the_harness_but_the_test_overrides_still_can()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            var keys = new Cli.Hosting.KeyRegistry(new Core.Security.SecretRedactor());
            ScriptedNvmlApi scripted = ScriptedGpu();
            void Hook(IServiceCollection services, IConfiguration configuration) =>
                services.AddSingleton<Core.Gpu.IGpuTelemetrySourceFactory>(new Nvml.NvmlGpuTelemetrySourceFactory(scripted));

            using (Microsoft.Extensions.Hosting.IHost isolated = Cli.Hosting.AgentJoulesHostFactory.Build(
                null, Hook, new Cli.CliHostOverrides(WorkingDirectory: workspace.Root), keys, TextWriter.Null, TextWriter.Null))
            {
                Core.Gpu.IGpuTelemetrySourceFactory factory = isolated.Services.GetRequiredService<Core.Gpu.IGpuTelemetrySourceFactory>();
                _ = Assert.IsType<Cli.Hosting.UnavailableGpuTelemetrySourceFactory>(factory);
                Assert.False(factory.Open(0).IsAvailable);
            }

            var replaced = new Cli.CliHostOverrides(
                Services: services => services.AddSingleton<Core.Gpu.IGpuTelemetrySourceFactory>(new Nvml.NvmlGpuTelemetrySourceFactory(scripted)),
                WorkingDirectory: workspace.Root);
            using Microsoft.Extensions.Hosting.IHost overridden = Cli.Hosting.AgentJoulesHostFactory.Build(null, Hook, replaced, keys, TextWriter.Null, TextWriter.Null);
            Assert.IsType<Nvml.NvmlGpuTelemetrySourceFactory>(overridden.Services.GetRequiredService<Core.Gpu.IGpuTelemetrySourceFactory>());
            Assert.Equal(0, scripted.InitCallCount);
        }

        // ---- the GPU reason survives in full (spec remark 4) ----

        [Theory]
        [Trait("Spec", "EDGE-001")]
        [InlineData("NVML library not found: libnvidia-ml.so.1 (EntryPointNotFoundException)")]
        [InlineData("NVML library not found: libnvidia-ml.so.1 (DllNotFoundException)")]
        [InlineData("NVML library not found: nvml.dll (BadImageFormatException)")]
        public void The_unavailable_reason_keeps_the_library_and_the_exception_type(string reason)
        {
            IConfiguration configuration = ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath(), ("NVIDIA_API_KEY", Key()));
            using PreflightHost host = PreflightFixtures.Build(configuration);

            MeasurementPreconditionException failure = Assert.Throws<MeasurementPreconditionException>(
                () => host.Preflight.CheckMeasuredTiers([Tier.Local], new UnavailableWith(reason)));

            Assert.EndsWith(reason, failure.Message, StringComparison.Ordinal);
        }

        private sealed class UnavailableWith(string reason) : Core.Gpu.IGpuTelemetrySourceFactory
        {
            public Core.Gpu.GpuOpenResult Open(int gpuIndex) => Core.Gpu.GpuOpenResult.Unavailable(reason);
        }

        // ---- the RunPlan invariants (code review M3 and N5) ----

        public enum PlanDefect
        {
            EmptyTiers,
            DuplicateTier,
            EmptyTasks,
            UndefinedMode,
            RepeatBelowOne,
            BaselineNotAboveZero,
            WritersDoNotMatchTiers,
            StartNotUtc,
            StartDefault,
            OutputDisplayBlank,
        }

        // The start of the command in the plans of these tests (AJ-056: one UTC instant per command).
        private static readonly DateTimeOffset PlanStart = RunCheckpointFixtures.RunStartUtc;

        [Theory]
        [InlineData(PlanDefect.EmptyTiers)]
        [InlineData(PlanDefect.DuplicateTier)]
        [InlineData(PlanDefect.EmptyTasks)]
        [InlineData(PlanDefect.UndefinedMode)]
        [InlineData(PlanDefect.RepeatBelowOne)]
        [InlineData(PlanDefect.BaselineNotAboveZero)]
        [InlineData(PlanDefect.WritersDoNotMatchTiers)]
        [InlineData(PlanDefect.StartNotUtc)]
        [InlineData(PlanDefect.StartDefault)]
        [InlineData(PlanDefect.OutputDisplayBlank)]
        public async Task A_plan_with_a_defect_is_refused(PlanDefect defect)
        {
            (KeyCheckResult keys, ModelCheckResult models) = await RealCheckResultsAsync();
            IReadOnlyList<Core.Tasks.TaskDefinition> tasks = TaskSetLoader.Load(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunCommandHarness.TasksFolder));
            var writers = new Dictionary<Tier, IRunCheckpointWriter> { [Tier.Nvidia] = new NullWriter() };

            Exception failure = Record.Exception(() => defect switch
            {
                PlanDefect.EmptyTiers => new RunPlan([], tasks, RunMode.Serialized, 1, 30, writers, keys, models, PlanStart, "results"),
                PlanDefect.DuplicateTier => new RunPlan([Tier.Nvidia, Tier.Nvidia], tasks, RunMode.Serialized, 1, 30, writers, keys, models, PlanStart, "results"),
                PlanDefect.EmptyTasks => new RunPlan([Tier.Nvidia], [], RunMode.Serialized, 1, 30, writers, keys, models, PlanStart, "results"),
                PlanDefect.UndefinedMode => new RunPlan([Tier.Nvidia], tasks, default, 1, 30, writers, keys, models, PlanStart, "results"),
                PlanDefect.RepeatBelowOne => new RunPlan([Tier.Nvidia], tasks, RunMode.Serialized, 0, 30, writers, keys, models, PlanStart, "results"),
                PlanDefect.BaselineNotAboveZero => new RunPlan([Tier.Nvidia], tasks, RunMode.Serialized, 1, 0, writers, keys, models, PlanStart, "results"),
                PlanDefect.StartNotUtc => new RunPlan([Tier.Nvidia], tasks, RunMode.Serialized, 1, 30, writers, keys, models, PlanStart.ToOffset(TimeSpan.FromHours(2)), "results"),
                PlanDefect.StartDefault => new RunPlan([Tier.Nvidia], tasks, RunMode.Serialized, 1, 30, writers, keys, models, default, "results"),
                PlanDefect.OutputDisplayBlank => new RunPlan([Tier.Nvidia], tasks, RunMode.Serialized, 1, 30, writers, keys, models, PlanStart, "  "),
                _ => new RunPlan([Tier.Nvidia], tasks, RunMode.Serialized, 1, 30, new Dictionary<Tier, IRunCheckpointWriter> { [Tier.Nebius] = new NullWriter() }, keys, models, PlanStart, "results"),
            });

            _ = Assert.IsType<ArgumentException>(failure, exactMatch: false);

            // The control: the same inputs without the defect make a plan.
            RunPlan plan = new([Tier.Nvidia], tasks, RunMode.Serialized, 1, 30, writers, keys, models, PlanStart, "results");
            Assert.Equal((PlanStart, "results"), (plan.StartUtc, plan.OutputDisplay));
        }

        [Theory]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public async Task A_baseline_that_is_negative_or_not_finite_is_refused(double baseline)
        {
            (KeyCheckResult keys, ModelCheckResult models) = await RealCheckResultsAsync();
            IReadOnlyList<Core.Tasks.TaskDefinition> tasks = TaskSetLoader.Load(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunCommandHarness.TasksFolder));
            var writers = new Dictionary<Tier, IRunCheckpointWriter> { [Tier.Nvidia] = new NullWriter() };

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RunPlan([Tier.Nvidia], tasks, RunMode.Serialized, 1, baseline, writers, keys, models, PlanStart, "results"));
        }

        [Fact]
        public async Task The_writers_of_a_plan_cannot_change_after_it_was_made()
        {
            (KeyCheckResult keys, ModelCheckResult models) = await RealCheckResultsAsync();
            IReadOnlyList<Core.Tasks.TaskDefinition> tasks = TaskSetLoader.Load(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunCommandHarness.TasksFolder));
            var first = new NullWriter();
            var writers = new Dictionary<Tier, IRunCheckpointWriter> { [Tier.Nvidia] = first };
            var plan = new RunPlan([Tier.Nvidia], tasks, RunMode.Serialized, 1, 30, writers, keys, models, PlanStart, "results");

            writers[Tier.Nvidia] = new NullWriter();
            writers[Tier.Nebius] = new NullWriter();

            Assert.Same(first, Assert.Single(plan.Writers).Value);
            Assert.IsNotType<Dictionary<Tier, IRunCheckpointWriter>>(plan.Writers);
        }

        // The real key check and model check results of the shipped configuration (nvidia with its key, a fake models route that lists the shipped IDs).
        private static async Task<(KeyCheckResult Keys, ModelCheckResult Models)> RealCheckResultsAsync()
        {
            IConfiguration configuration = ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath(), ("NVIDIA_API_KEY", Key()));
            using PreflightHost host = PreflightFixtures.Build(
                configuration,
                new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = Lists(ConfigurationFixtures.ShippedModelIds()) });
            KeyCheckResult keys = host.Preflight.CheckKeys([Tier.Nvidia], dryRun: false);
            ModelCheckResult models = await host.Preflight.CheckModelsAsync([Tier.Nvidia], keys, CancellationToken.None);
            return (keys, models);
        }

        private sealed class NullWriter : IRunCheckpointWriter
        {
            public string FilePath => "unused.json";

            public Task WriteAsync(Core.Results.RunDocument document, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        // ---- the output directory: probed before any request outside a dry run, ignored in a dry run ----

        [Fact]
        public async Task An_unwritable_out_exits_1_before_any_request_and_a_dry_run_ignores_it()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            string occupied = workspace.WriteFile("occupied", "this path is a file");
            (RunCommandSetup setup, FakeHttpMessageHandler nvidia) = NvidiaOnly();
            setup.WorkingDirectory = workspace.Root;

            CliRunResult measured = await RunAsync("run --tier nvidia --out occupied", setup);

            // The line echoes --out as given, never the absolute path it resolved to (security LOW-1).
            Assert.Equal(1, measured.ExitCode);
            string error = Assert.Single(measured.ErrorLines);
            Assert.Contains("The output directory \"occupied\" cannot be written", error, StringComparison.Ordinal);
            Assert.DoesNotContain(workspace.Root, measured.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, nvidia.RequestCount);
            Assert.Equal(0, setup.Orchestrator.Runs);

            CliRunResult dryRun = await RunAsync("run --tier nvidia --dry-run --out occupied", setup);

            Assert.Equal(0, dryRun.ExitCode);
            Assert.Equal("this path is a file", File.ReadAllText(occupied));
            Assert.Equal(1, nvidia.Count(FakeRoute.Models));
        }

        [Fact]
        public async Task A_dry_run_writes_no_file()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, _) = AllTiers();
            setup.WorkingDirectory = workspace.Root;
            string[] before = [.. Directory.EnumerateFileSystemEntries(workspace.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

            CliRunResult result = await RunAsync("run --tier all --dry-run", setup);

            Assert.Equal(0, result.ExitCode);
            Assert.False(Directory.Exists(Path.Combine(workspace.Root, "results")));
            Assert.Equal(before, Directory.EnumerateFileSystemEntries(workspace.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        }

        // ---- criterion 5 and EDGE-001: the GPU of a measured tier ----

        [Theory]
        [Trait("Spec", "EDGE-001")]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Run_tier_local_without_NVML_exits_6_naming_the_missing_library(bool dryRun)
        {
            ScriptedNvmlApi gpu = ScriptedGpu();
            gpu.MissingLibrary = true;
            (RunCommandSetup setup, FakeHttpMessageHandler local) = LocalTier(gpu);

            CliRunResult result = await RunAsync(dryRun ? "run --tier local --dry-run" : "run --tier local", setup);

            Assert.Equal(6, result.ExitCode);
            string error = Assert.Single(result.ErrorLines);
            Assert.Contains(LibraryName, error, StringComparison.Ordinal);
            Assert.EndsWith("(MeasurementPreconditionException)", error, StringComparison.Ordinal);
            Assert.Equal(0, local.Count(FakeRoute.ChatCompletions));
            Assert.Equal(1, local.Count(FakeRoute.Models));
            Assert.Equal(0, setup.Orchestrator.Runs);
            Assert.DoesNotContain(" OK", result.Stdout, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_local_dry_run_without_a_reachable_GPU_exits_6_with_the_reason_of_the_port()
        {
            (RunCommandSetup setup, _) = LocalTier(gpu: null);

            CliRunResult result = await RunAsync("run --tier local --dry-run", setup);

            Assert.Equal(6, result.ExitCode);
            Assert.Contains("no GPU is reachable under test overrides", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_forced_counter_on_a_GPU_without_one_exits_6_and_the_source_is_disposed()
        {
            ScriptedNvmlApi gpu = ScriptedGpu();
            gpu.CounterSupported = false;
            (RunCommandSetup setup, _) = LocalTier(gpu, ("AgentJoules:Meter:Method", "counter"));

            CliRunResult result = await RunAsync("run --tier local --dry-run", setup);

            Assert.Equal(6, result.ExitCode);
            Assert.Contains("Meter:Method=counter", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
            Assert.Equal(1, gpu.InitCallCount);
            Assert.False(gpu.IsInitialised);
        }

        [Fact]
        public async Task MIG_enabled_exits_6_for_cloudgpu_and_not_for_local()
        {
            ScriptedNvmlApi gpu = ScriptedGpu();
            gpu.MigModeCurrent = 1;
            RunCommandSetup cloud = Setup(
                Settings(("AgentJoules:Tiers:cloudgpu:Enabled", "true"), ("AgentJoules:CloudGpu:RequireSshVariables", "false"), ("NVIDIA_API_KEY", Key())),
                new Dictionary<string, HttpMessageHandler> { ["cloudgpu"] = Lists(ModelOf("cloudgpu")), ["nvidia"] = Lists(JudgeModel) },
                gpu);

            CliRunResult refused = await RunAsync("run --tier cloudgpu --dry-run", cloud);

            Assert.Equal(6, refused.ExitCode);
            Assert.Contains("MIG", Assert.Single(refused.ErrorLines), StringComparison.Ordinal);
            Assert.False(gpu.IsInitialised);

            (RunCommandSetup local, _) = LocalTier(gpu);
            CliRunResult passed = await RunAsync("run --tier local --dry-run", local);

            Assert.Equal(0, passed.ExitCode);
            Assert.False(gpu.IsInitialised);
        }

        [Fact]
        public async Task A_passing_GPU_check_opens_the_GPU_once_and_disposes_it()
        {
            ScriptedNvmlApi gpu = ScriptedGpu();
            (RunCommandSetup setup, _) = LocalTier(gpu);

            CliRunResult result = await RunAsync("run --tier local --dry-run", setup);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal([$"local {LocalBaseUrl} {ModelOf("local")} OK"], StdoutLines(result));
            Assert.Equal(1, gpu.InitCallCount);
            Assert.False(gpu.IsInitialised);
        }

        [Fact]
        public async Task A_hosted_tier_never_opens_the_GPU()
        {
            ScriptedNvmlApi gpu = ScriptedGpu();
            (RunCommandSetup setup, _) = NvidiaOnly();
            setup.Gpu = gpu;

            CliRunResult result = await RunAsync("run --tier nvidia --dry-run", setup);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(0, gpu.InitCallCount);
        }

        // ---- the order outside a dry run, and the orchestrator ----

        [Fact]
        public async Task Run_reaches_the_orchestrator_once_after_the_keys_the_writers_the_models_and_the_GPU()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            string results = Path.Combine(workspace.Root, "results");
            ScriptedNvmlApi gpu = ScriptedGpu();
            (RunCommandSetup setup, FakeHttpMessageHandler local) = LocalTier(gpu);
            setup.WorkingDirectory = workspace.Root;
            List<string> seen = [];
            local.CustomizeResponse = (route, _) =>
            {
                lock (seen)
                {
                    seen.Add($"{route} results={Directory.Exists(results)} gpuOpened={gpu.InitCallCount} gpuOpen={gpu.IsInitialised}");
                }
            };

            CliRunResult result = await RunAsync("run --tier local", setup);

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Equal(1, setup.Orchestrator.Runs);

            // The writer probe created the results folder before the NIM availability check (the shipped configuration selects the NIM runtime, AJ-105) and the models request; the GPU was opened and closed after them and before the completion request of the orchestrator.
            Assert.Equal(
                [
                    "NimVersion results=True gpuOpened=0 gpuOpen=False",
                    "Models results=True gpuOpened=0 gpuOpen=False",
                    "ChatCompletions results=True gpuOpened=1 gpuOpen=False",
                ],
                seen);
        }

        [Fact]
        public async Task The_production_orchestrator_runs_the_workflow_over_the_fake_endpoint_and_writes_the_file()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, FakeHttpMessageHandler nvidia) = NvidiaOnly();
            setup.UseProductionOrchestrator = true;
            setup.WorkingDirectory = workspace.Root;

            CliRunResult result = await RunAsync("run --tier nvidia --repeat 2", setup);

            // AJ-056 replaced the placeholder: one models request, then four completion requests per task and repeat.
            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Empty(result.ErrorLines);
            Assert.Equal(1, nvidia.Count(FakeRoute.Models));
            Assert.Equal(4 * TaskNames.Length * 2, nvidia.Count(FakeRoute.ChatCompletions));
            Assert.Equal(0, setup.Orchestrator.Runs);

            string file = Assert.Single(Directory.GetFiles(Path.Combine(workspace.Root, "results")));
            Assert.True(RunFileName.IsValid(Path.GetFileName(file)), file);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal("completed", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        }

        [Fact]
        public async Task A_missing_model_prints_the_error_line_before_the_list_of_available_ids()
        {
            FakeHttpMessageHandler nvidia = Lists("acme/listed-a", "acme/listed-b");
            RunCommandSetup setup = Setup(Settings(("NVIDIA_API_KEY", Key())), new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });

            foreach (string arguments in new[] { "run --tier nvidia --dry-run", "run --tier nvidia" })
            {
                CliRunResult result = await RunAsync(arguments, setup);

                Assert.Equal(4, result.ExitCode);
                int error = result.StderrLines.ToList().FindIndex(static line => line.StartsWith("error: ", StringComparison.Ordinal));
                int header = result.StderrLines.ToList().FindIndex(static line => line.StartsWith("available models of nvidia", StringComparison.Ordinal));
                Assert.True(error >= 0 && header > error, result.Stderr);
                Assert.Equal(["acme/listed-a", "acme/listed-b"], PrintedIds(result));
            }
        }

        // ---- the composition root ----

        [Fact]
        public async Task The_provider_clients_the_preflight_and_the_orchestrator_are_registered_once_for_every_command()
        {
            foreach (string arguments in new[] { "run --tier nvidia --dry-run", "probe" })
            {
                int factories = -1;
                int preflights = -1;
                int orchestrators = -1;
                bool secondCallThrew = false;
                (RunCommandSetup setup, _) = NvidiaOnly();
                setup.UseProductionOrchestrator = true;
                setup.Services = services =>
                {
                    factories = services.Count(static descriptor => descriptor.ServiceType == typeof(ProviderClientFactory));
                    preflights = services.Count(static descriptor => descriptor.ServiceType == typeof(PreflightService));
                    orchestrators = services.Count(static descriptor => descriptor.ServiceType == typeof(IRunOrchestrator));
                    try
                    {
                        _ = services.AddAgentJoulesProviderClients();
                    }
                    catch (InvalidOperationException)
                    {
                        secondCallThrew = true;
                    }
                };

                CliRunResult result = await RunAsync(arguments, setup);

                Assert.True(result.ExitCode == 0, $"{arguments}: exit code {result.ExitCode}; stderr: {result.Stderr}");
                Assert.Equal((1, 1, 1, true), (factories, preflights, orchestrators, secondCallThrew));
            }
        }

        // ---- criterion 7 (REQ-014): the console exporter of run, never of probe ----

        private static int MeterConfigurations(bool console) =>
            ServiceRegistrations.Count(TelemetrySetup.Add(new ServiceCollection(), new ConfigurationBuilder().Build(), console), ServiceRegistrations.MeterProviderConfigurations);

        private static async Task<(CliRunResult Result, int Meters, int Tracers)> CountTelemetryAsync(string arguments, params (string Key, string? Value)[] extra)
        {
            int meters = -1;
            int tracers = -1;
            (RunCommandSetup setup, _) = NvidiaOnly(extra);
            setup.Services = services =>
            {
                meters = ServiceRegistrations.Count(services, ServiceRegistrations.MeterProviderConfigurations);
                tracers = ServiceRegistrations.Count(services, ServiceRegistrations.TracerProviderConfigurations);
            };

            CliRunResult result = await RunAsync(arguments, setup);
            return (result, meters, tracers);
        }

        [Fact]
        [Trait("Spec", "REQ-014")]
        public async Task Run_registers_console_exporter_by_default()
        {
            (CliRunResult result, int meters, _) = await CountTelemetryAsync("run --tier nvidia");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(MeterConfigurations(console: false) + 1, MeterConfigurations(console: true));
            Assert.Equal(MeterConfigurations(console: true), meters);
        }

        [Fact]
        [Trait("Spec", "REQ-014")]
        public async Task No_console_telemetry_omits_console_exporter()
        {
            (CliRunResult result, int meters, _) = await CountTelemetryAsync("run --tier nvidia --no-console-telemetry");
            (CliRunResult probe, int probeMeters, int probeTracers) = await CountTelemetryAsync("probe");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(MeterConfigurations(console: false), meters);
            Assert.True(meters > 0, "The run registered no telemetry at all.");

            // probe never calls the telemetry setup: no metrics provider, no console exporter, no traces.
            Assert.Equal(0, probe.ExitCode);
            Assert.Equal((0, 0), (probeMeters, probeTracers));
        }

        [Fact]
        [Trait("Spec", "REQ-014")]
        public async Task A_dry_run_registers_the_telemetry_without_the_console_exporter_and_keeps_stdout_to_the_tier_lines()
        {
            // A dry run records no metric; the console exporter would print only the resource block of the SDK after the tier lines.
            (CliRunResult result, int meters, _) = await CountTelemetryAsync("run --tier nvidia --dry-run");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(MeterConfigurations(console: false), meters);
            Assert.Equal([$"nvidia {NvidiaBaseUrl} {ModelOf("nvidia")} OK"], StdoutLines(result));

            // The control: outside a dry run the console exporter prints its block on stdout when the host is disposed.
            (CliRunResult measured, _, _) = await CountTelemetryAsync("run --tier nvidia");
            Assert.Contains("Resource associated with Metrics", measured.Stdout, StringComparison.Ordinal);
        }

        [Fact]
        [Trait("Spec", "REQ-014")]
        public async Task Probe_json_stays_parseable_while_an_OTLP_endpoint_is_configured()
        {
            (CliRunResult result, int meters, int tracers) = await CountTelemetryAsync("probe --json", (TelemetrySetup.EndpointKey, "http://127.0.0.1:4317"));

            Assert.Equal(0, result.ExitCode);
            using JsonDocument parsed = JsonDocument.Parse(result.Stdout);
            Assert.False(parsed.RootElement.GetProperty("gpuPresent").GetBoolean());
            Assert.Equal((0, 0), (meters, tracers));
        }

        [Fact]
        [Trait("Spec", "REQ-014")]
        public async Task An_invalid_OTLP_endpoint_fails_run_with_2_naming_the_variable_and_leaves_probe_alone()
        {
            const string invalid = "not-an-endpoint-7f3a";
            (CliRunResult run, _, _) = await CountTelemetryAsync("run --tier nvidia --dry-run", (TelemetrySetup.EndpointKey, invalid));
            (CliRunResult probe, _, _) = await CountTelemetryAsync("probe", (TelemetrySetup.EndpointKey, invalid));

            Assert.Equal(2, run.ExitCode);
            string error = Assert.Single(run.ErrorLines);
            Assert.Contains(TelemetrySetup.EndpointKey, error, StringComparison.Ordinal);
            Assert.DoesNotContain(invalid, run.Stderr, StringComparison.Ordinal);
            Assert.Equal(0, probe.ExitCode);
        }
    }
}