using System.Globalization;
using System.Text.Json;

using AgentJoules.Metering;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>
/// The rig of the AJ-122 scenarios and tests (EDGE-017): one <c>run --tier cloudgpu</c> of the production orchestrator on the fake clock of <see cref="LocalRunRig"/>, with a fake cloudgpu endpoint that records its completions, the budget
/// keys of <c>CloudGpu</c> as configuration pairs (the way a builder sets <c>AGENTJOULES__CloudGpu__ObservedCostUsd</c> before an invocation), and a hook that moves the clock after a chosen number of completions. Nothing leaves the process.
/// </summary>
public sealed class CloudGpuBudgetRig : IDisposable
{
    private readonly LocalRunRig _rig;
    private int _completions;

    private CloudGpuBudgetRig(LocalRunRig rig, FakeHttpMessageHandler cloud)
    {
        _rig = rig;
        Cloud = cloud;
    }

    /// <summary>The fake that stands where the cloudgpu tier stands.</summary>
    public FakeHttpMessageHandler Cloud { get; }

    /// <summary>The fake clock of the run.</summary>
    public TimerTrackingTimeProvider Time => _rig.Time;

    /// <summary>The instant the tier started in the rig's terms: the first instant of the fake clock.</summary>
    public static DateTimeOffset ClockStart => RunOrchestrationFixtures.CommandStartUtc;

    /// <summary>How many chat completions the cloudgpu fake answered.</summary>
    public int Completions => Cloud.Count(FakeRoute.ChatCompletions);

    /// <summary>The operations of the run in order (probe, meter start, warm-up, baseline, task calls); empty before anything ran.</summary>
    public OperationRecorder Recorder => _rig.Recorder;

    /// <summary>Enables the cloudgpu tier, so that <c>--tier all</c> runs <c>local</c>, <c>cloudgpu</c>, <c>nvidia</c> and <c>nebius</c> in that order, and gives the nebius tier a fake endpoint.</summary>
    public void EnableAllTiers()
    {
        _rig.Setup.Settings["AgentJoules:Tiers:cloudgpu:Enabled"] = "true";
        _rig.Setup.Endpoints["nebius"] = RunOrchestrationFixtures.Endpoint(Time, TimeSpan.FromSeconds(1));
    }

    /// <summary>Gives the nebius tier a fake endpoint of its own, so that <c>run --tier nebius</c> runs against it (AJ-123: the stop of the gate is a cloudgpu behaviour only).</summary>
    /// <returns>The fake of the nebius tier.</returns>
    public FakeHttpMessageHandler UseNebius()
    {
        FakeHttpMessageHandler nebius = RunOrchestrationFixtures.Endpoint(Time, TimeSpan.FromSeconds(1));
        _rig.Setup.Endpoints["nebius"] = nebius;
        return nebius;
    }

    /// <summary>Wraps the console listener of the command in a decorator, as <see cref="RunListenerFixtures.Decorate"/> does.</summary>
    /// <param name="wrap">Builds the decorator around the console listener.</param>
    public void DecorateListener(Func<IRunListener, IRunListener> wrap) => RunListenerFixtures.Decorate(_rig.Setup, wrap);

    /// <summary>The result of the last run, or null before it ran.</summary>
    public CliRunResult? Result => _rig.Result;

