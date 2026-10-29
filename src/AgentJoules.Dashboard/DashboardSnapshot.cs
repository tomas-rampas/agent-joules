using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;

namespace AgentJoules.Dashboard;

/// <summary>
/// The in-memory state of the dashboard (design 3.10): the latest run started, its baseline and progress, the status of every agent, the active agents, the steps completed so far, the
/// cumulative values per agent and the running estimates of the steps in flight. Nothing is carried or aggregated across tier runs: a <see cref="RunStartedEvent"/> resets everything, and the
/// sink then sends a snapshot payload after the run-started payload, so a page resets only in its snapshot handler. The state lives in memory only and is never written anywhere.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe: the one lock of <see cref="DashboardSink"/> guards every call.
/// </para>
/// <para>
/// The cumulative values are computed here and not in the page, by the rule of the totals of a results file: all measured steps give the measured sum, otherwise the label and the reason of
/// the first completed step that lacks a measured value, and an agent without a completed step is not-reported <c>no-steps</c>. The running estimate of a measured step is the trapezoid of the
/// board power over the samples of the step, net of the idle baseline mean, labelled <c>estimated</c>; a hosted step has no power and says so. Estimates live in this project only and are
/// dropped when the measured reading arrives.
/// </para>
/// </remarks>
/// <param name="redactor">The redactor of the host: every payload of this state passes it before a connection sees it.</param>
/// <exception cref="ArgumentNullException"><paramref name="redactor"/> is null.</exception>
public sealed class DashboardSnapshot(SecretRedactor redactor)
{
    /// <summary>The status of an agent that has not started a step in this run.</summary>
    internal const string WaitingStatus = "waiting";

    /// <summary>The status of an agent whose step is in flight.</summary>
    internal const string RunningStatus = "running";

    private const string SharedWindowFlag = "shared-window";
    private const string JouleUnit = "J";
    private const string ChunkUnit = "chunks";
    private const string NoStepsReason = "no-steps";
    private const string NotMeasuredReason = "not-measured";
    private const string HostedReason = "hosted-tier";
    private const string NoBaselineReason = "no-baseline";
    private const string NoSamplesReason = "no-samples";
    private const string NoProgressReason = "no-progress";
    private const string OverflowReason = "overflow";

    // The agents the state tracks: the four of the configuration and at most this many more, in the order they appear. Any further agent has no card, no entry in agents[] and no cumulative value,
    // so one payload stays small whatever a run file names; the notice member agentsOmitted says that agents were left out.
    private const int MaxExtraAgents = 8;

    // The most distinct omitted agents counted (by name cut to 64 characters, so a long name costs 64): the notice is a count, not a list.
    private const int MaxOmittedCounted = 10_000;

    // The names of omitted agents are remembered cut to this many characters, so the count is exact for any realistic name and cheap for a hostile one.
    private const int OmittedNameLength = 64;

    // The most steps in flight of agents beyond the cap whose start is kept.
    private const int MaxCappedStarts = 64;

    private readonly Dictionary<string, string> _status = new(StringComparer.Ordinal);
    private readonly List<string> _active = [];
    private readonly List<string> _agentOrder = [.. TierOptions.KnownAgentNames];
    private readonly Dictionary<string, List<StepReading>> _stepsByAgent = new(StringComparer.Ordinal);
    private readonly List<StepReading> _completedSteps = [];
    private readonly Dictionary<string, RunningStep> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _omitted = new(StringComparer.Ordinal);

    // The start of the measured steps in flight of agents beyond the cap: kept for the overlap rule only (no card, no estimate), and bounded.
    private readonly Dictionary<string, double> _cappedStarts = new(StringComparer.Ordinal);

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

    /// <summary>Told once, with the exception type name, when a snapshot had to be written in its smaller form; the sink logs it. Set by the owner of the state.</summary>
    internal Action<string>? DegradedObserver { get; set; }

    /// <summary>Tells the observer that a snapshot fell back to a smaller form.</summary>
    /// <param name="failure">What the writer refused.</param>
    internal void NoteDegraded(Exception failure) => DegradedObserver?.Invoke(failure.GetType().Name);

    /// <summary>The number of distinct agents beyond the tracked ones that appeared in this run (counted up to 10,000); 0 when every agent has a card.</summary>
    internal int AgentsOmitted => _omitted.Count;

    /// <summary>The power of the last power event applied in this run, label kept (N/A included), or null before any.</summary>
    internal Metric? LastWatts { get; private set; }

    /// <summary>The run-timeline instant of <see cref="LastWatts"/>, or null before any power event.</summary>
    internal double? LastSampleSeconds { get; private set; }

    /// <summary>The number of events applied since the snapshot was created (every run included), for the subscribe tests.</summary>
    internal long AppliedCount { get; private set; }

