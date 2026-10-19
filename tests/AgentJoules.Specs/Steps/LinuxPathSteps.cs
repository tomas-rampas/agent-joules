using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AgentJoules.Cli;
using AgentJoules.Core.Gpu;
using AgentJoules.Nvml;
using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-036: the Linux and WSL2 library path and the no-GPU container path. Self-contained: its state lives here, not in ScenarioWorld. The class is
/// scoped to the feature because "I run ..." is a sentence of every CLI story. The two unit scenarios use a ScriptedNvmlApi in missing-library mode
/// (the exception the production resolver throws when no candidate loads) behind the real factory on the Linux platform for the probe scenario; the
/// factory scenario opens the running platform too, which names nvml.dll on Windows, so the "libnvidia-ml.so.1" of the scenario text is asserted
/// against the Linux factory. The four @integration scenarios follow the AJ-023 pattern: the Given step evaluates IntegrationGate.SkipReason for
/// AGENTJOULES_INTEGRATION (read, never set) and, with the gate closed, the remaining steps only record "gate closed" and pass without starting a
/// process. With the gate open they run the real docker and wsl.exe checks with bounded waits and read the "Probe and WSL2 notes" section of
/// docs/build-log.md as text. Every child process loses the three API key variables; HOME, USERPROFILE and APPDATA stay because docker needs its
/// context and credential store.
/// </summary>
/// <remarks>
/// Record formats the open-gate scenarios require in the notes section (lines of that section, nothing else is read):
/// <list type="bullet">
/// <item>A stopped daemon: a line that starts with an ISO date (yyyy-mm-dd) and contains <c>blocked: Docker daemon not running</c>.</item>
/// <item>A running daemon: a line that contains both <c>docker run</c> and <c>gpuPresent</c> (the container-run record).</item>
/// <item>A missing image tag: the sentence "no replacement tag needed" must be absent, and a line of its own reads
/// <c>replacement tag: mcr.microsoft.com/dotnet/sdk:&lt;tag&gt;</c> with a tag other than 10.0; the container scenario then runs that tag.</item>
/// <item>WSL2 without a 10.0.4xx SDK (401 or later, global.json pins 10.0.401 with latestPatch): <c>WSL2 probe not run: &lt;reason&gt;</c>;
/// with such an SDK: <c>WSL2 probe output:</c>.</item>
/// </list>
/// The container scenario runs the EDGE-001 command the notes record plus <c>--name agentjoules-aj036-&lt;guid&gt;</c>, with a direct-invocation mount
/// (<c>-v &lt;repo root&gt;:/src</c> with the Windows path) equivalent to the recorded <c>$(pwd -W)</c> form, so a run that
/// times out can remove its container with <c>docker rm --force</c> (killing docker.exe alone leaves it writing into the mounted folder). The image
/// is pulled by its own bounded <c>docker pull --quiet</c> beforehand, so the pull progress never reaches the stderr of the pinned command. An
/// open-gate run rewrites src/*/obj and bin with Linux artifacts, so the host needs <c>dotnet restore</c> and a build afterwards (design R-10).
/// </remarks>
[Binding]
[Scope(Feature = "Load NVML on Linux and WSL2 and prove the no-GPU container path")]
public sealed partial class LinuxPathSteps
{
    private const string ClosedGateReason = "Set AGENTJOULES_INTEGRATION=1";
    private const string DefaultImageTag = "mcr.microsoft.com/dotnet/sdk:10.0";
    private const string NotesHeading = "\n## Probe and WSL2 notes\n";
    private const int MinimumSdkPatch = 401;

    private static readonly TimeSpan DockerInfoLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ManifestLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PullLimit = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ContainerLimit = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RemoveLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WslLimit = TimeSpan.FromMinutes(2);

    private readonly IReqnrollOutputHelper _output;
    private readonly ScriptedNvmlApi _api = new(new FakeTimeProvider());
    private CliRunResult? _probeRun;
    private GpuOpenResult? _runningPlatformResult;
    private GpuOpenResult? _linuxResult;
    private Exception? _openFailure;
    private string? _gateSkipReason;
    private bool _gateEvaluated;
    private string? _preconditionNote;
    private string _imageTag = DefaultImageTag;
    private ProcessOutcome? _dockerInfo;
    private ProcessOutcome? _manifest;
    private ProcessOutcome? _container;
    private ProcessOutcome? _sdkList;

    /// <summary>Creates the steps for one scenario.</summary>
    /// <param name="output">Receives the notes of a scenario that passes without running its command.</param>
    public LinuxPathSteps(IReqnrollOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    private sealed record ProcessOutcome(int ExitCode, string Stdout, string Stderr, bool TimedOut, string? StartFailure)
    {
        public string Describe() =>
            $"exit {ExitCode}, timed out {TimedOut}, start failure \"{StartFailure}\", stdout \"{Excerpt(Stdout)}\", stderr \"{Excerpt(Stderr)}\"";
    }

    private bool GateClosed => _gateEvaluated && _gateSkipReason is not null;

    private bool GateOpen => _gateEvaluated && _gateSkipReason is null;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}.*blocked: Docker daemon not running", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BlockedRecord();

