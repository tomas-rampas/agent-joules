using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;

using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.TestSupport;

/// <summary>
/// The pieces that the unit tests and the BDD steps of AJ-071 (REQ-009, hosted cost) share: the tier configuration the calculator takes its missing-price reasons from, a step
/// identity, and the closed measurement window of a hosted step. The price rows themselves come from <see cref="PriceFileFixtures"/>.
/// </summary>
public static class CostFixtures
{
    /// <summary>The reason the committed configuration gives the nvidia tier for a model without a price row.</summary>
    public const string FreeTierReason = "free-tier-no-price";

    /// <summary>The reason a tier reports when its <c>MissingPriceReason</c> is not set.</summary>
    public const string DefaultMissingReason = "price-not-found";

    /// <summary>The tiers as the shipped <c>config/appsettings.json</c> configures their missing-price reasons: nvidia is a free tier, every other tier keeps the default.</summary>
    /// <returns>A new dictionary keyed by tier name.</returns>
    public static Dictionary<string, TierOptions> ShippedTiers() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["local"] = new TierOptions(),
        ["cloudgpu"] = new TierOptions(),
        ["nvidia"] = new TierOptions { MissingPriceReason = FreeTierReason },
        ["nebius"] = new TierOptions(),
    };

    /// <summary>The identity of one step.</summary>
    /// <param name="tier">The tier of the step.</param>
    /// <param name="modelId">The model ID the step goes to.</param>
    /// <param name="agent">The agent name.</param>
    /// <returns>The identity.</returns>
    public static StepIdentity Identity(Tier tier, string modelId, string agent = "Writer") => new(agent, modelId, tier, "summarise", 1, false);

    /// <summary>The closed window of a hosted step: its energy is N/A with the reason <c>hosted-tier</c>, and a calculator must not read it.</summary>
    /// <returns>The reading, one second long on a fake clock.</returns>
    public static EnergyReading HostedEnergy()
    {
        var time = new FakeTimeProvider();
        var meter = new UnmeteredEnergyMeter(time);
        meter.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        IMeterWindow window = meter.OpenWindow();
        time.Advance(TimeSpan.FromSeconds(1));
        EnergyReading reading = window.Close();
        meter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return reading;
    }

    /// <summary>The closed window of a local step that measured <paramref name="netJoules"/> of net energy; a negative net carries the <c>below-baseline</c> flag, as the reading requires.</summary>
    /// <param name="netJoules">The net energy in joules; finite.</param>
    /// <returns>The reading, one second long, sampled at 10 Hz.</returns>
    public static EnergyReading LocalEnergy(double netJoules) =>
        new(
            Metric.Measured(netJoules + 1000.0, "J"),
            Metric.Measured(netJoules, "J"),
            Metric.Measured(90.0, "W"),
            Metric.Measured(110.0, "W"),
            durationSeconds: 1.0,
            EnergyMethod.Sampling,
            sampleCount: 11,
            sampleRateHz: 10.0,
            netJoules < 0.0 ? ReadingFlags.BelowBaseline : ReadingFlags.None,
            throttleReasons: null);

    /// <summary>The closed window of a local step whose energy could not be measured: every energy metric is N/A with the given reason.</summary>
    /// <param name="reason">The reason of the absence.</param>
    /// <param name="notReported">True for not-reported metrics, false for N/A.</param>
    /// <returns>The reading.</returns>
    public static EnergyReading LocalEnergyAbsent(string reason, bool notReported = false)
    {
        Metric absent = notReported ? Metric.NotReported(reason) : Metric.NotApplicable(reason);
        return new EnergyReading(absent, absent, absent, absent, durationSeconds: 1.0, EnergyMethod.Sampling, sampleCount: 0, sampleRateHz: 10.0, ReadingFlags.None, throttleReasons: null);
    }
}