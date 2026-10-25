using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-114 scenario (run the laptop measurement campaign, REQ-018, REQ-020, REQ-021), handed to the step class by Reqnroll context injection. The scenarios follow the AJ-110 pattern:
/// <see cref="Open"/> evaluates <see cref="IntegrationGate.SkipReason"/> for AGENTJOULES_INTEGRATION; with the gate closed the remaining steps record <see cref="Note"/> and pass. With it open they read the newest
/// committed <c>-local.json</c>, <c>-nvidia.json</c> and <c>-nebius.json</c> run files of <c>results/</c> through <see cref="LaptopCampaign"/> and assert them; the campaign itself is never run (the orchestrator's
/// <c>run --tier all --repeat 3</c> is the run, and its exit marker is not available to a test), no key is read and nothing is written. A tier in <see cref="LaptopCampaign.PendingTiers"/> without a file is a note.
/// The scenario about a missing nebius key is hermetic and always asserts: the real <c>run</c> command runs through the harness with an in-memory configuration, deny-all HTTP and <c>--out</c> a fresh temporary
/// folder, so the exit code and the absence of a run file are checked on every run and nothing leaves the process.
/// </summary>
public sealed class LaptopCampaignContext : IDisposable
{
    private readonly string _out = Directory.CreateTempSubdirectory("aj114-").FullName;

    private string? ClosedReason { get; set; }

    private string? RecordedNote { get; set; }

    /// <summary>True when the gate is open and the precondition of the scenario holds: the scenario asserts the committed files.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The note of a scenario that passed without asserting, or null.</summary>
    public string? Note => ClosedReason ?? RecordedNote;

    /// <summary>The command line of the last When step that named one, or null.</summary>
    public string? LastCommand { get; set; }

    /// <summary>The result of the hermetic command, or null when none ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The text of standard output and standard error of the hermetic command.</summary>
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

    /// <summary>Runs <paramref name="arguments"/> hermetically: the fake judge key is the only key, the configuration is in memory, every HTTP request is refused and <c>--out</c> is the temporary folder.</summary>
    /// <param name="arguments">The command line without <c>--out</c>.</param>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunWithoutNebiusKeyAsync(string arguments)
    {
        RunCommandSetup setup = RunCommandSetup.Create([new KeyValuePair<string, string?>("NVIDIA_API_KEY", ProviderClientFixtures.FakeKey())]);
        Xunit.Assert.False(setup.Settings.ContainsKey("NEBIUS_API_KEY"));
        Result = await RunCommandHarness.RunAsync($"{arguments} --out \"{_out}\"", setup).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>Every file in the temporary output folder of the hermetic command.</summary>
    /// <returns>The full paths; empty when nothing was written.</returns>
    public string[] WrittenFiles() => [.. Directory.EnumerateFileSystemEntries(_out)];

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }
}