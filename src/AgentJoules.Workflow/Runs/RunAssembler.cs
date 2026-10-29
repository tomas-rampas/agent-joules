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
/// inside a git repository, so the part before <c>+</c> is the tool version and the part after it the commit (<c>unknown</c> when there is none).
/// </para>
/// </remarks>
public static class RunAssembler
{
    /// <summary>
    /// The reason of the energy totals of a concurrent run on a measured tier whose union could not be computed: no meter was given, or a step has no window (a reading read back
    /// from a file). A guard, never a sum of overlapping energies.
    /// </summary>
    public const string UnionNotComputedReason = "union-not-computed";

    /// <summary>The reason of a judge total while no judge reading exists (the judge was skipped or produced no reading).</summary>
    public const string NotScoredReason = "not-scored";

    /// <summary>The reason of <c>environment.cloudCostUsd</c> on every tier other than <c>cloudgpu</c>: no VM is rented.</summary>
    public const string NotCloudGpuReason = "not-cloudgpu";

    /// <summary>The reason of <c>environment.cloudCostUsd</c> on <c>cloudgpu</c>: the whole-run bill is known only after the invocation, so the run file states it as not reported and the final bill is recorded in the Cost and Teardown sections of <c>docs/cloudgpu-run.md</c> (peer review M-1). <c>CloudGpu:ObservedCostUsd</c> is a budget-guard input only.</summary>
    public const string BilledAfterRunReason = "billed-after-run";

    /// <summary>The reason of every currency of <c>costByCurrency</c> when at least one step carries no cost: a partial sum would understate the run.</summary>
    public const string MissingStepCostsReason = StepCosts.MissingStepCostsReason;

    /// <summary>The reason of a sum over the steps of a run that holds no step yet (a shutdown before the first step finished).</summary>
    public const string NoStepsReason = "no-steps";

    // The reason of a total whose first lacking step carries a label other than N/A or not-reported without a reason (not reachable through StepReading, kept so no total is ever unlabelled).
    private const string NotMeasuredReason = "not-measured";

    private const string JouleUnit = "J";
    private const string GramsPerKwhUnit = "gCO2e/kWh";
    private const string MillisecondUnit = "ms";
    private const string FallbackVersion = "0.0.0";

