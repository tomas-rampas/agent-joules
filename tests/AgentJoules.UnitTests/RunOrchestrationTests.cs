using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Providers;
using AgentJoules.Workflow.Tasks;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-056 (REQ-017, REQ-013, EDGE-005): the production orchestrator of <c>AJ run</c>. The command line tests run the real <c>run</c> command through <see cref="RunCommandHarness"/> with the
/// production orchestrator and a recording fake endpoint per tier: the real preflight, the real agent pipelines and workflow, the real meter (a scripted GPU behind the real NVML binding on a fake
/// clock for the measured tier), the real results writer and the redacting writers. Keys are runtime-built fakes in the in-memory configuration; no request leaves the process and no process variable
/// is read or set. The nested classes hold the measured tier and its baseline (<c>Spec=EDGE-005</c>), the mode wiring (<c>Spec=REQ-013</c>) and the units without a host (the assembler, the
/// console lines and the no-op seams).
/// </summary>
[Trait("Spec", "REQ-017")]
public sealed partial class RunOrchestrationTests
{
    // A command that hangs must fail the test, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    // The termination timeout of the CLI (InvocationConfiguration.ProcessTerminationTimeout).
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(15);

    private static readonly string[] Agents = [.. AgentCatalog.Names];

    private static readonly int[] TwoRepeats = [1, 2];

    private static readonly string[] Branches = [AgentCatalog.Researcher, AgentCatalog.Writer];

