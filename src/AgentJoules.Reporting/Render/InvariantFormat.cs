using System.Globalization;
using System.Text;

using AgentJoules.Core.Labels;

using LabelMetric = AgentJoules.Core.Labels.Metric;

namespace AgentJoules.Reporting.Render;

/// <summary>The kind of number a report cell holds; it fixes the decimals the cell prints (AJ-083, P-26).</summary>
public enum CellKind
{
    // Numbered from 1 so that default(CellKind) is never a kind.

    /// <summary>Joules: one decimal.</summary>
    Joules = 1,

    /// <summary>Watts: one decimal.</summary>
    Watts,

    /// <summary>Milliseconds: no decimal.</summary>
    Milliseconds,

    /// <summary>A count (tokens): no decimal when the value is integral, else one.</summary>
    Count,

    /// <summary>A cost: four decimals, followed by the currency code.</summary>
    Cost,

    /// <summary>A judged quality score: no decimal when the value is integral, else one (the median of an even number of scores can fall between two integers).</summary>
    Quality,
}

/// <summary>
/// The one place the report surfaces turn numbers and cells into text (AJ-083, REQ-016, design 805): every number is formatted with the invariant culture, a negative zero is written as zero, and a cell follows the
/// grammar <c>&lt;median&gt;[ &lt;currency&gt;][ [&lt;min&gt; to &lt;max&gt;]] n=&lt;N&gt;[ of &lt;Total&gt;][ &lt;label&gt;]</c> when it has a value and <c>&lt;N/A|not-reported&gt; (&lt;reason&gt;[; &lt;reason&gt;...])</c> when it has
/// none. The value is always the first text of a cell, the range appears only when two or more repeats have a value, <c>of &lt;Total&gt;</c> only when a repeat has none, and the label only when it is not
/// <c>measured</c>. A cell without a value never prints a number. The Markdown summary, the HTML page and the README block all use this class, so a number cannot be spelled two ways.
/// </summary>
public static class InvariantFormat
{
    private const string OneDecimal = "F1";
    private const string NoDecimal = "F0";
    private const string FourDecimals = "F4";
    private const int MaxReasons = 3;
    private const double SmallCost = 0.0001;

    // Math.Round accepts at most 15 digits; a magnitude that needs more is below 1e-14 and can never round up to 0.0001.
    private const int MaxRoundDigits = 15;

    /// <summary>Formats joules with one decimal.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text, for example <c>12.3</c>.</returns>
    public static string Joules(double value) => Fixed(value, OneDecimal);

    /// <summary>Formats watts with one decimal.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text, for example <c>90.0</c>.</returns>
    public static string Watts(double value) => Fixed(value, OneDecimal);

    /// <summary>Formats milliseconds with no decimal.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text, for example <c>1000</c>.</returns>
    public static string Milliseconds(double value) => Fixed(value, NoDecimal);

    /// <summary>
    /// Formats a cost: four decimals when the absolute value is at least 0.0001 and for an exact zero (<c>0.0000</c>); below 0.0001 the value with two significant digits in fixed notation (<c>0.000019</c>,
    /// <c>0.0000058</c>, never an exponent), so the cost of a small step does not read as an estimated zero. The currency code is not part of the number. A value near <see cref="double.Epsilon"/> prints a fixed
    /// string of about 327 characters: the length is bounded by the range of a double and is accepted.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The text, for example <c>0.0012</c> or <c>0.000019</c>.</returns>
    public static string Cost(double value)
    {
        double magnitude = Math.Abs(value);
        if (magnitude == 0.0 || magnitude >= SmallCost || !double.IsFinite(value))
        {
            return Fixed(value, FourDecimals);
        }

        // Two significant digits: the decimals reach the second digit after the first non-zero one. A value that rounds up to 0.0001 is printed in the four-decimal form.
        int decimals = 1 - (int)Math.Floor(Math.Log10(magnitude));

        if (decimals <= MaxRoundDigits && Math.Round(magnitude, decimals) >= SmallCost)
        {
            return Fixed(value, FourDecimals);
        }

        // A value that rounds up to the next power of ten (9.9996e-6 to 0.0000100) would show three significant digits: one decimal fewer shows the two it has (0.000010).
        string text = Fixed(value, "F" + decimals.ToString(CultureInfo.InvariantCulture));

        return SignificantDigits(text) > 2 ? Fixed(value, "F" + (decimals - 1).ToString(CultureInfo.InvariantCulture)) : text;
    }

    // The digits after the point from the first non-zero one: "0.000019" has two.
    private static int SignificantDigits(string text) => text[(text.IndexOf('.', StringComparison.Ordinal) + 1)..].TrimStart('0').Length;

