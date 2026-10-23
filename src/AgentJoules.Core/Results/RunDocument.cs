using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;

namespace AgentJoules.Core.Results;

/// <summary>
/// The root of a results file (REQ-015, REQ-017), mirroring the skeleton of the prompt member for member with camelCase names. The run orchestrator
/// builds one at the start with <c>run.status</c> <c>running</c> and writes a new one after every (task, repeat) through <c>IRunCheckpointWriter</c>.
/// <see cref="StepReading"/> is the element of <see cref="Steps"/> and <see cref="Judge"/>; a judge reading is a step-shaped reading that names the agent it scored in <see cref="StepReading.JudgedAgent"/>.
/// </summary>
/// <remarks>
/// <para>
/// The constructor checks the invariants of the document, on construction and so on read: the schema version is <see cref="CurrentSchemaVersion"/>; no member and no list element is null; no
/// <see cref="Steps"/> element names a judged agent; every <see cref="Judge"/> element names one, and with its task and repeat it matches the task, repeat and agent of exactly one <see cref="Steps"/>
/// element, and no two judge readings match the same step. An empty <see cref="Judge"/> is valid. A violation is an <see cref="ArgumentException"/> that names the member and the index (<c>judge[3]</c>, <c>steps[0]</c>), never a value.
/// <see cref="SchemaVersion"/>, <see cref="Steps"/> and <see cref="Judge"/> are get-only, so <c>with</c> cannot replace them and break the pairing; the two lists are stored as read-only copies. The run,
/// environment, probe, baseline and totals members stay <c>init</c>.
/// </para>
/// <para>
/// Record equality compares collection members by reference; compare documents member by member (tests: ResultsEquality) or by their serialised form.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunDocument
{
    /// <summary>The only schema version there is today.</summary>
    public const string CurrentSchemaVersion = "1";

    /// <summary>Creates a document after checking its invariants.</summary>
    /// <param name="schemaVersion">The version of the file layout; <see cref="CurrentSchemaVersion"/>.</param>
    /// <param name="run">Who ran what and where the run stands.</param>
    /// <param name="environment">What the run ran on.</param>
    /// <param name="probe">The GPU probe of a measured tier, or null on a hosted tier.</param>
    /// <param name="baseline">The idle baseline of a measured tier, or null on a hosted tier.</param>
    /// <param name="steps">The readings of the workflow steps finished so far.</param>
    /// <param name="judge">The readings of the judge calls (the same shape as a step).</param>
    /// <param name="totals">The sums over the steps.</param>
    /// <exception cref="ArgumentNullException">A member, a list or a list element is null.</exception>
    /// <exception cref="ArgumentException">The schema version is not the current one, a step names a judged agent, or a judge reading names none or does not match exactly one step.</exception>
    [JsonConstructor]
    public RunDocument(
        string schemaVersion,
        RunInfo run,
        EnvironmentInfo environment,
        ProbeResult? probe,
        BaselineResult? baseline,
        IReadOnlyList<StepReading> steps,
        IReadOnlyList<StepReading> judge,
        Totals totals)
    {
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The {nameof(schemaVersion)} must be \"{CurrentSchemaVersion}\".", nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(totals);

        ReadOnlyCollection<StepReading> stepCopy = CheckedSteps(steps, out Dictionary<(string Task, int Repeat, string Agent), int> stepCounts);
        ReadOnlyCollection<StepReading> judgeCopy = CheckedJudge(judge, stepCounts);

        SchemaVersion = schemaVersion;
        Run = run;
        Environment = environment;
        Probe = probe;
        Baseline = baseline;
        Steps = stepCopy;
        Judge = judgeCopy;
        Totals = totals;
    }

    /// <summary>The version of the file layout; <see cref="CurrentSchemaVersion"/>.</summary>
    public string SchemaVersion { get; }

    /// <summary>Who ran what and where the run stands.</summary>
    /// <exception cref="ArgumentNullException">The value is null (also on <c>with</c>).</exception>
    public RunInfo Run
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>What the run ran on.</summary>
    /// <exception cref="ArgumentNullException">The value is null (also on <c>with</c>).</exception>
    public EnvironmentInfo Environment
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>The GPU probe of a measured tier, or null on a hosted tier.</summary>
    public ProbeResult? Probe { get; init; }

    /// <summary>The idle baseline of a measured tier, or null on a hosted tier.</summary>
    public BaselineResult? Baseline { get; init; }

    /// <summary>The readings of the workflow steps finished so far; none of them names a judged agent.</summary>
    public IReadOnlyList<StepReading> Steps { get; }

    /// <summary>The readings of the judge calls; each names the judged agent and matches exactly one element of <see cref="Steps"/>. Empty while the judge has not run.</summary>
    public IReadOnlyList<StepReading> Judge { get; }

    /// <summary>The sums over the steps.</summary>
    /// <exception cref="ArgumentNullException">The value is null (also on <c>with</c>).</exception>
    public Totals Totals
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    private static ReadOnlyCollection<StepReading> CheckedSteps(IReadOnlyList<StepReading> steps, out Dictionary<(string Task, int Repeat, string Agent), int> counts)
    {
        counts = [];
        var copy = new List<StepReading>(steps.Count);
        for (int index = 0; index < steps.Count; index++)
        {
            StepReading step = steps[index] ?? throw new ArgumentNullException(nameof(steps), $"steps[{index}] must not be null.");
            if (step.JudgedAgent is not null)
            {
                throw new ArgumentException($"steps[{index}] must not name a judged agent; only judge[] readings do.", nameof(steps));
            }

            (string, int, string) key = (step.Task, step.Repeat, step.Agent);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            copy.Add(step);
        }

        return new ReadOnlyCollection<StepReading>(copy);
    }

    private static ReadOnlyCollection<StepReading> CheckedJudge(IReadOnlyList<StepReading> judge, Dictionary<(string Task, int Repeat, string Agent), int> stepCounts)
    {
        var copy = new List<StepReading>(judge.Count);
        var paired = new HashSet<(string Task, int Repeat, string Agent)>();
        for (int index = 0; index < judge.Count; index++)
        {
            StepReading reading = judge[index] ?? throw new ArgumentNullException(nameof(judge), $"judge[{index}] must not be null.");
            if (reading.JudgedAgent is null)
            {
                throw new ArgumentException($"judge[{index}] must name the judged agent.", nameof(judge));
            }

            (string, int, string) key = (reading.Task, reading.Repeat, reading.JudgedAgent);
            if (stepCounts.GetValueOrDefault(key) != 1)
            {
                throw new ArgumentException($"judge[{index}] must name exactly one steps[] element by task, repeat and judged agent.", nameof(judge));
            }

            if (!paired.Add(key))
            {
                throw new ArgumentException($"judge[{index}] pairs with the same step as an earlier judge reading.", nameof(judge));
            }

            copy.Add(reading);
        }

        return new ReadOnlyCollection<StepReading>(copy);
    }
}