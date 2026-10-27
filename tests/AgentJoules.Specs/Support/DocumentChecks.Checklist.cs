using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The checks of AJ-138 (REQ-028): the source and verification section, the 13 rule items with their evidence class, the deadline, the conflicts, the fields-to-artifacts list, the demo URL line and the Built-with tags
/// of <c>docs/devpost-checklist.md</c>. Pure functions over the checklist text; the existence of an artifact is asked of a delegate.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The repository-relative path of the checklist.</summary>
    public const string ChecklistPath = "docs/devpost-checklist.md";

    /// <summary>The address of the hackathon page.</summary>
    public const string HackathonUrl = "https://nebiusglobalaihackathon.devpost.com/";

    /// <summary>The deadline as the checklist writes it.</summary>
    public const string Deadline = "2026-10-30 10:00 PDT (17:00 UTC)";

    /// <summary>The address of the report page the demo URL names.</summary>
    public const string ReportPageUrl = "https://tomas-rampas.github.io/agent-joules/report/";

    /// <summary>The line that starts the demo URL statement.</summary>
    public const string DemoUrlLabel = "Demo URL:";

    /// <summary>The most Built-with tags the form takes.</summary>
    public const int TagLimit = 25;

    /// <summary>The number of rule items of prompt section 3.</summary>
    public const int RuleItemCount = 13;

    /// <summary>The earliest date a verification can carry (the first fetch of the rules page).</summary>
    public static readonly DateOnly EarliestVerification = new(2026, 10, 17);

    private static readonly string[] EvidenceClasses = ["(evidence: verified)", "(evidence: user-stated, not verified)", "(evidence: aggregator, unverified)"];

    private static readonly (string Topic, string Keyword)[] RuleTopics =
    [
        ("eligibility", "eligib"),
        ("required or judged technologies", "technolog"),
        ("submission fields", "submission field"),
        ("video length limit", "video"),
        ("license requirement", "license"),
        ("repository visibility requirement", "visibility"),
        ("judging criteria and weights", "judging"),
        ("tracks and selection rule", "track"),
        ("Builder Program or credit offers", "credit"),
        ("demo or test URL", "demo"),
        ("required NVIDIA model families", "model famil"),
        ("required Nebius product", "Nebius product"),
        ("deadline with time zone", "deadline"),
    ];

    private static readonly string[] RequiredTags = ["C#", ".NET", "Microsoft Agent Framework", "NVIDIA Nemotron", "Nebius Token Factory", "NVML"];

    /// <summary>The fifteen fields the Fields-to-artifacts list names, each as a lower-case text the field name must contain.</summary>
    public static IReadOnlyList<string> RequiredFields { get; } =
    [
        "project name", "elevator pitch", "about the project", "built-with", "try it out", "youtube", "track", "team members",
        "repository url", "license", "readme", "nemotron usage", "token factory", "other nebius", "feedback",
    ];

    [GeneratedRegex(@"^\d+\. ", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedEntry();

    [GeneratedRegex(@"^None found on \d{4}-\d{2}-\d{2}\.?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NoneFound();

    [GeneratedRegex(@"Verified: (\d{4}-\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex VerifiedDate();

    [GeneratedRegex(@"^- \[[ xX]\] (?<field>.+?) -> (?<artifact>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldRow();

    [GeneratedRegex(@"(?<![\w/.-])(?:(?:docs|results|content|tasks|config|artifacts|src|tests)/[A-Za-z0-9_./*<>{}-]*[A-Za-z0-9_*>}]|[A-Za-z0-9_-]+\.(?:md|json|html|png|zip|svg|txt|yml|cs)\b|LICENSE\b)", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactPath();

    [GeneratedRegex(@"^Tags: (\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TagsLine();

    [GeneratedRegex(@"^Built[- ]with\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BuiltWithHeading();

    /// <summary>The source check: the section <c>Source and verification</c> holds the hackathon address and either <c>Verified: &lt;date&gt;</c> (2026-10-17 or later) or <c>not verified</c> with an HTTP reason.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures; empty when the section is complete.</returns>
    public static IReadOnlyList<string> ChecklistSource(string checklist)
    {
        if (HasHeadingAtAnyLevel(checklist, "Source and verification") is { } heading)
        {
            return [heading];
        }

        string section = string.Join('\n', SectionAtAnyLevel(checklist, "Source and verification"));
        var failures = new List<string>();
        if (!section.Contains(HackathonUrl, StringComparison.Ordinal))
        {
            failures.Add("missing source URL: " + HackathonUrl);
        }

        Match verified = VerifiedDate().Match(section);
        if (verified.Success)
        {
            if (!DateOnly.TryParseExact(verified.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) || date < EarliestVerification)
            {
                failures.Add("verification date before 2026-10-17: " + verified.Groups[1].Value);
            }
        }
        else if (!section.Contains("not verified", StringComparison.Ordinal))
        {
            failures.Add("no verification date and no 'not verified' statement");
        }
        else if (!section.Contains("HTTP", StringComparison.Ordinal))
        {
            failures.Add("'not verified' without the HTTP reason");
        }

        return failures;
    }

    /// <summary>The Source and verification lines a builder writes for the status the rules page answered.</summary>
    /// <param name="status">The HTTP status of the request.</param>
    /// <param name="fetched">The date of the request.</param>
    /// <returns>The lines of the section body.</returns>
    public static IReadOnlyList<string> SourceSectionFor(int status, DateOnly fetched) =>
        status == 200
            ? [$"- Source: {HackathonUrl}", $"- Verified: {fetched.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}. The rules page answered HTTP 200."]
            :
            [
                $"- Source: {HackathonUrl}",
                $"- not verified: the rules page answered HTTP {Digits(status)} to automated fetching on {fetched.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.",
                "- Please paste the Rules tab text into the chat so the rules can be verified by hand.",
            ];

    /// <summary>The check of a Source and verification section against the status the page answered: 200 needs <c>Verified: &lt;date&gt;</c>; any other status needs <c>HTTP &lt;status&gt;</c>, <c>not verified</c> and the request to paste the Rules tab text.</summary>
    /// <param name="sectionLines">The body lines of the section.</param>
    /// <param name="status">The HTTP status.</param>
    /// <returns>The failures; empty when the section matches the status.</returns>
    public static IReadOnlyList<string> SourceSectionMatchesStatus(IReadOnlyList<string> sectionLines, int status)
    {
        string section = string.Join('\n', sectionLines);
        var failures = new List<string>();
        if (status == 200)
        {
            if (!VerifiedDate().IsMatch(section))
            {
                failures.Add("HTTP 200 without a Verified date");
            }

            return failures;
        }

        string code = "HTTP " + Digits(status);
        if (!section.Contains(code, StringComparison.Ordinal))
        {
            failures.Add("missing status: " + code);
        }

        if (!section.Contains("not verified", StringComparison.Ordinal))
        {
            failures.Add("missing text: not verified");
        }

        if (VerifiedDate().IsMatch(section))
        {
            failures.Add("a Verified date although the page answered " + code);
        }

        if (!section.Contains("paste", StringComparison.OrdinalIgnoreCase) || !section.Contains("Rules tab", StringComparison.Ordinal))
        {
            failures.Add("does not ask the user to paste the Rules tab text");
        }

        return failures;
    }

    /// <summary>The rules check: the section <c>Rules</c> holds at least 13 items, each a list line ending with one of the three evidence classes, and no line that calls itself verified and not verified.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures; <c>no evidence class: &lt;item&gt;</c> names the item of a line without a class.</returns>
    public static IReadOnlyList<string> ChecklistRules(string checklist)
    {
        if (HasHeadingAtAnyLevel(checklist, "Rules") is { } heading)
        {
            return [heading];
        }

        string[] items = [.. SectionAtAnyLevel(checklist, "Rules").Where(static line => line.StartsWith("- ", StringComparison.Ordinal))];
        var failures = new List<string>();
        foreach (string item in items)
        {
            string name = item[2..];
            int colon = name.IndexOf(':', StringComparison.Ordinal);
            name = colon < 0 ? name : name[..colon];
            if (!EvidenceClasses.Any(@class => item.Contains(@class, StringComparison.Ordinal)))
            {
                failures.Add("no evidence class: " + name.Trim());
            }
            else if (item.Contains("(evidence: verified)", StringComparison.Ordinal) && item.Replace("(evidence: verified)", string.Empty, StringComparison.Ordinal).Contains("not verified", StringComparison.Ordinal))
            {
                failures.Add("verified and not verified: " + name.Trim());
            }
        }

        string[] names = [.. items.Select(static item => { string text = item[2..]; int at = text.IndexOf(':', StringComparison.Ordinal); return at < 0 ? text : text[..at]; })];
        failures.AddRange(RuleTopics.Where(topic => !names.Any(name => name.Contains(topic.Keyword, StringComparison.OrdinalIgnoreCase))).Select(static topic => "rule item missing: " + topic.Topic));

        int classed = items.Count(item => EvidenceClasses.Any(@class => item.Contains(@class, StringComparison.Ordinal)));
        if (classed < RuleItemCount)
        {
            failures.Add($"{Digits(classed)} rule items with an evidence class, {Digits(RuleItemCount)} required");
        }

        return failures;
    }

    /// <summary>The deadline check: the checklist writes the deadline exactly as <c>2026-10-30 10:00 PDT (17:00 UTC)</c>.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ChecklistDeadline(string checklist) =>
        checklist.Contains(Deadline, StringComparison.Ordinal) ? [] : ["missing deadline: " + Deadline];

    /// <summary>The conflicts check: the section <c>Conflicts</c> lists a conflict with its resolution or says <c>None found on &lt;date&gt;</c>.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ChecklistConflicts(string checklist)
    {
        if (HasHeadingAtAnyLevel(checklist, "Conflicts") is { } heading)
        {
            return [heading];
        }

        string[] body = [.. SectionAtAnyLevel(checklist, "Conflicts").Where(static line => line.Trim().Length > 0)];

        if (body.Length == 0)
        {
            return ["empty Conflicts section"];
        }

        string[] entries = [.. body.Where(static line => NumberedEntry().IsMatch(line))];
        if (entries.Length == 0)
        {
            return body.Any(static line => NoneFound().IsMatch(line)) ? [] : ["Conflicts section has no numbered entry and no None-found line"];
        }

        return [.. entries.Where(static entry => !entry.Contains("resolution", StringComparison.OrdinalIgnoreCase) && !entry.Contains("resolved", StringComparison.OrdinalIgnoreCase)).Select(static entry => "conflict without resolution: " + entry[..Math.Min(ExcerptLength, entry.Length)])];
    }

    /// <summary>The artifact paths a field row names: the repository paths of its artifact side, without URLs, backticks, anchors and directory names.</summary>
    /// <param name="artifact">The text after <c> -&gt; </c>.</param>
    /// <returns>The distinct paths.</returns>
    public static IReadOnlyList<string> ArtifactPaths(string artifact)
    {
        string plain = UrlPattern().Replace(artifact, string.Empty).Replace("`", string.Empty, StringComparison.Ordinal);

        return
        [
            .. ArtifactPath().Matches(plain)
                .Select(static match => match.Value.TrimEnd('.', ',', ';', ':', ')'))
                .Where(static path => !path.EndsWith('/') && !path.Contains('*', StringComparison.Ordinal) && !path.Contains('<', StringComparison.Ordinal) && !path.Contains('{', StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>The fields check: the section <c>Fields to artifacts</c> is a checkbox list <c>- [ ] field -&gt; artifact</c> with the fifteen fields, the About the project row names <c>docs/devpost-story.md</c>, and every artifact path exists or its row is marked <c>(user)</c>.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <param name="exists">Whether a repository-relative file exists.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ChecklistFields(string checklist, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (HasHeadingAtAnyLevel(checklist, "Fields to artifacts") is { } heading)
        {
            return [heading];
        }

        var rows = SectionAtAnyLevel(checklist, "Fields to artifacts").Select(static line => FieldRow().Match(line)).Where(static match => match.Success).ToList();
        var failures = new List<string>();
        foreach (string field in RequiredFields)
        {
            if (!rows.Any(row => row.Groups["field"].Value.Contains(field, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add("missing field: " + field);
            }
        }

        Match? about = rows.FirstOrDefault(static row => row.Groups["field"].Value.Contains("about the project", StringComparison.OrdinalIgnoreCase));
        if (about is not null && !about.Groups["artifact"].Value.Contains("docs/devpost-story.md", StringComparison.Ordinal))
        {
            failures.Add("About the project does not name docs/devpost-story.md");
        }

        foreach (Match row in rows.Where(static row => !row.Groups["artifact"].Value.Contains("(user)", StringComparison.Ordinal)))
        {
            failures.AddRange(ArtifactPaths(row.Groups["artifact"].Value).Where(path => !exists(path)).Select(static path => "artifact not found: " + path));
        }

        return [.. failures.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The demo URL check: one line starts <c>Demo URL:</c>, names the report page (or reads <c>BLOCKED</c>) and gives a reason.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The failures; <c>missing line: Demo URL:</c> when no line starts with the label.</returns>
    public static IReadOnlyList<string> ChecklistDemoUrl(string checklist)
    {
        string[] lines = [.. Lines(checklist).Where(static line => line.TrimStart().StartsWith(DemoUrlLabel, StringComparison.Ordinal))];
        if (lines.Length == 0)
        {
            return ["missing line: " + DemoUrlLabel];
        }

        string line = lines[0].Trim();
        string rest = line[DemoUrlLabel.Length..].Trim();
        var failures = new List<string>();
        if (rest.StartsWith("BLOCKED", StringComparison.Ordinal))
        {
            return rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3 ? failures : ["Demo URL: BLOCKED without the missing decision"];
        }

        if (!line.Contains(ReportPageUrl, StringComparison.Ordinal))
        {
            failures.Add("Demo URL does not name " + ReportPageUrl);
        }

        string reason = rest.Replace(ReportPageUrl, string.Empty, StringComparison.Ordinal).Trim();
        if (reason.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4)
        {
            failures.Add("Demo URL line gives no reason");
        }

        return failures;
    }

    /// <summary>The Built-with tags: the list items of the section whose heading starts with <c>Built-with</c>.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The tags in order; empty when the section is missing.</returns>
    public static IReadOnlyList<string> BuiltWithTags(string checklist)
    {
        string? heading = HeadingsOf(Lines(checklist)).Select(static entry => entry.Text).FirstOrDefault(static text => BuiltWithHeading().IsMatch(text));

        return heading is null ? [] : [.. SectionAtAnyLevel(checklist, heading).Where(static line => line.StartsWith("- ", StringComparison.Ordinal)).Select(static line => line[2..].Trim())];
    }

    /// <summary>The Built-with check: at most 25 tags, the six required ones present, <c>Nebius AI Cloud</c> only with a cloudgpu run file, and the line <c>Tags: &lt;n&gt;</c> equal to the count.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <param name="cloudGpuRan">Whether a completed or partial cloudgpu run file exists.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ChecklistBuiltWith(string checklist, bool cloudGpuRan)
    {
        IReadOnlyList<string> tags = BuiltWithTags(checklist);
        if (tags.Count == 0)
        {
            return ["missing heading: Built-with"];
        }

        var failures = new List<string>();
        if (tags.Count > TagLimit)
        {
            failures.Add($"{Digits(tags.Count)} tags exceed {Digits(TagLimit)}");
        }

        failures.AddRange(RequiredTags.Where(tag => !tags.Contains(tag, StringComparer.Ordinal)).Select(static tag => "missing tag: " + tag));
        if (!cloudGpuRan && tags.Contains("Nebius AI Cloud", StringComparer.Ordinal))
        {
            failures.Add("tag without evidence: Nebius AI Cloud");
        }

        Match[] counts = [.. Lines(checklist).Select(static line => TagsLine().Match(line)).Where(static match => match.Success)];
        if (counts.Length == 0)
        {
            failures.Add("missing line: Tags:");
        }
        else if (!counts.Any(match => match.Groups[1].Value == Digits(tags.Count)))
        {
            failures.Add($"Tags line says {counts[0].Groups[1].Value}, the list has {Digits(tags.Count)}");
        }

        return failures;
    }

    /// <summary>The checklist text with the Built-with list grown to a number of tags and the line <c>Tags: &lt;n&gt;</c> updated.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <param name="count">The number of tags.</param>
    /// <returns>The new text.</returns>
    public static string WithTagCount(string checklist, int count)
    {
        string heading = HeadingsOf(Lines(checklist)).Select(static entry => entry.Text).First(static text => BuiltWithHeading().IsMatch(text));
        IReadOnlyList<string> body = SectionAtAnyLevel(checklist, heading);
        string[] kept = [.. body.Where(static line => !line.StartsWith("- ", StringComparison.Ordinal) && !TagsLine().IsMatch(line))];
        IEnumerable<string> tags = Enumerable.Range(1, count).Select(index => "- tag " + Digits(index));

        return WithSectionBody(checklist, heading, [.. kept, .. tags, string.Empty, "Tags: " + Digits(count)]);
    }

    /// <summary>The checklist text with the Built-with list holding one more tag.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <param name="tag">The tag.</param>
    /// <returns>The new text; the list gets the tag when it lacks it, and the line <c>Tags: &lt;n&gt;</c> is updated.</returns>
    public static string WithTag(string checklist, string tag)
    {
        IReadOnlyList<string> tags = BuiltWithTags(checklist);
        if (tags.Contains(tag, StringComparer.Ordinal))
        {
            return checklist;
        }

        string heading = HeadingsOf(Lines(checklist)).Select(static entry => entry.Text).First(static text => BuiltWithHeading().IsMatch(text));
        IReadOnlyList<string> body = SectionAtAnyLevel(checklist, heading);
        string[] list = [.. body.Where(static line => line.StartsWith("- ", StringComparison.Ordinal)), "- " + tag];
        string[] rest = [.. body.Where(static line => !line.StartsWith("- ", StringComparison.Ordinal) && !TagsLine().IsMatch(line))];

        return WithSectionBody(checklist, heading, [.. list, string.Empty, "Tags: " + Digits(list.Length), .. rest]);
    }
}