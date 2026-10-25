using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using AgentJoules.Cli.Hosting;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Metering;
using AgentJoules.Nvml.Interop;
using AgentJoules.Reporting.Json;
using AgentJoules.Reporting.Results;
using AgentJoules.Reporting.Schema;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Local;
using AgentJoules.Workflow.Runs;
using AgentJoules.Workflow.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-109 (REQ-021): the counter versus sampling calibration. The arithmetic and the model are tested on their own; the command is tested through the real command line harness against the scripted GPU behind the real
/// NVML binding (<see cref="ScriptedNvmlApi"/>, whose <see cref="ScriptedNvmlApi.CounterScale"/> makes the counter read 100 J where the samples integrate to 98 J), a loopback fake that stands where the NIM container or Ollama
/// stands, and a fake clock on which the fake workflow takes 4 s per repeat. No live endpoint is called, no real GPU is opened, no key but the fake one exists.
/// </summary>
[Trait("Spec", "REQ-021")]
public sealed partial class CalibrationTests
{
    private const string TaskName = "incident-brief";

    // The story's literal values: the scripted GPU draws 1.634 W on every third tenth of a second and 1.633 W otherwise, which the samples integrate to 98 J over the 60 s window, and the counter runs 100 / 98 as fast, so it counts
    // 100 J over the same window and the relative difference is 0.02, each within the tolerance below.
    private const double StoryTolerance = 1e-9;
    private const double StoryCounterScale = 100.0 / 98.0;
    private const string UnsupportedCounterReason = "counter-not-supported: NVML_ERROR_NOT_SUPPORTED";
    private static readonly Func<double, double> StoryWattsAt = static t => ((long)Math.Round(t * 10, MidpointRounding.AwayFromZero)) % 3 == 0 ? 1.634 : 1.633;
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(90);
    private static readonly Regex ResultLine = MyRegex();

    // ---- helpers ----

    private static ProbeResult Probe(bool counter = true) =>
        ProbeResult.Create(
            new GpuIdentity("Scripted GPU 3080", "GPU-00000000-0000-0000-0000-000000000000", "616.92", "94.02.5C.00.01", "8.6", 85.059, "disabled"),
            counter,
            counter ? new MeterCapabilities(EnergyMethod.Counter, true, null, 10.0, false) : new MeterCapabilities(EnergyMethod.Sampling, false, "NOT_SUPPORTED", 10.0, true),
            10.0);

    private static CalibrationDocument Document(Metric? counter = null, Metric? sampling = null, Metric? difference = null, string task = TaskName, int seconds = 60, double window = 60.0, int repeats = 15) =>
        new(
            task,
            seconds,
            window,
            sampleCount: 601,
            sampleRateHz: 10.0,
            repeats,
            new DateTimeOffset(2026, 10, 21, 10, 0, 5, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 21, 10, 1, 5, TimeSpan.Zero),
            "0.1.0",
            "abc1234",
            counter ?? Metric.Measured(100.0, "J"),
            sampling ?? Metric.Measured(98.0, "J"),
            difference ?? Metric.Measured(0.02, "ratio"),
            Probe(),
            new CalibrationEnvironment("nim", "1.0", 100.0, "BF16", 12.5, "ac"));

    private static void Script(LocalRunRig rig, Func<double, double>? wattsAt = null, double? counterScale = null, bool counterSupported = true)
    {
        ScriptedNvmlApi gpu = rig.Setup.Gpu ?? throw new InvalidOperationException("The rig has no scripted GPU.");
        if (wattsAt is not null)
        {
            gpu.WattsAt = wattsAt;
        }

        if (counterScale is { } scale)
        {
            gpu.CounterScale = scale;
        }

        gpu.CounterSupported = counterSupported;
    }

