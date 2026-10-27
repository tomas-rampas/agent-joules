using System.Text.Json.Nodes;

using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Readme;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The workspace builders the README stories share (AJ-131, AJ-132, AJ-133): the committed README with the two result markers guaranteed, the fixture run files of the tiers, and the cloudgpu run file in its completed
/// and partial forms. Everything is built in memory and written into a <see cref="TempWorkspace"/>; the repository is only read.
/// </summary>
public static class ReadmeFixtures
{
    /// <summary>The stop reason a budget-stopped cloudgpu run records.</summary>
    public const string CostCapStopReason = "cost-cap";

    /// <summary>The README text with the two marker lines; when the text has no begin marker, an empty marked block is appended.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>The text with markers.</returns>
    public static string WithMarkers(string readme)
    {
        ArgumentNullException.ThrowIfNull(readme);

        return readme.Contains(ReadmeResultsBlock.BeginMarker, StringComparison.Ordinal)
            ? readme
            : readme.TrimEnd() + "\n\n" + ReadmeResultsBlock.BeginMarker + "\n" + ReadmeResultsBlock.EndMarker + "\n";
    }

    /// <summary>Writes the committed fixture run file of each tier into <c>results</c> of the workspace.</summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="tiers">The local, nvidia or nebius tiers.</param>
    public static void AddCommitted(TempWorkspace workspace, params Tier[] tiers)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(tiers);

        foreach (Tier tier in tiers)
        {
            _ = workspace.WriteFile(Path.Combine("results", RunDocumentFixtures.CommittedFileName(tier)), RunDocumentFixtures.CommittedText(tier));
        }
    }

    /// <summary>The file name of the cloudgpu fixture run file.</summary>
    public static string CloudGpuFileName => "20260101T000000Z-cloudgpu.json";

    /// <summary>The text of a cloudgpu run file of the report fixtures: partial with the given stop reason, or completed.</summary>
    /// <param name="partial">True for <c>run.status</c> <c>partial</c> with <paramref name="stopReason"/>.</param>
    /// <param name="stopReason">The <c>run.stopReason</c> of a partial file.</param>
    /// <returns>The JSON text with a final line feed.</returns>
    public static string CloudGpuText(bool partial, string stopReason = CostCapStopReason)
    {
        string json = RunDocumentFixtures.Serialise(RunDocumentFixtures.CloudGpuPartialSampling());

        return RunDocumentFixtures.Edit(
            json,
            root =>
            {
                JsonObject run = root["run"]!.AsObject();
                if (partial)
                {
                    run["stopReason"] = stopReason;
                }
                else
                {
                    run["status"] = "completed";
                    _ = run.Remove("stopReason");
                }
            }) + "\n";
    }

    /// <summary>Writes the cloudgpu fixture run file into <c>results</c> of the workspace.</summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="partial">True for a partial file.</param>
    public static void AddCloudGpu(TempWorkspace workspace, bool partial)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _ = workspace.WriteFile(Path.Combine("results", CloudGpuFileName), CloudGpuText(partial));
    }

    /// <summary>The text of the local fixture run file with every step on the sampling method, as a GPU without the energy counter records it.</summary>
    /// <returns>The JSON text with a final line feed.</returns>
    public static string LocalSamplingText() =>
        RunDocumentFixtures.Edit(
            RunDocumentFixtures.CommittedText(Tier.Local),
            root =>
            {
                foreach (JsonNode? step in root["steps"]!.AsArray())
                {
                    step!["method"] = "sampling";
                }
            }) + "\n";

    /// <summary>The text of the nvidia fixture run file whose first step has a measured energy of zero joules: a hosted tier has no GPU energy, so the file must be refused.</summary>
    /// <returns>The JSON text with a final line feed.</returns>
    public static string HostedZeroEnergyText() =>
        RunDocumentFixtures.Edit(
            RunDocumentFixtures.CommittedText(Tier.Nvidia),
            root =>
            {
                JsonObject net = root["steps"]![0]!["netJoules"]!.AsObject();
                net["value"] = 0;
                net["label"] = "measured";
                net["unit"] = "J";
                _ = net.Remove("reason");
            }) + "\n";

    /// <summary>The text between the two marker lines of a README, without the markers.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>The block text.</returns>
    public static string BlockOf(string readme)
    {
        ArgumentNullException.ThrowIfNull(readme);
        int begin = readme.IndexOf(ReadmeResultsBlock.BeginMarker, StringComparison.Ordinal) + ReadmeResultsBlock.BeginMarker.Length;
        int end = readme.IndexOf(ReadmeResultsBlock.EndMarker, begin, StringComparison.Ordinal);

        return readme[begin..end];
    }
}