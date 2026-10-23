using System.Text;
using System.Xml;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;

namespace AgentJoules.Reporting.Render;

/// <summary>
/// Draws the report model as <c>joules-per-step.svg</c> (AJ-084, REQ-016, ADR-06): net joules as one bar per aggregate row, with the median as its value label, and an explicit text marker where there is no
/// measured value. The chart has one row per task (a <c>row-label</c> text holding the task name and an axis of its own), one group per agent in the row of its task, and one slot per tier in each group. A cell with
/// a value is one <c>rect</c> (class <c>bar</c>, below the axis when the value is negative) and one <c>text</c> (class <c>value</c>) holding exactly <see cref="InvariantFormat.Joules(double)"/>; a cell without a
/// value is one <c>text</c> (class <c>marker</c>) holding the label itself (<c>N/A</c> or <c>not-reported</c>), and a tier without a run file is the marker <c>not run</c>; neither draws a rect, so a missing value
/// is never mistaken for a short bar. A median of 0 is a measured value and a bar of height 0. All rows share one scale. The chart carries <see cref="ReportSentences.GpuBoardOnly"/> as a <c>text</c> element
/// and draws nothing across tiers: the slots only stand side by side in the fixed order, and no comparison, ranking or total is drawn. At most <see cref="MaxGroups"/> groups are drawn; a model with more gets
/// <see cref="ReportSentences.ChartTruncated"/> as a marker in a final row. The layout and the scale are those of <see cref="SvgLayout"/>. The text is written with <see cref="XmlWriter"/> (never concatenated):
/// every string from a document is cleaned with <see cref="ReportText"/> and then escaped by the writer as a text node, and none is ever an attribute value. The output is deterministic: invariant numbers, a
/// fixed palette, no font loaded, no timestamp, ASCII only (characters above U+007F are written as character references), LF newlines, no XML declaration, and one line feed at the end.
/// </summary>
public static class JoulesSvgRenderer
{
    /// <summary>The most groups (a task and an agent) the chart draws; the Markdown summary lists every row.</summary>
    public const int MaxGroups = 1000;

    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string Title = "Measured net energy per step in joules (J), median of the repeats of each bar";
    private const string AxisColor = "#555555";
    private const string TextColor = "#222222";
    private const string MarkerColor = "#6b6b6b";
    private const string FontFamily = "sans-serif";
    private const double TitleY = 26.0;
    private const double ScopeY = 48.0;
    private const double ValueLabelGap = 5.0;
    private const double NegativeLabelGap = 13.0;
    private const double MarkerGap = 8.0;
    private const double SlotLabelGap = 34.0;
    private const double GroupLabelGap = 138.0;
    private const double SlotLabelAngle = 35.0;

    // One fixed colour per tier, and a lighter one for the baseline file of the tier; the colours name the tier, not a rank.
    private static readonly Dictionary<Tier, (string Primary, string Baseline)> Palette = new()
    {
        [Tier.Local] = ("#2a6f97", "#9ec5de"),
        [Tier.CloudGpu] = ("#7b5ea7", "#c6b8de"),
        [Tier.Nvidia] = ("#3a7d44", "#a8d0ae"),
        [Tier.Nebius] = ("#b8651b", "#e6c19a"),
    };

    /// <summary>Renders the chart.</summary>
    /// <param name="model">The report model.</param>
    /// <returns>The SVG text: ASCII, LF newlines, no XML declaration, ending with one line feed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is null.</exception>
    public static string Render(ReportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        SvgLayout layout = SvgLayout.Build(model, MaxGroups);

        // Written to bytes with the ASCII encoding, so a character above U+007F becomes a character reference (a StringWriter would declare UTF-16 and keep the character).
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = true,
            CheckCharacters = true,
            Encoding = Encoding.ASCII,
            CloseOutput = false,
        };

