using System.Text.Json;

using AgentJoules.Cli;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-132 scenario (the README sections the Devpost form requires, REQ-025), handed to the step class by Reqnroll context injection. The texts are in memory; the committed README, the
/// endpoints document and the results are only read. The scenario without a cloudgpu run is modelled in a <see cref="TempWorkspace"/> that holds fixture run files of the other three tiers, so the real
/// <c>report --update-readme</c> writes the <c>cloudgpu | not run</c> row; <c>validate --config</c> runs through the harness over copies of the committed configuration and endpoints document.
/// </summary>
public sealed class ReadmeDevpostContext : IDisposable
{
    private TempWorkspace? _workspace;

    /// <summary>The README text under test, possibly a mutated copy of the committed one.</summary>
    public string Readme { get; set; } = string.Empty;

    /// <summary>The failures of the last check.</summary>
    public IReadOnlyList<string> Failures { get; set; } = [];

    /// <summary>The model IDs the README names, for the model-ID scenario.</summary>
    public IReadOnlyList<string> NamedIds { get; set; } = [];

    /// <summary>The <c>environment.localRuntime</c> of the latest local results file.</summary>
    public string LocalRuntime { get; private set; } = string.Empty;

    /// <summary>The model IDs of <c>environment.models</c> of the latest local results file.</summary>
    public IReadOnlyList<string> LocalModelIds { get; private set; } = [];

    /// <summary>The names of the latest results files of every tier (selected like the report does) whose <c>run.selfJudged</c> is true.</summary>
    public IReadOnlyList<string> SelfJudgedFiles { get; private set; } = [];

    /// <summary>Whether a completed or partial cloudgpu run file exists in the results the scenario looks at.</summary>
    public bool CloudGpuRan { get; set; }

    /// <summary>The result of <c>validate --config</c>, or null before it ran.</summary>
    public CliRunResult? ValidateResult { get; private set; }

    /// <summary>Reads the latest local results file of the repository (selected like the report does) and keeps what the scenarios compare with.</summary>
    public void ReadLatestLocalFile()
    {
        string path = RunFileSelector.Latest(RepositoryRoot.Resolve("results"), Tier.Local) ?? throw new InvalidOperationException("The repository holds no completed or partial local results file.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement environment = document.RootElement.GetProperty("environment");
        LocalRuntime = environment.GetProperty("localRuntime").GetString() ?? string.Empty;
        LocalModelIds = [.. environment.GetProperty("models").EnumerateArray().Select(static model => model.GetProperty("modelId").GetString() ?? string.Empty).Distinct(StringComparer.Ordinal)];
        var selfJudged = new List<string>();
        foreach (Tier tier in Enum.GetValues<Tier>())
        {
            if (RunFileSelector.Latest(RepositoryRoot.Resolve("results"), tier) is { } latest)
            {
                using JsonDocument file = JsonDocument.Parse(File.ReadAllText(latest));
                if (file.RootElement.GetProperty("run").GetProperty("selfJudged").GetBoolean())
                {
                    selfJudged.Add(Path.GetFileName(latest));
                }
            }
        }

        SelfJudgedFiles = selfJudged;
    }

    /// <summary>The <c>Judge:ModelId</c> of the committed configuration.</summary>
    /// <returns>The model ID.</returns>
    public static string JudgeModelId()
    {
        using JsonDocument document = JsonDocument.Parse(RepositoryRoot.ReadAllText("config/appsettings.json"));

        return document.RootElement.GetProperty("AgentJoules").GetProperty("Judge").GetProperty("ModelId").GetString() ?? string.Empty;
    }

    /// <summary>Whether a completed or partial cloudgpu run file exists in the committed <c>results</c> folder, decided as in AJ-141.</summary>
    /// <returns>True when a selectable cloudgpu file exists.</returns>
    public static bool CommittedCloudGpuRan() =>
        DocumentChecks.CloudGpuRan(Directory.GetFiles(RepositoryRoot.Resolve("results"), "*Z-cloudgpu.json"), File.ReadAllText);

    /// <summary>Lays out the workspace without a cloudgpu run file: fixture run files of local, nvidia and nebius, and the given README with the result markers.</summary>
    /// <param name="readme">The README text.</param>
    public void PrepareWorkspaceWithoutCloudGpu(string readme)
    {
        _workspace = TempWorkspace.CreateEmpty();
        ReadmeFixtures.AddCommitted(_workspace, Tier.Local, Tier.Nvidia, Tier.Nebius);
        _ = _workspace.WriteFile("README.md", ReadmeFixtures.WithMarkers(readme));
        CloudGpuRan = DocumentChecks.CloudGpuRan(Directory.GetFiles(Path.Combine(_workspace.Root, "results")), File.ReadAllText);
    }

    /// <summary>Runs <c>report --update-readme</c> in the workspace and reads the README back into <see cref="Readme"/>.</summary>
    /// <returns>A task that completes with the exit code.</returns>
    public async Task<int> UpdateWorkspaceReadmeAsync()
    {
        TempWorkspace workspace = _workspace ?? throw new InvalidOperationException("No workspace was prepared.");
        CliRunResult result = await CliHarness.RunTokensAsync(["report", "--update-readme"], new CliHostOverrides(WorkingDirectory: workspace.Root));
        Readme = File.ReadAllText(Path.Combine(workspace.Root, "README.md"));

        return result.ExitCode;
    }

    /// <summary>Runs <c>validate --config</c> over copies of the committed configuration and endpoints document; the check is file-only.</summary>
    /// <returns>A task that completes when <see cref="ValidateResult"/> is set.</returns>
    public async Task ValidateConfigAsync()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        _ = workspace.WriteFile(Path.Combine("docs", "endpoints.md"), RepositoryRoot.ReadAllText("docs/endpoints.md"));
        ValidateResult = await CliHarness.RunTokensAsync(["validate", "--config"], new CliHostOverrides(WorkingDirectory: workspace.Root));
    }

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}