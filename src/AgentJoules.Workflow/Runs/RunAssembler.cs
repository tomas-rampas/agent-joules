using System.Reflection;
using System.Runtime.InteropServices;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Quality;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Local;

namespace AgentJoules.Workflow.Runs;

/// <summary>
/// Builds the run document of a tier from what the orchestrator collected (REQ-017, REQ-013, design 3.4 "Run orchestration" step 4 and "Environment fields", prompt results skeleton): the
/// totals over the steps, the <c>environment</c> and <c>run</c> members, and the copy of a reading with another outcome. It is pure: every value comes from its arguments, the runtime of the
/// process (operating system, .NET version) or the assembly version, so it is tested without a host.
/// </summary>
/// <remarks>
/// <para>
/// The totals follow one rule for every sum: a sum exists only when every step carries the value as <c>measured</c>; otherwise the total takes the label and the reason of the first step
/// that lacks it, and it is never 0. On a hosted tier the energy totals are N/A <c>hosted-tier</c>. In concurrent mode on a measured tier the windows of parallel steps overlap, so their
/// energies do not add up (<see cref="ReportSentences.ConcurrentOverlap"/>): the energy totals are the union of the step windows, computed by the meter of the tier
/// (<see cref="IEnergyMeter.ComputeUnion"/>, REQ-013, EDGE-004), and not-reported <c>union-not-computed</c> when no meter was given or a step has no window; serialized mode sums. Tokens
/// sum in both modes. The cost follows the same all-steps rule per currency: when every step carries a cost <c>estimated</c> in a currency, the sums per currency;
/// when at least one step lacks one, every currency seen is N/A <c>missing-step-costs</c>; an empty map when no step carries a cost. <c>wallClockMs</c> is the measured
/// sum of the workflow wall times of the (task, repeat) runs, and not-reported <c>no-steps</c> when none started. The judge readings are never part of these and are summed into <c>totals.judge</c>
/// (N/A <c>not-scored</c> while there are none). The emission (<c>gco2e</c>) is the estimate of <see cref="CostCalculator.EstimateEmissions"/> from the net energy total and <c>Cost:GCo2ePerKwh</c>: N/A <c>not-configured</c> while no factor is set, N/A <c>energy-not-measured</c> when the net total is N/A or not-reported, N/A <c>below-baseline</c> when the net total is negative, N/A <c>price-not-representable</c> when the product cannot be held, and an estimated 0 for a measured net total of 0.
/// </para>
/// <para>
/// The <c>shared-window</c> flag is derived when a document is assembled (<see cref="MarkSharedWindows"/>): on a measured tier, in both modes, every step whose in-memory window strictly
/// overlaps another step's window is copied with the flag; the judge readings, a step without a window and a hosted step are never flagged. The readings the caller holds are not changed,
/// so every checkpoint, the completed write and the incomplete write derive the same flags.
/// </para>
/// <para>
/// <c>run.toolVersion</c> and <c>run.gitCommit</c> come from the <see cref="AssemblyInformationalVersionAttribute"/> of this assembly: the SDK appends <c>+&lt;commit&gt;</c> when it builds
/// inside a git repository, so the part before <c>+</c> is the tool version and the part after it the commit (null when there is none).
/// </para>
/// </remarks>
public static class RunAssembler
{
    /// <summary>
    /// The reason of the energy totals of a concurrent run on a measured tier whose union could not be computed: no meter was given, or a step has no window (a reading read back
    /// from a file). A guard, never a sum of overlapping energies.
    /// </summary>
    public const string UnionNotComputedReason = "union-not-computed";

    /// <summary>The reason of a judge total while no judge reading exists (M2: the judge seam is a no-op).</summary>
    public const string NotScoredReason = "not-scored";

    /// <summary>The reason of <c>environment.cloudCostUsd</c> on every tier other than <c>cloudgpu</c>: no VM is rented.</summary>
    public const string NotCloudGpuReason = "not-cloudgpu";

    /// <summary>The reason of <c>environment.cloudCostUsd</c> on <c>cloudgpu</c> while no cost was observed (M5 fills it).</summary>
    public const string NotObservedReason = "not-observed";

    /// <summary>The reason of every currency of <c>costByCurrency</c> when at least one step carries no cost: a partial sum would understate the run.</summary>
    public const string MissingStepCostsReason = "missing-step-costs";

