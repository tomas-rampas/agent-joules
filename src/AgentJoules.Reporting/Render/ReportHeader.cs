using System.Globalization;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;

namespace AgentJoules.Reporting.Render;

/// <summary>
/// The measurement context every report surface prints above its table (AJ-083, REQ-016, P-13): one bullet per run file with where and when it ran, on what, with which models, judged by which model, and by which
/// energy method; one <c>&lt;tier&gt;: not run</c> bullet per tier without a primary file; then the disclosure sentences of the model. The Markdown summary, the HTML page and the README block take their header
/// lines from here, so the three texts cannot drift. Every text that comes from a document passes through the <c>escape</c> of the surface; the fixed words and the sentences of <see cref="ReportSentences"/> do not.
/// </summary>
public static class ReportHeader
{
    private const string NotAvailable = "N/A";
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string DisclosureSeparator = ", ";
    private static readonly string[] TierNames = [.. TierOptions.KnownNames];

    // The sentences that are printed as they are: the fixed text of a disclosure.
    private static readonly HashSet<string> Sentences = new(StringComparer.Ordinal)
    {
        ReportSentences.GpuBoardOnly,
        ReportSentences.JudgeExcluded,
        ReportSentences.SelfJudged,
        ReportSentences.ConcurrentOverlap,
        ReportSentences.ConcurrentCostOverlap,
        ReportSentences.SamplingFallback,
    };

    /// <summary>
    /// The header lines of a report: a bullet per run file and per tier that was not run (in tier order, a tier's primary file before its baseline file), the sampling-fallback line indented under the bullet of
    /// each tier that fell back to sampling, one empty line when there are disclosures, and the disclosures in the order of the model. The models-differ disclosure (recognised by
    /// <see cref="ReportSentences.ModelsDifferPrefix"/>) is only a trigger: the printed line is the prefix and one <c>tier=models (precision)</c> entry per primary tier report of the model (the structural parentheses go through the escape too), built here with each model ID and each
    /// precision escaped on its own, never parsed from the text of the disclosure. A disclosure that equals one of the sentences of <see cref="ReportSentences"/> is printed unchanged; any other text is escaped whole with <paramref name="escape"/> and has a leading
    /// list or setext marker (<c>-</c>, <c>+</c>, <c>=</c>, <c>*</c>, or digits followed by <c>.</c> or <c>)</c>) backslash-escaped, so it is printed as text and not as a list item or a heading.
    /// </summary>
    /// <param name="model">The report model.</param>
    /// <param name="escape">The escaping of the surface for a text that comes from a document.</param>
    /// <returns>The lines, without line breaks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> or <paramref name="escape"/> is null.</exception>
    public static IReadOnlyList<string> Lines(ReportModel model, Func<string, string> escape)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(escape);

        var bullets = new List<(int Rank, string[] Lines)>();
        foreach (TierReport report in model.Tiers)
        {
            string[] lines = report.SamplingFallback
                ? [Bullet(report, escape), $"  - {ReportSentences.SamplingFallback}"]
                : [Bullet(report, escape)];
            bullets.Add((Rank(report.Tier, report.Variant), lines));
        }

        foreach (Tier tier in NotRun(model))
        {
            bullets.Add((Rank(tier, RunVariant.Primary), [$"- {TierOptions.NameOf(tier)}: {ReportSentences.NotRun}"]));
        }

        var result = new List<string>();

        // The sort is stable: a primary file comes before the baseline file of its tier, and the not-run line of a tier that has only a baseline file comes before that baseline.
        foreach ((int _, string[] lines) in bullets.OrderBy(entry => entry.Rank))
        {
            result.AddRange(lines);
        }

        if (model.Disclosures.Count > 0)
        {
            result.Add(string.Empty);
            result.AddRange(model.Disclosures.Select(disclosure => Disclosure(disclosure, model, escape)));
        }

