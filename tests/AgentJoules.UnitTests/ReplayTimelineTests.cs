using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Cli;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Dashboard;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-092 (REQ-032): <c>dashboard --replay</c> serves the same sink, server and page as a live run and replays the recorded steps of a committed run file on their timeline. Every replay test
/// runs on a fake clock that moves only when the test advances it (P-90), the client connects before the clock moves, and every test that binds a port takes it from a
/// <see cref="LoopbackPortBlock"/> through <see cref="ReplayRig"/> and passes it explicitly (P-79). The only real-time values are hang guards that end a test that already failed.
/// </summary>
[Trait("Spec", "REQ-032")]
public sealed partial class ReplayTimelineTests
{
    private static readonly TimeSpan HangGuard = ReplayRig.HangGuard;
    private static readonly string[] Options = ["--replay", "--speed", "--exit-after", "--start-delay", "--dashboard-port"];

    // ---- criterion 1: one step-completed event per step, through the real command ----

    [Fact]
    public async Task Replay_emits_one_event_per_step()
    {
        const double speed = 50.0;
        TimeSpan exitAfter = TimeSpan.FromSeconds(5);
        string path = ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Nvidia));
        RunDocument run = Fixture(Tier.Nvidia);

        // The fixture precondition (P-88): the recorded span at the speed of the command fits inside --exit-after, so a change of the fixture fails here and not as a missing event.
        TimeSpan span = SpanOf(run);
        Assert.True(span / speed < exitAfter, $"The fixture spans {span.TotalSeconds} s, which at speed {speed} does not fit --exit-after {exitAfter.TotalSeconds} s.");

        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartCommand($"dashboard --replay \"{path}\" --speed 50 --exit-after 5");
        SseLog log = await rig.ConnectAsync();
        await rig.EventZeroAsync(log, startWait: exitAfter, timelineSpan: span / speed);
        await rig.DriveToEndAsync();

        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        await log.Completion.WaitAsync(HangGuard);
        Assert.Equal(run.Steps.Count, log.Count("step-completed"));
        Assert.Equal(12, log.Count("step-completed"));
        Assert.Equal(run.Steps.Count, log.Count("step-started"));
        Assert.Equal(1, log.Count("run-started"));
        Assert.Equal(1, log.Count("run-completed"));

        CliRunResult result = await rig.Command!;
        Assert.Empty(result.DeniedRequests);
        Assert.Empty(result.ErrorLines);
        Assert.Equal(["Dashboard"], rig.Output.Lines().Select(static line => line.Split(':')[0]));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public void Committed_fixtures_span_less_than_the_exit_after_of_the_exit_criterion()
    {
        // The exit criterion of the story: --speed 50 --exit-after 5 on the committed files. Measured here from the files, not assumed.
        foreach (Tier tier in new[] { Tier.Local, Tier.Nvidia, Tier.Nebius })
        {
            TimeSpan span = SpanOf(Fixture(tier));
            Assert.Equal(TimeSpan.FromSeconds(23), span);
            Assert.True(span / 50.0 < TimeSpan.FromSeconds(5));
        }
    }

    // ---- criterion 2: the timeline ----

    [Fact]
    public void Timeline_creates_events_at_the_recorded_offsets()
    {
        RunDocument run = Fixture(Tier.Local);
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(run);

        Assert.Equal(1 + 1 + 3 + 12 + 12 + 24 + 1, entries.Count);
        Assert.Equal(entries.OrderBy(static entry => entry.Offset).Select(static entry => entry.Offset), entries.Select(static entry => entry.Offset));

        // Run started at 0 with the file's run and environment, then the baseline at 0 because the file has one.
        RunStartedEvent started = Assert.IsType<RunStartedEvent>(entries[0].Event);
        Assert.Equal(TimeSpan.Zero, entries[0].Offset);
        Assert.Same(run.Run, started.Run);
        Assert.Same(run.Environment, started.Environment);
        Assert.Equal(run.Probe!.PowerLimitW, started.PowerLimitW);
        BaselineEvent baseline = Assert.IsType<BaselineEvent>(entries[1].Event);
        Assert.Equal(TimeSpan.Zero, entries[1].Offset);
        Assert.Same(run.Baseline, baseline.Baseline);

        // Progress at the first step of each (task, repeat): the three tasks start at 0, 8 and 16 s.
        (TimeSpan Offset, ProgressEvent Event)[] progress = [.. entries.Where(static entry => entry.Event is ProgressEvent).Select(static entry => (entry.Offset, (ProgressEvent)entry.Event))];
        Assert.Equal(
            [(0, "incident-brief", 1), (8, "api-migration", 2), (16, "energy-explainer", 3)],
            progress.Select(static item => ((int)item.Offset.TotalSeconds, item.Event.Task, item.Event.TaskIndex)));
        Assert.All(progress, item => Assert.Equal((3, 1, 1), (item.Event.TaskCount, item.Event.Repeat, item.Event.RepeatCount)));

        // Every step: started at step.startUtc, completed at step.endUtc, one power event at each end carrying the step's recorded avgW unchanged.
        foreach (StepReading step in run.Steps)
        {
            TimeSpan start = step.StartUtc - run.Run.StartUtc;
            TimeSpan end = step.EndUtc - run.Run.StartUtc;
            ReplayEntry started1 = Assert.Single(entries, entry => entry.Event is StepStartedEvent e && SameStep(e.Step, step));
            Assert.Equal(start, started1.Offset);
            Assert.Equal(start.TotalSeconds, ((StepStartedEvent)started1.Event).StartSeconds);
            ReplayEntry completed = Assert.Single(entries, entry => entry.Event is StepCompletedEvent e && ReferenceEquals(e.Reading, step));
            Assert.Equal(end, completed.Offset);
            PowerEvent[] power = [.. entries.Where(entry => entry.Event is PowerEvent p && (entry.Offset == start || entry.Offset == end) && ReferenceEquals(p.Watts, step.AvgW)).Select(static entry => (PowerEvent)entry.Event)];
            Assert.Equal(2, power.Length);
            Assert.Equal([start.TotalSeconds, end.TotalSeconds], power.Select(static p => p.SampleSeconds).Order());
        }

        // Run completed at the last endUtc with the file's run and totals.
        ReplayEntry last = entries[^1];
        RunCompletedEvent done = Assert.IsType<RunCompletedEvent>(last.Event);
        Assert.Equal(SpanOf(run), last.Offset);
        Assert.Same(run.Run, done.Run);
        Assert.Same(run.Totals, done.Totals);
        Assert.Equal(run.Run.Flags, done.Run.Flags);
    }

    [Theory]
    [InlineData(Tier.Local)]
    [InlineData(Tier.Nvidia)]
    [InlineData(Tier.Nebius)]
    public void Every_timeline_event_is_marked_replay(Tier tier)
    {
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(Fixture(tier));

        Assert.NotEmpty(entries);
        Assert.All(entries, static entry => Assert.True(entry.Event.Replay, entry.Event.GetType().Name));
    }

    [Fact]
    public void Timeline_ties_are_ordered_so_a_step_ends_before_the_next_starts()
    {
        // The second step starts at the instant the first one ends.
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local))), root =>
        {
            JsonArray steps = root["steps"]!.AsArray();
            steps[1]!.AsObject()["startUtc"] = steps[0]!["endUtc"]!.GetValue<string>();
        });
        RunDocument run = RunDocumentFixtures.Read(text);
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(run);

        TimeSpan tie = run.Steps[0].EndUtc - run.Run.StartUtc;
        string[] atTie = [.. entries.Where(entry => entry.Offset == tie).Select(static entry => Name(entry.Event))];
        Assert.Equal(["power", "step-completed", "step-started", "power"], atTie);

        // At offset 0: run started, baseline, power at the end of nothing is absent; progress before the step start, the step start before its power.
        string[] atZero = [.. entries.Where(static entry => entry.Offset == TimeSpan.Zero).Select(static entry => Name(entry.Event))];
        Assert.Equal(["run-started", "baseline", "progress", "step-started", "power"], atZero);
    }

    [Fact]
    public void Timeline_clamps_a_negative_offset_to_zero()
    {
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local))), root =>
        {
            JsonObject first = root["steps"]!.AsArray()[0]!.AsObject();
            DateTimeOffset before = DateTimeOffset.Parse(root["run"]!["startUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture).AddSeconds(-5);
            first["startUtc"] = before.ToString("O", CultureInfo.InvariantCulture);
        });
        RunDocument run = RunDocumentFixtures.Read(text);
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(run);

        Assert.All(entries, static entry => Assert.True(entry.Offset >= TimeSpan.Zero));
        StepStartedEvent first = Assert.Single(entries.Select(static entry => entry.Event).OfType<StepStartedEvent>(), static e => e.Step.Agent == "Planner" && e.Step.Task == "incident-brief");
        Assert.Equal(0.0, first.StartSeconds);
    }

    [Fact]
    public void Timeline_gives_no_event_for_judge_readings()
    {
        RunDocument run = Fixture(Tier.Nvidia);
        Assert.NotEmpty(run.Judge);
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(run);

        Assert.DoesNotContain(entries, entry => entry.Event is StepCompletedEvent { Reading.JudgedAgent: not null });
        Assert.DoesNotContain(entries, entry => entry.Event is StepStartedEvent { Step.IsJudge: true });
        Assert.Equal(run.Steps.Count, entries.Count(static entry => entry.Event is StepCompletedEvent));
    }

    [Fact]
    public void Timeline_replays_recorded_flags_and_reasons_without_adding_any()
    {
        RunDocument run = Fixture(Tier.Nvidia);
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(run);

        StepCompletedEvent[] completed = [.. entries.Select(static entry => entry.Event).OfType<StepCompletedEvent>()];
        Assert.Equal(run.Steps.Count, completed.Length);
        Assert.All(completed, e => Assert.Contains(run.Steps, step => ReferenceEquals(step, e.Reading)));

        // The quality reason of the judge failure is data of the reading, kept as recorded.
        StepCompletedEvent failed = Assert.Single(completed, static e => e.Reading.Quality.Reason is { } reason && reason.StartsWith("judge-failed", StringComparison.Ordinal));
        Assert.Equal(MetricLabel.NotApplicable, failed.Reading.Quality.Label);
        Assert.Null(failed.Reading.Quality.Value);
        RunCompletedEvent done = Assert.Single(entries.Select(static entry => entry.Event).OfType<RunCompletedEvent>());
        Assert.Equal(run.Run.Flags, done.Run.Flags);
    }

    [Fact]
    public void Timeline_is_a_pure_function_of_the_run()
    {
        RunDocument run = Fixture(Tier.Local);

        IReadOnlyList<ReplayEntry> first = ReplayTimeline.Build(run);
        IReadOnlyList<ReplayEntry> second = ReplayTimeline.Build(run);

        Assert.Equal(first, second);
        _ = Assert.Throws<ArgumentNullException>(static () => ReplayTimeline.Build(null!));
    }

    // ---- criterion 5: a hosted fixture replays power as N/A, never as a number ----

    [Theory]
    [InlineData(Tier.Nvidia)]
    [InlineData(Tier.Nebius)]
    public void Hosted_replay_shows_power_as_NA_not_zero(Tier tier)
    {
        RunDocument run = Fixture(tier);
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(run);

        PowerEvent[] power = [.. entries.Select(static entry => entry.Event).OfType<PowerEvent>()];
        Assert.Equal(2 * run.Steps.Count, power.Length);
        Assert.All(power, static p =>
        {
            Assert.Equal(MetricLabel.NotApplicable, p.Watts.Label);
            Assert.Equal("hosted-tier", p.Watts.Reason);
            Assert.Null(p.Watts.Value);
        });

        // A file without a baseline gives no baseline event: nothing is invented.
        Assert.Null(run.Baseline);
        Assert.DoesNotContain(entries, static entry => entry.Event is BaselineEvent);
        Assert.Null(((RunStartedEvent)entries[0].Event).PowerLimitW);
    }

    [Fact]
    public void Page_shows_the_replay_banner_and_marker_from_the_replay_flag()
    {
        string page = DashboardPage.Render(null);
        string script = DashboardPageRules.Script(page);

        Assert.Contains("<span id=\"hdr-replay-marker\" class=\"banner\" hidden>replay</span>", page, StringComparison.Ordinal);
        Assert.Contains("<span id=\"hdr-replay\" class=\"banner\" hidden>power shown as recorded per-step average</span>", page, StringComparison.Ordinal);

        // The banner and the marker are fixed texts chosen by the boolean of the latest run-started or snapshot, which the two handlers copy from the envelope.
        Assert.Contains("state.replay = item.replay === true;", DashboardPageRules.FunctionBody(page, DashboardPageRules.SnapshotHandler), StringComparison.Ordinal);
        Assert.Contains("state.replay = item.replay === true;", DashboardPageRules.FunctionBody(page, "handleRunStarted"), StringComparison.Ordinal);
        Assert.Equal(2, DashboardPageRules.CountOf(script, "state.replay = item.replay === true;"));
        Assert.Contains("hdr.replay.hidden = !state.replay;", script, StringComparison.Ordinal);
        Assert.Contains("hdr.replayMarker.hidden = !state.replay;", script, StringComparison.Ordinal);
    }

    // ---- criterion 3: --speed ----

    [Fact]
    public async Task Speed_divides_the_recorded_delays()
    {
        // The first two steps start 8 seconds apart (every later step is moved after them).
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local))), static root =>
        {
            JsonArray steps = root["steps"]!.AsArray();
            for (int i = 1; i < steps.Count; i++)
            {
                JsonObject step = steps[i]!.AsObject();
                step["startUtc"] = Shifted(step["startUtc"]!.GetValue<string>(), 6);
                step["endUtc"] = Shifted(step["endUtc"]!.GetValue<string>(), 6);
            }
        });
        RunDocument run = RunDocumentFixtures.Read(text);
        Assert.Equal(TimeSpan.FromSeconds(8), run.Steps[1].StartUtc - run.Steps[0].StartUtc);

        // Every wait of the replay is the recorded gap to the previous entry divided by the speed: the first two are 1 s and 7 s, which at speed 4 are 0.25 s and 1.75 s.
        const double factor = 4.0;
        List<TimeSpan> expected = [];
        TimeSpan previous = TimeSpan.Zero;
        foreach (ReplayEntry entry in ReplayTimeline.Build(run))
        {
            if (entry.Offset > previous)
            {
                expected.Add((entry.Offset - previous) / factor);
            }

            previous = entry.Offset;
        }

        Assert.Equal([TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(1.75)], expected.Take(2));
        Assert.Equal(23, expected.Count);

        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartService(run, speed: factor);
        SseLog log = await rig.ConnectAsync();
        await rig.EventZeroAsync(log, startWait: TimeSpan.FromSeconds(120), timelineSpan: TimeSpan.Zero);
        await rig.AdvanceAsync(expected.Count);
        await log.WaitForCountAsync("run-completed", 1, HangGuard);

        SseReceipt[] started = [.. log.Items.Where(static item => item.Type == "step-started")];
        Assert.Equal(TimeSpan.FromSeconds(2), started[1].AtUtc - started[0].AtUtc);
        Assert.Equal(expected, rig.Time.History.Where(static wait => wait != TimeSpan.FromSeconds(120)).Take(expected.Count));
        rig.Cancel();
        Assert.Equal(0, await exit.WaitAsync(HangGuard));
    }

    [Theory]
    [InlineData("--speed 0")]
    [InlineData("--speed -1")]
    [InlineData("--speed NaN")]
    [InlineData("--speed Infinity")]
    [InlineData("--speed fast")]
    [InlineData("--speed 4294967.295")]
    [InlineData("--exit-after 0")]
    [InlineData("--exit-after -2")]
    [InlineData("--exit-after 4294967.295")]
    [InlineData("--exit-after 1e-8")]
    [InlineData("--start-delay -1")]
    [InlineData("--start-delay NaN")]
    [InlineData("--start-delay 4294967.295")]
    [InlineData("--start-delay 1e-8")]
    public async Task Out_of_range_numbers_exit_2_as_a_parse_error(string option)
    {
        CliRunResult result = await CliHarness.RunAsync("dashboard --replay 20260101T000000Z-local.json " + option);

        Assert.Equal(2, result.ExitCode);
        string line = Assert.Single(result.ErrorLines);
        Assert.EndsWith("(ParseError)", line, StringComparison.Ordinal);
        Assert.Contains(option.Split(' ')[0], line, StringComparison.Ordinal);
        Assert.Empty(result.DeniedRequests);
        Assert.Empty(result.Stdout);
    }

    [Theory]
    [InlineData("--speed 4294967.294")]
    [InlineData("--speed 0.001")]
    [InlineData("--exit-after 4294967.294")]
    [InlineData("--start-delay 0")]
    [InlineData("--start-delay 4294967.294")]
    public async Task Boundary_numbers_pass_the_parser(string option)
    {
        // The file is missing, so the command ends later and differently: a configuration failure, never a parse error.
        using TempWorkspace workspace = TempWorkspace.Create();
        CliRunResult result = await CliHarness.RunAsync("dashboard --replay 20260101T000000Z-local.json " + option, new CliHostOverrides(WorkingDirectory: workspace.Root));

        Assert.Equal(2, result.ExitCode);
        Assert.EndsWith("(ConfigurationException)", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("port")]
    public async Task Dashboard_port_outside_1_to_65535_exits_2(string port)
    {
        CliRunResult result = await CliHarness.RunAsync("dashboard --replay 20260101T000000Z-local.json --dashboard-port " + port);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--dashboard-port must be a whole number from 1 to 65535", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_replay_option_exits_2()
    {
        CliRunResult result = await CliHarness.RunAsync("dashboard");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--replay", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
    }

    // ---- criterion 4: the file and the port ----

    [Fact]
    public async Task Invalid_run_file_name_exits_2_naming_the_file()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        _ = workspace.WriteFile("results.json", File.ReadAllText(ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local))));

        CliRunResult result = await CliHarness.RunAsync("dashboard --replay results.json", new CliHostOverrides(WorkingDirectory: workspace.Root));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("error: dashboard --replay: results.json is not a run file name (ConfigurationException)", Assert.Single(result.ErrorLines));
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task File_that_fails_the_schema_exits_2_with_the_validate_lines()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        _ = workspace.WriteFile(name, "{}");

        CliRunResult result = await CliHarness.RunAsync($"dashboard --replay {name}", new CliHostOverrides(WorkingDirectory: workspace.Root));

        Assert.Equal(2, result.ExitCode);
        Assert.NotEmpty(result.Stdout);
        Assert.All(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith(name + ": ", line.TrimEnd('\r'), StringComparison.Ordinal));
        Assert.Equal($"error: dashboard --replay: {name} failed validation (ConfigurationException)", Assert.Single(result.ErrorLines));
    }

    [Fact]
    public async Task Missing_run_file_exits_2_naming_the_file()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);

        CliRunResult result = await CliHarness.RunAsync($"dashboard --replay {name}", new CliHostOverrides(WorkingDirectory: workspace.Root));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"{name}: (root) could not be read", result.Stdout, StringComparison.Ordinal);
        Assert.Equal($"error: dashboard --replay: {name} failed validation (ConfigurationException)", Assert.Single(result.ErrorLines));
    }

    [Fact]
    public async Task Step_agent_outside_the_configured_roles_exits_2_without_echoing_it()
    {
        const string intruder = "Intruder-agent";
        string name = RunDocumentFixtures.CommittedFileName(Tier.Nvidia);
        string text = RunDocumentFixtures.Edit(File.ReadAllText(ReplayRig.FixturePath(name)), static root =>
        {
            root["steps"]!.AsArray()[0]!.AsObject()["agent"] = intruder;
            root["judge"]!.AsArray()[0]!.AsObject()["judgedAgent"] = intruder;
        });
        using TempWorkspace workspace = TempWorkspace.Create();
        _ = workspace.WriteFile(name, text);

        CliRunResult result = await CliHarness.RunAsync($"dashboard --replay {name}", new CliHostOverrides(WorkingDirectory: workspace.Root));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal($"{name}: /steps/0/agent is not one of the configured agent roles", Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r'));
        Assert.Equal($"error: dashboard --replay: {name} has a step agent outside the configured roles (ConfigurationException)", Assert.Single(result.ErrorLines));
        Assert.DoesNotContain(intruder, result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_free_port_exits_5_with_the_range()
    {
        using LoopbackPortBlock held = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        string path = ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Nvidia));
        using TempWorkspace workspace = TempWorkspace.Create();

        CliRunResult result = await CliHarness.RunAsync($"dashboard --replay \"{path}\" --exit-after 1 --dashboard-port {held.First}", new CliHostOverrides(WorkingDirectory: workspace.Root));

        Assert.Equal(5, result.ExitCode);
        Assert.Equal($"error: no free dashboard port in {held.First}-{held.Last} (AgentJoulesException)", Assert.Single(result.ErrorLines));
        Assert.DoesNotContain("Dashboard:", result.Stdout, StringComparison.Ordinal);
    }

    // ---- criterion 6: Ctrl+C and the keys ----

    [Fact]
    public async Task Replay_cancellation_exits_0()
    {
        string path = ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local));
        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartCommand($"dashboard --replay \"{path}\"");
        string[] filesBefore = FilesOf(rig.WorkspaceRoot!);
        SseLog log = await rig.ConnectAsync();
        await rig.EventZeroAsync(log, startWait: TimeSpan.FromSeconds(120), timelineSpan: TimeSpan.Zero);

        // The replay is in progress with a connected client: three waits (1, 2 and 3 s of the recorded timeline) passed.
        await rig.AdvanceAsync(3);
        await log.WaitForCountAsync("step-completed", 1, HangGuard);
        Assert.False(exit.IsCompleted);

        rig.Cancel();

        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        CliRunResult result = await rig.Command!;
        Assert.Empty(result.ErrorLines);
        Assert.Empty(result.Stderr);
        Assert.True(result.ConsoleRestored);
        Assert.Empty(result.DeniedRequests);
        await log.Completion.WaitAsync(HangGuard);
        Assert.Equal(filesBefore, FilesOf(rig.WorkspaceRoot!));
    }

    [Fact]
    public async Task Without_exit_after_the_server_runs_until_cancelled_and_exits_0()
    {
        RunDocument run = Fixture(Tier.Nvidia);
        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartService(run, speed: 50.0, startDelay: TimeSpan.Zero);
        _ = await rig.UriAsync();

        // The timeline plays through (an in-process subscriber watches it end), and the server keeps serving after its last event.
        using DashboardConnection watcher = rig.Sink.Connect();
        int waits = ReplayTimeline.Build(run).Select(static entry => entry.Offset).Distinct().Count(static offset => offset > TimeSpan.Zero);
        await rig.AdvanceAsync(waits);
        await WaitForPayloadAsync(watcher, "run-completed");
        Assert.False(exit.IsCompleted);

        rig.Cancel();

        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        Assert.Empty(rig.Error.Lines());
    }

    [Fact]
    public async Task Replay_needs_no_key()
    {
        string path = ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Nvidia));
        await using ReplayRig rig = ReplayRig.Create();

        // The harness is deny-by-default: no key is configured, no environment variable is read, and every real HTTP handler is replaced by one that refuses and records.
        Task<int> exit = rig.StartCommand($"dashboard --replay \"{path}\" --speed 50 --exit-after 1 --start-delay 0");
        await rig.DriveToEndAsync();

        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        CliRunResult result = await rig.Command!;
        Assert.Empty(result.DeniedRequests);
        Assert.Empty(result.Stderr);
        Assert.DoesNotContain("KEY", result.Stdout + result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    // ---- criteria 7, 8 and 9: the start, the timeout and the clock of --exit-after ----

    [Fact]
    public async Task No_event_before_first_client()
    {
        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartService(Fixture(Tier.Local));
        _ = await rig.UriAsync();
        await rig.QuiescentAsync();

        // Thirty seconds pass on the fake clock with the Dashboard line printed and no client: the 120 s wait is armed, and nothing was emitted.
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await rig.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        Assert.Equal([TimeSpan.FromSeconds(120)], rig.Time.History);
        Assert.False(exit.IsCompleted);

        using DashboardConnection first = rig.Sink.Connect();
        Assert.Equal(0, first.AppliedAtConnect);
        Assert.Equal(["Dashboard"], rig.Output.Lines().Select(static line => line.Split(':')[0]));

        // After the first client connected the first event is emitted, at the instant of the connection on the fake clock.
        JsonElement event0 = JsonOf(await first.Events.ReadAsync().AsTask().WaitAsync(HangGuard));
        Assert.Equal("run-started", event0.GetProperty("type").GetString());
        Assert.True(event0.GetProperty("replay").GetBoolean());
        Assert.Equal(rig.Time.GetUtcNow(), event0.GetProperty("atUtc").GetDateTimeOffset());
        rig.Cancel();
        Assert.Equal(0, await exit.WaitAsync(HangGuard));
    }

    [Fact]
    public async Task Start_delay_starts_the_replay_without_a_client()
    {
        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartService(Fixture(Tier.Local), startDelay: TimeSpan.FromSeconds(30));
        _ = await rig.UriAsync();
        await rig.QuiescentAsync();
        Assert.Equal([TimeSpan.FromSeconds(30)], rig.Time.History);

        // The wait of the delay is advanced by hand below: take it off the queue of the driver, so the next quiescence is the next wait of the replay and not this one.
        Assert.True(rig.Time.DueTimes.TryRead(out TimeSpan armed));
        Assert.Equal(TimeSpan.FromSeconds(30), armed);

        // Nothing is emitted while the delay runs.
        await rig.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        Assert.False(exit.IsCompleted);

        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await rig.QuiescentAsync();
        await rig.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        using DashboardConnection late = rig.Sink.Connect();

        // Event 0 and what the recorded timeline holds at offset 0 (run started, baseline, progress, the first step start and its power) were emitted at the end of the delay.
        Assert.Equal(5, late.AppliedAtConnect);
        Assert.Equal("local", JsonOf(late.Snapshot).GetProperty("data").GetProperty("run").GetProperty("tier").GetString());
        rig.Cancel();
        Assert.Equal(0, await exit.WaitAsync(HangGuard));
    }

    [Fact]
    public async Task Start_delay_of_zero_starts_at_once()
    {
        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartService(Fixture(Tier.Local), startDelay: TimeSpan.Zero);
        _ = await rig.UriAsync();
        await rig.QuiescentAsync();
        await rig.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        using DashboardConnection late = rig.Sink.Connect();
        Assert.Equal(5, late.AppliedAtConnect);
        Assert.Equal([TimeSpan.FromSeconds(1)], rig.Time.History);
        rig.Cancel();
        Assert.Equal(0, await exit.WaitAsync(HangGuard));
    }

    [Fact]
    public async Task An_earlier_client_cuts_a_longer_start_delay_short()
    {
        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartService(Fixture(Tier.Local), startDelay: TimeSpan.FromSeconds(30));
        _ = await rig.UriAsync();
        await rig.QuiescentAsync();

        SseLog log = await rig.ConnectAsync();
        await log.WaitForCountAsync("run-started", 1, HangGuard);

        // No time passed: the clock still reads the instant at which the replay started.
        Assert.Equal(rig.Time.GetUtcNow(), log.Items.First(static item => item.Type == "run-started").AtUtc);
        Assert.Empty(rig.Error.Lines());
        rig.Cancel();
        Assert.Equal(0, await exit.WaitAsync(HangGuard));
    }

    [Fact]
    public async Task No_client_times_out_with_warning()
    {
        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartService(Fixture(Tier.Local), exitAfter: TimeSpan.FromSeconds(60));
        _ = await rig.UriAsync();
        await rig.QuiescentAsync();
        Assert.Equal([TimeSpan.FromSeconds(60)], rig.Time.History);

        rig.Time.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        Assert.Equal(["Warning: no browser connected to the dashboard within 60 s; the replay ends"], rig.Error.Lines());
        Assert.Equal(["Dashboard"], rig.Output.Lines().Select(static line => line.Split(':')[0]));
    }

    [Fact]
    public async Task No_client_without_exit_after_waits_120_seconds()
    {
        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartService(Fixture(Tier.Local));
        _ = await rig.UriAsync();
        await rig.QuiescentAsync();

        rig.Time.Advance(TimeSpan.FromSeconds(119));

        // One second short of the wait: the replay is still waiting (a real 100 ms is the only way to show that a task does not end).
        Task finished = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.NotSame(exit, finished);
        Assert.Empty(rig.Error.Lines());

        rig.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        Assert.Equal(["Warning: no browser connected to the dashboard within 120 s; the replay ends"], rig.Error.Lines());
    }

    [Fact]
    public async Task Exit_after_counts_from_event_zero_in_wall_seconds_not_scaled_by_speed()
    {
        await using ReplayRig rig = ReplayRig.Create();
        DateTimeOffset origin = rig.Time.GetUtcNow();

        // Ten seconds of start delay do not count; two seconds of --exit-after do, at speed 4 as at speed 1.
        Task<int> exit = rig.StartService(Fixture(Tier.Local), speed: 4.0, exitAfter: TimeSpan.FromSeconds(2), startDelay: TimeSpan.FromSeconds(10));
        _ = await rig.UriAsync();
        await rig.DriveToEndAsync();

        Assert.Equal(0, await exit.WaitAsync(HangGuard));
        Assert.Equal(TimeSpan.FromSeconds(12), rig.Time.GetUtcNow() - origin);
        Assert.Empty(rig.Error.Lines());
    }

    // ---- criterion 9: a reconnecting client ----

    [Fact]
    public async Task Reconnect_rebuilds_from_snapshot()
    {
        RunDocument run = Fixture(Tier.Local);
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(run);
        TimeSpan sixth = entries.Where(static entry => entry.Event is StepCompletedEvent).ElementAt(5).Offset;
        int waits = entries.Select(static entry => entry.Offset).Distinct().Count(offset => offset > TimeSpan.Zero && offset <= sixth);

        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartService(run);
        SseLog first = await rig.ConnectAsync();
        await rig.EventZeroAsync(first, startWait: TimeSpan.FromSeconds(120), timelineSpan: TimeSpan.Zero);
        await rig.AdvanceAsync(waits);
        await first.WaitForCountAsync("step-completed", 6, HangGuard);
        await rig.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        // The state of the dashboard at the instant the client drops.
        string before;
        using (DashboardConnection probe = rig.Sink.Connect())
        {
            before = probe.Snapshot;
        }

        await rig.DisconnectAsync(first, 0);
        SseLog second = await rig.ConnectAsync();
        await second.WaitForAsync(static items => items.Count >= 1, HangGuard);

        // Its first item is a snapshot, and its data is the state of the dropped client after 6 of the 12 steps (atUtc is compared nowhere: P-90).
        SseReceipt snapshot = second.Items[0];
        Assert.Equal("snapshot", snapshot.Type);
        Assert.Equal(DataOf(before), DataOf(snapshot.Json));
        Assert.Equal(6, JsonOf(snapshot.Json).GetProperty("data").GetProperty("steps").GetArrayLength());
        Assert.Equal(6, first.Count("step-completed"));
        rig.Cancel();
        Assert.Equal(0, await exit.WaitAsync(HangGuard));
    }

    // ---- criterion 10 and the page ----

    [Fact]
    public async Task Help_lists_the_five_options()
    {
        CliRunResult result = await CliHarness.RunAsync("dashboard --help");

        Assert.Equal(0, result.ExitCode);
        foreach (string option in Options)
        {
            Assert.Contains(option, result.Stdout, StringComparison.Ordinal);
        }

        Assert.Contains(DashboardServer.DefaultPort.ToString(CultureInfo.InvariantCulture), result.Stdout, StringComparison.Ordinal);
        Assert.Empty(result.DeniedRequests);

        CliRunResult root = await CliHarness.RunAsync("--help");
        Assert.Contains("dashboard", root.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_serves_the_page_with_the_repository_link_of_the_default_configuration()
    {
        string path = ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(Tier.Local));
        await using ReplayRig rig = ReplayRig.Create();
        Task<int> exit = rig.StartCommand($"dashboard --replay \"{path}\"");
        Uri uri = await rig.UriAsync();

        using JsonDocument configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.WorkspaceRoot!, "config", "appsettings.json")));
        string link = configuration.RootElement.GetProperty("AgentJoules").GetProperty("Report").GetProperty("RepositoryUrl").GetString()!;
        HeadlessResponse page = await rig.Client.GetAsync(uri, "/", hostHeader: null).WaitAsync(HangGuard);

        Assert.Equal(200, page.Status);
        Assert.Equal(DashboardPage.Render(new Uri(link, UriKind.Absolute)), page.Body);
        Assert.Contains("replay", page.Body, StringComparison.Ordinal);
        rig.Cancel();
        Assert.Equal(0, await exit.WaitAsync(HangGuard));
    }

    // ---- helpers ----

    private static RunDocument Fixture(Tier tier) => RunDocumentFixtures.Read(File.ReadAllText(ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(tier))));

    private static TimeSpan SpanOf(RunDocument run) => run.Steps.Max(static step => step.EndUtc) - run.Run.StartUtc;

    private static bool SameStep(StepIdentity identity, StepReading step) =>
        identity.Agent == step.Agent && identity.Task == step.Task && identity.Repeat == step.Repeat && identity.ModelId == step.ModelId && identity.Tier == step.Tier && !identity.IsJudge;

    private static string Name(LiveEvent liveEvent) => liveEvent switch
    {
        RunStartedEvent => "run-started",
        BaselineEvent => "baseline",
        ProgressEvent => "progress",
        StepStartedEvent => "step-started",
        StepProgressEvent => "step-progress",
        StepCompletedEvent => "step-completed",
        PowerEvent => "power",
        RunCompletedEvent => "run-completed",
        _ => throw new InvalidOperationException("Unknown live event " + liveEvent.GetType().Name),
    };

    private static string Shifted(string utc, int seconds) =>
        DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture).AddSeconds(seconds).ToString("O", CultureInfo.InvariantCulture);

    private static async Task WaitForPayloadAsync(DashboardConnection connection, string type)
    {
        using var guard = new CancellationTokenSource(HangGuard);
        while (await connection.Events.WaitToReadAsync(guard.Token))
        {
            while (connection.Events.TryRead(out string? payload))
            {
                if (JsonOf(payload).GetProperty("type").GetString() == type)
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException("The connection ended before a " + type + " payload arrived.");
    }

    private static JsonElement JsonOf(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string DataOf(string envelope) => JsonOf(envelope).GetProperty("data").GetRawText();

    private static string[] FilesOf(string root) => [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal)];
}