    /// <summary>The redactor every payload of this state passes.</summary>
    internal SecretRedactor Redactor { get; } = redactor ?? throw new ArgumentNullException(nameof(redactor));

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
                StartStep(step);
                break;
            case StepProgressEvent progress:
                if (_running.TryGetValue(progress.Step.Agent, out RunningStep? streaming))
                {
                    streaming.Chunks = Math.Max(streaming.Chunks ?? 0, progress.OutputChunks);
                }

                break;
            case PowerEvent power:
                LastWatts = power.Watts;
                LastSampleSeconds = power.SampleSeconds;
                Integrate(power);
                break;
            case StepCompletedEvent completed:
                CompleteStep(completed.Reading);
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

    /// <summary>Every known agent with its status, its cumulative values and, while its step is in flight, its running estimates.</summary>
    /// <returns>The agents, the agents of the configuration first and any other in the order they appeared.</returns>
    internal List<AgentData> AgentViews()
    {
        int measuredRunning = _running.Values.Count(static step => !step.Hosted);
        var views = new List<AgentData>(_agentOrder.Count);
        foreach (string agent in _agentOrder)
        {
            IReadOnlyList<StepReading> steps = _stepsByAgent.TryGetValue(agent, out List<StepReading>? own) ? own : [];
            _ = _running.TryGetValue(agent, out RunningStep? running);
            views.Add(new AgentData(
                agent,
                TierOptions.KnownAgentNames.Contains(agent, StringComparer.Ordinal),
                StatusOf(agent),
                Sum(steps, static step => step.NetJoules, JouleUnit),
                StepCosts.ByCurrency(steps),
                Sum(steps, static step => step.InputTokens, TokenCounts.TokenUnit),
                Sum(steps, static step => step.OutputTokens, TokenCounts.TokenUnit),
                running is null ? null : RunningEnergy(running),
                running is null ? null : running.Chunks is { } chunks ? Metric.EstimatedQuantity(chunks, ChunkUnit) : Metric.NotReported(NoProgressReason),
                running is { Hosted: false } && measuredRunning > 1 ? [SharedWindowFlag] : []));
        }

        return views;
    }

    /// <summary>The steps completed so far in completion order, each with its flags.</summary>
    /// <returns>The steps.</returns>
    internal List<CompletedStepData> CompletedViews() => [.. _completedSteps.Select(step => new CompletedStepData(step, FlagsOf(step)))];

    /// <summary>
    /// The flags of a completed step: its own flags plus <c>shared-window</c> when a measured step with a window is strictly overlapped by another completed step of the run or by a step still in
    /// flight that started before this one stopped. This equals the flag the results file derives; hosted steps, judge readings and steps without a window are never flagged, and the reading itself is not changed.
    /// </summary>
    /// <param name="reading">The completed step.</param>
    /// <returns>The flag names in the order of the flag vocabulary.</returns>
    internal List<string> FlagsOf(StepReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        ReadingFlags flags = reading.Flags;
        if (!flags.HasFlag(ReadingFlags.SharedWindow) && IsSharedWindow(reading))
        {
            flags |= ReadingFlags.SharedWindow;
        }

        return [.. ReadingFlagNames.ToStrings(flags)];
    }

    private bool IsSharedWindow(StepReading reading)
    {
        if (!TierKinds.IsMeasured(reading.Tier) || reading.JudgedAgent is not null || reading.Window is not { } window)
        {
            return false;
        }

        foreach (StepReading other in _completedSteps)
        {
            if (!ReferenceEquals(other, reading) && other.Window is { } span && UnionWindowCalculator.Overlaps(window, span))
            {
                return true;
            }
        }

        // A step still in flight started before this one stopped, and it stops after now, which is not before this one stopped.
        return _running.Values.Any(step => !step.Hosted && step.StartSeconds < window.StopSeconds)
            || _cappedStarts.Values.Any(start => start < window.StopSeconds);
    }

    private void StartStep(StepStartedEvent step)
    {
        // A judge step is not a workflow step: it creates no running entry, no card and no active agent.
        if (step.Step.IsJudge)
        {
            return;
        }

        string agent = step.Step.Agent;
        if (!Track(agent))
        {
            if (!TierKinds.IsHosted(step.Step.Tier) && (_cappedStarts.Count < MaxCappedStarts || _cappedStarts.ContainsKey(agent)))
            {
                _cappedStarts[agent] = step.StartSeconds;
            }

            return;
        }

        _status[agent] = RunningStatus;
        _ = _active.Remove(agent);
        _active.Add(agent);
        _running[agent] = new RunningStep(step.StartSeconds, TierKinds.IsHosted(step.Step.Tier));
    }