    /// <summary>The version and the commit of this build, as a run file has them (<c>run.toolVersion</c>, <c>run.gitCommit</c>); a calibration file carries the same pair (AJ-109).</summary>
    public static (string ToolVersion, string GitCommit) CurrentVersion { get; } = ParseVersion(
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
            StepCosts.ByCurrency(steps),
            wallClockMs is { } measured ? Metric.Measured(measured, MillisecondUnit) : Metric.NotReported(NoStepsReason),
            JudgeTotalsOf(judge),
            CostCalculator.EstimateEmissions(net, cost?.GCo2ePerKwh));
    }

    /// <summary>
    /// Builds the <c>environment</c> of a tier run from the sources the design names (design 3.4 "Environment fields"): the GPU identity of a measured tier, the local runtime's description and
    /// warm-up time, the tier options and the process. Every field without a source in M2 is null (<c>instancePreset</c>, <c>region</c>, <c>containerImage</c>
    /// and <c>containerDigest</c> unless the cloudgpu tier sets them in <c>CloudGpu</c>, AJ-122), never a guess.
    /// </summary>
    /// <param name="tier">The tier of the run.</param>
    /// <param name="tierOptions">The options of the tier: reasoning mode, precision and the model of each agent.</param>
    /// <param name="gpu">The identity of the measured GPU, or null for a hosted tier.</param>
    /// <param name="runtime">The local runtime's description, or null when none was asked (a hosted tier). A runtime that names itself states <c>precision</c> as it is, null included; only a tier without a named runtime takes <c>Tiers:&lt;t&gt;:Precision</c>.</param>
    /// <param name="modelLoad">The warm-up time of the local runtime, or null when none was observed.</param>
    /// <param name="cost">The <c>Cost</c> section whose price and factor fill <c>assumptions</c>; null takes the defaults of <see cref="CostOptions"/>.</param>
    /// <param name="powerSource">The detected power source (<see cref="PowerSources"/>), written as <c>powerSource</c>; a value outside the vocabulary is written as <c>unknown</c>, and null (none was asked) leaves the field null.</param>
    /// <param name="cloudGpu">The <c>CloudGpu</c> section (AJ-122): on the cloudgpu tier it fills provider, instance preset, region, container image and digest, and the observed cost; null (or another tier) leaves them as for a tier without a rented VM.</param>
    /// <returns>The environment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tierOptions"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The tier is not defined, or <paramref name="modelLoad"/> is negative.</exception>
    /// <exception cref="ArgumentException">The reasoning mode of the tier is not off, on or unsupported.</exception>
    /// <exception cref="Core.Cli.ConfigurationException">An agent of the tier has no model ID.</exception>
    public static EnvironmentInfo BuildEnvironment(Tier tier, TierOptions tierOptions, GpuIdentity? gpu, LocalRuntimeInfo? runtime, TimeSpan? modelLoad, CostOptions? cost = null, string? powerSource = null, CloudGpuOptions? cloudGpu = null)
    {
        ArgumentNullException.ThrowIfNull(tierOptions);
        if (modelLoad < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(modelLoad), modelLoad, "A model load time is not negative.");
        }

        string provider = ProviderOf(tier);
        CloudGpuOptions? rented = tier == Tier.CloudGpu ? cloudGpu : null;
        if (Text(rented?.Provider) is { } named)
        {
            provider = named;
        }

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
            PowerSource: powerSource is null ? null : PowerSources.Normalize(powerSource),
            ReasoningMode: TierEnvironmentFacts.From(tierOptions).ReasoningMode,
            Temperature: 0.0,
            Provider: provider,
            InstancePreset: Text(rented?.InstancePreset),
            Region: Text(rented?.Region),
            GpuUuid: gpu?.Uuid,
            ContainerImage: Text(rented?.ContainerImage),
            ContainerDigest: Text(rented?.ContainerDigest),
            Precision: PrecisionOf(runtime, tierOptions),
            CloudCostUsd: tier == Tier.CloudGpu ? Metric.NotReported(BilledAfterRunReason) : Metric.NotApplicable(NotCloudGpuReason),
            EnergyScope: EnvironmentInfo.GpuBoardOnly,
            Assumptions: new EnvironmentAssumptions(
                Metric.Estimated(assumed.ElectricityPricePerKwh, assumed.Currency),
                assumed.GCo2ePerKwh is { } factor ? Metric.EstimatedQuantity(factor, GramsPerKwhUnit) : Metric.NotApplicable(CostCalculator.NotConfiguredReason)),
            Models: models);
    }

    // A setting that is blank states nothing: null, never an empty string.
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // A runtime that names itself states the precision of what it serves: the quantization level of Ollama, or the configured value of a NIM container. A null there is a precision nobody stated, and it stays null:
    // Tiers:<t>:Precision is the NIM value (BF16 in the shipped configuration) and must not label an Ollama model. A tier without a named runtime (a hosted tier, the no-op runtime) takes the configured precision.
    private static string? PrecisionOf(LocalRuntimeInfo? runtime, TierOptions tierOptions) =>
        runtime?.Name is not null
            ? runtime.Precision
            : string.IsNullOrWhiteSpace(tierOptions.Precision) ? null : tierOptions.Precision;

    /// <summary>
    /// The run-level flags a local runtime's description implies (REQ-020, AJ-105): <see cref="RunFlagNames.PartialOffload"/> when the GPU offload is a number below 100, and none when it is 100 or unknown. An unknown
    /// offload (null) is not 0 and sets no flag. The description is checked first: a value that is not a number or lies outside 0 to 100 cannot come from a measurement and stops the run.
    /// </summary>
    /// <param name="runtime">The description of the local runtime, or null when none was asked (a hosted tier).</param>
    /// <returns>The flags, in the order of <see cref="RunFlagNames.All"/>; empty when none applies.</returns>
    /// <exception cref="Core.Cli.MeasurementPreconditionException">The offload is NaN, infinite or outside 0 to 100 (exit code 6).</exception>
    public static IReadOnlyList<string> RunFlagsOf(LocalRuntimeInfo? runtime)
    {
        return runtime?.GpuOffloadPercent is not { } offload
            ? []
            : !double.IsFinite(offload) || offload < 0.0 || offload > 100.0
                ? throw new Core.Cli.MeasurementPreconditionException("the GPU offload could not be determined: the runtime reported a value outside 0 to 100")
                : offload < 100.0 ? [RunFlagNames.PartialOffload] : [];
    }

    /// <summary>
    /// The run-level flags of a tier run (REQ-020, REQ-021): those of <see cref="RunFlagsOf(LocalRuntimeInfo?)"/>, and <see cref="RunFlagNames.NonPublishable"/> when the idle baseline of the tier is shorter than
    /// <see cref="BaselineOptions.PublishableSeconds"/> (30 s). A hosted tier has no baseline and passes null, so it is never flagged for one.
    /// </summary>
    /// <param name="runtime">The description of the local runtime, or null when none was asked (a hosted tier).</param>
    /// <param name="baselineSeconds">The length of the idle baseline of a measured tier in seconds, or null for a tier without a baseline.</param>
    /// <returns>The flags, in the order of <see cref="RunFlagNames.All"/>; empty when none applies.</returns>
    /// <exception cref="Core.Cli.MeasurementPreconditionException">The offload is NaN, infinite or outside 0 to 100 (exit code 6).</exception>
    public static IReadOnlyList<string> RunFlagsOf(LocalRuntimeInfo? runtime, double? baselineSeconds)
    {
        IReadOnlyList<string> flags = RunFlagsOf(runtime);
        return baselineSeconds is { } seconds && seconds < BaselineOptions.PublishableSeconds ? [.. flags, RunFlagNames.NonPublishable] : flags;
    }

    /// <summary>
    /// Builds the <c>run</c> member: the given fields, the tool version and the commit of this build, the given run flags (none by default), no stop reason, and the self-judging statement when given (null while the judge has not run: a checkpoint, a skipped judge, a failed or incomplete file).
    /// </summary>
    /// <param name="id">The run id: the file name of the run file without its extension.</param>
    /// <param name="tier">The tier of the run.</param>
    /// <param name="taskNames">The names of the tasks of the run, in run order.</param>
    /// <param name="mode">The mode of the run.</param>
    /// <param name="repeat">The repeats per task.</param>
    /// <param name="startUtc">The start of the command.</param>
    /// <param name="status">The status of the document.</param>
    /// <param name="selfJudged">Whether the judge model is the model of any step, or null when the judge has not run.</param>
    /// <param name="flags">The run flags (<see cref="RunFlagsOf(LocalRuntimeInfo?)"/>), or null for none.</param>
    /// <param name="stopReason">Why a partial run stopped (AJ-122: <c>cost-cap</c> or <c>time-box</c>), or null.</param>
    /// <returns>The run member.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="taskNames"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The tier or the mode is not defined.</exception>
    public static RunInfo BuildRunInfo(string id, Tier tier, IReadOnlyList<string> taskNames, RunMode mode, int repeat, DateTimeOffset startUtc, RunStatus status, bool? selfJudged = null, IReadOnlyList<string>? flags = null, string? stopReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(taskNames);
        RequireDefined(tier, mode);

        return new RunInfo(id, tier, [.. taskNames], mode, repeat, startUtc, CurrentVersion.ToolVersion, CurrentVersion.GitCommit, status, Flags: flags, StopReason: stopReason, SelfJudged: selfJudged);
    }

    /// <summary>Assembles one document of a tier run: schema version 1, the given members, copies of the step and judge lists, and the totals over them (<see cref="ComputeTotals"/>). Every step is copied with its shared-window flag (<see cref="MarkSharedWindows"/>), its duplicate-output flag (<see cref="MarkDuplicateOutputs"/>) and its derived metrics computed from its current quality (<see cref="WithDerivedMetrics"/>), so a checkpoint and the final document alike carry them.</summary>
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

        StepReading[] stepCopy = [.. MarkDuplicateOutputs(MarkSharedWindows(run.Tier, steps)).Select(WithDerivedMetrics)];
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
            joulesPerQualityPoint: DerivedMetrics.JoulesPerQualityPoint(reading.NetJoules, reading.Quality, reading.Flags),
            costPerQualityPoint: DerivedMetrics.CostPerQualityPoint(reading.Cost, reading.Quality));
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
                marked[windowed[k]] = Copy(step, flags: step.Flags | ReadingFlags.SharedWindow);
            }
        }

        return marked;
    }

    /// <summary>
    /// Flags repeated outputs (user decision 3 of the M3 exit, AJ-104): every workflow step that finished <c>ok</c> with output (<see cref="StepReading.OutputChars"/> above 0) and whose
    /// <see cref="StepReading.OutputSha256"/> equals that of an earlier step of the same task and the same repeat (one workflow run: the echo of the Writer of the output of the Planner) is copied with <see cref="ReadingFlags.DuplicateOutput"/> added. Earlier is by start time, and by position
    /// in the list for equal start times; identical outputs of different repeats or different tasks are expected at temperature 0 and are never compared; the earlier step itself is not flagged, so three equal outputs flag the second and the third. A step without a recorded hash neither is flagged nor makes a later one a
    /// duplicate. Pass the workflow steps only: the judge readings are never part of it. The flag changes no sum, and the input is not changed, so assembling again gives the same flags.
    /// </summary>
    /// <param name="steps">The workflow steps, in run order.</param>
    /// <returns>The steps in the same order: a flagged copy where an output repeats, the same reading otherwise.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> is null or holds a null.</exception>
    public static StepReading[] MarkDuplicateOutputs(IReadOnlyList<StepReading> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        StepReading[] marked = [.. steps];
        foreach (StepReading step in marked)
        {
            ArgumentNullException.ThrowIfNull(step, nameof(steps));
        }

        HashSet<(string Task, int Repeat, string Hash)> seen = [];
        foreach (int index in Enumerable.Range(0, marked.Length).OrderBy(i => marked[i].StartUtc).ThenBy(static i => i))
        {
            StepReading step = marked[index];
            if (step.OutputSha256 is not { Length: > 0 } hash)
            {
                continue;
            }

            bool repeated = !seen.Add((step.Task, step.Repeat, hash));
            if (repeated && step.OutputChars is > 0 && string.Equals(step.Outcome, StepOutcomes.Ok, StringComparison.Ordinal) && !step.Flags.HasFlag(ReadingFlags.DuplicateOutput))
            {
                marked[index] = Copy(step, flags: step.Flags | ReadingFlags.DuplicateOutput);
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

        return Copy(reading, outcome: outcome);
    }

    /// <summary>
    /// Copies <paramref name="reading"/> with the quality and the reason the judge produced and every other member unchanged, the in-memory output text and window included. The two derived metrics are
    /// left as they are: <see cref="Assemble"/> computes them from the quality of each step, so the judge never writes them. The reason is passed explicitly because the constructor derives it only
    /// when no quality is given: a model-judged quality takes <c>null</c>, a quality that is N/A takes <c>not-scored</c> (the value every row of the judge's reason table shares).
    /// </summary>
    /// <param name="reading">The step reading.</param>
    /// <param name="quality">The quality: model-judged from 1 to 5, or N/A with the reason that says why the step has none.</param>
    /// <param name="qualityReason">The <see cref="StepReading.QualityReason"/>: null for a scored step, <c>not-scored</c> otherwise.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> or <paramref name="quality"/> is null.</exception>
    /// <exception cref="ArgumentException">The quality does not have the shape of a judge score (a model-judged number from 1 to 5 without a unit or a currency), or is not one of the labels a quality may carry.</exception>
    public static StepReading WithQuality(StepReading reading, Metric quality, string? qualityReason)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(quality);

        return Copy(reading, quality: quality, qualityReason: qualityReason);
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
    /// Splits an informational version into the tool version (the part before <c>+</c>) and the commit (the part after it, <c>unknown</c> when there is none); without an informational version the
    /// tool version is <paramref name="assemblyVersion"/>, or <c>0.0.0</c> when that is null too.
    /// </summary>
    /// <param name="informationalVersion">The <see cref="AssemblyInformationalVersionAttribute.InformationalVersion"/>, or null.</param>
    /// <param name="assemblyVersion">The assembly version, the fallback.</param>
    /// <returns>The tool version and the commit.</returns>
    public static (string ToolVersion, string GitCommit) ParseVersion(string? informationalVersion, Version? assemblyVersion)
    {
        string fallback = assemblyVersion?.ToString() ?? FallbackVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return (fallback, RunInfo.UnknownCommit);
        }

        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return (informationalVersion.Trim(), RunInfo.UnknownCommit);
        }

        string version = informationalVersion[..plus].Trim();
        string commit = informationalVersion[(plus + 1)..].Trim();
        return (version.Length > 0 ? version : fallback, commit.Length > 0 ? commit : RunInfo.UnknownCommit);
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

    // The copy of a reading through its constructor: each optional argument that is given replaces the member of that name, every other member, the in-memory output text, the window and the
    // judged agent included, is kept. A given quality replaces the quality and its reason together (the reason may be null for a scored step).
    private static StepReading Copy(
        StepReading reading,
        string? outcome = null,
        ReadingFlags? flags = null,
        Metric? joulesPerQualityPoint = null,
        Metric? costPerQualityPoint = null,
        Metric? quality = null,
        string? qualityReason = null) =>
        new(
            reading.Task,
            reading.Repeat,
            reading.Agent,
            reading.ModelId,
            reading.Tier,
            reading.StartUtc,
            reading.EndUtc,
            outcome ?? reading.Outcome,
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
            flags ?? reading.Flags,
            reading.Method,
            reading.SampleCount,
            reading.SampleRateHz,
            reading.ThrottleReasons,
            quality ?? reading.Quality,
            quality is null ? reading.QualityReason : qualityReason,
            joulesPerQualityPoint ?? reading.JoulesPerQualityPoint,
            costPerQualityPoint ?? reading.CostPerQualityPoint,
            reading.OutputSha256,
            reading.OutputChars,
            reading.OutputText,
            reading.Window,
            reading.JudgedAgent);

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

    // The judge totals: N/A not-scored while no judge reading exists, otherwise the same sums as the steps (M3).
    private static JudgeTotals JudgeTotalsOf(IReadOnlyList<StepReading> judge) =>
        judge.Count == 0
            ? new JudgeTotals(Metric.NotApplicable(NotScoredReason), Metric.NotApplicable(NotScoredReason), new Dictionary<string, Metric>(StringComparer.Ordinal))
            : new JudgeTotals(
                Sum(judge, static step => step.InputTokens, TokenCounts.TokenUnit),
                Sum(judge, static step => step.OutputTokens, TokenCounts.TokenUnit),
                StepCosts.ByCurrency(judge));
}