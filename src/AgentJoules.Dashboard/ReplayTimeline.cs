using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

namespace AgentJoules.Dashboard;

/// <summary>One event of a replay and the offset from <c>run.startUtc</c> at which it is emitted.</summary>
/// <param name="Offset">The recorded offset, never negative.</param>
/// <param name="Event">The event, with <see cref="LiveEvent.Replay"/> true. Its <see cref="LiveEvent.AtUtc"/> is the recorded instant; the replay replaces it with the emit instant on its clock.</param>
public sealed record ReplayEntry(TimeSpan Offset, LiveEvent Event);

/// <summary>
/// The timeline of a replay (REQ-032, AJ-092): the events a committed run file would have produced live, at the offsets the file records them from <c>run.startUtc</c>. Only <c>steps[]</c>
/// produce events: run started at 0, the baseline at 0 when the file has one (nothing is invented for a file without it), progress at the first step of each (task, repeat), step started at
/// <c>startUtc</c>, step completed at <c>endUtc</c>, one power event at the start and one at the end of every step carrying the step's recorded <c>avgW</c> unchanged (a hosted step's N/A
/// <c>hosted-tier</c> with no value included), and run completed at the last <c>endUtc</c> with the file's run and totals. Labels, flags and reasons are those of the file; none is added and
/// nothing is aggregated or ranked. Every event has <see cref="LiveEvent.Replay"/> true.
/// </summary>
/// <remarks>
/// Events at the same offset are ordered by rank: run started, baseline, power at a step end, step completed, progress, step started, power at a step start, run completed, and then by the
/// index of the step, so a step ends before the next one starts at a shared instant and the snapshot of the dashboard sees no false overlap. An offset before the start of the run is
/// clamped to 0.
/// </remarks>
public static class ReplayTimeline
{
    private const int RunStartedRank = 0;
    private const int BaselineRank = 1;
    private const int PowerAtEndRank = 2;
    private const int StepCompletedRank = 3;
    private const int ProgressRank = 4;
    private const int StepStartedRank = 5;
    private const int PowerAtStartRank = 6;

    // A step that starts and ends at the same offset (a zero-length step, or one wholly before the start of the run, both ends clamped to 0) ends after its own start: its end entries come after the
    // ranks of the starts, while the order against other steps at that instant is unchanged (another step ends before this one starts, and every step ends before the run completes).
    private const int ZeroLengthEndOffset = 10;
    private const int AfterEveryStepRank = 20;

    /// <summary>Builds the timeline of <paramref name="run"/>.</summary>
    /// <param name="run">The run document read from a committed run file.</param>
    /// <returns>The entries in the order they are emitted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    public static IReadOnlyList<ReplayEntry> Build(RunDocument run)
    {
        ArgumentNullException.ThrowIfNull(run);

        RunInfo info = run.Run;
        DateTimeOffset origin = info.StartUtc;
        List<Item> items = [];
        items.Add(new Item(TimeSpan.Zero, RunStartedRank, 0, new RunStartedEvent(origin, true, info, run.Environment, run.Probe?.PowerLimitW)));
        if (run.Baseline is { } baseline)
        {
            items.Add(new Item(TimeSpan.Zero, BaselineRank, 0, new BaselineEvent(origin, true, baseline)));
        }

        TimeSpan last = TimeSpan.Zero;
        var firstOfGroup = new Dictionary<(string Task, int Repeat), (TimeSpan Start, int Step)>();
        for (int index = 0; index < run.Steps.Count; index++)
        {
            StepReading step = run.Steps[index];
            TimeSpan start = Clamp(step.StartUtc - origin);
            TimeSpan end = Clamp(step.EndUtc - origin);
            last = end > last ? end : last;
            var identity = new StepIdentity(step.Agent, step.ModelId, step.Tier, step.Task, step.Repeat, IsJudge: false);

            items.Add(new Item(start, StepStartedRank, index, new StepStartedEvent(origin + start, true, identity, start.TotalSeconds)));
            items.Add(new Item(start, PowerAtStartRank, index, new PowerEvent(origin + start, true, step.AvgW, start.TotalSeconds)));
            int endShift = end == start ? ZeroLengthEndOffset : 0;
            items.Add(new Item(end, PowerAtEndRank + endShift, index, new PowerEvent(origin + end, true, step.AvgW, end.TotalSeconds)));
            items.Add(new Item(end, StepCompletedRank + endShift, index, new StepCompletedEvent(origin + end, true, step)));

            // The first step of a (task, repeat) in time starts it; ties keep the earlier index.
            (string, int) group = (step.Task, step.Repeat);
            if (!firstOfGroup.TryGetValue(group, out (TimeSpan Start, int Step) earliest) || start < earliest.Start)
            {
                firstOfGroup[group] = (start, index);
            }
        }

        AddProgress(items, run, origin, firstOfGroup);
        items.Add(new Item(last, AfterEveryStepRank, run.Steps.Count, new RunCompletedEvent(origin + last, true, info, run.Totals)));

        // OrderBy is stable, so events with the same offset, rank and step keep the order they were added in.
        return [.. items.OrderBy(static item => item.Offset).ThenBy(static item => item.Rank).ThenBy(static item => item.Step).Select(static item => new ReplayEntry(item.Offset, item.Event))];
    }

    // The task position is the task's place in run.tasks; a task the file does not list is counted after the listed ones, in the order it appears.
    private static void AddProgress(List<Item> items, RunDocument run, DateTimeOffset origin, Dictionary<(string Task, int Repeat), (TimeSpan Start, int Step)> firstOfGroup)
    {
        List<string> tasks = [.. run.Run.Tasks];
        foreach (StepReading step in run.Steps)
        {
            if (!tasks.Contains(step.Task, StringComparer.Ordinal))
            {
                tasks.Add(step.Task);
            }
        }

        foreach (((string task, int repeat), (TimeSpan start, int index)) in firstOfGroup)
        {
            int taskIndex = tasks.FindIndex(candidate => string.Equals(candidate, task, StringComparison.Ordinal)) + 1;
            items.Add(new Item(start, ProgressRank, index, new ProgressEvent(origin + start, true, task, taskIndex, tasks.Count, repeat, run.Run.Repeat)));
        }
    }

    private static TimeSpan Clamp(TimeSpan offset) => offset < TimeSpan.Zero ? TimeSpan.Zero : offset;

    private readonly record struct Item(TimeSpan Offset, int Rank, int Step, LiveEvent Event);
}