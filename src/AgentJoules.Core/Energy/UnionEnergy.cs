using AgentJoules.Core.Labels;

using static AgentJoules.Core.Energy.EnergyShapeRules;

namespace AgentJoules.Core.Energy;

/// <summary>
/// The energy of the union of a run's windows: the run-level energy of a concurrent run, whose overlapping windows would count the same joules twice
/// if their readings were added up (REQ-013, EDGE-004, design 3.2 "Union"). <see cref="UnionWindowCalculator"/> computes it and
/// <see cref="IEnergyMeter.ComputeUnion"/> hands it to the run assembler.
/// </summary>
/// <remarks>
/// The constructor checks every invariant, so an instance that exists is well formed. Both metrics are measured in joules, not-reported with a reason,
/// or N/A (a hosted tier, reason <c>hosted-tier</c>), never 0 for a value that is missing. A measured net requires a measured gross, because the net
/// is the gross minus the baseline power times <see cref="UnionSeconds"/>; a negative net is kept as a measured negative value (it carries no flag,
/// because the totals have none; the per-step <c>below-baseline</c> flags stay on the steps).
/// </remarks>
public sealed record UnionEnergy
{
    /// <summary>Creates the energy of a union after checking every invariant.</summary>
    /// <param name="grossJoules">The gross energy over the merged segments: measured in J, not-reported with a reason, or N/A.</param>
    /// <param name="netJoules">The gross minus the baseline power times <paramref name="unionSeconds"/>: measured in J only when the gross is, otherwise not-reported or N/A.</param>
    /// <param name="unionSeconds">The summed length of the merged segments in seconds; finite and positive.</param>
    /// <param name="segmentCount">The number of merged segments; at least 1.</param>
    /// <exception cref="ArgumentNullException">A metric is null.</exception>
    /// <exception cref="ArgumentException">A metric is estimated or model-judged, a measured metric is not in J, or the net is measured while the gross is not.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="unionSeconds"/> is not finite and positive, or <paramref name="segmentCount"/> is below 1.</exception>
    public UnionEnergy(Metric grossJoules, Metric netJoules, double unionSeconds, int segmentCount)
    {
        ArgumentNullException.ThrowIfNull(grossJoules);
        ArgumentNullException.ThrowIfNull(netJoules);
        RequireShape(nameof(grossJoules), grossJoules, JouleUnit);
        RequireShape(nameof(netJoules), netJoules, JouleUnit);
        if (netJoules.Label == MetricLabel.Measured && grossJoules.Label != MetricLabel.Measured)
        {
            throw Rejected(nameof(netJoules), $"A measured net requires a measured gross, but the gross is {grossJoules.Label}.");
        }

        if (!double.IsFinite(unionSeconds) || unionSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(unionSeconds), unionSeconds, "The union of the windows must last a finite, positive number of seconds.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(segmentCount, 1);

        GrossJoules = grossJoules;
        NetJoules = netJoules;
        UnionSeconds = unionSeconds;
        SegmentCount = segmentCount;
    }

    /// <summary>The gross energy over the merged segments of the windows, in joules, or the reason it is missing.</summary>
    public Metric GrossJoules { get; }

    /// <summary>The gross energy minus the baseline power times <see cref="UnionSeconds"/>, in joules, or the reason it is missing (<c>no-baseline</c> without a baseline).</summary>
    public Metric NetJoules { get; }

    /// <summary>The summed length of the merged segments in seconds: the time at least one window was open.</summary>
    public double UnionSeconds { get; }

    /// <summary>The number of merged segments; windows that only touch at a boundary form two segments.</summary>
    public int SegmentCount { get; }
}