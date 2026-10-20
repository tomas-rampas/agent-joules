using System.Text.Json.Serialization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;

namespace AgentJoules.Core.Results;

/// <summary>
/// The root of a results file (REQ-015, REQ-017), mirroring the skeleton of the prompt member for member with camelCase names. The run orchestrator
/// builds one at the start with <c>run.status</c> <c>running</c> and writes a new one after every (task, repeat) through <c>IRunCheckpointWriter</c>.
/// <see cref="StepReading"/> is the element of <see cref="Steps"/> and <see cref="Judge"/>. M3 hardens the records (invariants) and generates the
/// schema from them.
/// </summary>
/// <param name="SchemaVersion">The version of the file layout; <see cref="CurrentSchemaVersion"/>.</param>
/// <param name="Run">Who ran what and where the run stands.</param>
/// <param name="Environment">What the run ran on.</param>
/// <param name="Probe">The GPU probe of a measured tier, or null on a hosted tier.</param>
/// <param name="Baseline">The idle baseline of a measured tier, or null on a hosted tier.</param>
/// <param name="Steps">The readings of the workflow steps finished so far.</param>
/// <param name="Judge">The readings of the judge calls (the same shape as a step).</param>
/// <param name="Totals">The sums over the steps.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunDocument(
    string SchemaVersion,
    RunInfo Run,
    EnvironmentInfo Environment,
    ProbeResult? Probe,
    BaselineResult? Baseline,
    IReadOnlyList<StepReading> Steps,
    IReadOnlyList<StepReading> Judge,
    Totals Totals)
{
    /// <summary>The only schema version there is today.</summary>
    public const string CurrentSchemaVersion = "1";
}