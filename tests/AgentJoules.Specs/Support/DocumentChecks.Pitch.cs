using System.Text.RegularExpressions;

using AgentJoules.TestSupport.Docs;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The checks of AJ-141 (REQ-028): the pitch rule (candidate A, candidate A-local or the GPU-token swap) and the checklist block against the pitch file. Pure functions over the checklist text and the pitch file text;
/// the cloudgpu state comes from <see cref="CloudGpuTaglineRule"/> or from <see cref="CloudGpuRan(IEnumerable{string}, Func{string, string})"/>.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The longest elevator pitch the form takes.</summary>
    public const int PitchLimit = 200;

    /// <summary>The reason the checklist gives when the cloudgpu tier left no run file.</summary>
    public const string NoCloudGpuReason = "cloudgpu tier produced no committed run file";

    private const int PitchBlockMinimum = 100;

    [GeneratedRegex(@"^Characters: (\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CharactersLine();

    [GeneratedRegex(@"[A-Z]+[0-9]+[A-Z]*", RegexOptions.CultureInvariant)]
    private static partial Regex GpuClass();

    /// <summary>The first token of a GPU name that matches <c>[A-Z]+[0-9]+[A-Z]*</c>: <c>H100</c> for <c>NVIDIA H100 80GB HBM3</c>.</summary>
    /// <param name="gpuName">The <c>environment.gpuName</c> of the cloudgpu file.</param>
    /// <returns>The token, or null when the name holds none.</returns>
    public static string? GpuClassToken(string? gpuName)
    {
        Match match = GpuClass().Match(gpuName ?? string.Empty);

        return match.Success ? match.Value : null;
    }

    /// <summary>The pitch the rule selects: A-local without a cloudgpu file, candidate A with one, and the GPU token <c>L40S</c> of candidate A replaced by the GPU class of the file when its name lacks <c>L40S</c>.</summary>
    /// <param name="state">The cloudgpu state of the run files.</param>
    /// <param name="candidateA">The text of candidate A.</param>
    /// <param name="candidateALocal">The text of candidate A-local.</param>
    /// <returns>The pitch text.</returns>
    public static string PitchFor(CloudGpuTierState state, string candidateA, string candidateALocal)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.State == CloudGpuTaglineRule.Absent)
        {
            return candidateALocal;
        }

        if (string.IsNullOrWhiteSpace(state.GpuName) || state.GpuName.Contains(CloudGpuTaglineRule.GpuToken, StringComparison.Ordinal))
        {
            return candidateA;
        }

        return candidateA.Replace(CloudGpuTaglineRule.GpuToken, GpuClassToken(state.GpuName) ?? state.GpuName.Trim(), StringComparison.Ordinal);
    }

    /// <summary>The pitch block of a checklist: the one fenced block that is not a user-action block and holds a single long line.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The pitch text, or null when the checklist holds no such block.</returns>
    public static string? PitchBlock(string checklist) =>
        LabelledBlocks(checklist)
            .Where(static block => block.Label != UserActionLabel)
            .Select(static block => block.Lines.Where(static line => line.Trim().Length > 0).ToArray())
            .Where(static lines => lines.Length == 1 && lines[0].Length > PitchBlockMinimum)
            .Select(static lines => lines[0])
            .FirstOrDefault();

    /// <summary>The singular form of the short tagline row.</summary>
    /// <param name="pluralTagline">The tagline of the pitch file.</param>
    /// <returns>The tagline without the final <c>s</c> of <c>GPUs</c>.</returns>
    public static string SingularTaglineOf(string pluralTagline) => pluralTagline.EndsWith("GPUs", StringComparison.Ordinal) ? pluralTagline[..^1] : pluralTagline;

    /// <summary>The pitch checks of the checklist against the pitch file and the cloudgpu state.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <param name="pitchFile">The text of <c>docs/devpost-pitch.md</c>.</param>
    /// <param name="cloudGpuRan">Whether a completed or partial cloudgpu run file exists.</param>
    /// <returns>The failures: <c>pitch block differs from docs/devpost-pitch.md</c>, <c>201 characters exceed 200</c>, <c>A-local chosen but cloudgpu run exists</c>, <c>candidate A chosen without cloudgpu run</c> and the others.</returns>
    public static IReadOnlyList<string> PitchChecks(string checklist, string pitchFile, bool cloudGpuRan)
    {
        string candidateA;
        string candidateALocal;
        try
        {
            candidateA = CloudGpuTaglineRule.CandidateOf(pitchFile, CloudGpuTaglineRule.CandidateALabel);
            candidateALocal = CloudGpuTaglineRule.CandidateOf(pitchFile, CloudGpuTaglineRule.CandidateALocalLabel);
        }
        catch (InvalidDataException exception)
        {
            return ["pitch file: " + exception.Message];
        }

        string? block = PitchBlock(checklist);
        if (block is null)
        {
            return ["missing pitch block"];
        }

        var failures = new List<string>();
        if (block.Length > PitchLimit)
        {
            failures.Add($"{Digits(block.Length)} characters exceed {Digits(PitchLimit)}");
        }

        string chosen = cloudGpuRan ? candidateA : candidateALocal;
        if (block != chosen)
        {
            failures.Add(!cloudGpuRan && block == candidateA ? "candidate A chosen without cloudgpu run"
                : cloudGpuRan && block == candidateALocal ? "A-local chosen but cloudgpu run exists"
                : "pitch block differs from docs/devpost-pitch.md");
        }

        Match[] counts = [.. Lines(checklist).Select(static line => CharactersLine().Match(line)).Where(static match => match.Success)];
        if (counts.Length == 0)
        {
            failures.Add("missing line: Characters:");
        }
        else if (counts[0].Groups[1].Value != Digits(block.Length))
        {
            failures.Add($"Characters line says {counts[0].Groups[1].Value}, the block has {Digits(block.Length)}");
        }

        failures.AddRange(PitchTaglineRow(checklist, pitchFile, cloudGpuRan));
        if (!cloudGpuRan)
        {
            if (!checklist.Contains(NoCloudGpuReason, StringComparison.Ordinal))
            {
                failures.Add("missing reason: " + NoCloudGpuReason);
            }

            failures.AddRange(new[] { "Nebius AI Cloud", CloudGpuTaglineRule.GpuToken }.Where(token => block.Contains(token, StringComparison.Ordinal)).Select(static token => "A-local text names " + token));
        }

        return failures;
    }

    /// <summary>The short tagline row: plural (58 characters) with a cloudgpu file, singular (57) without.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <param name="pitchFile">The text of the pitch file.</param>
    /// <param name="cloudGpuRan">Whether a completed or partial cloudgpu run file exists.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> PitchTaglineRow(string checklist, string pitchFile, bool cloudGpuRan)
    {
        string? plural = PitchTagline(pitchFile);
        if (plural is null)
        {
            return ["missing tagline section in the pitch file"];
        }

        string expected = cloudGpuRan ? plural : SingularTaglineOf(plural);
        bool found = Lines(checklist).Any(line =>
        {
            int at = line.IndexOf(expected, StringComparison.Ordinal);

            return at >= 0 && (cloudGpuRan || at + expected.Length >= line.Length || line[at + expected.Length] != 's');
        });

        return found ? [] : ["missing short tagline row: " + expected];
    }
}