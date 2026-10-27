using System.Globalization;
using System.Text.RegularExpressions;

using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>The outcome of the push-boundary check.</summary>
/// <param name="Violations">The violations, empty on a pass.</param>
/// <param name="Ahead">The number of commits from the recorded reference to HEAD, or -1 when git could not count them.</param>
/// <param name="OriginHash">The hash the rule took for origin/main (the recorded reference when origin/main is not tracked).</param>
/// <param name="Output">The one-line report: the ahead count or the violation, with the note about an untracked origin/main.</param>
public sealed record BoundaryResult(IReadOnlyList<string> Violations, int Ahead, string OriginHash, string Output);

/// <summary>
/// The git-boundary rules of AJ-156: the subjects of the build-session commits, the push boundary (origin/main against the recorded reference), the clean tree before the review gates, the push command of the checklist,
/// the user-action labels of the checklist and the last line of a review verdict. A rule reads a repository folder or a text and returns its violations as text, an empty list being a pass.
/// No rule pushes, fetches or asks anything: the remote hash is read from the local tracking reference, and the live <c>ls-remote</c> call belongs to the step.
/// </summary>
public static partial class EvidenceChecks
{
    /// <summary>The build-start of the live repository: the HEAD hash recorded by M0 AJ-002 (the M0 base), the start of the history range and of the build-session commit count.</summary>
    public const string BuildStartHash = "aafaba934b50db74947090086bc32fb2441c2380";

    /// <summary>
    /// The recorded reference of the commit-count report: it names the previous milestone's exit commit (the M6 exit commit, the one on origin/main at the time of writing). The 2026-10-20 user decision (recorded under
    /// "User decisions after the M1 exit" in the build log) has the session publish main at every milestone exit. It feeds only <see cref="CommitsAhead"/> and <see cref="BoundaryCheck"/>;
    /// the live <c>ls-remote</c> reading does not compare with it (see <see cref="RemoteMainViolations"/>), so it does not turn red when main is published.
    /// </summary>
    public const string RemoteReferenceHash = "01df5cf";

    /// <summary>The label every command that publishes or creates a resource carries in the checklist.</summary>
    public const string UserActionLabel = "User action (the builder never runs this):";

    /// <summary>The label a command carries when the user delegated it to the session at the M7 exit (decision of 2026-10-27 12:11Z).</summary>
    public const string DelegatedUserActionLabel = DocumentChecks.DelegatedUserActionLabel;

    /// <summary>The line the review verdict ends with when the branch is approved.</summary>
    public const string ApprovedVerdict = "VERDICT: APPROVED";

    /// <summary>The question the check asks the user when the remote main moved; the step records it as output and never asks interactively.</summary>
    public const string MovedQuestion = "remote main moved: did you push since the recorded reference?";

    // The commands of the checklist that the builder never runs: they publish or create a resource.
    private static readonly string[] UserActionCommands = ["git push", "gh release create", "gh api", "nebius ", "az ", "gcloud ", "aws "];

    /// <summary>
    /// The subjects of the history check after the amendment recorded for box 13: the story form (several story ids for a pooled commit, zero or more requirement ids) or a milestone subject.
    /// </summary>
    /// <param name="subject">The commit subject.</param>
    /// <returns>True when the subject is accepted.</returns>
    public static bool IsAcceptedSubject(string subject) => StoryForm().IsMatch(subject) || MilestoneForm().IsMatch(subject);

    /// <summary>The literal form of criterion 1: one story id, then zero or more requirement ids, then a colon; a milestone exit subject is excluded by the criterion itself.</summary>
    /// <param name="subject">The commit subject.</param>
    /// <returns>True when the subject is accepted by the literal form.</returns>
    public static bool IsLiteralSubject(string subject) => LiteralForm().IsMatch(subject) || MilestoneExitForm().IsMatch(subject);

    /// <summary>The non-merge commit subjects after a recorded build-start.</summary>
    /// <param name="root">The repository folder.</param>
    /// <param name="buildStart">The recorded build-start hash.</param>
    /// <returns>The subjects, or null when git could not list the range.</returns>
    public static IReadOnlyList<string>? SubjectsAfter(string root, string buildStart) => CommitsAfter(root, buildStart)?.ConvertAll(static commit => commit.Subject);

