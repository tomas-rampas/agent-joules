using System.Text.Json;

using AgentJoules.Cli;

using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>
/// The rig of the AJ-105 scenarios and tests (REQ-020): one <c>run --tier local</c> of the production orchestrator on a fake clock, with a scripted GPU behind the real NVML binding, a loopback fake where the local
/// runtime stands (Ollama or a NIM container, answering the native routes and the completions) and the judge tier's models route. Nothing leaves the process: every URL is a loopback address built from parts, every
/// key a fake one, and the deny-all handler of the harness stops any request the rig did not route. The run files are read back, and the report command is run on them to get the rendered header.
/// </summary>
public sealed class LocalRunRig : IDisposable
{
    /// <summary>The command line of the story's scenarios.</summary>
    public const string StoryCommand = "run --tier local --repeat 1 --task incident-brief";

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private readonly TempWorkspace _workspace;
    private int _reports;

    private LocalRunRig(TempWorkspace workspace, TimerTrackingTimeProvider time, FakeHttpMessageHandler local, RunCommandSetup setup)
    {
        _workspace = workspace;
        Time = time;
        Local = local;
        Setup = setup;
    }

    /// <summary>The fake clock of the run.</summary>
    public TimerTrackingTimeProvider Time { get; }

    /// <summary>The fake that stands where the local runtime stands.</summary>
    public FakeHttpMessageHandler Local { get; }

    /// <summary>The setup of the run; a test may add configuration or services before <see cref="RunAsync(string)"/>.</summary>
    public RunCommandSetup Setup { get; }

    /// <summary>The working directory of the run.</summary>
    public string Root => _workspace.Root;

    /// <summary>The result of the last <see cref="RunAsync(string)"/>, or null before it ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>
    /// An Ollama rig: <c>Local:Runtime</c> <c>ollama</c>, the local tier at the loopback address of Ollama, the tag listed on the models route, and the fake's <c>/api/ps</c> and <c>/api/show</c> answers as given.
    /// </summary>
    /// <param name="psBody">The body of <c>/api/ps</c> (<see cref="OllamaFixtures.Ps(string[])"/>).</param>
    /// <param name="showBody">The body of <c>/api/show</c> (<see cref="OllamaFixtures.Show(string?)"/>), or null for the default quantization level.</param>
    /// <param name="tag">The configured <c>Tiers:local:ModelId</c>; <see cref="OllamaFixtures.StoryTag"/> when null.</param>
    /// <param name="agentModelIds">Per-agent tags, or null for none.</param>
    /// <param name="listedTags">The tags the models route lists, or null for the configured tag and the per-agent tags.</param>
    /// <returns>The rig; the caller disposes it.</returns>
    public static LocalRunRig Ollama(string psBody, string? showBody = null, string? tag = null, IReadOnlyDictionary<string, string>? agentModelIds = null, IReadOnlyList<string>? listedTags = null)
    {
        string configured = tag ?? OllamaFixtures.StoryTag;
        string[] tags = [.. listedTags ?? [configured, .. (agentModelIds ?? new Dictionary<string, string>()).Values]];
        return Create(
            time => new FakeHttpMessageHandler { ModelIds = tags, OllamaStyleModels = true, Clock = time, DefaultDuration = TimeSpan.FromSeconds(1), OllamaPsBody = psBody, OllamaShowBody = showBody ?? OllamaFixtures.Show("Q4_K_M") },
            [
                ("AgentJoules:Local:Runtime", "ollama"),
                ("AgentJoules:Tiers:local:BaseUrl", OllamaFixtures.LocalBaseUrl()),
                ("AgentJoules:Tiers:local:ModelId", configured),
                .. (agentModelIds ?? new Dictionary<string, string>()).Select(static pair => ("AgentJoules:Tiers:local:AgentModelIds:" + pair.Key, (string?)pair.Value)),
            ]);
    }

    /// <summary>
    /// A NIM rig: the shipped configuration (<c>Local:Runtime</c> <c>nim</c>, the local tier at port 8000, precision <c>BF16</c>), a fake that lists the shipped model ID and answers <c>/v1/version</c>.
    /// </summary>
    /// <param name="precision">The configured <c>Tiers:local:Precision</c>; the shipped value when null.</param>
    /// <returns>The rig; the caller disposes it.</returns>
    public static LocalRunRig Nim(string? precision = null) =>
        Create(
            static time => new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), Clock = time, DefaultDuration = TimeSpan.FromSeconds(1) },
            precision is null ? [] : [("AgentJoules:Tiers:local:Precision", precision)]);

    /// <summary>Runs <paramref name="arguments"/> on the fake clock and keeps the result.</summary>
    /// <param name="arguments">The command line; the story's command by default.</param>
    /// <returns>The result of the run.</returns>
    public async Task<CliRunResult> RunAsync(string arguments = StoryCommand)
    {
        Result = await RunOrchestrationFixtures.RunDrivenAsync(arguments, Setup, Time).WaitAsync(HangGuard);
        return Result;
    }

    /// <summary>The run files the command wrote, in ordinal order.</summary>
    /// <returns>The full paths; empty when none.</returns>
    public string[] RunFiles() => RunOrchestrationFixtures.RunFilesIn(Root);

    /// <summary>Reads the one run file the command wrote; the caller disposes the document.</summary>
    /// <returns>The document.</returns>
    public JsonDocument ReadRunFile() => RunOrchestrationFixtures.Read(Assert.Single(RunFiles()));

    /// <summary>Runs the report command on the results folder of the run and returns the text of <c>summary.md</c>.</summary>
    /// <returns>The Markdown summary.</returns>
    public async Task<string> SummaryAsync() => File.ReadAllText(Path.Combine(await ReportAsync(), "summary.md"));

    /// <summary>Runs the report command on the results folder of the run and returns the text of <c>index.html</c>.</summary>
    /// <returns>The HTML page.</returns>
    public async Task<string> HtmlAsync() => File.ReadAllText(Path.Combine(await ReportAsync(), "index.html"));

    /// <inheritdoc />
    public void Dispose() => _workspace.Dispose();

    private async Task<string> ReportAsync()
    {
        string output = Path.Combine(Root, "report-out-" + (++_reports).ToString(System.Globalization.CultureInfo.InvariantCulture));
        CliRunResult report = await CliHarness.RunTokensAsync(
            ["report", "--in", Path.Combine(Root, RunOrchestrationFixtures.ResultsFolder), "--out", output],
            new CliHostOverrides(WorkingDirectory: Root)).WaitAsync(HangGuard);
        Assert.True(report.ExitCode == 0, $"The report command exited {report.ExitCode}; stdout: {report.Stdout}; stderr: {report.Stderr}");
        return output;
    }

    private static LocalRunRig Create(Func<TimerTrackingTimeProvider, FakeHttpMessageHandler> makeLocal, IEnumerable<(string Key, string? Value)> extra)
    {
        TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        var gpu = new ScriptedNvmlApi(time) { ConstantPowerWatts = 30.0 };
        FakeHttpMessageHandler local = makeLocal(time);
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["local"] = local, ["nvidia"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)) },
            [.. extra]);
        setup.Gpu = gpu;
        RunOrchestrationFixtures.UseClock(setup, time);
        return new LocalRunRig(workspace, time, local, setup);
    }
}