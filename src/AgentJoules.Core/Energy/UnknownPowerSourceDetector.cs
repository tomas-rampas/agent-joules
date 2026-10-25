namespace AgentJoules.Core.Energy;

/// <summary>
/// The <see cref="IPowerSourceDetector"/> that reads nothing and answers <see cref="PowerSources.Unknown"/> (REQ-021, EDGE-011): the floor under every host built with test overrides, so a test that runs the command never
/// depends on whether the machine that runs it is on its battery, whatever harness it uses. A test that wants another answer registers its own detector after it.
/// </summary>
public sealed class UnknownPowerSourceDetector : IPowerSourceDetector
{
    /// <inheritdoc />
    /// <returns>Always <see cref="PowerSources.Unknown"/>.</returns>
    public string Detect() => PowerSources.Unknown;
}