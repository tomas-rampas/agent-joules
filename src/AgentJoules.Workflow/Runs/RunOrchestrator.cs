using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Graph;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Local;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentJoules.Workflow.Runs;

/// <summary>
/// The production <see cref="IRunOrchestrator"/> (REQ-017, REQ-013, EDGE-005, EDGE-016, design 3.4 "Run orchestration"): runs every tier of the plan in order, and in each tier every task in
/// file order for every repeat, with fresh agents, fresh pipelines and a fresh workflow per (task, repeat); writes one checkpoint after each (task, repeat) and the final document with
/// status <c>completed</c>; and tells the user about every finished step, the idle baseline, a judge that failed for some steps of a tier (one warning line, EDGE-012) and the closing lines of each tier through the <see cref="IRunListener"/>.
/// </summary>
/// <remarks>
/// <para>
/// A measured tier (<c>local</c>, <c>cloudgpu</c>) opens the GPU at <c>Gpu:Index</c> (the preflight closed its own session), selects the meter method, starts a
/// <see cref="SampledEnergyMeter"/>, and, for the local tier only, calls the unmetered warm-up of the <see cref="ILocalRuntime"/> once for every distinct model of the tier (the sum of the times is <c>environment.modelLoadSeconds</c>; a failing warm-up ends the run with exit code 5 before the baseline) and asks the runtime to describe itself, then measures the idle baseline for
/// the plan's baseline length; the meter and the GPU are released in a <c>finally</c>. A GPU offload below 100 in that description adds the run flag <c>partial-offload</c> to every write of the tier, checkpoints and
/// the final file alike (<see cref="RunAssembler.RunFlagsOf(LocalRuntimeInfo?)"/>, AJ-105); an offload that is not a number from 0 to 100 stops the run with exit code 6 before the baseline. A hosted tier (<c>nvidia</c>, <c>nebius</c>) uses an <see cref="UnmeteredEnergyMeter"/>, never opens the
/// GPU and records no probe and no baseline. The sampling-fallback disclosure is the command's (it prints it once); the orchestrator never prints it.
/// </para>
/// <para>
/// Each tier run has its own <see cref="MeteringServices"/>: its meter, the one <see cref="IMeterGate"/> of the process (the mode), a new <see cref="InMemoryMetricsSink"/> whose readings are
/// also handed to the listener when they are published (so a step line appears when its step finishes), the cost calculator, the instruments and the clock. A failing tier ends the command with
/// the exit code of its exception (a failed step is <see cref="StepFailedException"/>, exit code 5); later tiers do not run, and the files of earlier tiers stay.
/// </para>
/// <para>
/// A failed step (EDGE-006; a <see cref="StepFailedException"/> out of the workflow, nothing else) stops the tier: the orchestrator writes the readings published so far through the tier's writer
/// with status <c>failed</c> (or <c>incomplete</c> when the failing agent's latest reading is <c>incomplete</c>, a connection that dropped after the first update), so steps that finished earlier
/// are the same readings as in the last checkpoint and steps that never ran are absent, never zero-filled; the judge is not called. The write has a token of its own, bounded like the shutdown write.
/// When it succeeded, the listener is told (<see cref="IRunListener.TierFailed"/>) and the exception leaves as a copy that names the file (<see cref="StepFailedException.WithPartialFile"/>); when it
/// failed, the failure is logged at Debug with its type only, told as <see cref="IRunListener.CheckpointFailed"/> with <see cref="CheckpointKind.StepFailure"/>, and the original exception leaves
/// unchanged. The exit code is 5 either way. Engine faults, configuration and precondition failures write no partial file here.
/// </para>
/// <para>
/// Shutdown (Ctrl+C, SIGTERM, <c>ApplicationStopping</c>; EDGE-016): when the run token is cancelled, the calls in flight end as <c>cancelled</c> readings; the orchestrator replaces each
/// <c>cancelled</c> reading of the interrupted (task, repeat) by a copy with the outcome <c>incomplete</c>, writes the document with status <c>incomplete</c> through the tier's writer with a token
/// of its own, and only then tells the listener the incomplete steps; it always rethrows the <see cref="OperationCanceledException"/>, which the command host maps to exit code 5. The write's
/// budget is anchored to the signal on the wall clock: <see cref="ShutdownWriteTimeout"/> at most, and never past <see cref="ShutdownWriteDeadline"/> after the signal, inside the 15 s termination
/// timeout of the CLI, but at least <see cref="MinimumShutdownWrite"/>. The token cancels the asynchronous part of the write only: the synchronous file operations of the writer (the flush, the
/// move onto the run file) are not cancellable and finish once started. A write that fails is logged at Debug and told to the listener (<see cref="IRunListener.CheckpointFailed"/>) with its type
/// only; the last complete checkpoint stays on disk and the exit code stays the shutdown's. A cancellation that lands between tiers starts nothing: the next tier neither opens the GPU nor
/// writes a file. A run that completes never carries <c>incomplete</c>.
/// </para>
/// </remarks>
/// <param name="options">The bound and validated options.</param>
/// <param name="gpus">The GPU port a measured tier opens.</param>
/// <param name="gate">The one meter gate of the process, chosen by <c>--mode</c>.</param>
/// <param name="agentFactory">Builds the agent factory of one tier run around its metering services.</param>
/// <param name="cost">The cost calculator of every step (M2: not configured).</param>
/// <param name="instruments">The OpenTelemetry instruments every reading is recorded on.</param>
/// <param name="time">The clock of the meter, the windows, the latencies and the workflow wall times.</param>
/// <param name="listener">Receives the step lines, the baseline, the judge warning and the closing lines of each tier.</param>
/// <param name="judge">Scores the steps of a tier (the hosted judge; a test registers a double).</param>
/// <param name="localRuntime">The runtime of a measured tier: its warm-up and its description (M2: a no-op; M4: Ollama or NIM).</param>
/// <param name="powerSource">Tells where the machine takes its power from; read once per tier run, before the idle baseline, and written as <c>environment.powerSource</c> of every tier, hosted ones included (the machine still runs the client). A value outside the vocabulary is written as <c>unknown</c>.</param>
/// <param name="live">The live sink of the dashboard (<see cref="NullLiveEventSink"/> without <c>--dashboard</c>): when it also takes readings, the workflow readings of every tier run fan out to it after the listener sink; a measured tier forwards its stored power samples to it at <c>Dashboard:PowerIntervalMs</c>; the judge's services keep the null sink, so a judge reading never reaches it. Each tier run emits one run-started event (a measured tier once its meter started, before the warm-up and the baseline; a hosted tier once its meter started, followed by one N/A power event), the baseline next to the listener's, one progress event per (task, repeat) and one run-completed event after its final document was written or attempted (AJ-091); every emit goes through <see cref="LiveEventSinkExtensions.EmitSafely"/>.</param>
/// <param name="logger">The logger of the Debug records of a failed listener call, a failed shutdown checkpoint and a failed write of the partial file of a failed step (exception types only).</param>
/// <exception cref="ArgumentNullException">An argument is null.</exception>
public sealed class RunOrchestrator(
    IOptions<AgentJoulesOptions> options,
    IGpuTelemetrySourceFactory gpus,
    IMeterGate gate,
    Func<MeteringServices, AgentFactory> agentFactory,
    ICostCalculator cost,
    AgentJoulesInstruments instruments,
    TimeProvider time,
    IRunListener listener,
    IJudgeService judge,
    ILocalRuntime localRuntime,
    IPowerSourceDetector powerSource,
    ILiveEventSink live,
    ILogger<RunOrchestrator> logger) : IRunOrchestrator
{
    /// <summary>
    /// How long the <c>incomplete</c> checkpoint of a shutdown may take, on the wall clock: inside the 15 s process termination timeout of the CLI, with room for the host to stop.
    /// </summary>
    public static readonly TimeSpan ShutdownWriteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The latest end of the shutdown write, counted from the signal on the wall clock: 12 s, leaving 3 s of the 15 s termination timeout for the host to stop.</summary>
    public static readonly TimeSpan ShutdownWriteDeadline = TimeSpan.FromSeconds(12);

    /// <summary>The shortest budget the shutdown write gets, however late it starts.</summary>
    public static readonly TimeSpan MinimumShutdownWrite = TimeSpan.FromSeconds(1);

    // The GPU port's reason names the library and the exception type; both must survive (the same cut as the preflight's).
    private const int MaxReasonLength = 128;

    private readonly IOptions<AgentJoulesOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IGpuTelemetrySourceFactory _gpus = gpus ?? throw new ArgumentNullException(nameof(gpus));
    private readonly IMeterGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly Func<MeteringServices, AgentFactory> _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
    private readonly ICostCalculator _cost = cost ?? throw new ArgumentNullException(nameof(cost));
    private readonly AgentJoulesInstruments _instruments = instruments ?? throw new ArgumentNullException(nameof(instruments));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly IRunListener _listener = listener ?? throw new ArgumentNullException(nameof(listener));
    private readonly IJudgeService _judge = judge ?? throw new ArgumentNullException(nameof(judge));
    private readonly ILocalRuntime _localRuntime = localRuntime ?? throw new ArgumentNullException(nameof(localRuntime));
    private readonly IPowerSourceDetector _powerSource = powerSource ?? throw new ArgumentNullException(nameof(powerSource));
    private readonly ILiveEventSink _live = live ?? throw new ArgumentNullException(nameof(live));
    private readonly ILogger<RunOrchestrator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    /// <exception cref="ConfigurationException">A tier of the plan is not configured, or an agent of it has no model ID (exit code 2).</exception>
    /// <exception cref="MeasurementPreconditionException">The GPU of a measured tier cannot be opened, or <c>Meter:Method=counter</c> meets a GPU without the counter (exit code 6).</exception>
    /// <exception cref="StepFailedException">A provider call of a step failed (exit code 5); the message carries no provider text, and later tiers do not run. The partial file of the tier was written first and is named by <see cref="StepFailedException.PartialFile"/>, unless that write failed (then it is null).</exception>
    /// <exception cref="OperationCanceledException">
    /// The run token was cancelled; the interrupted tier's file was written with status <c>incomplete</c> first (or the failure of that write was told), and no later tier started.
    /// </exception>
    public async Task<int> RunAsync(RunPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var shutdown = new ShutdownSignal();
        using CancellationTokenRegistration registration = cancellationToken.Register(static state => ((ShutdownSignal)state!).Mark(), shutdown);
        foreach (Tier tier in plan.Tiers)
        {
            // A cancellation between tiers starts nothing: no GPU is opened and no file is written for a tier that never ran.
            cancellationToken.ThrowIfCancellationRequested();
            await RunTierAsync(plan, tier, shutdown, cancellationToken).ConfigureAwait(false);
        }

        return (int)ExitCode.Success;
    }

    private async Task RunTierAsync(RunPlan plan, Tier tier, ShutdownSignal shutdown, CancellationToken cancellationToken)
    {
        AgentJoulesOptions options = _options.Value;
        string name = TierOptions.NameOf(tier);
        TierOptions tierOptions = options.Tiers.TryGetValue(name, out TierOptions? configured) && configured is not null
            ? configured
            : throw new ConfigurationException("Tiers:" + name + " is not configured");

        var progress = new TierProgress(plan, tier, tierOptions, options.Cost, plan.Writers[tier]);
        var sink = new InMemoryMetricsSink();
        var judgeSink = new InMemoryMetricsSink();
        var listening = new RunListenerSink(sink, _listener, _logger, cancellationToken);
        IGpuTelemetrySource? source = null;
        IEnergyMeter? meter = null;
        try
        {
            // Once per tier run, before the baseline (REQ-021): a laptop on its battery draws another GPU power than on mains. The detector never throws; a value outside the vocabulary is not written.
            progress.PowerSource = PowerSources.Normalize(_powerSource.Detect());
            if (TierKinds.IsMeasured(tier))
            {
                GpuOpenResult opened = _gpus.Open(options.Gpu.Index);
                source = opened.Source
                    ?? throw new MeasurementPreconditionException($"tier {name} measures the GPU, but the GPU cannot be opened: {TextEcho.ForMessage(opened.Reason ?? "no reason given", MaxReasonLength)}");
                MeterCapabilities capabilities = MeterMethodSelector.Select(options.Meter.Method, source, options.Meter.SampleRateHz);
                progress.Gpu = source.Identity;
                progress.Probe = ProbeResult.Create(source.Identity, source.EnergyCounterSupported, capabilities, options.Meter.SampleRateHz);

                meter = new SampledEnergyMeter(source, capabilities, _time, _live is NullLiveEventSink ? null : new PowerForwarding(_live, options.Dashboard.PowerIntervalMs));

                // The union of the step windows for the energy totals of a concurrent run; the meter is alive for every write of the tier (it is disposed in the finally below).
                progress.Union = meter.ComputeUnion;
                await meter.StartAsync(cancellationToken).ConfigureAwait(false);

                // The live view's run starts once the GPU is known and before the warm-up and the baseline (AJ-091, P-72).
                // The local tier alone is served by the local runtime (a cloudgpu run sends it nothing and keeps Tiers:cloudgpu:Precision). A runtime that names itself is known by name from here on: a run
                // interrupted before its description is written states the runtime, a precision and an offload of null and no flag, never the configured precision of another runtime.
                if (tier == Tier.Local && !string.Equals(_localRuntime.Name, NoOpLocalRuntime.NoneName, StringComparison.Ordinal))
                {
                    progress.Runtime = new LocalRuntimeInfo(_localRuntime.Name, null, null, null);
                }

                _live.EmitSafely(progress.Started(_time.GetUtcNow()));
                if (tier == Tier.Local)
                {
                    // The warm-up, like the description, belongs to the local tier: a cloudgpu run sends nothing to the local runtime and its modelLoadSeconds stays null until M5.
                    progress.ModelLoad = await WarmUpAsync(tierOptions, cancellationToken).ConfigureAwait(false);
                    progress.Runtime = await _localRuntime.DescribeAsync(cancellationToken).ConfigureAwait(false);
                }

                // A description that cannot come from a measurement (an offload that is not a number or lies outside 0 to 100) stops the run before the baseline, not at the first write.
                _ = RunAssembler.RunFlagsOf(progress.Runtime);
                progress.Baseline = await meter
                    .MeasureBaselineAsync(TimeSpan.FromSeconds(plan.BaselineSeconds), options.Baseline.MaxCoefficientOfVariation, cancellationToken)
                    .ConfigureAwait(false);
                BaselineResult measured = progress.Baseline;
                listening.Tell(listener => listener.BaselineMeasured(measured));
                _live.EmitSafely(new BaselineEvent(_time.GetUtcNow(), false, measured));
            }
            else
            {
                meter = new UnmeteredEnergyMeter(_time);
                await meter.StartAsync(cancellationToken).ConfigureAwait(false);

                // A hosted tier has no power to show: one N/A power event right after its run started (P-56).
                _live.EmitSafely(progress.Started(_time.GetUtcNow()));
                _live.EmitSafely(new PowerEvent(_time.GetUtcNow(), false, Metric.NotApplicable(MeterCapabilities.HostedTierReason), 0.0));
            }

            // With a dashboard that also takes readings, the workflow readings fan out to the listener sink (which keeps them for the file) first and to the dashboard second.
            IMetricsSink workflowSink = _live is IMetricsSink liveReadings ? new FanOutMetricsSink(_logger, listening, liveReadings) : listening;
            var services = new MeteringServices(meter, _gate, workflowSink, _cost, _instruments, _time) { Live = _live };
            AgentFactory factory = _agentFactory(services);
            for (int taskIndex = 0; taskIndex < plan.Tasks.Count; taskIndex++)
            {
                TaskDefinition task = plan.Tasks[taskIndex];
                for (int repeat = 1; repeat <= plan.Repeat; repeat++)
                {
                    // The live view's progress at the start of each (task, repeat), 1-based.
                    _live.EmitSafely(new ProgressEvent(_time.GetUtcNow(), false, task.Name, taskIndex + 1, plan.Tasks.Count, repeat, plan.Repeat));
                    try
                    {
                        await RunRepeatAsync(factory, progress, task, repeat, options.Workflow.Seed, cancellationToken).ConfigureAwait(false);
                    }
                    catch (StepFailedException failure)
                    {
                        // A failed step stops the tier (EDGE-006): the partial file is written first. The copy that names the file leaves; when the file could not be written the original
                        // leaves unchanged, because a claim about a partial file would be false.
                        StepFailedException? named = await WriteFailedAsync(plan, progress, sink, judgeSink, listening, shutdown, failure).ConfigureAwait(false);
                        if (named is null)
                        {
                            throw;
                        }

                        throw named;
                    }

                    await progress.Writer.WriteAsync(progress.Document(sink.Snapshot(), judgeSink.Snapshot(), RunStatus.Running), cancellationToken).ConfigureAwait(false);
                    progress.CheckpointWritten = true;
                }
            }

            // The judge scores the steps on its own sink: its services are the tier run's with the sink replaced and the null live sink, so a judge reading never reaches the workflow sink, the listener (no step line) or the dashboard.
            IReadOnlyList<StepReading> workflow = sink.Snapshot();
            JudgeResult? judged = await JudgeAsync(plan, tier, workflow, services with { Sink = judgeSink, Live = NullLiveEventSink.Instance }, cancellationToken).ConfigureAwait(false);
            RunDocument completed = progress.Document(judged?.Steps ?? workflow, judgeSink.Snapshot(), RunStatus.Completed, judged?.SelfJudged);
            try
            {
                await progress.Writer.WriteAsync(completed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The final document was attempted (P-72): the live view ends with it as the failed and incomplete paths do. A shutdown during the write goes to the incomplete write,
                // which emits its own run-completed event, so one tier run never emits two.
                EmitCompleted(completed);
                throw;
            }

            string displayPath = RunAssembler.DisplayPath(plan.OutputDisplay, progress.Writer.FilePath);

            // A judge failure is a warning and nothing else (EDGE-012): one line per tier run in which a judged step failed, told after the file was written and before the closing lines of the tier.
            // The counts and the kinds travel in the result; no message does. A skipped judge (no result) and a judge that scored every step tell nothing.
            if (judged is { FailedSteps: > 0 } failures)
            {
                listening.Tell(listener => listener.JudgeFailed(tier, failures.FailedSteps, failures.JudgedSteps, failures.FailureKinds, failures.FailureDetails));
            }

            listening.Tell(listener => listener.TierCompleted(displayPath, completed.Totals));
            _live.EmitSafely(new RunCompletedEvent(_time.GetUtcNow(), false, completed.Run, completed.Totals));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The checkpoint, or the warning that it failed, and then always the shutdown itself (exit code 5).
            await WriteIncompleteAsync(progress, sink, judgeSink, listening, shutdown).ConfigureAwait(false);
            throw;
        }
        finally
        {
            // The meter does not own the source: its sampling loop must be gone before the GPU session ends, and a meter that fails to stop must not keep the session open.
            try
            {
                if (meter is not null)
                {
                    await meter.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                source?.Dispose();
            }
        }
    }

    // One (task, repeat): four new agents with new pipelines, a new workflow graph, and its wall time on the clock of the run, counted even when the run is interrupted.
    private async Task RunRepeatAsync(AgentFactory factory, TierProgress progress, TaskDefinition task, int repeat, int seed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Current = (task.Name, repeat);
        long started = _time.GetTimestamp();
        try
        {
            IReadOnlyList<AIAgent> agents = factory.CreateAll(progress.Tier, progress.TierOptions, task, repeat, seed);
            _ = await new AgentJoulesWorkflowFactory(agents, task).RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            progress.WallClockMs = (progress.WallClockMs ?? 0.0) + _time.GetElapsedTime(started).TotalMilliseconds;
        }

        progress.Current = null;
    }

    // The warm-up of every distinct model the agents of the tier call (TierOptions.ResolvedModelIds, the set the description reads back); the load time is the sum of the times the runtime observed, null when it observed none.
    private async Task<TimeSpan?> WarmUpAsync(TierOptions tierOptions, CancellationToken cancellationToken)
    {
        TimeSpan? total = null;
        foreach (string modelId in tierOptions.ResolvedModelIds())
        {
            if (await _localRuntime.WarmUpAsync(modelId, cancellationToken).ConfigureAwait(false) is { } load)
            {
                total = (total ?? TimeSpan.Zero) + load;
            }
        }

        return total;
    }

    // The judge scores the steps of the tier and its steps are put in their places; it is skipped when the judge key is missing (the command warned). The count, the windows and the quality range of what it returns are checked.
    private async Task<JudgeResult?> JudgeAsync(RunPlan plan, Tier tier, IReadOnlyList<StepReading> steps, MeteringServices judgeServices, CancellationToken cancellationToken)
    {
        if (plan.Keys.MissingJudgeVariable is not null || steps.Count == 0)
        {
            return null;
        }

        JudgeResult scored = await _judge.ScoreAsync(new JudgeRequest(tier, steps, plan.Tasks, judgeServices), cancellationToken).ConfigureAwait(false);
        if (scored is null || scored.Steps is null || scored.Steps.Count != steps.Count)
        {
            throw new InvalidOperationException("The judge must return one step for every step it was given, in the same order.");
        }

        for (int i = 0; i < steps.Count; i++)
        {
            if (scored.Steps[i] is null)
            {
                throw new InvalidOperationException("The judge must return one step for every step it was given, in the same order.");
            }

            // The in-memory windows carry the union totals and the shared-window flags of the completed write (AJ-047): a judge that rebuilt a reading without its window
            // would turn a concurrent run's energy into union-not-computed. The message names the rule, never a reading.
            if (scored.Steps[i].Window != steps[i].Window)
            {
                throw new InvalidOperationException("The judge must return each step with its window unchanged.");
            }

            // A faulty judge must not surface as an exception from Assemble after every step has run: a model-judged quality is a whole number from 1 to 5, as the parser produces it.
            if (scored.Steps[i].Quality is { Label: MetricLabel.ModelJudged, Value: { } score } && !(score is >= 1.0 and <= 5.0 && Math.Floor(score) == score))
            {
                throw new InvalidOperationException("The judge must return a quality that is a whole number from 1 to 5, N/A or not-reported.");
            }
        }

        return scored;
    }

    // The partial file of a step failure (EDGE-006): the document of the readings published so far, which are the same instances the earlier checkpoints held, with the status failed, or incomplete
    // when the failing agent's latest reading is incomplete (a drop after the first update, EDGE-016). No judge call: a stopped tier is not scored. The write has a token of its own, bounded like
    // the shutdown write, and a failed write is logged and told with its type only. The token cancels the asynchronous part of the write only: the synchronous file operations of the writer (delete,
    // create, flush, move) are not cancellable and finish once started, so the budget bounds the await, not the disk. Returns the failure carrying the display path of the file, or null when the file could not be written.
    private async Task<StepFailedException?> WriteFailedAsync(RunPlan plan, TierProgress progress, InMemoryMetricsSink sink, InMemoryMetricsSink judgeSink, RunListenerSink listening, ShutdownSignal shutdown, StepFailedException failure)
    {
        IReadOnlyList<StepReading> steps = sink.Snapshot();
        StepReading? latest = steps.LastOrDefault(step => string.Equals(step.Agent, failure.Agent, StringComparison.Ordinal));
        RunStatus status = latest?.Outcome == StepOutcomes.Incomplete ? RunStatus.Incomplete : RunStatus.Failed;
        RunDocument document = progress.Document(steps, judgeSink.Snapshot(), status);
        try
        {
            // The wall clock, like the shutdown write: a fake clock in a test must not cancel the write.
            using var timeout = new CancellationTokenSource(shutdown.WriteBudget());
            await progress.Writer.WriteAsync(document, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception writeFailure)
        {
            string type = writeFailure.GetType().Name;
            RunLog.StepFailureCheckpointFailed(_logger, type);
            listening.Tell(listener => listener.CheckpointFailed(type, progress.CheckpointWritten, CheckpointKind.StepFailure));
            EmitCompleted(document);
            return null;
        }

        EmitCompleted(document);
        string displayPath = RunAssembler.DisplayPath(plan.OutputDisplay, progress.Writer.FilePath);
        listening.Tell(listener => listener.TierFailed(displayPath, failure.Agent, status));
        return failure.WithPartialFile(displayPath);
    }

    // The shutdown checkpoint: the in-flight steps of the interrupted (task, repeat) become incomplete, the document is written with a token of its own, and then the listener is told.
    // A failed write is logged and told with its type only and is never thrown: the caller rethrows the run's OperationCanceledException whatever happens here.
    private async Task WriteIncompleteAsync(TierProgress progress, InMemoryMetricsSink sink, InMemoryMetricsSink judgeSink, RunListenerSink listening, ShutdownSignal shutdown)
    {
        IReadOnlyList<StepReading> snapshot = sink.Snapshot();
        var steps = new List<StepReading>(snapshot.Count);
        var told = new List<StepReading>();
        foreach (StepReading reading in snapshot)
        {
            if (reading.Outcome == StepOutcomes.Cancelled
                && progress.Current is { } current
                && string.Equals(reading.Task, current.Task, StringComparison.Ordinal)
                && reading.Repeat == current.Repeat)
            {
                StepReading incomplete = RunAssembler.WithOutcome(reading, StepOutcomes.Incomplete);
                steps.Add(incomplete);
                told.Add(incomplete);
            }
            else
            {
                steps.Add(reading);
            }
        }

        RunDocument document = progress.Document(steps, judgeSink.Snapshot(), RunStatus.Incomplete);
        try
        {
            // The wall clock of the termination timeout, not the run's clock: a fake clock in a test must not cancel the write. The token reaches the asynchronous part of the write
            // only; the writer's synchronous file operations finish once started.
            using var timeout = new CancellationTokenSource(shutdown.WriteBudget());
            await progress.Writer.WriteAsync(document, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            string type = failure.GetType().Name;
            RunLog.ShutdownCheckpointFailed(_logger, type);
            listening.Tell(listener => listener.CheckpointFailed(type, progress.CheckpointWritten, CheckpointKind.Shutdown));
            EmitCompleted(document);
            return;
        }

        foreach (StepReading incomplete in told)
        {
            listening.Tell(listener => listener.StepFinished(incomplete));
        }

        EmitCompleted(document);
    }

    // The live view's end of a tier run (AJ-091, P-72): once per tier end that wrote or attempted its final document, with that document's run and totals.
    private void EmitCompleted(RunDocument document) => _live.EmitSafely(new RunCompletedEvent(_time.GetUtcNow(), false, document.Run, document.Totals));

    // The instant the run token was cancelled, on the wall clock (the termination timeout of the process is wall time), and the budget of the shutdown write that follows from it.
    private sealed class ShutdownSignal
    {
        private long _timestamp;

        public void Mark() => Interlocked.CompareExchange(ref _timestamp, TimeProvider.System.GetTimestamp(), 0);

        // min(ShutdownWriteTimeout, ShutdownWriteDeadline - time since the signal), never below MinimumShutdownWrite; ShutdownWriteTimeout when no signal was seen.
        public TimeSpan WriteBudget()
        {
            long signalled = Interlocked.Read(ref _timestamp);
            if (signalled == 0)
            {
                return ShutdownWriteTimeout;
            }

            TimeSpan left = ShutdownWriteDeadline - TimeProvider.System.GetElapsedTime(signalled);
            TimeSpan budget = left < ShutdownWriteTimeout ? left : ShutdownWriteTimeout;
            return budget > MinimumShutdownWrite ? budget : MinimumShutdownWrite;
        }
    }

    // What one tier run has collected so far, and the document of any of its writes.
    private sealed class TierProgress(RunPlan plan, Tier tier, TierOptions tierOptions, CostOptions costOptions, IRunCheckpointWriter writer)
    {
        private readonly string[] _taskNames = [.. plan.Tasks.Select(static task => task.Name)];

        public Tier Tier { get; } = tier;

        public TierOptions TierOptions { get; } = tierOptions;

        public CostOptions CostOptions { get; } = costOptions;

        public IRunCheckpointWriter Writer { get; } = writer;

        public GpuIdentity? Gpu { get; set; }

        public ProbeResult? Probe { get; set; }

        public BaselineResult? Baseline { get; set; }

        public LocalRuntimeInfo? Runtime { get; set; }

        public TimeSpan? ModelLoad { get; set; }

        // The power source detected at the start of the tier run, or null before it was read.
        public string? PowerSource { get; set; }

        // Null until the first (task, repeat) started: a run that never started one has no wall clock (not-reported no-steps), never a measured 0.
        public double? WallClockMs { get; set; }

        // True once a running checkpoint of this tier run is on disk: a failed shutdown write then leaves that checkpoint, otherwise no file of the tier.
        public bool CheckpointWritten { get; set; }

        // The (task, repeat) whose workflow is running, or null between them.
        public (string Task, int Repeat)? Current { get; set; }

        // The meter's union of the step windows on a measured tier (AJ-047), or null for a hosted tier, whose energy totals are N/A anyway.
        public Func<IReadOnlyList<WindowSpan>, UnionEnergy>? Union { get; set; }

        // selfJudged is the judge's statement and is given to the completed write only: a checkpoint, a skipped judge, a failed or an incomplete file state none.
        public RunDocument Document(IReadOnlyList<StepReading> steps, IReadOnlyList<StepReading> judge, RunStatus status, bool? selfJudged = null)
        {
            RunInfo run = RunInfoOf(status, selfJudged);
            EnvironmentInfo environment = RunAssembler.BuildEnvironment(Tier, TierOptions, Gpu, Runtime, ModelLoad, CostOptions, PowerSource);
            return RunAssembler.Assemble(run, environment, Probe, Baseline, steps, judge, WallClockMs, Union, CostOptions);
        }

        // The live view's start of this tier run: the run as running and the environment known so far (the GPU of a measured tier, not yet its runtime or load time), with the power limit of the probe.
        public RunStartedEvent Started(DateTimeOffset atUtc) =>
            new(atUtc, false, RunInfoOf(RunStatus.Running, selfJudged: null), RunAssembler.BuildEnvironment(Tier, TierOptions, Gpu, Runtime, ModelLoad, CostOptions, PowerSource), Probe?.PowerLimitW);

        // The run flags of every write: partial-offload from the runtime's description, non-publishable when a measured tier's idle baseline is shorter than 30 s (a hosted tier has no baseline).
        private RunInfo RunInfoOf(RunStatus status, bool? selfJudged) =>
            RunAssembler.BuildRunInfo(
                Path.GetFileNameWithoutExtension(Writer.FilePath),
                Tier,
                _taskNames,
                plan.Mode,
                plan.Repeat,
                plan.StartUtc,
                status,
                selfJudged,
                RunAssembler.RunFlagsOf(Runtime, TierKinds.IsMeasured(Tier) ? plan.BaselineSeconds : null));
    }
}