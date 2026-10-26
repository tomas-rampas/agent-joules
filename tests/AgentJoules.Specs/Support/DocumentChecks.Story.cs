using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The checks of AJ-140 (REQ-028): the filled <c>docs/devpost-story.md</c>. The nine filled passages are found by the section they sit in and the words they hold (the story has no marker left once it is filled), each is held
/// to a results file or document that exists, a unit and one label word, the cloud passage to the cloudgpu state, the comparison passage to the cost and ranking rules, and every other changed sentence to a
/// <c>Story change:</c> entry of the build log. Pure functions over strings.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The repository-relative path of the story.</summary>
    public const string StoryPath = "docs/devpost-story.md";

    /// <summary>The sentence the cloud passage opens with when the cloudgpu tier did not run.</summary>
    public const string NoCloudStorySentence = "The cloudgpu tier did not run in this submission";

    /// <summary>The paragraph the story ends with.</summary>
    public const string StoryEnding = "A LinkedIn post and a long-form article follow, with every number traced to a results file.";

    /// <summary>The marker of a build-log entry for a changed story sentence.</summary>
    public const string StoryChangeMarker = "Story change:";

    private const string HowBuiltHeading = "How I built it";
    private const string ChallengesHeading = "Challenges I ran into";
    private const string AccomplishmentsHeading = "Accomplishments that I'm proud of";
    private const string LearnedHeading = "What I learned";
    private const string ResearchParagraph = "Research set two expectations";
    private const string OpenSourceParagraph = "The code is open source";
    private const string MechanismParagraph = "NVML offers two paths";
    private const string ChecklistHeadingText = "Fill-after-build";

    private static readonly string[] Currencies = ["EUR", "USD"];

    private static readonly string[] StoryLabels = ["measured", "estimated", "model-judged", "not-reported", "N/A"];

    private static readonly string[] PreFillCloudParagraphs = ["On the Nebius AI Cloud instance, one NVIDIA", "On the Nebius AI Cloud H100 the energy counter"];

    [GeneratedRegex(@"(?<![\w.])(?:(?:USD|EUR)\s\d[\d,]*(?:\.\d+)?|\d[\d,]*(?:\.\d+)?\s?(?:kJ|J|W|ms|s|%|percent|USD|EUR|GB|MB|MHz)(?![A-Za-z0-9]))", RegexOptions.CultureInvariant)]
    private static partial Regex Quantity();

    [GeneratedRegex(@"(?:results|docs)/[A-Za-z0-9_./-]*[A-Za-z0-9_]", RegexOptions.CultureInvariant)]
    private static partial Regex SourcePath();

    [GeneratedRegex(@"\b(?:greener|better|faster than|cheaper than)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StoryRankingWord();

    [GeneratedRegex(@"\b(?:average|averaged|mean|combined|merged|pooled|blended)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MergingWord();

    [GeneratedRegex(@"\b(?:FP\d+|BF16|INT\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PrecisionToken();

    [GeneratedRegex(@"\b(?:together|combined|in total|sum)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TotalingWord();

    [GeneratedRegex(@"(?<![\w.])\d+(?:\.\d+)? ?J(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex JouleQuantity();

    [GeneratedRegex(@"[.?!](?=\s)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\((?:[^()]*\s)?(?:results|docs)/[^()]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesisedFile();

    [GeneratedRegex(@"\([^()]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Parenthesised();

    [GeneratedRegex(@"\b[A-Z][a-z]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex CapitalisedWord();

    [GeneratedRegex(@"^\W*\w+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingWord();

    [GeneratedRegex(@"\b(?:laptop|local)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LaptopWord();

    [GeneratedRegex(@"\b(?:cloud|cloudgpu|H100|instance)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CloudWord();

    /// <summary>The placeholder check: no <c>[[FILL:</c> is left.</summary>
    /// <param name="text">The document text.</param>
    /// <returns>The failures, <c>leftover placeholder: &lt;n&gt;</c>.</returns>
    public static IReadOnlyList<string> LeftoverPlaceholders(string text)
    {
        int count = CountOf(text, "[[FILL:");

        return count == 0 ? [] : ["leftover placeholder: " + Digits(count)];
    }

    /// <summary>The checklist check: no heading of the story holds <c>Fill-after-build</c>.</summary>
    /// <param name="story">The story text.</param>
    /// <returns>The failures, <c>checklist section not deleted</c>.</returns>
    public static IReadOnlyList<string> StoryChecklistDeleted(string story) =>
        HeadingsOf(Lines(story)).Any(static entry => entry.Text.Contains(ChecklistHeadingText, StringComparison.OrdinalIgnoreCase)) ? ["checklist section not deleted"] : [];

    /// <summary>The ending check: the file ends with the paragraph that announces the LinkedIn post and the article.</summary>
    /// <param name="story">The story text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> StoryEndsWithParagraph(string story) =>
        story.TrimEnd().EndsWith(StoryEnding, StringComparison.Ordinal) ? [] : ["the story does not end with: " + StoryEnding];

    /// <summary>The lines of the nine filled passages, found by section and words: the agent-names line of <c>How I built it</c>, every line of <c>Challenges I ran into</c> but the research sentence, every line of the Accomplishments section but the open-source line, and the calibration line of <c>What I learned</c>.</summary>
    /// <param name="story">The story text.</param>
    /// <returns>The lines in document order.</returns>
    public static IReadOnlyList<string> StoryPassageLines(string story)
    {
        static IEnumerable<string> Filled(IReadOnlyList<string> section) => section.Where(static line => line.Trim().Length > 0);

        return
        [
            .. Filled(SectionAtAnyLevel(story, HowBuiltHeading)).Where(static line => line.Contains("Planner", StringComparison.Ordinal)),
            .. Filled(SectionAtAnyLevel(story, ChallengesHeading)).Where(static line => !line.StartsWith(ResearchParagraph, StringComparison.Ordinal)),
            .. Filled(SectionAtAnyLevel(story, AccomplishmentsHeading)).Where(static line => !line.StartsWith(OpenSourceParagraph, StringComparison.Ordinal)),
            .. CalibrationLines(story),
        ];
    }

    private static IEnumerable<string> CalibrationLines(string story) =>
        SectionAtAnyLevel(story, LearnedHeading).Where(static line => !line.StartsWith(MechanismParagraph, StringComparison.Ordinal)
            && (line.Contains("relative difference", StringComparison.OrdinalIgnoreCase) || line.Contains("relativeDifference", StringComparison.Ordinal)));

    /// <summary>The source check of the filled passages: each names a <c>results/</c> or <c>docs/</c> file that exists, and a passage with a quantity (a number with a unit) holds a label word.</summary>
    /// <param name="story">The story text.</param>
    /// <param name="exists">Whether a repository-relative file exists.</param>
    /// <returns>The failures: <c>number without source: &lt;quantity&gt;</c>, <c>source not found: &lt;path&gt;</c>, <c>passage without source: &lt;excerpt&gt;</c>, <c>number without label: &lt;quantity&gt;</c>.</returns>
    public static IReadOnlyList<string> StorySources(string story, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var failures = new List<string>();
        foreach (string line in StoryPassageLines(story))
        {
            string[] paths = [.. SourcePath().Matches(line).Select(static match => match.Value).Distinct(StringComparer.Ordinal)];
            string[] quantities = [.. Quantity().Matches(line).Select(static match => match.Value)];
            if (paths.Length == 0)
            {
                failures.AddRange(quantities.Select(static quantity => "number without source: " + quantity));
                failures.Add("passage without source: " + line[..Math.Min(ExcerptLength, line.Length)]);
            }

            failures.AddRange(paths.Where(path => !exists(path)).Select(static path => "source not found: " + path));
            if (quantities.Length > 0 && !StoryLabels.Any(label => line.Contains(label, StringComparison.Ordinal)))
            {
                failures.AddRange(quantities.Select(static quantity => "number without label: " + quantity));
            }
        }

        return [.. failures.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The merged-tiers check: no sentence about both the laptop (or the local tier) and the cloud instance averages, pools, combines or merges them, and none gives them one joule figure together
    /// (<c>together</c>, <c>combined</c>, <c>in total</c> or <c>sum</c> with a quantity in J), unless it says they never are.
    /// </summary>
    /// <param name="story">The story text.</param>
    /// <returns>The failures, <c>merged tiers: &lt;excerpt&gt;</c>.</returns>
    public static IReadOnlyList<string> StoryMergedTiers(string story) =>
    [
        .. Lines(story).SelectMany(SentencesOfLine)
            .Where(static sentence => LaptopWord().IsMatch(sentence)
                && CloudWord().IsMatch(sentence)
                && (MergingWord().IsMatch(sentence) || (TotalingWord().IsMatch(sentence) && JouleQuantity().IsMatch(sentence)))
                && !sentence.Contains("never", StringComparison.OrdinalIgnoreCase)
                && !sentence.Contains("separate", StringComparison.OrdinalIgnoreCase)
                && !sentence.Contains(" not ", StringComparison.OrdinalIgnoreCase))
            .Select(static sentence => "merged tiers: " + sentence[..Math.Min(ExcerptLength, sentence.Length)]),
    ];

    /// <summary>
    /// The sentence-count check of the first passages (the fill mapping): passage 1 (the agent names) is one sentence, passage 2 (the counter and method) one sentence, passage 3 (the NIM outcome) two or three; passage 5 (the local
    /// median) names its file in parentheses. Sentences are counted by the terminators <c>. </c>, <c>? </c> and <c>! </c> plus the end.
    /// </summary>
    /// <param name="story">The story text.</param>
    /// <returns>The failures, <c>passage &lt;n&gt; has &lt;k&gt; sentences</c> and <c>passage 5 names no file in parentheses</c>.</returns>
    public static IReadOnlyList<string> StorySentenceCounts(string story)
    {
        IReadOnlyList<string> passages = StoryPassageLines(story);
        var failures = new List<string>();
        (int Number, int Min, int Max)[] limits = [(1, 1, 1), (2, 1, 1), (3, 2, 3)];
        foreach ((int number, int min, int max) in limits)
        {
            if (passages.Count < number)
            {
                continue;
            }

            int count = SentenceEnd().Count(passages[number - 1].Trim()) + 1;
            if (count < min || count > max)
            {
                failures.Add("passage " + Digits(number) + " has " + Digits(count) + " sentences");
            }
        }

        if (passages.Count >= 5 && !ParenthesisedFile().IsMatch(passages[4]))
        {
            failures.Add("passage 5 names no file in parentheses");
        }

        return failures;
    }

    /// <summary>The agent-names check (fill mapping row 1): the capitalised names in passage 1 are the distinct <c>steps.agent</c> values of the latest local run file.</summary>
    /// <param name="story">The story text.</param>
    /// <param name="runFileAgents">The distinct agent names of the latest local run file.</param>
    /// <returns>The failures, <c>agent names differ from the run file: &lt;names&gt;</c> (names only in the story or only in the file).</returns>
    public static IReadOnlyList<string> StoryAgents(string story, IEnumerable<string> runFileAgents)
    {
        ArgumentNullException.ThrowIfNull(runFileAgents);
        IReadOnlyList<string> passages = StoryPassageLines(story);
        string? passage = passages.Count > 0 ? passages[0] : null;
        if (passage is null)
        {
            return ["missing passage 1: the agent names"];
        }

        string[] ignored = ["Microsoft", "Agent", "Framework", "NVIDIA", "Nebius", "NVML", "NIM"];
        string outside = Parenthesised().Replace(passage, " ");
        string[] stated = [.. SentencesOfLine(outside)
            .SelectMany(static sentence => CapitalisedWord().Matches(LeadingWord().Replace(sentence, string.Empty)).Select(static match => match.Value))
            .Where(word => !ignored.Contains(word, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)];
        string[] actual = [.. runFileAgents.Distinct(StringComparer.Ordinal)];
        string[] differ = [.. stated.Except(actual, StringComparer.Ordinal).Concat(actual.Except(stated, StringComparer.Ordinal)).Order(StringComparer.Ordinal)];

        return differ.Length == 0 ? [] : ["agent names differ from the run file: " + string.Join(", ", differ)];
    }

    /// <summary>
    /// The cloud-passage check. With a cloudgpu run file one passage names the file and holds the preset, the driver, the precision, the repeats and the observed cost, apart from the laptop file. Without one the story says
    /// <c>The cloudgpu tier did not run in this submission</c> with a reason, and no other sentence names the Nebius AI Cloud instance or the L40S.
    /// </summary>
    /// <param name="story">The story text.</param>
    /// <param name="cloudGpuRan">Whether a completed or partial cloudgpu run file exists.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> StoryCloud(string story, bool cloudGpuRan)
    {
        string[] lines = [.. Lines(story).Where(static line => line.Trim().Length > 0)];
        var failures = new List<string>();
        if (cloudGpuRan)
        {
            if (story.Contains(NoCloudStorySentence, StringComparison.Ordinal))
            {
                failures.Add("the story says the cloudgpu tier did not run although a cloudgpu run file exists");
            }

            string? cloud = lines.FirstOrDefault(static line => line.Contains("-cloudgpu.json", StringComparison.Ordinal));
            if (cloud is null)
            {
                failures.Add("missing cloud passage: no line names a cloudgpu results file");
                return failures;
            }

            foreach (string word in new[] { "preset", "driver", "repeat", "USD" })
            {
                if (!cloud.Contains(word, StringComparison.Ordinal))
                {
                    failures.Add("cloud passage lacks: " + word);
                }
            }

            if (!PrecisionToken().IsMatch(cloud))
            {
                failures.Add("cloud passage lacks: precision");
            }

            if (cloud.Contains("-local.json", StringComparison.Ordinal))
            {
                failures.Add("merged tiers: the cloud passage names the local file");
            }

            return failures;
        }

        string? absent = lines.FirstOrDefault(static line => line.Contains(NoCloudStorySentence, StringComparison.Ordinal));
        if (absent is null)
        {
            return ["missing sentence: " + NoCloudStorySentence];
        }

        string reason = absent[(absent.IndexOf(NoCloudStorySentence, StringComparison.Ordinal) + NoCloudStorySentence.Length)..].Trim(' ', '.', ':', ',', ';');
        if (reason.Length < 8)
        {
            failures.Add("cloud passage without a reason");
        }

        failures.AddRange(
            lines.Where(line => line != absent).SelectMany(SentencesOfLine)
                .Where(static sentence => sentence.Contains("Nebius AI Cloud", StringComparison.Ordinal) || sentence.Contains("L40S", StringComparison.Ordinal))
                .Select(static sentence => "cloud claim without cloudgpu run: " + sentence[..Math.Min(ExcerptLength, sentence.Length)]));

        return failures;
    }

    /// <summary>
    /// The comparison-passage check: the passage that names the nebius or nvidia file gives a cost in EUR and in USD, <c>N/A</c> for a tier without a price, a model ID per tier (two at least), ends with
    /// <c>on this workload</c> and holds no ranking word (<c>greener</c>, <c>better</c>, <c>faster than</c>, <c>cheaper than</c>).
    /// </summary>
    /// <param name="story">The story text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> StoryComparison(string story)
    {
        string? passage = SectionAtAnyLevel(story, AccomplishmentsHeading)
            .FirstOrDefault(static line => line.Contains("-nebius.json", StringComparison.Ordinal) || line.Contains("-nvidia.json", StringComparison.Ordinal) || line.Contains("costByCurrency", StringComparison.Ordinal));
        if (passage is null)
        {
            return ["missing comparison passage"];
        }

        var failures = new List<string>();
        failures.AddRange(Currencies.Where(currency => !passage.Contains(currency, StringComparison.Ordinal)).Select(static currency => "comparison lacks currency: " + currency));
        if (!passage.Contains("N/A", StringComparison.Ordinal))
        {
            failures.Add("comparison lacks N/A for the tier without a price");
        }

        if (ModelIdsNamed(passage).Count < 2)
        {
            failures.Add("comparison names fewer than two model IDs");
        }

        if (!passage.TrimEnd().TrimEnd('.').EndsWith("on this workload", StringComparison.Ordinal))
        {
            failures.Add("comparison does not end with: on this workload");
        }

        failures.AddRange(StoryRankingWord().Matches(passage).Select(static match => "ranking word: " + match.Value));

        return failures;
    }

    /// <summary>The calibration-passage check: the passage quotes the relative difference with its label (<c>measured</c>) or states <c>N/A</c>.</summary>
    /// <param name="story">The story text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> StoryCalibration(string story)
    {
        string? passage = CalibrationLines(story).FirstOrDefault();
        if (passage is null)
        {
            return ["missing calibration passage"];
        }

        return passage.Contains("measured", StringComparison.Ordinal) || passage.Contains("N/A", StringComparison.Ordinal) ? [] : ["calibration passage without a label"];
    }

    /// <summary>The change check: every line that differs from the pre-fill story outside the filled passages and the deleted checklist needs a <c>Story change:</c> entry in the build log.</summary>
    /// <param name="preFill">The story at the last pre-fill commit.</param>
    /// <param name="story">The story under test.</param>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <returns>The failures, <c>unlogged story change: &lt;excerpt&gt;</c>.</returns>
    public static IReadOnlyList<string> StoryChanges(string preFill, string story, string buildLog)
    {
        string[] pre = Lines(preFill);
        int checklistAt = Array.FindIndex(pre, static line => line.StartsWith('#') && line.Contains(ChecklistHeadingText, StringComparison.OrdinalIgnoreCase));
        IEnumerable<LineHunk> changed = LineHunks(preFill, story).Where(hunk => !IsFilledOrDeleted(hunk, checklistAt));

        return UnloggedChanges(changed, buildLog, StoryChangeMarker, "unlogged story change");
    }

    private static bool IsFilledOrDeleted(LineHunk hunk, int checklistAt)
    {
        string[] before = [.. hunk.Before.Where(static line => line.Trim().Length > 0)];
        string[] after = [.. hunk.After.Where(static line => line.Trim().Length > 0)];
        if (before.Length == 0 && after.Length == 0)
        {
            return true;
        }

        return before.Any(static line => line.Contains("[[FILL", StringComparison.Ordinal))
            || (checklistAt >= 0 && hunk.BeforeStart >= checklistAt)
            || (before.Length > 0 && before.All(line => PreFillCloudParagraphs.Any(start => line.StartsWith(start, StringComparison.Ordinal))));
    }

    /// <summary>All checks of the story that need no pre-fill version.</summary>
    /// <param name="story">The story text.</param>
    /// <param name="exists">Whether a repository-relative file exists.</param>
    /// <param name="cloudGpuRan">Whether a completed or partial cloudgpu run file exists.</param>
    /// <returns>The failures of every check.</returns>
    public static IReadOnlyList<string> StoryChecks(string story, Func<string, bool> exists, bool cloudGpuRan) =>
    [
        .. LeftoverPlaceholders(story),
        .. StoryChecklistDeleted(story),
        .. StoryEndsWithParagraph(story),
        .. StorySources(story, exists),
        .. StoryMergedTiers(story),
        .. StorySentenceCounts(story),
        .. StoryCloud(story, cloudGpuRan),
        .. StoryComparison(story),
        .. StoryCalibration(story),
    ];
}