using System.Diagnostics;
using System.Globalization;

using AgentJoules.Cli.Output;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Nvml;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-030: the idle baseline. Self-contained: its state lives here, not in ScenarioWorld, and the meter scenario builds its own fake
/// clock, scripted NVML session and meter. Nothing waits on the wall clock; the fake clock is advanced one sampling period at a time.
/// </summary>
[Binding]
public sealed class BaselineSteps : IDisposable
{
    // The default of Baseline:MaxCoefficientOfVariation; the scenarios that state no threshold use it.
    private const double DefaultThreshold = 0.10;
    private const double SampleRateHz = 10.0;
    private const int MaxYields = 2_000_000;
    private const double Tolerance = 1e-9;

    private PowerSample[] _samples = [];
    private double _startSeconds;
    private double _stopSeconds;
    private double _threshold = DefaultThreshold;
    private BaselineResult? LastBaseline { get; set; }

    private FakeTimeProvider? _time;
    private ScriptedNvmlApi? _api;
    private IGpuTelemetrySource? _source;
    private SampledEnergyMeter? _meter;
    private TimeSpan _waited;

    public void Dispose()
    {
        // The meter does not own the source, so it goes first; DisposeAsync is idempotent.
        _meter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _meter = null;
        _source?.Dispose();
        _source = null;
        while (_api is { IsInitialised: true })
        {
            _api.Shutdown();
        }

        _api = null;
    }

    private static PowerSample At(double seconds, double watts) => new(seconds, watts, ThrottleReasons.Unsupported);

    // Samples at i / 10 s for the given count; the watts of sample i come from the function.
    private static PowerSample[] TenHertz(int count, Func<int, double> wattsOf) =>
        [.. Enumerable.Range(0, count).Select(i => At(i / 10.0, wattsOf(i)))];

    private BaselineResult Baseline => LastBaseline ?? throw new InvalidOperationException("No baseline was calculated.");

    private static void AssertMeasured(Metric metric, double expected, string unit)
    {
        Assert.Equal(MetricLabel.Measured, metric.Label);
        Assert.Equal(unit, metric.Unit);
        Assert.NotNull(metric.Value);
        Assert.Equal(expected, metric.Value.Value, Tolerance);
        Assert.Null(metric.Reason);
    }

    private static void AssertNotReported(Metric metric, string reason)
    {
        Assert.Equal(MetricLabel.NotReported, metric.Label);
        Assert.Equal(reason, metric.Reason);
        Assert.Null(metric.Value);
    }

    [Given(@"^30 s of samples at a constant 20 W$")]
    public void GivenThirtySecondsOfSamplesAtAConstant20W()
    {
        _samples = TenHertz(301, _ => 20.0);
        _startSeconds = 0.0;
        _stopSeconds = 30.0;
    }

    // 300 samples, half at each level, so the mean and the population deviation are exact.
    [Given(@"^samples alternating 20 W and 30 W$")]
    public void GivenSamplesAlternating20WAnd30W()
    {
        _samples = TenHertz(300, i => i % 2 == 0 ? 20.0 : 30.0);
        _startSeconds = 0.0;
        _stopSeconds = 30.0;
    }

    // Mean 10 W and population deviation 1 W: the coefficient of variation is 1 / 10, which is exactly the double 0.10.
    [Given(@"^a baseline whose CV is exactly 0\.10$")]
    public void GivenABaselineWhoseCvIsExactly010()
    {
        _samples = TenHertz(300, i => i % 2 == 0 ? 9.0 : 11.0);
        _startSeconds = 0.0;
        _stopSeconds = 30.0;
    }

    [Given(@"^samples that are all 0 W$")]
    public void GivenSamplesThatAreAll0W()
    {
        _samples = TenHertz(301, _ => 0.0);
        _startSeconds = 0.0;
        _stopSeconds = 30.0;
    }

    [Given(@"^a single baseline sample$")]
    public void GivenASingleBaselineSample()
    {
        _samples = [At(0.0, 20.0)];
        _startSeconds = 0.0;
        _stopSeconds = 30.0;
    }

    [Given(@"^samples 10 W and 20 W$")]
    public void GivenSamples10WAnd20W()
    {
        _samples = [At(0.0, 10.0), At(1.0, 20.0)];
        _startSeconds = 0.0;
        _stopSeconds = 1.0;
    }

    [When(@"^the baseline is calculated with threshold (\d+\.\d+)$")]
    public void WhenTheBaselineIsCalculatedWithThreshold(string threshold)
    {
        _threshold = double.Parse(threshold, CultureInfo.InvariantCulture);
        LastBaseline = BaselineCalculator.Compute(_samples, _startSeconds, _stopSeconds, _threshold);
    }

    [When(@"^the baseline is calculated$")]
    public void WhenTheBaselineIsCalculated()
    {
        _threshold = DefaultThreshold;
        LastBaseline = BaselineCalculator.Compute(_samples, _startSeconds, _stopSeconds, _threshold);
    }

    [Then(@"^mean is (\d+) W, standard deviation is (\d+) W and there are no flags$")]
    public void ThenMeanAndStandardDeviationAndNoFlags(double mean, double standardDeviation)
    {
        AssertMeasured(Baseline.MeanW, mean, "W");
        AssertMeasured(Baseline.StdDevW, standardDeviation, "W");
        Assert.Equal(ReadingFlags.None, Baseline.Flags);
    }

