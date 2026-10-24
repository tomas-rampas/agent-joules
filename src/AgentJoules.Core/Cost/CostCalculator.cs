using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;

namespace AgentJoules.Core.Cost;

/// <summary>
/// The cost calculator of M3 (REQ-009, design 3.2): it replaces <see cref="NotConfiguredCostCalculator"/> in the composition root. A hosted step (<c>nvidia</c>, <c>nebius</c>) costs
/// <c>(inputTokens x inputUsdPer1M + outputTokens x outputUsdPer1M) / 1 000 000</c> from the <see cref="PriceRow"/> of its tier and model, labelled <c>estimated</c> in <c>USD</c>; when that
/// cost does not exist it is N/A with a reason, never 0 and never <c>measured</c>.
/// </summary>
/// <remarks>
/// <para>
/// The checks of a hosted step run in this order, and the order is part of the contract: (1) no price row for the tier and the step's model ID (the per-agent override of
/// <c>AgentModelIds</c> already lives in <see cref="StepIdentity.ModelId"/>; the match ignores letter case) gives N/A with the <c>Tiers:&lt;tier&gt;:MissingPriceReason</c> of the tier
/// (<c>free-tier-no-price</c> for nvidia by configuration, <c>price-not-found</c> otherwise), so a free tier reports that it has no price even when its tokens are missing too; (2) either token count
/// not reported (or reported as a negative number, which <see cref="TokenCounts"/> turns into not-reported) gives N/A with the reason <see cref="TokensNotReportedReason"/>; (3) a product that
/// a <see cref="decimal"/> cannot hold, or that comes out not above 0 although a token was counted, gives N/A with the reason <see cref="PriceNotRepresentableReason"/>: the schema of the price file
/// accepts any finite double above 0, including a denormal or a huge one, and the cost must not turn such a price into 0 or into infinity. A stated count of 0 is a count: 0 input and 0 output tokens
/// cost an estimated 0 USD, because a step that consumed nothing at a real price cost nothing.
/// </para>
/// <para>
/// The arithmetic is in <see cref="decimal"/> from the row's doubles and the <see cref="long"/> counts, then converted once to a double, so 1 000 000 input tokens at 0.10 and 500 000 output tokens at
/// 0.40 cost exactly 0.30. The decimal path avoids binary residue only for a result with a terminating decimal expansion; the conversion of a <see cref="decimal"/> to a double is not correctly rounded
/// (1e6 J at 0.30 per kWh gives 0.08333333333333334 through decimal and 0.08333333333333333 in double), so such a result may differ from the double quotient in its last digit. A local step costs <c>net joules / 3 600 000 x Cost:ElectricityPricePerKwh</c>, <c>estimated</c> in <c>Cost:Currency</c>, and is N/A <see cref="EnergyNotMeasuredReason"/> when its net energy is N/A or not-reported and N/A
/// <see cref="BelowBaselineReason"/> when the net is negative (the reading keeps its negative joules); a measured net of exactly 0 costs an estimated 0. A cloudgpu step costs <c>CloudGpu:HourlyPriceUsd x wallSeconds / 3600</c>, <c>estimated</c> in USD
/// (ADR-15: the instance is paid for the time it is held, retry waits included, and no electricity model applies to a rented VM), and is N/A <see cref="HourlyPriceNotConfiguredReason"/> while the price is unset. A product that
/// <see cref="decimal"/> cannot hold, or that is 0 for a quantity that is not 0, is N/A <see cref="PriceNotRepresentableReason"/> in every branch.
/// </para>
/// <para>
/// Precision bound: a double converts to a decimal with 15 significant digits and a decimal holds at most 28 decimal places, so a price below about 1E-13 USD per 1 000 000 tokens can lose digits in the conversion; no realistic price reaches it, and the arithmetic is left as it is. A denormal price, which a decimal reads as 0, gives N/A <see cref="PriceNotRepresentableReason"/> even when the count it multiplies is 0.
/// </para>
/// <para>
/// The calculator reads a <see cref="PriceTable"/> that never changes and a snapshot of the missing-price reasons and of the cost and cloudgpu options (electricity price, currency, hourly price) taken at construction, so one instance is thread-safe and is shared by every metered
/// client of a run.
/// </para>
/// </remarks>
public sealed class CostCalculator : ICostCalculator
{
    /// <summary>The currency of every hosted cost: the price file holds USD per 1 000 000 tokens.</summary>
    public const string HostedCurrency = "USD";

    /// <summary>The reason of a hosted step whose input or output token count was not reported.</summary>
    public const string TokensNotReportedReason = "tokens-not-reported";

    /// <summary>The reason of a hosted step with a counted token that prices to 0, or whose price or product overflows.</summary>
    public const string PriceNotRepresentableReason = "price-not-representable";

    /// <summary>The reason of a local step, or of an emission total, whose energy is N/A or not-reported: no energy was measured, so no cost or emission can be derived.</summary>
    public const string EnergyNotMeasuredReason = "energy-not-measured";

