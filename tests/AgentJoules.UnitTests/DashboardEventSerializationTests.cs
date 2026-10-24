using System.Text;
using System.Text.Json;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Dashboard;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-090 (REQ-032): the serialization of the live events. Every payload is one line, <c>{"type","atUtc","replay","data"}</c>, whose labelled values are the label objects of the results file
/// (a missing value is null with its label, never 0); the cumulative values are computed on the server so that dropped events cannot skew them; the completed-step flags equal the ones the
/// results file derives; and no payload carries a registered key. Every timing test runs on a fake clock; the only real-time values are hang guards that end a run that already failed.
/// </summary>
[Trait("Spec", "REQ-032")]
public sealed class DashboardEventSerializationTests
{
    internal static readonly DateTimeOffset Origin = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private const int Batch = 500;
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    // ---- criteria 1 to 3: the payload ----

    [Fact]
    public void Step_completed_payload()
    {
        // A key present in the configuration of the host: built at run time, registered with the redactor, never written down in the source.
        string key = RuntimeKey();
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(key));
        var snapshot = DashboardFixtures.NewSnapshot(redactor);

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer")));

        Assert.DoesNotContain('\n', payload);
        JsonElement root = Parse(payload);
        Assert.Equal("step-completed", root.GetProperty("type").GetString());
        Assert.Equal(Origin, root.GetProperty("atUtc").GetDateTimeOffset());
        Assert.False(root.GetProperty("replay").GetBoolean());
        JsonElement step = root.GetProperty("data").GetProperty("step");
        Assert.Equal("Writer", step.GetProperty("agent").GetString());
        AssertMetric(step.GetProperty("netJoules"), 60.0, "measured", unit: "J");
        Assert.Contains("\"netJoules\":{\"value\":60,\"label\":\"measured\",\"unit\":\"J\"}", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(KeySuffix(key), payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hosted_step_serializes_na_and_not_reported_never_zero()
    {
        StepReading hosted = await StepReadingFixtures.HostedAsync(Tier.Nvidia, agent: "Writer");
        StepReading unreported = RunDocumentFixtures.HostedNoUsage().Steps[0];
        var snapshot = DashboardFixtures.NewSnapshot();

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, hosted));
        string unreportedPayload = snapshot.Apply(new StepCompletedEvent(Origin, false, unreported));

        Assert.Contains("\"netJoules\":{\"value\":null,\"label\":\"N/A\",\"reason\":\"hosted-tier\"}", payload, StringComparison.Ordinal);
        JsonElement tokens = Parse(unreportedPayload).GetProperty("data").GetProperty("step");
        AssertMetric(tokens.GetProperty("inputTokens"), null, "not-reported", reason: "provider-omitted-usage");
        AssertMetric(tokens.GetProperty("outputTokens"), null, "not-reported", reason: "provider-omitted-usage");
        foreach (string text in new[] { payload, unreportedPayload })
        {
            AssertNoMissingValueIsZero(Parse(text));
        }
    }

    [Fact]
    public void Hosted_power_event_serializes_na_with_a_null_value()
    {
        var snapshot = DashboardFixtures.NewSnapshot();

        string payload = snapshot.Apply(new PowerEvent(Origin, false, Metric.NotApplicable("hosted-tier"), 1.0));

        JsonElement data = Parse(payload).GetProperty("data");
        AssertMetric(data.GetProperty("watts"), null, "N/A", reason: "hosted-tier");
        Assert.Equal(1.0, data.GetProperty("sampleSeconds").GetDouble());
        AssertNoMissingValueIsZero(Parse(payload));
    }

