using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The checks of AJ-142 (REQ-028): the filled <c>docs/devpost-feedback.md</c> against its pre-fill version. The builder placeholders are gone, the seven user placeholders are byte-identical to the pre-fill lines, the
/// filled passages (found by diffing against the pre-fill file) name an existing source and a label or an explicit not-run statement, the baseline and credits answers follow their rules, every changed design sentence
/// has a <c>Feedback change:</c> entry and no ranking claim stands. Pure functions over strings.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The repository-relative path of the feedback file.</summary>
    public const string FeedbackPath = "docs/devpost-feedback.md";

    /// <summary>The heading of the checklist section that lists the user placeholders.</summary>
    public const string FeedbackChecklistHeading = "Feedback form, user fills";

    /// <summary>The baseline passage when no baseline run file exists.</summary>
    public const string NoBaselineSentence = "No Qwen3 8B baseline run was made.";

    /// <summary>The credits passage when the user reported nothing.</summary>
    public const string CreditsNotReported = "Not reported: the builder cannot see the Nebius billing page.";

    /// <summary>The label of the build-log line that records what the user said about the credits.</summary>
    public const string CreditsStatementMarker = "User statement on credits:";

    /// <summary>The marker of a build-log entry for a changed design sentence.</summary>
    public const string FeedbackChangeMarker = "Feedback change:";

    /// <summary>The questions whose placeholders belong to the user.</summary>
    public static IReadOnlyList<int> UserQuestions { get; } = [2, 4, 5, 6, 7, 8, 9];

    private const string FillChecklistHeading = "Fill checklist";

    private static readonly string[] NotRunPhrases = ["was not used", "was not run", "did not run", "was not made", "Not reported"];

    private static readonly string[] ExplicitStatements = [NoBaselineSentence, CreditsNotReported, "The Nebius AI Cloud VM was not used in this submission"];

    [GeneratedRegex(@"\bQ(\d)\b", RegexOptions.CultureInvariant)]
    private static partial Regex QuestionReference();

    [GeneratedRegex(@"^## Q(\d+)\.", RegexOptions.CultureInvariant)]
    private static partial Regex QuestionHeading();

    [GeneratedRegex(@"^\| .* \| user \|", RegexOptions.CultureInvariant)]
    private static partial Regex UserRow();

    [GeneratedRegex(@"^\| .* \| builder \|", RegexOptions.CultureInvariant)]
    private static partial Regex BuilderRow();

    [GeneratedRegex(@"\b(?:compar\w*|against|than|versus)\b|\bvs\.?\s", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ComparisonWord();

    [GeneratedRegex(@"greener|more efficient than|outperforms|beats ", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FeedbackRankingClaim();

    /// <summary>The lines that carry <c>owner: user</c> with the number of the question they sit under.</summary>
    /// <param name="feedback">The feedback text.</param>
    /// <returns>The pairs in document order.</returns>
    public static IReadOnlyList<(int Question, string Line)> UserPlaceholders(string feedback)
    {
        var found = new List<(int, string)>();
        int question = 0;
        foreach (string line in Lines(feedback))
        {
            Match heading = QuestionHeading().Match(line);
            if (heading.Success)
            {
                question = int.Parse(heading.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (line.Contains("owner: user", StringComparison.Ordinal))
            {
                found.Add((question, line));
            }
        }

        return found;
    }

    /// <summary>The marker check: no <c>owner: builder</c> is left, seven <c>owner: user</c> lines remain and each equals its pre-fill line.</summary>
    /// <param name="feedback">The feedback text.</param>
    /// <param name="preFill">The pre-fill feedback text.</param>
    /// <returns>The failures: <c>builder placeholder left: &lt;n&gt;</c> and <c>user placeholder modified: Q&lt;n&gt;</c>.</returns>
    public static IReadOnlyList<string> FeedbackMarkers(string feedback, string preFill)
    {
        var failures = new List<string>();
        int builder = CountOf(feedback, "owner: builder");
        if (builder > 0)
        {
            failures.Add("builder placeholder left: " + Digits(builder));
        }

        IReadOnlyList<(int Question, string Line)> now = UserPlaceholders(feedback);
        if (now.Count != UserQuestions.Count)
        {
            failures.Add($"{Digits(now.Count)} user placeholders, {Digits(UserQuestions.Count)} required");
        }

        foreach ((int question, string line) in UserPlaceholders(preFill))
        {
            if (!now.Contains((question, line)))
            {
                failures.Add("user placeholder modified: Q" + Digits(question));
            }
        }

        return failures;
    }

    /// <summary>The checklist check: the section <c>Feedback form, user fills</c> has seven bullets, one for each of Q2, Q4, Q5, Q6, Q7, Q8 and Q9, each naming <c>docs/devpost-feedback.md</c>.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> FeedbackChecklist(string checklist)
    {
        if (HasHeadingAtAnyLevel(checklist, FeedbackChecklistHeading) is { } heading)
        {
            return [heading];
        }

        string[] bullets = [.. SectionAtAnyLevel(checklist, FeedbackChecklistHeading).Where(static line => line.StartsWith("- ", StringComparison.Ordinal))];
        var failures = new List<string>();
        if (bullets.Length != UserQuestions.Count)
        {
            failures.Add($"{Digits(bullets.Length)} bullets under {FeedbackChecklistHeading}, {Digits(UserQuestions.Count)} required");
        }

        foreach (int question in UserQuestions)
        {
            string name = "Q" + Digits(question);
            string[] named = [.. bullets.Where(bullet => QuestionReference().Matches(bullet).Any(match => match.Groups[1].Value == Digits(question)))];
            if (named.Length == 0)
            {
                failures.Add("missing bullet: " + name);
            }
            else if (!named.Any(static bullet => bullet.Contains(FeedbackPath, StringComparison.Ordinal)))
            {
                failures.Add("bullet does not name " + FeedbackPath + ": " + name);
            }
        }

        return failures;
    }

    /// <summary>The fill-checklist-table check: seven rows owned by the user and none owned by the builder.</summary>
    /// <param name="feedback">The feedback text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> FeedbackFillTable(string feedback)
    {
        string[] lines = Lines(feedback);
        var failures = new List<string>();
        int user = lines.Count(static line => UserRow().IsMatch(line));
        int builder = lines.Count(static line => BuilderRow().IsMatch(line));
        if (user != UserQuestions.Count)
        {
            failures.Add($"{Digits(user)} user rows in the Fill checklist, {Digits(UserQuestions.Count)} required");
        }

        if (builder > 0)
        {
            failures.Add($"{Digits(builder)} builder rows left in the Fill checklist");
        }

        return failures;
    }

    /// <summary>
    /// The passage check. The passages are the changed blocks that replace a builder placeholder of the pre-fill file. A block needs an existing <c>results/</c> or <c>docs/</c> file and a label word or an explicit not-run
    /// statement, unless every line of it is one of the explicit statements. A quantity in a passage or in a block that only inserts lines needs a file in the same block.
    /// </summary>
    /// <param name="feedback">The feedback text.</param>
    /// <param name="preFill">The pre-fill feedback text.</param>
    /// <param name="exists">Whether a repository-relative file exists.</param>
    /// <returns>The failures: <c>passage without source: &lt;excerpt&gt;</c>, <c>source not found: &lt;path&gt;</c>, <c>number without source: &lt;quantity&gt;</c>.</returns>
    public static IReadOnlyList<string> FeedbackPassages(string feedback, string preFill, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var failures = new List<string>();
        LineHunk[] passages = [.. ContentHunks(feedback, preFill).Where(static hunk => hunk.Before.Any(static line => line.Contains("owner: builder", StringComparison.Ordinal)) || hunk.Before.All(static line => line.Trim().Length == 0))];
        if (!passages.Any(static hunk => hunk.Before.Any(static line => line.Contains("owner: builder", StringComparison.Ordinal))))
        {
            failures.Add("no filled passage found: no builder placeholder of the pre-fill file was replaced");
        }

        foreach (LineHunk hunk in passages)
        {
            string[] lines = [.. hunk.After.Where(static line => line.Trim().Length > 0)];
            if (lines.Length == 0)
            {
                continue;
            }

            string text = string.Join('\n', lines);
            string[] paths = [.. SourcePath().Matches(text).Select(static match => match.Value).Distinct(StringComparer.Ordinal)];
            failures.AddRange(paths.Where(path => !exists(path)).Select(static path => "source not found: " + path));
            string[] quantities = [.. Quantity().Matches(text).Select(static match => match.Value)];
            if (paths.Length == 0)
            {
                failures.AddRange(quantities.Select(static quantity => "number without source: " + quantity));
            }

            bool statements = lines.All(line => ExplicitStatements.Any(statement => line.StartsWith(statement, StringComparison.Ordinal)));
            bool labelled = StoryLabels.Any(label => text.Contains(label, StringComparison.Ordinal)) || NotRunPhrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal));
            if (!statements && (paths.Length == 0 || !labelled))
            {
                failures.Add("passage without source: " + lines[0][..Math.Min(ExcerptLength, lines[0].Length)]);
            }
        }

        return [.. failures.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The baseline check: without a baseline run file the Q4 passage reads <c>No Qwen3 8B baseline run was made.</c>; with one it may not.</summary>
    /// <param name="feedback">The feedback text.</param>
    /// <param name="baselineRunExists">Whether a run file with the <c>-baseline</c> suffix exists.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> FeedbackBaseline(string feedback, bool baselineRunExists)
    {
        bool said = feedback.Contains(NoBaselineSentence, StringComparison.Ordinal);

        return baselineRunExists
            ? (said ? ["the baseline passage says no baseline run was made although a baseline run file exists"] : [])
            : (said ? [] : ["missing sentence: " + NoBaselineSentence]);
    }

    /// <summary>The credits check: without a <c>User statement on credits:</c> list item in the build log, every added Q5 line that mentions the credits must say <c>not reported</c>, and the not-reported sentence must be present.</summary>
    /// <param name="feedback">The feedback text.</param>
    /// <param name="preFill">The pre-fill feedback text.</param>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> FeedbackCredits(string feedback, string preFill, string buildLog)
    {
        HashSet<string> old = [.. Lines(preFill)];
        string[] added = [.. QuestionLines(feedback, 5).Where(line => !old.Contains(line) && line.Contains("credit", StringComparison.OrdinalIgnoreCase))];
        bool statement = HasCreditsStatement(buildLog);
        var failures = new List<string>();
        if (!statement && added.Any(static line => !line.Contains("not reported", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("credits answer without a user statement");
        }

        if (!statement && !added.Any(static line => line.Contains(CreditsNotReported, StringComparison.Ordinal)))
        {
            failures.Add("missing sentence: " + CreditsNotReported);
        }

        return failures;
    }

    /// <summary>Whether the build log holds a user statement on credits: a Markdown list item <c>- User statement on credits:</c> whose value (the text up to the first semicolon or the line end) is not <c>none</c> or <c>none recorded</c>.</summary>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <returns>True when a statement is recorded.</returns>
    public static bool HasCreditsStatement(string buildLog) =>
        Lines(buildLog).Any(static line =>
        {
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("- " + CreditsStatementMarker, StringComparison.Ordinal))
            {
                return false;
            }

            string value = trimmed[(2 + CreditsStatementMarker.Length)..];
            int end = value.IndexOf(';', StringComparison.Ordinal);
            value = (end < 0 ? value : value[..end]).Trim().TrimEnd('.').Trim();

            return value.Length > 0 && !value.Equals("none", StringComparison.OrdinalIgnoreCase) && !value.Equals("none recorded", StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>The design-change check: every sentence the builder changed outside the filled passages and the Fill checklist needs a <c>Feedback change:</c> entry in the build log.</summary>
    /// <param name="feedback">The feedback text.</param>
    /// <param name="preFill">The pre-fill feedback text.</param>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <returns>The failures, <c>unlogged feedback change: &lt;excerpt&gt;</c>.</returns>
    public static IReadOnlyList<string> FeedbackChanges(string feedback, string preFill, string buildLog)
    {
        // The paragraph above the first question describes the file, not a design statement of an answer: it needs no entry.
        int firstQuestion = Array.FindIndex(Lines(preFill), static line => QuestionHeading().IsMatch(line));

        return UnloggedChanges(
            ContentHunks(feedback, preFill).Where(hunk => hunk.BeforeStart >= firstQuestion && hunk.Before.Any(static line => line.Trim().Length > 0) && !hunk.Before.Any(static line => line.Contains("owner: builder", StringComparison.Ordinal))),
            buildLog,
            FeedbackChangeMarker,
            "unlogged feedback change");
    }

    /// <summary>The ranking check: no ranking claim stands and every comparison sentence of a changed block names a model ID and a tier.</summary>
    /// <param name="feedback">The feedback text.</param>
    /// <param name="preFill">The pre-fill feedback text.</param>
    /// <returns>The failures: <c>ranking claim: &lt;text&gt;</c> and <c>comparison without model ID or tier: &lt;excerpt&gt;</c>.</returns>
    public static IReadOnlyList<string> FeedbackComparisons(string feedback, string preFill)
    {
        var failures = new List<string>();
        failures.AddRange(FeedbackRankingClaim().Matches(feedback).Select(static match => "ranking claim: " + match.Value.Trim()));
        string[] tiers = ["local", "nvidia", "nebius", "cloudgpu", "Token Factory", "build.nvidia.com", "laptop"];
        IEnumerable<string> sentences = ContentHunks(feedback, preFill).SelectMany(static hunk => hunk.After).Where(static line => line.Trim().Length > 0 && !line.StartsWith('|')).SelectMany(SentencesOfLine);
        foreach (string sentence in sentences.Where(static sentence => ComparisonWord().IsMatch(sentence)))
        {
            if (ModelIdsNamed(sentence).Count == 0 || !tiers.Any(tier => sentence.Contains(tier, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add("comparison without model ID or tier: " + sentence[..Math.Min(ExcerptLength, sentence.Length)]);
            }
        }

        return failures;
    }

    /// <summary>All checks of the feedback file against its pre-fill version.</summary>
    /// <param name="feedback">The feedback text.</param>
    /// <param name="preFill">The pre-fill feedback text.</param>
    /// <param name="checklist">The checklist text.</param>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <param name="exists">Whether a repository-relative file exists.</param>
    /// <param name="baselineRunExists">Whether a baseline run file exists.</param>
    /// <returns>The failures of every check.</returns>
    public static IReadOnlyList<string> FeedbackChecks(string feedback, string preFill, string checklist, string buildLog, Func<string, bool> exists, bool baselineRunExists) =>
    [
        .. FeedbackMarkers(feedback, preFill),
        .. FeedbackChecklist(checklist),
        .. FeedbackFillTable(feedback),
        .. FeedbackPassages(feedback, preFill, exists),
        .. FeedbackBaseline(feedback, baselineRunExists),
        .. FeedbackCredits(feedback, preFill, buildLog),
        .. FeedbackChanges(feedback, preFill, buildLog),
        .. FeedbackComparisons(feedback, preFill),
    ];

    // The hunks outside the Fill checklist section (from its heading to the end of the file), which the builder edits by removing rows.
    private static IEnumerable<LineHunk> ContentHunks(string feedback, string preFill)
    {
        int checklistAt = Array.FindIndex(Lines(preFill), static line => line.StartsWith('#') && line.EndsWith(FillChecklistHeading, StringComparison.Ordinal));
        int checklistNow = Array.FindIndex(Lines(feedback), static line => line.StartsWith('#') && line.EndsWith(FillChecklistHeading, StringComparison.Ordinal));

        return LineHunks(preFill, feedback)
            .Where(hunk => (checklistAt < 0 || hunk.BeforeStart < checklistAt) && (checklistNow < 0 || hunk.AfterStart < checklistNow));
    }

    // The lines of the section of a question (## Q<n>. up to the next ## heading).
    private static string[] QuestionLines(string feedback, int question)
    {
        string[] lines = Lines(feedback);
        int start = Array.FindIndex(lines, line => QuestionHeading().Match(line) is { Success: true } match && match.Groups[1].Value == Digits(question));
        if (start < 0)
        {
            return [];
        }

        int end = Array.FindIndex(lines, start + 1, static line => line.StartsWith("## ", StringComparison.Ordinal));

        return end < 0 ? lines[(start + 1)..] : lines[(start + 1)..end];
    }
}