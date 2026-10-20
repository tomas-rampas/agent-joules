using System.Globalization;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Energy.Testing;
using AgentJoules.Core.Labels;

using Microsoft.Extensions.Time.Testing;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-031: the deterministic fake meter. Self-contained: its state lives here, not in ScenarioWorld, with its own fake clock. The
/// class is scoped to the feature because four of its sentences ("a closed window", "Close is called again", "an InvalidOperationException
/// is thrown" and "net is N J and the flags contain") also exist, unscoped, in the AJ-029 and AJ-026 bindings; a scoped binding wins inside this feature
/// and these bindings never match another feature. Nothing waits on the wall clock; the fake clock moves only when a step advances it.
/// </summary>
[Binding]
[Scope(Feature = "Provide the deterministic fake meter")]
public sealed class DeterministicMeterSteps : IDisposable
{
    private const double RateHz = 10.0;
    private const double Threshold = 0.10;
    private const double Tolerance = 1e-9;

    private readonly FakeTimeProvider _time = new();

    private DeterministicEnergyMeter? _meter;
    private Func<DeterministicEnergyMeter>? _construct;
    private IMeterWindow? _closedWindow;
    private EnergyReading? _reading;
    private BaselineResult? _baseline;
    private bool _baselineCompletedSynchronously;
    private bool _clockUnchanged;
    private Exception? _exception;

    public void Dispose()
    {
        _meter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _meter = null;
    }

    private DeterministicEnergyMeter Meter => _meter ?? throw new InvalidOperationException("No meter was created.");

    private EnergyReading RequireReading() => _reading ?? throw new InvalidOperationException("No window was closed.");

    private async Task StartAsync(Func<double, double> wattsAt, double? baseline = null)
    {
        _meter = new DeterministicEnergyMeter(_time, wattsAt, RateHz, counterSupported: true, MeterMethodSetting.Auto, baseline);
        await _meter.StartAsync(CancellationToken.None);
    }

    private EnergyReading OpenAdvanceClose(TimeSpan length)
    {
        IMeterWindow window = Meter.OpenWindow();
        _time.Advance(length);
        return window.Close();
    }

    private static void AssertFlag(EnergyReading reading, string name) =>
        Assert.Contains(name, ReadingFlagNames.ToStrings(reading.Flags));

    private static void AssertMeasured(Metric metric, double expected, string unit)
    {
        Assert.Equal(MetricLabel.Measured, metric.Label);
        Assert.Equal(unit, metric.Unit);
        Assert.NotNull(metric.Value);
        Assert.Equal(expected, metric.Value.Value, Tolerance);
    }

    // 20 W on even grid indices and 30 W on odd ones at 10 Hz.
    private static double Alternating(double seconds) => (long)Math.Round(seconds * RateHz) % 2 == 0 ? 20.0 : 30.0;

    [Given(@"^a deterministic meter at constant 60 W, 10 Hz and a 20 W baseline$")]
    public async Task GivenADeterministicMeterAtConstant60W10HzAnd20WBaseline() => await StartAsync(_ => 60.0, baseline: 20.0);

    [Given(@"^a deterministic meter at constant 15 W and a 20 W baseline$")]
    public async Task GivenADeterministicMeterAtConstant15WAnd20WBaseline() => await StartAsync(_ => 15.0, baseline: 20.0);

    [Given(@"^a deterministic meter at 10 Hz$")]
    public async Task GivenADeterministicMeterAt10Hz() => await StartAsync(_ => 60.0);

    [Given(@"^a deterministic meter with an alternating 20 W and 30 W function$")]
    public async Task GivenADeterministicMeterWithAnAlternating20WAnd30WFunction() => await StartAsync(Alternating);

    [Given(@"^a deterministic meter with counter support off and method counter$")]
    public void GivenADeterministicMeterWithCounterSupportOffAndMethodCounter() =>
        _construct = () => new DeterministicEnergyMeter(_time, static _ => 60.0, RateHz, counterSupported: false, MeterMethodSetting.Counter);

    [Given(@"^a closed window$")]
    public async Task GivenAClosedWindow()
    {
        await StartAsync(_ => 60.0);
        _closedWindow = Meter.OpenWindow();
        _time.Advance(TimeSpan.FromSeconds(3));
        _reading = _closedWindow.Close();
    }

