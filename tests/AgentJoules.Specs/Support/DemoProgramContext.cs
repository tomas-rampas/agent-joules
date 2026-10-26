using AgentJoules.Cli;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-135 scenario (the demo program, REQ-027, REQ-028, REQ-032), handed to the step class by Reqnroll context injection. The script, the story file, the README, the build log and the set of
/// repository paths are in memory, so a scenario that deletes a file or saves a screenshot under another name changes the set, not the repository. Commands run in process through the harness: <c>validate results/</c>
/// with the repository root as the working directory (a command that reads only), and <c>report</c> in a <see cref="TempWorkspace"/> with fixture run files.
/// </summary>
public sealed class DemoProgramContext : IDisposable
{
    private TempWorkspace? _workspace;

    /// <summary>The script under test.</summary>
    public string Script { get; set; } = string.Empty;

    /// <summary>The text of the story file.</summary>
    public string Story { get; set; } = string.Empty;

    /// <summary>The failures of the last check.</summary>
    public IReadOnlyList<string> Failures { get; set; } = [];

    /// <summary>The repository paths the artifact check searches.</summary>
    public HashSet<string> Files { get; set; } = [];

    /// <summary>The bytes of the screenshot in the scenario's world, or null when it does not exist.</summary>
    public byte[]? ScreenshotBytes { get; set; }

    /// <summary>Whether the scenario is about the screenshot.</summary>
    public bool ScreenshotScenario { get; set; }

    /// <summary>The outcome of the screenshot check, or null before it ran.</summary>
    public ScreenshotStatus? Screenshot { get; set; }

    /// <summary>The build log of the width scenarios, or null.</summary>
    public string? BuildLog { get; set; }

    /// <summary>The run files of the shape scenarios by file name.</summary>
    public Dictionary<string, string> RunFiles { get; set; } = [];

    /// <summary>A second text of the scenario, for example the <c>.gitignore</c>.</summary>
    public string Other { get; set; } = string.Empty;

    /// <summary>The README text, for the scenarios that compare it with the script.</summary>
    public string Readme { get; set; } = string.Empty;

    /// <summary>A count the scenario made, for example the script tags of the report page.</summary>
    public int Count { get; set; }

    /// <summary>The names read from the task files.</summary>
    public IReadOnlyList<string> TaskNames { get; set; } = [];

    /// <summary>The result of the last command, or null.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The text of <c>docs/report/summary.md</c> in the report workspace, or null.</summary>
    public string? Summary { get; private set; }

    /// <summary>Runs <c>report --in results/ --out docs/report/</c> in a workspace that holds the fixture run files of three tiers and reads the summary.</summary>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunReportWithoutCloudGpuAsync()
    {
        _workspace = TempWorkspace.CreateEmpty();
        ReadmeFixtures.AddCommitted(_workspace, Tier.Local, Tier.Nvidia, Tier.Nebius);
        Result = await CliHarness.RunTokensAsync(["report", "--in", "results/", "--out", "docs/report/"], new CliHostOverrides(WorkingDirectory: _workspace.Root));
        string summary = Path.Combine(_workspace.Root, "docs", "report", "summary.md");
        Summary = File.Exists(summary) ? File.ReadAllText(summary) : null;
    }

    /// <summary>Runs <c>validate results/</c> with the repository root as the working directory; the command reads the files and writes none.</summary>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task ValidateCommittedResultsAsync() =>
        Result = await CliHarness.RunTokensAsync(["validate", "results/"], new CliHostOverrides(WorkingDirectory: RepositoryRoot.Path));

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}