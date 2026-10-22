using AgentJoules.Core.Labels;
using AgentJoules.Core.Quality;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-074 scenario (joules and cost per quality point), handed to the step class by Reqnroll context injection. The Given steps set the net energy, the cost, the quality and the flags
/// of one step; the When step runs the real <see cref="DerivedMetrics"/> over them and keeps both results. There is no HTTP, no judge model and no file.
/// </summary>
public sealed class DerivedMetricsContext
{
    /// <summary>The net energy of the step; N/A until a Given step sets it.</summary>
    public Metric NetJoules { get; set; } = Metric.NotApplicable("not-set");

    /// <summary>The cost of the step; N/A until a Given step sets it.</summary>
    public Metric Cost { get; set; } = Metric.NotApplicable("not-set");

    /// <summary>The quality of the step; N/A until a Given step sets it.</summary>
    public Metric Quality { get; set; } = Metric.NotApplicable("not-scored");

    /// <summary>The reading flags of the step.</summary>
    public ReadingFlags Flags { get; set; } = ReadingFlags.None;

    /// <summary>The joules per quality point of the single computation of a scenario, or null before it ran.</summary>
    public Metric? JoulesPerQualityPoint { get; private set; }

    /// <summary>The cost per quality point of the single computation of a scenario, or null before it ran.</summary>
    public Metric? CostPerQualityPoint { get; private set; }

    /// <summary>Computes both derived metrics of the step with <see cref="DerivedMetrics"/> and keeps them.</summary>
    public void Compute()
    {
        JoulesPerQualityPoint = DerivedMetrics.JoulesPerQualityPoint(NetJoules, Quality, Flags);
        CostPerQualityPoint = DerivedMetrics.CostPerQualityPoint(Cost, Quality);
    }
}