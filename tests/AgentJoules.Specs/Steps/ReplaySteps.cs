using System.Globalization;
using System.Text.Json;

using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Dashboard;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-092: <c>dashboard --replay</c> plays a committed run file back on its recorded timeline (REQ-032). Scoped to the feature because sentences such as "the exit code is 0", "is executed" and
/// "the timeline is built" are reused by other stories with other meanings. The state lives in <see cref="ReplayContext"/>; the sink, the server, the command and the page are the real ones,
/// every port is from a <see cref="LoopbackPortBlock"/> and every HTTP request goes through the one <see cref="HeadlessSseClient"/> of the rig. Every scenario runs on a fake clock that moves only
/// when a step advances it (P-90).
/// </summary>
[Binding]
[Scope(Feature = "AJ-092 Replay a committed run file on its recorded timeline")]
public sealed class ReplaySteps(ReplayContext context)
{
    private static readonly TimeSpan HangGuard = ReplayRig.HangGuard;
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(120);

    private readonly ReplayContext _context = context;

    private ReplayRig Rig => _context.Rig ?? throw new XunitException("The scenario has no replay rig.");

    private RunDocument Run => _context.Run ?? throw new XunitException("The scenario has no fixture run file.");

    [AfterScenario]
    public async Task DisposeTheRig() => await _context.DisposeAsync();

    // ---- the fixture ----

    [Given(@"^a fixture run file with (\d+) steps$")]
    public void GivenAFixtureRunFileWithSteps(int steps)
    {
        // The nvidia file is the one of the exit criterion; it holds one step per agent of each of the three committed tasks.
        _context.UseFixture(Tier.Nvidia);
        Assert.Equal(steps, Run.Steps.Count);
    }

    [Given(@"^a fixture run file$")]
    public void GivenAFixtureRunFile() => _context.UseFixture(Tier.Local);

    [Given(@"^a nvidia fixture run file$")]
    public void GivenANvidiaFixtureRunFile() => _context.UseFixture(Tier.Nvidia);

    [Given(@"^a fixture whose first two steps start (\d+) seconds apart$")]
    public void GivenAFixtureWhoseFirstTwoStepsStartApart(int seconds)
    {
        _context.UseShiftedFixture();
        Assert.Equal(TimeSpan.FromSeconds(seconds), Run.Steps[1].StartUtc - Run.Steps[0].StartUtc);
    }

    [Given(@"^a file whose name does not match the run-file pattern$")]
    public void GivenAFileWhoseNameDoesNotMatchTheRunFilePattern()
    {
        _context.UseFixture(Tier.Local);
        _context.UseFileWithAnInvalidName();
    }

    [Given(@"^listeners hold (\d+) consecutive ports starting at the preferred port$")]
    public void GivenListenersHoldPorts(int count)
    {
        // The command line of the scenario names no port: the run step appends --dashboard-port with the first held port (P-79).
        _context.UseFixture(Tier.Nvidia);
        _context.HoldPorts(count);
    }

    // ---- the timeline ----

    [When(@"^the timeline is built$")]
    public void WhenTheTimelineIsBuilt() => _context.Entries = ReplayTimeline.Build(Run);

    [Then(@"^every event has replay true and the page shows the replay banner$")]
    public void ThenEveryEventHasReplayTrueAndThePageShowsTheBanner()
    {
        Assert.NotEmpty(_context.Entries);
        Assert.All(_context.Entries, static entry => Assert.True(entry.Event.Replay, entry.Event.GetType().Name));

        // The banner and the marker are fixed texts of the page that the replay flag of the latest run-started or snapshot shows.
        string page = DashboardPage.Render(null);
        string script = DashboardPageRules.Script(page);
        Assert.Contains("<span id=\"hdr-replay\" class=\"banner\" hidden>power shown as recorded per-step average</span>", page, StringComparison.Ordinal);
        Assert.Contains("<span id=\"hdr-replay-marker\" class=\"banner\" hidden>replay</span>", page, StringComparison.Ordinal);
        Assert.Contains("hdr.replay.hidden = !state.replay;", script, StringComparison.Ordinal);
        Assert.Contains("hdr.replayMarker.hidden = !state.replay;", script, StringComparison.Ordinal);
        Assert.Equal(2, DashboardPageRules.CountOf(script, "state.replay = item.replay === true;"));
    }

