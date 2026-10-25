using AgentJoules.Core.Energy;

namespace AgentJoules.TestSupport;

/// <summary>
/// The <see cref="IPowerSourceDetector"/> of the harness (AJ-106, EDGE-011): it answers a fixed value and never reads the host's power state, so the laptop that runs the tests being on its battery or on mains
/// changes no result. The value is <see cref="PowerSources.Unknown"/> unless a test sets another.
/// </summary>
public sealed class FakePowerSourceDetector : IPowerSourceDetector
{
    private int _calls;

    /// <summary>What <see cref="Detect"/> returns; <see cref="PowerSources.Unknown"/> by default. A value outside the vocabulary is returned as it is, so a test can check that the orchestrator keeps it out of the file.</summary>
    public string Value { get; set; } = PowerSources.Unknown;

    /// <summary>How many times <see cref="Detect"/> was called.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc />
    public string Detect()
    {
        _ = Interlocked.Increment(ref _calls);
        return Value;
    }
}