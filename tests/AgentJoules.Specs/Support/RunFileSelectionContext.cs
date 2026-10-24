using System.Text.Json.Nodes;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-081 scenario (the selection of the run files a report may use), handed to the step class by Reqnroll context injection. The Given steps write run files into a
/// <see cref="TempWorkspace"/> folder, each the committed fixture document of its tier with the status and the flags of the scenario (the repository is never written, the committed files are never copied);
/// the When steps run the real <see cref="RunFileSelector"/> on that folder; the Then steps read the path it returned. There is no HTTP, no model and no GPU.
/// </summary>
public sealed class RunFileSelectionContext : IDisposable
{
    private const string FolderName = "runs";

    private TempWorkspace? _workspace;

    /// <summary>The name of the file the Given step wrote as the newest candidate, or null.</summary>
    public string? NewestName { get; private set; }

    /// <summary>The name of the older file the Given step wrote, or null.</summary>
    public string? OlderName { get; private set; }

    /// <summary>The name of the baseline file the Given step wrote, or null.</summary>
    public string? BaselineName { get; private set; }

    /// <summary>The path <see cref="RunFileSelector.Latest"/> returned, or null when it returned no file or has not run.</summary>
    public string? Latest { get; private set; }

    /// <summary>The path <see cref="RunFileSelector.LatestBaseline"/> returned, or null when it returned no file or has not run.</summary>
    public string? LatestBaseline { get; private set; }

    /// <summary>What <see cref="RunFileSelector.Select"/> made of the folder, or null before a When step.</summary>
    public RunFileSelection? Selection { get; private set; }

    private TempWorkspace Workspace => _workspace ??= TempWorkspace.CreateEmpty();

    private string Folder => Path.Combine(Workspace.Root, FolderName);

    /// <summary>Writes a run file named for <paramref name="stamp"/> and <paramref name="tier"/>, holding the committed fixture document of that tier with the given status and flags.</summary>
    /// <param name="stamp">The timestamp of the name, <c>yyyyMMddTHHmmssZ</c>.</param>
    /// <param name="tier">The local, nvidia or nebius tier.</param>
    /// <param name="status">The <c>run.status</c> text.</param>
    /// <param name="flags">The <c>run.flags</c>.</param>
    /// <param name="baseline">Whether the name carries the <c>-baseline</c> suffix.</param>
    /// <returns>The file name.</returns>
    public string WriteRunFile(string stamp, Tier tier, string status, string[] flags, bool baseline = false)
    {
        string name = $"{stamp}-{TierOptions.NameOf(tier)}{(baseline ? "-baseline" : string.Empty)}.json";
        string text = RunDocumentFixtures.Edit(
            RunDocumentFixtures.CommittedText(tier),
            root =>
            {
                JsonObject run = root["run"]!.AsObject();
                run["status"] = status;
                run["flags"] = new JsonArray([.. flags.Select(static flag => (JsonNode?)JsonValue.Create(flag))]);
            });
        _ = Workspace.WriteFile(Path.Combine(FolderName, name), text);
        return name;
    }

    /// <summary>Writes a probe or calibration file whose content carries a header that would be accepted, so only its name keeps it out.</summary>
    /// <param name="fileName">The name, for example <c>probe-20261019T205344Z.json</c>.</param>
    public void WriteNonRunFile(string fileName)
    {
        string text = RunDocumentFixtures.Edit(RunDocumentFixtures.CommittedText(Tier.Local), static _ => { });
        _ = Workspace.WriteFile(Path.Combine(FolderName, fileName), text);
    }

    /// <summary>Remembers the names a Given step wrote.</summary>
    /// <param name="newest">The newest candidate.</param>
    /// <param name="older">The older candidate, or null.</param>
    /// <param name="baseline">The baseline candidate, or null.</param>
    public void Remember(string? newest, string? older = null, string? baseline = null)
    {
        NewestName = newest;
        OlderName = older;
        BaselineName = baseline;
    }

    /// <summary>Selects the latest file of the tier and the whole folder.</summary>
    /// <param name="tier">The tier.</param>
    public void SelectLatest(Tier tier)
    {
        Latest = RunFileSelector.Latest(Folder, tier);
        Selection = RunFileSelector.Select([Folder]);
    }

    /// <summary>Selects the latest file and the latest baseline file of the tier.</summary>
    /// <param name="tier">The tier.</param>
    public void SelectLatestAndBaseline(Tier tier)
    {
        Latest = RunFileSelector.Latest(Folder, tier);
        LatestBaseline = RunFileSelector.LatestBaseline(Folder, tier);
        Selection = RunFileSelector.Select([Folder]);
    }

    /// <summary>The name of the file <see cref="Latest"/> names.</summary>
    /// <exception cref="XunitException">No file was returned.</exception>
    public string LatestName() => Latest is null ? throw new XunitException("No file was returned.") : Path.GetFileName(Latest);

    /// <summary>The name of the file <see cref="LatestBaseline"/> names.</summary>
    /// <exception cref="XunitException">No baseline file was returned.</exception>
    public string LatestBaselineName() => LatestBaseline is null ? throw new XunitException("No baseline file was returned.") : Path.GetFileName(LatestBaseline);

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}