        return result.AsReadOnly();
    }

    /// <summary>The place of a run file in the order of a report: tiers in <see cref="TierOptions.KnownNames"/> order, the primary file before the baseline file of a tier.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="variant">The variant of the file.</param>
    /// <returns>A number that grows with the place.</returns>
    internal static int Rank(Tier tier, RunVariant variant) => (Array.IndexOf(TierNames, TierOptions.NameOf(tier)) * 2) + (variant == RunVariant.Baseline ? 1 : 0);

    /// <summary>The tiers of <see cref="ReportModel.TiersNotRun"/> that have no primary file among <see cref="ReportModel.Tiers"/>, in the order of the model.</summary>
    /// <param name="model">The report model.</param>
    /// <returns>The tiers.</returns>
    internal static IEnumerable<Tier> NotRun(ReportModel model) =>
        model.TiersNotRun.Where(tier => !model.Tiers.Any(report => report.Tier == tier && report.Variant == RunVariant.Primary));

    private static string Bullet(TierReport report, Func<string, string> escape)
    {
        string name = TierOptions.NameOf(report.Tier) + (report.Variant == RunVariant.Baseline ? " baseline" : string.Empty);
        string runtime = Fact(report.LocalRuntime, escape);
        if (report.LocalRuntime is not null && report.RuntimeVersion is not null)
        {
            runtime = $"{runtime} {Fact(report.RuntimeVersion, escape)}";
        }

        string models = report.Models.Count == 0
            ? NotAvailable
            : string.Join(DisclosureSeparator, report.Models.Select(model => $"{Fact(model.Agent, escape)}={Fact(model.ModelId, escape)}"));
        string judge = report.JudgeModelIds.Count == 0 ? "none" : string.Join(DisclosureSeparator, report.JudgeModelIds.Select(id => Fact(id, escape)));
        string selfJudging = report.SelfJudged switch
        {
            true => "yes",
            false => "no",
            null => "not judged",
        };
        string methods = report.EnergyMethods.Count == 0 ? NotAvailable : string.Join(DisclosureSeparator, report.EnergyMethods.Select(Method));

        return $"- {name}: file {Fact(report.FileName, escape)}; run date {report.StartUtc.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture)}; status {Status(report.Status)}; mode {Mode(report.Mode)}; "
            + $"GPU {Fact(report.GpuName, escape)}; driver {Fact(report.DriverVersion, escape)}; local runtime {runtime}; precision {Fact(report.Precision, escape)}; "
            + $"reasoning mode {Fact(report.ReasoningMode, escape)}; power source {Fact(report.PowerSource, escape)}; models {models}; judge {judge}; self-judging {selfJudging}; energy method {methods}";
    }

    // A missing fact is N/A; so is a fact that is empty after the cleaning, so a bullet never has a field with no text.
    private static string Fact(string? text, Func<string, string> escape)
    {
        if (text is null)
        {
            return NotAvailable;
        }

        string escaped = escape(text);

        return escaped.Length == 0 ? NotAvailable : escaped;
    }

    // A disclosure is printed unchanged only when it is one of the sentences of ReportSentences. A line that starts with the models-differ prefix is only the trigger: the printed line is built here, from the
    // primary tier reports of the model, one "tier=models (precision)" entry per tier with each model ID and the precision cleaned and escaped on its own, so the structure of the line is the renderer's and a
    // model ID cannot hide or drop a real entry (every tier keeps its own entry); a model ID that contains ", <tier>=" still reads like an extra entry, an accepted residual. Any other text is escaped whole and, as it stands at the start of
    // a line, has a leading list or setext marker backslash-escaped, because the escape of a surface covers the inline characters only.
    private static string Disclosure(string line, ReportModel model, Func<string, string> escape)
    {
        if (Sentences.Contains(line))
        {
            return line;
        }

        if (line.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal))
        {
            string[] entries =
            [
                .. model.Tiers
                    .Where(report => report.Variant == RunVariant.Primary)
                    .Select(report => $"{TierOptions.NameOf(report.Tier)}={ModelsOf(report, escape)} {escape("(")}{Fact(report.Precision, escape)}{escape(")")}"),
            ];

            return ReportSentences.ModelsDifferPrefix + (entries.Length == 0 ? NotAvailable : string.Join(DisclosureSeparator, entries));
        }

        return EscapeLeadingMarker(escape(line).TrimStart(' '));
    }

    // The distinct model IDs of a tier in ordinal order, joined by "+" as the aggregator joins them; N/A for a tier without models.
    private static string ModelsOf(TierReport report, Func<string, string> escape)
    {
        string[] ids = [.. report.Models.Select(model => model.ModelId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        return ids.Length == 0 ? NotAvailable : string.Join('+', ids.Select(id => Fact(id, escape)));
    }

    // A line that starts with -, +, = or * is a list item or an underline, and one that starts with digits and a dot or a parenthesis is a numbered list item: a backslash makes it text.
    private static string EscapeLeadingMarker(string text)
    {
        if (text.Length > 0 && text[0] is '-' or '+' or '=' or '*')
        {
            return "\\" + text;
        }

        int digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        return digits > 0 && digits < text.Length && text[digits] is '.' or ')' ? text[..digits] + "\\" + text[digits..] : text;
    }

    private static string Status(RunStatus status) => status switch
    {
        RunStatus.Running => "running",
        RunStatus.Completed => "completed",
        RunStatus.Failed => "failed",
        RunStatus.Partial => "partial",
        RunStatus.Incomplete => "incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(status), (int)status, "The run status is not a defined run status."),
    };

    private static string Mode(RunMode mode) => mode switch
    {
        RunMode.Serialized => "serialized",
        RunMode.Concurrent => "concurrent",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), (int)mode, "The run mode is not a defined run mode."),
    };

    private static string Method(EnergyMethod method) => method switch
    {
        EnergyMethod.Counter => "counter",
        EnergyMethod.Sampling => "sampling",
        EnergyMethod.Unmetered => "unmetered",
        _ => throw new ArgumentOutOfRangeException(nameof(method), (int)method, "The energy method is not a defined energy method."),
    };
}