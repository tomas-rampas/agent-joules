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
/// 0.40 cost exactly 0.30 and no binary-float residue reaches the results file. The local and cloudgpu tiers answer N/A <see cref="NotConfiguredCostCalculator.Reason"/> until the electricity and
/// rental branches of AJ-072 replace those two answers.
/// </para>
/// <para>
/// Precision bound: a double converts to a decimal with 15 significant digits and a decimal holds at most 28 decimal places, so a price below about 1E-13 USD per 1 000 000 tokens can lose digits in the conversion; no realistic price reaches it, and the arithmetic is left as it is. A denormal price, which a decimal reads as 0, gives N/A <see cref="PriceNotRepresentableReason"/> even when the count it multiplies is 0.
/// </para>
/// <para>
/// The calculator reads a <see cref="PriceTable"/> that never changes and a snapshot of the missing-price reasons taken at construction, so one instance is thread-safe and is shared by every metered
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

    private const decimal TokensPerPriceUnit = 1_000_000m;

    // The default of Tiers:<tier>:MissingPriceReason, read from the options class so that the two cannot drift apart.
    private static readonly string DefaultMissingPriceReason = new TierOptions().MissingPriceReason;

    private readonly PriceTable _prices;
    private readonly Dictionary<string, string> _missingPriceReasons;

    /// <summary>Creates a calculator over a price table and the tier configuration.</summary>
    /// <param name="prices">The loaded price table.</param>
    /// <param name="tiers">The configured tiers by name; each tier's <see cref="TierOptions.MissingPriceReason"/> is copied now, so a later change of the options does not reach the calculator. A tier that is absent, or whose reason is blank, reports <c>price-not-found</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="prices"/> or <paramref name="tiers"/> is null.</exception>
    public CostCalculator(PriceTable prices, IReadOnlyDictionary<string, TierOptions> tiers)
    {
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(tiers);

        _prices = prices;
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

        return TierKinds.IsHosted(identity.Tier) ? ComputeHosted(identity, tokens) : Metric.NotApplicable(NotConfiguredCostCalculator.Reason);
    }

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