    /// <summary>The reason of a local step, or of an emission total, whose net energy is negative (the reading carries <c>below-baseline</c>): a negative cost is meaningless; the negative joules stay in the reading.</summary>
    public const string BelowBaselineReason = "below-baseline";

    /// <summary>The reason of a cloudgpu step while <c>CloudGpu:HourlyPriceUsd</c> is not set.</summary>
    public const string HourlyPriceNotConfiguredReason = "hourly-price-not-configured";

    /// <summary>The currency of every cloudgpu cost: the rental price is configured in USD per hour.</summary>
    public const string CloudGpuCurrency = "USD";

    /// <summary>The reason of an emission total while <c>Cost:GCo2ePerKwh</c> is not set.</summary>
    public const string NotConfiguredReason = "not-configured";

    /// <summary>The unit of an emission estimate.</summary>
    public const string EmissionUnit = "gCO2e";

    private const decimal TokensPerPriceUnit = 1_000_000m;
    private const decimal JoulesPerKwh = 3_600_000m;
    private const decimal SecondsPerHour = 3600m;

    // The default of Tiers:<tier>:MissingPriceReason, read from the options class so that the two cannot drift apart.
    private static readonly string DefaultMissingPriceReason = new TierOptions().MissingPriceReason;

    private readonly PriceTable _prices;
    private readonly Dictionary<string, string> _missingPriceReasons;
    private readonly double _electricityPricePerKwh;
    private readonly string _currency;
    private readonly double? _hourlyPriceUsd;