    /// <summary>The reason of a sum over the steps of a run that holds no step yet (a shutdown before the first step finished).</summary>
    public const string NoStepsReason = "no-steps";

    // The reason of a total whose first lacking step carries a label other than N/A or not-reported without a reason (not reachable through StepReading, kept so no total is ever unlabelled).
    private const string NotMeasuredReason = "not-measured";

    private const string JouleUnit = "J";
    private const string GramsPerKwhUnit = "gCO2e/kWh";
    private const string MillisecondUnit = "ms";
    private const string FallbackVersion = "0.0.0";

    private static readonly (string ToolVersion, string? GitCommit) ThisVersion = ParseVersion(
        typeof(RunAssembler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(RunAssembler).Assembly.GetName().Version);

    /// <summary>Computes the totals of a tier run over <paramref name="steps"/> (see the remarks of the type for the rules).</summary>
    /// <param name="tier">The tier of the run; a hosted tier has N/A energy totals.</param>
    /// <param name="mode">The mode of the run; concurrent mode on a measured tier takes the energy totals from the union of the step windows.</param>
    /// <param name="steps">The workflow steps of the run so far, in run order.</param>
    /// <param name="judge">The judge readings of the run; never part of the step totals.</param>
    /// <param name="wallClockMs">The sum of the workflow wall times of the (task, repeat) runs so far, in milliseconds, finite and not negative; null when no (task, repeat) started.</param>
    /// <param name="computeUnion">
    /// The union of the step windows (<see cref="IEnergyMeter.ComputeUnion"/> of the tier's meter), asked once for a concurrent run on a measured tier whose steps all carry a window;
    /// null when there is none, which leaves those energy totals not-reported <see cref="UnionNotComputedReason"/>.
    /// </param>
    /// <param name="cost">The <c>Cost</c> section whose carbon factor turns the net energy total into <c>gco2e</c>; null takes the defaults of <see cref="CostOptions"/> (no factor).</param>
    /// <returns>The totals.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> or <paramref name="judge"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The tier or the mode is not defined, or <paramref name="wallClockMs"/> is negative or not finite.</exception>
    public static Totals ComputeTotals(Tier tier, RunMode mode, IReadOnlyList<StepReading> steps, IReadOnlyList<StepReading> judge, double? wallClockMs, Func<IReadOnlyList<WindowSpan>, UnionEnergy>? computeUnion = null, CostOptions? cost = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(judge);
        RequireDefined(tier, mode);
        if (wallClockMs is { } wall && (!double.IsFinite(wall) || wall < 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(wallClockMs), wallClockMs, "The wall clock must be a finite number of milliseconds, not negative.");
        }

        (Metric gross, Metric net) = TierKinds.IsHosted(tier)
            ? (Metric.NotApplicable(MeterCapabilities.HostedTierReason), Metric.NotApplicable(MeterCapabilities.HostedTierReason))
            : mode == RunMode.Concurrent && steps.Count > 0
                ? UnionTotals(steps, computeUnion)
                : (Sum(steps, static step => step.GrossJoules, JouleUnit), Sum(steps, static step => step.NetJoules, JouleUnit));

        return new Totals(
            gross,
            net,
            Sum(steps, static step => step.InputTokens, TokenCounts.TokenUnit),
            Sum(steps, static step => step.OutputTokens, TokenCounts.TokenUnit),
            CostByCurrency(steps),
            wallClockMs is { } measured ? Metric.Measured(measured, MillisecondUnit) : Metric.NotReported(NoStepsReason),
            JudgeTotalsOf(judge),
            CostCalculator.EstimateEmissions(net, cost?.GCo2ePerKwh));
    }

    /// <summary>
    /// Builds the <c>environment</c> of a tier run from the sources the design names (design 3.4 "Environment fields"): the GPU identity of a measured tier, the local runtime's description and
    /// warm-up time, the tier options and the process. Every field without a source in M2 is null (<c>powerSource</c> until AJ-106; <c>instancePreset</c>, <c>region</c>, <c>containerImage</c>
    /// and <c>containerDigest</c> until M5), never a guess.
    /// </summary>
    /// <param name="tier">The tier of the run.</param>
    /// <param name="tierOptions">The options of the tier: reasoning mode, precision and the model of each agent.</param>
    /// <param name="gpu">The identity of the measured GPU, or null for a hosted tier.</param>
    /// <param name="runtime">The local runtime's description, or null when none was asked (a hosted tier).</param>
    /// <param name="modelLoad">The warm-up time of the local runtime, or null when none was observed.</param>
    /// <param name="cost">The <c>Cost</c> section whose price and factor fill <c>assumptions</c>; null takes the defaults of <see cref="CostOptions"/>.</param>
    /// <returns>The environment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tierOptions"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The tier is not defined, or <paramref name="modelLoad"/> is negative.</exception>
    /// <exception cref="ArgumentException">The reasoning mode of the tier is not off, on or unsupported.</exception>
    /// <exception cref="Core.Cli.ConfigurationException">An agent of the tier has no model ID.</exception>
    public static EnvironmentInfo BuildEnvironment(Tier tier, TierOptions tierOptions, GpuIdentity? gpu, LocalRuntimeInfo? runtime, TimeSpan? modelLoad, CostOptions? cost = null)
    {
        ArgumentNullException.ThrowIfNull(tierOptions);
        if (modelLoad < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(modelLoad), modelLoad, "A model load time is not negative.");
        }

        string provider = ProviderOf(tier);
        EnvironmentModel[] models =
        [
            .. AgentCatalog.Names.Select(agent =>
            {
                string modelId = tierOptions.ResolveModelId(agent);
                return new EnvironmentModel(agent, modelId, Baseline: !ModelIdRules.IsNemotron(modelId));
            }),
        ];
        CostOptions assumed = cost ?? new CostOptions();

        return new EnvironmentInfo(
            GpuName: gpu?.Name,
            DriverVersion: gpu?.DriverVersion,
            Os: RuntimeInformation.OSDescription,
            DotnetVersion: Environment.Version.ToString(),
            LocalRuntime: runtime?.Name,
            RuntimeVersion: runtime?.Version,
            GpuOffloadPercent: runtime?.GpuOffloadPercent,
            ModelLoadSeconds: modelLoad?.TotalSeconds,
            PowerSource: null,
            ReasoningMode: TierEnvironmentFacts.From(tierOptions).ReasoningMode,
            Temperature: 0.0,
            Provider: provider,
            InstancePreset: null,
            Region: null,
            GpuUuid: gpu?.Uuid,
            ContainerImage: null,
            ContainerDigest: null,
            Precision: runtime?.Precision ?? (string.IsNullOrWhiteSpace(tierOptions.Precision) ? null : tierOptions.Precision),
            CloudCostUsd: tier == Tier.CloudGpu ? Metric.NotReported(NotObservedReason) : Metric.NotApplicable(NotCloudGpuReason),
            EnergyScope: EnvironmentInfo.GpuBoardOnly,
            Assumptions: new EnvironmentAssumptions(
                Metric.Estimated(assumed.ElectricityPricePerKwh, assumed.Currency),
                assumed.GCo2ePerKwh is { } factor ? Metric.EstimatedQuantity(factor, GramsPerKwhUnit) : Metric.NotApplicable(CostCalculator.NotConfiguredReason)),
            Models: models);
    }

