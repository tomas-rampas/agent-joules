using System.Text;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// In-memory methodology documents and calibration files for the contract of <c>docs/methodology.md</c> (AJ-112, REQ-022). <see cref="Document"/> renders a complete document that passes
/// <see cref="MethodologyDocumentParser.Check"/>, with one knob per scenario of the story: a heading left out, a Limitations string left out, the strings in reverse order, a quoted difference, the calibration file
/// left unnamed, a REQ-032 sentence left out. Nothing here reads a file or the network.
/// </summary>
public static class MethodologyDocumentFixtures
{
    /// <summary>The calibration file the fixture documents name.</summary>
    public const string CalibrationName = "results/calibration-20261025T101500Z.json";

    /// <summary>The <c>relativeDifference</c> value the fixture calibration file stores and the fixture documents quote.</summary>
    public const string StoredDifference = "0.0202";

    /// <summary>The five required headings.</summary>
    public static readonly string[] Headings = ["Measurement", "Attribution", "Baseline", "Labels", "Limitations"];

    /// <summary>The seven strings the Limitations section must contain, in the order of the story.</summary>
    public static readonly string[] LimitationStrings = ["GPU-only", "display", "power limit", "sampling", "offload", "hosted", "cloud"];

    // One short item per string, each holding its own string and none of the others.
    private static readonly Dictionary<string, string> Items = new(StringComparer.Ordinal)
    {
        ["GPU-only"] = "- Scope: GPU-only board energy.",
        ["display"] = "- The GPU is shared with the display.",
        ["power limit"] = "- The laptop power limit caps the clocks.",
        ["sampling"] = "- The sampling resolution is finite.",
        ["offload"] = "- Partial offload moves work to the CPU.",
        ["hosted"] = "- The energy of a hosted tier is N/A.",
        ["cloud"] = "- The cloud tier is a dedicated VM GPU.",
    };

    /// <summary>The calibration file with the given stored <c>relativeDifference</c> (a JSON number text), or N/A with a reason when <paramref name="difference"/> is null.</summary>
    /// <param name="difference">The JSON text of the stored value, or null for N/A.</param>
    /// <param name="reason">The reason of the N/A.</param>
    /// <returns>The JSON text.</returns>
    public static string CalibrationFile(string? difference = StoredDifference, string reason = "counter-zero") =>
        difference is null
            ? "{\"relativeDifference\":{\"value\":null,\"label\":\"N/A\",\"reason\":\"" + reason + "\"}}"
            : "{\"relativeDifference\":{\"value\":" + difference + ",\"label\":\"measured\",\"unit\":\"ratio\"}}";

    /// <summary>A reader of the results folder that serves <paramref name="json"/> for <see cref="CalibrationName"/> and nothing else.</summary>
    /// <param name="json">The content of the calibration file.</param>
    /// <returns>The reader for <see cref="MethodologyDocumentParser.Check"/>.</returns>
    public static Func<string, string?> Reader(string json) => path => string.Equals(path, CalibrationName, StringComparison.Ordinal) ? json : null;

    /// <summary>Renders a methodology document.</summary>
    /// <param name="omitHeading">A required heading to leave out (its section text stays, under the heading before it), or null.</param>
    /// <param name="omitString">A Limitations string to leave out, or null.</param>
    /// <param name="reverseStrings">True to list the Limitations strings in reverse order.</param>
    /// <param name="quoted">The text quoted as the <c>relativeDifference</c>; null to quote N/A with <paramref name="quotedReason"/>.</param>
    /// <param name="quotedReason">The reason quoted beside N/A.</param>
    /// <param name="nameFile">False to leave the calibration file unnamed.</param>
    /// <param name="omitSentence">One of the three REQ-032 sentences to leave out, or null.</param>
    /// <param name="existing">A sentence of an earlier milestone to keep under Limitations, or null.</param>
    /// <param name="verified">False to leave out the Verified line.</param>
    /// <returns>The markdown text.</returns>
    public static string Document(
        string? omitHeading = null,
        string? omitString = null,
        bool reverseStrings = false,
        string? quoted = StoredDifference,
        string quotedReason = "counter-zero",
        bool nameFile = true,
        string? omitSentence = null,
        string? existing = null,
        bool verified = true)
    {
        var text = new StringBuilder();
        text.Append("# Methodology\n\n");
        if (verified)
        {
            text.Append("Verified: 2026-10-25\n\n");
        }

        AppendHeading(text, "Measurement", omitHeading);
        text.Append("NVML reports the energy counter in millijoule units.\n");
        text.Append("NVML reports the power draw in milliwatt units.\n");
        text.Append("The sample rate of the sampling method is 10 Hz.\n");
        text.Append("A window opens just before the HTTP call and closes just after it.\n\n");

        AppendHeading(text, "Attribution", omitHeading);
        text.Append("One metered client serves each agent; no ambient context is used. A cloud VM would be another matter.\n\n");

        AppendHeading(text, "Baseline", omitHeading);
        text.Append("Gross energy minus the idle baseline is the net energy; a concurrent run reports union totals.\n\n");

        AppendHeading(text, "Labels", omitHeading);
        text.Append("Five labels: measured, estimated, derived, N/A and replayed.\n");
        if (nameFile)
        {
            text.Append("The calibration file is ").Append(CalibrationName).Append(" and its relativeDifference is ");
            text.Append(quoted ?? "N/A (" + quotedReason + ")").Append(".\n");
        }

        text.Append('\n');

        AppendHeading(text, "Limitations", omitHeading);
        IEnumerable<string> strings = omitString is null ? LimitationStrings : LimitationStrings.Where(item => !string.Equals(item, omitString, StringComparison.Ordinal));
        foreach (string item in reverseStrings ? strings.Reverse() : strings)
        {
            text.Append(Items[item]).Append('\n');
        }

        text.Append('\n');
        string[] sentences = [MethodologyDocumentParser.NoGpuWorkSentence, MethodologyDocumentParser.CompositingSentence, MethodologyDocumentParser.EstimatesSentence];
        foreach (string sentence in sentences.Where(sentence => !string.Equals(sentence, omitSentence, StringComparison.Ordinal)))
        {
            text.Append(sentence).Append('\n');
        }

        if (existing is not null)
        {
            text.Append('\n').Append(existing).Append('\n');
        }

        return text.ToString();
    }

    private static void AppendHeading(StringBuilder text, string heading, string? omitted)
    {
        if (!string.Equals(heading, omitted, StringComparison.Ordinal))
        {
            text.Append("## ").Append(heading).Append('\n');
        }
    }
}