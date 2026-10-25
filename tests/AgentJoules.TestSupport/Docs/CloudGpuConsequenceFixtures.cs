using System.Text.RegularExpressions;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// The cloudgpu fixtures of AJ-129 (EDGE-017, REQ-031), built in memory from the report fixtures: a cloudgpu run document of a chosen status, GPU name and energy method (provider nebius-ai-cloud, precision FP8) and the scratch
/// candidates and story texts the tagline rule and the story check are tried on. Nothing here reads a committed file or the network.
/// </summary>
public static partial class CloudGpuConsequenceFixtures
{
    /// <summary>The pattern of a joule figure such as <c>12.5 J</c>, <c>30J</c>, <c>7 kJ</c> or <c>3 Joules</c>, shared by the report test and the feature step.</summary>
    public const string JoulePattern = @"\d+(\.\d+)?\s*(J|kJ|Joules?)\b";

    /// <summary>The GPU name of the L40S.</summary>
    public const string L40S = "NVIDIA L40S";

    /// <summary>The name of the committed file the scratch folders hold.</summary>
    public const string FileName = "20261024T090000Z-cloudgpu.json";

    /// <summary>A scratch candidate A: the GPU token is the literal L40S.</summary>
    public const string CandidateA = "Which agent step burns the most energy? Meters joules on your GPU and a Nebius AI Cloud L40S, then compares cost.";

    /// <summary>A scratch candidate A-local.</summary>
    public const string CandidateALocal = "Which agent step burns the most energy? Meters local GPU joules and compares cost.";

    /// <summary>A scratch pre-drafted story of three sentences.</summary>
    public const string PreDraftedStory = "# Story\n\nThe cloud tier runs on an H100. The laptop tier runs on an RTX GPU. Energy is measured on both.\n";

    /// <summary>A cloudgpu run document.</summary>
    /// <param name="status">The run status.</param>
    /// <param name="gpuName">The <c>environment.gpuName</c>.</param>
    /// <param name="sampling">True for the sampling energy method, false for the counter.</param>
    /// <returns>The document.</returns>
    public static RunDocument Document(RunStatus status, string gpuName = L40S, bool sampling = false)
    {
        RunDocument source = sampling ? RunDocumentFixtures.CloudGpuPartialSampling() : RunDocumentFixtures.Partial(Tier.CloudGpu);

        return source with
        {
            Run = source.Run with { Status = status, StopReason = status == RunStatus.Partial ? "budget-reached" : null },
            Environment = source.Environment with { GpuName = gpuName, Precision = "FP8" },
        };
    }

    /// <summary>The serialised text of <see cref="Document"/> as the tool would write it.</summary>
    /// <param name="status">The run status.</param>
    /// <param name="gpuName">The <c>environment.gpuName</c>.</param>
    /// <returns>The JSON text.</returns>
    public static string Json(RunStatus status, string gpuName = L40S) => RunDocumentFixtures.Serialise(Document(status, gpuName)) + "\n";

    /// <summary>The lines of a summary that name both a laptop and a cloudgpu tier and hold a joule figure: a merged figure, which the report never writes.</summary>
    /// <param name="lines">The lines of the summary.</param>
    /// <returns>The offending lines; empty when no figure mixes the tiers.</returns>
    public static IReadOnlyList<string> MergedJouleLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return [.. lines.Where(static line => line.Contains("local", StringComparison.Ordinal) && line.Contains("cloudgpu", StringComparison.Ordinal) && Joules().IsMatch(line))];
    }

    [GeneratedRegex(JoulePattern, RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Joules();

    /// <summary>The tier state of a scratch folder that holds one cloudgpu file of the given status.</summary>
    /// <param name="status">The run status.</param>
    /// <param name="gpuName">The <c>environment.gpuName</c>.</param>
    /// <returns>The state.</returns>
    public static CloudGpuTierState StateOf(RunStatus status, string gpuName = L40S) =>
        CloudGpuTaglineRule.Evaluate([FileName], _ => Json(status, gpuName));
}