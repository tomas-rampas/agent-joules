using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-037: <c>probe --save</c> and <c>--suffix</c>, and the laptop probe file. Self-contained: its state lives here, not in ScenarioWorld. The class
/// is scoped to the feature because "I run ..." and "the exit code is ..." are sentences of every CLI story. Each scenario owns a
/// <see cref="TempWorkspace"/> that is the content root of the run (the harness gets it as the working directory, so <c>results/</c> resolves under
/// it), a <see cref="FakeTimeProvider"/> set to 2026-10-19T09:30:15Z that replaces the host clock, and a <see cref="ScriptedNvmlApi"/> behind the
/// real GPU factory; a missing NVML loader is the scripted API in missing-library mode. The two @integration scenarios follow the AJ-036 pattern:
/// the Given step evaluates <see cref="IntegrationGate.SkipReason"/> for AGENTJOULES_INTEGRATION (read, never set) and, with the gate closed, the
/// remaining steps only record "gate closed" and pass without starting a process. With the gate open, the laptop scenario starts the built Cli
/// assembly as a child process in the workspace (production path, real NVML) and the driver scenario reads the newest committed laptop probe file
/// under the repository root; both compare <c>driverVersion</c> with the first line of
/// <c>nvidia-smi --query-gpu=driver_version --format=csv,noheader</c>, started by its full path (the system directory, then Program Files, then PATH).
/// </summary>
[Binding]
[Scope(Feature = "Save the probe result and commit the laptop probe file")]
public sealed partial class ProbeSaveSteps : IDisposable
{
    private const string ClosedGateReason = "Set AGENTJOULES_INTEGRATION=1";
    private const string SeedContent = "{\"preexisting\": true}\n";

    private static readonly TimeSpan ChildLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SmiLimit = TimeSpan.FromSeconds(60);

    private readonly IReqnrollOutputHelper _output;
    private readonly ScriptedNvmlApi _api = new(new FakeTimeProvider());
    private readonly FakeTimeProvider _clock = new(ProbeSaveFixtures.FixedInstant);
    private readonly TempWorkspace _workspace = TempWorkspace.Create();
    private CliRunResult? CurrentRun { get; set; }
    private ProcessOutcome? _child;
    private string? _gateSkipReason;
    private bool _gateEvaluated;
    private string? _committedProbePath;
    private string? _committedDriver;
    private string? _smiDriver;

    /// <summary>Creates the steps for one scenario.</summary>
    /// <param name="output">Receives the notes of a scenario that passes without running its command.</param>
    public ProbeSaveSteps(IReqnrollOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    private sealed record ProcessOutcome(int ExitCode, string Stdout, string Stderr)
    {
        public string Describe() => $"exit {ExitCode}, stdout \"{Excerpt(Stdout)}\", stderr \"{Excerpt(Stderr)}\"";
    }

    private bool GateClosed => _gateEvaluated && _gateSkipReason is not null;

    private CliRunResult Run => CurrentRun ?? throw new InvalidOperationException("The CLI was not run.");

    [GeneratedRegex(@"^probe-[0-9]{8}T[0-9]{6}Z\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex LaptopProbeName();

    /// <inheritdoc />
    public void Dispose()
    {
        _workspace.Dispose();
    }

    private static string Excerpt(string text) => text.Length <= 1500 ? text : text[..1500] + "...";

    // The full path of nvidia-smi: the system directory (where current drivers install it), the legacy NVSMI folder, then every PATH entry.
    private static string FindNvidiaSmi()
    {
        List<string> candidates =
        [
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        ];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(directory, "nvidia-smi.exe"));
            candidates.Add(Path.Combine(directory, "nvidia-smi"));
        }

        string? found = candidates.FirstOrDefault(File.Exists);
        Assert.True(found is not null, "nvidia-smi was not found in the system directory, under Program Files or on PATH");
        return found;
    }

    private static async Task<ProcessOutcome> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan limit,
        string? isolatedHome = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (string variable in CliChildProcess.KeyVariables)
        {
            info.Environment.Remove(variable);
        }

        if (isolatedHome is not null)
        {
            // User secrets live under APPDATA (Windows) or HOME (elsewhere); neither may reach the laptop's own store.
            info.Environment["APPDATA"] = isolatedHome;
            info.Environment["HOME"] = isolatedHome;
            info.Environment["USERPROFILE"] = isolatedHome;
        }

        using var timeout = new CancellationTokenSource(limit);
        using var process = new Process { StartInfo = info };
        Assert.True(process.Start(), "the child process did not start: " + fileName);
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            // The reads are bounded too: a grandchild that keeps a pipe open would otherwise hold the test past the limit.
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdout.WaitAsync(timeout.Token);
            string error = await stderr.WaitAsync(timeout.Token);
            return new ProcessOutcome(process.ExitCode, output, error);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("the child process did not exit within " + limit.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s: " + fileName);
        }
    }