    [Then(@"^mean is (\d+) W, standard deviation is (\d+) W, the flags contain ""([^""]*)"" and the console line is ""([^""]*)""$")]
    public void ThenMeanAndStandardDeviationAndTheFlagsContainAndTheConsoleLineIs(double mean, double standardDeviation, string flag, string line)
    {
        AssertMeasured(Baseline.MeanW, mean, "W");
        AssertMeasured(Baseline.StdDevW, standardDeviation, "W");
        Assert.Contains(flag, ReadingFlagNames.ToStrings(Baseline.Flags));

        using StringWriter output = new();
        using StringWriter error = new();
        new ConsoleReporter(output, error).ReportBaseline(Baseline);

        Assert.Equal(line + Environment.NewLine, error.ToString());
        Assert.Equal(string.Empty, output.ToString());
    }

    [Then(@"^the flags are empty$")]
    public void ThenTheFlagsAreEmpty()
    {
        Assert.Equal(ReadingFlags.None, Baseline.Flags);
        Assert.Empty(ReadingFlagNames.ToStrings(Baseline.Flags));
    }

    [Then(@"^meanW is not-reported with reason ""baseline-mean-nonpositive"" and no CV is divided by zero$")]
    public void ThenMeanWIsNotReportedBecauseTheMeanIsNotPositive()
    {
        AssertNotReported(Baseline.MeanW, "baseline-mean-nonpositive");
        Assert.Null(Baseline.CoefficientOfVariation);
        Assert.Equal(ReadingFlags.None, Baseline.Flags);
    }

    [Then(@"^meanW is not-reported with reason ""insufficient-samples""$")]
    public void ThenMeanWIsNotReportedBecauseOfInsufficientSamples()
    {
        AssertNotReported(Baseline.MeanW, "insufficient-samples");
        AssertNotReported(Baseline.StdDevW, "insufficient-samples");
        Assert.Null(Baseline.CoefficientOfVariation);
        Assert.Equal(ReadingFlags.None, Baseline.Flags);
    }

    // 10 W and 20 W: the population deviation is 5, the sample (n - 1) deviation would be 7.07.
    [Then(@"^the standard deviation is 5 W, not 7\.07 W$")]
    public void ThenTheStandardDeviationIs5WNot707W()
    {
        AssertMeasured(Baseline.StdDevW, 5.0, "W");
        Assert.NotEqual(7.07, Baseline.StdDevW.Value.GetValueOrDefault(), 2);
    }

    [Given(@"^a started meter at 10 Hz$")]
    public async Task GivenAStartedMeterAt10Hz()
    {
        _time = new FakeTimeProvider();
        _api = new ScriptedNvmlApi(_time) { ConstantPowerWatts = 20.0, CounterSupported = true };
        GpuOpenResult open = new NvmlGpuTelemetrySourceFactory(_api).Open(0);
        Assert.True(open.IsAvailable, open.Reason);
        _source = open.Source;
        MeterCapabilities capabilities = MeterMethodSelector.Select("sampling", _source, SampleRateHz);
        _meter = new SampledEnergyMeter(_source, capabilities, _time);
        await _meter.StartAsync(CancellationToken.None);
    }

    [When(@"^MeasureBaselineAsync\(30 s\) runs and the fake clock advances 30 s$")]
    public async Task WhenMeasureBaselineAsyncRunsAndTheFakeClockAdvances30S()
    {
        SampledEnergyMeter meter = _meter ?? throw new InvalidOperationException("No meter was created.");
        FakeTimeProvider time = _time ?? throw new InvalidOperationException("No fake clock was created.");
        ScriptedNvmlApi api = _api ?? throw new InvalidOperationException("No scripted NVML session was created.");

        Stopwatch stopwatch = Stopwatch.StartNew();
        Task<BaselineResult> measuring = meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), DefaultThreshold, CancellationToken.None).AsTask();

        // One sampling period at a time; each step yields (never waits on the wall clock) until the loop has taken its tick.
        TimeSpan period = TimeSpan.FromSeconds(1.0 / SampleRateHz);
        for (int tick = 0; tick < 300; tick++)
        {
            if (tick == 299)
            {
                // 29.9 s of the 30 s have passed: the wait is still pending, so it is the fake clock that ends it.
                Assert.False(measuring.IsCompleted, "MeasureBaselineAsync completed before 30 s of fake time had passed.");
            }

            int target = api.PowerCallCount + 1;
            time.Advance(period);
            int yields = 0;
            while (api.PowerCallCount < target)
            {
                Assert.True(yields++ < MaxYields, "The sampling loop did not take its tick.");
                await Task.Yield();
            }

            _ = meter.SampleCount;
        }

        int waits = 0;
        while (!measuring.IsCompleted)
        {
            Assert.True(waits++ < MaxYields, "MeasureBaselineAsync did not complete after 30 s of fake time.");
            await Task.Yield();
        }

        LastBaseline = await measuring;
        stopwatch.Stop();
        _waited = stopwatch.Elapsed;
    }

    [Then(@"^the baseline seconds is 30 and no real time was spent waiting$")]
    public void ThenTheBaselineSecondsIs30AndNoRealTimeWasSpentWaiting()
    {
        Assert.Equal(30.0, Baseline.Seconds, Tolerance);
        Assert.True(_waited < TimeSpan.FromSeconds(1), $"The fake-time wait took {_waited.TotalMilliseconds:F0} ms of real time.");
    }
}