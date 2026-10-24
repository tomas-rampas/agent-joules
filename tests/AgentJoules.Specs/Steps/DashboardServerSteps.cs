using System.Globalization;
using System.Text.Json;

using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Dashboard;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-091: the dashboard is served on loopback with SSE and a port fallback (REQ-032, EDGE-018). Scoped to the feature because sentences such as "the exit code is 0", "the run command"
/// and "is executed" are reused by other stories with other meanings. The state lives in <see cref="DashboardServerContext"/>; the server, the sink, the parser, the preflight and the
/// production orchestrator are the real ones, every port the scenario binds is from its own <see cref="LoopbackPortBlock"/>, and every HTTP request goes through the one
/// <see cref="HeadlessSseClient"/> of the scenario. The two latency scenarios run on the system clock, because they measure real delivery (P-80); every other scenario uses a fake clock.
/// </summary>
[Binding]
[Scope(Feature = "AJ-091 Serve the dashboard on loopback with SSE and a port fallback")]
public sealed class DashboardServerSteps(DashboardServerContext context, IReqnrollOutputHelper output)
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private readonly DashboardServerContext _context = context;
    private readonly IReqnrollOutputHelper _output = output;

    private CliRunResult Result => _context.Result ?? throw new XunitException("The command has not run yet.");

    private string[] StdoutLines => RunOrchestrationFixtures.StdoutLines(Result);

    [AfterScenario]
    public async Task DisposeTheRig() => await _context.DisposeAsync();

    // ---- the run command ----

    [Given(@"^the run command with fake keys, a scripted GPU and the dashboard enabled$")]
    public void GivenTheRunCommandWithTheDashboard()
    {
        // The dashboard is enabled by --dashboard on the command line; the port comes from a block of free ports the scenario reserved (P-79).
        _context.UseDryRunSetup();
        _context.UseFreePorts();
    }

    [Given(@"^listeners hold 20 consecutive ports starting at the preferred port$")]
    public void GivenTwentyPortsHeld()
    {
        // The command line of the scenario names no port: the run step appends --dashboard-port with the first held port (P-79).
        _context.HoldPorts(DashboardServer.PortAttempts);
        _context.UseHostedRunSetup();
    }

    [Given(@"^a listener holds the preferred port$")]
    public void GivenTheListenerHoldsThePreferredPort()
    {
        _context.HoldPorts(1);
        _context.UseHostedRunSetup();
    }

    [Given(@"^the run command without --dashboard$")]
    [Given(@"^the run command$")]
    public void GivenTheRunCommand() => _context.UseHostedRunSetup();

    [When(@"^""([^""]*)"" is executed$")]
    public async Task WhenCommandIsExecuted(string arguments) => await _context.RunAsync(arguments);

    [When(@"^the dashboard starts during a run$")]
    public async Task WhenTheDashboardStartsDuringARun() => await _context.RunAsync($"run --tier nvidia --task {DashboardServerContext.TaskName} --repeat 1 --dashboard");

    [Then(@"^the output has a line starting ""([^""]*)"" containing ""([^""]*)""$")]
    public void ThenTheOutputHasALine(string start, string fragment)
    {
        string line = Assert.Single(StdoutLines, candidate => candidate.StartsWith(start, StringComparison.Ordinal));
        Assert.Contains(fragment, line, StringComparison.Ordinal);

        // The dry run printed its tier line first, made no completion call, and disposed the server at once: the port it served is free again.
        Assert.StartsWith("local ", StdoutLines[0], StringComparison.Ordinal);
        Uri printed = new(line[(start.Length + 1)..]);
        Assert.True(_context.Ports!.Contains(printed.Port), line);
        using LoopbackPortBlock? free = LoopbackPortBlock.TryHold(printed.Port);
        Assert.NotNull(free);
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.Empty(Result.DeniedRequests);
        if (expected == 2)
        {
            // A parse error of --dashboard-port: no host was built and nothing was sent.
            Assert.Equal(0, _context.Setup!.HostBuilds);
            Assert.Contains("--dashboard-port must be a whole number from 1 to 65535", Result.Stderr, StringComparison.Ordinal);
        }
    }

    [Then(@"^it reports a different port and the run completes with exit code 0$")]
    public void ThenItReportsADifferentPort()
    {
        Assert.True(Result.ExitCode == 0, Result.Stderr);
        string line = Assert.Single(StdoutLines, static candidate => candidate.StartsWith("Dashboard: ", StringComparison.Ordinal));
        Uri printed = new(line["Dashboard: ".Length..]);
        int preferred = _context.PreferredPort!.Value;
        Assert.NotEqual(preferred, printed.Port);
        Assert.InRange(printed.Port, preferred + 1, preferred + DashboardServer.PortAttempts - 1);
        AssertCompleteResultsFile(stepsExpected: 4);
    }

    [Then(@"^a warning is printed and no ""([^""]*)"" line appears$")]
    public void ThenAWarningIsPrinted(string prefix)
    {
        Assert.DoesNotContain(StdoutLines, candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        Assert.Contains($"Warning: the dashboard did not start ({DashboardServer.NoFreePortMessage(_context.PreferredPort!.Value)}); the run continues without it", Result.StderrLines);
    }

    [Then(@"^the exit code is 0 and the results file is complete$")]
    public void ThenTheExitCodeIs0AndTheResultsFileIsComplete()
    {
        Assert.True(Result.ExitCode == 0, Result.Stderr);
        Assert.Empty(Result.ErrorLines);
        AssertCompleteResultsFile(stepsExpected: 12);
    }

    [Then(@"^no ""([^""]*)"" line appears and no port was opened$")]
    public void ThenNoLineAppearsAndNoPortWasOpened(string prefix)
    {
        Assert.True(Result.ExitCode == 0, Result.Stderr);
        Assert.DoesNotContain(StdoutLines, candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));

        // Without --dashboard the host registers no dashboard sink, so no server can exist, and the run keeps the null live sink.
        Assert.False(_context.SinkRegistered);
        Assert.Same(NullLiveEventSink.Instance, _context.LiveSink);
        Assert.DoesNotContain("dashboard", Result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the server ----

    [Given(@"^a started dashboard server with one emitted step event$")]
    public async Task GivenAStartedServerWithOneStepEvent()
    {
        await _context.StartServerAsync(systemClock: false);
        _context.Sink!.Publish(StepReadingFixtures.Metered(agent: "Writer"));
        await _context.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
    }

    [Given(@"^a started dashboard server$")]
    public async Task GivenAStartedServer() => await _context.StartServerAsync(systemClock: false);

    [When(@"^a client reads ""([^""]*)""$")]
    public async Task WhenAClientReads(string path)
    {
        Assert.Equal(HeadlessSseClient.EventsPath, path);
        using var guard = new CancellationTokenSource(HangGuard);
        await using HeadlessSseConnection stream = await _context.Client.OpenEventsAsync(_context.Uri!, cancellationToken: guard.Token);
        await using IAsyncEnumerator<SseReceipt> events = stream.ReadEventsAsync(guard.Token).GetAsyncEnumerator(guard.Token);
        _context.EventsContentType = stream.ContentType;
        Assert.True(await events.MoveNextAsync());
        _context.Receipts = [events.Current];
    }

    [Then(@"^the response content type is ""([^""]*)""$")]
    public void ThenTheResponseContentTypeIs(string contentType) => Assert.Equal(contentType, _context.EventsContentType);

    [Then(@"^the first item is a snapshot and a later line starts with ""([^""]*)""$")]
    public void ThenTheFirstItemIsASnapshot(string prefix)
    {
        SseReceipt first = _context.Receipts[0];
        Assert.Equal(("aj", "snapshot"), (first.EventType, first.Type));
        Assert.Equal("event: aj", first.Lines[0]);

        // The data line is the envelope as raw JSON, not a quoted string, and the snapshot holds the step emitted before the client connected.
        string data = Assert.Single(first.Lines.Skip(1), candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        Assert.Equal("data: " + first.Json, data);
        using JsonDocument document = JsonDocument.Parse(first.Json);
        Assert.Contains(document.RootElement.GetProperty("data").GetProperty("agents").EnumerateArray(), static agent => agent.GetProperty("agent").GetString() == "Writer");
    }

    [When(@"^a client requests ""([^""]*)"" and ""([^""]*)""$")]
    public async Task WhenAClientRequests(string first, string second)
    {
        foreach (string path in new[] { first, second })
        {
            _context.Responses.Add(await _context.Client.GetAsync(_context.Uri!, path, null).WaitAsync(HangGuard));
        }
    }

    [Then(@"^both responses carry the Content-Security-Policy and X-Content-Type-Options ""([^""]*)"" headers$")]
    public void ThenBothResponsesCarryTheHeaders(string nosniff)
    {
        string policy = DashboardServer.ContentSecurityPolicy(DashboardPage.Render(null));
        Assert.StartsWith("default-src 'none'; script-src 'sha256-", policy, StringComparison.Ordinal);
        Assert.Equal(2, _context.Responses.Count);
        Assert.All(_context.Responses, response =>
        {
            Assert.Equal(200, response.Status);
            Assert.Equal(policy, response.Header("Content-Security-Policy"));
            Assert.Equal(nosniff, response.Header("X-Content-Type-Options"));
        });
        Assert.Equal("text/html; charset=utf-8", _context.Responses[0].Header("Content-Type"));
        Assert.Equal("text/event-stream", _context.Responses[1].Header("Content-Type"));
    }

    [When(@"^a client sends a request with Host ""([^""]*)""$")]
    public async Task WhenAClientSendsAForeignHost(string host)
    {
        // The event stream, after an event was applied, so "no event data" is the Host rule's doing.
        _context.Sink!.Publish(StepReadingFixtures.Metered());
        await _context.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        _context.Responses.Add(await _context.Client.GetAsync(_context.Uri!, HeadlessSseClient.EventsPath, host).WaitAsync(HangGuard));
    }

    [Then(@"^the status is (\d+) and no event data is returned$")]
    public void ThenTheStatusIs(int status)
    {
        HeadlessResponse response = Assert.Single(_context.Responses);
        Assert.Equal(status, response.Status);
        Assert.True(response.BodyRead);
        Assert.Empty(response.BodyBytes);
        Assert.NotNull(response.Header("Content-Security-Policy"));
        Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
    }

    // ---- the latency ----

    [Given(@"^a started dashboard server, a headless SSE client and the system clock$")]
    public async Task GivenAServerOnTheSystemClock() => await _context.StartServerAsync(systemClock: true);

    [Given(@"^a dashboard server variant that delays delivery by (\d+) ms$")]
    public async Task GivenADelayingVariant(int milliseconds)
    {
        await _context.StartServerAsync(systemClock: true);
        _context.DelayDelivery(TimeSpan.FromMilliseconds(milliseconds));
    }

    [When(@"^a fixture run emits at least (\d+) events including power events at the default interval$")]
    public async Task WhenAFixtureRunEmitsEvents(int atLeast)
    {
        Assert.True(DashboardLatencyRun.EventCount >= atLeast);
        Assert.Equal(TimeSpan.FromMilliseconds(100), DashboardLatencyRun.PowerInterval);
        await _context.RunLatencyFixtureAsync();
        Assert.True(_context.Receipts.Count(static receipt => receipt.Type != "snapshot") >= atLeast);
        Assert.True(_context.Receipts.Count(static receipt => receipt.Type == "power") >= 10);
    }

    [When(@"^a fixture run emits its events$")]
    public async Task WhenAFixtureRunEmitsItsEvents() => await _context.RunLatencyFixtureAsync();

    [Then(@"^each event was received within (\d+) ms of its atUtc$")]
    public void ThenEachEventWasReceivedWithin(int milliseconds)
    {
        LatencyResult result = HeadlessSseClient.CheckLatency(_context.Receipts, TimeSpan.FromMilliseconds(milliseconds));
        _output.WriteLine("AJ-091 latency: " + result.Message);
        Assert.True(result.Passed, result.Message);
    }

    [Then(@"^the latency check fails and reports a maximum above (\d+) ms$")]
    public void ThenTheLatencyCheckFails(int milliseconds)
    {
        LatencyResult result = HeadlessSseClient.CheckLatency(_context.Receipts, TimeSpan.FromMilliseconds(milliseconds));
        _output.WriteLine("AJ-091 delayed latency: " + result.Message);
        Assert.False(result.Passed, result.Message);
        Assert.True(result.MaximumMs > milliseconds, result.Message);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"maximum latency {result.MaximumMs:F1} ms exceeds"), result.Message, StringComparison.Ordinal);
    }

    // The one results file of the run is completed, with every step ok.
    private void AssertCompleteResultsFile(int stepsExpected)
    {
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(_context.RunFiles()));
        Assert.Equal("completed", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
        Assert.Equal(stepsExpected, steps.Length);
        Assert.All(steps, static step => Assert.Equal(StepOutcomes.Ok, step.GetProperty("outcome").GetString()));
    }
}