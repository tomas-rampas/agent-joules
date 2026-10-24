namespace AgentJoules.Core.Configuration;

/// <summary>The <c>Cost</c> section of <c>AgentJoules</c>: the electricity price that turns the net energy of a local step into money, the carbon factor that turns it into grams of CO2e, and the currency of the former.</summary>
/// <remarks>
/// A mutable class with plain get and set accessors so that the configuration binding generator binds it; a flat annotated section checked by <see cref="FlatSectionsValidator"/>, whose messages
/// name the key and never the value. The price is an assumption, not a measurement: every cost and emission computed from it is labelled <c>estimated</c> (REQ-009), and
/// <c>environment.assumptions</c> repeats it so a reader can see what was assumed. The cloudgpu tier is priced by rental time (<see cref="CloudGpuOptions.HourlyPriceUsd"/>) and uses none of these keys for its cost.
/// </remarks>
public sealed class CostOptions
{
    /// <summary>The default of <see cref="ElectricityPricePerKwh"/>: 0.30 per kWh.</summary>
    public const double DefaultElectricityPricePerKwh = 0.30;

    /// <summary>The default of <see cref="Currency"/>.</summary>
    public const string DefaultCurrency = "EUR";

    /// <summary>Key <c>Cost:ElectricityPricePerKwh</c>, default 0.30: the price of one kWh in <see cref="Currency"/>; finite and greater than 0.</summary>
    [MinimumValue(0.0, IsExclusive = true, ErrorMessage = "Cost:ElectricityPricePerKwh must be a finite number greater than 0")]
    public double ElectricityPricePerKwh { get; set; } = DefaultElectricityPricePerKwh;

    /// <summary>Key <c>Cost:Currency</c>, default <c>EUR</c>: the currency code of the electricity price and of every local cost, exactly three upper-case letters.</summary>
    [CurrencyCode(ErrorMessage = "Cost:Currency must be three upper-case letters")]
    public string Currency { get; set; } = DefaultCurrency;

    /// <summary>
    /// Key <c>Cost:GCo2ePerKwh</c>, default null: grams of CO2e emitted per kWh; null leaves <c>totals.gco2e</c> N/A <c>not-configured</c>, and a value must be finite and greater than 0. There is no
    /// default factor: a grid mix is a place and a year, and a guessed one would be a number nobody measured.
    /// </summary>
    [MinimumValue(0.0, IsExclusive = true, ErrorMessage = "Cost:GCo2ePerKwh must be a finite number greater than 0 when it is set")]
    public double? GCo2ePerKwh { get; set; }

    /// <summary>The one rule of a currency code: exactly three upper-case ASCII letters. Null, empty and white space fail it.</summary>
    /// <param name="value">The candidate code.</param>
    /// <returns>True when <paramref name="value"/> is a currency code.</returns>
    internal static bool IsCurrencyCode(string? value) => value is { Length: 3 } text && text.All(char.IsAsciiLetterUpper);
}