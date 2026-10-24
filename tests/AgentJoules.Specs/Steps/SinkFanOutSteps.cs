using AgentJoules.Core.Live;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Logging;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-089: readings fan out to a dashboard sink that can never block the run (REQ-032, EDGE-019). Scoped to the feature because sentences such as "the exit code is 2", "any command starts"
/// and "the step is metered" are reused by other stories with other meanings; a scoped binding wins inside this feature and never matches another one. The state lives in
/// <see cref="SinkFanOutContext"/>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-089 Fan readings out to a dashboard sink that can never block the run")]
public sealed class SinkFanOutSteps(SinkFanOutContext context)
{
    private const string PowerIntervalKey = "Dashboard:PowerIntervalMs";

    private readonly SinkFanOutContext _context = context;

    private CliRunResult Result => _context.Result ?? throw new InvalidOperationException("No command ran.");

    [Given(@"^a fan-out sink over an in-memory sink and a dashboard sink$")]
    public void GivenAFanOutOverMemoryAndDashboard() => _context.CreateFanOut(secondThrows: false);

    [Given(@"^a fan-out sink whose second target throws on every reading$")]
    public void GivenAFanOutWhoseSecondTargetThrows() => _context.CreateFanOut(secondThrows: true);

    [When(@"^one reading is published$")]
    public async Task WhenOneReadingIsPublished() => await _context.PublishAsync(1);

    [When(@"^(\d+) readings are published$")]
    public async Task WhenReadingsArePublished(int count) => await _context.PublishAsync(count);

    [Then(@"^the in-memory (?:snapshot|sink) holds (\d+) readings?$")]
    public void ThenTheInMemorySinkHolds(int count) => Assert.Equal(count, _context.Memory.Snapshot().Count);

    [Then(@"^the dashboard sink received a step-completed event$")]
    public void ThenTheDashboardReceivedAStepCompletedEvent()
    {
        string payload = Assert.Single(_context.DrainConnection());
        Assert.Equal("step-completed", SinkFanOutContext.TypeOf(payload));
    }

    [Then(@"^no exception reached the caller$")]
    public void ThenNoExceptionReachedTheCaller() => Assert.Null(_context.CallerFailure);

    [Given(@"^a run without --dashboard$")]
    public static void GivenARunWithoutTheDashboard()
    {
        // The composition root decides: nothing registers a dashboard, so there is nothing to set up.
    }

    [When(@"^the live sink is resolved$")]
    public async Task WhenTheLiveSinkIsResolved() => await _context.ResolveLiveSinkAsync();

    [Then(@"^it is the null live sink$")]
    public void ThenItIsTheNullLiveSink()
    {
        Assert.Equal(0, Result.ExitCode);
        Assert.Same(NullLiveEventSink.Instance, _context.ResolvedLiveSink);
    }

    [Given(@"^a connected client that never reads$")]
    public void GivenAConnectedClientThatNeverReads() => _context.CreateConnectedDashboard();

    [Given(@"^a dashboard sink whose pump throws on the first event$")]
    public void GivenADashboardSinkWhosePumpThrowsOnTheFirstEvent() => _context.FaultTheFirstEvent();

    [When(@"^(\d+) events are emitted$")]
    public async Task WhenEventsAreEmitted(int count) => await _context.EmitAsync(count);

    [Then(@"^the connection buffer holds 1000 events and they are the newest 1000$")]
    public void ThenTheConnectionHoldsTheNewest1000()
    {
        Assert.Equal(SinkFanOutContext.Capacity, _context.Connection.Events.Count);
        DateTimeOffset[] received = [.. _context.DrainConnection().Select(SinkFanOutContext.AtUtcOf)];
        Assert.Equal(Enumerable.Range(_context.Emitted - SinkFanOutContext.Capacity, SinkFanOutContext.Capacity).Select(SinkFanOutContext.At), received);
    }

    [Then(@"^every emit call returned normally$")]
    public void ThenEveryEmitReturnedNormally()
    {
        Assert.Null(_context.CallerFailure);
        Assert.Equal(10, _context.Emitted);
        Assert.Equal(9, _context.Connection.Events.Count);
    }