    [Then(@"^every power event has label ""([^""]*)"" and a null value$")]
    public void ThenEveryPowerEventHasLabelAndANullValue(string label)
    {
        PowerEvent[] power = [.. _context.Entries.Select(static entry => entry.Event).OfType<PowerEvent>()];
        Assert.Equal(2 * Run.Steps.Count, power.Length);
        Assert.All(power, p =>
        {
            Assert.Equal(label, MetricLabelText.Of(p.Watts.Label));
            Assert.Equal("hosted-tier", p.Watts.Reason);
            Assert.Null(p.Watts.Value);
        });
    }

    // ---- the command ----

    [When(@"^""([^""]*)"" is executed$")]
    public async Task WhenCommandIsExecuted(string arguments) => await _context.RunAsync(arguments);

    [When(@"^""([^""]*)"" is executed and a client reads ""([^""]*)""$")]
    public async Task WhenCommandIsExecutedAndAClientReads(string arguments, string path)
    {
        Assert.Equal(HeadlessSseClient.EventsPath, path);
        await _context.RunWithClientAsync(arguments);
    }

    [Then(@"^the client received (\d+) step-completed events$")]
    public void ThenTheClientReceivedStepCompletedEvents(int count)
    {
        SseLog log = _context.Log ?? throw new XunitException("No client read the stream.");
        string received = string.Join(", ", log.Items.Select(static item => item.Type));
        Assert.True(count == log.Count("step-completed"), $"Expected {count} step-completed events; exit {_context.ExitCode}; stderr: {_context.Result?.Stderr}; stdout: {_context.Result?.Stdout}; received: {received}");
        Assert.Equal(Run.Steps.Count, count);
        Assert.Equal(count, log.Count("step-started"));
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        int actual = _context.ExitCode ?? throw new XunitException("The replay has not ended.");
        string stderr = _context.Result?.Stderr ?? Rig.Error.Text;
        Assert.True(expected == actual, $"Expected exit code {expected} but got {actual}; stderr: {stderr}");
        if (_context.Result is { } result)
        {
            Assert.Empty(result.DeniedRequests);
        }

        if (expected == 5)
        {
            ThenTheMessageNamesThePortRange();
        }
    }

    [Then(@"^the exit code is 2 and the output names the file$")]
    public void ThenTheExitCodeIs2AndTheOutputNamesTheFile()
    {
        CliRunResult result = _context.Result ?? throw new XunitException("The command has not run yet.");
        Assert.True(result.ExitCode == 2, result.Stderr);
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains(ReplayContext.NotARunFileName, line, StringComparison.Ordinal);
        Assert.Contains("is not a run file name", line, StringComparison.Ordinal);
    }

    // ---- speed ----

    [When(@"^the timeline is replayed at speed (\d+) on a fake clock$")]
    public async Task WhenTheTimelineIsReplayedAtSpeedOnAFakeClock(int speed)
    {
        ReplayRig rig = _context.CreateRig();
        _ = rig.StartService(Run, speed);
        SseLog log = await rig.ConnectAsync();
        _context.Log = log;
        await rig.EventZeroAsync(log, StartWait, TimeSpan.Zero);

        // The next two waits of the recorded timeline: the end of the first step (1 s) and the start of the second (8 s).
        await rig.AdvanceAsync(2);
        await log.WaitForCountAsync("step-started", 2, HangGuard);
    }

    [Then(@"^the second step start is emitted (\d+) seconds after the first$")]
    public void ThenTheSecondStepStartIsEmittedSecondsAfterTheFirst(int seconds)
    {
        SseReceipt[] started = [.. (_context.Log ?? throw new XunitException("No client read the stream.")).Items.Where(static item => item.Type == "step-started")];
        Assert.Equal(TimeSpan.FromSeconds(seconds), started[1].AtUtc - started[0].AtUtc);
        Rig.Cancel();
    }

