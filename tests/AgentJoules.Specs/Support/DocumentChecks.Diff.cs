using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>A run of lines that differ between two texts: the lines of the first text and the lines of the second that replace them (either side may be empty).</summary>
/// <param name="Before">The lines of the first text.</param>
/// <param name="After">The lines of the second text.</param>
/// <param name="BeforeStart">The zero-based index of the first line of <paramref name="Before"/> (the insertion point when it is empty).</param>
/// <param name="AfterStart">The zero-based index of the first line of <paramref name="After"/> (the insertion point when it is empty).</param>
public sealed record LineHunk(IReadOnlyList<string> Before, IReadOnlyList<string> After, int BeforeStart, int AfterStart);

/// <summary>
/// The line diff and the change-log rule shared by the story check (AJ-140, <c>Story change:</c>) and the feedback check (AJ-142, <c>Feedback change:</c>). A pure function over two texts: the longest common subsequence of
/// their lines, the runs between matched lines as hunks, and the sentences a hunk changes matched against the entries of the build log.
/// </summary>
public static partial class DocumentChecks
{
    private const int FragmentMinimum = 12;
    private const int ExcerptLength = 60;
    private const int KeyLength = 40;

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z\[""(`*])", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"^(?<marker>[^:]+): (?<before>.+?) -> (?<after>.+?)(?: \(fact: [^)]*\))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ChangeEntry();

    /// <summary>The hunks that turn the first text into the second.</summary>
    /// <param name="before">The first text.</param>
    /// <param name="after">The second text.</param>
    /// <returns>The hunks in order; empty when the texts are equal line by line.</returns>
    public static IReadOnlyList<LineHunk> LineHunks(string before, string after)
    {
        string[] first = Lines(before);
        string[] second = Lines(after);
        int[,] common = new int[first.Length + 1, second.Length + 1];
        for (int i = first.Length - 1; i >= 0; i--)
        {
            for (int j = second.Length - 1; j >= 0; j--)
            {
                common[i, j] = first[i] == second[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
            }
        }

        var hunks = new List<LineHunk>();
        int left = 0;
        int right = 0;
        int hunkLeft = 0;
        int hunkRight = 0;
        while (left < first.Length || right < second.Length)
        {
            if (left < first.Length && right < second.Length && first[left] == second[right])
            {
                if (hunkLeft < left || hunkRight < right)
                {
                    hunks.Add(new LineHunk(first[hunkLeft..left], second[hunkRight..right], hunkLeft, hunkRight));
                }

                left++;
                right++;
                hunkLeft = left;
                hunkRight = right;
            }
            else if (right >= second.Length || (left < first.Length && common[left + 1, right] >= common[left, right + 1]))
            {
                left++;
            }
            else
            {
                right++;
            }
        }

        if (hunkLeft < left || hunkRight < right)
        {
            hunks.Add(new LineHunk(first[hunkLeft..left], second[hunkRight..right], hunkLeft, hunkRight));
        }

        return hunks;
    }

    /// <summary>The sentences of a line: split after a full stop, question mark or exclamation mark that is followed by a capital letter, a bracket, a quote or a code span.</summary>
    /// <param name="line">One line.</param>
    /// <returns>The trimmed sentences, none empty.</returns>
    public static IReadOnlyList<string> SentencesOfLine(string line) =>
        [.. SentenceBreak().Split(line.Trim()).Select(static sentence => sentence.Trim()).Where(static sentence => sentence.Length > 0)];

    /// <summary>The sentences a hunk changes: those of one side that the other side does not hold.</summary>
    /// <param name="hunk">The hunk.</param>
    /// <returns>The changed sentences of both sides.</returns>
    public static IReadOnlyList<string> ChangedSentences(LineHunk hunk)
    {
        ArgumentNullException.ThrowIfNull(hunk);
        string[] removed = [.. hunk.Before.Where(static line => line.Trim().Length > 0).SelectMany(SentencesOfLine)];
        string[] added = [.. hunk.After.Where(static line => line.Trim().Length > 0).SelectMany(SentencesOfLine)];

        return [.. removed.Except(added, StringComparer.Ordinal).Concat(added.Except(removed, StringComparer.Ordinal))];
    }

    /// <summary>
    /// The change-log rule: every sentence a hunk changes needs an entry of the build log, a Markdown list item that starts with the label of the marker (<c>- Story change</c> or <c>- Feedback change</c>, the labelled
    /// variant <c>- Feedback change (&lt;label&gt;):</c> included) and holds either its before or its after part (<c>&lt;marker&gt; &lt;before&gt; -&gt; &lt;after&gt; (fact: &lt;file&gt;)</c>, the text between the first and the last double quote of the part)
    /// equal to the sentence, or holding it as one whole sentence, after trimming and whitespace normalisation. Either part is accepted for either kind of sentence, an added one or a removed one, as <c>EntryCovers</c> does.
    /// A key of the first 40 characters or an overlap of 12 characters is no longer acceptance: it only adds a hint to the failure message.
    /// </summary>
    /// <param name="hunks">The hunks that need an entry.</param>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <param name="marker">The entry label with its colon.</param>
    /// <param name="failure">The failure label, for example <c>unlogged story change</c>.</param>
    /// <returns>One failure per unlogged sentence, <c>&lt;failure&gt;: &lt;first 60 characters&gt;</c>.</returns>
    public static IReadOnlyList<string> UnloggedChanges(IEnumerable<LineHunk> hunks, string buildLog, string marker, string failure)
    {
        ArgumentNullException.ThrowIfNull(hunks);
        string label = "- " + marker.TrimEnd(':');
        string[] entries = [.. Lines(buildLog).Where(line => line.TrimStart().StartsWith(label, StringComparison.Ordinal)).Select(line => line.TrimStart()[2..])];
        var failures = new List<string>();
        foreach (string sentence in hunks.SelectMany(ChangedSentences))
        {
            if (!entries.Any(entry => EntryCovers(entry, sentence)))
            {
                string hint = entries.Any(entry => EntryOverlaps(entry, sentence)) ? " (an entry overlaps it but does not record this exact sentence)" : string.Empty;
                failures.Add(failure + ": " + sentence[..Math.Min(ExcerptLength, sentence.Length)] + hint);
            }
        }

        return failures;
    }

    /// <summary>The text with the lines of the changed block that a predicate picks replaced by one line.</summary>
    /// <param name="after">The changed text.</param>
    /// <param name="before">The text it is compared with.</param>
    /// <param name="pick">Chooses the hunk by its lines of the changed text.</param>
    /// <param name="replacement">The one replacement line.</param>
    /// <returns>The new text, or the changed text unchanged when no hunk is picked.</returns>
    public static string WithChangedBlockReplaced(string after, string before, Func<LineHunk, bool> pick, string replacement)
    {
        ArgumentNullException.ThrowIfNull(pick);
        LineHunk? hunk = LineHunks(before, after).FirstOrDefault(pick);
        if (hunk is null)
        {
            return after;
        }

        string[] lines = Lines(after);

        return string.Join('\n', [.. lines[..hunk.AfterStart], replacement, .. lines[(hunk.AfterStart + hunk.After.Count)..]]);
    }

    private static bool EntryCovers(string entry, string sentence)
    {
        string wanted = Squash(sentence);
        Match parts = ChangeEntry().Match(entry);
        if (!parts.Success)
        {
            return false;
        }

        // The entry records the sentence when its after part equals it, or its before part equals it (a removed sentence, or "-> unchanged ..." for one the paragraph change left as it was).
        return new[] { parts.Groups["before"].Value, parts.Groups["after"].Value }
            .Any(part => Squash(Unquote(part)) == wanted || SentencesOfLine(Unquote(part)).Any(each => Squash(each) == wanted));
    }

    // A weaker match, used only to make the failure message say what is near: the first 40 characters of the sentence, or a 12-character part of an entry, appear in the entry.
    private static bool EntryOverlaps(string entry, string sentence)
    {
        if (entry.Contains(sentence[..Math.Min(KeyLength, sentence.Length)], StringComparison.Ordinal))
        {
            return true;
        }

        Match parts = ChangeEntry().Match(entry);

        return parts.Success && new[] { parts.Groups["before"].Value, parts.Groups["after"].Value }
            .Select(static part => part.Trim().Trim('`', '"', '\''))
            .Any(part => part.Length >= FragmentMinimum && (sentence.Contains(part, StringComparison.Ordinal) || part.Contains(sentence, StringComparison.Ordinal)));
    }

    // The quoted text of an entry part: from the first double quote to the last one, or the part itself when it holds none (for example "Q1 ""sentence""" gives the sentence).
    private static string Unquote(string part)
    {
        int first = part.IndexOf('"', StringComparison.Ordinal);
        int last = part.LastIndexOf('"');

        return first >= 0 && last > first ? part[(first + 1)..last] : part;
    }

    private static string Squash(string text) => WhitespaceRun().Replace(text.Trim().Trim('`', '"', '\''), " ");

    /// <summary>Formats an integer with the invariant culture.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The digits.</returns>
    public static string Digits(int value) => value.ToString(CultureInfo.InvariantCulture);
}