    /// <summary>
    /// Formats a count (tokens) or a quality score: no decimal when the value is integral, one decimal otherwise, so the median of an even number of integer scores (3.5) is never rounded to a score nobody gave.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The text, for example <c>120</c> or <c>3.5</c>.</returns>
    public static string Count(double value) => Fixed(value, double.IsInteger(value) ? NoDecimal : OneDecimal);

    /// <summary>
    /// Formats one report cell. The text starts with the value (or with the label of a cell without one); <paramref name="escape"/> is applied to each reason and to the currency code, nothing else: the numbers, the
    /// label text and the fixed words are produced here and need no escaping. A cell without a value lists at most three reasons and then <c>; +&lt;k&gt; more</c>.
    /// </summary>
    /// <param name="cell">The cell.</param>
    /// <param name="kind">The kind of number the cell holds.</param>
    /// <param name="escape">The escaping of the surface (Markdown escaping, or the cleaning that precedes HTML encoding) for a text that comes from a document.</param>
    /// <returns>The text of the cell.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cell"/> or <paramref name="escape"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined kind.</exception>
    public static string Cell(ReportCell cell, CellKind kind, Func<string, string> escape)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(escape);
        RequireDefined(kind);

        if (cell.Median is not { } median)
        {
            return Missing(cell.Label, cell.Reasons, escape);
        }

        var text = new StringBuilder(Number(median, kind));
        if (cell.Currency is not null)
        {
            text.Append(' ').Append(escape(cell.Currency));
        }

        if (cell.N >= 2 && cell.Min is { } min && cell.Max is { } max)
        {
            text.Append(" [").Append(Number(min, kind)).Append(" to ").Append(Number(max, kind)).Append(']');
        }

        text.Append(" n=").Append(cell.N.ToString(CultureInfo.InvariantCulture));
        if (cell.N < cell.Total)
        {
            text.Append(" of ").Append(cell.Total.ToString(CultureInfo.InvariantCulture));
        }

        if (cell.Label != MetricLabel.Measured)
        {
            text.Append(' ').Append(MetricLabelText.Of(cell.Label));
        }

        return text.ToString();
    }

    /// <summary>
    /// Formats one value of the totals: <c>&lt;value&gt;[ &lt;currency&gt;][ &lt;label&gt;]</c> (the label only when it is not <c>measured</c>), or <c>&lt;label&gt; (&lt;reason&gt;)</c> for a value that does not exist.
    /// <paramref name="escape"/> is applied to the reason and to the currency code.
    /// </summary>
    /// <param name="m">The metric.</param>
    /// <param name="kind">The kind of number the metric holds.</param>
    /// <param name="escape">The escaping of the surface for a text that comes from a document.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="m"/> or <paramref name="escape"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined kind.</exception>
    public static string Metric(LabelMetric m, CellKind kind, Func<string, string> escape)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(escape);
        RequireDefined(kind);

        if (m.Value is not { } value)
        {
            return Missing(m.Label, m.Reason is null ? [] : [m.Reason], escape);
        }

        var text = new StringBuilder(Number(value, kind));
        if (m.Currency is not null)
        {
            text.Append(' ').Append(escape(m.Currency));
        }

        if (m.Label != MetricLabel.Measured)
        {
            text.Append(' ').Append(MetricLabelText.Of(m.Label));
        }

        return text.ToString();
    }

    private static void RequireDefined(CellKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), (int)kind, "The cell kind is not a defined cell kind.");
        }
    }

    private static string Number(double value, CellKind kind) => kind switch
    {
        CellKind.Joules => Joules(value),
        CellKind.Watts => Watts(value),
        CellKind.Milliseconds => Milliseconds(value),
        CellKind.Cost => Cost(value),
        _ => Count(value),
    };

    private static string Missing(MetricLabel label, IReadOnlyList<string> reasons, Func<string, string> escape)
    {
        string text = MetricLabelText.Of(label);
        if (reasons.Count == 0)
        {
            return text;
        }

        string[] shown = [.. reasons.Take(MaxReasons).Select(escape)];
        string list = string.Join("; ", shown);
        if (reasons.Count > MaxReasons)
        {
            list = $"{list}; +{(reasons.Count - MaxReasons).ToString(CultureInfo.InvariantCulture)} more";
        }

        return $"{text} ({list})";
    }

    // The rounded text of a number, without the sign of a result that rounds to zero ("-0.0" is "0.0"): a negative zero, or a value too small for the decimals, is not a negative figure.
    private static string Fixed(double value, string format)
    {
        string text = value.ToString(format, CultureInfo.InvariantCulture);

        return text.Length > 1 && text[0] == '-' && text.AsSpan(1).IndexOfAnyExcept('0', '.') < 0 ? text[1..] : text;
    }
}