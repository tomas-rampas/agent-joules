using System.Text.Json;
using System.Text.RegularExpressions;

using AgentJoules.Reporting.Results;

namespace AgentJoules.TestSupport.Docs;

/// <summary>The state of the cloudgpu tier as the committed run files give it (AJ-129, EDGE-017).</summary>
/// <param name="State"><c>complete</c> (a selectable file with status <c>completed</c>), <c>partial</c> (a selectable file with status <c>partial</c>) or <c>absent</c> (no such file).</param>
/// <param name="SelectedFile">The file name (no directory) of the newest selectable cloudgpu file, or null when the state is <c>absent</c>.</param>
/// <param name="GpuName">The <c>environment.gpuName</c> of that file, or null when the state is <c>absent</c> or the file names none.</param>
/// <param name="Status">The <c>run.status</c> of that file, or null when the state is <c>absent</c>.</param>
public sealed record CloudGpuTierState(string State, string? SelectedFile, string? GpuName, string? Status);

/// <summary>
/// The tagline rule and the story check of AJ-129 (REQ-028, EDGE-017), as pure functions over texts so a test can feed scratch files. The tier state comes only from the committed cloudgpu run files, by the rules of
/// <see cref="RunFileSelector"/>: the name must satisfy <see cref="RunFileName.IsValid"/> and name the cloudgpu tier without the baseline suffix, the newest name wins, and a file is selectable when its header
/// status is <c>completed</c> or <c>partial</c> and its flags lack <c>non-publishable</c>; a file that is <c>incomplete</c>, <c>running</c>, <c>failed</c>, non-publishable, unreadable or of another tier counts as none.
/// </summary>
public static partial class CloudGpuTaglineRule
{
    /// <summary>The state when no cloudgpu file is selectable.</summary>
    public const string Absent = "absent";

    /// <summary>The state of a selectable file with status <c>completed</c>.</summary>
    public const string Complete = "complete";

    /// <summary>The state of a selectable file with status <c>partial</c>.</summary>
    public const string Partial = "partial";

    /// <summary>The GPU token of candidate A that is replaced when the cloudgpu file names another GPU: the literal text <c>L40S</c>.</summary>
    public const string GpuToken = "L40S";

    /// <summary>The label that starts the line before candidate A in <c>docs/devpost-pitch.md</c>.</summary>
    public const string CandidateALabel = "**A (recommended)**";

    /// <summary>The label that starts the line before candidate A-local in <c>docs/devpost-pitch.md</c>.</summary>
    public const string CandidateALocalLabel = "**A-local (fallback)**";