    [Fact]
    public void Every_event_type_carries_its_data_members()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        StepIdentity step = Identity("Planner");
        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);
        (LiveEvent Event, string Type, string[] Members)[] cases =
        [
            (new RunStartedEvent(Origin, false, run.Run, run.Environment, 85.0), "run-started", ["run", "environment", "powerLimitW"]),
            (new BaselineEvent(Origin, false, run.Baseline!), "baseline", ["baseline"]),
            (new ProgressEvent(Origin, false, "summarise", 2, 3, 1, 3), "progress", ["task", "taskIndex", "taskCount", "repeat", "repeatCount"]),
            (new StepStartedEvent(Origin, false, step, 1.0), "step-started", ["step", "startSeconds", "agents", "activeAgents", "agentsOmitted"]),
            (new StepProgressEvent(Origin, false, step, 7), "step-progress", ["step", "outputChunks", "agents", "activeAgents", "agentsOmitted"]),
            (new PowerEvent(Origin, false, Metric.Measured(42.0, "W"), 1.5), "power", ["watts", "sampleSeconds", "agents", "activeAgents", "agentsOmitted"]),
            (new StepCompletedEvent(Origin, false, Reading()), "step-completed", ["step", "flags", "agents", "activeAgents", "agentsOmitted"]),
            (new RunCompletedEvent(Origin, false, run.Run, run.Totals), "run-completed", ["run", "totals"]),
        ];

        foreach ((LiveEvent liveEvent, string type, string[] members) in cases)
        {
            JsonElement root = Parse(snapshot.Apply(liveEvent));
            Assert.Equal(type, root.GetProperty("type").GetString());
            Assert.Equal(members.Order(StringComparer.Ordinal), root.GetProperty("data").EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        }

        JsonElement snapshotRoot = Parse(snapshot.ToSnapshotPayload(Origin));
        Assert.Equal("snapshot", snapshotRoot.GetProperty("type").GetString());
        JsonElement data = snapshotRoot.GetProperty("data");
        foreach (string member in new[] { "run", "environment", "powerLimitW", "baseline", "progress", "completed", "agents", "activeAgents", "steps", "watts", "sampleSeconds", "agentsOmitted", "snapshotDegraded" })
        {
            Assert.True(data.TryGetProperty(member, out _), $"The snapshot data has no {member}.");
        }
    }

    [Fact]
    public void Completed_step_payload_carries_the_flags_of_the_reading()
    {
        var snapshot = DashboardFixtures.NewSnapshot();

        JsonElement flags = Parse(snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(flags: ReadingFlags.LowResolution | ReadingFlags.MaxTokensExceeded)))).GetProperty("data").GetProperty("flags");

        Assert.Equal(["low-resolution", "max-tokens-exceeded"], flags.EnumerateArray().Select(static flag => flag.GetString()));
    }

    // ---- the shape of the step: the results file ----

    [Fact]
    public void Step_payload_matches_results_file()
    {
        StepReading step = RunDocumentFixtures.PlannerStep();
        RunDocument document = RunDocumentFixtures.Document([step], []);
        var snapshot = DashboardFixtures.NewSnapshot();

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, step));

        using JsonDocument file = JsonDocument.Parse(RunDocumentFixtures.Serialise(document));
        string fromFile = Compact(file.RootElement.GetProperty("steps")[0]);
        string fromPayload = Compact(Parse(payload).GetProperty("data").GetProperty("step"));
        Assert.Equal(fromFile, fromPayload);
        Assert.DoesNotContain(RunDocumentFixtures.SecretText, payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Quality_reason_markup_stays_data()
    {
        const string reason = "judge-failed: <b>x</b>";
        var snapshot = DashboardFixtures.NewSnapshot();

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(qualityReason: reason)));

        // The markup is an escaped JSON string: no angle bracket reaches the wire, and the value read back is the text itself.
        Assert.DoesNotContain("<b>", payload, StringComparison.Ordinal);
        Assert.Contains("\\u003Cb\\u003E", payload, StringComparison.Ordinal);
        Assert.Equal(reason, Parse(payload).GetProperty("data").GetProperty("step").GetProperty("qualityReason").GetString());
    }

    // ---- criterion 8: cumulative values ----

    [Fact]
    public void Cumulative_values_are_label_objects_summed_on_the_server()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer", netJoules: Metric.Measured(10.0, "J"))));

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer", netJoules: Metric.Measured(12.0, "J"))));

        JsonElement writer = AgentOf(payload, "Writer");
        AssertMetric(writer.GetProperty("cumulativeNetJoules"), 22.0, "measured", unit: "J");
        AssertMetric(writer.GetProperty("cumulativeInputTokens"), 240.0, "measured", unit: "tokens");
        AssertMetric(writer.GetProperty("cumulativeOutputTokens"), 160.0, "measured", unit: "tokens");
        JsonElement planner = AgentOf(payload, "Planner");
        AssertMetric(planner.GetProperty("cumulativeNetJoules"), null, "not-reported", reason: "no-steps");
    }

    [Fact]
    public void Cumulative_of_a_measured_and_a_not_reported_step_takes_the_label_and_reason_of_the_lacking_step()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer", inputTokens: Metric.Measured(120.0, "tokens"))));

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer", inputTokens: Metric.NotReported("provider-omitted-usage"))));

        JsonElement writer = AgentOf(payload, "Writer");
        AssertMetric(writer.GetProperty("cumulativeInputTokens"), null, "not-reported", reason: "provider-omitted-usage");
        AssertMetric(writer.GetProperty("cumulativeNetJoules"), 120.0, "measured", unit: "J");
    }

    [Fact]
    public async Task Hosted_cumulative_is_na_hosted_tier_never_zero()
    {
        var snapshot = DashboardFixtures.NewSnapshot();

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, await StepReadingFixtures.HostedAsync(Tier.Nebius, agent: "Writer")));

        AssertMetric(AgentOf(payload, "Writer").GetProperty("cumulativeNetJoules"), null, "N/A", reason: "hosted-tier");
    }

    [Fact]
    public async Task Cumulative_survives_dropped_events()
    {
        const int steps = 1500;
        var time = new FakeTimeProvider(Origin);
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), time);
        DashboardConnection connection = sink.Connect();

        // Nobody reads the connection, so its channel of 1000 drops the oldest payloads; the ingress is flushed every batch so that no event is lost before the pump applies it.
        for (int emitted = 0; emitted < steps; emitted++)
        {
            sink.Publish(Reading(agent: "Writer", netJoules: Metric.Measured(1.0, "J")));
            if ((emitted + 1) % Batch == 0)
            {
                await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            }
        }

        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        List<string> received = Drain(connection);
        Assert.Equal(1000, received.Count);
        JsonElement last = AgentOf(received[^1], "Writer");
        AssertMetric(last.GetProperty("cumulativeNetJoules"), steps, "measured", unit: "J");
        AssertMetric(last.GetProperty("cumulativeInputTokens"), steps * 120.0, "measured", unit: "tokens");
        JsonElement first = AgentOf(received[0], "Writer");
        Assert.True(first.GetProperty("cumulativeNetJoules").GetProperty("value").GetDouble() > 500.0, "The oldest 500 payloads were dropped, so the oldest one left is past the 500th step.");

        // A client that connects now still sees every step in its snapshot.
        DashboardConnection late = sink.Connect();
        AssertMetric(AgentOf(late.Snapshot, "Writer").GetProperty("cumulativeNetJoules"), steps, "measured", unit: "J");
    }

    // ---- P-68: the completed-step flags ----

    [Fact]
    public void Completed_overlap_flags_shared_window_like_the_file()
    {
        // Three measured steps: Planner 1 to 3 s and Researcher 2 to 4 s overlap, Writer 5 to 6 s touches nothing.
        WindowSpan plannerWindow = new(1.0, 3.0);
        WindowSpan researcherWindow = new(2.0, 4.0);
        WindowSpan writerWindow = new(5.0, 6.0);
        StepReading planner = Reading(agent: "Planner", window: plannerWindow);
        StepReading researcher = Reading(agent: "Researcher", window: researcherWindow);
        StepReading writer = Reading(agent: "Writer", window: writerWindow);
        var snapshot = DashboardFixtures.NewSnapshot();

        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Planner"), plannerWindow.StartSeconds));
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Researcher"), researcherWindow.StartSeconds));
        string plannerDone = snapshot.Apply(new StepCompletedEvent(Origin, false, planner));
        string researcherDone = snapshot.Apply(new StepCompletedEvent(Origin, false, researcher));
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Writer"), writerWindow.StartSeconds));
        string writerDone = snapshot.Apply(new StepCompletedEvent(Origin, false, writer));

        // The file: RunAssembler.MarkSharedWindows over the same readings.
        StepReading[] marked = AgentJoules.Workflow.Runs.RunAssembler.MarkSharedWindows(Tier.Local, [planner, researcher, writer]);
        string[] expected = [.. marked.Select(static step => step.Flags.HasFlag(ReadingFlags.SharedWindow) ? "shared-window" : "-")];
        string[] actual = [.. new[] { plannerDone, researcherDone, writerDone }.Select(static payload => FlagsOf(payload).Contains("shared-window", StringComparer.Ordinal) ? "shared-window" : "-")];
        Assert.Equal(["shared-window", "shared-window", "-"], expected);
        Assert.Equal(expected, actual);

        // The reading itself is not changed.
        Assert.False(planner.Flags.HasFlag(ReadingFlags.SharedWindow));
    }

    [Fact]
    public async Task Hosted_steps_judge_readings_and_steps_without_a_window_are_never_flagged()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        WindowSpan window = new(1.0, 3.0);
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Planner"), 1.0));
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Writer", Tier.Nvidia), 1.0));

        // A measured step without a window, a hosted step and a judge reading, each while another step is still active.
        string noWindow = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Researcher")));
        string hosted = snapshot.Apply(new StepCompletedEvent(Origin, false, await StepReadingFixtures.HostedAsync(Tier.Nvidia, agent: "Writer")));
        string judge = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Reviewer", window: window, judgedAgent: "Writer")));

        Assert.DoesNotContain("shared-window", FlagsOf(noWindow));
        Assert.DoesNotContain("shared-window", FlagsOf(hosted));
        Assert.DoesNotContain("shared-window", FlagsOf(judge));
    }

    // ---- the snapshot ----

    [Fact]
    public void Snapshot_holds_every_agent_in_catalog_order_and_every_completed_step()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer")));
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Planner")));

        JsonElement data = Parse(snapshot.ToSnapshotPayload(Origin.AddSeconds(1))).GetProperty("data");

        Assert.Equal(TierOptions.KnownAgentNames, data.GetProperty("agents").EnumerateArray().Select(static agent => agent.GetProperty("agent").GetString()));
        Assert.Equal(["Writer", "Planner"], data.GetProperty("steps").EnumerateArray().Select(static entry => entry.GetProperty("step").GetProperty("agent").GetString()));
        Assert.Equal("ok", data.GetProperty("agents").EnumerateArray().Single(static agent => agent.GetProperty("agent").GetString() == "Writer").GetProperty("status").GetString());
        Assert.Equal("waiting", data.GetProperty("agents").EnumerateArray().Single(static agent => agent.GetProperty("agent").GetString() == "Reviewer").GetProperty("status").GetString());
    }

    [Fact]
    public void A_run_started_event_clears_the_agents_and_the_cumulative_values()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer")));

        _ = snapshot.Apply(new RunStartedEvent(Origin, false, run.Run, run.Environment, 85.0));

        JsonElement data = Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data");
        Assert.Empty(data.GetProperty("steps").EnumerateArray());
        AssertMetric(AgentOf(snapshot.ToSnapshotPayload(Origin), "Writer").GetProperty("cumulativeNetJoules"), null, "not-reported", reason: "no-steps");
    }

    // ---- the redactor is required ----

    [Fact]
    public async Task The_snapshot_and_the_sink_require_the_redactor_of_the_host()
    {
        ParamName(Assert.Throws<ArgumentNullException>(() => new DashboardSnapshot(null!)), "redactor");
        ArgumentNullException failure = Assert.Throws<ArgumentNullException>(() => new DashboardSink(new RecordingLogger(), new FakeTimeProvider(Origin), null!));
        ParamName(failure, "redactor");
        _ = Assert.Throws<ArgumentNullException>(() => new DashboardSink(null!, new FakeTimeProvider(Origin), new SecretRedactor()));
        _ = Assert.Throws<ArgumentNullException>(() => new DashboardSink(new RecordingLogger(), null!, new SecretRedactor()));

        // With a redactor both work, and the sink masks with the redactor it was given.
        string key = RuntimeKey();
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register("registered-" + KeySuffix(key)));
        await using DashboardSink sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin), redactor);
        DashboardConnection connection = sink.Connect();
        sink.Emit(new ProgressEvent(Origin, false, "registered-" + KeySuffix(key), 1, 1, 1, 1));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        Assert.DoesNotContain(KeySuffix(key), string.Concat(Drain(connection)), StringComparison.Ordinal);
    }

    private static void ParamName(ArgumentNullException failure, string expected) => Assert.Equal(expected, failure.ParamName);

    // ---- the last power sample (AJ-090 fix round) ----

    [Fact]
    public void Snapshot_carries_the_last_power_sample_with_its_label_and_no_history()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        JsonElement empty = Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data");

        // Before any power event the members are present and null (there is no sample to show).
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("watts").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("sampleSeconds").ValueKind);

        _ = snapshot.Apply(new PowerEvent(Origin, false, Metric.Measured(41.0, "W"), 1.0));
        _ = snapshot.Apply(new PowerEvent(Origin, false, Metric.Measured(42.5, "W"), 1.1));
        JsonElement measured = Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data");
        AssertMetric(measured.GetProperty("watts"), 42.5, "measured", unit: "W");
        Assert.Equal(1.1, measured.GetProperty("sampleSeconds").GetDouble(), 9);

        // A tier without GPU power: the N/A sample is the last sample, label and reason kept.
        _ = snapshot.Apply(new PowerEvent(Origin, false, Metric.NotApplicable("hosted-tier"), 2.0));
        JsonElement hosted = Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data");
        AssertMetric(hosted.GetProperty("watts"), null, "N/A", reason: "hosted-tier");

        // A new run starts from nothing.
        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);
        _ = snapshot.Apply(new RunStartedEvent(Origin, false, run.Run, run.Environment, 85.0));
        Assert.Equal(JsonValueKind.Null, Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data").GetProperty("watts").ValueKind);
    }

    // ---- judge readings, overlap boundary, snapshot flags ----

    [Fact]
    public void A_judge_reading_changes_no_agent_no_count_and_no_flag()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Planner"), 1.0));
        string before = snapshot.ToSnapshotPayload(Origin);
        int count = snapshot.CompletedStepCount;

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Judge", window: new WindowSpan(1.0, 3.0), judgedAgent: "Planner")));

        Assert.Equal(count, snapshot.CompletedStepCount);
        Assert.Equal(["Planner"], snapshot.ActiveAgents);
        Assert.Equal(TierOptionsAgents(), Parse(payload).GetProperty("data").GetProperty("agents").EnumerateArray().Select(static agent => agent.GetProperty("agent").GetString()));
        Assert.Equal(before, snapshot.ToSnapshotPayload(Origin));
        Assert.Empty(Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data").GetProperty("steps").EnumerateArray());
    }

    [Fact]
    public void Windows_that_only_touch_are_not_flagged_like_the_file()
    {
        // Planner 1 to 3 s, Researcher 3 to 5 s: the windows touch at 3 s and share no interval, so neither is flagged, in the file or live.
        StepReading planner = Reading(agent: "Planner", window: new WindowSpan(1.0, 3.0));
        StepReading researcher = Reading(agent: "Researcher", window: new WindowSpan(3.0, 5.0));
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Planner"), 1.0));
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Researcher"), 3.0));

        string plannerDone = snapshot.Apply(new StepCompletedEvent(Origin, false, planner));
        string researcherDone = snapshot.Apply(new StepCompletedEvent(Origin, false, researcher));

        StepReading[] marked = AgentJoules.Workflow.Runs.RunAssembler.MarkSharedWindows(Tier.Local, [planner, researcher]);
        Assert.All(marked, static step => Assert.False(step.Flags.HasFlag(ReadingFlags.SharedWindow)));
        Assert.DoesNotContain("shared-window", FlagsOf(plannerDone));
        Assert.DoesNotContain("shared-window", FlagsOf(researcherDone));
    }

    [Fact]
    public void The_snapshot_steps_carry_the_flags_of_the_step_completed_payloads()
    {
        WindowSpan plannerWindow = new(1.0, 3.0);
        WindowSpan researcherWindow = new(2.0, 4.0);
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Planner"), plannerWindow.StartSeconds));
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Researcher"), researcherWindow.StartSeconds));
        string plannerDone = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Planner", window: plannerWindow, flags: ReadingFlags.LowResolution)));
        string researcherDone = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Researcher", window: researcherWindow)));

        // After a reconnect the derived flag is still there: it is recomputed for the snapshot, not lost.
        JsonElement steps = Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data").GetProperty("steps");
        string[][] fromSnapshot = [.. steps.EnumerateArray().Select(static entry => entry.GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()!).ToArray())];
        Assert.Equal(2, fromSnapshot.Length);
        Assert.Equal(FlagsOf(plannerDone), fromSnapshot[0]);
        Assert.Equal(FlagsOf(researcherDone), fromSnapshot[1]);
        Assert.Equal(["low-resolution", "shared-window"], fromSnapshot[0]);
        Assert.Equal(["shared-window"], fromSnapshot[1]);
    }

    // ---- the cap on tracked agents (security M-1) ----

    [Fact]
    public void A_thousand_distinct_agents_give_a_bounded_payload_and_the_notice()
    {
        const int distinct = 1000;
        var snapshot = DashboardFixtures.NewSnapshot();
        int largest = 0;
        for (int i = 0; i < distinct; i++)
        {
            string agent = "agent-" + i;
            largest = Math.Max(largest, snapshot.Apply(new StepStartedEvent(Origin, false, Identity(agent), i)).Length);
            largest = Math.Max(largest, snapshot.Apply(new PowerEvent(At(i), false, Metric.Measured(40.0, "W"), i)).Length);
            largest = Math.Max(largest, snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: agent))).Length);
        }

        // At most the four configured agents and eight more have a card, whatever the run names: a payload stays well under 20 KB.
        Assert.True(largest < 20_000, $"largest payload {largest} bytes");
        string power = snapshot.Apply(new PowerEvent(At(5000), false, Metric.Measured(40.0, "W"), 5000.0));
        JsonElement data = Parse(power).GetProperty("data");
        Assert.Equal(12, data.GetProperty("agents").GetArrayLength());
        Assert.Equal(distinct - 8, data.GetProperty("agentsOmitted").GetInt32());

        // The steps of the agents without a card are still counted and listed.
        Assert.Equal(distinct, snapshot.CompletedStepCount);
        JsonElement snapshotData = Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data");
        Assert.Equal(distinct, snapshotData.GetProperty("steps").GetArrayLength());
        Assert.Equal(distinct - 8, snapshotData.GetProperty("agentsOmitted").GetInt32());
        Assert.Equal(12, snapshotData.GetProperty("agents").GetArrayLength());

        // A new run starts the count again.
        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);
        _ = snapshot.Apply(new RunStartedEvent(Origin, false, run.Run, run.Environment, 85.0));
        Assert.Equal(0, Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data").GetProperty("agentsOmitted").GetInt32());
    }

    [Fact]
    public void Agents_within_the_cap_keep_their_cards_in_appearance_order()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Extra-b")));
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Extra-a")));

        string[] agents = [.. Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data").GetProperty("agents").EnumerateArray().Select(static agent => agent.GetProperty("agent").GetString()!)];

        Assert.Equal([.. TierOptionsAgents(), "Extra-b", "Extra-a"], agents);
        Assert.Equal(0, Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data").GetProperty("agentsOmitted").GetInt32());
    }

    // ---- judge steps, steps beyond the cap, look-alike names (fix round 2) ----

    [Fact]
    public void A_judge_step_started_creates_no_running_entry_no_card_no_active_agent_and_no_flag()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        string before = snapshot.ToSnapshotPayload(Origin);

        string started = snapshot.Apply(new StepStartedEvent(Origin, false, new StepIdentity("Judge", "m", Tier.Local, "t", 1, IsJudge: true), 1.5));

        Assert.Empty(Parse(started).GetProperty("data").GetProperty("activeAgents").EnumerateArray());
        Assert.Equal(TierOptionsAgents(), Parse(started).GetProperty("data").GetProperty("agents").EnumerateArray().Select(static agent => agent.GetProperty("agent").GetString()));
        Assert.Equal(before, snapshot.ToSnapshotPayload(Origin));

        // A lone measured step that overlaps the judge step in time is not flagged: the results file has no judge reading among the steps it compares.
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Planner"), 1.0));
        string done = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Planner", window: new WindowSpan(1.0, 3.0))));
        Assert.DoesNotContain("shared-window", FlagsOf(done));
    }

    [Fact]
    public void A_running_step_of_an_agent_beyond_the_cap_still_counts_for_the_overlap_flag_like_the_file()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        for (int i = 0; i < 8; i++)
        {
            _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Extra-" + i)));
        }

        // Planner runs 1 to 3 s, the agent beyond the cap runs from 2 s: the windows overlap, so Planner is flagged when it completes, as the file flags it.
        StepReading planner = Reading(agent: "Planner", window: new WindowSpan(1.0, 3.0));
        StepReading capped = Reading(agent: "Capped", window: new WindowSpan(2.0, 4.0));
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Planner"), 1.0));
        string cappedStarted = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Capped"), 2.0));
        string plannerDone = snapshot.Apply(new StepCompletedEvent(Origin, false, planner));

        StepReading[] marked = AgentJoules.Workflow.Runs.RunAssembler.MarkSharedWindows(Tier.Local, [planner, capped]);
        Assert.True(marked[0].Flags.HasFlag(ReadingFlags.SharedWindow));
        Assert.Contains("shared-window", FlagsOf(plannerDone));

        // The agent beyond the cap has no card, no active entry and no estimate.
        JsonElement data = Parse(cappedStarted).GetProperty("data");
        Assert.Equal(12, data.GetProperty("agents").GetArrayLength());
        Assert.DoesNotContain("Capped", data.GetProperty("activeAgents").EnumerateArray().Select(static agent => agent.GetString()));

        // When it completes its start is forgotten: a later lone step is not flagged by a step that is over.
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, capped));
        string later = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer", window: new WindowSpan(10.0, 11.0))));
        Assert.DoesNotContain("shared-window", FlagsOf(later));
    }

    [Fact]
    public void Only_the_configured_agents_are_marked_configured_so_a_look_alike_gets_the_fixed_badge()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        string[] lookAlikes = ["planner", "Planner ", " Planner", "PLANNER", string.Concat((char)0x0420, "lanner"), string.Concat("Planner", (char)0x3164)];
        foreach (string name in lookAlikes)
        {
            _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: name)));
        }

        JsonElement agents = Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data").GetProperty("agents");

        Assert.Equal(TierOptionsAgents().Count + lookAlikes.Length, agents.GetArrayLength());
        foreach (JsonElement agent in agents.EnumerateArray())
        {
            Assert.Equal(TierOptionsAgents().Contains(agent.GetProperty("agent").GetString()!), agent.GetProperty("configured").GetBoolean());
        }
    }

    [Fact]
    public void A_snapshot_in_its_smaller_form_says_so_and_the_sink_logs_it_once_with_the_type_only()
    {
        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);
        var snapshot = DashboardFixtures.NewSnapshot();
        Assert.False(Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data").GetProperty("snapshotDegraded").GetBoolean());
        _ = snapshot.Apply(new RunStartedEvent(Origin, false, run.Run, run.Environment, 85.0));
        _ = Assert.ThrowsAny<Exception>(() => snapshot.Apply(new PowerEvent(Origin, false, Metric.Measured(40.0, "W"), double.NaN)));
        var told = new List<string>();
        snapshot.DegradedObserver = told.Add;

        JsonElement data = Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data");

        Assert.True(data.GetProperty("snapshotDegraded").GetBoolean());
        Assert.Equal(run.Run.Id, data.GetProperty("run").GetProperty("id").GetString());
        Assert.Single(told);
        Assert.DoesNotContain("NaN", told[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sink_logs_the_degraded_snapshot_once_per_sink_at_warning_with_the_exception_type_only()
    {
        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);
        var logger = new RecordingLogger();
        await using DashboardSink sink = DashboardFixtures.NewSink(logger, new FakeTimeProvider(Origin));
        sink.Emit(new RunStartedEvent(Origin, false, run.Run, run.Environment, 85.0));
        sink.Emit(new PowerEvent(Origin, false, Metric.Measured(40.0, "W"), double.NaN));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        // Every connect after the bad event gets the smaller snapshot; the warning is written once, from another thread (a hang guard ends the wait if it never comes).
        for (int i = 0; i < 3; i++)
        {
            Assert.True(Parse(sink.Connect().Snapshot).GetProperty("data").GetProperty("snapshotDegraded").GetBoolean());
        }

        using var guard = new CancellationTokenSource(HangGuard);
        while (!logger.Entries.Any(static entry => entry.EventId.Id == 302))
        {
            await Task.Yield();
            guard.Token.ThrowIfCancellationRequested();
        }

        RecordedLogEntry[] warnings = [.. logger.Entries.Where(static entry => entry.EventId.Id == 302)];
        RecordedLogEntry warning = Assert.Single(warnings);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.DoesNotContain("NaN", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Run.Id, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_or_whitespace_progress_task_is_not_reported()
    {
        var snapshot = DashboardFixtures.NewSnapshot();

        foreach (string task in new[] { string.Empty, "   " })
        {
            string payload = snapshot.Apply(new ProgressEvent(Origin, false, task, 1, 3, 1, 3));
            Assert.Equal(JsonValueKind.Null, Parse(payload).GetProperty("data").GetProperty("task").ValueKind);
        }

        Assert.Equal("incident", Parse(snapshot.Apply(new ProgressEvent(Origin, false, "incident", 1, 3, 1, 3))).GetProperty("data").GetProperty("task").GetString());
    }

    [Fact]
    public void Omitted_agents_are_counted_by_name_cut_to_64_characters_so_the_count_is_exact()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        for (int i = 0; i < 8; i++)
        {
            _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Extra-" + i)));
        }

        string longName = new('x', 100);
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: longName + "1")));
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: longName + "2")));
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "other")));
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "other")));

        Assert.Equal(2, snapshot.AgentsOmitted);
    }

    [Fact]
    public void At_most_64_running_starts_of_agents_beyond_the_cap_are_kept_and_the_newest_are_refused()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        for (int i = 0; i < 8; i++)
        {
            _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Extra-" + i)));
        }

        // 64 agents beyond the cap start late (100 s and after) and are kept; 6 more start at 0 s and are refused, because the code keeps the first 64 and refuses the newest.
        for (int i = 0; i < 64; i++)
        {
            _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Capped-" + i), 100.0 + i));
        }

        for (int i = 64; i < 70; i++)
        {
            _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Capped-" + i), 0.0));
        }

        // A step over 1 to 3 s would be flagged by a refused start (0 s) but by no kept one: it is not flagged.
        string early = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Planner", window: new WindowSpan(1.0, 3.0))));
        Assert.DoesNotContain("shared-window", FlagsOf(early));

        // A step over 100 to 200 s is overlapped by the kept starts: the overlap rule still works for them.
        string late = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer", window: new WindowSpan(100.0, 200.0))));
        Assert.Contains("shared-window", FlagsOf(late));
    }

    // ---- values that must not poison the state (security L-1) ----

    [Fact]
    public void Energy_sums_that_overflow_are_not_reported_overflow_and_the_payloads_go_on()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer", netJoules: Metric.Measured(1e308, "J"))));

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer", netJoules: Metric.Measured(1e308, "J"))));

        AssertMetric(AgentOf(payload, "Writer").GetProperty("cumulativeNetJoules"), null, "not-reported", reason: "overflow");
        AssertMetric(AgentOf(snapshot.ToSnapshotPayload(Origin), "Writer").GetProperty("cumulativeInputTokens"), 240.0, "measured", unit: "tokens");
        _ = snapshot.Apply(new PowerEvent(Origin, false, Metric.Measured(40.0, "W"), 1.0));
    }

    [Fact]
    public void A_power_of_1e308_makes_the_running_estimate_not_reported_overflow()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new BaselineEvent(Origin, false, RunDocumentFixtures.Committed(Tier.Local).Baseline!));
        _ = snapshot.Apply(new StepStartedEvent(Origin, false, Identity("Writer"), 0.0));
        _ = snapshot.Apply(new PowerEvent(Origin, false, Metric.Measured(1e308, "W"), 0.0));

        string payload = snapshot.Apply(new PowerEvent(At(1), false, Metric.Measured(1e308, "W"), 1.0));

        AssertMetric(AgentOf(payload, "Writer").GetProperty("runningNetJoules"), null, "not-reported", reason: "overflow");
        AssertMetric(AgentOf(snapshot.ToSnapshotPayload(Origin), "Writer").GetProperty("runningNetJoules"), null, "not-reported", reason: "overflow");

        // The state still works: the next step completes and the energy is measured again.
        string done = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: "Writer")));
        AssertMetric(AgentOf(done, "Writer").GetProperty("cumulativeNetJoules"), 60.0, "measured", unit: "J");
    }

    [Fact]
    public void A_progress_event_without_a_task_is_not_reported()
    {
        var snapshot = DashboardFixtures.NewSnapshot();

        string payload = snapshot.Apply(new ProgressEvent(Origin, false, null!, 1, 3, 1, 3));

        Assert.Equal(JsonValueKind.Null, Parse(payload).GetProperty("data").GetProperty("task").ValueKind);
        JsonElement progress = Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data").GetProperty("progress");
        Assert.Equal(JsonValueKind.Null, progress.GetProperty("task").ValueKind);
        Assert.Equal(3, progress.GetProperty("taskCount").GetInt32());
    }

    [Fact]
    public async Task A_state_that_cannot_be_serialized_still_gives_a_valid_snapshot_and_a_connection()
    {
        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(new RunStartedEvent(Origin, false, run.Run, run.Environment, 85.0));

        // A sample time that is not a number cannot be written as JSON; the event that carries it is refused, and the state keeps it.
        _ = Assert.ThrowsAny<Exception>(() => snapshot.Apply(new PowerEvent(Origin, false, Metric.Measured(40.0, "W"), double.NaN)));

        JsonElement root = Parse(snapshot.ToSnapshotPayload(Origin));
        Assert.Equal("snapshot", root.GetProperty("type").GetString());
        Assert.Equal(run.Run.Id, root.GetProperty("data").GetProperty("run").GetProperty("id").GetString());
        Assert.Equal(0, root.GetProperty("data").GetProperty("agentsOmitted").GetInt32());

        // A client that connects to a sink in this state gets its snapshot; Connect never throws.
        await using DashboardSink sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        sink.Emit(new RunStartedEvent(Origin, false, run.Run, run.Environment, 85.0));
        sink.Emit(new PowerEvent(Origin, false, Metric.Measured(40.0, "W"), double.NaN));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        DashboardConnection connection = sink.Connect();
        Assert.Equal("snapshot", Parse(connection.Snapshot).GetProperty("type").GetString());
    }

    // ---- display-unsafe characters (security L-3) ----

    [Fact]
    public void Control_and_bidi_characters_in_a_name_are_replaced()
    {
        string hostile = string.Concat("a", (char)0x202E, "b", (char)1, "c", (char)0x2028, "d", (char)0x200B, "e");
        var snapshot = DashboardFixtures.NewSnapshot();

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: hostile)));

        // Each unsafe character is a question mark in the payload and in the snapshot; nothing is escaped and passed on.
        JsonElement step = Parse(payload).GetProperty("data").GetProperty("step");
        Assert.Equal("a?b?c?d?e", step.GetProperty("agent").GetString());
        Assert.DoesNotContain("202E", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("u0001", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a?b?c?d?e", Parse(snapshot.ToSnapshotPayload(Origin)).GetProperty("data").GetProperty("steps")[0].GetProperty("step").GetProperty("agent").GetString());
    }

    [Fact]
    public void A_lone_surrogate_in_a_name_never_reaches_a_payload_and_the_snapshot_stays_valid()
    {
        string hostile = string.Concat("a", (char)0xD800, "b");
        var snapshot = DashboardFixtures.NewSnapshot();

        // The serializer writes the lone surrogate as the replacement character U+FFFD, in the payload and in every snapshot after it: the surrogate itself never reaches the page.
        string replaced = string.Concat("a", (char)0xFFFD, "b");
        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(agent: hostile)));

        Assert.Equal(replaced, Parse(payload).GetProperty("data").GetProperty("step").GetProperty("agent").GetString());
        string text = snapshot.ToSnapshotPayload(Origin);
        Assert.Equal("snapshot", Parse(text).GetProperty("type").GetString());
        Assert.DoesNotContain(hostile, text, StringComparison.Ordinal);
        Assert.DoesNotContain("ud800", payload + text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(replaced, Parse(text).GetProperty("data").GetProperty("steps")[0].GetProperty("step").GetProperty("agent").GetString());
    }

    // ---- the key and the redaction ----

    [Fact]
    public async Task Payloads_never_carry_a_registered_key_or_an_nvapi_pattern_in_any_string()
    {
        string nvidiaKey = RuntimeKey();
        string otherKey = string.Concat("sk-", RuntimeKeySuffix().ToLowerInvariant(), "-7");
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(nvidiaKey));
        Assert.True(redactor.Register(otherKey));
        var time = new FakeTimeProvider(Origin);
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), time, redactor);
        DashboardConnection connection = sink.Connect();
        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);

        // The key in every string member the events carry: task, agent, model, quality reason, run stop reason, GPU name, tier-run identifiers.
        StepReading step = Reading(agent: "Agent-" + nvidiaKey, task: "task-" + otherKey, modelId: "model-" + nvidiaKey, qualityReason: "judge-failed: " + otherKey + " and " + nvidiaKey);
        RunInfo runInfo = run.Run with { StopReason = "stopped " + nvidiaKey, Id = "id-" + otherKey };
        EnvironmentInfo environment = run.Environment with { GpuName = "GPU " + otherKey, Provider = nvidiaKey };
        StepIdentity identity = new("Agent-" + nvidiaKey, "model-" + otherKey, Tier.Local, "task-" + nvidiaKey, 1, IsJudge: false);

        // The key is also a member name: the currency key of a cost map is a string of the file like any other.
        var keyedCosts = new Dictionary<string, Metric>(StringComparer.Ordinal) { [nvidiaKey] = Metric.NotApplicable("cost-not-configured"), [otherKey] = Metric.NotApplicable("cost-not-configured") };
        var keyedTotals = new Totals(run.Totals.GrossJoules, run.Totals.NetJoules, run.Totals.InputTokens, run.Totals.OutputTokens, keyedCosts, run.Totals.WallClockMs, run.Totals.Judge, run.Totals.Gco2e);

        sink.Emit(new RunStartedEvent(Origin, false, runInfo, environment, 85.0));
        sink.Emit(new ProgressEvent(Origin, false, "task-" + nvidiaKey, 1, 3, 1, 3));
        sink.Emit(new StepStartedEvent(Origin, false, identity, 1.0));
        sink.Emit(new StepProgressEvent(Origin, false, identity, 3));
        sink.Emit(new PowerEvent(Origin, false, Metric.Measured(42.0, "W"), 1.5));
        sink.Publish(step);
        sink.Emit(new RunCompletedEvent(Origin, false, runInfo, keyedTotals));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        List<string> payloads = [connection.Snapshot, .. Drain(connection), sink.Connect().Snapshot];
        Assert.True(payloads.Count >= 9, $"{payloads.Count} payloads");
        foreach (string payload in payloads)
        {
            AssertNoKeyMaterial(payload, nvidiaKey);
            AssertNoKeyMaterial(payload, otherKey);
        }

        // Every payload is still one line of valid JSON after the masking.
        Assert.All(payloads, static payload => Assert.Equal(JsonValueKind.Object, Parse(payload).ValueKind));
        Assert.Contains(payloads, static payload => payload.Contains("nvapi-****", StringComparison.Ordinal));
    }

    [Fact]
    public void A_snapshot_without_a_registered_redactor_still_masks_the_nvapi_pattern()
    {
        string key = RuntimeKey();
        var snapshot = DashboardFixtures.NewSnapshot();

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(modelId: "model-" + key)));

        Assert.DoesNotContain(KeySuffix(key), payload, StringComparison.Ordinal);
        Assert.Contains("nvapi-****", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_text_never_reaches_the_payload()
    {
        var snapshot = DashboardFixtures.NewSnapshot();

        string payload = snapshot.Apply(new StepCompletedEvent(Origin, false, Reading(outputText: "a private model answer")));

        Assert.DoesNotContain("a private model answer", payload, StringComparison.Ordinal);
    }

    // ---- helpers shared with the other AJ-090 test classes ----

    internal static DateTimeOffset At(double seconds) => Origin.AddSeconds(seconds);

    internal static StepIdentity Identity(string agent, Tier tier = Tier.Local) => new(agent, "model-a", tier, "summarise", 1, IsJudge: false);

    internal static StepReading Reading(
        string agent = "Planner",
        string task = "summarise",
        string modelId = "model-a",
        Metric? netJoules = null,
        Metric? inputTokens = null,
        WindowSpan? window = null,
        ReadingFlags flags = ReadingFlags.None,
        string? qualityReason = null,
        string? outputText = null,
        string? judgedAgent = null) =>
        new(
            task: task,
            repeat: 1,
            agent: agent,
            modelId: modelId,
            tier: Tier.Local,
            startUtc: StepReadingFixtures.StartUtc,
            endUtc: StepReadingFixtures.StartUtc.AddSeconds(2),
            outcome: StepOutcomes.Ok,
            latencyMs: Metric.Measured(2000, "ms"),
            ttftMs: Metric.Measured(250, "ms"),
            inputTokens: inputTokens ?? Metric.Measured(120, "tokens"),
            outputTokens: Metric.Measured(80, "tokens"),
            grossJoules: Metric.Measured(180, "J"),
            netJoules: netJoules ?? Metric.Measured(60, "J"),
            avgW: Metric.Measured(90, "W"),
            peakW: Metric.Measured(110, "W"),
            cost: Metric.NotReported("not-priced"),
            retries: 0,
            waitMs: 0,
            flags: flags,
            method: EnergyMethod.Sampling,
            sampleCount: 21,
            sampleRateHz: 10.0,
            quality: qualityReason is null ? null : Metric.NotApplicable("judge-failed"),
            qualityReason: qualityReason,
            outputText: outputText,
            window: window,
            judgedAgent: judgedAgent);

    internal static JsonElement Parse(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    internal static JsonElement AgentOf(string payload, string agent) =>
        Parse(payload).GetProperty("data").GetProperty("agents").EnumerateArray().Single(item => item.GetProperty("agent").GetString() == agent);

    internal static IReadOnlyList<string> TierOptionsAgents() => TierOptions.KnownAgentNames;

    internal static IReadOnlyList<string> FlagsOf(string payload) =>
        [.. Parse(payload).GetProperty("data").GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()!)];

    internal static void AssertMetric(JsonElement metric, double? value, string label, string? reason = null, string? unit = null)
    {
        Assert.Equal(label, metric.GetProperty("label").GetString());
        JsonElement actual = metric.GetProperty("value");
        if (value is null)
        {
            Assert.Equal(JsonValueKind.Null, actual.ValueKind);
        }
        else
        {
            Assert.Equal(value.Value, actual.GetDouble(), 9);
        }

        Assert.Equal(reason, metric.TryGetProperty("reason", out JsonElement reasonElement) ? reasonElement.GetString() : null);
        Assert.Equal(unit, metric.TryGetProperty("unit", out JsonElement unitElement) ? unitElement.GetString() : null);
    }

    internal static List<string> Drain(DashboardConnection connection)
    {
        var payloads = new List<string>();
        while (connection.Events.TryRead(out string? payload))
        {
            payloads.Add(payload);
        }

        return payloads;
    }

    // Every object that has a label and a value: a label of N/A or not-reported has a null value, so a missing value is never written as 0.
    private static void AssertNoMissingValueIsZero(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("label", out JsonElement label) && element.TryGetProperty("value", out JsonElement value) && label.GetString() is "N/A" or "not-reported")
                {
                    Assert.Equal(JsonValueKind.Null, value.ValueKind);
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    AssertNoMissingValueIsZero(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    AssertNoMissingValueIsZero(item);
                }

                break;
            default:
                break;
        }
    }

    internal static string RuntimeKeySuffix() => new(['Z', 'q', '7', 'K', 'x', '3', 'W', 'm', '9', 'T', 'r', '4', 'L', 'p', '2', 'V']);

    internal static string RuntimeKey() => string.Concat("nvapi-", RuntimeKeySuffix());

    internal static string KeySuffix(string key) => key[(key.IndexOf('-', StringComparison.Ordinal) + 1)..];

    // No window of six characters of the key after its prefix, and not the key, appears in the payload: the masking replaced the whole value, not a part of it.
    internal static void AssertNoKeyMaterial(string payload, string key)
    {
        string material = KeySuffix(key);
        Assert.DoesNotContain(key, payload, StringComparison.Ordinal);
        for (int start = 0; start + 6 <= material.Length; start++)
        {
            Assert.DoesNotContain(material.Substring(start, 6), payload, StringComparison.Ordinal);
        }
    }

    private static string Compact(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}