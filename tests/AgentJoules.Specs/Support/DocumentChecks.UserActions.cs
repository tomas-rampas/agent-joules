namespace AgentJoules.Specs.Support;

/// <summary>A fenced block with the nearest non-blank line before its opening fence.</summary>
/// <param name="Label">The trimmed line before the fence, empty when the block opens the text.</param>
/// <param name="Lines">The lines between the fences.</param>
public sealed record LabelledBlock(string Label, IReadOnlyList<string> Lines);

/// <summary>
/// The checks of AJ-139 (REQ-028): the four headings, the registration lines left for the user, the tracks, the judging criteria mapped to artifacts, the credits, the three labelled command blocks, the Pages URL label
/// and the pre-push checks of <c>docs/devpost-checklist.md</c>. Pure functions over the checklist text and the README text.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The line that precedes every command block the user runs.</summary>
    public const string UserActionLabel = "User action (the builder never runs this):";

    /// <summary>The release command the third block holds.</summary>
    public const string ReleaseCommand = "gh release create v0.1.0 --repo tomas-rampas/agent-joules --target main --title \"Agent Joules v0.1.0\" --notes-file docs/release-notes-v0.1.0.md";

    /// <summary>The four headings of the user-action sections, each once.</summary>
    public static IReadOnlyList<string> UserActionHeadings { get; } = ["Registration", "Tracks", "Judging criteria", "Credits"];

    /// <summary>The four tracks of the form.</summary>
    public static IReadOnlyList<string> TrackNames { get; } = ["Coding and agentic engineering", "Best apps and agents", "Personal AI", "Physical AI"];

    /// <summary>The four judging criteria.</summary>
    public static IReadOnlyList<string> JudgingCriteria { get; } = ["Technological Implementation", "Design", "Potential Impact", "Quality of the Idea"];

    /// <summary>The three release assets the release block lists.</summary>
    public static IReadOnlyList<string> ReleaseAssets { get; } =
        ["artifacts/agent-joules-v0.1.0-fdd.zip", "artifacts/agent-joules-v0.1.0-results.zip", "artifacts/agent-joules-v0.1.0-report.zip"];

    private static readonly string[] UserFields = ["Devpost handle", "Joined on", "Credits requested on"];

    /// <summary>The headings check: <c>Registration</c>, <c>Tracks</c>, <c>Judging criteria</c> and <c>Credits</c> once each, at any level.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures, for example <c>missing heading: Credits</c>.</returns>
    public static IReadOnlyList<string> ChecklistUserHeadings(string checklist) =>
        [.. UserActionHeadings.Select(heading => HasHeadingAtAnyLevel(checklist, heading)).OfType<string>()];

    /// <summary>The registration check: the four lines, the three user values left as <c>&lt;user fills&gt;</c> and the team line as written.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures, for example <c>builder filled a user field: Devpost handle</c>.</returns>
    public static IReadOnlyList<string> ChecklistRegistration(string checklist)
    {
        if (HasHeadingAtAnyLevel(checklist, "Registration") is { } heading)
        {
            return [heading];
        }

        string[] lines = [.. SectionAtAnyLevel(checklist, "Registration").Select(static line => line.Trim().TrimStart('-').Trim()).Where(static line => line.Length > 0)];
        var failures = new List<string>();
        foreach (string field in UserFields)
        {
            string[] found = [.. lines.Where(line => line.StartsWith(field + ":", StringComparison.Ordinal))];
            if (found.Length != 1)
            {
                failures.Add("missing registration line: " + field);
            }
            else if (found[0] != field + ": <user fills>")
            {
                failures.Add("builder filled a user field: " + field);
            }
        }

        if (lines.Count(static line => line == "Team: solo unless stated") != 1)
        {
            failures.Add("missing registration line: Team");
        }

        return failures;
    }

    /// <summary>The tracks check: the four tracks, a recommendation of the coding track with a reason, and <c>Final pick: the user decides</c>.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ChecklistTracks(string checklist)
    {
        if (HasHeadingAtAnyLevel(checklist, "Tracks") is { } heading)
        {
            return [heading];
        }

        IReadOnlyList<string> section = SectionAtAnyLevel(checklist, "Tracks");
        string text = string.Join('\n', section);
        var failures = new List<string>();
        failures.AddRange(TrackNames.Where(track => !text.Contains(track, StringComparison.OrdinalIgnoreCase)).Select(static track => "missing track: " + track));
        string? recommendation = section.FirstOrDefault(static line => line.Contains("recommend", StringComparison.OrdinalIgnoreCase) && line.Contains(TrackNames[0], StringComparison.OrdinalIgnoreCase));
        if (recommendation is null)
        {
            failures.Add("no recommendation of " + TrackNames[0]);
        }
        else if (!recommendation.Contains("reason", StringComparison.OrdinalIgnoreCase) && !recommendation.Contains("because", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("recommendation without a reason");
        }

        if (!section.Any(static line => line.Trim() == "Final pick: the user decides"))
        {
            failures.Add("missing line: Final pick: the user decides");
        }

        return failures;
    }

    /// <summary>The judging check: each of the four criteria has a line that maps it to a README heading (a code span) or a file, and every mapped heading and file exists.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <param name="readme">The README text.</param>
    /// <param name="exists">Whether a repository-relative file exists.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ChecklistJudging(string checklist, string readme, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (HasHeadingAtAnyLevel(checklist, "Judging criteria") is { } heading)
        {
            return [heading];
        }

        IReadOnlyList<string> section = SectionAtAnyLevel(checklist, "Judging criteria");
        string[] readmeHeadings = [.. HeadingsOf(Lines(readme)).Select(static entry => entry.Text)];
        var failures = new List<string>();
        if (!section.Any(static line => line.Contains("equal", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("the equal weights are not stated");
        }

        foreach (string criterion in JudgingCriteria)
        {
            string? line = section.FirstOrDefault(entry => entry.TrimStart('-', ' ').StartsWith(criterion, StringComparison.Ordinal));
            if (line is null)
            {
                failures.Add("missing criterion: " + criterion);
                continue;
            }

            string mapped = line[(line.IndexOf(criterion, StringComparison.Ordinal) + criterion.Length)..];
            string[] paths = [.. ArtifactPaths(mapped)];
            string[] headings =
            [
                .. BacktickedText().Matches(mapped).Select(static match => match.Groups[1].Value)
                    .Where(static span => !span.StartsWith('-') && !span.Contains('/', StringComparison.Ordinal) && !ArtifactPath().IsMatch(span)),
            ];
            if (paths.Length == 0 && headings.Length == 0)
            {
                failures.Add("criterion maps to nothing: " + criterion);
            }

            failures.AddRange(paths.Where(path => !exists(path)).Select(static path => "artifact not found: " + path));
            failures.AddRange(headings.Where(name => !readmeHeadings.Contains(name, StringComparer.Ordinal)).Select(static name => "README heading not found: " + name));
        }

        return failures;
    }

    /// <summary>The credits check: both USD 25 offers, the Resources-tab form with the activation code, the Builders Program, both as user actions, and that neither applies to Nebius AI Cloud.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures, <c>credits section lacks: &lt;text&gt;</c>.</returns>
    public static IReadOnlyList<string> ChecklistCredits(string checklist)
    {
        if (HasHeadingAtAnyLevel(checklist, "Credits") is { } heading)
        {
            return [heading];
        }

        string text = string.Join('\n', SectionAtAnyLevel(checklist, "Credits"));
        var failures = new List<string>();
        if (CountOf(text, "USD 25") < 2)
        {
            failures.Add("credits section lacks: two USD 25 offers");
        }

        foreach (string phrase in new[] { "Token Factory", "Resources", "activation code", "Builders Program" })
        {
            if (!text.Contains(phrase, StringComparison.Ordinal))
            {
                failures.Add("credits section lacks: " + phrase);
            }
        }

        if (!text.Contains("user action", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("credits section lacks: user action");
        }

        bool notApplicable = Lines(text).Any(static line => line.Contains("Nebius AI Cloud", StringComparison.Ordinal)
            && (line.Contains("not apply", StringComparison.OrdinalIgnoreCase) || line.Contains("not applicable", StringComparison.OrdinalIgnoreCase)));
        if (!notApplicable)
        {
            failures.Add("credits section lacks: not applicable to Nebius AI Cloud");
        }

        return failures;
    }

    /// <summary>The fenced blocks of a text, each with the non-blank line before its fence.</summary>
    /// <param name="text">The document text.</param>
    /// <returns>The blocks in order.</returns>
    public static IReadOnlyList<LabelledBlock> LabelledBlocks(string text)
    {
        string[] lines = Lines(text);
        var blocks = new List<LabelledBlock>();
        for (int index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith(CloseFence, StringComparison.Ordinal))
            {
                continue;
            }

            int close = Array.FindIndex(lines, index + 1, static line => line.StartsWith(CloseFence, StringComparison.Ordinal));
            if (close < 0)
            {
                break;
            }

            int before = index - 1;
            while (before >= 0 && lines[before].Trim().Length == 0)
            {
                before--;
            }

            blocks.Add(new LabelledBlock(before < 0 ? string.Empty : lines[before].Trim(), lines[(index + 1)..close]));
            index = close;
        }

        return blocks;
    }

    /// <summary>The command-block check: the push, the Pages and the release block exist, each is preceded by the user-action label, and the release block lists the three assets; the Pages settings are written out and the Pages URL carries its label.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures, for example <c>missing command block: gh release create</c> and <c>block not marked as user action: git push</c>.</returns>
    public static IReadOnlyList<string> ChecklistCommandBlocks(string checklist)
    {
        IReadOnlyList<LabelledBlock> blocks = LabelledBlocks(checklist);
        var failures = new List<string>();
        (string Key, string Needle)[] required = [("git push", "git push -u origin main"), ("gh api", "gh api"), ("gh release create", ReleaseCommand)];
        foreach ((string key, string needle) in required)
        {
            LabelledBlock? block = blocks.FirstOrDefault(entry => entry.Lines.Any(line => line.Contains(needle, StringComparison.Ordinal)));
            if (block is null)
            {
                failures.Add("missing command block: " + key);
            }
            else if (block.Label != UserActionLabel)
            {
                failures.Add("block not marked as user action: " + key);
            }
            else if (key == "gh release create")
            {
                string text = string.Join('\n', block.Lines);
                failures.AddRange(ReleaseAssets.Where(asset => !text.Contains(asset, StringComparison.Ordinal)).Select(static asset => "release block lacks asset: " + asset));
            }
            else if (key == "gh api" && !block.Lines.Any(static line => line.Contains("Deploy from a branch", StringComparison.Ordinal)))
            {
                failures.Add("Pages block lacks the UI path");
            }
        }

        if (!checklist.Contains("Deploy from a branch", StringComparison.Ordinal) || !checklist.Contains("/docs", StringComparison.Ordinal))
        {
            failures.Add("missing Pages settings: Deploy from a branch, branch main, folder /docs");
        }

        if (!checklist.Contains("inferred from the standard project-pages pattern", StringComparison.Ordinal) || !checklist.Contains(ReportPageUrl, StringComparison.Ordinal))
        {
            failures.Add("Pages URL without the label: inferred from the standard project-pages pattern");
        }

        return failures;
    }

    /// <summary>The pre-push check: a <c>Before you push</c> list with the verify-readme, key-grep and clean-status checks.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ChecklistBeforePush(string checklist)
    {
        string[] lines = Lines(checklist);
        var failures = new List<string>();
        if (!lines.Any(static line => line.Contains("Before you push", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("missing list: Before you push");
        }

        (string Command, string Outcome)[] checks = [("report --verify-readme", "exits 0"), ("git grep -nE \"nvapi-[A-Za-z0-9_-]{8,}\"", "nothing"), ("git status --short", "empty")];
        foreach ((string command, string outcome) in checks)
        {
            if (!lines.Any(line => line.Contains(command, StringComparison.Ordinal) && line.Contains(outcome, StringComparison.Ordinal)))
            {
                failures.Add("missing pre-push check: " + command);
            }
        }

        return failures;
    }

    /// <summary>All checks of the checklist (AJ-138 and AJ-139).</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <param name="readme">The README text.</param>
    /// <param name="exists">Whether a repository-relative file exists.</param>
    /// <param name="cloudGpuRan">Whether a completed or partial cloudgpu run file exists.</param>
    /// <returns>The failures of every check.</returns>
    public static IReadOnlyList<string> ChecklistChecks(string checklist, string readme, Func<string, bool> exists, bool cloudGpuRan) =>
    [
        .. ChecklistSource(checklist),
        .. ChecklistRules(checklist),
        .. ChecklistDeadline(checklist),
        .. ChecklistConflicts(checklist),
        .. ChecklistFields(checklist, exists),
        .. ChecklistDemoUrl(checklist),
        .. ChecklistBuiltWith(checklist, cloudGpuRan),
        .. ChecklistUserHeadings(checklist),
        .. ChecklistRegistration(checklist),
        .. ChecklistTracks(checklist),
        .. ChecklistJudging(checklist, readme, exists),
        .. ChecklistCredits(checklist),
        .. ChecklistCommandBlocks(checklist),
        .. ChecklistBeforePush(checklist),
    ];
}