    private const string BaselineSuffix = "-baseline.json";
    private const string TierSuffix = "-cloudgpu.json";
    private const string ChecklistHeading = "Fill-after-build checklist";
    private const string Placeholder = "[[FILL";
    private const string Task = "AJ-129";
    private const int EntryKeyLength = 40;
    private const int NameLength = 60;

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z\[""(`*])", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBreak();

    /// <summary>Derives the tier state from the committed results files. A <c>partial</c> file with no step whose <c>outcome</c> is <c>ok</c> is skipped, as the selector skips it.</summary>
    /// <param name="committedResultsFiles">The paths or names of the committed files of <c>results/</c>; only the file name of each is used to choose, and the path as given is passed to <paramref name="readFile"/>.</param>
    /// <param name="readFile">Returns the text of a file, by the path as given.</param>
    /// <returns>The state.</returns>
    public static CloudGpuTierState Evaluate(IEnumerable<string> committedResultsFiles, Func<string, string> readFile)
    {
        ArgumentNullException.ThrowIfNull(committedResultsFiles);
        ArgumentNullException.ThrowIfNull(readFile);

        IEnumerable<(string Path, string Name)> candidates = committedResultsFiles
            .Select(static path => (Path: path, Name: System.IO.Path.GetFileName(path)))
            .Where(static file => RunFileName.IsValid(file.Name) && file.Name.EndsWith(TierSuffix, StringComparison.Ordinal) && !file.Name.EndsWith(BaselineSuffix, StringComparison.Ordinal))
            .OrderByDescending(static file => file.Name, StringComparer.Ordinal);

        foreach ((string path, string name) in candidates)
        {
            if (Header(readFile(path)) is { } header)
            {
                // A partial file with no ok step (the box expired during warm-up) holds no measurement: the selector skips it (partial-without-ok-step, peer review m-2), so the rule skips it too and an older valid file stands.
                if (header.Status == "partial" && header.OkSteps == 0)
                {
                    continue;
                }

                return new CloudGpuTierState(header.Status == "completed" ? Complete : Partial, name, header.GpuName, header.Status);
            }
        }

        return new CloudGpuTierState(Absent, null, null, null);
    }

    /// <summary>The tagline for a state: candidate A for <c>complete</c> and <c>partial</c>, candidate A-local for <c>absent</c>.</summary>
    /// <param name="state">The tier state.</param>
    /// <param name="candidateA">The text of candidate A.</param>
    /// <param name="candidateALocal">The text of candidate A-local.</param>
    /// <returns>
    /// The text. When the state is <c>complete</c> or <c>partial</c> and the file's <c>gpuName</c> is not blank and does not contain <c>L40S</c>, the literal token <c>L40S</c> of candidate A (ordinal match, every
    /// occurrence) is replaced by the <c>gpuName</c>.
    /// </returns>
    public static string Tagline(CloudGpuTierState state, string candidateA, string candidateALocal)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(candidateA);
        ArgumentNullException.ThrowIfNull(candidateALocal);

        return state.State == Absent
            ? candidateALocal
            : string.IsNullOrWhiteSpace(state.GpuName) || state.GpuName.Contains(GpuToken, StringComparison.Ordinal)
                ? candidateA
                : candidateA.Replace(GpuToken, state.GpuName.Trim(), StringComparison.Ordinal);
    }

    /// <summary>The text of a candidate: the first quote line (<c>&gt; </c>) after the line that starts with <paramref name="label"/>.</summary>
    /// <param name="pitch">The text of <c>docs/devpost-pitch.md</c>.</param>
    /// <param name="label">One of <see cref="CandidateALabel"/> and <see cref="CandidateALocalLabel"/>.</param>
    /// <returns>The candidate text without the quote marker.</returns>
    /// <exception cref="InvalidDataException">The label or its quote line is missing.</exception>
    public static string CandidateOf(string pitch, string label)
    {
        ArgumentNullException.ThrowIfNull(pitch);
        ArgumentException.ThrowIfNullOrEmpty(label);

        string[] lines = pitch.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        int at = Array.FindIndex(lines, line => line.StartsWith(label, StringComparison.Ordinal));
        for (int index = at + 1; at >= 0 && index < lines.Length && !lines[index].StartsWith("**", StringComparison.Ordinal); index++)
        {
            if (lines[index].StartsWith("> ", StringComparison.Ordinal))
            {
                return lines[index][2..].Trim();
            }
        }

        throw new InvalidDataException("The pitch has no quote line after the label " + label + ".");
    }

    /// <summary>
    /// The commit that holds the pre-drafted <c>docs/devpost-story.md</c>: the last commit that changed the story before AJ-129-T7 (found at ef5992b with <c>git log -1 --format=%H -- docs/devpost-story.md</c>; the story switch
    /// to one Nebius L40S in FP8). It is a constant, not a merge-base: a merge-base moves when <c>main</c> moves or is absent on CI, and after the M5 merge the story would be compared with itself. AJ-129-T7 changes the
    /// story after this commit, so a sentence it replaces needs an AJ-129 entry in <c>docs/build-log.md</c>.
    /// </summary>
    public const string PreDraftedStoryCommit = "9f4ac3660a08e1dd9b433d460055f3af60bddea6";

    /// <summary>The longest tagline Devpost accepts, in characters.</summary>
    public const int TaglineLimit = 200;

    /// <summary>The pre-drafted text of <c>docs/devpost-story.md</c>: the file at <see cref="PreDraftedStoryCommit"/>, read through <see cref="GitRunner"/>.</summary>
    /// <param name="repositoryRoot">The working tree.</param>
    /// <returns>The text, or null when git or the commit (a shallow clone may lack it) is not available.</returns>
    public static string? PreDraftedStory(string repositoryRoot)
    {
        try
        {
            GitResult show = GitRunner.Run(repositoryRoot, "show", PreDraftedStoryCommit + ":docs/devpost-story.md");

            return show.ExitCode == 0 ? show.Stdout : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The problems of the story against its pre-drafted version. A sentence of the pre-drafted text that the current text no longer holds needs an entry of the build log: a line that contains <c>AJ-129</c> and
    /// the first 40 characters of that old sentence and the proving file (a <c>-cloudgpu.json</c> name or a <c>results/</c> path). A pre-drafted sentence with a <c>[[FILL</c> placeholder and everything from the "Fill-after-build checklist" heading on are not contradictions of a measured fact (the
    /// builder fills and deletes them as the prompt requires), so they need no entry here. A sentence only the current text holds is not checked: it is the new wording of an old one.
    /// </summary>
    /// <param name="devpostStory">The current text of <c>docs/devpost-story.md</c>.</param>
    /// <param name="preDraftedStory">The pre-drafted text.</param>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <returns>One problem per changed sentence without an entry, naming the first 60 characters of the old sentence; empty when every change is logged.</returns>
    public static IReadOnlyList<string> ConsequenceProblems(string devpostStory, string preDraftedStory, string buildLog)
    {
        ArgumentNullException.ThrowIfNull(devpostStory);
        ArgumentNullException.ThrowIfNull(preDraftedStory);
        ArgumentNullException.ThrowIfNull(buildLog);

        HashSet<string> current = [.. SentencesOf(devpostStory)];
        string[] entries = [.. buildLog.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Where(static line => line.Contains(Task, StringComparison.Ordinal))];
        var problems = new List<string>();
        foreach (string old in SentencesOf(preDraftedStory).Where(sentence => !current.Contains(sentence) && !sentence.Contains(Placeholder, StringComparison.Ordinal)))
        {
            string key = old[..Math.Min(EntryKeyLength, old.Length)];
            if (!entries.Any(line => line.Contains(key, StringComparison.Ordinal) && (line.Contains(TierSuffix, StringComparison.Ordinal) || line.Contains("results/", StringComparison.Ordinal))))
            {
                problems.Add($"docs/devpost-story.md sentence changed without an {Task} entry in docs/build-log.md: \"{old[..Math.Min(NameLength, old.Length)]}\"");
            }
        }

        return problems;
    }

    /// <summary>
    /// The problems of a tagline change. When the tagline in use differs from candidate A (the GPU token was replaced), <c>docs/build-log.md</c> needs a line that contains <c>AJ-129</c> and the first 40 characters of
    /// candidate A and either the replaced GPU segment of the new tagline (the file's <c>gpuName</c>) or the proving <c>-cloudgpu.json</c> file name (A and A-local share their first 55 characters, so the old text
    /// alone does not say which candidate a line is about), and the new tagline may not exceed <see cref="TaglineLimit"/> characters.
    /// </summary>
    /// <param name="oldCandidateA">Candidate A as <c>docs/devpost-pitch.md</c> holds it.</param>
    /// <param name="newTagline">The tagline the rule selects for the state of the tier.</param>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <returns>One problem per defect, never the tagline text beyond its first 40 characters; empty when the tagline is unchanged, or changed with an entry and within the limit.</returns>
    public static IReadOnlyList<string> TaglineChangeProblems(string oldCandidateA, string newTagline, string buildLog)
    {
        ArgumentNullException.ThrowIfNull(oldCandidateA);
        ArgumentNullException.ThrowIfNull(newTagline);
        ArgumentNullException.ThrowIfNull(buildLog);

        var problems = new List<string>();
        if (string.Equals(oldCandidateA, newTagline, StringComparison.Ordinal))
        {
            return problems;
        }

        string key = oldCandidateA[..Math.Min(EntryKeyLength, oldCandidateA.Length)];
        string? segment = ReplacedSegment(oldCandidateA, newTagline);
        string[] entries = [.. buildLog.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Where(static line => line.Contains(Task, StringComparison.Ordinal))];
        if (!entries.Any(line => line.Contains(key, StringComparison.Ordinal)
            && (line.Contains(TierSuffix, StringComparison.Ordinal) || (segment is not null && line.Contains(segment, StringComparison.Ordinal)))))
        {
            problems.Add($"the tagline changed without an {Task} entry in docs/build-log.md naming the old candidate A: \"{key}\"");
        }

        if (newTagline.Length > TaglineLimit)
        {
            problems.Add($"the changed tagline has {newTagline.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters, above the {TaglineLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)} limit");
        }

        return problems;
    }

    // The text of the new tagline that stands where the first GPU token of the old candidate stood; null when the old candidate holds no token or the new tagline does not keep its start.
    private static string? ReplacedSegment(string oldCandidateA, string newTagline)
    {
        int at = oldCandidateA.IndexOf(GpuToken, StringComparison.Ordinal);
        if (at < 0 || !newTagline.StartsWith(oldCandidateA[..at], StringComparison.Ordinal))
        {
            return null;
        }

        string rest = newTagline[at..];
        string after = oldCandidateA[(at + GpuToken.Length)..];
        int end = after.Length == 0 ? -1 : rest.IndexOf(after[..Math.Min(10, after.Length)], StringComparison.Ordinal);
        string segment = (end < 0 ? rest : rest[..end]).Trim();

        return segment.Length == 0 ? null : segment;
    }

    // The sentences of a story: headings and blank lines are no sentence, the checklist section ends the text, and a list marker or quote marker is no part of a sentence.
    private static IEnumerable<string> SentencesOf(string text)
    {
        foreach (string raw in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('#'))
            {
                if (line.Contains(ChecklistHeading, StringComparison.Ordinal))
                {
                    yield break;
                }

                continue;
            }

            line = line.TrimStart('>', '-', '*', ' ');
            if (line.Length == 0)
            {
                continue;
            }

            foreach (string sentence in SentenceBreak().Split(line))
            {
                if (sentence.Trim().Length > 0)
                {
                    yield return sentence.Trim();
                }
            }
        }
    }

    // The status, GPU name and count of ok steps of a selectable header: completed or partial, without the non-publishable flag, for the cloudgpu tier; null for everything else.
    // A file without a steps array (or with a steps value that is not an array) is not skipped by the selector, which leaves it to the full read (peer review n-3): it is counted as one ok step here, so the rule keeps it selectable too.
    private static (string Status, string? GpuName, int OkSteps)? Header(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("run", out JsonElement run) || run.ValueKind != JsonValueKind.Object
                || !run.TryGetProperty("tier", out JsonElement tier) || tier.GetString() != "cloudgpu"
                || !run.TryGetProperty("status", out JsonElement statusElement))
            {
                return null;
            }

            string? status = statusElement.GetString();
            if (status is not ("completed" or "partial"))
            {
                return null;
            }

            // Like the selector's header reader: flags are absent, null, or an array of strings; anything else makes the file unusable.
            if (run.TryGetProperty("flags", out JsonElement flags) && flags.ValueKind != JsonValueKind.Null)
            {
                if (flags.ValueKind != JsonValueKind.Array || flags.EnumerateArray().Any(static flag => flag.ValueKind != JsonValueKind.String))
                {
                    return null;
                }

                if (flags.EnumerateArray().Any(static flag => flag.GetString() == "non-publishable"))
                {
                    return null;
                }
            }

            string? gpu = root.TryGetProperty("environment", out JsonElement environment) && environment.ValueKind == JsonValueKind.Object
                && environment.TryGetProperty("gpuName", out JsonElement name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null;

            int okSteps = root.TryGetProperty("steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array
                ? steps.EnumerateArray().Count(static step => step.ValueKind == JsonValueKind.Object && step.TryGetProperty("outcome", out JsonElement outcome) && outcome.ValueKind == JsonValueKind.String && outcome.GetString() == "ok")
                : 1;

            return (status, gpu, okSteps);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}