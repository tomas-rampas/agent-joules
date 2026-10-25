using System.Text.Json;

using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-111 scenario (run the calibration on the laptop, REQ-021), handed to the step class by Reqnroll context injection. The scenarios follow the AJ-101 pattern: <see cref="Open"/> evaluates
/// <see cref="IntegrationGate.SkipReason"/> for AGENTJOULES_INTEGRATION; with the gate closed the remaining steps record <see cref="Note"/> and pass, and with it open they assert the newest committed
/// <c>results/calibration-*.json</c> and never perform the builder's action (no calibration runs, nothing is written). The scenario about a stopped Ollama is hermetic and needs no gate: the real <c>calibrate</c>
/// command runs through the harness with <c>Local:Runtime</c> <c>ollama</c> against a loopback-style fake that refuses the connection (the AJ-107 pattern), with <c>--out</c> a fresh temporary folder, so the exit
/// code, the fix command and the absence of a file are checked on every run; nothing leaves the process.
/// </summary>
public sealed class CalibrationLiveContext : IDisposable
{
    private readonly string _out = Directory.CreateTempSubdirectory("aj111-").FullName;

    private string? ClosedReason { get; set; }

    private string? RecordedNote { get; set; }

    /// <summary>True when the gate is open and the precondition of the scenario holds: the scenario asserts the committed file.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The note of a scenario that passed without asserting, or null.</summary>
    public string? Note => ClosedReason ?? RecordedNote;

    /// <summary>The fake that stands where Ollama stands in the failure scenario, or null in the other scenarios.</summary>
    public FakeHttpMessageHandler? Endpoint { get; set; }

    /// <summary>The result of the last command, or null when no command ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The text of standard output and standard error of the last command.</summary>
    /// <exception cref="XunitException">No command ran.</exception>
    public string Output => (Result ?? throw new XunitException("The command has not run yet.")).Stdout + "\n" + Result.Stderr;

    /// <summary>Evaluates the gate.</summary>
    public void Open()
    {
        ClosedReason = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        IsActive = ClosedReason is null;
    }

    /// <summary>Records why an open-gate scenario passed without asserting.</summary>
    /// <param name="note">The reason.</param>
    public void Skip(string note)
    {
        RecordedNote = note;
        IsActive = false;
    }

    /// <summary>Runs <paramref name="arguments"/> through the real command against <see cref="Endpoint"/> with <c>Local:Runtime</c> <c>ollama</c> and <c>--out</c> the temporary folder.</summary>
    /// <param name="arguments">The command line without <c>--out</c>.</param>
    /// <returns>A task that completes when the command ended.</returns>
    /// <exception cref="XunitException">No Given step prepared the endpoint.</exception>
    public async Task RunAsync(string arguments)
    {
        FakeHttpMessageHandler endpoint = Endpoint ?? throw new XunitException("No Given step prepared the Ollama fake.");
        RunCommandSetup setup = OllamaFixtures.Setup(endpoint, runtime: "ollama");
        Result = await RunCommandHarness.RunAsync($"{arguments} --out \"{_out}\"", setup).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>The calibration files in the temporary output folder.</summary>
    /// <returns>The full paths; empty when there are none.</returns>
    public string[] WrittenCalibrationFiles() => [.. Directory.EnumerateFiles(_out, "calibration-*")];

    /// <summary>Reads the newest committed calibration file; the caller disposes the document.</summary>
    /// <returns>The document.</returns>
    /// <exception cref="XunitException">The results folder holds no calibration file.</exception>
    public static JsonDocument ReadNewestCalibration() =>
        JsonDocument.Parse(File.ReadAllText(LaptopResults.NewestCalibration() ?? throw new XunitException(LaptopResults.NoFileMessage("results/calibration-<yyyyMMddTHHmmssZ>.json", "AJ-111-T2"))));

    /// <summary>Reads the newest committed laptop probe file; the caller disposes the document.</summary>
    /// <returns>The document.</returns>
    /// <exception cref="XunitException">The results folder holds no probe file.</exception>
    public static JsonDocument ReadNewestProbe() =>
        JsonDocument.Parse(File.ReadAllText(LaptopResults.NewestProbe() ?? throw new XunitException(LaptopResults.NoFileMessage("results/probe-<yyyyMMddTHHmmssZ>.json", "AJ-110-T3"))));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }
}