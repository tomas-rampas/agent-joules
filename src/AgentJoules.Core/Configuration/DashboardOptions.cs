namespace AgentJoules.Core.Configuration;

/// <summary>The <c>Dashboard</c> section of <c>AgentJoules</c> (REQ-032, design 3.10): how the live dashboard is fed.</summary>
/// <remarks>A mutable class with plain get and set accessors so that the configuration binding generator binds it.</remarks>
public sealed class DashboardOptions
{
    /// <summary>
    /// Key <c>Dashboard:PowerIntervalMs</c>, default 100: the shortest time in milliseconds between two power samples forwarded to the dashboard; a whole number from 50 to 1000.
    /// An interval shorter than the sampling period forwards every sample. Forwarding never reads the GPU: it reuses the samples the meter already took.
    /// </summary>
    [MinimumValue(50, Maximum = 1000, ErrorMessage = "Dashboard:PowerIntervalMs must be a whole number from 50 to 1000")]
    public int PowerIntervalMs { get; set; } = 100;
}