    [GeneratedRegex(@"^replacement tag: (mcr\.microsoft\.com/dotnet/sdk:(?!10\.0(?![\w.-]))[\w.-]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReplacementTagRecord();

    [GeneratedRegex(@"WSL2 probe not run: [^<\s]", RegexOptions.CultureInvariant)]
    private static partial Regex WslNotRunRecord();

    [GeneratedRegex(@"^10\.0\.(4\d\d)(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex RequiredSdkLine();

    private static string Excerpt(string text) => text.Length <= 1500 ? text : text[..1500] + "...";

    private static string ReadBuildLog() =>
        File.ReadAllText(Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "build-log.md"));

    // The text of the "Probe and WSL2 notes" section: from its heading line to the next level-2 heading.
    private static string ReadNotesSection()
    {
        string log = ReadBuildLog();
        int heading = log.IndexOf(NotesHeading, StringComparison.Ordinal);
        Assert.True(heading >= 0, "docs/build-log.md has no \"## Probe and WSL2 notes\" section");
        int start = heading + 1;
        int end = log.IndexOf("\n## ", heading + NotesHeading.Length, StringComparison.Ordinal);
        return end < 0 ? log[start..] : log[start..end];
    }

    // The tag the notes record as the verified replacement; fails unless the anchored record line exists.
    private static string ReadReplacementTag()
    {
        string notes = ReadNotesSection();
        Match record = ReplacementTagRecord().Match(notes);
        Assert.True(
            record.Success,
            "the notes section has no line \"replacement tag: mcr.microsoft.com/dotnet/sdk:<tag>\" with a tag other than 10.0");
        return record.Groups[1].Value;
    }

    private static ProcessStartInfo NewStartInfo(string fileName, IEnumerable<string> arguments, string? workingDirectory)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            info.WorkingDirectory = workingDirectory;
        }

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (string variable in CliChildProcess.KeyVariables)
        {
            info.Environment.Remove(variable);
        }

        return info;
    }

    private static async Task<ProcessOutcome> RunProcessAsync(string fileName, IEnumerable<string> arguments, TimeSpan limit, string? workingDirectory = null)
    {
        using var timeout = new CancellationTokenSource(limit);
        using var process = new Process { StartInfo = NewStartInfo(fileName, arguments, workingDirectory) };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // A missing executable (no docker, no wsl.exe on this host) counts as a non-zero exit of that command.
            return new ProcessOutcome(-1, string.Empty, string.Empty, false, ex.Message);
        }

        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdout.WaitAsync(timeout.Token);
            string error = await stderr.WaitAsync(timeout.Token);
            return new ProcessOutcome(process.ExitCode, output, error, false, null);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new ProcessOutcome(-1, string.Empty, string.Empty, true, null);
        }
    }

    private static async Task<ProcessOutcome> RunWslAsync(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ProcessOutcome(-1, string.Empty, string.Empty, false, "wsl.exe exists on Windows hosts only");
        }

        // The full path under the system directory, so a wsl.exe earlier on PATH is never started. The temp directory is the working directory
        // so the repository's global.json cannot influence the listing.
        string wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        return await RunProcessAsync(wsl, ["-e", "bash", "-lc", command], WslLimit, Path.GetTempPath());
    }

    private static JsonDocument ParseOneObject(string stdout)
    {
        JsonDocument document = JsonDocument.Parse(stdout);
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
    }

    private CliHostOverrides LinuxOverrides() =>
        ScriptedGpuOverrides.Create(_api) with
        {
            Services = services =>
            {
                services.RemoveAll<IGpuTelemetrySourceFactory>();
                services.AddSingleton<IGpuTelemetrySourceFactory>(new NvmlGpuTelemetrySourceFactory(_api, OSPlatform.Linux));
            },
        };

    [Given(@"^the Linux loader is faked as missing$")]
    public void GivenTheLinuxLoaderIsMissing()
    {
        _api.MissingLibrary = true;
    }

    [Given(@"^the native library cannot be found$")]
    public void GivenTheNativeLibraryCannotBeFound()
    {
        _api.MissingLibrary = true;
    }

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments)
    {
        _probeRun = await CliHarness.RunAsync(arguments, LinuxOverrides());
    }

    [When(@"^the factory opens device 0$")]
    public void WhenTheFactoryOpensDevice0()
    {
        // The running platform names its own first candidate; the Linux platform is opened as well, so the scenario's soname is asserted on every host.
        _openFailure = Record.Exception(() =>
        {
            _runningPlatformResult = new NvmlGpuTelemetrySourceFactory(_api).Open(0);
            _linuxResult = new NvmlGpuTelemetrySourceFactory(_api, OSPlatform.Linux).Open(0);
        });
    }

    [Then(@"^gpuPresent is false and the exit code is 0$")]
    public void ThenGpuPresentIsFalseAndTheExitCodeIs0()
    {
        CliRunResult run = _probeRun ?? throw new InvalidOperationException("The probe was not run.");
        Assert.Equal(0, run.ExitCode);
        using JsonDocument document = ParseOneObject(run.Stdout);
        Assert.Equal(JsonValueKind.False, document.RootElement.GetProperty("gpuPresent").ValueKind);
    }

    [Then(@"^the result is Unavailable naming ""([^""]*)"" and no exception escapes$")]
    public void ThenTheResultIsUnavailableNaming(string library)
    {
        Assert.Null(_openFailure);

        GpuOpenResult linux = _linuxResult ?? throw new InvalidOperationException("The Linux factory did not open.");
        Assert.False(linux.IsAvailable);
        Assert.Contains(library, linux.Reason, StringComparison.Ordinal);

        // On Windows the running platform names nvml.dll, on Linux the first Linux candidate.
        GpuOpenResult running = _runningPlatformResult ?? throw new InvalidOperationException("The running-platform factory did not open.");
        Assert.False(running.IsAvailable);
        OSPlatform platform = OperatingSystem.IsWindows() ? OSPlatform.Windows : OSPlatform.Linux;
        Assert.Contains(NvmlLibraryCandidates.For(platform, null)[0], running.Reason, StringComparison.Ordinal);
        Assert.Equal(0, _api.ShutdownCallCount);
    }

    [Given(@"^Docker Desktop is running and the sdk:10\.0 image tag exists$")]
    public async Task GivenDockerIsRunningAndTheImageTagExists()
    {
        EvaluateGate();
        if (GateClosed)
        {
            return;
        }

        _dockerInfo = await RunProcessAsync("docker", ["info"], DockerInfoLimit);
        if (_dockerInfo.ExitCode != 0)
        {
            _preconditionNote = "docker info exits non-zero; the blocked record is checked by the stopped-daemon scenario";
            _output.WriteLine(_preconditionNote);
            return;
        }

        _manifest = await RunProcessAsync("docker", ["manifest", "inspect", DefaultImageTag], ManifestLimit);
        if (_manifest.ExitCode != 0)
        {
            // The tag was not found: the notes must record the verified replacement, and the container runs that tag.
            _imageTag = ReadReplacementTag();
        }

        // Its own bounded process, so a host without the image cached does not put pull progress on the stderr of the pinned command.
        ProcessOutcome pull = await RunProcessAsync("docker", ["pull", "--quiet", _imageTag], PullLimit);
        _output.WriteLine($"docker pull --quiet {_imageTag}: exit {pull.ExitCode}");
        Assert.True(pull.ExitCode == 0, "docker pull failed: " + pull.Describe());
    }

    [When(@"^the EDGE-001 docker command runs without --gpus$")]
    public async Task WhenTheContainerCommandRuns()
    {
        if (!GateOpen || _preconditionNote is not null)
        {
            return;
        }

        string root = ConfigurationFixtures.RepositoryRoot();
        Assert.True(
            File.Exists(Path.Combine(root, "src", "AgentJoules.Cli", "AgentJoules.Cli.csproj")),
            "the repository root " + root + " does not hold src/AgentJoules.Cli/AgentJoules.Cli.csproj, so it is not mounted");

        // The exact EDGE-001 command plus --name. The Windows path is mounted by a direct docker invocation, so Git Bash path rewriting never applies. No --gpus flag.
        string name = "agentjoules-aj036-" + Guid.NewGuid().ToString("N");
        string[] arguments =
        [
            "run", "--rm",
            "--name", name,
            "-e", "DOTNET_NOLOGO=1",
            "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "-v", root + ":/src",
            "-w", "/src",
            _imageTag,
            "dotnet", "run", "--project", "src/AgentJoules.Cli", "--", "probe", "--json",
        ];
        _container = await RunProcessAsync("docker", arguments, ContainerLimit);
        if (_container.TimedOut)
        {
            // Killing docker.exe leaves the container running and writing into the mounted folder.
            _ = await RunProcessAsync("docker", ["rm", "--force", name], RemoveLimit);
        }
    }

    [Then(@"^it exits 0 and stdout parses as JSON with gpuPresent false$")]
    public void ThenItExits0AndStdoutParsesAsJson()
    {
        if (GateClosed)
        {
            AssertGateClosedRecorded();
            return;
        }

        if (_preconditionNote is not null)
        {
            return;
        }

        ProcessOutcome container = _container ?? throw new InvalidOperationException("The container command did not run.");
        Assert.False(container.TimedOut, "the container command timed out: " + container.Describe());
        Assert.True(container.ExitCode == 0, "the container command failed: " + container.Describe());
        Assert.True(container.Stderr.Length == 0, "the container command wrote to stderr: " + container.Describe());
        using JsonDocument document = ParseOneObject(container.Stdout);
        JsonProperty[] properties = [.. document.RootElement.EnumerateObject()];
        Assert.Equal(11, properties.Length);
        foreach (JsonProperty property in properties)
        {
            Assert.Equal(property.Name == "gpuPresent" ? JsonValueKind.False : JsonValueKind.Null, property.Value.ValueKind);
        }
    }

    [Given(@"^""docker info"" exits non-zero$")]
    public async Task GivenDockerInfoExitsNonZero()
    {
        EvaluateGate();
        if (GateClosed)
        {
            return;
        }

        _dockerInfo = await RunProcessAsync("docker", ["info"], DockerInfoLimit);
    }

    [Given(@"^""docker manifest inspect mcr\.microsoft\.com/dotnet/sdk:10\.0"" fails$")]
    public async Task GivenManifestInspectFails()
    {
        EvaluateGate();
        if (GateClosed)
        {
            return;
        }

        _manifest = await RunProcessAsync("docker", ["manifest", "inspect", DefaultImageTag], ManifestLimit);
    }

    [Given(@"^the WSL2 distro has no dotnet command$")]
    public async Task GivenTheWslDistroHasNoDotnet()
    {
        EvaluateGate();
        if (GateClosed)
        {
            return;
        }

        _sdkList = await RunWslAsync("dotnet --list-sdks");
    }

    [When(@"^the container check is attempted$")]
    public static void WhenTheContainerCheckIsAttempted()
    {
        // The check is the pair of docker commands of the Given step; this build-log scenario only reads the record.
    }

    [When(@"^the WSL2 probe is attempted$")]
    public static void WhenTheWslProbeIsAttempted()
    {
        // The listing of the Given step decides whether the probe can run; the Then step reads the record.
    }

    [Then(@"^docs/build-log\.md records ""blocked: Docker daemon not running"" with the date$")]
    public void ThenTheBlockedRecordExists()
    {
        if (GateClosed)
        {
            AssertGateClosedRecorded();
            return;
        }

        ProcessOutcome info = _dockerInfo ?? throw new InvalidOperationException("docker info did not run.");
        string notes = ReadNotesSection();
        if (info.ExitCode == 0)
        {
            // The daemon is up, so no blocked record is required; the container-run record must exist instead.
            Assert.True(
                notes.Split('\n').Any(line => line.Contains("docker run", StringComparison.OrdinalIgnoreCase) && line.Contains("gpuPresent", StringComparison.Ordinal)),
                "docker info exits 0 and the notes section has no container-run record (a line naming \"docker run\" and \"gpuPresent\")");
            return;
        }

        Assert.True(
            BlockedRecord().IsMatch(notes),
            "docker info exits non-zero and the notes section has no line that starts with a date and contains \"blocked: Docker daemon not running\"");
    }

    [Then(@"^the verified replacement tag is recorded before the run$")]
    public void ThenTheReplacementTagIsRecorded()
    {
        if (GateClosed)
        {
            AssertGateClosedRecorded();
            return;
        }

        ProcessOutcome manifest = _manifest ?? throw new InvalidOperationException("docker manifest inspect did not run.");
        if (manifest.ExitCode == 0)
        {
            // The tag exists, so there is nothing to replace.
            return;
        }

        _ = ReadReplacementTag();
    }

    [Then(@"^docs/build-log\.md records ""not run"" with the reason$")]
    public void ThenTheNotRunRecordExists()
    {
        if (GateClosed)
        {
            AssertGateClosedRecorded();
            return;
        }

        ProcessOutcome sdks = _sdkList ?? throw new InvalidOperationException("The WSL2 SDK listing did not run.");
        string notes = ReadNotesSection();
        bool hasRequiredSdk = sdks.ExitCode == 0 && sdks.Stdout.Split('\n').Any(HasRequiredSdk);
        if (hasRequiredSdk)
        {
            // An SDK that satisfies global.json exists, so the probe output must be recorded instead.
            Assert.Contains("WSL2 probe output:", notes, StringComparison.Ordinal);
            return;
        }

        Assert.True(WslNotRunRecord().IsMatch(notes), "the notes section has no \"WSL2 probe not run: <reason>\" record with a real reason");
    }

    // True for a "dotnet --list-sdks" line whose release version is 10.0.401 or later in the 10.0.4xx band.
    private static bool HasRequiredSdk(string line)
    {
        Match match = RequiredSdkLine().Match(line.Trim());
        return match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) >= MinimumSdkPatch;
    }
}