    /// <summary>
    /// Builds the <c>run</c> member: the given fields, the tool version and the commit of this build, no run flag, no stop reason and no self-judging statement (the judge of M3 sets it).
    /// </summary>
    /// <param name="id">The run id: the file name of the run file without its extension.</param>
    /// <param name="tier">The tier of the run.</param>
    /// <param name="taskNames">The names of the tasks of the run, in run order.</param>
    /// <param name="mode">The mode of the run.</param>
    /// <param name="repeat">The repeats per task.</param>
    /// <param name="startUtc">The start of the command.</param>
    /// <param name="status">The status of the document.</param>
    /// <returns>The run member.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="taskNames"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The tier or the mode is not defined.</exception>
    public static RunInfo BuildRunInfo(string id, Tier tier, IReadOnlyList<string> taskNames, RunMode mode, int repeat, DateTimeOffset startUtc, RunStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(taskNames);
        RequireDefined(tier, mode);

        return new RunInfo(id, tier, [.. taskNames], mode, repeat, startUtc, ThisVersion.ToolVersion, ThisVersion.GitCommit, status);
    }

    /// <summary>Assembles one document of a tier run: schema version 1, the given members, copies of the step and judge lists, and the totals over them (<see cref="ComputeTotals"/>). Every step is copied with its derived metrics computed from its current quality (<see cref="WithDerivedMetrics"/>), so a checkpoint and the final document alike carry them.</summary>
    /// <param name="run">The run member; its tier and mode decide the totals.</param>
    /// <param name="environment">The environment member.</param>
    /// <param name="probe">The probe of a measured tier, or null.</param>
    /// <param name="baseline">The idle baseline of a measured tier, or null.</param>
    /// <param name="steps">The workflow steps so far, in run order.</param>
    /// <param name="judge">The judge readings.</param>
    /// <param name="wallClockMs">The sum of the workflow wall times so far, in milliseconds; null when no (task, repeat) started.</param>
    /// <param name="computeUnion">The union of the step windows for the energy totals of a concurrent measured run (see <see cref="ComputeTotals"/>), or null.</param>
    /// <param name="cost">The <c>Cost</c> section for the carbon factor of the totals (see <see cref="ComputeTotals"/>), or null.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentNullException">A member, the steps or the judge readings are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="wallClockMs"/> is negative or not finite.</exception>
    public static RunDocument Assemble(
        RunInfo run,
        EnvironmentInfo environment,
        ProbeResult? probe,
        BaselineResult? baseline,
        IReadOnlyList<StepReading> steps,
        IReadOnlyList<StepReading> judge,
        double? wallClockMs,
        Func<IReadOnlyList<WindowSpan>, UnionEnergy>? computeUnion = null,
        CostOptions? cost = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(judge);

        StepReading[] stepCopy = [.. MarkSharedWindows(run.Tier, steps).Select(WithDerivedMetrics)];
        StepReading[] judgeCopy = [.. judge];
        return new RunDocument(
            RunDocument.CurrentSchemaVersion,
            run,
            environment,
            probe,
            baseline,
            stepCopy,
            judgeCopy,
            ComputeTotals(run.Tier, run.Mode, stepCopy, judgeCopy, wallClockMs, computeUnion, cost));
    }

