using System.Collections.Concurrent;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Runs;

namespace AgentJoules.TestSupport;

/// <summary>
/// A run listener that forwards every event to the listener of the command and records the name of each call in order. When told that the judge failed it also calls
/// <paramref name="statusAtJudgeFailed"/> and keeps the answer, which lets a test prove that the judge warning is told after the results file was written. The answer is kept and not asserted
/// here, because the orchestrator isolates an exception thrown by a listener.
/// </summary>
/// <param name="inner">The listener that receives every event.</param>
/// <param name="statusAtJudgeFailed">Reads the status of the results file at the moment the judge failure is told; null when there is no file yet.</param>
public sealed class OrderRecordingListener(IRunListener inner, Func<string?> statusAtJudgeFailed) : IRunListener
{
    private readonly ConcurrentQueue<string> _calls = new();

    /// <summary>The names of the calls received so far, in order.</summary>
    public IReadOnlyList<string> Calls => [.. _calls];

    /// <summary>The status of the results file at the moment <c>JudgeFailed</c> was told, or null when it was not told or the file did not exist yet.</summary>
    public string? StatusAtJudgeFailed { get; private set; }

    /// <inheritdoc />
    public void StepFinished(StepReading reading)
    {
        _calls.Enqueue(nameof(StepFinished));
        inner.StepFinished(reading);
    }

    /// <inheritdoc />
    public void CheckpointFailed(string exceptionTypeName, bool previousCheckpointKept, CheckpointKind kind)
    {
        _calls.Enqueue(nameof(CheckpointFailed));
        inner.CheckpointFailed(exceptionTypeName, previousCheckpointKept, kind);
    }

    /// <inheritdoc />
    public void BaselineMeasured(BaselineResult baseline)
    {
        _calls.Enqueue(nameof(BaselineMeasured));
        inner.BaselineMeasured(baseline);
    }

    /// <inheritdoc />
    public void TierCompleted(string displayPath, Totals totals)
    {
        _calls.Enqueue(nameof(TierCompleted));
        inner.TierCompleted(displayPath, totals);
    }

    /// <inheritdoc />
    public void JudgeFailed(Tier tier, int failedSteps, int judgedSteps, IReadOnlyList<string> failureKinds)
    {
        StatusAtJudgeFailed = statusAtJudgeFailed();
        _calls.Enqueue(nameof(JudgeFailed));
        inner.JudgeFailed(tier, failedSteps, judgedSteps, failureKinds);
    }

    /// <inheritdoc />
    public void JudgeFailed(Tier tier, int failedSteps, int judgedSteps, IReadOnlyList<string> failureKinds, IReadOnlyList<string> failureDetails)
    {
        StatusAtJudgeFailed = statusAtJudgeFailed();
        _calls.Enqueue(nameof(JudgeFailed));
        inner.JudgeFailed(tier, failedSteps, judgedSteps, failureKinds, failureDetails);
    }

    /// <inheritdoc />
    public void TierFailed(string displayPath, string agent, RunStatus status)
    {
        _calls.Enqueue(nameof(TierFailed));
        inner.TierFailed(displayPath, agent, status);
    }
}