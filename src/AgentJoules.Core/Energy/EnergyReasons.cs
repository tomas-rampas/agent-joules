namespace AgentJoules.Core.Energy;

/// <summary>
/// The reasons of an energy metric that is not reported, shared by the reading of one window (<see cref="ReadingCalculator"/>), the idle baseline (<see cref="BaselineCalculator"/>) and the energy of
/// the union of several windows (<see cref="UnionWindowCalculator"/>), so the two say the same thing for the same cause (design 3.2).
/// </summary>
internal static class EnergyReasons
{
    /// <summary>Fewer than two samples cover a window or a merged segment, so there is no integral.</summary>
    internal const string InsufficientSamples = "insufficient-samples";

    /// <summary>A counter mark at an edge of a window or a merged segment is missing.</summary>
    internal const string CounterNotRead = "counter-not-read";

    /// <summary>The counter difference over a window or a merged segment is negative.</summary>
    internal const string CounterWentBackwards = "counter-went-backwards";

    /// <summary>No idle baseline is stored, so there is no net energy.</summary>
    internal const string NoBaseline = "no-baseline";
}