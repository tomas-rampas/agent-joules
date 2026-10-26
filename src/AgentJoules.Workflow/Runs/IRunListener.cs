using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

namespace AgentJoules.Workflow.Runs;

/// <summary>
/// What the <see cref="RunOrchestrator"/> tells the user while a run goes on (REQ-017 criterion 6, EDGE-005, design 3.6 "Run console output"): every finished step when it finishes, the idle
/// baseline of a measured tier, one warning for every tier run in which the judge failed for some steps (EDGE-012), and the file and the totals of every tier that completed. The CLI implements it over its console reporter; the orchestrator never writes to a console itself.
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

    /// <summary>
    /// The checkpoint that ends a tier early could not be written: the <c>incomplete</c> checkpoint of a shutdown (the command still exits as a shutdown) or the <c>failed</c> checkpoint of a
    /// step that failed (the command still exits with the code of the step failure, 5). The listener words the two cases differently.
    /// </summary>
    /// <param name="exceptionTypeName">The type name of the failure, never its message.</param>
    /// <param name="previousCheckpointKept">True when a checkpoint of the tier was written before and stays on disk; false when the tier has no file.</param>
    /// <param name="kind">Which checkpoint could not be written.</param>
    void CheckpointFailed(string exceptionTypeName, bool previousCheckpointKept, CheckpointKind kind);

    /// <summary>The idle baseline of a measured tier was measured, before its first step; an unstable baseline carries <c>baseline-unstable</c> (EDGE-005).</summary>
    /// <param name="baseline">The baseline.</param>
    void BaselineMeasured(BaselineResult baseline);

    /// <summary>The run waits <paramref name="seconds"/> between the warm-up and the idle baseline window (<c>Baseline:SettleSeconds</c>, K1): called once per measured tier run, only when the interval is above 0. The default does nothing, so a listener that does not print it stays valid.</summary>
    /// <param name="seconds">The settle interval in seconds; above 0.</param>
    void SettleStarted(double seconds)
    {
    }

    /// <summary>
    /// The judge could not score some of the steps it was sent in a tier run (EDGE-012): called once per tier run in which at least one judged step failed, after the final document was written and
    /// before <see cref="TierCompleted"/>, and never for a tier whose judge was skipped. The exit code of the command is not affected by any judge failure.
    /// </summary>
    /// <param name="tier">The tier whose steps were judged.</param>
    /// <param name="failedSteps">How many of the judged steps ended with a quality that is N/A; at least 1.</param>
    /// <param name="judgedSteps">How many steps were sent to the judge.</param>
    /// <param name="failureKinds">The distinct failure kinds in ordinal order: exception type names, <c>parse-error</c> or a refusal token; never a message.</param>
    void JudgeFailed(Tier tier, int failedSteps, int judgedSteps, IReadOnlyList<string> failureKinds);

    /// <summary>
    /// The same event with the reasons of the failures (user decision 4 of the M3 exit): one redacted, bounded line per distinct kind, <c>&lt;kind&gt;: &lt;first line of the message&gt;</c>. The orchestrator calls this one; the default
    /// forwards to <see cref="JudgeFailed(Tier, int, int, IReadOnlyList{string})"/> so a listener that does not print reasons stays valid.
    /// </summary>
    /// <param name="tier">The tier whose steps were judged.</param>
    /// <param name="failedSteps">How many of the judged steps ended with a quality that is N/A; at least 1.</param>
    /// <param name="judgedSteps">How many steps were sent to the judge.</param>
    /// <param name="failureKinds">The distinct failure kinds in ordinal order.</param>
    /// <param name="failureDetails">The reason lines, possibly empty.</param>
    void JudgeFailed(Tier tier, int failedSteps, int judgedSteps, IReadOnlyList<string> failureKinds, IReadOnlyList<string> failureDetails) => JudgeFailed(tier, failedSteps, judgedSteps, failureKinds);

    /// <summary>The budget guard of the cloudgpu tier has a fact to tell once per tier run (AJ-122): <c>TierStartUtc not set: the 4-hour box is not enforced</c>. The default does nothing.</summary>
    /// <param name="note">The sentence to tell.</param>
    void BudgetNote(string note)
    {
    }

    /// <summary>The budget guard stopped the cloudgpu tier and the plan has later tiers (AJ-122, EDGE-017): they do not start. Called once, when the stop is decided (before the judge pass), before <see cref="BudgetStopDecided"/>. The default does nothing.</summary>
    /// <param name="tiers">The names of the skipped tiers, in plan order; at least one.</param>
    void BudgetTiersSkipped(IReadOnlyList<string> tiers)
    {
    }

    /// <summary>The budget guard decided to stop the cloudgpu tier between two iterations (AJ-122, EDGE-017): called when the stop is decided, before the judge pass and the final write (peer review M-1); the VM must not be deleted yet. The default does nothing.</summary>
    /// <param name="reason">The stop reason: <c>cost-cap</c> or <c>time-box</c>.</param>
    void BudgetStopDecided(string reason)
    {
    }

    /// <summary>The budget guard stopped the cloudgpu tier between two iterations (AJ-122, EDGE-017): called after the partial file was written and after <see cref="TierCompleted"/>, so the line it prints is the last one. The default does nothing.</summary>
    /// <param name="reason">The stop reason: <c>cost-cap</c> or <c>time-box</c>.</param>
    void BudgetStopped(string reason)
    {
    }

    /// <summary>A tier completed and its final document was written.</summary>
    /// <param name="displayPath">The file as the user names it: the <c>--out</c> value as given, a forward slash and the file name.</param>
    /// <param name="totals">The totals of the final document.</param>
    void TierCompleted(string displayPath, Totals totals);

    /// <summary>
    /// A step failed and stopped the tier (EDGE-006), and the partial file was written: it holds every step published so far and carries <paramref name="status"/>. Called after the write succeeded
    /// (when the write failed, <see cref="CheckpointFailed"/> is told instead) and before the failure leaves the orchestrator, so the lines it prints come before the error line of the command.
    /// </summary>
    /// <param name="displayPath">The partial file as the user names it: the <c>--out</c> value as given, a forward slash and the file name.</param>
    /// <param name="agent">The name of the agent whose step failed.</param>
    /// <param name="status">The status the partial file carries: <see cref="RunStatus.Failed"/>, or <see cref="RunStatus.Incomplete"/> when the connection dropped after the first update.</param>
    void TierFailed(string displayPath, string agent, RunStatus status);

    /// <summary>
    /// The connection of the cloudgpu tier dropped during a call (AJ-123, EDGE-016): the step is <c>incomplete</c>, no further call and no retry follows, and the user must look at the VM in the provider console.
    /// Called after <see cref="TierFailed"/> (or after <see cref="CheckpointFailed"/> when the partial file could not be written) and before the failure leaves the orchestrator. The default does nothing.
    /// </summary>
    void ConnectionLost()
    {
    }
}