    /// <summary>
    /// Returns a copy of a reading whose two derived metrics are computed from its own quality, net energy, cost and flags (REQ-010, <see cref="DerivedMetrics"/>); every other member, the in-memory
    /// output text and window included, is kept. A reading whose quality holds no value, whatever the reason (<c>not-scored</c>, <c>step-not-ok</c>, <c>empty-output</c>, a parse error), gets both
    /// metrics as N/A <c>missing-input</c>: the <c>not-scored</c> that a fresh reading carries is only the value before this method ran. The input is not changed, so assembling again gives the same result.
    /// </summary>
    /// <param name="reading">The step reading.</param>
    /// <returns>The reading with the derived metrics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null.</exception>
    public static StepReading WithDerivedMetrics(StepReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        return Copy(
            reading,
            reading.Outcome,
            reading.Flags,
            DerivedMetrics.JoulesPerQualityPoint(reading.NetJoules, reading.Quality, reading.Flags),
            DerivedMetrics.CostPerQualityPoint(reading.Cost, reading.Quality));
    }

    /// <summary>
    /// Flags shared windows (REQ-013, EDGE-004): on a measured tier, in both modes, every step whose in-memory <see cref="StepReading.Window"/> strictly overlaps the window of another
    /// step (<see cref="UnionWindowCalculator.Overlaps"/>; windows that only touch do not) is copied with <see cref="ReadingFlags.SharedWindow"/> added. A step without a window is never
    /// flagged and never counted as an overlap, and on a hosted tier nothing is flagged, because nothing of ours was measured. Pass the workflow steps only: the judge readings are never
    /// part of it. The flag changes no sum, and the input is not changed, so assembling again gives the same flags.
    /// </summary>
    /// <param name="tier">The tier of the run.</param>
    /// <param name="steps">The workflow steps, in run order.</param>
    /// <returns>The steps in the same order: a flagged copy where a window is shared, the same reading otherwise.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The tier is not defined.</exception>
    public static StepReading[] MarkSharedWindows(Tier tier, IReadOnlyList<StepReading> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (!Enum.IsDefined(tier))
        {
            throw new ArgumentOutOfRangeException(nameof(tier), (int)tier, "The tier is not a defined tier.");
        }

        StepReading[] marked = [.. steps];
        if (!TierKinds.IsMeasured(tier))
        {
            return marked;
        }

        List<int> windowed = [];
        List<WindowSpan> spans = [];
        for (int i = 0; i < marked.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(marked[i], nameof(steps));
            if (marked[i].Window is { } span)
            {
                windowed.Add(i);
                spans.Add(span);
            }
        }

        if (spans.Count == 0)
        {
            return marked;
        }

        bool[] shared = UnionWindowCalculator.OverlapsAnother(spans);
        for (int k = 0; k < shared.Length; k++)
        {
            StepReading step = marked[windowed[k]];
            if (shared[k] && !step.Flags.HasFlag(ReadingFlags.SharedWindow))
            {
                marked[windowed[k]] = Copy(step, step.Outcome, step.Flags | ReadingFlags.SharedWindow);
            }
        }

        return marked;
    }

