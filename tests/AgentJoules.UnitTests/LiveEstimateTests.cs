using System.Reflection;
using System.Text.Json;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Dashboard;
using AgentJoules.TestSupport;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit;

using static AgentJoules.UnitTests.DashboardEventSerializationTests;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-090 (REQ-032 R3 and R5): the running estimates of the live dashboard. During a step <see cref="DashboardSnapshot"/> integrates the power samples the meter already took, net of the idle
/// baseline mean, with the trapezoid rule over the sample timestamps, and carries the result as an <c>estimated</c> label object with the running output-chunk count; the measured step values
/// replace both on completion. The fixtures here are fixtures, not measurements. An estimate lives only in the dashboard project and never enters a results file.
/// </summary>
[Trait("Spec", "REQ-032")]
public sealed class LiveEstimateTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    // ---- criterion 11: the trapezoid ----

    [Fact]
    public void Running_energy_is_trapezoid_estimate()
    {
        // 60 W at 0.0, 0.1 and 0.2 s with a 20 W baseline mean: the trapezoid of 40 W over 0.2 s, 8 J.
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new BaselineEvent(Origin, false, Baseline(20.0)));
        _ = snapshot.Apply(Started("Writer", 0.0));
        _ = snapshot.Apply(Power(60.0, 0.0));
        _ = snapshot.Apply(Power(60.0, 0.1));
        string payload = snapshot.Apply(Power(60.0, 0.2));

        AssertMetric(AgentOf(payload, "Writer").GetProperty("runningNetJoules"), 8.0, "estimated", unit: "J");
    }

    [Fact]
    public void The_first_power_sample_after_step_start_is_held_to_the_step_start()
    {
        // The step starts at 1.00 s and the first sample is at 1.05 s: the estimate covers 0.15 s of net 40 W, 6 J.
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new BaselineEvent(Origin, false, Baseline(20.0)));
        _ = snapshot.Apply(Started("Writer", 1.00));
        string afterFirst = snapshot.Apply(Power(60.0, 1.05));
        string payload = snapshot.Apply(Power(60.0, 1.15));

        AssertMetric(AgentOf(afterFirst, "Writer").GetProperty("runningNetJoules"), 2.0, "estimated", unit: "J");
        AssertMetric(AgentOf(payload, "Writer").GetProperty("runningNetJoules"), 6.0, "estimated", unit: "J");
    }

    [Fact]
    public void The_trapezoid_averages_two_different_samples_and_does_not_clamp_below_the_baseline()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new BaselineEvent(Origin, false, Baseline(20.0)));
        _ = snapshot.Apply(Started("Writer", 0.0));
        _ = snapshot.Apply(Power(10.0, 0.0));

        // 10 W then 30 W over 1 s: (10 + 30) / 2 - 20 = 0 net; then 10 W over the next second: (30 + 10) / 2 - 20 = 0; the held first point is (0, 10).
        _ = snapshot.Apply(Power(30.0, 1.0));
        string payload = snapshot.Apply(Power(10.0, 2.0));
        AssertMetric(AgentOf(payload, "Writer").GetProperty("runningNetJoules"), 0.0, "estimated", unit: "J");

        // Below the baseline the estimate goes negative: 10 W against a 20 W baseline for one more second is -10 J.
        string below = snapshot.Apply(Power(10.0, 3.0));
        AssertMetric(AgentOf(below, "Writer").GetProperty("runningNetJoules"), -10.0, "estimated", unit: "J");
    }

    [Fact]
    public void Samples_before_the_step_started_and_after_it_completed_are_ignored()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new BaselineEvent(Origin, false, Baseline(20.0)));
        _ = snapshot.Apply(Power(90.0, 0.4));
        _ = snapshot.Apply(Started("Writer", 1.0));

        // A sample older than the step start (a power event of the previous step still in flight) adds nothing.
        string payload = snapshot.Apply(Power(90.0, 0.9));
        AssertMetric(AgentOf(payload, "Writer").GetProperty("runningNetJoules"), null, "not-reported", reason: "no-samples");

        _ = snapshot.Apply(Power(60.0, 1.0));
        string running = snapshot.Apply(Power(60.0, 2.0));
        AssertMetric(AgentOf(running, "Writer").GetProperty("runningNetJoules"), 40.0, "estimated", unit: "J");

        _ = snapshot.Apply(new StepCompletedEvent(At(2), false, Reading(agent: "Writer")));
        string after = snapshot.Apply(Power(90.0, 3.0));
        Assert.Equal(JsonValueKind.Null, AgentOf(after, "Writer").GetProperty("runningNetJoules").ValueKind);
    }

    [Fact]
    public void Running_energy_without_a_measured_baseline_is_not_reported_no_baseline()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(Started("Writer", 0.0));
        _ = snapshot.Apply(Power(60.0, 0.0));

        string payload = snapshot.Apply(Power(60.0, 0.1));

        AssertMetric(AgentOf(payload, "Writer").GetProperty("runningNetJoules"), null, "not-reported", reason: "no-baseline");
    }

    [Fact]
    public void Running_energy_before_the_first_sample_is_not_reported_no_samples()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new BaselineEvent(Origin, false, Baseline(20.0)));

        string payload = snapshot.Apply(Started("Writer", 0.0));

        AssertMetric(AgentOf(payload, "Writer").GetProperty("runningNetJoules"), null, "not-reported", reason: "no-samples");
        Assert.Equal("running", AgentOf(payload, "Writer").GetProperty("status").GetString());
        Assert.Equal(["Writer"], Parse(payload).GetProperty("data").GetProperty("activeAgents").EnumerateArray().Select(static agent => agent.GetString()));
    }

    [Fact]
    public void Running_output_chunks_are_an_estimated_count_on_the_step_progress_payload()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(Started("Writer", 0.0));
        JsonElement beforeProgress = AgentOf(snapshot.Apply(Power(60.0, 0.1)), "Writer");
        AssertMetric(beforeProgress.GetProperty("runningOutputChunks"), null, "not-reported", reason: "no-progress");

        string payload = snapshot.Apply(new StepProgressEvent(Origin, false, Identity("Writer"), 7));

        Assert.Equal("step-progress", Parse(payload).GetProperty("type").GetString());
        AssertMetric(AgentOf(payload, "Writer").GetProperty("runningOutputChunks"), 7.0, "estimated", unit: "chunks");
        string later = snapshot.Apply(new StepProgressEvent(Origin, false, Identity("Writer"), 12));
        AssertMetric(AgentOf(later, "Writer").GetProperty("runningOutputChunks"), 12.0, "estimated", unit: "chunks");
    }

    // ---- criterion 11: replaced by the measured values ----

    [Fact]
    public void Estimated_replaced_by_measured_on_step_completed()
    {
        // 79 W against a 20 W baseline for 2 s: 118 J estimated, with 9 chunks.
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new BaselineEvent(Origin, false, Baseline(20.0)));
        _ = snapshot.Apply(Started("Writer", 0.0));
        _ = snapshot.Apply(Power(79.0, 0.0));
        _ = snapshot.Apply(Power(79.0, 1.0));
        _ = snapshot.Apply(Power(79.0, 2.0));
        string running = snapshot.Apply(new StepProgressEvent(Origin, false, Identity("Writer"), 9));
        JsonElement estimate = AgentOf(running, "Writer");
        AssertMetric(estimate.GetProperty("runningNetJoules"), 118.0, "estimated", unit: "J");
        AssertMetric(estimate.GetProperty("runningOutputChunks"), 9.0, "estimated", unit: "chunks");

        string done = snapshot.Apply(new StepCompletedEvent(At(2), false, Reading(agent: "Writer", netJoules: Metric.Measured(120.0, "J"))));

        JsonElement data = Parse(done).GetProperty("data");
        AssertMetric(data.GetProperty("step").GetProperty("netJoules"), 120.0, "measured", unit: "J");
        JsonElement agent = AgentOf(done, "Writer");
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("runningNetJoules").ValueKind);
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("runningOutputChunks").ValueKind);
        AssertMetric(agent.GetProperty("cumulativeNetJoules"), 120.0, "measured", unit: "J");
        Assert.Empty(data.GetProperty("activeAgents").EnumerateArray());

        // Nothing estimated is left anywhere in the snapshot of this agent either.
        JsonElement snapshotAgent = AgentOf(snapshot.ToSnapshotPayload(At(3)), "Writer");
        Assert.Equal(JsonValueKind.Null, snapshotAgent.GetProperty("runningNetJoules").ValueKind);
        Assert.DoesNotContain("\"estimated\"", snapshotAgent.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Hosted_running_energy_is_NA()
    {
        var snapshot = DashboardFixtures.NewSnapshot();

        string payload = snapshot.Apply(Started("Writer", 0.0, Tier.Nvidia));

        JsonElement running = AgentOf(payload, "Writer").GetProperty("runningNetJoules");
        AssertMetric(running, null, "N/A", reason: "hosted-tier");
        Assert.Contains("\"runningNetJoules\":{\"value\":null,\"label\":\"N/A\",\"reason\":\"hosted-tier\"}", payload, StringComparison.Ordinal);

        // A hosted tier forwards N/A power; it adds nothing to the estimate and two hosted steps at once never carry shared-window.
        _ = snapshot.Apply(Started("Planner", 0.0, Tier.Nvidia));
        string power = snapshot.Apply(new PowerEvent(Origin, false, Metric.NotApplicable("hosted-tier"), 1.0));
        AssertMetric(AgentOf(power, "Writer").GetProperty("runningNetJoules"), null, "N/A", reason: "hosted-tier");
        Assert.Empty(AgentOf(power, "Writer").GetProperty("flags").EnumerateArray());
        Assert.Empty(AgentOf(power, "Planner").GetProperty("flags").EnumerateArray());
    }

    [Fact]
    public void Overlapping_steps_flag_shared_window()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new BaselineEvent(Origin, false, Baseline(20.0)));
        string first = snapshot.Apply(Started("Planner", 0.0));
        Assert.Empty(AgentOf(first, "Planner").GetProperty("flags").EnumerateArray());

        string second = snapshot.Apply(Started("Researcher", 0.5));

        Assert.Equal(["shared-window"], AgentOf(second, "Planner").GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()));
        Assert.Equal(["shared-window"], AgentOf(second, "Researcher").GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()));

        // Each estimate integrates the full device power, so both carry the same value from the later start.
        _ = snapshot.Apply(Power(60.0, 0.5));
        string power = snapshot.Apply(Power(60.0, 1.5));
        AssertMetric(AgentOf(power, "Planner").GetProperty("runningNetJoules"), 60.0, "estimated", unit: "J");
        AssertMetric(AgentOf(power, "Researcher").GetProperty("runningNetJoules"), 40.0, "estimated", unit: "J");

        // Once one of them completes the other runs alone again.
        string done = snapshot.Apply(new StepCompletedEvent(At(2), false, Reading(agent: "Planner")));
        Assert.Empty(AgentOf(done, "Researcher").GetProperty("flags").EnumerateArray());
    }

    // ---- criterion 12 and R5: never in the results file ----

    [Fact]
    public void Estimates_live_only_in_the_dashboard_project()
    {
        // No type of the results model, and no type outside the dashboard project that this suite references, has a running-estimate member.
        Assembly[] assemblies = [typeof(StepReading).Assembly, typeof(AgentJoules.Workflow.Runs.RunAssembler).Assembly, typeof(AgentJoules.Reporting.Json.AgentJoulesJsonContext).Assembly];
        foreach (Assembly assembly in assemblies)
        {
            Assert.DoesNotContain(
                assembly.GetTypes().SelectMany(static type => type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)),
                static member => member.Name.StartsWith("Running", StringComparison.Ordinal) && member.Name.Contains("Joules", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(
            typeof(StepReading).Assembly.GetReferencedAssemblies().Concat(typeof(AgentJoules.Workflow.Runs.RunAssembler).Assembly.GetReferencedAssemblies()).Concat(typeof(AgentJoules.Reporting.Json.AgentJoulesJsonContext).Assembly.GetReferencedAssemblies()),
            static reference => string.Equals(reference.Name, "AgentJoules.Dashboard", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Estimates_never_enter_results_file()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        var gpu = new ScriptedNvmlApi(time) { ConstantPowerWatts = 30.0 };
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
            {
                ["local"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)),
                ["nvidia"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)),
            });
        setup.Gpu = gpu;
        RunOrchestrationFixtures.UseClock(setup, time);
        await using var dashboard = DashboardFixtures.NewSink(new RecordingLogger(), time);
        DashboardConnection connection = dashboard.Connect();

        // At this commit no production code emits the step-started event (the producers of the run header and of the step start come with the server), so the dashboard is told that the first step
        // started with the baseline of 20 W; the power samples the production meter forwards during the run then feed a real running estimate that the results file must not show.
        dashboard.Emit(new BaselineEvent(Origin, false, Baseline(20.0)));
        dashboard.Emit(Started("Planner", 0.0));
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<ILiveEventSink>();
            _ = services.AddSingleton<ILiveEventSink>(dashboard);
        });

        CliRunResult result = await RunOrchestrationFixtures.RunDrivenAsync("run --tier local --task incident-brief --repeat 1 --baseline-seconds 2", setup, time).WaitAsync(HangGuard);

        Assert.True(result.ExitCode == 0, result.Stderr);
        await dashboard.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        // The dashboard did compute running estimates during the run ...
        List<string> payloads = Drain(connection);
        Assert.True(payloads.Any(DashboardFixtures.HasNumericEstimate), "payload types: " + string.Join(",", payloads.Select(static payload => Parse(payload).GetProperty("type").GetString())) + "; first power: " + payloads.FirstOrDefault(static payload => payload.Contains("\"type\":\"power\"", StringComparison.Ordinal)));

        // ... and none of them is in the file: no running member anywhere, no estimated label on an energy metric.
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        Assert.DoesNotContain(PropertyNames(document.RootElement), static name => name.StartsWith("running", StringComparison.OrdinalIgnoreCase));
        string[] energyMembers = ["grossJoules", "netJoules", "avgW", "peakW"];
        foreach (JsonElement step in RunOrchestrationFixtures.Steps(document))
        {
            foreach (string member in energyMembers)
            {
                Assert.NotEqual("estimated", step.GetProperty(member).GetProperty("label").GetString());
            }
        }

        JsonElement totals = document.RootElement.GetProperty("totals");
        foreach (JsonProperty property in totals.EnumerateObject().Where(static property => property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("label", out _)))
        {
            if (property.Name is not ("cost" or "costByCurrency"))
            {
                Assert.NotEqual("estimated", property.Value.GetProperty("label").GetString());
            }
        }
    }

    // ---- helpers ----

    private static StepStartedEvent Started(string agent, double startSeconds, Tier tier = Tier.Local) => new(Origin, false, Identity(agent, tier), startSeconds);

    private static PowerEvent Power(double watts, double seconds) => new(At(seconds), false, Metric.Measured(watts, "W"), seconds);

    private static BaselineResult Baseline(double meanW) =>
        new(Metric.Measured(meanW, "W"), Metric.Measured(0.5, "W"), seconds: 10.0, sampleCount: 100, coefficientOfVariation: 0.025, maxCoefficientOfVariation: 0.1, ReadingFlags.None);

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (string name in PropertyNames(property.Value))
                    {
                        yield return name;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    foreach (string name in PropertyNames(item))
                    {
                        yield return name;
                    }
                }

                break;
            default:
                break;
        }
    }
}