    [When(@"^a window is opened, fake time advances 3 s and the window is closed$")]
    public void WhenAWindowIsOpenedFakeTimeAdvances3SAndTheWindowIsClosed() => _reading = OpenAdvanceClose(TimeSpan.FromSeconds(3));

    [When(@"^a 3 s window is closed$")]
    public void WhenA3SWindowIsClosed() => _reading = OpenAdvanceClose(TimeSpan.FromSeconds(3));

    [When(@"^a 150 ms window is closed$")]
    public void WhenA150MsWindowIsClosed() => _reading = OpenAdvanceClose(TimeSpan.FromMilliseconds(150));

    [When(@"^MeasureBaselineAsync\(30 s\) is awaited without advancing time$")]
    public async Task WhenMeasureBaselineAsync30SIsAwaitedWithoutAdvancingTime()
    {
        long stampBefore = _time.GetTimestamp();

        ValueTask<BaselineResult> measuring = Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), Threshold, CancellationToken.None);
        _baselineCompletedSynchronously = measuring.IsCompleted;
        _baseline = await measuring;
        _clockUnchanged = _time.GetTimestamp() == stampBefore;
    }

    [When(@"^the meter is constructed$")]
    public void WhenTheMeterIsConstructed()
    {
        Func<DeterministicEnergyMeter> construct = _construct ?? throw new InvalidOperationException("No construction was described.");
        _exception = Record.Exception(() => _meter = construct());
    }

    [When(@"^Close is called again$")]
    public void WhenCloseIsCalledAgain()
    {
        IMeterWindow window = _closedWindow ?? throw new InvalidOperationException("No window was closed.");
        _exception = Record.Exception(() => window.Close());
    }

    [Then(@"^gross is 180 J, net is 120 J, average is 60 W, peak is 60 W and sampleCount equals LastWindowSampleCount \((\d+)\)$")]
    public void ThenTheReferenceWindowHasTheDocumentedNumbers(int expectedSampleCount)
    {
        EnergyReading reading = RequireReading();
        AssertMeasured(reading.GrossJoules, 180.0, "J");
        AssertMeasured(reading.NetJoules, 120.0, "J");
        AssertMeasured(reading.AverageWatts, 60.0, "W");
        AssertMeasured(reading.PeakWatts, 60.0, "W");
        Assert.Equal(expectedSampleCount, reading.SampleCount);
        Assert.Equal(Meter.LastWindowSampleCount, reading.SampleCount);
    }

    [Then(@"^net is (-?\d+) J and the flags contain ""([^""]*)""$")]
    public void ThenNetIsAndTheFlagsContain(string joules, string flag)
    {
        AssertMeasured(RequireReading().NetJoules, double.Parse(joules, CultureInfo.InvariantCulture), "J");
        AssertFlag(RequireReading(), flag);
    }

    [Then(@"^sampleCount is (\d+) and the flags contain ""([^""]*)""$")]
    public void ThenSampleCountIsAndTheFlagsContain(int sampleCount, string flag)
    {
        Assert.Equal(sampleCount, RequireReading().SampleCount);
        Assert.Equal(sampleCount, Meter.LastWindowSampleCount);
        AssertFlag(RequireReading(), flag);
    }

    [Then(@"^it completes and the flags contain ""([^""]*)""$")]
    public void ThenItCompletesAndTheFlagsContain(string flag)
    {
        BaselineResult baseline = _baseline ?? throw new InvalidOperationException("No baseline was measured.");
        Assert.True(_baselineCompletedSynchronously, "MeasureBaselineAsync did not complete synchronously.");
        Assert.True(_clockUnchanged, "MeasureBaselineAsync advanced the fake clock.");
        Assert.Contains(flag, ReadingFlagNames.ToStrings(baseline.Flags));
    }

    [Then(@"^a MeasurementPreconditionException is thrown$")]
    public void ThenAMeasurementPreconditionExceptionIsThrown()
    {
        MeasurementPreconditionException failure = Assert.IsType<MeasurementPreconditionException>(_exception);
        Assert.Equal(ExitCode.PreconditionFailed, failure.ExitCode);
    }

    [Then(@"^an InvalidOperationException is thrown$")]
    public void ThenAnInvalidOperationExceptionIsThrown()
    {
        // The only scenario of this feature that ends here closes a window twice, so the message must say that and nothing else.
        InvalidOperationException failure = Assert.IsType<InvalidOperationException>(_exception);
        Assert.Contains("already closed", failure.Message, StringComparison.Ordinal);
    }
}