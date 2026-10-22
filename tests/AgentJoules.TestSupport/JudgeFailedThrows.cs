using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Runs;

namespace AgentJoules.TestSupport;

/// <summary>
/// The run listener of the command, except that it fails when told that the judge failed (EDGE-012): every member is forwarded, and <see cref="JudgeFailed"/> throws an
/// <see cref="IOException"/> whose message is a sentinel, so a test can prove that the failure of the console never changes the file or the exit code and never reaches the user.
/// </summary>
/// <param name="inner">The listener that receives every other event.</param>
/// <param name="detail">The message of the exception; a test builds it at run time and looks for it in the output.</param>
public sealed class JudgeFailedThrows(IRunListener inner, string detail) : IRunListener
{
    /// <inheritdoc />
    public void StepFinished(StepReading reading) => inner.StepFinished(reading);

    /// <inheritdoc />
    public void CheckpointFailed(string exceptionTypeName, bool previousCheckpointKept, CheckpointKind kind) => inner.CheckpointFailed(exceptionTypeName, previousCheckpointKept, kind);

    /// <inheritdoc />
    public void BaselineMeasured(BaselineResult baseline) => inner.BaselineMeasured(baseline);

    /// <inheritdoc />
    public void TierCompleted(string displayPath, Totals totals) => inner.TierCompleted(displayPath, totals);

    /// <inheritdoc />
    public void JudgeFailed(Tier tier, int failedSteps, int judgedSteps, IReadOnlyList<string> failureKinds) => throw new IOException(detail);

    /// <inheritdoc />
    public void TierFailed(string displayPath, string agent, RunStatus status) => inner.TierFailed(displayPath, agent, status);
}