    /// <summary>
    /// Copies <paramref name="reading"/> with another outcome and every other member unchanged, the in-memory window included (the reading has no init setters, so the copy goes through
    /// the constructor and its checks); the shutdown path of the orchestrator marks an in-flight step <c>incomplete</c> with it.
    /// </summary>
    /// <param name="reading">The reading to copy.</param>
    /// <param name="outcome">The outcome of the copy, one of <see cref="StepOutcomes"/>.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> is not one of <see cref="StepOutcomes"/>.</exception>
    public static StepReading WithOutcome(StepReading reading, string outcome)
    {
        ArgumentNullException.ThrowIfNull(reading);

        return Copy(reading, outcome, reading.Flags);
    }

    /// <summary>The <c>environment.provider</c> of a tier: <c>laptop</c>, <c>nebius-ai-cloud</c>, <c>build.nvidia.com</c> or <c>nebius-token-factory</c>.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The provider.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The tier is not defined.</exception>
    public static string ProviderOf(Tier tier) => tier switch
    {
        Tier.Local => "laptop",
        Tier.CloudGpu => "nebius-ai-cloud",
        Tier.Nvidia => "build.nvidia.com",
        Tier.Nebius => "nebius-token-factory",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), (int)tier, "The tier is not a defined tier."),
    };

    /// <summary>
    /// Splits an informational version into the tool version (the part before <c>+</c>) and the commit (the part after it, null when there is none); without an informational version the
    /// tool version is <paramref name="assemblyVersion"/>, or <c>0.0.0</c> when that is null too.
    /// </summary>
    /// <param name="informationalVersion">The <see cref="AssemblyInformationalVersionAttribute.InformationalVersion"/>, or null.</param>
    /// <param name="assemblyVersion">The assembly version, the fallback.</param>
    /// <returns>The tool version and the commit.</returns>
    public static (string ToolVersion, string? GitCommit) ParseVersion(string? informationalVersion, Version? assemblyVersion)
    {
        string fallback = assemblyVersion?.ToString() ?? FallbackVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return (fallback, null);
        }

        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return (informationalVersion.Trim(), null);
        }

        string version = informationalVersion[..plus].Trim();
        string commit = informationalVersion[(plus + 1)..].Trim();
        return (version.Length > 0 ? version : fallback, commit.Length > 0 ? commit : null);
    }

    /// <summary>
    /// How the <c>results:</c> line names a run file: the <c>--out</c> value as the user gave it, without trailing separators and with forward slashes, a forward slash, and the file name; never
    /// the absolute path the value resolved to.
    /// </summary>
    /// <param name="outputDisplay">The <c>--out</c> value as given.</param>
    /// <param name="filePath">The path of the run file.</param>
    /// <returns>The display path, for example <c>results/20261021T100000Z-nvidia.json</c>.</returns>
    /// <exception cref="ArgumentException">An argument is null or blank.</exception>
    public static string DisplayPath(string outputDisplay, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDisplay);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string folder = outputDisplay.Replace('\\', '/').TrimEnd('/');
        string name = Path.GetFileName(filePath.Replace('\\', '/'));
        return folder + "/" + name;
    }

    private static void RequireDefined(Tier tier, RunMode mode)
    {
        if (!Enum.IsDefined(tier))
        {
            throw new ArgumentOutOfRangeException(nameof(tier), (int)tier, "The tier is not a defined tier.");
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), (int)mode, "The run mode is not a defined value.");
        }
    }

    // The energy totals of a concurrent run on a measured tier: the union of the step windows, asked once, when the meter's union function was given and every step carries a window;
    // union-not-computed otherwise, never a sum of overlapping energies. The caller passes at least one step, so the union of nothing is never asked.
    private static (Metric Gross, Metric Net) UnionTotals(IReadOnlyList<StepReading> steps, Func<IReadOnlyList<WindowSpan>, UnionEnergy>? computeUnion)
    {
        (Metric, Metric) notComputed = (Metric.NotReported(UnionNotComputedReason), Metric.NotReported(UnionNotComputedReason));
        if (computeUnion is null)
        {
            return notComputed;
        }

        var spans = new WindowSpan[steps.Count];
        for (int i = 0; i < spans.Length; i++)
        {
            if (steps[i].Window is not { } span)
            {
                return notComputed;
            }

            spans[i] = span;
        }

        UnionEnergy union = computeUnion(spans) ?? throw new InvalidOperationException("The union function returned no energy.");
        return (union.GrossJoules, union.NetJoules);
    }

    // The copy of a reading through its constructor with another outcome and flags; every other member, the in-memory output text and window included, is kept.
    private static StepReading Copy(StepReading reading, string outcome, ReadingFlags flags) =>
        Copy(reading, outcome, flags, reading.JoulesPerQualityPoint, reading.CostPerQualityPoint);

    // The same copy with the two derived metrics replaced.
    private static StepReading Copy(StepReading reading, string outcome, ReadingFlags flags, Metric joulesPerQualityPoint, Metric costPerQualityPoint) =>
        new(
            reading.Task,
            reading.Repeat,
            reading.Agent,
            reading.ModelId,
            reading.Tier,
            reading.StartUtc,
            reading.EndUtc,
            outcome,
            reading.LatencyMs,
            reading.TtftMs,
            reading.InputTokens,
            reading.OutputTokens,
            reading.GrossJoules,
            reading.NetJoules,
            reading.AvgW,
            reading.PeakW,
            reading.Cost,
            reading.Retries,
            reading.WaitMs,
            flags,
            reading.Method,
            reading.SampleCount,
            reading.SampleRateHz,
            reading.ThrottleReasons,
            reading.Quality,
            reading.QualityReason,
            joulesPerQualityPoint,
            costPerQualityPoint,
            reading.OutputSha256,
            reading.OutputChars,
            reading.OutputText,
            reading.Window);

    // The sum of a measured value over the steps; the label and the reason of the first step that lacks it otherwise; not-reported no-steps without a step.
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

        return Metric.Measured(sum, unit);
    }

    // The costs per currency, in ordinal currency order, under the all-steps rule: the sums when every step carries an estimated cost; every currency seen N/A missing-step-costs when at
    // least one step lacks one (a partial sum would understate the run); empty when no step carries a cost (M2: every cost is N/A).
    private static Dictionary<string, Metric> CostByCurrency(IReadOnlyList<StepReading> steps)
    {
        var sums = new SortedDictionary<string, double>(StringComparer.Ordinal);
        bool missing = false;
        foreach (StepReading step in steps)
        {
            if (step.Cost is { Label: MetricLabel.Estimated, Currency: { } currency, Value: { } value })
            {
                sums[currency] = (sums.TryGetValue(currency, out double sum) ? sum : 0.0) + value;
            }
            else
            {
                missing = true;
            }
        }

        var costs = new Dictionary<string, Metric>(StringComparer.Ordinal);
        foreach ((string currency, double sum) in sums)
        {
            costs[currency] = missing ? Metric.NotApplicable(MissingStepCostsReason) : Metric.Estimated(sum, currency);
        }

        return costs;
    }

    // The judge totals: N/A not-scored while no judge reading exists, otherwise the same sums as the steps (M3).
    private static JudgeTotals JudgeTotalsOf(IReadOnlyList<StepReading> judge) =>
        judge.Count == 0
            ? new JudgeTotals(Metric.NotApplicable(NotScoredReason), Metric.NotApplicable(NotScoredReason), new Dictionary<string, Metric>(StringComparer.Ordinal))
            : new JudgeTotals(
                Sum(judge, static step => step.InputTokens, TokenCounts.TokenUnit),
                Sum(judge, static step => step.OutputTokens, TokenCounts.TokenUnit),
                CostByCurrency(judge));
}