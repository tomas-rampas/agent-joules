using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>A markdown table read from a section: the header cells and the data rows, the cells trimmed and the escaped pipes (<c>\|</c>) restored.</summary>
/// <param name="Header">The header cells.</param>
/// <param name="Rows">The data rows, the separator row left out.</param>
public sealed record PipeTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>The index of the first header cell that contains the text (ignoring case), or -1.</summary>
    /// <param name="name">The header text to look for.</param>
    /// <returns>The index, or -1.</returns>
    public int ColumnOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (int index = 0; index < Header.Count; index++)
        {
            if (Header[index].Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>The rules of AJ-158 over <c>docs/devpost-checklist.md</c>: the submission order, the timeline offsets, the cut-off plan and the builder-action sentences.</summary>
public static partial class EvidenceChecks
{
    /// <summary>The deadline string the checklist must hold.</summary>
    public const string DeadlineText = "2026-10-30 10:00 PDT (17:00 UTC)";

    /// <summary>The number of user actions in the submission order.</summary>
    public const int SubmissionStepCount = 12;

    /// <summary>The fewest hours between the last timeline target and the deadline.</summary>
    public const double MinimumBufferHours = 3;

    /// <summary>The fewest UTC/PDT pairs the checklist holds.</summary>
    public const int MinimumTimelinePairs = 6;

    /// <summary>The seconds of the minimal video cut (shots 1, 3, 6 and 9 of the demo script).</summary>
    public const int MinimalCutSeconds = 65;

    /// <summary>The instant of the deadline, 2026-10-30 17:00 UTC.</summary>
    public static readonly DateTime DeadlineUtc = new(2026, 10, 30, 17, 0, 0, DateTimeKind.Utc);

    /// <summary>The words that identify each of the 12 submission steps, in the order of the form.</summary>
    public static IReadOnlyList<string> SubmissionStepKeys { get; } =
        ["project name", "elevator pitch", "About the project", "Built-with", "Try it out", "YouTube", "track", "team members", "repository URL", "feedback", "submit", "after submission"];

    /// <summary>The missing items the cut-off plan has a row for, each with the word its first cell holds.</summary>
    public static IReadOnlyList<string> CutOffItems { get; } = ["cloudgpu", "local", "nvidia", "nebius", "judge", "Pages", "video", "feedback"];

    private static readonly string[] BoxNumbers = ["21", "22", "23"];

    private static readonly string[] CloudGpuRowTexts = ["cloudgpu | not run", "None besides Token Factory."];

    private const string UserMarker = "(user)";
    private const string CutOffSection = "Cut-off plan";
    private const string RegenerateCommand = "report --in results/ --out docs/report/ --update-readme --render-content";
    private const string VerifyCommand = "report --verify-readme";

    /// <summary>The lines of a section that are table rows, as a table; null when the section has no table.</summary>
    /// <param name="section">The section body.</param>
    /// <returns>The table or null.</returns>
    public static PipeTable? TableOf(string? section)
    {
        if (section is null)
        {
            return null;
        }

        List<IReadOnlyList<string>> rows = [];
        foreach (string line in DocumentChecks.Lines(section).Where(static line => line.TrimStart().StartsWith('|')))
        {
            IReadOnlyList<string> cells = CellsOf(line);
            if (cells.Count > 0 && !cells.All(static cell => SeparatorCell().IsMatch(cell)))
            {
                rows.Add(cells);
            }
        }

        return rows.Count == 0 ? null : new PipeTable(rows[0], rows.Skip(1).ToArray());
    }

    /// <summary>The cells of one table row: split on pipes that are not escaped, trimmed, the escapes restored.</summary>
    /// <param name="line">The row.</param>
    /// <returns>The cells.</returns>
    public static IReadOnlyList<string> CellsOf(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^1];
        }

        return [.. UnescapedPipe().Split(trimmed).Select(static cell => cell.Replace("\\|", "|", StringComparison.Ordinal).Trim())];
    }

    /// <summary>The submission-order rule: 12 numbered items, each ending with <c>(user)</c>, in the order of the form.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> SubmissionOrderViolations(string checklist)
    {
        ArgumentNullException.ThrowIfNull(checklist);

        string? section = SectionOf(checklist, "Submission order");
        if (section is null)
        {
            return ["missing section: Submission order"];
        }

        var violations = new List<string>();
        Match[] items = [.. DocumentChecks.Lines(section).Select(static line => NumberedItem().Match(line)).Where(static match => match.Success)];
        if (items.Length != SubmissionStepCount)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"submission order has {items.Length} items, expected {SubmissionStepCount}"));
        }

        for (int index = 0; index < items.Length; index++)
        {
            string number = items[index].Groups["n"].Value;
            string text = items[index].Groups["text"].Value.TrimEnd();
            if (!text.EndsWith(UserMarker, StringComparison.Ordinal))
            {
                violations.Add("step not marked as user action: " + number);
            }

            if (index < SubmissionStepKeys.Count && !text.Contains(SubmissionStepKeys[index], StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"step {number} is out of order, expected: {SubmissionStepKeys[index]}");
            }
        }

        return violations;
    }

    /// <summary>The timeline rule: every UTC/PDT pair differs by 7 hours, six or more pairs exist, the last target is 3 hours or more before the deadline and the buffer and CET statements are present.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> TimelineViolations(string checklist)
    {
        ArgumentNullException.ThrowIfNull(checklist);

        string? section = SectionOf(checklist, "Timeline");
        if (section is null)
        {
            return ["missing section: Timeline"];
        }

        var violations = new List<string>();
        int pairLines = DocumentChecks.Lines(checklist).Count(static line => TimelinePair().IsMatch(line));
        if (pairLines < MinimumTimelinePairs)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"timeline has {pairLines} UTC/PDT lines, expected {MinimumTimelinePairs} or more"));
        }

        DateTime? last = null;
        DateOnly? carried = null;
        foreach (string line in DocumentChecks.Lines(section))
        {
            foreach (Match pair in TimelinePair().Matches(line).Cast<Match>())
            {
                int utc = (Minutes(pair, "uh", "um"));
                int pdt = (Minutes(pair, "ph", "pm"));
                if (((utc - pdt + 1440) % 1440) != 420)
                {
                    violations.Add("offset must be 7 hours: " + pair.Value);
                }

                Match date = IsoDate().Matches(line).Cast<Match>().LastOrDefault(candidate => candidate.Index < pair.Index) ?? Match.Empty;
                if (date.Success)
                {
                    carried = DateOnly.ParseExact(date.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                // Only the pair of the deadline itself is left out: a line that merely mentions the deadline is still a target.
                DateOnly day = carried ?? DateOnly.FromDateTime(DeadlineUtc);
                DateTime at = day.ToDateTime(new TimeOnly(int.Parse(pair.Groups["uh"].Value, CultureInfo.InvariantCulture), int.Parse(pair.Groups["um"].Value, CultureInfo.InvariantCulture)));
                if (at != DeadlineUtc)
                {
                    last = last is null || at > last ? at : last;
                }
            }
        }

        if (last is null)
        {
            violations.Add("timeline has no target");
        }
        else if ((DeadlineUtc - DateTime.SpecifyKind(last.Value, DateTimeKind.Utc)).TotalHours < MinimumBufferHours)
        {
            violations.Add("last target is less than 3 h before the deadline: " + last.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
        }

        if (!section.Contains("3 hours", StringComparison.Ordinal))
        {
            violations.Add("missing buffer statement: 3 hours");
        }

        if (!section.Contains("UTC+1", StringComparison.Ordinal))
        {
            violations.Add("missing CET note: UTC+1");
        }

        return violations;
    }

    /// <summary>The cut-off rule: a row for every missing item, each with files, a regeneration command, a fallback story and unchecked boxes, plus the rules of the nebius, cloudgpu and video rows.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <param name="demoScript">The demo script text for the minimal-cut sum, or null to skip that sum.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> CutOffViolations(string checklist, string? demoScript = null)
    {
        ArgumentNullException.ThrowIfNull(checklist);

        string? section = SectionOf(checklist, CutOffSection);
        if (section is null)
        {
            return ["missing section: Cut-off plan"];
        }

        PipeTable? table = TableOf(section);
        if (table is null)
        {
            return ["Cut-off plan has no table"];
        }

        var violations = new List<string>();
        int files = table.ColumnOf("files");
        int command = table.ColumnOf("regeneration");
        int story = table.ColumnOf("fallback");
        int boxes = table.ColumnOf("unchecked");
        foreach ((string name, int column) in new[] { ("files", files), ("regeneration", command), ("fallback", story), ("unchecked", boxes) }.Where(static pair => pair.Item2 < 0))
        {
            violations.Add("Cut-off plan table lacks column: " + name);
        }

        if (violations.Count > 0)
        {
            return violations;
        }

        foreach (string item in CutOffItems)
        {
            IReadOnlyList<string>? row = table.Rows.FirstOrDefault(candidate => candidate.Count > 0 && candidate[0].TrimStart('*', '`', ' ').StartsWith(item, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                violations.Add("no cut-off row: " + item);
                continue;
            }

            if (Blank(row, files))
            {
                violations.Add("no files: " + item);
            }

            if (Blank(row, command))
            {
                violations.Add("no regeneration command: " + item);
            }

            if (Blank(row, story))
            {
                violations.Add("no fallback story: " + item);
            }

            if (Blank(row, boxes))
            {
                violations.Add("no unchecked boxes: " + item);
            }

            ItemRules(violations, item, row, command, boxes);
        }

        if (demoScript is not null && MinimalCutSeconds != ShotSeconds(demoScript, [1, 3, 6, 9]))
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"minimal cut of shots 1, 3, 6 and 9 sums to {ShotSeconds(demoScript, [1, 3, 6, 9])} s, expected {MinimalCutSeconds} s"));
        }

        return violations;
    }

    /// <summary>The builder-action and deadline rules: the deadline string is present and no sentence says the builder pushed, submitted, posted or created the release.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> SubmissionStatementViolations(string checklist)
    {
        ArgumentNullException.ThrowIfNull(checklist);

        var violations = new List<string>();
        if (!checklist.Contains(DeadlineText, StringComparison.Ordinal))
        {
            violations.Add("missing deadline");
        }

        foreach (Match claim in BuilderAction().Matches(checklist).Cast<Match>())
        {
            violations.Add("builder action claimed: " + claim.Groups["verb"].Value.ToLowerInvariant());
        }

        return violations;
    }

    /// <summary>Every AJ-158 rule over the checklist of a folder (the minimal-cut sum when the demo script exists there).</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> SubmissionPlanViolations(string root)
    {
        string? checklist = ReadText(root, ChecklistPath);
        return checklist is null
            ? ["missing docs/devpost-checklist.md"]
            : [.. SubmissionOrderViolations(checklist), .. TimelineViolations(checklist), .. CutOffViolations(checklist, ReadText(root, "content/demo-script.md")), .. SubmissionStatementViolations(checklist)];
    }

    /// <summary>The sum of the durations of the numbered shots in the shot list of a demo script.</summary>
    /// <param name="script">The demo script text.</param>
    /// <param name="shots">The shot numbers.</param>
    /// <returns>The seconds.</returns>
    public static int ShotSeconds(string script, IReadOnlyCollection<int> shots)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(shots);

        int total = 0;
        foreach (string line in DocumentChecks.Lines(script).Where(static line => line.Length > 2 && line.StartsWith("| ", StringComparison.Ordinal) && char.IsAsciiDigit(line[2])))
        {
            string[] cells = line.Split('|');
            if (cells.Length > 3 && int.TryParse(cells[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int shot) && shots.Contains(shot)
                && int.TryParse(cells[3].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
            {
                total += seconds;
            }
        }

        return total;
    }

    private static void ItemRules(List<string> violations, string item, IReadOnlyList<string> row, int command, int boxes)
    {
        string text = string.Join(" | ", row);
        if (string.Equals(item, "nebius", StringComparison.Ordinal) && !text.Contains("BLOCKED", StringComparison.Ordinal))
        {
            violations.Add("nebius row lacks the BLOCKED rule");
        }

        if (string.Equals(item, "cloudgpu", StringComparison.Ordinal))
        {
            if (!Blank(row, command) && !(row[command].Contains(RegenerateCommand, StringComparison.Ordinal) && row[command].Contains(VerifyCommand, StringComparison.Ordinal)))
            {
                violations.Add("cloudgpu row lacks the regeneration commands");
            }

            foreach (string required in CloudGpuRowTexts.Where(required => !text.Contains(required, StringComparison.Ordinal)))
            {
                violations.Add("cloudgpu row lacks: " + required);
            }

            if (!Blank(row, boxes) && !BoxNumbers.All(box => row[boxes].Contains(box, StringComparison.Ordinal)))
            {
                violations.Add("cloudgpu row lacks the boxes 21, 22 and 23");
            }
        }

        if (string.Equals(item, "video", StringComparison.Ordinal) && !text.Contains("65", StringComparison.Ordinal))
        {
            violations.Add("video row lacks the 65 s minimal cut");
        }
    }

    private static bool Blank(IReadOnlyList<string> row, int column) =>
        column >= row.Count || string.IsNullOrWhiteSpace(row[column]) || string.Equals(row[column], "-", StringComparison.Ordinal);

    private static int Minutes(Match pair, string hours, string minutes) =>
        (int.Parse(pair.Groups[hours].Value, CultureInfo.InvariantCulture) * 60) + int.Parse(pair.Groups[minutes].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\s*(?<n>[0-9]+)\. (?<text>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedItem();

    [GeneratedRegex(@"(?<uh>[0-9]{2}):(?<um>[0-9]{2}) UTC \((?<ph>[0-9]{2}):(?<pm>[0-9]{2}) PDT\)", RegexOptions.CultureInvariant)]
    private static partial Regex TimelinePair();

    [GeneratedRegex(@"[0-9]{4}-[0-9]{2}-[0-9]{2}", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"builder (?<verb>pushed|submitted|posted|created the release)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BuilderAction();

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapedPipe();

    [GeneratedRegex(@"^:?-{3,}:?$", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorCell();
}