    // The first line of the driver query, trimmed: one line per GPU, and probe reads GPU index 0.
    private static async Task<string> ReadSmiDriverAsync()
    {
        ProcessOutcome smi = await RunProcessAsync(
            FindNvidiaSmi(),
            ["--query-gpu=driver_version", "--format=csv,noheader"],
            Path.GetTempPath(),
            SmiLimit);
        Assert.True(smi.ExitCode == 0, "nvidia-smi failed: " + smi.Describe());
        string first = smi.Stdout.Split('\n')[0].Trim();
        Assert.True(first.Length > 0, "nvidia-smi printed no driver version: " + smi.Describe());
        return first;
    }

    private static JsonDocument ParseOneObject(string json)
    {
        JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document;
    }

    private void EvaluateGate()
    {
        _gateSkipReason = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        _gateEvaluated = true;
    }

    private void AssertGateClosedRecorded()
    {
        Assert.True(_gateEvaluated);
        Assert.Equal(ClosedGateReason, _gateSkipReason);
        _output.WriteLine("gate closed: " + _gateSkipReason);
    }

    private string SavedPath(string name) => ProbeSaveFixtures.FilePath(_workspace, name);

    [Given(@"^the fake clock is 2026-10-19T09:30:15Z and a scripted GPU$")]
    [Given(@"^the same clock and GPU$")]
    public void GivenTheClockAndAScriptedGpu()
    {
        _clock.SetUtcNow(ProbeSaveFixtures.FixedInstant);
    }

    [Given(@"^a scripted GPU$")]
    public static void GivenAScriptedGpu()
    {
        // The default scripted GPU and the default fake clock of the fields are the precondition.
    }

    [Given(@"^the NVML loader is faked as missing$")]
    public void GivenTheLoaderIsMissing()
    {
        _api.MissingLibrary = true;
    }

    [Given(@"^results/probe-20261019T093015Z\.json already exists and the clock reads the same second$")]
    public void GivenTheFileExists()
    {
        _clock.SetUtcNow(ProbeSaveFixtures.FixedInstant);
        _workspace.WriteFile(Path.Combine(ProbeSaveFixtures.ResultsFolder, ProbeSaveFixtures.FixedName), SeedContent);
    }

    [Given(@"^the laptop GPU and AGENTJOULES_INTEGRATION is ""1""$")]
    [Given(@"^the committed probe driverVersion differs from the nvidia-smi value$")]
    public void GivenTheGate()
    {
        // Read, never set: the gate is the laptop's own environment.
        EvaluateGate();
    }

