using System.Collections.Frozen;

using AgentJoules.Core.Runs;
using AgentJoules.Core.Tasks;

namespace AgentJoules.Workflow.Runs;

/// <summary>
/// What one <c>AJ run</c> outside a dry run hands to the <see cref="IRunOrchestrator"/> (REQ-017): the selected tiers in the order of a run, the selected tasks, the mode, the repeat count,
/// the idle-baseline length, one checkpoint writer per tier (constructed before any request, so its directory probe already passed), and the results of the key check and the model
/// check. The two check results can only be created by <see cref="PreflightService"/>, so a plan proves that both checks ran and passed.
/// </summary>
public sealed record RunPlan
{
    /// <summary>Creates the plan and checks that it is complete.</summary>
    /// <param name="tiers">The selected tiers in the order of a run; at least one, each once.</param>
    /// <param name="tasks">The selected tasks in file order; at least one.</param>
    /// <param name="mode">The run mode, <c>--mode</c>.</param>
    /// <param name="repeat">The repeats per task, <c>--repeat</c>; 1 or more.</param>
    /// <param name="baselineSeconds">The idle-baseline length of a measured tier in seconds, <c>--baseline-seconds</c> or <c>Baseline:Seconds</c>; finite and greater than 0.</param>
    /// <param name="writers">The checkpoint writer of each selected tier, keyed by tier; exactly the selected tiers. The plan keeps a frozen copy, so a later change to the dictionary does not reach it.</param>
    /// <param name="keys">The result of <see cref="PreflightService.CheckKeys"/> outside a dry run.</param>
    /// <param name="models">The result of <see cref="PreflightService.CheckModelsAsync"/>.</param>
    /// <exception cref="ArgumentNullException">A list, the writers or a check result is null.</exception>
    /// <exception cref="ArgumentException">The tiers or the tasks are empty, a tier repeats, or the writers do not match the tiers.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not defined, the repeat count is below 1, or the baseline is not a finite number greater than 0.</exception>
    public RunPlan(
        IReadOnlyList<Tier> tiers,
        IReadOnlyList<TaskDefinition> tasks,
        RunMode mode,
        int repeat,
        double baselineSeconds,
        IReadOnlyDictionary<Tier, IRunCheckpointWriter> writers,
        KeyCheckResult keys,
        ModelCheckResult models)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(writers);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(models);
        if (tiers.Count == 0 || tiers.Distinct().Count() != tiers.Count)
        {
            throw new ArgumentException("A plan needs at least one tier, each once.", nameof(tiers));
        }

        if (tasks.Count == 0)
        {
            throw new ArgumentException("A plan needs at least one task.", nameof(tasks));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The run mode is not a defined value.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(repeat, 1);
        if (!double.IsFinite(baselineSeconds) || baselineSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(baselineSeconds), baselineSeconds, "The idle baseline must be a finite number of seconds greater than 0.");
        }

        if (writers.Count != tiers.Count || !tiers.All(writers.ContainsKey))
        {
            throw new ArgumentException("A plan needs exactly one writer per selected tier.", nameof(writers));
        }

        Tiers = [.. tiers];
        Tasks = [.. tasks];
        Mode = mode;
        Repeat = repeat;
        BaselineSeconds = baselineSeconds;
        Writers = writers.ToFrozenDictionary();
        Keys = keys;
        Models = models;
    }

    /// <summary>The selected tiers in the order of a run (<c>local</c>, <c>cloudgpu</c>, <c>nvidia</c>, <c>nebius</c>).</summary>
    public IReadOnlyList<Tier> Tiers { get; }

    /// <summary>The selected tasks: every task of <c>Paths:Tasks</c> in file order, or the one <c>--task</c> names.</summary>
    public IReadOnlyList<TaskDefinition> Tasks { get; }

    /// <summary>The run mode, <c>--mode</c>; it chooses the one meter gate of the process.</summary>
    public RunMode Mode { get; }

    /// <summary>The repeats per task, <c>--repeat</c>.</summary>
    public int Repeat { get; }

    /// <summary>The idle-baseline length of a measured tier in seconds: <c>--baseline-seconds</c> when given, otherwise <c>Baseline:Seconds</c>; hosted tiers ignore it.</summary>
    public double BaselineSeconds { get; }

    /// <summary>The checkpoint writer of each selected tier (a frozen copy); each was constructed, and so probed its directory, before any request.</summary>
    public IReadOnlyDictionary<Tier, IRunCheckpointWriter> Writers { get; }

    /// <summary>The key check result; <see cref="KeyCheckResult.MissingJudgeVariable"/> says whether the judge must be skipped.</summary>
    public KeyCheckResult Keys { get; }

    /// <summary>The model check result: the endpoint and the matched model IDs of every tier that was asked.</summary>
    public ModelCheckResult Models { get; }
}