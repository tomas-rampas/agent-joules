using AgentJoules.Metering;

namespace AgentJoules.TestSupport;

/// <summary>Builds the instruments the tests that only need a complete <see cref="MeteringServices"/> pass along.</summary>
public static class TestInstruments
{
    // One factory and one set of instruments for the whole test run: the factory is never disposed, so its meter lives as long as the process, and no call allocates a meter of its own.
    private static readonly AgentJoulesInstruments Shared = new(new TestMeterFactory());

    /// <summary>
    /// Returns the instruments over a <see cref="TestMeterFactory"/>; the factory and the instruments are shared by every caller and live as long as the process of the test run. They record
    /// on the meter <c>AgentJoules</c>, so a meter provider that listens to that name (the telemetry setup, a telemetry rig) sees the measurements of every test that uses them; a test that reads
    /// measurements therefore filters by an agent name of its own, as <see cref="TelemetryRig"/> does.
    /// </summary>
    /// <returns>The shared instruments.</returns>
    public static AgentJoulesInstruments Create() => Shared;
}