    [When(@"^I run ""([^""]*)"" in a temp workspace$")]
    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments)
    {
        if (!_gateEvaluated)
        {
            CurrentRun = await CliHarness.RunAsync(arguments, ProbeSaveFixtures.Overrides(_api, _workspace, _clock));
            return;
        }

        if (GateClosed)
        {
            return;
        }

        // The laptop scenario: the built Cli assembly in the workspace, the production configuration path and the real NVML. The "AJ " of the
        // scenario text stands for the program name.
        string[] tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> cliArguments = tokens.Length > 0 && tokens[0] == "AJ" ? tokens.Skip(1) : tokens;
        _child = await RunProcessAsync(
            "dotnet",
            [CliChildProcess.CliAssemblyPath(), .. cliArguments],
            _workspace.Root,
            ChildLimit,
            isolatedHome: _workspace.Root);
    }

    [When(@"^the comparison runs$")]
    public async Task WhenTheComparisonRuns()
    {
        // It reads the newest matching file on disk, committed or not: when the gate run happens before the commit, the file is still untracked.
        if (!_gateEvaluated || GateClosed)
        {
            return;
        }

        string results = Path.Combine(ConfigurationFixtures.RepositoryRoot(), ProbeSaveFixtures.ResultsFolder);
        string? newest = Directory.Exists(results)
            ? Directory.GetFiles(results)
                .Where(path => LaptopProbeName().IsMatch(Path.GetFileName(path)))
                .Order(StringComparer.Ordinal)
                .LastOrDefault()
            : null;
        Assert.True(
            newest is not null,
            "the committed laptop probe file results/probe-<yyyyMMddTHHmmssZ>.json does not exist under " + results + "; AJ-037-T3 saves and commits it");

        _committedProbePath = newest;
        using JsonDocument document = ParseOneObject(await File.ReadAllTextAsync(newest));
        _committedDriver = document.RootElement.GetProperty("driverVersion").GetString();
        _smiDriver = await ReadSmiDriverAsync();
    }

    [Then(@"^results/probe-20261019T093015Z\.json exists and equals the stdout JSON$")]
    public void ThenTheFileEqualsStdout()
    {
        Assert.Equal(0, Run.ExitCode);
        string path = SavedPath(ProbeSaveFixtures.FixedName);
        Assert.True(File.Exists(path), "the saved file does not exist: " + path);
        using JsonDocument stdout = ParseOneObject(Run.Stdout);
        Assert.Equal(JsonValueKind.True, stdout.RootElement.GetProperty("gpuPresent").ValueKind);
        Assert.Equal(Encoding.UTF8.GetBytes(Run.Stdout), File.ReadAllBytes(path));
    }

    [Then(@"^results/probe-20261019T093015Z-cloudgpu\.json exists$")]
    public void ThenTheSuffixedFileExists()
    {
        Assert.Equal(0, Run.ExitCode);
        string path = SavedPath(ProbeSaveFixtures.FixedSuffixedName);
        Assert.True(File.Exists(path), "the saved file does not exist: " + path);
        Assert.Equal([ProbeSaveFixtures.FixedSuffixedName], ProbeSaveFixtures.SavedNames(_workspace));
    }

    [Then(@"^the saved file has gpuPresent false and the exit code is 0$")]
    public void ThenTheSavedFileIsAnHonestRecord()
    {
        Assert.Equal(0, Run.ExitCode);
        string path = SavedPath(ProbeSaveFixtures.FixedName);
        Assert.True(File.Exists(path), "the saved file does not exist: " + path);
        using JsonDocument document = ParseOneObject(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.False, document.RootElement.GetProperty("gpuPresent").ValueKind);
    }

    [Then(@"^the exit code is 2$")]
    public void ThenTheExitCodeIs2()
    {
        Assert.Equal(2, Run.ExitCode);
        Assert.Equal(string.Empty, Run.Stdout);
        Assert.Empty(ProbeSaveFixtures.SavedNames(_workspace));
    }

    [Then(@"^the exit code is 2 and no file is written$")]
    public void ThenTheExitCodeIs2AndNothingIsWritten()
    {
        Assert.Equal(2, Run.ExitCode);
        Assert.Equal(string.Empty, Run.Stdout);
        Assert.False(Directory.Exists(ProbeSaveFixtures.ResultsPath(_workspace)), "the results folder was created");
        Assert.Equal(["config"], ProbeSaveFixtures.RootEntries(_workspace));
    }

    [Then(@"^the exit code is 1, stderr says ""file exists"" and the file is unchanged$")]
    public void ThenTheExistingFileIsKept()
    {
        Assert.Equal(1, Run.ExitCode);
        Assert.Contains("file exists", Assert.Single(Run.ErrorLines), StringComparison.Ordinal);
        Assert.Equal(SeedContent, File.ReadAllText(SavedPath(ProbeSaveFixtures.FixedName)));
        Assert.Equal([ProbeSaveFixtures.FixedName], ProbeSaveFixtures.SavedNames(_workspace));
    }

    [Then(@"^gpuPresent is true, driverVersion equals the nvidia-smi driver version and energyCounterSupported is a boolean$")]
    public async Task ThenTheLaptopProbeMatches()
    {
        if (GateClosed)
        {
            AssertGateClosedRecorded();
            return;
        }

        ProcessOutcome child = _child ?? throw new InvalidOperationException("The laptop probe did not run.");
        Assert.True(child.ExitCode == 0, "the laptop probe failed: " + child.Describe());

        // The file name is the timestamp of the run, which the real clock decides, so the single file of the empty workspace is the one saved.
        string name = Assert.Single(ProbeSaveFixtures.SavedNames(_workspace));
        Assert.Matches(@"^probe-\d{8}T\d{6}Z\.json$", name);
        string saved = await File.ReadAllTextAsync(ProbeSaveFixtures.FilePath(_workspace, name));
        using JsonDocument document = ParseOneObject(saved);
        JsonElement root = document.RootElement;
        Assert.Equal(JsonValueKind.True, root.GetProperty("gpuPresent").ValueKind);
        Assert.Equal(await ReadSmiDriverAsync(), root.GetProperty("driverVersion").GetString());
        JsonValueKind counter = root.GetProperty("energyCounterSupported").ValueKind;
        Assert.True(counter is JsonValueKind.True or JsonValueKind.False, "energyCounterSupported is not a boolean: " + counter);
        Assert.Equal(child.Stdout, saved);
    }

    [Then(@"^the story fails and the discrepancy is recorded in docs/build-log\.md$")]
    public void ThenAMismatchFailsTheStory()
    {
        if (GateClosed)
        {
            AssertGateClosedRecorded();
            return;
        }

        string committed = _committedDriver ?? throw new InvalidOperationException("The comparison did not run.");
        string smi = _smiDriver ?? throw new InvalidOperationException("nvidia-smi did not run.");
        Assert.True(
            string.Equals(committed, smi, StringComparison.Ordinal),
            $"driver mismatch: {Path.GetFileName(_committedProbePath)} has driverVersion \"{committed}\" but nvidia-smi reports \"{smi}\"; "
            + "the story fails until the discrepancy is recorded in docs/build-log.md and the probe file is saved again");
    }
}