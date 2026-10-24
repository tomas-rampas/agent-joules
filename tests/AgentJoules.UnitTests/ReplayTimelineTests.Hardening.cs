using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json.Nodes;

using AgentJoules.Cli;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Dashboard;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

// The round-2 tests of AJ-092 (REQ-032): the checks that run before any output, the sink flush after every emit, the registered keys, the agent rule, the files that are not regular files, the
// cancellation at every phase and the literal rows of the timeline. They share the helpers and the rig of the first part of the class.
public sealed partial class ReplayTimelineTests
{
    // ---- the speed that does not fit a timer, and the durations that are not one tick ----

    [Theory]
    [InlineData("1e-9")]
    [InlineData("1e-12")]
    [InlineData("1e-13")]
    [InlineData("1e-300")]
    public async Task Speed_too_small_for_the_gaps_exits_2_before_any_output(string speed)
    {
        // The nvidia fixture has recorded gaps of 1 s, so 1 s divided by any of these exceeds the 4294967.294 s of a timer: exit 2 from the check of the replay, not from the parser.
        string path = ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Nvidia));
        int port = FreePort();
        using TempWorkspace workspace = TempWorkspace.Create();

        CliRunResult result = await CliHarness.RunAsync($"dashboard --replay \"{path}\" --speed {speed} --dashboard-port {port}", new CliHostOverrides(WorkingDirectory: workspace.Root)).WaitAsync(HangGuard);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal($"error: {ReplayService.SpeedTooSmallMessage} (ConfigurationException)", Assert.Single(result.ErrorLines));
        Assert.Empty(result.Stdout);
        Assert.DoesNotContain(speed, result.Stderr, StringComparison.Ordinal);
        using LoopbackPortBlock? nothingListens = LoopbackPortBlock.TryHold(port);
        Assert.NotNull(nothingListens);
    }

    [Fact]
    public void A_speed_just_large_enough_for_the_longest_gap_passes_the_check()
    {
        IReadOnlyList<ReplayEntry> timeline = ReplayTimeline.Build(Fixture(Tier.Nvidia));
        double longestGap = 0.0;
        for (int i = 1; i < timeline.Count; i++)
        {
            longestGap = Math.Max(longestGap, (timeline[i].Offset - timeline[i - 1].Offset).TotalSeconds);
        }

        double boundary = longestGap / BaselineOptions.MaximumSeconds;

        ReplayService.RequireDelaysWithinTimerLimit(timeline, boundary * 1.001);
        ConfigurationException refused = Assert.Throws<ConfigurationException>(() => ReplayService.RequireDelaysWithinTimerLimit(timeline, boundary * 0.999));
        Assert.Equal(ReplayService.SpeedTooSmallMessage, refused.Message);
    }

    // ---- the sink flush (security M-1) and the deadline ----

    [Fact]
    public async Task A_burst_of_events_at_one_instant_loses_none_at_the_highest_speed()
    {
        // 300 zero-length steps at the start of the run: about 1,200 events at one offset, more than the 1,000 slots of the ingress of the sink, emitted without one timer between them.
        const int steps = 300;
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local))), static root => ManySteps(root, steps, atRunStart: true));
        RunDocument run = RunDocumentFixtures.Read(text);
        Assert.Equal(steps, run.Steps.Count);

        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartService(run, speed: 4294967.294);
        _ = await rig.UriAsync();
        using DashboardConnection watcher = rig.Sink.Connect();
        var counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task drain = Task.Run(async () =>
        {
            await foreach (string payload in watcher.Events.ReadAllAsync())
            {
                string type = JsonOf(payload).GetProperty("type").GetString()!;
                _ = counts.AddOrUpdate(type, 1, static (_, count) => count + 1);
                if (type == "run-completed")
                {
                    _ = completed.TrySetResult();
                }
            }
        });
        await completed.Task.WaitAsync(HangGuard);
        await rig.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        using DashboardConnection late = rig.Sink.Connect();
        Assert.Equal(steps, JsonOf(late.Snapshot).GetProperty("data").GetProperty("steps").GetArrayLength());
        Assert.Equal(steps, counts["step-completed"]);
        Assert.Equal(steps, counts["step-started"]);
        Assert.Equal(0, rig.Sink.PumpFaultCount);
        rig.Cancel();
        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        watcher.Dispose();
        await drain.WaitAsync(HangGuard);
    }

    [Fact]
    public async Task An_event_scheduled_exactly_at_the_deadline_is_emitted()
    {
        // Speed 0.5 makes every recorded second 2 s, so the 23 s of the fixture end at exactly 46 s, the deadline: run completed is emitted, and the next entry (there is none) would be cut.
        RunDocument run = Fixture(Tier.Local);
        await using ReplayRig rig = ReplayRig.Create();
        DateTimeOffset origin = rig.Time.GetUtcNow();
        Task<int> exit = rig.StartService(run, speed: 0.5, exitAfter: TimeSpan.FromSeconds(46), startDelay: TimeSpan.Zero);
        _ = await rig.UriAsync();
        using DashboardConnection watcher = rig.Sink.Connect();
        await rig.DriveToEndAsync();

        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        Assert.Equal(TimeSpan.FromSeconds(46), rig.Time.GetUtcNow() - origin);
        List<string> types = [];
        while (watcher.Events.TryRead(out string? payload))
        {
            types.Add(JsonOf(payload).GetProperty("type").GetString()!);
        }

        Assert.Contains("run-completed", types);
    }

    [Fact]
    public async Task An_exit_after_longer_than_120_seconds_still_waits_120_seconds_for_a_client()
    {
        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartService(Fixture(Tier.Local), exitAfter: TimeSpan.FromSeconds(300));
        _ = await rig.UriAsync();
        await rig.QuiescentAsync();
        Assert.Equal([TimeSpan.FromSeconds(120)], rig.Time.History);

        rig.Time.Advance(TimeSpan.FromSeconds(120));

        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        Assert.Equal(["Warning: no browser connected to the dashboard within 120 s; the replay ends"], rig.Error.Lines());
    }

    // ---- registered keys (security) ----

    [Fact]
    public async Task A_registered_key_in_the_repository_link_is_never_served()
    {
        string key = "Zk" + Guid.NewGuid().ToString("N");
        Uri shipped = new(ConfigurationFixtures.ShippedOptions().Report.RepositoryUrl!, UriKind.Absolute);
        string link = new UriBuilder(shipped) { Path = "/" + key + "/x" }.Uri.AbsoluteUri;
        string path = ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Nvidia));
        int port = FreePort();
        using TempWorkspace workspace = TempWorkspace.Create();
        var overrides = new CliHostOverrides(
            Configuration: new Dictionary<string, string?> { ["NEBIUS_API_KEY"] = key, ["AgentJoules:Report:RepositoryUrl"] = link },
            WorkingDirectory: workspace.Root);

        CliRunResult result = await CliHarness.RunAsync($"dashboard --replay \"{path}\" --dashboard-port {port}", overrides).WaitAsync(HangGuard);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal($"error: {DashboardServer.SecretInPageMessage} (AgentJoulesException)", Assert.Single(result.ErrorLines));
        Assert.DoesNotContain("Dashboard:", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(key, result.Stdout + result.Stderr, StringComparison.Ordinal);
        using LoopbackPortBlock? nothingListens = LoopbackPortBlock.TryHold(port);
        Assert.NotNull(nothingListens);
    }

    [Fact]
    public async Task A_run_file_holding_a_registered_key_or_an_nvapi_key_is_refused_without_echoing_it()
    {
        string key = "Zk" + Guid.NewGuid().ToString("N");
        string nvidiaKey = string.Concat("nvapi-", Guid.NewGuid().ToString("N"));
        string name = RunDocumentFixtures.CommittedFileName(Tier.Nvidia);
        string original = File.ReadAllText(ReplayRig.FixturePath(name));
        foreach ((string label, string value) in new[] { ("registered", key), ("nvapi", nvidiaKey) })
        {
            using TempWorkspace workspace = TempWorkspace.Create();
            _ = workspace.WriteFile(name, RunDocumentFixtures.Edit(original, root => root["run"]!.AsObject()["gitCommit"] = "commit-" + value));
            var overrides = new CliHostOverrides(Configuration: new Dictionary<string, string?> { ["NEBIUS_API_KEY"] = key }, WorkingDirectory: workspace.Root);

            CliRunResult result = await CliHarness.RunAsync($"dashboard --replay {name} --dashboard-port {FreePort()}", overrides).WaitAsync(HangGuard);

            Assert.True(result.ExitCode == 2, label + ": " + result.Stderr);
            Assert.Equal($"{name}: (root) holds a registered key", Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r'));
            Assert.Equal($"error: dashboard --replay: {name} failed validation (ConfigurationException)", Assert.Single(result.ErrorLines));
            Assert.DoesNotContain(key, result.Stdout + result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(nvidiaKey["nvapi-".Length..], result.Stdout + result.Stderr, StringComparison.Ordinal);
        }
    }

    // The host settings of the dashboard's web host are process-wide environment variables, so this test runs alone with the AJ-091 ones.
    [Collection(DashboardSinkTests.HostSettingsGroup.Name)]
    [Trait("Spec", "REQ-032")]
    public sealed class HostSettings
    {
        [Fact]
        public async Task A_start_failure_that_is_not_a_busy_port_exits_5_with_its_own_reason()
        {
            string path = ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Nvidia));
            int port = FreePort();
            using TempWorkspace workspace = TempWorkspace.Create();
            const string variable = "DOTNET_hostBuilder__reloadConfigOnChange";
            string? previous = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, "maybe");
            try
            {
                CliRunResult result = await CliHarness.RunAsync($"dashboard --replay \"{path}\" --dashboard-port {port}", new CliHostOverrides(WorkingDirectory: workspace.Root)).WaitAsync(HangGuard);

                Assert.Equal(5, result.ExitCode);
                Assert.Equal("error: start failed (InvalidOperationException) (AgentJoulesException)", Assert.Single(result.ErrorLines));
                Assert.DoesNotContain("Dashboard:", result.Stdout, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
        }
    }

    // ---- the agent rule ----

    [Theory]
    [InlineData("planner")]
    [InlineData("PLANNER")]
    [InlineData("judge")]
    [InlineData("Planner ")]
    public async Task The_agent_rule_is_ordinal_and_case_sensitive(string agent)
    {
        string name = RunDocumentFixtures.CommittedFileName(Tier.Nvidia);
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(name)), root => SetFirstAgent(root, agent));
        using TempWorkspace workspace = TempWorkspace.Create();
        _ = workspace.WriteFile(name, text);

        CliRunResult result = await CliHarness.RunAsync($"dashboard --replay {name} --dashboard-port {FreePort()}", new CliHostOverrides(WorkingDirectory: workspace.Root)).WaitAsync(HangGuard);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal($"{name}: /steps/0/agent is not one of the configured agent roles", Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r'));
        Assert.Equal($"error: dashboard --replay: {name} has a step agent outside the configured roles (ConfigurationException)", Assert.Single(result.ErrorLines));
    }

    [Theory]
    [InlineData(Tier.Local)]
    [InlineData(Tier.Nvidia)]
    [InlineData(Tier.Nebius)]
    public async Task The_committed_fixtures_pass_the_agent_rules_and_replay(Tier tier)
    {
        string path = ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(tier));
        await using ReplayRig rig = ReplayRig.Create();

        Task<int> exit = rig.StartCommand($"dashboard --replay \"{path}\" --speed 50 --exit-after 1 --start-delay 0");
        await rig.DriveToEndAsync();

        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        Assert.Empty(rig.Error.Lines());
        Assert.Equal(["Dashboard"], rig.Output.Lines().Select(static line => line.Split(':')[0]));
    }

    [Fact]
    public async Task A_step_of_the_judge_identity_is_accepted()
    {
        string name = RunDocumentFixtures.CommittedFileName(Tier.Nvidia);
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(name)), static root => SetFirstAgent(root, "Judge"));
        using TempWorkspace files = TempWorkspace.Create();
        string path = files.WriteFile(name, text);
        await using ReplayRig rig = ReplayRig.Create();

        Task<int> exit = rig.StartCommand($"dashboard --replay \"{path}\" --speed 50 --exit-after 1 --start-delay 0");
        await rig.DriveToEndAsync();

        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        Assert.Empty(rig.Error.Lines());
        Assert.Equal(["Dashboard"], rig.Output.Lines().Select(static line => line.Split(':')[0]));
    }

    [Fact]
    public async Task An_environment_model_agent_outside_the_configured_roles_exits_2_without_echoing_it()
    {
        const string intruder = "Intruder-model-agent";
        string name = RunDocumentFixtures.CommittedFileName(Tier.Nvidia);
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(name)), static root => root["environment"]!["models"]!.AsArray()[1]!.AsObject()["agent"] = intruder);
        using TempWorkspace workspace = TempWorkspace.Create();
        _ = workspace.WriteFile(name, text);

        CliRunResult result = await CliHarness.RunAsync($"dashboard --replay {name} --dashboard-port {FreePort()}", new CliHostOverrides(WorkingDirectory: workspace.Root)).WaitAsync(HangGuard);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal($"{name}: /environment/models/1/agent is not one of the configured agent roles", Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r'));
        Assert.Equal($"error: dashboard --replay: {name} has an environment agent outside the configured roles (ConfigurationException)", Assert.Single(result.ErrorLines));
        Assert.DoesNotContain(intruder, result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public async Task At_most_20_agent_offenders_are_listed_and_the_rest_are_counted(int offenders, bool counted)
    {
        string name = RunDocumentFixtures.CommittedFileName(Tier.Nvidia);
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(name)), root =>
        {
            JsonArray steps = root["steps"]!.AsArray();
            for (int i = 0; i < offenders; i++)
            {
                JsonNode copy = steps[0]!.DeepClone();
                copy["agent"] = "Rogue" + i.ToString(CultureInfo.InvariantCulture);
                steps.Add(copy);
            }
        });
        using TempWorkspace workspace = TempWorkspace.Create();
        _ = workspace.WriteFile(name, text);

        CliRunResult result = await CliHarness.RunAsync($"dashboard --replay {name} --dashboard-port {FreePort()}", new CliHostOverrides(WorkingDirectory: workspace.Root)).WaitAsync(HangGuard);

        Assert.Equal(2, result.ExitCode);
        string[] lines = [.. result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => line.TrimEnd('\r'))];
        Assert.Equal(counted ? 21 : 20, lines.Length);
        Assert.Equal(20, lines.Count(static line => line.Contains("/agent is not one of the configured agent roles", StringComparison.Ordinal)));
        Assert.Equal(counted, lines.Contains($"{name}: (root) and 1 more failures"));
    }

    // ---- files that are not regular files, and Ctrl+C at every phase of the action ----

    [WindowsFact]
    public async Task A_named_pipe_is_refused_at_once_by_replay_and_by_validate()
    {
        // A pipe passes the name check and would block on its read: it is refused after the open, with the line of a file that could not be read.
        string name = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Random.Shared.Next(1, 86_000)).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-nvidia.json";
        string pipePath = Path.Combine(@"\\.\pipe", name);
        using var server = new NamedPipeServerStream(name, PipeDirection.Out, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // The server is listening, so the open succeeds and a read would wait for bytes that never come: only the refusal after the open ends the command.
        Task connected = server.WaitForConnectionAsync();
        using TempWorkspace workspace = TempWorkspace.Create();
        var overrides = new CliHostOverrides(WorkingDirectory: workspace.Root);

        CliRunResult replay = await CliHarness.RunAsync($"dashboard --replay \"{pipePath}\" --dashboard-port {FreePort()}", overrides).WaitAsync(HangGuard);

        // A second listening instance for validate, which reads the same path.
        using var secondServer = new NamedPipeServerStream(name, PipeDirection.Out, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task secondConnected = secondServer.WaitForConnectionAsync();
        CliRunResult validate = await CliHarness.RunAsync($"validate \"{pipePath}\"", overrides).WaitAsync(HangGuard);

        Assert.Equal(2, replay.ExitCode);
        Assert.Equal($"{name}: (root) could not be read", Assert.Single(replay.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r'));
        Assert.Equal($"error: dashboard --replay: {name} failed validation (ConfigurationException)", Assert.Single(replay.ErrorLines));
        Assert.Equal(2, validate.ExitCode);
        Assert.Contains($"{name}: (root) could not be read", validate.Stdout, StringComparison.Ordinal);
        await connected.WaitAsync(HangGuard);
        await secondConnected.WaitAsync(HangGuard);
    }

    [Fact]
    public async Task Cancellation_while_the_file_is_read_exits_0()
    {
        string path = ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Nvidia));
        using TempWorkspace workspace = TempWorkspace.Create();
        using var cancellation = new CancellationTokenSource();

        // The host started and the action begins with a token that is already cancelled: the read of the file is the phase that sees it.
        var overrides = new CliHostOverrides(WorkingDirectory: workspace.Root, OnHostStarted: _ => cancellation.Cancel());
        CliRunResult result = await CliHarness.RunAsync($"dashboard --replay \"{path}\" --dashboard-port {FreePort()}", overrides, cancellationToken: cancellation.Token).WaitAsync(HangGuard);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.DoesNotContain("Dashboard:", result.Stdout, StringComparison.Ordinal);
    }

    // ---- the order of the timeline: ties, zero-length steps, repeats, a reversed file ----

    [Fact]
    public void A_step_that_starts_where_the_previous_task_ended_follows_the_progress_of_its_task()
    {
        // The first step of api-migration starts at the instant the last step of incident-brief ends (7 s): end power, end of the step, progress, start of the step, its power.
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local))), static root =>
        {
            JsonArray steps = root["steps"]!.AsArray();
            steps[4]!.AsObject()["startUtc"] = steps[3]!["endUtc"]!.GetValue<string>();
        });
        RunDocument run = RunDocumentFixtures.Read(text);
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(run);

        TimeSpan tie = run.Steps[3].EndUtc - run.Run.StartUtc;
        string[] atTie = [.. entries.Where(entry => entry.Offset == tie).Select(static entry => Name(entry.Event))];
        Assert.Equal(["power", "step-completed", "progress", "step-started", "power"], atTie);
        ReplayEntry progress = Assert.Single(entries, static entry => entry.Event is ProgressEvent { Task: "api-migration" });
        Assert.Equal(tie, progress.Offset);
    }

    [Fact]
    public void A_file_with_its_steps_in_reverse_order_gives_the_same_timeline()
    {
        string original = File.ReadAllText(ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local)));
        RunDocument run = RunDocumentFixtures.Read(original);
        RunDocument reversed = RunDocumentFixtures.Read(RunDocumentFixtures.Edit(original, static root =>
        {
            JsonArray steps = root["steps"]!.AsArray();
            JsonNode[] copies = [.. steps.Select(static step => step!.DeepClone()).Reverse()];
            steps.Clear();
            foreach (JsonNode copy in copies)
            {
                steps.Add(copy);
            }

            root["judge"] = new JsonArray();
        }));

        (TimeSpan, string)[] expected = [.. ReplayTimeline.Build(run).Select(static entry => (entry.Offset, Name(entry.Event)))];
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(reversed);

        // The group of a (task, repeat) starts at its earliest step in time, the run completes at the maximum end, whatever the order of the steps in the file.
        Assert.Equal(expected, entries.Select(static entry => (entry.Offset, Name(entry.Event))));
        Assert.Equal([0, 8, 16], entries.Where(static entry => entry.Event is ProgressEvent).Select(static entry => (int)entry.Offset.TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(23), entries[^1].Offset);
    }

    [Fact]
    public void Repeats_are_pinned_in_the_progress_events()
    {
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local))), static root =>
        {
            root["run"]!.AsObject()["repeat"] = 2;
            JsonArray steps = root["steps"]!.AsArray();
            for (int i = 4; i < 8; i++)
            {
                steps[i]!.AsObject()["repeat"] = 2;
            }

            root["judge"] = new JsonArray();
        });
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(RunDocumentFixtures.Read(text));

        Assert.Equal(
            [("incident-brief", 1, 3, 1, 2), ("api-migration", 2, 3, 2, 2), ("energy-explainer", 3, 3, 1, 2)],
            entries.Select(static entry => entry.Event).OfType<ProgressEvent>().Select(static p => (p.Task, p.TaskIndex, p.TaskCount, p.Repeat, p.RepeatCount)));
    }

    [Fact]
    public void A_zero_length_last_step_is_not_left_running_after_the_run_completed()
    {
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local))), static root =>
        {
            JsonObject last = root["steps"]!.AsArray()[11]!.AsObject();
            last["startUtc"] = last["endUtc"]!.GetValue<string>();
        });
        RunDocument run = RunDocumentFixtures.Read(text);
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(run);

        // The step starts before it ends, and both come before the completion of the run.
        int started = IndexOf(entries, static e => e is StepStartedEvent { Step.Agent: "Reviewer", Step.Task: "energy-explainer" });
        int completed = IndexOf(entries, static e => e is StepCompletedEvent { Reading.Agent: "Reviewer", Reading.Task: "energy-explainer" });
        int done = IndexOf(entries, static e => e is RunCompletedEvent);
        Assert.True(started < completed && completed < done, $"{started} {completed} {done}");

        DashboardSnapshot snapshot = Applied(entries, int.MaxValue);
        Assert.Empty(snapshot.ActiveAgents);
        Assert.Equal(run.Steps[11].Outcome, snapshot.StatusOf("Reviewer"));
    }

    [Fact]
    public void A_step_wholly_before_the_start_of_the_run_is_not_left_running()
    {
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local))), static root =>
        {
            DateTimeOffset start = DateTimeOffset.Parse(root["run"]!["startUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            JsonObject first = root["steps"]!.AsArray()[0]!.AsObject();
            first["startUtc"] = start.AddSeconds(-6).ToString("O", CultureInfo.InvariantCulture);
            first["endUtc"] = start.AddSeconds(-5).ToString("O", CultureInfo.InvariantCulture);
        });
        RunDocument run = RunDocumentFixtures.Read(text);
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(run);

        Assert.Equal(TimeSpan.Zero, entries.First(static entry => entry.Event is StepCompletedEvent { Reading.Agent: "Planner" }).Offset);
        DashboardSnapshot atZero = Applied([.. entries.Where(static entry => entry.Offset == TimeSpan.Zero)], int.MaxValue);
        Assert.Empty(atZero.ActiveAgents);
        Assert.Equal(run.Steps[0].Outcome, atZero.StatusOf("Planner"));
    }

    // ---- helpers of the second part ----

    private static DashboardSnapshot Applied(IReadOnlyList<ReplayEntry> entries, int count)
    {
        DashboardSnapshot snapshot = DashboardFixtures.NewSnapshot();
        foreach (ReplayEntry entry in entries.Take(count))
        {
            _ = snapshot.Apply(entry.Event);
        }

        return snapshot;
    }

    private static int IndexOf(IReadOnlyList<ReplayEntry> entries, Func<LiveEvent, bool> match)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (match(entries[i].Event))
            {
                return i;
            }
        }

        return -1;
    }

    // The agent of the first step, and of the judge reading that scored it, so the pairing of the file stays valid.
    private static void SetFirstAgent(JsonObject root, string agent)
    {
        root["steps"]!.AsArray()[0]!.AsObject()["agent"] = agent;
        root["judge"]!.AsArray()[0]!.AsObject()["judgedAgent"] = agent;
    }

    // Replaces the steps by copies of the first one, all at the start of the run (zero-length) or all one second long, and drops the judge readings that pointed at them.
    private static void ManySteps(JsonObject root, int count, bool atRunStart)
    {
        JsonArray steps = root["steps"]!.AsArray();
        JsonNode template = steps[0]!.DeepClone();
        string start = root["run"]!["startUtc"]!.GetValue<string>();
        template["startUtc"] = start;
        template["endUtc"] = atRunStart ? start : Shifted(start, 1);
        steps.Clear();
        for (int i = 0; i < count; i++)
        {
            steps.Add(template.DeepClone());
        }

        root["judge"] = new JsonArray();
    }

    // A block of free loopback ports, released at once: the first port is where the command is told to look, and nothing may be left listening there.
    private static int FreePort()
    {
        using LoopbackPortBlock block = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        return block.First;
    }
}