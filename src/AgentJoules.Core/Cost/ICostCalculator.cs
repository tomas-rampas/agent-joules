using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;

namespace AgentJoules.Core.Cost;

/// <summary>
/// The only way the metering decorator obtains the cost of a step (REQ-008, design 3.2): the decorator holds no price, no currency and no
/// arithmetic, it asks this seam and puts the answer into the reading. M2 ships <see cref="NotConfiguredCostCalculator"/>; the price-table
/// calculator of M3 replaces it by registration.
/// </summary>
/// <remarks>
/// One instance is shared by every metered client of a run, and clients run concurrently in concurrent mode, so an implementation must be thread-safe.
/// The wall-seconds argument is currently the latency of the call; once the retry layer (AJ-045) reports its waits they are excluded from it, and AJ-045
/// decides which figure the hourly price of the cloudgpu tier uses.
/// </remarks>
public interface ICostCalculator
{
    /// <summary>Computes the cost of one step.</summary>
    /// <param name="identity">Who the step is: the tier and the model the price depends on.</param>
    /// <param name="tokens">The token counts of the step; either count may be not-reported, and a calculator then has no basis for a price.</param>
    /// <param name="energy">The closed measurement window of the step; a hosted tier's window is the unmetered one.</param>
    /// <param name="wallSeconds">The wall time of the call in seconds, for a tier that is priced by time.</param>
    /// <returns>The cost: estimated with a currency, or N/A or not-reported with a reason; never a bare 0 for a cost that does not exist.</returns>
    Metric Compute(StepIdentity identity, TokenCounts tokens, EnergyReading energy, double wallSeconds);
}