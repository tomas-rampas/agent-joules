using System.Text.Json.Serialization;

namespace AgentJoules.Core.Labels;

/// <summary>
/// The only representation of a metric value: a number with exactly one <see cref="MetricLabel"/>.
/// A value that does not exist is <c>null</c> with the label <see cref="MetricLabel.NotApplicable"/> or
/// <see cref="MetricLabel.NotReported"/> and a reason; it is never written as 0. A measured 0 is legal.
/// Instances come from the factories or from <see cref="MetricJsonConverter"/>, and both run the same constructor checks.
/// </summary>
[JsonConverter(typeof(MetricJsonConverter))]
public sealed record Metric
{
    private Metric(double? value, MetricLabel label, string? reason, string? unit, string? currency)
    {
        if (!Enum.IsDefined(label))
        {
            throw new ArgumentException($"Label {(int)label} is not a defined metric label.", nameof(label));
        }

        if (label is MetricLabel.NotApplicable or MetricLabel.NotReported)
        {
            if (value is not null)
            {
                throw new ArgumentException($"A {label} metric must not carry a value.", nameof(value));
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException($"A {label} metric requires a non-empty reason.", nameof(reason));
            }
        }
        else
        {
            if (value is null)
            {
                throw new ArgumentException($"A {label} metric requires a value.", nameof(value));
            }

            if (!double.IsFinite(value.Value))
            {
                throw new ArgumentException($"A {label} metric requires a finite value.", nameof(value));
            }

            if (reason is not null)
            {
                throw new ArgumentException($"A {label} metric must not carry a reason.", nameof(reason));
            }
        }

        ValidateQualifiers(label, unit, currency);

        Value = value;
        Label = label;
        Reason = reason;
        Unit = unit;
        Currency = currency;
    }

    /// <summary>The number, or null exactly when the label is N/A or not-reported; always serialized, null included.</summary>
    public double? Value { get; }

    /// <summary>The one label this value carries.</summary>
    public MetricLabel Label { get; }

    /// <summary>Why the value is absent; set for N/A and not-reported, omitted from the JSON when null.</summary>
    public string? Reason { get; }

    /// <summary>
    /// The unit of a quantity, for example <c>J</c> or <c>gCO2e</c>; omitted from the JSON when null.
    /// Exactly one of <see cref="Unit"/> and <see cref="Currency"/> is set on a measured or estimated metric; a model-judged metric carries at most one of them.
    /// </summary>
    public string? Unit { get; }

    /// <summary>The currency code of a cost, for example <c>USD</c>; omitted from the JSON when null.</summary>
    public string? Currency { get; }

    /// <summary>Creates a quantity read from hardware or a provider response; 0 is legal.</summary>
    /// <param name="value">The finite number.</param>
    /// <param name="unit">The non-blank unit.</param>
    public static Metric Measured(double value, string unit) => new(value, MetricLabel.Measured, null, unit, null);

    /// <summary>Creates a cost read from a provider response or an invoice.</summary>
    /// <param name="value">The finite amount.</param>
    /// <param name="currency">The non-blank currency code.</param>
    public static Metric MeasuredCost(double value, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        return new(value, MetricLabel.Measured, null, null, currency);
    }

    /// <summary>Creates a cost computed from measured inputs and a price table.</summary>
    /// <param name="value">The finite number.</param>
    /// <param name="currency">The non-blank currency code.</param>
    public static Metric Estimated(double value, string currency) => new(value, MetricLabel.Estimated, null, null, currency);

    /// <summary>Creates a quantity computed from measured inputs and a factor, for example grams of CO2e or running joules.</summary>
    /// <param name="value">The finite number.</param>
    /// <param name="unit">The non-blank unit.</param>
    public static Metric EstimatedQuantity(double value, string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        return new(value, MetricLabel.Estimated, null, unit, null);
    }

    /// <summary>Creates a value scored by a judge model.</summary>
    /// <param name="value">The finite score.</param>
    public static Metric ModelJudged(double value) => new(value, MetricLabel.ModelJudged, null, null, null);

    /// <summary>Creates a value scored by a judge model, with the unit of the score scale.</summary>
    /// <param name="value">The finite score.</param>
    /// <param name="unit">The non-blank unit of the scale.</param>
    public static Metric ModelJudged(double value, string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        return new(value, MetricLabel.ModelJudged, null, unit, null);
    }

    /// <summary>Creates a value derived from a judge score and a cost, with the currency of that cost, for example the cost per quality point.</summary>
    /// <param name="value">The finite amount.</param>
    /// <param name="currency">The non-blank currency code.</param>
    public static Metric ModelJudgedCost(double value, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        return new(value, MetricLabel.ModelJudged, null, null, currency);
    }

    /// <summary>Creates a metric for a value that does not exist for this step.</summary>
    /// <param name="reason">The non-blank explanation.</param>
    public static Metric NotApplicable(string reason) => new(null, MetricLabel.NotApplicable, reason, null, null);

    /// <summary>Creates a metric for a value the source did not supply.</summary>
    /// <param name="reason">The non-blank explanation.</param>
    public static Metric NotReported(string reason) => new(null, MetricLabel.NotReported, reason, null, null);

    // Measured and Estimated carry exactly one of unit and currency; the "primary" one (unit for Measured, currency for Estimated) is
    // named when both are missing, the other when both are present. ModelJudged allows at most one of unit and currency, never both.
    // N/A and not-reported carry neither. A non-null qualifier is never blank.
    private static void ValidateQualifiers(MetricLabel label, string? unit, string? currency)
    {
        if (unit is not null && string.IsNullOrWhiteSpace(unit))
        {
            throw new ArgumentException($"A {label} metric must not carry a blank unit.", nameof(unit));
        }

        if (currency is not null && string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException($"A {label} metric must not carry a blank currency.", nameof(currency));
        }

        switch (label)
        {
            case MetricLabel.Measured:
                RequireExactlyOne(label, unit, currency, primary: nameof(unit), secondary: nameof(currency));
                break;
            case MetricLabel.Estimated:
                RequireExactlyOne(label, unit, currency, primary: nameof(currency), secondary: nameof(unit));
                break;
            case MetricLabel.ModelJudged:
                if (unit is not null && currency is not null)
                {
                    throw new ArgumentException($"A {label} metric carries a unit or a currency, not both.", nameof(currency));
                }

                break;
            default:
                if (unit is not null)
                {
                    throw new ArgumentException($"A {label} metric must not carry a unit.", nameof(unit));
                }

                if (currency is not null)
                {
                    throw new ArgumentException($"A {label} metric must not carry a currency.", nameof(currency));
                }

                break;
        }
    }

    private static void RequireExactlyOne(MetricLabel label, string? unit, string? currency, string primary, string secondary)
    {
        if (unit is null && currency is null)
        {
            throw new ArgumentException($"A {label} metric requires a unit or a currency.", primary);
        }

        if (unit is not null && currency is not null)
        {
            throw new ArgumentException($"A {label} metric carries a unit or a currency, not both.", secondary);
        }
    }

    /// <summary>The entry point of <see cref="MetricJsonConverter"/>: runs the full constructor validation on parts read from JSON.</summary>
    internal static Metric FromParts(double? value, MetricLabel label, string? reason, string? unit, string? currency) =>
        new(value, label, reason, unit, currency);
}