    private static string[] TaskNames => [.. TaskSetLoader.Load(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunCommandHarness.TasksFolder)).Select(static task => task.Name)];

    private static Task<CliRunResult> RunAsync(string arguments, RunCommandSetup setup) => RunCommandHarness.RunAsync(arguments, setup).WaitAsync(HangGuard);

    private static void AssertExit(int expected, CliRunResult result) =>
        Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stderr: {result.Stderr}");

    private static RunCommandSetup NvidiaRun(TempWorkspace workspace, HttpMessageHandler nvidia, params (string Key, string? Value)[] extra) =>
        RunOrchestrationFixtures.Production(workspace.Root, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia }, extra);

    private static JsonElement RunOf(JsonDocument document) => document.RootElement.GetProperty("run");

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private static string KeyOf(JsonElement step) => $"{Text(step, "task")}/{step.GetProperty("repeat").GetInt32()}/{Text(step, "agent")}";

    private static string[] StepLines(CliRunResult result) =>
        [.. RunOrchestrationFixtures.StdoutLines(result).Where(static line => TaskNames.Any(task => line.StartsWith(task + " r", StringComparison.Ordinal)))];

    // ---- criterion 1: tasks x repeats x agents ----

    [Fact]
    public async Task Run_produces_tasks_x_repeats_x_agents()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint();

        CliRunResult result = await RunAsync("run --tier nvidia --repeat 2", NvidiaRun(workspace, nvidia));

        AssertExit(0, result);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
        Assert.Equal(24, steps.Length);
        Assert.Equal(TaskNames.Length * 2 * Agents.Length, steps.Length);
        Assert.Equal(24, nvidia.Count(FakeRoute.ChatCompletions));
        Assert.Equal(1, nvidia.Count(FakeRoute.Models));

        // Every (task, repeat, agent) once; the (task, repeat) groups in run order (tasks in file order, repeats ascending); the Planner first and the Reviewer last of each group.
        string[] expected = [.. from task in TaskNames from repeat in TwoRepeats from agent in Agents select $"{task}/{repeat}/{agent}"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), steps.Select(KeyOf).Order(StringComparer.Ordinal));
        string[][] groups = [.. steps.Chunk(4).Select(static chunk => chunk.Select(KeyOf).ToArray())];
        Assert.Equal([.. from task in TaskNames from repeat in TwoRepeats select $"{task}/{repeat}"], groups.Select(static group => group[0][..group[0].LastIndexOf('/')]));
        Assert.All(groups, static group =>
        {
            Assert.EndsWith("/Planner", group[0], StringComparison.Ordinal);
            Assert.EndsWith("/Reviewer", group[3], StringComparison.Ordinal);
        });

        JsonElement run = RunOf(document);
        Assert.Equal("completed", Text(run, "status"));
        Assert.Equal("nvidia", Text(run, "tier"));
        Assert.Equal(2, run.GetProperty("repeat").GetInt32());
        Assert.Equal(TaskNames, run.GetProperty("tasks").EnumerateArray().Select(static task => task.GetString()));
        Assert.All(steps, static step => Assert.Equal("ok", Text(step, "outcome")));
    }

    // ---- criterion 2: fresh clients and a fresh workflow per (task, repeat), one checkpoint each ----

    [Fact]
    public async Task Fresh_clients_per_task_repeat()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        RunCommandSetup setup = NvidiaRun(workspace, RunOrchestrationFixtures.Endpoint());
        List<IChatClient> clients = [];
        var orchestrator = new RecordingOrchestrator();
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<IProviderClientFactory>();
            _ = services.AddSingleton<IProviderClientFactory>(provider => new CountingProviderClients(provider.GetRequiredService<ProviderClientFactory>(), clients));
            orchestrator.Register(services);
        });

        CliRunResult result = await RunAsync("run --tier nvidia --repeat 2", setup);

        AssertExit(0, result);
        int taskRepeats = TaskNames.Length * 2;

        // One provider client per agent and (task, repeat), each a new instance: tasks x repeats client sets of four.
        Assert.Equal(taskRepeats * Agents.Length, clients.Count);
        Assert.Equal(clients.Count, clients.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(taskRepeats, clients.Count / Agents.Length);

        // One running checkpoint per (task, repeat), each holding the steps so far, then the one completed write.
        (Tier Tier, RunStatus Status, int Steps)[] writes = [.. orchestrator.Writes];
        Assert.Equal(1, orchestrator.Calls);
        Assert.Equal(taskRepeats, writes.Count(static write => write.Status == RunStatus.Running));
        Assert.Equal([.. Enumerable.Range(1, taskRepeats).Select(static n => n * 4)], writes.Where(static write => write.Status == RunStatus.Running).Select(static write => write.Steps));
        Assert.Equal((Tier.Nvidia, RunStatus.Completed, taskRepeats * 4), writes[^1]);
        Assert.Equal(taskRepeats + 1, writes.Length);
    }

    // ---- criterion 3: --tier all, one file per enabled tier in the fixed order ----

    [Fact]
    public async Task All_writes_one_file_per_enabled_tier()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        var gpu = new ScriptedNvmlApi(time) { ConstantPowerWatts = 30.0 };
        var endpoints = new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
        {
            ["local"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)),
            ["nvidia"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)),
            ["nebius"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)),
        };
        RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, endpoints);
        setup.Gpu = gpu;
        RunOrchestrationFixtures.UseClock(setup, time);

        CliRunResult result = await RunOrchestrationFixtures.RunDrivenAsync("run --tier all --repeat 1", setup, time).WaitAsync(HangGuard);

        AssertExit(0, result);

        // The committed configuration disables cloudgpu: three files, one command start in every name.
        string[] names = [.. RunOrchestrationFixtures.RunFilesIn(workspace.Root).Select(static path => Path.GetFileName(path))];
        Assert.Equal(["20261021T100000Z-local.json", "20261021T100000Z-nebius.json", "20261021T100000Z-nvidia.json"], names);
        Assert.Equal(names, RunOrchestrationFixtures.EntriesIn(workspace.Root));
        foreach (string tierName in new[] { "local", "nvidia", "nebius" })
        {
            using JsonDocument document = RunOrchestrationFixtures.Read(Path.Combine(workspace.Root, "results", $"20261021T100000Z-{tierName}.json"));
            Assert.Equal(tierName, Text(RunOf(document), "tier"));
            Assert.Equal("completed", Text(RunOf(document), "status"));
            Assert.Equal(TaskNames.Length * Agents.Length, RunOrchestrationFixtures.Steps(document).Length);
        }

        // The tiers ran in the fixed order local, nvidia, nebius: their closing lines appear in that order.
        Assert.Equal(
            ["results: results/20261021T100000Z-local.json", "results: results/20261021T100000Z-nvidia.json", "results: results/20261021T100000Z-nebius.json"],
            RunOrchestrationFixtures.StdoutLines(result).Where(static line => line.StartsWith("results: ", StringComparison.Ordinal)));
        Assert.False(gpu.IsInitialised);
    }

    // ---- criterion 4: the keys of every selected tier before the first call ----

    [Fact]
    public async Task All_tier_keys_checked_first()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        var endpoints = new Dictionary<string, FakeHttpMessageHandler>(StringComparer.Ordinal)
        {
            ["local"] = RunOrchestrationFixtures.Endpoint(),
            ["nvidia"] = RunOrchestrationFixtures.Endpoint(),
            ["nebius"] = RunOrchestrationFixtures.Endpoint(),
        };
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            workspace.Root,
            endpoints.ToDictionary(static pair => pair.Key, static pair => (HttpMessageHandler)pair.Value, StringComparer.Ordinal),
            (ProviderClientFixtures.NebiusKeyVariable, null));
        var orchestrator = new RecordingOrchestrator();
        RunOrchestrationFixtures.AddServices(setup, orchestrator.Register);

        CliRunResult result = await RunAsync("run --tier all", setup);

        AssertExit(3, result);
        Assert.EndsWith("(MissingKeyException)", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        Assert.Contains(ProviderClientFixtures.NebiusKeyVariable, result.Stderr, StringComparison.Ordinal);
        Assert.Empty(RunOrchestrationFixtures.EntriesIn(workspace.Root));
        Assert.All(endpoints.Values, static endpoint => Assert.Equal(0, endpoint.RequestCount));
        Assert.Empty(result.DeniedRequests);
        Assert.Equal(0, orchestrator.Calls);
    }

    // ---- criterion 7: completed, exit 0, and --task bogus ----

    [Fact]
    public async Task Completed_run_has_no_incomplete()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();

        CliRunResult result = await RunAsync("run --tier nvidia --repeat 1", NvidiaRun(workspace, RunOrchestrationFixtures.Endpoint()));

        AssertExit(0, result);
        Assert.Empty(result.ErrorLines);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        Assert.Equal("completed", Text(RunOf(document), "status"));
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
        Assert.Equal(TaskNames.Length * Agents.Length, steps.Length);
        Assert.All(steps, static step => Assert.Equal(StepOutcomes.Ok, Text(step, "outcome")));
        Assert.DoesNotContain(StepOutcomes.Incomplete, File.ReadAllText(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root))), StringComparison.Ordinal);
        Assert.DoesNotContain(" incomplete ", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_task_exits_2_before_any_call_and_any_file()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint();

        CliRunResult result = await RunAsync("run --tier nvidia --task bogus", NvidiaRun(workspace, nvidia));

        AssertExit(2, result);
        Assert.Contains("unknown task bogus", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        Assert.Equal(0, nvidia.RequestCount);
        Assert.Empty(RunOrchestrationFixtures.EntriesIn(workspace.Root));
    }

    // ---- criterion 8: shutdown marks the in-flight step incomplete, writes the file and exits 5 ----

    [Fact]
    public async Task Shutdown_marks_in_flight_steps_incomplete()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint();
        var held = new HoldingHttpMessageHandler(nvidia, FakeRoute.ChatCompletions, number: 2);
        RunCommandSetup setup = NvidiaRun(workspace, held);
        string task = TaskNames[0];
        using var cancellation = new CancellationTokenSource();

        CliHarnessRun run = CliHarness.Start($"run --tier nvidia --task {task} --repeat 1", setup.ToOverrides(workspace.Root), cancellationToken: cancellation.Token);
        try
        {
            await held.Reached.WaitAsync(HangGuard);
            var stopwatch = Stopwatch.StartNew();
            await run.RaiseShutdownAsync(CancellationToken.None).WaitAsync(HangGuard);
            CliRunResult result = await run.Result.WaitAsync(HangGuard);
            stopwatch.Stop();

            AssertIncomplete(result, workspace, task, stopwatch.Elapsed);
        }
        finally
        {
            await EndAsync(run, cancellation);
        }
    }

    // Ends a started run whatever the test found, so a failed shutdown test never keeps the harness gate (CliApplication's RunGate) for the rest of the assembly.
    private static async Task EndAsync(CliHarnessRun run, CancellationTokenSource cancellation)
    {
        await cancellation.CancelAsync();
        try
        {
            _ = await run.Result.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            // The assertion that failed first is the one the test reports.
        }
    }

    [Fact]
    public async Task A_cancelled_command_token_writes_the_incomplete_file_and_exits_5()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint();
        var held = new HoldingHttpMessageHandler(nvidia, FakeRoute.ChatCompletions, number: 2);
        RunCommandSetup setup = NvidiaRun(workspace, held);
        string task = TaskNames[0];
        using var cancellation = new CancellationTokenSource();

        CliHarnessRun run = CliHarness.Start($"run --tier nvidia --task {task} --repeat 1", setup.ToOverrides(workspace.Root), cancellationToken: cancellation.Token);
        try
        {
            await held.Reached.WaitAsync(HangGuard);
            var stopwatch = Stopwatch.StartNew();
            await cancellation.CancelAsync();
            CliRunResult result = await run.Result.WaitAsync(HangGuard);
            stopwatch.Stop();

            AssertIncomplete(result, workspace, task, stopwatch.Elapsed);
        }
        finally
        {
            await EndAsync(run, cancellation);
        }
    }

    // The file of an interrupted nvidia run of one task: the Planner finished, the branch whose request was held is incomplete, nothing else ran.
    private static void AssertIncomplete(CliRunResult result, TempWorkspace workspace, string task, TimeSpan shutdown)
    {
        AssertExit(5, result);
        Assert.Equal("error: interrupted by shutdown (OperationCanceledException)", Assert.Single(result.ErrorLines));

        // The command ended, and so the checkpoint was on disk, well inside the termination timeout; the move left no partial file.
        Assert.True(shutdown < TerminationTimeout / 3, $"The shutdown took {shutdown}.");
        string file = Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root));
        Assert.Equal([Path.GetFileName(file)], RunOrchestrationFixtures.EntriesIn(workspace.Root));

        using JsonDocument document = RunOrchestrationFixtures.Read(file);
        Assert.Equal("incomplete", Text(RunOf(document), "status"));
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
        Assert.Equal(2, steps.Length);
        Assert.Equal(($"{task}/1/Planner", StepOutcomes.Ok), (KeyOf(steps[0]), Text(steps[0], "outcome")));
        Assert.Contains(Text(steps[1], "agent"), Branches);
        Assert.Equal(StepOutcomes.Incomplete, Text(steps[1], "outcome"));
        Assert.DoesNotContain(StepOutcomes.Cancelled, File.ReadAllText(file), StringComparison.Ordinal);

        // The console shows the same outcomes as the file.
        Assert.Equal(
            [$"{task} r1 Planner ok", $"{task} r1 {Text(steps[1], "agent")} incomplete"],
            StepLines(result).Select(static line => string.Join(' ', line.Split(' ').Take(4))));
    }

    // ---- criterion 6: one line per finished step, the results path and the totals, labels instead of 0 ----

    [Fact]
    public async Task Each_finished_step_prints_one_line_with_its_values()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        RunCommandSetup setup = NvidiaRun(workspace, RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(2)));
        RunOrchestrationFixtures.UseClock(setup, time);
        string task = TaskNames[0];

        CliRunResult result = await RunAsync($"run --tier nvidia --task {task} --repeat 1", setup);

        AssertExit(0, result);

        // A hosted step: its energy is N/A, the fake reports 7 and 3 tokens, and every call takes 2 s on the fake clock.
        string[] lines = StepLines(result);
        Assert.Equal(4, lines.Length);
        Assert.Equal(
            Agents.Select(agent => $"{task} r1 {agent} ok netJ=N/A avgW=N/A in=7 out=3 latency=2000").Order(StringComparer.Ordinal),
            lines.Order(StringComparer.Ordinal));
        Assert.StartsWith($"{task} r1 Planner ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith($"{task} r1 Reviewer ", lines[3], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, static line => line.Contains("=0 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_without_usage_prints_labels_and_never_0()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        var nvidia = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), PromptTokens = null, CompletionTokens = null };
        string task = TaskNames[0];

        CliRunResult result = await RunAsync($"run --tier nvidia --task {task} --repeat 1", NvidiaRun(workspace, nvidia));

        AssertExit(0, result);
        string[] lines = StepLines(result);
        Assert.Equal(4, lines.Length);
        Assert.All(lines, static line =>
        {
            Assert.Contains(" netJ=N/A avgW=N/A in=not-reported out=not-reported latency=", line, StringComparison.Ordinal);
            Assert.DoesNotContain("=0 ", line, StringComparison.Ordinal);
        });

        // The totals take the label and the reason of the steps, never 0.
        string totals = Assert.Single(RunOrchestrationFixtures.StdoutLines(result), static line => line.StartsWith("totals: ", StringComparison.Ordinal));
        Assert.StartsWith("totals: grossJ=N/A netJ=N/A in=not-reported out=not-reported cost=N/A wallClockMs=", totals, StringComparison.Ordinal);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        JsonElement inputTokens = document.RootElement.GetProperty("totals").GetProperty("inputTokens");
        Assert.Equal("not-reported", Text(inputTokens, "label"));
        Assert.Equal(JsonValueKind.Null, inputTokens.GetProperty("value").ValueKind);
    }

    [Theory]
    [InlineData("", "results")]
    [InlineData(" --out custom/out/", "custom/out")]
    [InlineData(" --out custom\\out", "custom/out")]
    public async Task The_closing_lines_name_the_file_as_given_and_the_totals(string outOption, string shown)
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        RunCommandSetup setup = NvidiaRun(workspace, RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(2)));
        RunOrchestrationFixtures.UseClock(setup, time);
        string task = TaskNames[0];

        CliRunResult result = await RunAsync($"run --tier nvidia --task {task} --repeat 1{outOption}", setup);

        AssertExit(0, result);
        string[] lines = RunOrchestrationFixtures.StdoutLines(result);
        int results = Array.FindIndex(lines, static line => line.StartsWith("results: ", StringComparison.Ordinal));
        Assert.Equal($"results: {shown}/20261021T100000Z-nvidia.json", lines[results]);

        // Four serialized calls of 2 s: 8 s of workflow wall time; 4 x 7 input and 4 x 3 output tokens; the hosted energy is N/A and the cost is not configured.
        Assert.Equal("totals: grossJ=N/A netJ=N/A in=28 out=12 cost=N/A wallClockMs=8000", lines[results + 1]);

        // The closing lines follow the four step lines, and the absolute path of the workspace is never printed.
        int[] stepIndexes = [.. lines.Select(static (line, index) => (line, index)).Where(pair => pair.line.StartsWith(task + " r1 ", StringComparison.Ordinal)).Select(static pair => pair.index)];
        Assert.Equal(4, stepIndexes.Length);
        Assert.True(stepIndexes.Max() < results, result.Stdout);
        Assert.DoesNotContain(workspace.Root, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    // ---- RunPlan.StartUtc: one command start for every file name and every run.startUtc ----

    [Fact]
    public async Task The_file_name_and_run_startUtc_come_from_one_command_start()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        RunCommandSetup setup = NvidiaRun(workspace, RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)));
        RunOrchestrationFixtures.UseClock(setup, time);

        CliRunResult result = await RunAsync($"run --tier nvidia --task {TaskNames[0]} --repeat 1", setup);

        AssertExit(0, result);
        string file = Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root));
        Assert.Equal("20261021T100000Z-nvidia.json", Path.GetFileName(file));
        using JsonDocument document = RunOrchestrationFixtures.Read(file);
        JsonElement run = RunOf(document);
        Assert.Equal("20261021T100000Z-nvidia", Text(run, "id"));
        DateTimeOffset start = DateTimeOffset.Parse(Text(run, "startUtc"), CultureInfo.InvariantCulture);
        Assert.Equal(RunOrchestrationFixtures.CommandStartUtc, start);
        Assert.Equal(TimeSpan.Zero, start.Offset);

        // The models request moved the clock before the first step: every step starts after the command did.
        Assert.All(RunOrchestrationFixtures.Steps(document), step => Assert.True(DateTimeOffset.Parse(Text(step, "startUtc"), CultureInfo.InvariantCulture) > start));
    }

    // ---- the run and environment fields of a hosted tier (T4) ----

    [Fact]
    public async Task A_hosted_run_records_the_run_and_environment_fields_and_no_measurement()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        ScriptedNvmlApi gpu = new(RunOrchestrationFixtures.Clock());
        RunCommandSetup setup = NvidiaRun(workspace, RunOrchestrationFixtures.Endpoint());
        setup.Gpu = gpu;

        CliRunResult result = await RunAsync($"run --tier nvidia --task {TaskNames[0]} --repeat 1", setup);

        AssertExit(0, result);
        Assert.Equal(0, gpu.InitCallCount);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        JsonElement root = document.RootElement;
        Assert.Equal("1", Text(root, "schemaVersion"));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("probe").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("baseline").ValueKind);
        Assert.Empty(root.GetProperty("judge").EnumerateArray());

        JsonElement run = root.GetProperty("run");
        Assert.Equal("serialized", Text(run, "mode"));
        Assert.Empty(run.GetProperty("flags").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(Text(run, "toolVersion")));
        Assert.DoesNotContain('+', Text(run, "toolVersion"));

        JsonElement environment = root.GetProperty("environment");
        Assert.Equal("build.nvidia.com", Text(environment, "provider"));
        Assert.Equal("off", Text(environment, "reasoningMode"));
        Assert.Equal(0.0, environment.GetProperty("temperature").GetDouble());
        Assert.Equal(EnvironmentInfo.GpuBoardOnly, Text(environment, "energyScope"));
        Assert.False(string.IsNullOrWhiteSpace(Text(environment, "os")));
        Assert.Equal(Environment.Version.ToString(), Text(environment, "dotnetVersion"));
        foreach (string absent in new[] { "gpuName", "driverVersion", "gpuUuid", "localRuntime", "runtimeVersion", "gpuOffloadPercent", "modelLoadSeconds", "powerSource", "precision", "instancePreset", "region", "containerImage", "containerDigest" })
        {
            Assert.True(environment.GetProperty(absent).ValueKind == JsonValueKind.Null, absent);
        }

        Assert.Equal(("N/A", "not-cloudgpu"), (Text(environment.GetProperty("cloudCostUsd"), "label"), Text(environment.GetProperty("cloudCostUsd"), "reason")));
        Assert.Equal(("not-reported", "not-configured"), (Text(environment.GetProperty("assumptions").GetProperty("electricityPrice"), "label"), Text(environment.GetProperty("assumptions").GetProperty("electricityPrice"), "reason")));
        Assert.Equal(
            [.. Agents.Select(static agent => $"{agent} {ConfigurationFixtures.ShippedOptions().Tiers["nvidia"].ModelId} False")],
            environment.GetProperty("models").EnumerateArray().Select(static model => $"{Text(model, "agent")} {Text(model, "modelId")} {model.GetProperty("baseline").GetBoolean()}"));

        JsonElement totals = root.GetProperty("totals");
        Assert.Equal(("N/A", "hosted-tier"), (Text(totals.GetProperty("grossJoules"), "label"), Text(totals.GetProperty("grossJoules"), "reason")));
        Assert.Equal(("N/A", "not-scored"), (Text(totals.GetProperty("judge").GetProperty("inputTokens"), "label"), Text(totals.GetProperty("judge").GetProperty("inputTokens"), "reason")));
        Assert.Equal(("N/A", "not-configured"), (Text(totals.GetProperty("gco2e"), "label"), Text(totals.GetProperty("gco2e"), "reason")));
        Assert.Empty(totals.GetProperty("costByCurrency").EnumerateObject());
        Assert.Equal("measured", Text(totals.GetProperty("wallClockMs"), "label"));
    }

    // ---- the doubles of this class ----

    // The real provider client factory, recording every client it makes.
    private sealed class CountingProviderClients(ProviderClientFactory inner, List<IChatClient> clients) : IProviderClientFactory
    {
        public IChatClient Create(Tier tier, string modelId)
        {
            IChatClient client = inner.Create(tier, modelId);
            lock (clients)
            {
                clients.Add(client);
            }

            return client;
        }
    }
}