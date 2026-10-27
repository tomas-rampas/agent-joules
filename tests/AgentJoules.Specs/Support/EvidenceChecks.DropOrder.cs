using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>One step row of the drop order table: the step id, the header of its table, its cells and its line in the section.</summary>
/// <param name="Id">The step id, for example <c>C4</c>.</param>
/// <param name="Header">The header cells of the table the row belongs to.</param>
/// <param name="Cells">The cells of the row.</param>
/// <param name="Line">The zero-based line in the section.</param>
public sealed record DropRow(string Id, IReadOnlyList<string> Header, IReadOnlyList<string> Cells, int Line)
{
    /// <summary>The text of the cell under the first header that contains the name, or null when the table has no such column.</summary>
    /// <param name="column">The header text to look for.</param>
    /// <returns>The cell text (empty for a short row), or null.</returns>
    public string? Cell(string column)
    {
        ArgumentNullException.ThrowIfNull(column);
        for (int index = 0; index < Header.Count; index++)
        {
            if (Header[index].Contains(column, StringComparison.OrdinalIgnoreCase))
            {
                return index < Cells.Count ? Cells[index] : string.Empty;
            }
        }

        return null;
    }

    /// <summary>All cells joined, as the text the never-dropped rule searches.</summary>
    public string Text => string.Join(" | ", Cells);
}

/// <summary>The rules of AJ-159 over <c>docs/build-log.md</c>: the drop order, the triggers, the risk links, the never-dropped list and the buffer.</summary>
public static partial class EvidenceChecks
{
    /// <summary>The step ids of the drop order in the order of plan.md section 3.4: Level 1 C1 to C9, then Level 2 D1 to D3.</summary>
    public static IReadOnlyList<string> DropSteps { get; } = ["C1", "C2", "C3", "C4", "C5", "C6", "C7", "C8", "C9", "D1", "D2", "D3"];

    /// <summary>
    /// The items the never-dropped list names, each with the words (all required, ignoring case) that identify it; the label is the text of the violation.
    /// </summary>
    public static IReadOnlyList<(string Label, string[] Words)> NeverDroppedItems { get; } =
    [
        ("nebius run file", ["nebius run file"]),
        ("local run file", ["local run file"]),
        ("schema validation", ["schema validation"]),
        ("build, format and test commands", ["build", "format", "test commands"]),
        ("secret scan", ["secret scan"]),
        ("README results block", ["README results block"]),
        ("checklist with the push command", ["checklist", "push command"]),
        ("story", ["the story (docs/devpost-story.md"]),
        ("feedback draft", ["feedback draft"]),
        ("demo script", ["demo script"]),
        ("peer-review-critic approval", ["peer-review-critic"]),
        ("user's video", ["the user's video"]),
        ("user's submission", ["the user's submission on Devpost"]),
        ("second cloudgpu repeat set", ["repeat set"]),
        ("three LinkedIn variants", ["LinkedIn"]),
    ];

    // The phrases that must not appear in a drop row: the never-dropped items that a drop row could name.
    private static readonly string[] NeverDroppedPhrases = ["secret scan", "schema validation", "nebius run file", "local run file", "results block", "demo script", "feedback draft", "peer-review-critic", "push command"];

    private static readonly string[] CapacityWords = ["M6", "55.25", "M7", "planning figures"];

    private const string DropSection = "Drop order";
    private const string Level3Boxes = "boxes 21, 22 and 23";

    /// <summary>The step rows of the Drop order section (C1 to C9, D1 to D3 and an L3 table row) with their lines.</summary>
    /// <param name="section">The section body.</param>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<DropRow> DropRows(string section)
    {
        ArgumentNullException.ThrowIfNull(section);

        var rows = new List<DropRow>();
        IReadOnlyList<string> header = [];
        string[] lines = DocumentChecks.Lines(section);
        for (int index = 0; index < lines.Length; index++)
        {
            if (!lines[index].TrimStart().StartsWith('|'))
            {
                continue;
            }

            IReadOnlyList<string> cells = CellsOf(lines[index]);
            if (cells.Count == 0 || cells.All(static cell => cell.Length > 0 && cell.All(static c => c is '-' or ':' or ' ')))
            {
                continue;
            }

            string id = cells[0].Trim('*', '`', ' ');
            if (string.Equals(id, "Step", StringComparison.OrdinalIgnoreCase))
            {
                header = cells;
            }
            else if (DropSteps.Contains(id, StringComparer.Ordinal) || string.Equals(id, "L3", StringComparison.Ordinal))
            {
                rows.Add(new DropRow(id, header, cells, index));
            }
        }

        return rows;
    }

