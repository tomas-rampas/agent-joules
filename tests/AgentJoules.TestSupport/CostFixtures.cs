using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
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
}