    private static string[] CalibrationFiles(LocalRunRig rig, string folder = RunOrchestrationFixtures.ResultsFolder)
    {
        string directory = Path.Combine(rig.Root, folder);
        return Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory, "calibration-*").Order(StringComparer.Ordinal)] : [];
    }

    private static JsonDocument ReadOnlyFile(LocalRunRig rig, string folder = RunOrchestrationFixtures.ResultsFolder) => JsonDocument.Parse(File.ReadAllText(Assert.Single(CalibrationFiles(rig, folder))));

    private static async Task<CliRunResult> RunAsync(LocalRunRig rig, string arguments)
    {
        CliRunResult result = await rig.RunAsync(arguments).WaitAsync(HangGuard);
        return result;
    }

    private static string Fail(CliRunResult result) => $"exit code {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}";

    // ---- the arithmetic (criterion 2, 3) ----

    [Fact]
    public void The_difference_is_counter_minus_sampling_over_counter()
    {
        Metric difference = CalibrationDocument.DifferenceOf(Metric.Measured(100.0, "J"), Metric.Measured(98.0, "J"));

        Assert.Equal(MetricLabel.Measured, difference.Label);
        Assert.Equal("ratio", difference.Unit);
        Assert.Equal(0.02, difference.Value);
    }

    [Fact]
    public void A_sampling_total_above_the_counter_gives_a_negative_difference_that_is_not_clamped()
    {
        Metric difference = CalibrationDocument.DifferenceOf(Metric.Measured(100.0, "J"), Metric.Measured(103.0, "J"));

        Assert.Equal(MetricLabel.Measured, difference.Label);
        Assert.Equal(-0.03, difference.Value!.Value, 12);
    }

    [Fact]
    public void An_unsupported_counter_gives_an_N_A_difference_with_the_reason_up_to_the_colon()
    {
        Metric difference = CalibrationDocument.DifferenceOf(Metric.NotApplicable("counter-not-supported: NOT_SUPPORTED"), Metric.Measured(98.0, "J"));

        Assert.Equal(MetricLabel.NotApplicable, difference.Label);
        Assert.Null(difference.Value);
        Assert.Equal("counter-not-supported", difference.Reason);
    }

    [Fact]
    public void A_counter_of_zero_gives_an_N_A_difference_with_the_reason_counter_zero()
    {
        Metric difference = CalibrationDocument.DifferenceOf(Metric.Measured(0.0, "J"), Metric.Measured(98.0, "J"));

        Assert.Equal(MetricLabel.NotApplicable, difference.Label);
        Assert.Equal("counter-zero", difference.Reason);
    }

    [Fact]
    public void A_window_without_samples_gives_an_N_A_difference_with_the_reason_no_samples()
    {
        Metric difference = CalibrationDocument.DifferenceOf(Metric.Measured(100.0, "J"), Metric.NotApplicable("no-samples"));

        Assert.Equal(MetricLabel.NotApplicable, difference.Label);
        Assert.Equal("no-samples", difference.Reason);
    }

    [Fact]
    public void The_counter_is_looked_at_before_the_samples()
    {
        Assert.Equal("counter-not-supported", CalibrationDocument.DifferenceOf(Metric.NotApplicable("counter-not-supported: x"), Metric.NotApplicable("no-samples")).Reason);
        Assert.Equal("counter-zero", CalibrationDocument.DifferenceOf(Metric.Measured(0.0, "J"), Metric.NotApplicable("no-samples")).Reason);
    }

    // ---- the model ----

    [Fact]
    public void A_document_keeps_what_it_was_given()
    {
        CalibrationDocument document = Document();

        Assert.Equal((TaskName, 60, 60.0, 601, 10.0, 15), (document.Task, document.Seconds, document.WindowSeconds, document.SampleCount, document.SampleRateHz, document.Repeats));
        Assert.Equal("nim", document.Environment.LocalRuntime);
        Assert.True(document.Probe.GpuPresent);
    }

    [Fact]
    public void A_document_refuses_a_total_that_is_estimated_or_has_another_unit_or_is_not_reported()
    {
        _ = Assert.Throws<ArgumentException>(() => Document(counter: Metric.EstimatedQuantity(100.0, "J")));
        _ = Assert.Throws<ArgumentException>(() => Document(sampling: Metric.Measured(98.0, "W")));
        _ = Assert.Throws<ArgumentException>(() => Document(difference: Metric.Measured(0.02, "J")));
        _ = Assert.Throws<ArgumentException>(() => Document(counter: Metric.NotReported("counter-not-read")));
    }

    [Theory]
    [InlineData(0, 60.0, 1)]
    [InlineData(60, 0.0, 1)]
    [InlineData(60, double.NaN, 1)]
    [InlineData(60, 60.0, 0)]
    public void A_document_refuses_numbers_outside_their_range(int seconds, double window, int repeats) =>
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Document(seconds: seconds, window: window, repeats: repeats));

    [Fact]
    public void A_document_refuses_a_blank_task_and_a_window_that_ends_before_it_starts()
    {
        _ = Assert.Throws<ArgumentException>(() => Document(task: " "));
        _ = Assert.Throws<ArgumentException>(() => new CalibrationDocument(
            TaskName, 60, 60.0, 601, 10.0, 15, new DateTimeOffset(2026, 10, 21, 10, 1, 5, TimeSpan.Zero), new DateTimeOffset(2026, 10, 21, 10, 0, 5, TimeSpan.Zero), "0.1.0", "abc", Metric.Measured(1.0, "J"), Metric.Measured(1.0, "J"), Metric.Measured(0.0, "ratio"), Probe(), new CalibrationEnvironment(null, null, null, null, null, null)));
    }

    [Fact]
    public void A_document_without_a_GPU_in_its_probe_is_refused() =>
        _ = Assert.Throws<ArgumentException>(() => new CalibrationDocument(
            TaskName, 60, 60.0, 601, 10.0, 15, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), "0.1.0", "abc", Metric.Measured(1.0, "J"), Metric.Measured(1.0, "J"), Metric.Measured(0.0, "ratio"), ProbeResult.NoGpu, new CalibrationEnvironment(null, null, null, null, null, null)));

    [Fact]
    public void The_environment_refuses_an_offload_outside_zero_to_one_hundred() =>
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new CalibrationEnvironment("nim", null, 101.0, null, null, null));

    [Fact]
    public void A_document_survives_a_round_trip_through_the_json_context()
    {
        CalibrationDocument document = Document(counter: Metric.NotApplicable("counter-not-supported: NOT_SUPPORTED"), difference: Metric.NotApplicable("counter-not-supported"));

        string text = JsonSerializer.Serialize(document, AgentJoulesJsonContext.Default.CalibrationDocument);
        CalibrationDocument? read = JsonSerializer.Deserialize(text, AgentJoulesJsonContext.Default.CalibrationDocument);

        Assert.NotNull(read);
        Assert.Equal(document.Task, read.Task);
        Assert.Equal(document.CounterJoules, read.CounterJoules);
        Assert.Equal(document.SamplingJoules, read.SamplingJoules);
        Assert.Equal(document.RelativeDifference, read.RelativeDifference);
        Assert.Equal(document.Environment, read.Environment);
        Assert.Contains("\"counterJoules\"", text, StringComparison.Ordinal);
        Assert.Contains("\"windowSeconds\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    // ---- the file name, the schema and the writer (criterion 1, 5) ----

    [Fact]
    public void The_file_name_is_calibration_then_the_start_of_the_command()
    {
        Assert.Equal("calibration-20261021T100000Z.json", CalibrationFile.Name(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(2))));
        Assert.Matches(CalibrationFile.Pattern, CalibrationFile.Name(DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("calibration-20261021T100000Z.json", true)]
    [InlineData("calibration-20261021T100000Z.json\n", false)]
    [InlineData("calibration-20261021T100000Z-local.json", false)]
    [InlineData("calibration-20261021T100000.json", false)]
    [InlineData("Calibration-20261021T100000Z.json", false)]
    [InlineData("calibration-20261021T100000Z.jsonl", false)]
    [InlineData("20261021T100000Z-local.json", false)]
    [InlineData("probe-20261021T100000Z.json", false)]
    public void Only_a_calibration_name_is_a_calibration_file_name(string name, bool expected) => Assert.Equal(expected, CalibrationFile.IsValid(name));

    [Fact]
    public void A_calibration_file_name_is_never_a_run_file_name_so_the_selector_and_validate_ignore_it() =>
        Assert.False(RunFileName.IsValid(CalibrationFile.Name(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero))));

    [Fact]
    public void A_written_document_validates_against_the_calibration_definition()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = CalibrationFile.Prepare(workspace.Root, CalibrationFile.Name(RunOrchestrationFixtures.CommandStartUtc));

        CalibrationFile.Write(path, Document());

        string text = File.ReadAllText(path);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
        using JsonDocument written = JsonDocument.Parse(text);
        Assert.Empty(SchemaValidator.Validate(written.RootElement, SchemaTarget.Calibration));
    }

    [Fact]
    public void A_document_with_N_A_totals_validates_too()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = CalibrationFile.Prepare(workspace.Root, CalibrationFile.Name(RunOrchestrationFixtures.CommandStartUtc));

        CalibrationFile.Write(path, Document(counter: Metric.NotApplicable("counter-not-supported: NOT_SUPPORTED"), difference: Metric.NotApplicable("counter-not-supported")));

        using JsonDocument written = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Empty(SchemaValidator.Validate(written.RootElement, SchemaTarget.Calibration));
    }

    [Fact]
    public void The_schema_refuses_an_estimated_total_a_missing_member_and_a_member_it_does_not_define()
    {
        string text = JsonSerializer.Serialize(Document(), AgentJoulesJsonContext.Default.CalibrationDocument);

        Assert.Empty(Check(text));
        Assert.NotEmpty(Check(text.Replace("\"label\": \"measured\"", "\"label\": \"estimated\"", StringComparison.Ordinal)));
        Assert.NotEmpty(Check(text.Replace("\"repeats\": 15,", string.Empty, StringComparison.Ordinal)));
        Assert.NotEmpty(Check(text.Replace("\"repeats\": 15,", "\"repeats\": 15, \"extra\": 1,", StringComparison.Ordinal)));
        Assert.NotEmpty(Check(text.Replace("\"seconds\": 60,", "\"seconds\": 0,", StringComparison.Ordinal)));

        static IReadOnlyList<SchemaFailure> Check(string json)
        {
            using JsonDocument parsed = JsonDocument.Parse(json);
            return SchemaValidator.Validate(parsed.RootElement, SchemaTarget.Calibration);
        }
    }

    [Fact]
    public void A_run_file_is_not_valid_as_a_calibration_file()
    {
        using JsonDocument run = RunOrchestrationFixtures.Read(Path.Combine(ConfigurationFixtures.RepositoryRoot(), "tests", "AgentJoules.TestSupport", "Fixtures", "results", "20260101T000000Z-local.json"));

        Assert.NotEmpty(SchemaValidator.Validate(run.RootElement, SchemaTarget.Calibration));
    }

    [Fact]
    public void The_file_is_never_replaced()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = CalibrationFile.Name(RunOrchestrationFixtures.CommandStartUtc);
        string path = CalibrationFile.Prepare(workspace.Root, name);
        CalibrationFile.Write(path, Document());
        string before = File.ReadAllText(path);

        AgentJoulesException prepare = Assert.Throws<AgentJoulesException>(() => CalibrationFile.Prepare(workspace.Root, name));
        AgentJoulesException write = Assert.Throws<AgentJoulesException>(() => CalibrationFile.Write(path, Document(task: "api-migration")));

        Assert.Equal(ExitCode.Unexpected, prepare.ExitCode);
        Assert.Equal(ExitCode.Unexpected, write.ExitCode);
        Assert.Contains(name, prepare.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, ".*"));
    }

    [Fact]
    public void A_name_that_is_not_a_calibration_name_is_refused_before_anything_is_created()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        _ = Assert.Throws<ArgumentException>(() => CalibrationFile.Prepare(workspace.Root, "../calibration-20261021T100000Z.json"));
        _ = Assert.Throws<ArgumentException>(() => CalibrationFile.Prepare(workspace.Root, "20261021T100000Z-local.json"));
    }

    // ---- the command: the window, the two methods and the file (criteria 1, 2) ----

    [Fact]
    public async Task Calibrate_writes_both_totals_and_their_relative_difference_for_one_window()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        Script(rig, wattsAt: StoryWattsAt, counterScale: StoryCounterScale);

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 60");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        JsonElement root = file.RootElement;
        Assert.Equal(100.0, root.GetProperty("counterJoules").GetProperty("value").GetDouble(), StoryTolerance);
        Assert.Equal(98.0, root.GetProperty("samplingJoules").GetProperty("value").GetDouble(), StoryTolerance);
        Assert.Equal(0.02, root.GetProperty("relativeDifference").GetProperty("value").GetDouble(), StoryTolerance);
        foreach (string metric in new[] { "counterJoules", "samplingJoules" })
        {
            Assert.Equal("measured", root.GetProperty(metric).GetProperty("label").GetString());
            Assert.Equal("J", root.GetProperty(metric).GetProperty("unit").GetString());
        }

        Assert.Equal("ratio", root.GetProperty("relativeDifference").GetProperty("unit").GetString());
    }

    [Fact]
    public async Task The_plain_fields_describe_the_window_the_samples_and_the_tool()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        rig.Setup.PowerSource.Value = PowerSources.Ac;

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 60");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        JsonElement root = file.RootElement;
        Assert.Equal(TaskName, root.GetProperty("task").GetString());
        Assert.Equal(60, root.GetProperty("seconds").GetInt32());
        double window = root.GetProperty("windowSeconds").GetDouble();
        int repeats = root.GetProperty("repeats").GetInt32();
        Assert.InRange(window, 60.0, 64.0);
        Assert.True(repeats >= 1, "At least one repeat completes.");
        Assert.Equal(10.0, root.GetProperty("sampleRateHz").GetDouble());
        Assert.True(root.GetProperty("sampleCount").GetInt32() >= 2, "The window holds at least its two edge samples.");
        DateTimeOffset start = root.GetProperty("startUtc").GetDateTimeOffset();
        // The window opens after the preflight and the warm-up, so its start is not the start of the command: 4 s of fake time measured on the NIM rig, 2 s of which come after the service starts (a start read before the warm-up gives 2 s).
        Assert.Equal(TimeSpan.FromSeconds(4), start - RunOrchestrationFixtures.CommandStartUtc);
        DateTimeOffset end = root.GetProperty("endUtc").GetDateTimeOffset();
        Assert.Equal(window, (end - start).TotalSeconds, 1e-6);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("toolVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("gitCommit").GetString()));
        Assert.True(root.GetProperty("probe").GetProperty("gpuPresent").GetBoolean());
        Assert.True(root.GetProperty("probe").GetProperty("energyCounterSupported").GetBoolean());

        JsonElement environment = root.GetProperty("environment");
        Assert.Equal("nim", environment.GetProperty("localRuntime").GetString());
        Assert.Equal(100.0, environment.GetProperty("gpuOffloadPercent").GetDouble());
        Assert.Equal("ac", environment.GetProperty("powerSource").GetString());
        Assert.Equal(JsonValueKind.String, environment.GetProperty("precision").ValueKind);

        // The version is the release the fake NIM answers; the load time is the one second the fake takes to answer each warm-up call on the fake clock.
        Assert.Equal("1.8.5", environment.GetProperty("runtimeVersion").GetString());
        Assert.Equal(rig.Recorder.Count(OperationRecorder.WarmUp) * 1.0, environment.GetProperty("modelLoadSeconds").GetDouble());
    }

    [Fact]
    public async Task The_workflow_repeats_inside_the_window_unmetered_without_a_judge_a_baseline_or_a_run_file()
    {
        using LocalRunRig rig = LocalRunRig.Nim();

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 60");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        int repeats = file.RootElement.GetProperty("repeats").GetInt32();
        Assert.True(repeats > 1, $"A 60 s window holds several repeats of a 4 s workflow, but it holds {repeats}.");

        // One unmetered warm-up before the first task call, then four calls per repeat, no baseline, no judge call, no run file.
        string[] operations = [.. rig.Recorder.Operations];
        int firstTask = Array.IndexOf(operations, OperationRecorder.TaskCall);
        Assert.True(rig.Recorder.Count(OperationRecorder.WarmUp) >= 1);
        Assert.True(Array.LastIndexOf(operations, OperationRecorder.WarmUp) < firstTask, $"The warm-up must come first: {rig.Recorder.Describe()}");
        Assert.Equal(4 * repeats, rig.Recorder.Count(OperationRecorder.TaskCall));
        Assert.Equal(1, rig.Recorder.Count(OperationRecorder.MeterStart));
        Assert.Equal(0, rig.Recorder.Count(OperationRecorder.Baseline));
        Assert.Equal(0, rig.Hosted.Count(FakeRoute.ChatCompletions));
        Assert.Empty(rig.RunFiles());
    }

    [Fact]
    public async Task A_repeat_in_flight_at_the_deadline_completes_and_the_window_ends_after_it()
    {
        using LocalRunRig rig = LocalRunRig.Nim();

        // 5 s on a 4 s workflow: the second repeat starts at 4 s, is in flight at the deadline and completes, so the window is 8 s long.
        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 5");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        Assert.Equal(2, file.RootElement.GetProperty("repeats").GetInt32());
        Assert.InRange(file.RootElement.GetProperty("windowSeconds").GetDouble(), 8.0, 8.5);
    }

    [Fact]
    public async Task One_second_still_completes_one_repeat()
    {
        using LocalRunRig rig = LocalRunRig.Nim();

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        Assert.Equal(1, file.RootElement.GetProperty("repeats").GetInt32());
    }

    [Fact]
    public async Task The_defaults_are_the_first_task_in_file_order_and_sixty_seconds()
    {
        using LocalRunRig rig = LocalRunRig.Nim();

        CliRunResult result = await RunAsync(rig, "calibrate");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        string first = TaskSetLoader.Load(Path.Combine(rig.Root, RunCommandHarness.TasksFolder))[0].Name;
        Assert.Equal(first, file.RootElement.GetProperty("task").GetString());
        Assert.Equal(60, file.RootElement.GetProperty("seconds").GetInt32());
    }

    [Fact]
    public async Task A_named_task_is_the_one_that_repeats()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        string second = TaskSetLoader.Load(Path.Combine(rig.Root, RunCommandHarness.TasksFolder))[1].Name;

        CliRunResult result = await RunAsync(rig, $"calibrate --task {second} --seconds 1");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        Assert.Equal(second, file.RootElement.GetProperty("task").GetString());
    }

    [Fact]
    public async Task The_file_is_named_after_the_start_of_the_command_and_validates()
    {
        using LocalRunRig rig = LocalRunRig.Nim();

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.True(result.ExitCode == 0, Fail(result));
        string path = Assert.Single(CalibrationFiles(rig));
        Assert.Equal("calibration-20261021T100000Z.json", Path.GetFileName(path));
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Empty(SchemaValidator.Validate(file.RootElement, SchemaTarget.Calibration));
        Assert.Equal(["calibration-20261021T100000Z.json"], RunOrchestrationFixtures.EntriesIn(rig.Root));
    }

    [Fact]
    public async Task Out_names_the_folder_and_the_console_shows_the_path_as_given()
    {
        using LocalRunRig rig = LocalRunRig.Nim();

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1 --out out/cal --no-console-telemetry");

        Assert.True(result.ExitCode == 0, Fail(result));
        _ = Assert.Single(CalibrationFiles(rig, "out/cal"));
        Assert.Contains("calibration: out/cal/calibration-20261021T100000Z.json", RunOrchestrationFixtures.StdoutLines(result));
        Assert.Empty(CalibrationFiles(rig));
    }

    [Fact]
    public async Task An_existing_file_of_the_same_name_is_refused_with_exit_1_before_any_request()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        string folder = Path.Combine(rig.Root, RunOrchestrationFixtures.ResultsFolder);
        _ = Directory.CreateDirectory(folder);
        string existing = Path.Combine(folder, "calibration-20261021T100000Z.json");
        File.WriteAllText(existing, "keep");

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.Contains("calibration-20261021T100000Z.json", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
    }

    // ---- the console (P-4) ----

    [Fact]
    public async Task The_console_says_the_scope_once_then_the_file_and_one_result_line()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        Script(rig, wattsAt: StoryWattsAt, counterScale: StoryCounterScale);

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 60 --no-console-telemetry");

        Assert.True(result.ExitCode == 0, Fail(result));
        string[] lines = RunOrchestrationFixtures.StdoutLines(result);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Energy is GPU-board energy only.", lines[0]);
        Assert.Equal("calibration: results/calibration-20261021T100000Z.json", lines[1]);
        Match line = ResultLine.Match(lines[2]);
        Assert.True(line.Success, lines[2]);
        Assert.Equal("100.00 J", line.Groups["counter"].Value);
        Assert.Equal("98.00 J", line.Groups["sampling"].Value);
        Assert.Equal("0.0200", line.Groups["difference"].Value);
        Assert.True(double.Parse(line.Groups["window"].Value, CultureInfo.InvariantCulture) >= 60.0);
        Assert.True(int.Parse(line.Groups["samples"].Value, CultureInfo.InvariantCulture) >= 2);
        Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unsupported_counter_is_written_as_N_A_with_the_probe_reason_and_the_console_says_so()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        Script(rig, counterSupported: false);

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 60 --no-console-telemetry");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        JsonElement root = file.RootElement;
        JsonElement counter = root.GetProperty("counterJoules");
        Assert.Equal(JsonValueKind.Null, counter.GetProperty("value").ValueKind);
        Assert.Equal("N/A", counter.GetProperty("label").GetString());
        string reason = counter.GetProperty("reason").GetString()!;
        Assert.Equal(UnsupportedCounterReason, reason);
        Assert.Equal("N/A", root.GetProperty("relativeDifference").GetProperty("label").GetString());
        Assert.Equal("counter-not-supported", root.GetProperty("relativeDifference").GetProperty("reason").GetString());
        Assert.Equal("measured", root.GetProperty("samplingJoules").GetProperty("label").GetString());
        Assert.False(root.GetProperty("probe").GetProperty("energyCounterSupported").GetBoolean());
        Assert.Empty(SchemaValidator.Validate(root, SchemaTarget.Calibration));
        Assert.Matches(@"^counter=N/A sampling=\d+\.\d\d J difference=N/A window=", RunOrchestrationFixtures.StdoutLines(result)[^1]);
    }

    [Fact]
    public async Task A_counter_that_reads_zero_gives_an_N_A_difference_and_a_measured_zero_total()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        Script(rig, counterScale: 0.0);

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 60");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        Assert.Equal(0.0, file.RootElement.GetProperty("counterJoules").GetProperty("value").GetDouble());
        JsonElement difference = file.RootElement.GetProperty("relativeDifference");
        Assert.Equal(JsonValueKind.Null, difference.GetProperty("value").ValueKind);
        Assert.Equal("counter-zero", difference.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_counter_that_answers_NOT_SUPPORTED_when_the_window_closes_is_written_as_N_A_with_the_reason()
    {
        using LocalRunRig rig = LocalRunRig.Nim();

        // The counter is supported when the GPU is opened (the preflight and the service each probe it), and fails from the fourth read on (the preflight probe, the service probe, the window opening, the window closing): the read at the end of the window.
        rig.Setup.Gpu!.CounterReturnForRead = static call => call >= 4 ? NvmlReturn.NotSupported : NvmlReturn.Success;

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        JsonElement counter = file.RootElement.GetProperty("counterJoules");
        Assert.Equal("N/A", counter.GetProperty("label").GetString());
        Assert.Equal("counter-not-supported: counter-not-read", counter.GetProperty("reason").GetString());
        Assert.Equal("N/A", file.RootElement.GetProperty("relativeDifference").GetProperty("label").GetString());
        Assert.Equal("measured", file.RootElement.GetProperty("samplingJoules").GetProperty("label").GetString());
        Assert.True(file.RootElement.GetProperty("probe").GetProperty("energyCounterSupported").GetBoolean());
        Assert.Empty(SchemaValidator.Validate(file.RootElement, SchemaTarget.Calibration));
    }

    [Fact]
    public async Task A_forced_counter_method_on_a_GPU_without_the_counter_is_still_a_sampling_calibration()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        rig.Setup.Settings["AgentJoules:Meter:Method"] = "counter";
        Script(rig, counterSupported: false);

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        JsonElement root = file.RootElement;
        JsonElement counter = root.GetProperty("counterJoules");
        Assert.Equal("N/A", counter.GetProperty("label").GetString());
        Assert.Equal(UnsupportedCounterReason, counter.GetProperty("reason").GetString());
        Assert.Equal("measured", root.GetProperty("samplingJoules").GetProperty("label").GetString());
        Assert.Equal("N/A", root.GetProperty("relativeDifference").GetProperty("label").GetString());
        Assert.False(root.GetProperty("probe").GetProperty("energyCounterSupported").GetBoolean());
        Assert.Empty(SchemaValidator.Validate(root, SchemaTarget.Calibration));
    }

    [Fact]
    public async Task A_counter_that_goes_backwards_is_written_as_N_A_with_the_reason_counter_went_backwards_and_so_is_the_difference()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        ScriptedNvmlApi gpu = rig.Setup.Gpu!;

        // The read at the end of the window (the fourth) finds the counter lower than the read at its start: the offset drops to 0.
        gpu.CounterReturnForRead = call =>
        {
            if (call >= 4)
            {
                gpu.CounterOffsetMillijoules = 0;
            }

            return NvmlReturn.Success;
        };

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        JsonElement root = file.RootElement;
        JsonElement counter = root.GetProperty("counterJoules");
        Assert.Equal(JsonValueKind.Null, counter.GetProperty("value").ValueKind);
        Assert.Equal("N/A", counter.GetProperty("label").GetString());
        Assert.Equal("counter-went-backwards", counter.GetProperty("reason").GetString());
        Assert.Equal("counter-went-backwards", root.GetProperty("relativeDifference").GetProperty("reason").GetString());
        Assert.Equal("measured", root.GetProperty("samplingJoules").GetProperty("label").GetString());
        Assert.Empty(SchemaValidator.Validate(root, SchemaTarget.Calibration));
    }

    [Fact]
    public async Task On_a_linear_power_ramp_the_sampling_total_equals_the_counter_total()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        Script(rig, wattsAt: static t => 10.0 + (0.25 * t));

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 60");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        double counter = file.RootElement.GetProperty("counterJoules").GetProperty("value").GetDouble();
        double sampling = file.RootElement.GetProperty("samplingJoules").GetProperty("value").GetDouble();
        Assert.True(counter > 600.0, $"The ramp draws at least 10 W for 60 s, but the counter read {counter} J.");
        Assert.Equal(counter, sampling, 0.05);
    }

    // ---- the runtimes and the preflight (criterion 4, P-1) ----

    [Fact]
    public async Task An_Ollama_calibration_records_the_runtime_the_offload_and_the_quantization()
    {
        using LocalRunRig rig = LocalRunRig.Ollama(OllamaFixtures.Ps(OllamaFixtures.PsEntry(OllamaFixtures.StoryTag, 1000, 1000)), OllamaFixtures.Show("Q4_K_M"));

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.True(result.ExitCode == 0, Fail(result));
        using JsonDocument file = ReadOnlyFile(rig);
        JsonElement environment = file.RootElement.GetProperty("environment");
        Assert.Equal("ollama", environment.GetProperty("localRuntime").GetString());
        Assert.Equal(100.0, environment.GetProperty("gpuOffloadPercent").GetDouble());
        Assert.Equal("Q4_K_M", environment.GetProperty("precision").GetString());
        Assert.Empty(SchemaValidator.Validate(file.RootElement, SchemaTarget.Calibration));
    }

    [Fact]
    public async Task A_model_the_runtime_does_not_list_exits_4_and_writes_nothing()
    {
        using LocalRunRig rig = LocalRunRig.Ollama(OllamaFixtures.Ps(), listedTags: ["other-model:1b"]);

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.True(result.ExitCode == 4, Fail(result));
        Assert.Empty(CalibrationFiles(rig));
        Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
    }

    [Fact]
    public async Task A_runtime_that_does_not_answer_exits_5_and_writes_nothing()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        rig.Local.ConnectionRefused = true;

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.True(result.ExitCode == 5, Fail(result));
        Assert.Empty(CalibrationFiles(rig));
    }

    [Fact]
    public async Task Without_a_GPU_calibrate_exits_6_and_writes_no_file()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        rig.Setup.Gpu!.MissingLibrary = true;

        CliRunResult result = await RunAsync(rig, "calibrate");

        Assert.True(result.ExitCode == 6, Fail(result));
        Assert.Empty(CalibrationFiles(rig));
        Assert.Empty(RunOrchestrationFixtures.EntriesIn(rig.Root));
        Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
    }

    [Fact]
    public async Task The_judge_is_not_checked_so_a_judge_model_the_hosted_tier_does_not_list_is_no_failure()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        rig.Setup.Settings["AgentJoules:Judge:ModelId"] = "no-such-judge-model";

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.True(result.ExitCode == 0, Fail(result));
        Assert.Equal(0, rig.Hosted.Count(FakeRoute.Models));
        _ = Assert.Single(CalibrationFiles(rig));
    }

    [Fact]
    public async Task A_runtime_that_states_an_offload_above_100_exits_6_before_the_window_and_makes_no_task_call()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        RunOrchestrationFixtures.AddServices(rig.Setup, static services =>
        {
            _ = services.RemoveAll<ILocalRuntime>();
            _ = services.AddSingleton<ILocalRuntime>(new FixedRuntime(new LocalRuntimeInfo("fixed", "1.2.3", 150.0, null)));
        });

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.True(result.ExitCode == 6, Fail(result));
        Assert.Equal(0, rig.Recorder.Count(OperationRecorder.TaskCall));
        Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
        Assert.Empty(CalibrationFiles(rig));
    }

    [Fact]
    public async Task The_service_refuses_an_agent_factory_that_meters_and_accepts_one_that_does_not()
    {
        await using AgentFactoryRig rig = await AgentFactoryRig.CreateAsync();
        var unmetered = new AgentFactory(rig.Providers, rig.Services, RetryOptions.Default, rig.Time, new Random(1)) { Metered = false };

        ArgumentException refused = Assert.Throws<ArgumentException>(() => NewService(rig, rig.Factory));
        Assert.Equal("agents", refused.ParamName);
        Assert.NotNull(NewService(rig, unmetered));
        _ = Assert.Throws<ArgumentNullException>(() => NewService(rig, null!));
    }

    private static CalibrationService NewService(AgentFactoryRig rig, AgentFactory agents) =>
        new(Options.Create(new AgentJoulesOptions()), new NoGpu(), new FixedRuntime(new LocalRuntimeInfo("fixed", "1.2.3", 100.0, null)), new FakePowerSourceDetector(), rig.Time, agents);

    // ---- the arguments (criterion 4) ----

    [Theory]
    [InlineData("calibrate --seconds 0")]
    [InlineData("calibrate --seconds -5")]
    [InlineData("calibrate --seconds abc")]
    [InlineData("calibrate --seconds 1.5")]
    [InlineData("calibrate --seconds")]
    public async Task A_bad_option_value_exits_2_before_any_host_is_built(string arguments)
    {
        using LocalRunRig rig = LocalRunRig.Nim();

        CliRunResult result = await RunAsync(rig, arguments);

        Assert.True(result.ExitCode == 2, Fail(result));
        Assert.Equal(0, rig.Setup.HostBuilds);
        Assert.Empty(CalibrationFiles(rig));
        Assert.Contains("--", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zero_duration_is_refused_by_name()
    {
        using LocalRunRig rig = LocalRunRig.Nim();

        CliRunResult result = await RunAsync(rig, "calibrate --seconds 0");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--seconds", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_task_exits_2_naming_it_and_the_known_names_and_makes_no_request()
    {
        using LocalRunRig rig = LocalRunRig.Nim();
        string[] known = [.. TaskSetLoader.Load(Path.Combine(rig.Root, RunCommandHarness.TasksFolder)).Select(static task => task.Name)];

        CliRunResult result = await RunAsync(rig, "calibrate --task no-such-task");

        Assert.True(result.ExitCode == 2, Fail(result));
        string error = Assert.Single(result.ErrorLines);
        Assert.Contains("no-such-task", error, StringComparison.Ordinal);
        foreach (string name in known)
        {
            Assert.Contains(name, error, StringComparison.Ordinal);
        }

        Assert.Empty(CalibrationFiles(rig));
        Assert.Equal(0, rig.Local.Count(FakeRoute.ChatCompletions));
    }

    [Fact]
    public async Task A_command_line_that_names_the_hosted_tier_is_not_a_calibrate_option()
    {
        using LocalRunRig rig = LocalRunRig.Nim();

        CliRunResult result = await RunAsync(rig, "calibrate --tier nvidia");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(CalibrationFiles(rig));
    }

    [Fact]
    public async Task The_workflow_agents_are_the_four_of_the_catalog()
    {
        using LocalRunRig rig = LocalRunRig.Nim();

        CliRunResult result = await RunAsync(rig, $"calibrate --task {TaskName} --seconds 1");

        Assert.True(result.ExitCode == 0, Fail(result));
        Assert.Equal(AgentCatalog.Names.Count, rig.Recorder.Count(OperationRecorder.TaskCall));
    }

    /// <summary>
    /// The telemetry of <c>calibrate</c> (REQ-014, AJ-051 spec review): the option of <see cref="TelemetryOptions.CreateNoConsoleTelemetry"/> exists, and the host registers the console metrics exporter unless it is given.
    /// </summary>
    [Trait("Spec", "REQ-014")]
    public sealed class Telemetry
    {
        private static int MeterConfigurations(bool console) =>
            ServiceRegistrations.Count(TelemetrySetup.Add(new ServiceCollection(), new ConfigurationBuilder().Build(), console), ServiceRegistrations.MeterProviderConfigurations);

        private static async Task<(CliRunResult Result, int Meters)> CountAsync(string arguments)
        {
            using LocalRunRig rig = LocalRunRig.Nim();
            int meters = -1;
            RunOrchestrationFixtures.AddServices(rig.Setup, services => meters = ServiceRegistrations.Count(services, ServiceRegistrations.MeterProviderConfigurations));
            CliRunResult result = await rig.RunAsync(arguments).WaitAsync(HangGuard);
            return (result, meters);
        }

        [Fact]
        public async Task Calibrate_registers_the_console_exporter_by_default()
        {
            (CliRunResult result, int meters) = await CountAsync($"calibrate --task {TaskName} --seconds 1");

            Assert.True(result.ExitCode == 0, Fail(result));
            Assert.Equal(MeterConfigurations(console: false) + 1, MeterConfigurations(console: true));
            Assert.Equal(MeterConfigurations(console: true), meters);
            Assert.Contains("Resource associated with Metrics", result.Stdout, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Calibrate_no_console_telemetry_omits_console_exporter()
        {
            (CliRunResult result, int meters) = await CountAsync($"calibrate --task {TaskName} --seconds 1 --no-console-telemetry");

            Assert.True(result.ExitCode == 0, Fail(result));
            Assert.Equal(MeterConfigurations(console: false), meters);
            Assert.True(meters > 0, "The command registered no telemetry at all.");
            Assert.DoesNotContain("Resource associated with Metrics", result.Stdout, StringComparison.Ordinal);
        }

        [Fact]
        public void The_option_is_the_shared_one()
        {
            var option = TelemetryOptions.CreateNoConsoleTelemetry();

            Assert.Equal("--no-console-telemetry", option.Name);
        }
    }

    // A runtime that is available, warms up at once and describes itself as given.
    private sealed class FixedRuntime(LocalRuntimeInfo description) : ILocalRuntime
    {
        public string Name => description.Name ?? "fixed";

        public Task CheckAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LocalRuntimeInfo> DescribeAsync(CancellationToken cancellationToken) => Task.FromResult(description);

        public Task<TimeSpan?> WarmUpAsync(string modelId, CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);
    }

    // A GPU port that is never asked: the constructor tests of the service do not open a GPU.
    private sealed class NoGpu : IGpuTelemetrySourceFactory
    {
        public GpuOpenResult Open(int gpuIndex) => GpuOpenResult.Unavailable("no GPU in this test");
    }

    [GeneratedRegex(@"^counter=(?<counter>\S+(?: J)?) sampling=(?<sampling>\S+(?: J)?) difference=(?<difference>\S+) window=(?<window>\S+) s samples=(?<samples>\d+)$", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}