    /// <summary>Creates the rig; the caller disposes it.</summary>
    /// <param name="responseDelay">Real time every answer of the fake is held back (the handler's <see cref="FakeHttpMessageHandler.ResponseDelay"/>, not the fake clock): it lets the calls of the workflow overlap in time as on a network; zero answers at once.</param>
    /// <returns>The rig.</returns>
    public static CloudGpuBudgetRig Create(TimeSpan responseDelay = default)
    {
        LocalRunRig rig = LocalRunRig.Nim();
        var cloud = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), Clock = rig.Time, DefaultDuration = TimeSpan.FromSeconds(1), ResponseDelay = responseDelay, WarmUpApart = true };
        rig.Setup.Endpoints["cloudgpu"] = cloud;

        // The cloudgpu tier asks for SSH variables unless the run happens on the VM itself; the run here is a fake and has no host.
        rig.Setup.Settings["AgentJoules:CloudGpu:RequireSshVariables"] = "false";

        // A real cloudgpu run needs the handover time (peer review m-1): the default is the start of the fake clock, so no time box fires unless a test moves the clock or sets another start.
        rig.Setup.Settings["AgentJoules:CloudGpu:TierStartUtc"] = ClockStart.ToString("O", CultureInfo.InvariantCulture);
        return new CloudGpuBudgetRig(rig, cloud);
    }

    /// <summary>Sets one key of the <c>CloudGpu</c> section.</summary>
    /// <param name="key">The key without its section, for example <c>ObservedCostUsd</c>.</param>
    /// <param name="value">The value as text.</param>
    public void Set(string key, string value) => _rig.Setup.Settings["AgentJoules:CloudGpu:" + key] = value;

    /// <summary>Sets <c>CloudGpu:ObservedCostUsd</c>.</summary>
    /// <param name="usd">The cost the billing page shows.</param>
    public void SetObservedCost(double usd) => Set("ObservedCostUsd", usd.ToString("0.00", CultureInfo.InvariantCulture));

    /// <summary>Sets <c>CloudGpu:TierStartUtc</c>.</summary>
    /// <param name="start">The instant the tier started.</param>
    public void SetTierStart(DateTimeOffset start) => Set("TierStartUtc", start.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>Removes <c>CloudGpu:TierStartUtc</c>, as a builder who forgot the handover time would run.</summary>
    public void ClearTierStart() => _ = _rig.Setup.Settings.Remove("AgentJoules:CloudGpu:TierStartUtc");

    /// <summary>Moves the fake clock by <paramref name="by"/> as the answer to the <paramref name="completion"/>-th chat completion is returned.</summary>
    /// <param name="completion">The 1-based number of the completion.</param>
    /// <param name="by">How far the clock moves.</param>
    public void AdvanceAtCompletion(int completion, TimeSpan by) =>
        Cloud.CustomizeResponse = (route, _) =>
        {
            if (route == FakeRoute.ChatCompletions && Interlocked.Increment(ref _completions) == completion)
            {
                Time.Advance(by);
            }
        };

    /// <summary>
    /// Makes the gate of the run a <see cref="QueueObservingMeterGate"/> and holds every cut stream of the cloudgpu fake open until a caller waits for the gate behind the dropping one, so the sibling really queues before the
    /// connection drops (AJ-123). Call it before <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="releaseLag">Real time the releaser of the gate stays busy after it gave the slot on; see <see cref="QueueObservingMeterGate"/>.</param>
    /// <param name="endpoint">The fake whose cut streams are held; the cloudgpu fake when null.</param>
    /// <returns>The gate, which the rig's host disposes.</returns>
    public QueueObservingMeterGate HoldDropUntilSiblingQueues(TimeSpan releaseLag = default, FakeHttpMessageHandler? endpoint = null)
    {
        var gate = new QueueObservingMeterGate(releaseLag);
        RunOrchestrationFixtures.AddServices(_rig.Setup, services =>
        {
            _ = services.RemoveAll<IMeterGate>();
            _ = services.AddSingleton<IMeterGate>(_ => gate);
        });
        (endpoint ?? Cloud).HoldDropUntil = gate.WaiterQueued;
        return gate;
    }

    /// <summary>Registers <paramref name="judge"/> in place of the no-op judge of the rig.</summary>
    /// <param name="judge">The judge.</param>
    public void UseJudge(IJudgeService judge) =>
        RunOrchestrationFixtures.AddServices(_rig.Setup, services =>
        {
            _ = services.RemoveAll<IJudgeService>();
            _ = services.AddSingleton(judge);
        });

    /// <summary>Runs <paramref name="arguments"/> on the fake clock.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>The result.</returns>
    public Task<CliRunResult> RunAsync(string arguments) => _rig.RunAsync(arguments);

    /// <summary>Reads the one run file the command wrote; the caller disposes the document.</summary>
    /// <returns>The document.</returns>
    public JsonDocument ReadRunFile() => _rig.ReadRunFile();

    /// <summary>The run files the command wrote.</summary>
    /// <returns>The full paths; empty when none.</returns>
    public string[] RunFiles() => _rig.RunFiles();

    /// <summary>Runs the report command on the results folder of the run and returns the text of <c>summary.md</c> (AJ-123).</summary>
    /// <returns>The Markdown summary.</returns>
    public Task<string> SummaryAsync() => _rig.SummaryAsync();

    /// <summary>The completions one (task, repeat) of the committed task takes on the cloudgpu fake, found by running one repeat on a rig of its own.</summary>
    /// <param name="task">The task name.</param>
    /// <returns>The number of completions of one repeat.</returns>
    public static async Task<int> CompletionsPerRepeatAsync(string task)
    {
        using CloudGpuBudgetRig calibration = Create();
        CliRunResult result = await calibration.RunAsync("run --tier cloudgpu --repeat 1 --task " + task);
        Assert.True(result.ExitCode == 0, $"The calibration run exited {result.ExitCode}; stderr: {result.Stderr}");
        return calibration.Completions;
    }

    /// <inheritdoc />
    public void Dispose() => _rig.Dispose();
}