    // ---- the first client ----

    [Given(@"^a started replay of a fixture run file and no connected client$")]
    public async Task GivenAStartedReplayAndNoConnectedClient()
    {
        _context.UseFixture(Tier.Local);
        ReplayRig rig = _context.CreateRig();
        _ = rig.StartService(Run);
        _ = await rig.UriAsync();
        await rig.QuiescentAsync();
    }

    [When(@"^the clock advances (\d+) seconds$")]
    public async Task WhenTheClockAdvances(int seconds)
    {
        Rig.Time.Advance(TimeSpan.FromSeconds(seconds));
        await Rig.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
    }

    [Then(@"^no event was emitted$")]
    public async Task ThenNoEventWasEmitted()
    {
        // The replay printed its Dashboard line and armed the 120 s wait for the first client; no timeline wait was ever armed, and it has not ended.
        Assert.Equal(["Dashboard"], Rig.Output.Lines().Select(static line => line.Split(':')[0]));
        Assert.Equal([StartWait], Rig.Time.History);
        Assert.False(Rig.Exit!.IsCompleted);

        // The proof that nothing was emitted: a client that connects now gets a snapshot with no run and no step. It is the first client, so it also releases the replay for the next step.
        SseLog log = await Rig.ConnectAsync();
        _context.Log = log;
        await log.WaitForAsync(static items => items.Count >= 1, HangGuard);
        SseReceipt snapshot = log.Items[0];
        Assert.Equal("snapshot", snapshot.Type);
        using JsonDocument data = JsonDocument.Parse(snapshot.Json);
        JsonElement state = data.RootElement.GetProperty("data");
        Assert.True(!state.TryGetProperty("run", out JsonElement run) || run.ValueKind == JsonValueKind.Null, snapshot.Json);
        Assert.Empty(state.GetProperty("steps").EnumerateArray());
    }

    [Then(@"^after a client connects the first event is emitted$")]
    public async Task ThenAfterAClientConnectsTheFirstEventIsEmitted()
    {
        SseLog log = _context.Log ?? throw new XunitException("No client connected.");
        await log.WaitForAsync(static items => items.Count >= 2, HangGuard);

        SseReceipt first = log.Items[1];
        Assert.Equal("run-started", first.Type);
        Assert.Contains("\"replay\":true", first.Json, StringComparison.Ordinal);
        Rig.Cancel();
        Assert.Equal(0, await Rig.Exit!.WaitAsync(HangGuard));
    }

    // ---- the reconnect ----

    [Given(@"^a replay in progress after (\d+) of (\d+) steps$")]
    public async Task GivenAReplayInProgressAfterSteps(int done, int total)
    {
        _context.UseFixture(Tier.Local);
        Assert.Equal(total, Run.Steps.Count);
        IReadOnlyList<ReplayEntry> entries = ReplayTimeline.Build(Run);
        ReplayRig rig = _context.CreateRig();
        _ = rig.StartService(Run);
        SseLog log = await rig.ConnectAsync();
        _context.Log = log;
        await rig.EventZeroAsync(log, StartWait, TimeSpan.Zero);
        await rig.AdvanceAsync(ReplayContext.WaitsUntilStepsCompleted(entries, done));
        await log.WaitForCountAsync("step-completed", done, HangGuard);
        await rig.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        using DashboardConnection probe = rig.Sink.Connect();
        _context.SnapshotBeforeDrop = probe.Snapshot;
    }

    [When(@"^the client disconnects and reconnects$")]
    public async Task WhenTheClientDisconnectsAndReconnects()
    {
        await Rig.DisconnectAsync(_context.Log!, 0);
        _context.Second = await Rig.ConnectAsync();
        await _context.Second.WaitForAsync(static items => items.Count >= 1, HangGuard);
    }