    // The hash and subject of each non-merge commit after the build-start; null when git fails or the listed count differs from rev-list's (a commit with an empty subject must not vanish).
    private static List<(string Hash, string Subject)>? CommitsAfter(string root, string buildStart)
    {
        GitResult result = GitRunner.Run(root, "log", "--no-merges", "--format=%H%x09%s", "--end-of-options", buildStart + "..HEAD");
        GitResult count = GitRunner.Run(root, "rev-list", "--no-merges", "--count", "--end-of-options", buildStart + "..HEAD");
        if (result.ExitCode != 0 || count.ExitCode != 0 || !int.TryParse(count.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int expected))
        {
            return null;
        }

        List<(string Hash, string Subject)> commits = [];
        foreach (string line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            commits.Add(tab < 0 ? (line, string.Empty) : (line[..tab], line[(tab + 1)..].Trim()));
        }

        return commits.Count == expected ? commits : null;
    }

    /// <summary>The history check: every non-merge commit after the build-start has an accepted subject.</summary>
    /// <param name="root">The repository folder.</param>
    /// <param name="buildStart">The recorded build-start hash.</param>
    /// <returns>One <c>commit without story id: &lt;subject&gt;</c> for each commit that has none; <c>cannot list the history</c> when git fails.</returns>
    public static IReadOnlyList<string> HistoryViolations(string root, string buildStart)
    {
        List<(string Hash, string Subject)>? commits = CommitsAfter(root, buildStart);
        return commits is null
            ? ["cannot list the history"]
            : [.. commits.Where(static commit => !IsAcceptedSubject(commit.Subject)).Select(static commit => "commit without story id: " + (commit.Subject.Length == 0 ? commit.Hash : commit.Subject))];
    }

    /// <summary>The count of criterion 1 read literally: the subjects that the literal form (and the milestone exit exclusion) does not accept.</summary>
    /// <param name="root">The repository folder.</param>
    /// <param name="buildStart">The recorded build-start hash.</param>
    /// <returns>The count, or -1 when git could not list the range.</returns>
    public static int LiteralHistoryCount(string root, string buildStart) => HistoryCounts(root, buildStart).Literal;

    /// <summary>Both counts of the history check: the literal one and the one of the amended form.</summary>
    /// <param name="root">The repository folder.</param>
    /// <param name="buildStart">The recorded build-start hash.</param>
    /// <returns>The literal count and the amended count, -1 for both when git could not list the range.</returns>
    public static (int Literal, int Amended) HistoryCounts(string root, string buildStart)
    {
        IReadOnlyList<string>? subjects = SubjectsAfter(root, buildStart);
        return subjects is null ? (-1, -1) : (subjects.Count(static subject => !IsLiteralSubject(subject)), subjects.Count(static subject => !IsAcceptedSubject(subject)));
    }

    /// <summary>The number of commits (merges included) from a commit to HEAD.</summary>
    /// <param name="root">The repository folder.</param>
    /// <param name="from">The commit the range starts after.</param>
    /// <returns>The count, or -1 when git fails.</returns>
    public static int CommitsAhead(string root, string from)
    {
        GitResult result = GitRunner.Run(root, "rev-list", "--count", "--end-of-options", from + "..HEAD");
        return result.ExitCode == 0 && int.TryParse(result.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? count : -1;
    }

    /// <summary>
    /// The push-boundary check: origin/main (the local tracking reference; the recorded reference, stated in the output, when origin/main is not tracked) must be the recorded reference, and the ahead count is
    /// <c>rev-list --count &lt;recorded&gt;..HEAD</c>. It fails with <c>origin/main moved: the builder pushed</c> when origin/main equals HEAD or is anything but the recorded reference.
    /// </summary>
    /// <param name="root">The repository folder.</param>
    /// <param name="recorded">The recorded reference hash (full or abbreviated).</param>
    /// <returns>The result with the ahead count and the one-line report.</returns>
    public static BoundaryResult BoundaryCheck(string root, string recorded)
    {
        string recordedFull = ResolveCommit(root, recorded) ?? recorded;
        int ahead = CommitsAhead(root, recorded);
        string? origin = ResolveCommit(root, "origin/main");
        string note = string.Empty;
        if (origin is null)
        {
            origin = recordedFull;
            note = " (origin/main is not tracked, the recorded reference " + recorded + " is used)";
        }

        string? head = ResolveCommit(root, "HEAD");
        List<string> violations = [];
        if (!string.Equals(origin, recordedFull, StringComparison.Ordinal) || string.Equals(origin, head, StringComparison.Ordinal))
        {
            violations.Add("origin/main moved: the builder pushed");
        }

        if (ahead < 0)
        {
            violations.Add("cannot count the commits ahead");
        }

        string output = violations.Count == 0 ? ahead.ToString(CultureInfo.InvariantCulture) + " commits ahead" + note : violations[0] + note;
        return new BoundaryResult(violations, ahead, origin, output);
    }

    /// <summary>
    /// The ls-remote check: the hash the remote main prints must be a commit this checkout holds and that HEAD descends from (<c>git merge-base --is-ancestor</c>), so nothing exists on the remote that the checkout lacks.
    /// This holds before and after the exit publication of main, unlike a comparison with a recorded hash.
    /// </summary>
    /// <param name="root">The repository folder whose HEAD is the reference.</param>
    /// <param name="lsRemoteOutput">What <c>git ls-remote origin refs/heads/main</c> printed.</param>
    /// <returns><c>remote main moved</c> unless exactly one line was printed, its ref is <c>refs/heads/main</c>, its hash is a full hash present in the repository and an ancestor of (or equal to) HEAD.</returns>
    public static IReadOnlyList<string> RemoteMainViolations(string root, string lsRemoteOutput)
    {
        ArgumentNullException.ThrowIfNull(lsRemoteOutput);
        string[] lines = lsRemoteOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1)
        {
            return ["remote main moved"];
        }

        string[] fields = lines[0].Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 || !string.Equals(fields[1], "refs/heads/main", StringComparison.Ordinal) || !HexHash().IsMatch(fields[0]))
        {
            return ["remote main moved"];
        }

