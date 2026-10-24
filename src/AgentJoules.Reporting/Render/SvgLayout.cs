using System.Globalization;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;

namespace AgentJoules.Reporting.Render;

/// <summary>What a slot of the chart holds: a bar with a value label, or a text marker with no bar (AJ-084, REQ-016).</summary>
internal enum SvgSlotKind
{
    // Numbered from 1 so that default(SvgSlotKind) is never a kind.

    /// <summary>A measured value: one rect and one value label.</summary>
    Bar = 1,

    /// <summary>No measured value: one text marker (<c>N/A</c>, <c>not-reported</c>, <c>not run</c> or <c>no completed steps</c>) and no rect.</summary>
    Marker,
}

/// <summary>
/// One slot of a group (AJ-084, P-19): the place of one tier (and, for a tier that has a baseline file, of its baseline) in a (task, agent) group.
/// </summary>
/// <param name="Tier">The tier of the slot.</param>
/// <param name="Variant">Whether the slot shows the latest run file of the tier or its baseline file.</param>
/// <param name="Label">The slot label: the tier name, then <c> baseline</c> and <c> (partial)</c> when they apply.</param>
/// <param name="Kind">Whether the slot holds a bar or a marker.</param>
/// <param name="Text">The text of the value label or of the marker.</param>
/// <param name="CenterX">The horizontal centre of the slot.</param>
/// <param name="BarY">The top of the bar; the axis for a bar of height 0 and for a negative bar; the axis again for a marker.</param>
/// <param name="BarHeight">The height of the bar; 0 for a marker.</param>
/// <param name="Negative">True when the bar is drawn below the axis.</param>
internal sealed record SvgSlot(Tier Tier, RunVariant Variant, string Label, SvgSlotKind Kind, string Text, double CenterX, double BarY, double BarHeight, bool Negative);

/// <summary>One group of a row: a (task, agent) pair and its slots (AJ-084, P-19).</summary>
/// <param name="Label">The cleaned agent name; empty for the one group of a chart with no rows.</param>
/// <param name="CenterX">The horizontal centre of the group.</param>
/// <param name="Slots">The slots in tier order, a baseline slot after its tier.</param>
internal sealed record SvgGroup(string Label, double CenterX, IReadOnlyList<SvgSlot> Slots);

/// <summary>One row of the chart: the groups of one task, with the label of the task and an axis of its own (AJ-084).</summary>
/// <param name="Label">The cleaned task name; empty for the one row of a chart with no rows.</param>
/// <param name="LabelY">The y of the baseline of the row label.</param>
/// <param name="PlotTop">The y of the top of the plot of the row.</param>
/// <param name="AxisY">The y of the axis of the row.</param>
/// <param name="AxisRight">The x where the axis of the row ends.</param>
/// <param name="Groups">The groups of the task in the order of the rows of the model.</param>
internal sealed record SvgRow(string Label, double LabelY, double PlotTop, double AxisY, double AxisRight, IReadOnlyList<SvgGroup> Groups);

/// <summary>
/// The scale and the layout of the joules chart (AJ-084, REQ-016, P-19 to P-21 as amended): one row per task in the order the tasks first appear in the rows of the model, one group per agent in the row of its
/// task, one slot per tier in the fixed order in each group (a baseline slot after the slot of a tier that has a baseline file), one bar or marker per aggregate row. All bars of the chart share one scale: with
/// <c>maxPos</c> the largest median above 0 and <c>maxNeg</c> the largest magnitude below 0 over the bars drawn, the scale is <c>plotHeight / (maxPos + maxNeg)</c> (0 when the sum is 0), so bars of different rows
/// stay comparable. Each row has an axis at <c>plotTop + maxPos * scale</c> when any bar is negative and at the bottom of its plot otherwise. A median of 0 is a bar of height 0 with its label; a cell without a
/// value is a text marker and never a bar. At most <c>maxGroups</c> groups are drawn. Nothing compares or ranks the tiers: the slots only stand side by side in the fixed order. Every text from a document is cleaned
/// with <see cref="ReportText"/> here.
/// </summary>
internal sealed class SvgLayout
{
    // The header is the title (baseline 26, 16 px) and the scope sentence (baseline 48, 12 px); the first row label (13 px) sits at FirstRowTop + RowLabelGap. With a descent of a quarter em under the scope sentence
    // and an ascent of nine tenths of an em over the row label, the sentence ends at 51 and the label starts at 64.3: a clear gap of 13.3 px (at FirstRowTop 44 the two boxes overlapped by 4.7 px).
    internal const double FirstRowTop = 62.0;
    internal const double RowHeaderHeight = 40.0;
    internal const double PlotHeight = 240.0;
    internal const double BarWidth = 48.0;
    internal const double SlotWidth = 80.0;
    internal const double MarginLeft = 24.0;
    internal const double MarginRight = 130.0;
    internal const double MarginBottom = 150.0;
    internal const double GroupGap = 32.0;
    internal const double MinWidth = 560.0;
    internal const double NoticeHeight = 40.0;
    internal const double RowLabelGap = 14.0;
    internal const double RowPitch = RowHeaderHeight + PlotHeight + MarginBottom;

