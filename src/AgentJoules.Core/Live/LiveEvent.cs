using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

namespace AgentJoules.Core.Live;

/// <summary>
/// One event of the live dashboard (REQ-032, design 3.10). The events reuse the types of the results model, so a label travels unchanged from the measurement to the page and an
/// unmeasured value is never turned into a number on the way. No type here depends on ASP.NET Core: the dashboard project is the only one that serves them.
/// </summary>
/// <param name="AtUtc">The instant the event was emitted, read from the injected <see cref="TimeProvider"/> of the producer (the latency semantics of REQ-032 R1); times the event reports about travel in its payload.</param>
/// <param name="Replay">True when the event comes from a committed run file played back on its recorded timeline; false for a live run.</param>
public abstract record LiveEvent(DateTimeOffset AtUtc, bool Replay);

/// <summary>A tier run started: what runs, where and with which limit.</summary>
/// <param name="AtUtc">The emit instant.</param>
/// <param name="Replay">True for a replayed run.</param>
/// <param name="Run">The run section of the results file as it stands when the tier run starts.</param>
/// <param name="Environment">The environment section of the results file.</param>
/// <param name="PowerLimitW">The power limit of the measured GPU in watts, or null when the tier measures no GPU or the limit is not known.</param>
public sealed record RunStartedEvent(DateTimeOffset AtUtc, bool Replay, RunInfo Run, EnvironmentInfo Environment, double? PowerLimitW) : LiveEvent(AtUtc, Replay);

/// <summary>The idle baseline of a measured tier run was measured.</summary>
/// <param name="AtUtc">The emit instant.</param>
/// <param name="Replay">True for a replayed run.</param>
/// <param name="Baseline">The baseline exactly as the results file records it.</param>
public sealed record BaselineEvent(DateTimeOffset AtUtc, bool Replay, BaselineResult Baseline) : LiveEvent(AtUtc, Replay);

/// <summary>A (task, repeat) of the tier run started; all four numbers are 1-based, so the page renders "task 2 of 3, repeat 1 of 3".</summary>
/// <param name="AtUtc">The emit instant.</param>
/// <param name="Replay">True for a replayed run.</param>
/// <param name="Task">The name of the task.</param>
/// <param name="TaskIndex">The position of the task in the task set, from 1.</param>
/// <param name="TaskCount">The number of tasks of the run.</param>
/// <param name="Repeat">The repeat, from 1.</param>
/// <param name="RepeatCount">The number of repeats of every task.</param>
public sealed record ProgressEvent(DateTimeOffset AtUtc, bool Replay, string Task, int TaskIndex, int TaskCount, int Repeat, int RepeatCount) : LiveEvent(AtUtc, Replay);

/// <summary>A metered step started.</summary>
/// <param name="AtUtc">The emit instant.</param>
/// <param name="Replay">True for a replayed run.</param>
/// <param name="Step">Who the step belongs to.</param>
/// <param name="StartSeconds">The start of the step on the run timeline in seconds: live, the meter's seconds since it started (the window's start); in a replay, the recorded offset from <c>run.startUtc</c>.</param>
public sealed record StepStartedEvent(DateTimeOffset AtUtc, bool Replay, StepIdentity Step, double StartSeconds) : LiveEvent(AtUtc, Replay);

/// <summary>A streaming step received more output; emitted at most once per 100 ms per step, so at most 10 per second.</summary>
/// <param name="AtUtc">The emit instant.</param>
/// <param name="Replay">True for a replayed run.</param>
/// <param name="Step">Who the step belongs to.</param>
/// <param name="OutputChunks">The number of streamed updates that carried non-empty text so far in this step; it never decreases within a step.</param>
public sealed record StepProgressEvent(DateTimeOffset AtUtc, bool Replay, StepIdentity Step, int OutputChunks) : LiveEvent(AtUtc, Replay);

/// <summary>A metered step finished and its reading was published.</summary>
/// <param name="AtUtc">The emit instant.</param>
/// <param name="Replay">True for a replayed run.</param>
/// <param name="Reading">The reading exactly as the results file records it.</param>
public sealed record StepCompletedEvent(DateTimeOffset AtUtc, bool Replay, StepReading Reading) : LiveEvent(AtUtc, Replay);

/// <summary>
/// One power sample the meter already took (never an extra read of the GPU), forwarded at most once per <c>Dashboard:PowerIntervalMs</c>. The active agents are not part of the
/// event: the dashboard derives them from the step events.
/// </summary>
/// <param name="AtUtc">The emit instant.</param>
/// <param name="Replay">True for a replayed run.</param>
/// <param name="Watts">The sampled board power, labelled; a hosted tier has none and says so with its label.</param>
/// <param name="SampleSeconds">The instant of the sample on the run timeline in seconds: live, the meter's own sample time since it started; in a replay, the recorded offset from <c>run.startUtc</c>.</param>
public sealed record PowerEvent(DateTimeOffset AtUtc, bool Replay, Metric Watts, double SampleSeconds) : LiveEvent(AtUtc, Replay);

/// <summary>A tier run ended and its results file was written.</summary>
/// <param name="AtUtc">The emit instant.</param>
/// <param name="Replay">True for a replayed run.</param>
/// <param name="Run">The run section of the written file (its status and run flags).</param>
/// <param name="Totals">The totals of the written file.</param>
public sealed record RunCompletedEvent(DateTimeOffset AtUtc, bool Replay, RunInfo Run, Totals Totals) : LiveEvent(AtUtc, Replay);