    [Then(@"^the pump error was logged once$")]
    public void ThenThePumpErrorWasLoggedOnce()
    {
        Assert.Equal(1, _context.Dashboard.PumpFaultCount);
        RecordedLogEntry warning = Assert.Single(_context.Logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
    }

    [Given(@"^Dashboard:PowerIntervalMs is set to (\d+)$")]
    public void GivenThePowerInterval(string interval) => _context.Set(PowerIntervalKey, interval);

    [When(@"^any command starts$")]
    public async Task WhenAnyCommandStarts() => await _context.StartAnyCommandAsync();

    [Then(@"^configuration validation passes$")]
    public void ThenConfigurationValidationPasses()
    {
        Assert.True(Result.ExitCode == 0, Result.Stderr);
        Assert.DoesNotContain(Result.StderrLines, line => line.Contains(PowerIntervalKey, StringComparison.Ordinal));
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.Equal(string.Empty, Result.Stdout);
    }

    [Then(@"^the output names ""([^""]*)""$")]
    public void ThenTheOutputNames(string key) => Assert.Contains(Result.ErrorLines, line => line.Contains(key, StringComparison.Ordinal));

    [Given(@"^a scripted NVML API and Dashboard:PowerIntervalMs (\d+) on a fake clock$")]
    public void GivenAScriptedNvmlApiAndTheInterval(int interval) => _context.UseMeter(10.0, interval);

    [Given(@"^a meter sampling every (\d+) ms and Dashboard:PowerIntervalMs (\d+)$")]
    public void GivenAMeterSamplingEveryAndTheInterval(int periodMs, int interval) => _context.UseMeter(1000.0 / periodMs, interval);

    [When(@"^a 1 second window is metered$")]
    public async Task WhenAOneSecondWindowIsMetered() => await _context.MeterOneSecondAsync();

    [Then(@"^at most 10 power events were emitted$")]
    public void ThenAtMostTenPowerEvents() => Assert.InRange(Forwarded.Events.Count, 1, 10);

    [Then(@"^the NVML call count equals the count of a run without the dashboard$")]
    public void ThenTheNvmlCallCountEqualsARunWithoutTheDashboard()
    {
        SinkFanOutContext.MeterRun plain = _context.Plain ?? throw new InvalidOperationException("No meter ran without forwarding.");
        Assert.Empty(plain.Events);
        Assert.Equal(plain.PowerCalls, Forwarded.PowerCalls);
        Assert.Equal(plain.CounterCalls, Forwarded.CounterCalls);
    }

    [Then(@"^every sample is forwarded as a power event$")]
    public void ThenEverySampleIsForwarded()
    {
        Assert.Equal(Forwarded.Ticks, Forwarded.Events.Count);
        Assert.Equal(Enumerable.Range(1, Forwarded.Ticks).Select(tick => Math.Round(tick / _context.SampleRateHz, 9)), Forwarded.Events.Select(static power => Math.Round(power.SampleSeconds, 9)));
    }

    [Then(@"^one startup note says the interval is shorter than the sampling period$")]
    public void ThenOneStartupNoteSaysTheIntervalIsShorter()
    {
        string? note = PowerForwarding.ShortIntervalNote(_context.PowerIntervalMs, _context.SampleRateHz);
        Assert.NotNull(note);
        Assert.Contains("is shorter than the sampling period", note, StringComparison.Ordinal);
        _ = Assert.Single(note.Split('\n'));
    }

    [Given(@"^a streaming step that delivers 100 output chunks within 1 second on a fake clock$")]
    public void GivenAStreamingStep() => _context.UseStep(streaming: true);

    [Given(@"^a buffered step$")]
    public void GivenABufferedStep() => _context.UseStep(streaming: false);

    [When(@"^the step is metered$")]
    public async Task WhenTheStepIsMetered() => await _context.MeterTheStepAsync();

    [Then(@"^at most 10 step-progress events were emitted and the last chunk count is monotonic$")]
    public void ThenAtMostTenStepProgressEvents()
    {
        Assert.InRange(_context.Progress.Count, 1, 10);
        Assert.All(_context.Progress.Zip(_context.Progress.Skip(1)), static pair => Assert.True(pair.Second.OutputChunks > pair.First.OutputChunks));
    }

    [Then(@"^no step-progress event was emitted$")]
    public void ThenNoStepProgressEventWasEmitted() => Assert.Empty(_context.Progress);

    private SinkFanOutContext.MeterRun Forwarded => _context.Forwarded ?? throw new InvalidOperationException("No meter ran with forwarding.");
}