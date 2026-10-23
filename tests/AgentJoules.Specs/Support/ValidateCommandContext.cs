using System.Text.Json.Nodes;

using AgentJoules.Cli;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-080 scenario (validate run files with <c>AJ validate</c>), handed to the step class by Reqnroll context injection. The Given steps write run files into a
/// <see cref="TempWorkspace"/> that has no <c>config</c> and no <c>docs</c> folder (the repository is never written); the When steps run the real command line through the harness with that workspace as
/// the working directory; the Then steps read the exit code and the two streams. There is no HTTP, no model and no GPU: <c>validate</c> never builds a host.
/// </summary>
public sealed class ValidateCommandContext : IDisposable
{
    /// <summary>The folder of the scenario, relative to the working directory.</summary>
    public const string Folder = "runs";

    private const string ProbeFileName = "probe-20261019T205344Z.json";

    private TempWorkspace? _workspace;

    /// <summary>The name of the run file the scenario corrupted or filled with text, or null.</summary>
    public string? FileName { get; private set; }

    /// <summary>The path the scenario names instead of the folder, or null.</summary>
    public string? MissingPath { get; private set; }

    /// <summary>The result of the run, or null before it ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The workspace root, the working directory of the run.</summary>
    public string Root => Workspace.Root;

    private TempWorkspace Workspace => _workspace ??= TempWorkspace.CreateEmpty();

    /// <summary>Creates the empty working directory.</summary>
    public void CreateEmptyWorkingDirectory() => _ = Workspace;

    /// <summary>Writes the committed fixture run files and, when asked, a probe file next to them.</summary>
    /// <param name="runFiles">How many run files to write; at most three, one per tier.</param>
    /// <param name="probeFiles">How many probe files to write; at most one.</param>
    public void WriteFiles(int runFiles, int probeFiles)
    {
        Tier[] tiers = [Tier.Local, Tier.Nvidia, Tier.Nebius];
        foreach (Tier tier in tiers.Take(runFiles))
        {
            _ = Workspace.WriteFile(Path.Combine(Folder, RunDocumentFixtures.CommittedFileName(tier)), RunDocumentFixtures.CommittedText(tier));
        }

        if (probeFiles > 0)
        {
            _ = Workspace.WriteFile(Path.Combine(Folder, ProbeFileName), "{}\n");
        }
    }

    /// <summary>Writes one run file whose first step has a cost with the given label and value.</summary>
    /// <param name="label">The label to put in <c>cost.label</c>.</param>
    /// <param name="value">The number to put in <c>cost.value</c>.</param>
    public void WriteRunFileWithCost(string label, double value)
    {
        string text = RunDocumentFixtures.Edit(
            RunDocumentFixtures.CommittedText(Tier.Local),
            root =>
            {
                JsonObject cost = root["steps"]![0]!["cost"]!.AsObject();
                cost["label"] = label;
                cost["value"] = value;
            });
        FileName = RunDocumentFixtures.CommittedFileName(Tier.Local);
        _ = Workspace.WriteFile(Path.Combine(Folder, FileName), text);
    }

    /// <summary>Writes one run file that holds <paramref name="text"/>.</summary>
    /// <param name="text">The content of the file.</param>
    public void WriteRunFileWithText(string text)
    {
        FileName = RunDocumentFixtures.CommittedFileName(Tier.Local);
        _ = Workspace.WriteFile(Path.Combine(Folder, FileName), text);
    }

    /// <summary>Names a path that is not there.</summary>
    public void UseMissingPath() => MissingPath = "no-such-folder";

    /// <summary>Runs <c>validate</c> on the folder of the scenario, or on the missing path when a Given step named one.</summary>
    /// <returns>A task that completes when <see cref="Result"/> is set.</returns>
    public async Task RunAsync() =>
        Result = await CliHarness.RunTokensAsync(["validate", MissingPath ?? Folder], new CliHostOverrides(WorkingDirectory: Workspace.Root));

    /// <summary>The result of the run.</summary>
    /// <exception cref="XunitException">The command has not run.</exception>
    public CliRunResult Required() => Result ?? throw new XunitException("The command has not run yet.");

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}