    private void CompleteStep(StepReading reading)
    {
        // A judge reading is not a workflow step: it changes no card, no status, no count and no flag.
        if (reading.JudgedAgent is not null)
        {
            return;
        }

        CompletedStepCount++;
        _completedSteps.Add(reading);

        // A step of an agent beyond the tracked ones still counts and is listed, but it gets no card and no cumulative value.
        string agent = reading.Agent;
        if (!Track(agent))
        {
            _ = _cappedStarts.Remove(agent);
            return;
        }

        _status[agent] = reading.Outcome;
        _ = _active.Remove(agent);
        _ = _running.Remove(agent);
        if (!_stepsByAgent.TryGetValue(agent, out List<StepReading>? own))
        {
            own = [];
            _stepsByAgent[agent] = own;
        }

        own.Add(reading);
    }

    // The trapezoid over the board power samples of every measured step in flight; a sample older than the step start is not part of it, and the first sample after the start holds the step's
    // first point at the start (the same rule as the integrator of the meter). Each active step integrates the full device power, so two overlapping steps both carry shared-window.
    private void Integrate(PowerEvent power)
    {
        if (power.Watts is not { Label: MetricLabel.Measured, Value: { } watts })
        {
            return;
        }

        foreach (RunningStep step in _running.Values)
        {
            if (!step.Hosted && power.SampleSeconds >= step.StartSeconds)
            {
                step.Add(power.SampleSeconds, watts);
            }
        }
    }

    private Metric RunningEnergy(RunningStep step) =>
        step.Hosted ? Metric.NotApplicable(HostedReason)
        : Baseline?.MeanW is not { Label: MetricLabel.Measured, Value: { } mean } ? Metric.NotReported(NoBaselineReason)
        : step.Samples == 0 ? Metric.NotReported(NoSamplesReason)
        : double.IsFinite(step.GrossJoules - (mean * step.Seconds)) ? Metric.EstimatedQuantity(step.GrossJoules - (mean * step.Seconds), JouleUnit)
        : Metric.NotReported(OverflowReason);

    // The sum of a measured value over the completed steps of an agent: the rule of the totals of a results file (RunAssembler), so that the live number and the file agree. The label and the
    // reason of the first step that lacks a measured value otherwise; not-reported no-steps without a step.
    private static Metric Sum(IReadOnlyList<StepReading> steps, Func<StepReading, Metric> select, string unit)
    {
        if (steps.Count == 0)
        {
            return Metric.NotReported(NoStepsReason);
        }

        double sum = 0.0;
        foreach (StepReading step in steps)
        {
            Metric metric = select(step);
            if (metric is not { Label: MetricLabel.Measured, Value: { } value })
            {
                return metric.Label == MetricLabel.NotApplicable
                    ? Metric.NotApplicable(metric.Reason ?? NotMeasuredReason)
                    : Metric.NotReported(metric.Reason ?? NotMeasuredReason);
            }

            sum += value;
        }

        // A sum of finite values can overflow; a number that is not finite is never published, the total says it overflowed.
        return double.IsFinite(sum) ? Metric.Measured(sum, unit) : Metric.NotReported(OverflowReason);
    }

    // True when the agent has a card: one of the configured agents, or one of the first few others. Any further agent is only counted.
    private bool Track(string agent)
    {
        if (_agentOrder.Contains(agent, StringComparer.Ordinal))
        {
            return true;
        }

        if (_agentOrder.Count < TierOptions.KnownAgentNames.Count + MaxExtraAgents)
        {
            _agentOrder.Add(agent);
            return true;
        }

        if (_omitted.Count < MaxOmittedCounted)
        {
            _ = _omitted.Add(agent.Length > OmittedNameLength ? agent[..OmittedNameLength] : agent);
        }

        return false;
    }

    private void Reset()
    {
        Run = null;
        Baseline = null;
        Progress = null;
        Completed = null;
        _status.Clear();
        _active.Clear();
        _agentOrder.Clear();
        _agentOrder.AddRange(TierOptions.KnownAgentNames);
        _stepsByAgent.Clear();
        _completedSteps.Clear();
        _running.Clear();
        _cappedStarts.Clear();
        _omitted.Clear();
        LastWatts = null;
        LastSampleSeconds = null;
        CompletedStepCount = 0;
    }

    // The running state of one step in flight: its start on the run timeline, whether the tier is hosted, the integral of the board power so far and the streamed chunk count.
    private sealed class RunningStep(double startSeconds, bool hosted)
    {
        private bool _started;
        private double _lastSeconds;
        private double _lastWatts;

        public double StartSeconds { get; } = startSeconds;

        public bool Hosted { get; } = hosted;

        public double GrossJoules { get; private set; }

        public double Seconds { get; private set; }

        public int Samples { get; private set; }

        public int? Chunks { get; set; }

        public void Add(double seconds, double watts)
        {
            if (!_started)
            {
                _started = true;
                _lastSeconds = StartSeconds;
                _lastWatts = watts;
            }

            double span = seconds - _lastSeconds;
            if (span < 0.0)
            {
                return;
            }

            GrossJoules += (_lastWatts + watts) / 2.0 * span;
            Seconds += span;
            _lastSeconds = seconds;
            _lastWatts = watts;
            Samples++;
        }
    }
}