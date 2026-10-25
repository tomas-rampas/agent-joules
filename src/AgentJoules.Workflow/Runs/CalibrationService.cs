using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Tasks;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Graph;
using AgentJoules.Workflow.Local;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;

namespace AgentJoules.Workflow.Runs;

/// <summary>What one calibration measures (REQ-021, AJ-109).</summary>
/// <param name="Task">The task whose workflow repeats in the window.</param>
/// <param name="Seconds">The length of the window in seconds, 1 or more; a repeat in flight at the deadline completes and the window ends after it.</param>
public sealed record CalibrationRequest(TaskDefinition Task, int Seconds);

/// <summary>
/// The calibration of the two energy methods against each other (REQ-021, REQ-004, REQ-005, AJ-109, design 3.1 and 3.3): one window during which the task workflow repeats, measured by the energy counter and by power sampling at
/// once, and the relative difference of the two totals.
/// </summary>
/// <remarks>
/// <para>
/// The service opens the GPU once and keeps it for the whole calibration, starts one <see cref="SampledEnergyMeter"/> at <c>Meter:SampleRateHz</c> and performs the unmetered warm-up of the local runtime exactly as a
/// measured run does (every distinct model of the tier, once; there is no idle baseline). It then opens one window, repeats the four agents of the task through the provider client of the local tier until the requested seconds have
/// elapsed on the clock (the first repeat always runs, a repeat in flight at the deadline completes, the window closes after it), and closes the window. The workflow runs on pipelines without the <see cref="AgentJoules.Metering.MeteredChatClient"/>
/// (<see cref="AgentFactory.Metered"/> false): no window of its own, no gate, no reading, no results file and no judge; the calibration is the one window.
/// </para>
/// <para>
/// Both totals come from that window. The method of the meter is the counter when the GPU supports it, and sampling otherwise, whatever <c>Meter:Method</c> says (a calibration compares both methods, so a forced method has no
/// meaning here). With the counter method the window's gross joules are the counter's millijoule delta read at the window's two edges, and the sampling total is the integral of the same window's power samples (average power times the
/// window's duration, the integral the meter computed). With the sampling method the gross joules are the integral and the counter total is N/A with the reason <c>counter-not-supported: &lt;probe reason&gt;</c>. A counter that cannot be read at the end of the
/// window (the probe found it supported) is N/A with <c>counter-not-supported: counter-not-read</c>, and one that went backwards is N/A with <c>counter-went-backwards</c>; nothing is estimated, and the difference is N/A with the
/// reason up to the colon (<see cref="CalibrationDocument.DifferenceOf"/>).
/// </para>
/// <para>
/// A failing step of the workflow ends the calibration with the exit code of its exception (<see cref="StepFailedException"/>, exit code 5) and no result; a cancellation (Ctrl+C) ends it as the shutdown it is. The file name and the writing
/// of the document belong to the command.
/// </para>
/// </remarks>
/// <param name="options">The bound and validated options.</param>
/// <param name="gpus">The GPU port the calibration opens.</param>
/// <param name="localRuntime">The runtime of the local tier: its warm-up and its description.</param>
/// <param name="powerSource">Tells where the machine takes its power from; read once, before the window.</param>
/// <param name="time">The clock of the meter, the window and the deadline.</param>
/// <param name="agents">The factory of the workload's agents; it must build unmetered pipelines (<see cref="AgentFactory.Metered"/> false).</param>
/// <exception cref="ArgumentNullException">An argument is null.</exception>
/// <exception cref="ArgumentException"><paramref name="agents"/> builds metered pipelines.</exception>
public sealed class CalibrationService(
    IOptions<AgentJoulesOptions> options,
    IGpuTelemetrySourceFactory gpus,
    ILocalRuntime localRuntime,
    IPowerSourceDetector powerSource,
    TimeProvider time,
    AgentFactory agents)
{
    // The GPU port's reason names the library and the exception type; both must survive (the same cut as the orchestrator's).
    private const int MaxReasonLength = 128;

    // The reasons of the energy meter's readings, which the service maps: a counter that could not be read at an edge, and one that read lower at the end than at the start (EnergyReasons, internal to the core).
    private const string CounterNotRead = "counter-not-read";
    private const string CounterWentBackwards = "counter-went-backwards";
    private const string JouleUnit = "J";

    private readonly IOptions<AgentJoulesOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IGpuTelemetrySourceFactory _gpus = gpus ?? throw new ArgumentNullException(nameof(gpus));
    private readonly ILocalRuntime _localRuntime = localRuntime ?? throw new ArgumentNullException(nameof(localRuntime));
    private readonly IPowerSourceDetector _powerSource = powerSource ?? throw new ArgumentNullException(nameof(powerSource));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly AgentFactory _agents = Unmetered(agents);

    /// <summary>Runs the calibration and returns its document.</summary>
    /// <param name="request">The task and the length of the window.</param>
    /// <param name="cancellationToken">Cancels the calibration (the shutdown of the process).</param>
    /// <returns>The document: both totals and their relative difference, the window, the samples and the environment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="CalibrationRequest.Seconds"/> is below 1.</exception>
    /// <exception cref="ConfigurationException"><c>Tiers:local</c> is not configured or an agent has no model ID (exit code 2).</exception>
    /// <exception cref="MeasurementPreconditionException">The GPU cannot be opened, or the runtime reports an offload that is not a number from 0 to 100 (exit code 6).</exception>
    /// <exception cref="EndpointUnreachableException">The warm-up call could not reach the runtime (exit code 5).</exception>
    /// <exception cref="StepFailedException">A provider call of the workflow failed (exit code 5); nothing is written.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async Task<CalibrationDocument> RunAsync(CalibrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Seconds, 1);

        AgentJoulesOptions options = _options.Value;
        string name = TierOptions.NameOf(Tier.Local);
        TierOptions tierOptions = options.Tiers.TryGetValue(name, out TierOptions? configured) && configured is not null
            ? configured
            : throw new ConfigurationException("Tiers:" + name + " is not configured");

        GpuOpenResult opened = _gpus.Open(options.Gpu.Index);
        using IGpuTelemetrySource source = opened.Source
            ?? throw new MeasurementPreconditionException($"the calibration measures the GPU, but the GPU cannot be opened: {TextEcho.ForMessage(opened.Reason ?? "no reason given", MaxReasonLength)}");
        MeterCapabilities capabilities = CapabilitiesOf(source, options.Meter.SampleRateHz);
        ProbeResult probe = ProbeResult.Create(source.Identity, source.EnergyCounterSupported, capabilities, options.Meter.SampleRateHz);

        // The meter does not own the source: it is disposed first (declared last), so its sampling loop is gone before the GPU session ends.
        await using SampledEnergyMeter meter = new(source, capabilities, _time);
        await meter.StartAsync(cancellationToken).ConfigureAwait(false);

        TimeSpan? modelLoad = await LocalWarmUp.RunAsync(_localRuntime, tierOptions, cancellationToken).ConfigureAwait(false);
        LocalRuntimeInfo runtime = await _localRuntime.DescribeAsync(cancellationToken).ConfigureAwait(false);

        // A description that cannot come from a measurement (an offload that is not a number or lies outside 0 to 100) stops the calibration before the window, as it stops a run before its baseline.
        _ = RunAssembler.RunFlagsOf(runtime);
        string power = PowerSources.Normalize(_powerSource.Detect());

        TimeSpan deadline = TimeSpan.FromSeconds(request.Seconds);
        IMeterWindow window = meter.OpenWindow();
        DateTimeOffset startUtc = _time.GetUtcNow();
        long started = _time.GetTimestamp();
        int repeats = 0;
        do
        {
            repeats++;
            await RunRepeatAsync(tierOptions, request.Task, repeats, options.Workflow.Seed, cancellationToken).ConfigureAwait(false);
        }
        while (_time.GetElapsedTime(started) < deadline);

        EnergyReading reading = window.Close();

        // The window ends as long after it started as the meter measured it on its monotonic clock: a step of the wall clock during the window cannot put the end before the start.
        DateTimeOffset endUtc = startUtc + TimeSpan.FromSeconds(reading.DurationSeconds);

        (Metric counter, Metric sampling) = Totals(capabilities, reading);
        EnvironmentInfo environment = RunAssembler.BuildEnvironment(Tier.Local, tierOptions, source.Identity, runtime, modelLoad, options.Cost, power);
        (string toolVersion, string gitCommit) = RunAssembler.CurrentVersion;

        return new CalibrationDocument(
            request.Task.Name,
            request.Seconds,
            reading.DurationSeconds,
            reading.SampleCount,
            reading.SampleRateHz,
            repeats,
            startUtc,
            endUtc,
            toolVersion,
            gitCommit,
            counter,
            sampling,
            CalibrationDocument.DifferenceOf(counter, sampling),
            probe,
            new CalibrationEnvironment(environment.LocalRuntime, environment.RuntimeVersion, environment.GpuOffloadPercent, environment.Precision, environment.ModelLoadSeconds, environment.PowerSource));
    }

    private static AgentFactory Unmetered(AgentFactory agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return agents.Metered ? throw new ArgumentException("The calibration repeats an unmetered workload: the agent factory must have Metered false.", nameof(agents)) : agents;
    }

    // The counter whenever the GPU has one, sampling otherwise; the sample rate is the configured one in both. The setting is never "auto": a fallback is a run's disclosure, and a calibration discloses the missing counter in its file.
    private static MeterCapabilities CapabilitiesOf(IGpuTelemetrySource source, double sampleRateHz)
    {
        MeterMethodSetting setting = source.EnergyCounterSupported ? MeterMethodSetting.Counter : MeterMethodSetting.Sampling;
        return MeterMethodSelector.TrySelect(setting, source, sampleRateHz, out MeterCapabilities? capabilities)
            ? capabilities
            : throw new InvalidOperationException("The meter method of the calibration could not be selected.");
    }

    // One repeat: four new agents with new pipelines and a new workflow graph, as a run builds them, on pipelines nobody meters.
    private async Task RunRepeatAsync(TierOptions tierOptions, TaskDefinition task, int repeat, int seed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AIAgent> workload = _agents.CreateAll(Tier.Local, tierOptions, task, repeat, seed);
        _ = await new AgentJoulesWorkflowFactory(workload, task).RunAsync(cancellationToken).ConfigureAwait(false);
    }

    // The two totals of the window. Counter method: gross is the counter's delta and the integral is the average power times the window (the average is the integral over the duration). Sampling method: gross is the integral and the counter has no total.
    private static (Metric Counter, Metric Sampling) Totals(MeterCapabilities capabilities, EnergyReading reading)
    {
        if (capabilities.Method == EnergyMethod.Counter)
        {
            Metric sampling = reading.AverageWatts.Value is { } average
                ? Metric.Measured(average * reading.DurationSeconds, JouleUnit)
                : Metric.NotApplicable(CalibrationDocument.NoSamplesReason);
            return (CounterTotal(reading.GrossJoules), sampling);
        }

        Metric integral = reading.GrossJoules.Value is { } joules
            ? Metric.Measured(joules, JouleUnit)
            : Metric.NotApplicable(CalibrationDocument.NoSamplesReason);
        return (Metric.NotApplicable(CounterNotSupported(capabilities.CounterUnsupportedReason)), integral);
    }

    // The reason of a counter total that does not exist carries one prefix: the NVML source already says "counter-not-supported: NVML_ERROR_NOT_SUPPORTED", so that is kept as it is, and a bare reason gets the prefix.
    private static string CounterNotSupported(string? reason) =>
        reason is not null && reason.StartsWith(CalibrationDocument.CounterNotSupportedReason, StringComparison.Ordinal)
            ? reason
            : $"{CalibrationDocument.CounterNotSupportedReason}: {reason}";

    private static Metric CounterTotal(Metric gross) => gross switch
    {
        { Value: { } joules } => Metric.Measured(joules, JouleUnit),
        { Reason: CounterWentBackwards } => Metric.NotApplicable(CalibrationDocument.CounterWentBackwardsReason),
        _ => Metric.NotApplicable(CounterNotSupported(gross.Reason ?? CounterNotRead)),
    };
}