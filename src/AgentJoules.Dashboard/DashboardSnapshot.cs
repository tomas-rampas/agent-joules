using AgentJoules.Core.Energy;
using AgentJoules.Core.Live;

namespace AgentJoules.Dashboard;

/// <summary>
/// The in-memory state of the dashboard (design 3.10): the latest run started, its baseline and progress, the status of every agent, the active agents and the steps completed so far.
/// Nothing is carried or aggregated across tier runs: a <see cref="RunStartedEvent"/> resets everything, and the sink then sends a snapshot payload after the run-started payload, so a
/// page resets only in its snapshot handler. The state lives in memory only and is never written anywhere.
/// </summary>
/// <remarks>Not thread-safe: the one lock of <see cref="DashboardSink"/> guards every call.</remarks>
public sealed class DashboardSnapshot
{
    /// <summary>The status of an agent that has not started a step in this run.</summary>
    internal const string WaitingStatus = "waiting";

    /// <summary>The status of an agent whose step is in flight.</summary>
    internal const string RunningStatus = "running";

    private readonly Dictionary<string, string> _status = new(StringComparer.Ordinal);
    private readonly List<string> _active = [];

    /// <summary>The latest run started, or null before the first one.</summary>
    internal RunStartedEvent? Run { get; private set; }

    /// <summary>The baseline of the current run, or null when none was measured yet (always null on a hosted tier).</summary>
    internal BaselineResult? Baseline { get; private set; }

    /// <summary>The latest progress of the current run, or null.</summary>
    internal ProgressEvent? Progress { get; private set; }

    /// <summary>The completion of the current run, or null while it runs.</summary>
    internal RunCompletedEvent? Completed { get; private set; }

    /// <summary>The number of steps completed in the current run.</summary>
    internal int CompletedStepCount { get; private set; }

    /// <summary>The agents whose step is in flight, in the order their steps started.</summary>
    internal IReadOnlyList<string> ActiveAgents => [.. _active];

    /// <summary>True when the current state comes from a replayed run.</summary>
    internal bool Replay => Run?.Replay ?? false;

    /// <summary>The number of events applied since the snapshot was created (every run included), for the subscribe tests.</summary>
    internal long AppliedCount { get; private set; }

    /// <summary>The status of <paramref name="agent"/>: <c>waiting</c>, <c>running</c>, or the outcome of its latest step.</summary>
    /// <param name="agent">The agent name.</param>
    /// <returns>The status.</returns>
    internal string StatusOf(string agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return _status.TryGetValue(agent, out string? status) ? status : WaitingStatus;
    }

    /// <summary>Applies <paramref name="liveEvent"/> and returns its envelope as of after the apply.</summary>
    /// <param name="liveEvent">The event.</param>
    /// <returns>One line of JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="liveEvent"/> is null.</exception>
    /// <exception cref="NotSupportedException">The event is of a type the dashboard does not know; nothing was applied.</exception>
    public string Apply(LiveEvent liveEvent)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);

        switch (liveEvent)
        {
            case RunStartedEvent started:
                Reset();
                Run = started;
                break;
            case BaselineEvent baseline:
                Baseline = baseline.Baseline;
                break;
            case ProgressEvent progress:
                Progress = progress;
                break;
            case StepStartedEvent step:
                _status[step.Step.Agent] = RunningStatus;
                _ = _active.Remove(step.Step.Agent);
                _active.Add(step.Step.Agent);
                break;
            case StepProgressEvent:
            case PowerEvent:
                // The running estimates of these events are AJ-090's; the state keeps nothing of them yet.
                break;
            case StepCompletedEvent completed:
                _status[completed.Reading.Agent] = completed.Reading.Outcome;
                _ = _active.Remove(completed.Reading.Agent);
                CompletedStepCount++;
                break;
            case RunCompletedEvent done:
                Completed = done;
                break;
            default:
                throw new NotSupportedException($"The dashboard cannot apply the live event type {liveEvent.GetType().Name}.");
        }

        AppliedCount++;
        return DashboardEventSerializer.Serialize(liveEvent, this);
    }

    /// <summary>The snapshot envelope of the current state.</summary>
    /// <param name="atUtc">The instant of the snapshot.</param>
    /// <returns>One line of JSON.</returns>
    public string ToSnapshotPayload(DateTimeOffset atUtc) => DashboardEventSerializer.SerializeSnapshot(this, atUtc);

    private void Reset()
    {
        Run = null;
        Baseline = null;
        Progress = null;
        Completed = null;
        _status.Clear();
        _active.Clear();
        CompletedStepCount = 0;
    }
}