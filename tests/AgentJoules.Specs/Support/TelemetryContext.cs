using AgentJoules.TestSupport;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-051 scenario (the OpenTelemetry instruments and the telemetry setup), handed to the step class by Reqnroll context injection. It owns the in-memory
/// pipeline of the scenario (<see cref="TelemetryRig"/>, which gives the scenario an agent name of its own, so scenarios running in parallel do not read each other's points),
/// the configuration override the setup reads, and the service collection the setup built. No request leaves the process.
/// </summary>
public sealed class TelemetryContext : IDisposable
{
    /// <summary>The in-memory pipeline, built by a Given step of a scenario that records calls.</summary>
    public TelemetryRig? Rig { get; set; }

    /// <summary>What the metered fake call of the scenario looks like.</summary>
    public TelemetryCall Call { get; set; } = new();

    /// <summary>The points of this scenario's agent, read after the flush.</summary>
    public IReadOnlyList<MeasurementPoint> Points { get; set; } = [];

    /// <summary>The configuration override the setup reads; empty unless a Given step sets the endpoint.</summary>
    public IConfiguration Configuration { get; set; } = new ConfigurationBuilder().Build();

    /// <summary>Whether the console exporter is requested; the commands that print metrics ask for it unless <c>--no-console-telemetry</c> is given.</summary>
    public bool ConsoleEnabled { get; set; } = true;

    /// <summary>The service collection the telemetry setup was added to.</summary>
    public ServiceCollection? Services { get; set; }

    /// <summary>The rig of the scenario.</summary>
    /// <exception cref="InvalidOperationException">No Given step built it.</exception>
    public TelemetryRig RequireRig() => Rig ?? throw new InvalidOperationException("No Given step built the in-memory exporter.");

    /// <inheritdoc />
    public void Dispose() => Rig?.Dispose();
}