    /// <summary>The order rule: the twelve step rows appear in the order of the plan, the boxes unticked column is none for all of them and Level 3, naming boxes 21, 22 and 23, comes after the last of them.</summary>
    /// <param name="buildLog">The build log text.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> DropOrderMonotonicViolations(string buildLog)
    {
        ArgumentNullException.ThrowIfNull(buildLog);

        string? section = SectionOf(buildLog, DropSection);
        if (section is null)
        {
            return ["missing section: Drop order"];
        }

        var violations = new List<string>();
        IReadOnlyList<DropRow> rows = DropRows(section);
        DropRow[] steps = [.. rows.Where(static row => DropSteps.Contains(row.Id, StringComparer.Ordinal))];
        violations.AddRange(DropSteps.Where(step => steps.All(row => row.Id != step)).Select(static step => "missing drop step: " + step));
        string[] expected = [.. DropSteps.Where(step => steps.Any(row => row.Id == step))];
        for (int index = 0; index < steps.Length && index < expected.Length; index++)
        {
            if (!string.Equals(steps[index].Id, expected[index], StringComparison.Ordinal))
            {
                violations.Add($"drop step out of order: {steps[index].Id} (expected: {expected[index]})");
                break;
            }
        }

        foreach (DropRow row in steps)
        {
            string? boxes = row.Cell("boxes unticked");
            if (boxes is null)
            {
                violations.Add("drop order table lacks column: boxes unticked");
                break;
            }

            if (!string.Equals(boxes.Trim('`', ' ', '*'), "none", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add("boxes unticked must be none: " + row.Id);
            }
        }

        (int line, string text)? level3 = Level3Entry(section, rows);
        if (level3 is null)
        {
            violations.Add("no Level 3 entry naming " + Level3Boxes);
        }
        else
        {
            if (steps.Length > 0 && level3.Value.line < steps.Max(static row => row.Line))
            {
                violations.Add("Level 3 unticks boxes before harmless steps");
            }

            if (!BoxNumbers.All(box => level3.Value.text.Contains(box, StringComparison.Ordinal)))
            {
                violations.Add("Level 3 must name " + Level3Boxes);
            }
        }

        return violations;
    }

    /// <summary>The trigger rule: every step row holds a UTC time and every time is before the deadline.</summary>
    /// <param name="buildLog">The build log text.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> DropTriggerViolations(string buildLog)
    {
        ArgumentNullException.ThrowIfNull(buildLog);

        string? section = SectionOf(buildLog, DropSection);
        if (section is null)
        {
            return ["missing section: Drop order"];
        }

        var violations = new List<string>();
        foreach (DropRow row in DropRows(section))
        {
            string? trigger = row.Cell("trigger");
            if (trigger is null)
            {
                violations.Add("drop order table lacks column: trigger");
                break;
            }

            DateTime[] times = UtcTimes(trigger);
            if (times.Length == 0)
            {
                violations.Add("no trigger: " + row.Id);
            }
            else if (times.Any(static time => time >= DeadlineUtc))
            {
                violations.Add("trigger after deadline: " + row.Id);
            }
        }

        if (Level3Entry(section, DropRows(section)) is { } level3 && !DropRows(section).Any(static row => row.Id == "L3") && UtcTimes(level3.text).Any(static time => time >= DeadlineUtc))
        {
            violations.Add("trigger after deadline: L3");
        }

        return violations;
    }

    /// <summary>The risk-link rule: every step row names a risk id, and the id is one of the story's register (RK-1 to RK-8 when the register is not supplied).</summary>
    /// <param name="buildLog">The build log text.</param>
    /// <param name="knownRisks">The risk ids of the story's Risks table, or null for RK-1 to RK-8.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> DropRiskViolations(string buildLog, IReadOnlyCollection<string>? knownRisks = null)
    {
        ArgumentNullException.ThrowIfNull(buildLog);

        string? section = SectionOf(buildLog, DropSection);
        if (section is null)
        {
            return ["missing section: Drop order"];
        }

        IReadOnlyCollection<string> known = knownRisks is { Count: > 0 } ? knownRisks : [.. Enumerable.Range(1, 8).Select(static n => "RK-" + n.ToString(CultureInfo.InvariantCulture))];
        var violations = new List<string>();
        foreach (DropRow row in DropRows(section))
        {
            string[] ids = [.. RiskId().Matches(row.Text).Cast<Match>().Select(static match => match.Value).Distinct(StringComparer.Ordinal)];
            if (ids.Length == 0)
            {
                violations.Add("no risk link: " + row.Id);
            }

            violations.AddRange(ids.Where(id => !known.Contains(id, StringComparer.Ordinal)).Select(id => $"unknown risk: {id} in {row.Id}"));
        }

        return violations;
    }

    /// <summary>The never-dropped rule: the list names the minimum submission, and none of its items appears in a drop row.</summary>
    /// <param name="buildLog">The build log text.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> NeverDroppedViolations(string buildLog)
    {
        ArgumentNullException.ThrowIfNull(buildLog);

        var violations = new List<string>();
        string? list = SectionOf(buildLog, "Never dropped");
        if (list is null)
        {
            violations.Add("missing section: Never dropped");
        }
        else
        {
            violations.AddRange(NeverDroppedItems
                .Where(item => !item.Words.All(word => list.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Select(static item => "never-dropped list lacks: " + item.Label));
        }

        if (SectionOf(buildLog, DropSection) is { } section)
        {
            foreach (string phrase in NeverDroppedPhrases.Where(phrase => DropRows(section).Where(static row => row.Id != "L3").Any(row => row.Text.Contains(phrase, StringComparison.OrdinalIgnoreCase))))
            {
                violations.Add("never-dropped item in drop list: " + phrase);
            }
        }

        return violations;
    }

    /// <summary>The buffer rule: the last target of the Buffer table is 3 hours or more before the deadline, and the capacity comparison is stated.</summary>
    /// <param name="buildLog">The build log text.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> BufferViolations(string buildLog)
    {
        ArgumentNullException.ThrowIfNull(buildLog);

        string? section = SectionOf(buildLog, "Buffer");
        if (section is null)
        {
            return ["missing section: Buffer"];
        }

        PipeTable? table = TableOf(section);
        if (table is null)
        {
            return ["Buffer has no table"];
        }

        var violations = new List<string>();
        DateTime[] targets = [.. table.Rows.SelectMany(static row => UtcTimes(string.Join(' ', row))).Where(static time => time != DeadlineUtc)];
        if (targets.Length == 0)
        {
            violations.Add("Buffer table has no target time");
        }
        else
        {
            double hours = (DeadlineUtc - targets.Max()).TotalHours;
            if (hours < MinimumBufferHours)
            {
                violations.Add("buffer " + hours.ToString("0.##", CultureInfo.InvariantCulture) + " h is below 3 h");
            }
        }

        if (!CapacityWords.All(word => section.Contains(word, StringComparison.Ordinal)))
        {
            violations.Add("capacity comparison missing: M6 55.25 h, M7 and planning figures");
        }

        return violations;
    }

    /// <summary>The risk ids of the <c>## Risks</c> table of a story file text.</summary>
    /// <param name="story">The story file text.</param>
    /// <returns>The ids.</returns>
    public static IReadOnlyList<string> StoryRiskIds(string story)
    {
        ArgumentNullException.ThrowIfNull(story);
        return [.. DocumentChecks.Lines(SectionOf(story, "Risks") ?? string.Empty).Select(static line => RiskRow().Match(line)).Where(static match => match.Success).Select(static match => match.Groups["id"].Value)];
    }

    /// <summary>Every AJ-159 rule over the build log of a folder (the risk ids from the M7 story file when it exists there).</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> DropOrderViolations(string root)
    {
        string? log = ReadText(root, "docs/build-log.md");
        if (log is null)
        {
            return ["missing docs/build-log.md"];
        }

        string? story = ReadText(root, "docs/plan/stories/M7-hardening-and-submission.md");
        return [.. DropOrderMonotonicViolations(log), .. DropTriggerViolations(log), .. DropRiskViolations(log, story is null ? null : StoryRiskIds(story)), .. NeverDroppedViolations(log), .. BufferViolations(log)];
    }

    // The Level 3 entry: an L3 table row, or a paragraph that starts with "Level 3" (with the lines that follow it up to a blank line).
    private static (int line, string text)? Level3Entry(string section, IReadOnlyList<DropRow> rows)
    {
        if (rows.FirstOrDefault(static row => row.Id == "L3") is { } row)
        {
            return (row.Line, row.Text);
        }

        string[] lines = DocumentChecks.Lines(section);
        int start = Array.FindIndex(lines, static line => Level3Start().IsMatch(line));
        if (start < 0)
        {
            return null;
        }

        string text = string.Join('\n', lines.Skip(start).TakeWhile(static line => !string.IsNullOrWhiteSpace(line)));
        return (start, text);
    }

    private static DateTime[] UtcTimes(string text) =>
        [.. UtcTime().Matches(text).Cast<Match>().Select(static match => DateTime.ParseExact(match.Groups["t"].Value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))];

    [GeneratedRegex(@"(?<t>[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}) UTC", RegexOptions.CultureInvariant)]
    private static partial Regex UtcTime();

    [GeneratedRegex(@"RK-[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex RiskId();

    [GeneratedRegex(@"^\|\s*(?<id>RK-[0-9]+)\s*\|", RegexOptions.CultureInvariant)]
    private static partial Regex RiskRow();

    [GeneratedRegex(@"^[*\s]*Level 3\b", RegexOptions.CultureInvariant)]
    private static partial Regex Level3Start();
}