using System.Globalization;
using System.Text.Json;

using AgentJoules.Workflow.Runs;

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

    /// <summary>Wraps the console listener of the command in a decorator, as <see cref="RunListenerFixtures.Decorate"/> does.</summary>
    /// <param name="wrap">Builds the decorator around the console listener.</param>
    public void DecorateListener(Func<IRunListener, IRunListener> wrap) => RunListenerFixtures.Decorate(_rig.Setup, wrap);

    /// <summary>The result of the last run, or null before it ran.</summary>
    public CliRunResult? Result => _rig.Result;

    /// <summary>Creates the rig; the caller disposes it.</summary>
    /// <returns>The rig.</returns>
    public static CloudGpuBudgetRig Create()
    {
        LocalRunRig rig = LocalRunRig.Nim();
        var cloud = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), Clock = rig.Time, DefaultDuration = TimeSpan.FromSeconds(1) };
        rig.Setup.Endpoints["cloudgpu"] = cloud;

        // The cloudgpu tier asks for SSH variables unless the run happens on the VM itself; the run here is a fake and has no host.
        rig.Setup.Settings["AgentJoules:CloudGpu:RequireSshVariables"] = "false";
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