    [Then(@"^its first item is a snapshot holding the state after (\d+) steps$")]
    public async Task ThenItsFirstItemIsASnapshotHoldingTheStateAfterSteps(int steps)
    {
        SseReceipt first = _context.Second!.Items[0];
        Assert.Equal("snapshot", first.Type);
        Assert.Equal(DataOf(_context.SnapshotBeforeDrop!), DataOf(first.Json));
        Assert.Equal(steps, JsonDocument.Parse(first.Json).RootElement.GetProperty("data").GetProperty("steps").GetArrayLength());
        Rig.Cancel();
        Assert.Equal(0, await Rig.Exit!.WaitAsync(HangGuard));
    }

    // ---- the timeout ----

    [Given(@"^a started replay with no client and --exit-after (\d+)$")]
    public async Task GivenAStartedReplayWithNoClientAndExitAfter(int seconds)
    {
        _context.UseFixture(Tier.Local);
        ReplayRig rig = _context.CreateRig();
        _ = rig.StartService(Run, exitAfter: TimeSpan.FromSeconds(seconds));
        _ = await rig.UriAsync();
        await rig.QuiescentAsync();
    }

    [When(@"^(\d+) seconds pass on the fake clock$")]
    public async Task WhenSecondsPassOnTheFakeClock(int seconds)
    {
        Rig.Time.Advance(TimeSpan.FromSeconds(seconds));
        _context.ExitCode = await Rig.Exit!.WaitAsync(HangGuard);
    }

    [Then(@"^a warning is printed and the exit code is 0$")]
    public void ThenAWarningIsPrintedAndTheExitCodeIs0()
    {
        Assert.Equal(0, _context.ExitCode);
        Assert.Equal(["Warning: no browser connected to the dashboard within 60 s; the replay ends"], Rig.Error.Lines());
    }

    // ---- Ctrl+C ----

    [Given(@"^a replay in progress with a connected client$")]
    public async Task GivenAReplayInProgressWithAConnectedClient()
    {
        _context.UseFixture(Tier.Local);
        await _context.StartInProgressCommandAsync();
        Assert.False(Rig.Exit!.IsCompleted);
    }

    [When(@"^the cancellation token is signalled$")]
    public async Task WhenTheCancellationTokenIsSignalled()
    {
        Rig.Cancel();
        _context.ExitCode = await Rig.Exit!.WaitAsync(HangGuard);
    }

    [Then(@"^the replay server stops by itself and the exit code is 0$")]
    public async Task ThenTheReplayServerStopsByItself()
    {
        Assert.Equal(0, _context.ExitCode);
        CliRunResult result = await Rig.Command!;
        Assert.Empty(result.Stderr);
        Assert.True(result.ConsoleRestored);
        Assert.Empty(result.DeniedRequests);

        // The server stopped without anything stopping it: the stream of the client ended and the port is free again.
        await _context.Log!.Completion.WaitAsync(HangGuard);
        Uri uri = await Rig.UriAsync();
        using LoopbackPortBlock? free = LoopbackPortBlock.TryHold(uri.Port);
        Assert.NotNull(free);
    }

    [Then(@"^no file was written$")]
    public void ThenNoFileWasWritten() => Assert.Equal(_context.FilesBefore, ReplayContext.FilesOf(Rig.WorkspaceRoot!));

    // ---- helpers ----

    private void ThenTheMessageNamesThePortRange()
    {
        CliRunResult result = _context.Result ?? throw new XunitException("The command has not run yet.");
        int first = _context.Held!.First;
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains(DashboardServer.NoFreePortMessage(first), line, StringComparison.Ordinal);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"{first}-{DashboardServer.PortRange(first).Last}"), line, StringComparison.Ordinal);
        Assert.DoesNotContain("Dashboard:", result.Stdout, StringComparison.Ordinal);
    }

    private static string DataOf(string envelope)
    {
        using JsonDocument document = JsonDocument.Parse(envelope);
        return document.RootElement.GetProperty("data").GetRawText();
    }
}