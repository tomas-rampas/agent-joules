namespace AgentJoules.Core.Energy;

/// <summary>
/// The time slice a measurement window covers, in seconds since the meter started.
/// </summary>
/// <param name="StartSeconds">The timestamp of the edge sample taken when the window was opened.</param>
/// <param name="StopSeconds">The timestamp of the edge sample taken when the window was closed; equal to <paramref name="StartSeconds"/> until then.</param>
public readonly record struct WindowSpan(double StartSeconds, double StopSeconds)
{
    /// <summary>The length of the window in seconds: stop minus start.</summary>
    public double DurationSeconds => StopSeconds - StartSeconds;
}