        // A hash absent locally makes git exit 128, a commit outside HEAD's history exit 1: both are a moved remote.
        return GitRunner.Run(root, "merge-base", "--is-ancestor", fields[0] + "^{commit}", "HEAD").ExitCode == 0 ? [] : ["remote main moved"];
    }

    /// <summary>The hash a one-line <c>ls-remote</c> output prints, for the scenario output.</summary>
    /// <param name="lsRemoteOutput">What <c>git ls-remote origin refs/heads/main</c> printed.</param>
    /// <returns>The first field of the first line, or an empty string.</returns>
    public static string RemoteMainHash(string lsRemoteOutput)
    {
        ArgumentNullException.ThrowIfNull(lsRemoteOutput);
        string? line = lsRemoteOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return line?.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    }

    /// <summary>The clean-tree check that precedes the review gates.</summary>
    /// <param name="root">The repository folder.</param>
    /// <returns><c>uncommitted changes</c> when <c>git status --porcelain</c> prints anything.</returns>
    public static IReadOnlyList<string> CleanTreeViolations(string root)
    {
        GitResult status = GitRunner.Run(root, "status", "--porcelain");
        return status.ExitCode != 0 ? ["cannot read the status"] : status.Stdout.Trim().Length > 0 ? ["uncommitted changes"] : [];
    }

    /// <summary>The checklist check: at least one fenced block holds the push command.</summary>
    /// <param name="checklist">The text of docs/devpost-checklist.md.</param>
    /// <returns><c>missing git push block</c> when no fenced block holds it.</returns>
    public static IReadOnlyList<string> ChecklistPushViolations(string checklist) =>
        FencedBlocksWithLabel(checklist).Any(static block => block.Body.Contains("git push", StringComparison.Ordinal)) ? [] : ["missing git push block"];

    /// <summary>
    /// Criterion 6, the checklist half: every fenced block that holds a push, a release or a cloud-resource command is one the line before it labels <see cref="UserActionLabel"/> or <see cref="DelegatedUserActionLabel"/>; a bare line outside any fence that starts with such a command is flagged too.
    /// </summary>
    /// <param name="checklist">The text of docs/devpost-checklist.md.</param>
    /// <returns>One <c>unlabelled command: &lt;command&gt;</c> for each such block without the label and for each such bare line.</returns>
    public static IReadOnlyList<string> UserActionLabelViolations(string checklist)
    {
        List<string> violations = [];
        foreach ((string preceding, string body) in FencedBlocksWithLabel(checklist))
        {
            string? line = body.Split('\n').Select(static l => l.TrimStart()).FirstOrDefault(static l => StartsWithUserActionCommand(l));
            if (line is not null && !string.Equals(preceding, UserActionLabel, StringComparison.Ordinal) && !string.Equals(preceding, DelegatedUserActionLabel, StringComparison.Ordinal))
            {
                violations.Add("unlabelled command: " + line.Trim());
            }
        }

        // A bare bullet or line outside any fence that starts with such a command (after a bullet, a prompt or a backtick) cannot carry the label.
        bool fenced = false;
        foreach (string raw in checklist.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }
            else if (!fenced)
            {
                string command = line.TrimStart().TrimStart('-', '*', '>', '$', '`', ' ');
                if (StartsWithUserActionCommand(command))
                {
                    violations.Add("unlabelled command: " + command.Trim());
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Criterion 6, the build-log half: no line outside a fenced block shows the orchestrator or the builder running a push or a release (a line that only names the command in a description is not a run).
    /// A line is a run when it starts with <c>gh release create</c> (after a bullet, a prompt or a backtick) or says <c>the orchestrator ran</c> or <c>the builder ran</c> followed by one of the two commands.
    /// </summary>
    /// <param name="buildLog">The text of docs/build-log.md.</param>
    /// <returns>One <c>command run by the builder: &lt;line&gt;</c> for each such line.</returns>
    public static IReadOnlyList<string> BuildLogRunViolations(string buildLog)
    {
        List<string> violations = [];
        bool fenced = false;
        foreach (string raw in buildLog.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }
            else if (!fenced && (ReleaseRunForm().IsMatch(line) || RanForm().IsMatch(line)))
            {
                violations.Add("command run by the builder: " + line.Trim());
            }
        }

        return violations;
    }

    /// <summary>The verdict check: the last non-empty line of a review output is <see cref="ApprovedVerdict"/> (an earlier verdict line does not count).</summary>
    /// <param name="reviewOutput">The text the review agent printed.</param>
    /// <returns><c>last line is not VERDICT: APPROVED</c> otherwise.</returns>
    public static IReadOnlyList<string> VerdictLastLineViolations(string reviewOutput)
    {
        ArgumentNullException.ThrowIfNull(reviewOutput);
        string? last = reviewOutput.Split('\n').Select(static l => l.Trim()).LastOrDefault(static l => l.Length > 0);
        return string.Equals(last, ApprovedVerdict, StringComparison.Ordinal) ? [] : ["last line is not " + ApprovedVerdict];
    }

    private static bool StartsWithUserActionCommand(string line) => UserActionCommands.Any(command => line.StartsWith(command, StringComparison.Ordinal));

    private static string? ResolveCommit(string root, string name)
    {
        GitResult result = GitRunner.Run(root, "rev-parse", "--verify", "--quiet", name + "^{commit}");
        return result.ExitCode == 0 ? result.Stdout.Trim() : null;
    }

    // The fenced blocks of a text, each with the last non-empty line before its opening fence and its body.
    private static List<(string Preceding, string Body)> FencedBlocksWithLabel(string text)
    {
        List<(string, string)> blocks = [];
        string preceding = string.Empty;
        string last = string.Empty;
        List<string>? body = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (body is null)
                {
                    preceding = last;
                    body = [];
                }
                else
                {
                    blocks.Add((preceding, string.Join('\n', body)));
                    body = null;
                    last = string.Empty;
                }
            }
            else if (body is not null)
            {
                body.Add(line);
            }
            else if (line.Trim().Length > 0)
            {
                last = line.Trim();
            }
        }

        return blocks;
    }

    [GeneratedRegex(@"^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexHash();

    [GeneratedRegex(@"^AJ-[0-9]{3}( AJ-[0-9]{3})*( (REQ|EDGE)-[0-9]{3})*:", RegexOptions.CultureInvariant)]
    private static partial Regex StoryForm();

    [GeneratedRegex(@"^M[0-9] (exit( addendum)?|kickoff|exit review fixes|exit peer review)(:| )", RegexOptions.CultureInvariant)]
    private static partial Regex MilestoneForm();

    [GeneratedRegex(@"^AJ-[0-9]{3}( (REQ|EDGE)-[0-9]{3})*:", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralForm();

    [GeneratedRegex(@"^M[0-9] exit( addendum)?:", RegexOptions.CultureInvariant)]
    private static partial Regex MilestoneExitForm();

    [GeneratedRegex(@"^\s*([-*>]\s*)?(\$\s*)?`?gh release create\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseRunForm();

    [GeneratedRegex(@"^- .*(orchestrator|builder) ran `?(git push|gh release create)", RegexOptions.CultureInvariant)]
    private static partial Regex RanForm();
}