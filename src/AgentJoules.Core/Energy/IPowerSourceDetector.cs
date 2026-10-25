namespace AgentJoules.Core.Energy;

/// <summary>
/// Tells where the machine takes its power from (REQ-021, AJ-106): the orchestrator reads it once per tier run, before the idle baseline, and writes it as <c>environment.powerSource</c>. The production
/// implementation reads the operating system (Windows <c>GetSystemPowerStatus</c>, Linux <c>/sys/class/power_supply</c>); a test harness registers one that never touches the host (EDGE-011).
/// </summary>
public interface IPowerSourceDetector
{
    /// <summary>Reads the power source now.</summary>
    /// <returns><see cref="PowerSources.Ac"/>, <see cref="PowerSources.Battery"/> or <see cref="PowerSources.Unknown"/>; an implementation never throws and never returns another value.</returns>
    string Detect();
}