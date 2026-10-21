using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;

namespace AgentJoules.Workflow.Runs;

/// <summary>
/// What the <see cref="RunOrchestrator"/> tells the user while a run goes on (REQ-017 criterion 6, EDGE-005, design 3.6 "Run console output"): every finished step when it finishes, the idle
/// baseline of a measured tier, and the file and the totals of every tier that completed. The CLI implements it over its console reporter; the orchestrator never writes to a console itself.
/// </summary>
public interface IRunListener
{
    /// <summary>
    /// A step finished: called once per reading, when the reading is published, from the thread of the call that produced it. A reading the run token cut (outcome <c>cancelled</c>
    /// while the token is cancelled) is not told at publish time: the shutdown path tells its copy with the outcome <c>incomplete</c> after the checkpoint is written, so the console and the
    /// file show the same outcome. A <c>cancelled</c> reading published while the run token is not cancelled (a consumer that walked away) is told like any other.
    /// </summary>
    /// <param name="reading">The reading of the step.</param>
    void StepFinished(StepReading reading);

    /// <summary>The <c>incomplete</c> checkpoint of a shutdown could not be written; the command still exits as a shutdown.</summary>
    /// <param name="exceptionTypeName">The type name of the failure, never its message.</param>
    /// <param name="previousCheckpointKept">True when a checkpoint of the tier was written before and stays on disk; false when the tier has no file.</param>
    void CheckpointFailed(string exceptionTypeName, bool previousCheckpointKept);

    /// <summary>The idle baseline of a measured tier was measured, before its first step; an unstable baseline carries <c>baseline-unstable</c> (EDGE-005).</summary>
    /// <param name="baseline">The baseline.</param>
    void BaselineMeasured(BaselineResult baseline);

    /// <summary>A tier completed and its final document was written.</summary>
    /// <param name="displayPath">The file as the user names it: the <c>--out</c> value as given, a forward slash and the file name.</param>
    /// <param name="totals">The totals of the final document.</param>
    void TierCompleted(string displayPath, Totals totals);
}