        using var stream = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(stream, settings))
        {
            WriteChart(writer, layout);
        }

        // The final line feed goes into the stream, so the text is decoded once and not copied again to append it.
        stream.WriteByte((byte)'\n');

        return Encoding.ASCII.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void WriteChart(XmlWriter writer, SvgLayout layout)
    {
        writer.WriteStartElement("svg", SvgNamespace);
        writer.WriteAttributeString("xmlns", SvgNamespace);
        writer.WriteAttributeString("width", SvgLayout.Coordinate(layout.Width));
        writer.WriteAttributeString("height", SvgLayout.Coordinate(layout.Height));
        writer.WriteAttributeString("viewBox", $"0 0 {SvgLayout.Coordinate(layout.Width)} {SvgLayout.Coordinate(layout.Height)}");
        writer.WriteAttributeString("font-family", FontFamily);
        writer.WriteAttributeString("style", "background-color:#ffffff");

        WriteText(writer, "title", SvgLayout.MarginLeft, TitleY, Title, "start", "16", TextColor, bold: true);
        WriteText(writer, "scope", SvgLayout.MarginLeft, ScopeY, ReportSentences.GpuBoardOnly, "start", "12", TextColor);

        foreach (SvgRow row in layout.Rows)
        {
            WriteRow(writer, row, layout.AxisLeft);
        }

        if (layout.Truncated)
        {
            writer.WriteStartElement("g");
            writer.WriteAttributeString("class", "notice");
            WriteText(writer, "notice", SvgLayout.MarginLeft, layout.NoticeY, ReportSentences.ChartTruncated, "start", "12", MarkerColor, bold: true);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteRow(XmlWriter writer, SvgRow row, double axisLeft)
    {
        writer.WriteStartElement("g");
        writer.WriteAttributeString("class", "row");
        WriteText(writer, "row-label", SvgLayout.MarginLeft, row.LabelY, row.Label, "start", "13", TextColor, bold: true);

        writer.WriteStartElement("line");
        writer.WriteAttributeString("class", "axis");
        writer.WriteAttributeString("x1", SvgLayout.Coordinate(axisLeft));
        writer.WriteAttributeString("y1", SvgLayout.Coordinate(row.AxisY));
        writer.WriteAttributeString("x2", SvgLayout.Coordinate(row.AxisRight));
        writer.WriteAttributeString("y2", SvgLayout.Coordinate(row.AxisY));
        writer.WriteAttributeString("stroke", AxisColor);
        writer.WriteAttributeString("stroke-width", "1");
        writer.WriteEndElement();

        double plotBottom = row.PlotTop + SvgLayout.PlotHeight;
        foreach (SvgGroup group in row.Groups)
        {
            writer.WriteStartElement("g");
            writer.WriteAttributeString("class", "group");
            WriteText(writer, "group-label", group.CenterX, plotBottom + GroupLabelGap, group.Label, "middle", "12", TextColor, bold: true);
            foreach (SvgSlot slot in group.Slots)
            {
                WriteSlot(writer, slot, row.AxisY, plotBottom);
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteSlot(XmlWriter writer, SvgSlot slot, double axisY, double plotBottom)
    {
        writer.WriteStartElement("g");
        writer.WriteAttributeString("class", "slot");
        writer.WriteAttributeString("data-tier", TierOptions.NameOf(slot.Tier));
        writer.WriteAttributeString("data-variant", slot.Variant == RunVariant.Baseline ? "baseline" : "primary");

        if (slot.Kind == SvgSlotKind.Bar)
        {
            (string primary, string baseline) = Palette[slot.Tier];
            writer.WriteStartElement("rect");
            writer.WriteAttributeString("class", "bar");
            writer.WriteAttributeString("x", SvgLayout.Coordinate(slot.CenterX - (SvgLayout.BarWidth / 2.0)));
            writer.WriteAttributeString("y", SvgLayout.Coordinate(slot.BarY));
            writer.WriteAttributeString("width", SvgLayout.Coordinate(SvgLayout.BarWidth));
            writer.WriteAttributeString("height", SvgLayout.Coordinate(slot.BarHeight));
            writer.WriteAttributeString("fill", slot.Variant == RunVariant.Baseline ? baseline : primary);
            writer.WriteEndElement();

            double labelY = slot.Negative ? slot.BarY + slot.BarHeight + NegativeLabelGap : slot.BarY - ValueLabelGap;
            WriteText(writer, "value", slot.CenterX, labelY, slot.Text, "middle", "11", TextColor);
        }
        else
        {
            WriteText(writer, "marker", slot.CenterX, axisY - MarkerGap, slot.Text, "middle", "11", MarkerColor, bold: true);
        }

        double labelTop = plotBottom + SlotLabelGap;
        WriteText(writer, "slot-label", slot.CenterX, labelTop, slot.Label, "start", "10", TextColor, rotate: SlotLabelAngle);
        writer.WriteEndElement();
    }

    // A text element is written start, string, full end: its only child is the string, so the writer keeps "<text ...>N/A</text>" on one line and an empty label as "<text ...></text>".
    private static void WriteText(XmlWriter writer, string cssClass, double x, double y, string text, string anchor, string size, string color, bool bold = false, double rotate = 0.0)
    {
        writer.WriteStartElement("text");
        writer.WriteAttributeString("class", cssClass);
        writer.WriteAttributeString("x", SvgLayout.Coordinate(x));
        writer.WriteAttributeString("y", SvgLayout.Coordinate(y));
        writer.WriteAttributeString("text-anchor", anchor);
        writer.WriteAttributeString("font-size", size);
        writer.WriteAttributeString("fill", color);
        if (bold)
        {
            writer.WriteAttributeString("font-weight", "bold");
        }

        if (rotate != 0.0)
        {
            writer.WriteAttributeString("transform", $"rotate({SvgLayout.Coordinate(rotate)} {SvgLayout.Coordinate(x)} {SvgLayout.Coordinate(y)})");
        }

        writer.WriteString(text);
        writer.WriteFullEndElement();
    }
}