    /// <summary>Creates a calculator over a price table and the tier configuration.</summary>
    /// <param name="prices">The loaded price table.</param>
    /// <param name="tiers">The configured tiers by name; each tier's <see cref="TierOptions.MissingPriceReason"/> is copied now, so a later change of the options does not reach the calculator. A tier that is absent, or whose reason is blank, reports <c>price-not-found</c>.</param>
    /// <param name="cost">The <c>Cost</c> section: the electricity price per kWh and its currency for a local step; null takes the defaults of <see cref="CostOptions"/>; a non-null section must hold a currency code (see <see cref="CostOptions.Currency"/>). The values are copied now.</param>
    /// <param name="cloudGpu">The <c>CloudGpu</c> section: <see cref="CloudGpuOptions.HourlyPriceUsd"/> prices a cloudgpu step by time; null leaves that price unset. The value is copied now.</param>
    /// <exception cref="ArgumentNullException"><paramref name="prices"/> or <paramref name="tiers"/> is null.</exception>
    /// <exception cref="ArgumentException">The currency of <paramref name="cost"/> is not three upper-case letters (the message never shows it).</exception>
    public CostCalculator(PriceTable prices, IReadOnlyDictionary<string, TierOptions> tiers, CostOptions? cost = null, CloudGpuOptions? cloudGpu = null)
    {
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(tiers);

        _prices = prices;
        _electricityPricePerKwh = cost?.ElectricityPricePerKwh ?? CostOptions.DefaultElectricityPricePerKwh;
        _currency = cost is null ? CostOptions.DefaultCurrency : CostOptions.IsCurrencyCode(cost.Currency) ? cost.Currency : throw new ArgumentException("The currency must be three upper-case letters.", nameof(cost));
        _hourlyPriceUsd = cloudGpu?.HourlyPriceUsd;
        _missingPriceReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, TierOptions? options) in tiers)
        {
            if (options is not null && !string.IsNullOrWhiteSpace(options.MissingPriceReason))
            {
                _missingPriceReasons[name] = options.MissingPriceReason;
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="identity"/>, <paramref name="tokens"/> or <paramref name="energy"/> is null.</exception>
    public Metric Compute(StepIdentity identity, TokenCounts tokens, EnergyReading energy, double wallSeconds)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(energy);

        return TierKinds.IsHosted(identity.Tier)
            ? ComputeHosted(identity, tokens)
            : identity.Tier switch
            {
                Tier.Local => ComputeLocal(energy),
                Tier.CloudGpu => ComputeCloudGpu(wallSeconds),
                _ => Metric.NotApplicable(NotConfiguredCostCalculator.Reason),
            };
    }

    /// <summary>
    /// Estimates the grams of CO2e of a run from its net energy total and the carbon factor (REQ-009, design 3.2): <c>net kWh x factor</c>, computed in <see cref="decimal"/>, <c>estimated</c> with the unit <c>gCO2e</c>.
    /// No factor gives N/A <c>not-configured</c>; a net total that is N/A or not-reported (a hosted tier, a concurrent run whose union was not computed, a run without a step) gives N/A <see cref="EnergyNotMeasuredReason"/>;
    /// a negative net total gives N/A <see cref="BelowBaselineReason"/> because a negative emission is meaningless; a product a decimal cannot hold, or one that is 0 for a measured joule, gives N/A <see cref="PriceNotRepresentableReason"/>.
    /// A measured net total of exactly 0 is an estimated 0.
    /// </summary>
    /// <param name="netJoules">The net energy total of the run.</param>
    /// <param name="gramsPerKwh">The factor <c>Cost:GCo2ePerKwh</c>, or null when it is not set.</param>
    /// <returns>The estimated grams of CO2e, or N/A with a reason; never a bare 0 for an emission that does not exist.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="netJoules"/> is null.</exception>
    public static Metric EstimateEmissions(Metric netJoules, double? gramsPerKwh)
    {
        ArgumentNullException.ThrowIfNull(netJoules);

        return gramsPerKwh is not { } factor
            ? Metric.NotApplicable(NotConfiguredReason)
            : netJoules is not { Label: MetricLabel.Measured, Value: { } joules }
                ? Metric.NotApplicable(EnergyNotMeasuredReason)
                : joules < 0.0
                    ? Metric.NotApplicable(BelowBaselineReason)
                    : Scale(joules, factor, JoulesPerKwh) is { } grams
                        ? Metric.EstimatedQuantity(grams, EmissionUnit)
                        : Metric.NotApplicable(PriceNotRepresentableReason);
    }

    // quantity x rate / divisor in decimal, converted once to a double; null when no real amount exists: a rate or quantity a decimal cannot hold (overflow, NaN, infinity), a rate a decimal reads as 0,
    // a negative result, or a result of 0 for a quantity that is not exactly 0 (a denormal, or a product below the smallest decimal). A quantity of exactly 0 gives an amount of exactly 0.
    private static double? Scale(double quantity, double rate, decimal divisor)
    {
        decimal product;
        try
        {
            decimal unit = (decimal)rate;
            if (unit <= 0)
            {
                return null;
            }

            product = (decimal)quantity * unit / divisor;
        }
        catch (OverflowException)
        {
            return null;
        }

        double value = (double)product;
        return value > 0 || (quantity == 0.0 && value == 0.0) ? value : null;
    }

    // The local tier (design 3.2, Cost row): the net energy of the step times the electricity price. N/A when the energy was not measured, N/A when it is negative (below the baseline; the reading keeps the negative joules).
    private Metric ComputeLocal(EnergyReading energy) =>
        energy.NetJoules is not { Label: MetricLabel.Measured, Value: { } netJoules }
            ? Metric.NotApplicable(EnergyNotMeasuredReason)
            : netJoules < 0.0
                ? Metric.NotApplicable(BelowBaselineReason)
                : Scale(netJoules, _electricityPricePerKwh, JoulesPerKwh) is { } cost
                    ? Metric.Estimated(cost, _currency)
                    : Metric.NotApplicable(PriceNotRepresentableReason);

    // The cloudgpu tier (ADR-15): the rental price of the hour times the wall time the instance was held; no electricity model applies to a rented VM, so the energy reading is not looked at.
    private Metric ComputeCloudGpu(double wallSeconds) =>
        _hourlyPriceUsd is not { } hourly
            ? Metric.NotApplicable(HourlyPriceNotConfiguredReason)
            : Scale(wallSeconds, hourly, SecondsPerHour) is { } cost
                ? Metric.Estimated(cost, CloudGpuCurrency)
                : Metric.NotApplicable(PriceNotRepresentableReason);

    private static Metric Priced(PriceRow row, long inputCount, long outputCount)
    {
        decimal cost;
        try
        {
            decimal inputPrice = (decimal)row.InputUsdPer1M;
            decimal outputPrice = (decimal)row.OutputUsdPer1M;
            if (inputPrice <= 0 || outputPrice <= 0)
            {
                // A price the schema accepts as a double above 0 but that a decimal reads as 0.
                return Metric.NotApplicable(PriceNotRepresentableReason);
            }

            cost = ((inputCount * inputPrice) + (outputCount * outputPrice)) / TokensPerPriceUnit;
        }
        catch (OverflowException)
        {
            return Metric.NotApplicable(PriceNotRepresentableReason);
        }

        // A decimal is always finite as a double, so the only check left is the sign.
        double value = (double)cost;
        return value > 0 || (inputCount == 0 && outputCount == 0)
            ? Metric.Estimated(value, HostedCurrency)
            : Metric.NotApplicable(PriceNotRepresentableReason);
    }

    private Metric ComputeHosted(StepIdentity identity, TokenCounts tokens)
    {
        string tierName = TierOptions.NameOf(identity.Tier);
        PriceRow? row = _prices.Find(tierName, identity.ModelId);

        return row is null
            ? Metric.NotApplicable(_missingPriceReasons.GetValueOrDefault(tierName, DefaultMissingPriceReason))
            : tokens.InputCount is { } inputCount && tokens.OutputCount is { } outputCount
                ? Priced(row, inputCount, outputCount)
                : Metric.NotApplicable(TokensNotReportedReason);
    }
}