    private const string BaselineSuffix = " baseline";
    private const string PartialSuffix = " (partial)";

    private SvgLayout(IReadOnlyList<SvgRow> rows, double scale, double width, double height, double axisLeft, bool truncated, double noticeY)
    {
        Rows = rows;
        Scale = scale;
        Width = width;
        Height = height;
        AxisLeft = axisLeft;
        Truncated = truncated;
        NoticeY = noticeY;
    }

    /// <summary>The rows in the order the tasks first appear in the rows of the model; one row with an empty label when there are no rows.</summary>
    public IReadOnlyList<SvgRow> Rows { get; }

    /// <summary>The pixels per joule; 0 when no bar has a magnitude.</summary>
    public double Scale { get; }

    /// <summary>The width of the drawing: the widest row.</summary>
    public double Width { get; }

    /// <summary>The height of the drawing: the rows stacked, and the notice when the chart is cut.</summary>
    public double Height { get; }

    /// <summary>The x where the axis of every row starts.</summary>
    public double AxisLeft { get; }

    /// <summary>True when the model holds more groups than were drawn.</summary>
    public bool Truncated { get; }

    /// <summary>The y of the baseline of the notice of a cut chart.</summary>
    public double NoticeY { get; }

    /// <summary>Lays out the chart of a report model.</summary>
    /// <param name="model">The report model.</param>
    /// <param name="maxGroups">The most groups to draw, at least 1.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxGroups"/> is less than 1.</exception>
    public static SvgLayout Build(ReportModel model, int maxGroups)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGroups, 1);

        var reports = new Dictionary<(Tier, RunVariant), TierReport>();
        foreach (TierReport report in model.Tiers)
        {
            _ = reports.TryAdd((report.Tier, report.Variant), report);
        }

        var rows = new Dictionary<(Tier, RunVariant, string, string), ReportRow>();
        var withRows = new HashSet<(Tier, RunVariant)>();
        foreach (ReportRow row in model.Rows)
        {
            _ = rows.TryAdd((row.Tier, row.Variant, row.Task, row.Agent), row);
            _ = withRows.Add((row.Tier, row.Variant));
        }

        (Tier Tier, RunVariant Variant)[] places = [.. PlacesOf(reports)];

        // One more key than the cap is read, to know whether the chart is cut.
        (string Task, string Agent)[] keys = [.. model.Rows.Select(row => (row.Task, row.Agent)).Distinct().Take(maxGroups + 1)];
        bool truncated = keys.Length > maxGroups;
        if (truncated)
        {
            keys = keys[..maxGroups];
        }

        // A chart with no rows draws one row and one group with empty labels, so the tiers that were not run still show their markers.
        var contents = new List<(string RowLabel, List<(string GroupLabel, Content[] Slots)> Groups)>();
        if (keys.Length == 0)
        {
            contents.Add((string.Empty, [(string.Empty, [.. places.Select(place => ContentOf(place, reports, rows, withRows, null))])]));
        }

        foreach (IGrouping<string, (string Task, string Agent)> task in keys.GroupBy(key => key.Task, StringComparer.Ordinal))
        {
            var groups = new List<(string GroupLabel, Content[] Slots)>();
            foreach ((string taskName, string agent) in task)
            {
                groups.Add((ReportText.Clean(agent, ReportText.NameMaxLength), [.. places.Select(place => ContentOf(place, reports, rows, withRows, (taskName, agent)))]));
            }

            contents.Add((ReportText.Clean(task.Key, ReportText.NameMaxLength), groups));
        }

        double maxPos = 0.0;
        double maxNeg = 0.0;
        foreach (Content content in contents.SelectMany(row => row.Groups).SelectMany(group => group.Slots))
        {
            if (content.Value is double value)
            {
                maxPos = Math.Max(maxPos, value);
                maxNeg = Math.Max(maxNeg, -value);
            }
        }

        // The halves keep the sum of two huge medians from overflowing; the quotient is the same number as PlotHeight / (maxPos + maxNeg) otherwise. A sum of 0 divides to infinity and gives the scale 0.
        double scale = (PlotHeight / 2.0) / ((maxPos / 2.0) + (maxNeg / 2.0));
        if (!double.IsFinite(scale))
        {
            scale = 0.0;
        }

        // The axis is where zero is: below the tallest positive bar when a bar is negative, at the bottom of the plot when none is (so a chart of markers and zeros does not float above an empty plot).
        double axisOffset = maxNeg > 0.0 ? maxPos * scale : PlotHeight;
        double groupWidth = places.Length * SlotWidth;
        double widest = contents.Max(row => PlotWidth(row.Groups.Count, groupWidth));

        var laidOut = new List<SvgRow>(contents.Count);
        for (int index = 0; index < contents.Count; index++)
        {
            double rowTop = FirstRowTop + (index * RowPitch);
            double plotTop = rowTop + RowHeaderHeight;
            double axisY = plotTop + axisOffset;
            var groups = new List<SvgGroup>(contents[index].Groups.Count);
            for (int groupIndex = 0; groupIndex < contents[index].Groups.Count; groupIndex++)
            {
                double left = MarginLeft + (groupIndex * (groupWidth + GroupGap));
                var slots = new List<SvgSlot>(places.Length);
                for (int slot = 0; slot < places.Length; slot++)
                {
                    slots.Add(Place(places[slot], contents[index].Groups[groupIndex].Slots[slot], left + (slot * SlotWidth) + (SlotWidth / 2.0), scale, axisY));
                }

                groups.Add(new SvgGroup(contents[index].Groups[groupIndex].GroupLabel, left + (groupWidth / 2.0), slots.AsReadOnly()));
            }

            laidOut.Add(new SvgRow(contents[index].RowLabel, rowTop + RowLabelGap, plotTop, axisY, MarginLeft + PlotWidth(groups.Count, groupWidth), groups.AsReadOnly()));
        }

        double rowsBottom = FirstRowTop + (contents.Count * RowPitch);

        return new SvgLayout(
            laidOut.AsReadOnly(),
            scale,
            Math.Max(MinWidth, MarginLeft + widest + MarginRight),
            rowsBottom + (truncated ? NoticeHeight : 0.0),
            MarginLeft,
            truncated,
            rowsBottom + (NoticeHeight / 2.0));
    }

    /// <summary>The text of a number in a coordinate or a size: at most two decimals, the invariant culture, no negative zero, no exponent.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The text.</returns>
    public static string Coordinate(double value)
    {
        string text = Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

        return text == "-0" ? "0" : text;
    }

    private static double PlotWidth(int groups, double groupWidth) => (groups * groupWidth) + ((groups - 1) * GroupGap);

    // The tiers in the fixed order, the primary place of each, and the baseline place of a tier that has a baseline file.
    private static IEnumerable<(Tier Tier, RunVariant Variant)> PlacesOf(Dictionary<(Tier, RunVariant), TierReport> reports)
    {
        foreach (Tier tier in Enum.GetValues<Tier>().OrderBy(tier => ReportHeader.Rank(tier, RunVariant.Primary)))
        {
            yield return (tier, RunVariant.Primary);
            if (reports.ContainsKey((tier, RunVariant.Baseline)))
            {
                yield return (tier, RunVariant.Baseline);
            }
        }
    }

    private static Content ContentOf(
        (Tier Tier, RunVariant Variant) place,
        Dictionary<(Tier, RunVariant), TierReport> reports,
        Dictionary<(Tier, RunVariant, string, string), ReportRow> rows,
        HashSet<(Tier, RunVariant)> withRows,
        (string Task, string Agent)? key)
    {
        string name = TierOptions.NameOf(place.Tier) + (place.Variant == RunVariant.Baseline ? BaselineSuffix : string.Empty);
        if (!reports.TryGetValue(place, out TierReport? report))
        {
            return new Content(name, ReportSentences.NotRun, null);
        }

        string label = report.Status == RunStatus.Partial ? name + PartialSuffix : name;
        if (key is (string task, string agent) && rows.TryGetValue((place.Tier, place.Variant, task, agent), out ReportRow? row))
        {
            ReportCell cell = row.NetJoules;

            return cell.Median is double median && cell.N >= 1 && double.IsFinite(median)
                ? new Content(label, InvariantFormat.Joules(median), median)
                : new Content(label, MetricLabelText.Of(cell.Label), null);
        }

        // A file that has no row at all says so; a file that has rows but none for this group has no value for it.
        return new Content(label, withRows.Contains(place) ? MetricLabelText.NotApplicable : ReportSentences.NoCompletedSteps, null);
    }

    private static SvgSlot Place((Tier Tier, RunVariant Variant) place, Content content, double centerX, double scale, double axisY)
    {
        if (content.Value is not double value)
        {
            return new SvgSlot(place.Tier, place.Variant, content.Label, SvgSlotKind.Marker, content.Text, centerX, axisY, 0.0, Negative: false);
        }

        double height = Math.Abs(value) * scale;

        return value < 0.0
            ? new SvgSlot(place.Tier, place.Variant, content.Label, SvgSlotKind.Bar, content.Text, centerX, axisY, height, Negative: true)
            : new SvgSlot(place.Tier, place.Variant, content.Label, SvgSlotKind.Bar, content.Text, centerX, axisY - height, height, Negative: false);
    }

    private sealed record Content(string Label, string Text, double? Value);
}