using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

using AgentJoules.Cli.Output;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Nvml;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-005")]
public sealed class AttributionBaselineTests
{
    private const double Tolerance = 1e-9;
    private const double Threshold = 0.10;
    private const int MaxYields = 2_000_000;
    private const string UnstableLine = "Warning: idle baseline unstable (CV 0.20 > 0.10)";

    private static PowerSample Sample(double seconds, double watts) =>
        new(seconds, watts, ThrottleReasons.Unsupported);

    // 10 Hz samples at i / 10.0 s, both window edges included: 31 samples span exactly 3 s.
    private static PowerSample[] Constant(int count, double watts) =>
        [.. Enumerable.Range(0, count).Select(i => Sample(i / 10.0, watts))];

    private static ReadingInput Input(
        IReadOnlyList<PowerSample>? samples,
        double start = 0.0,
        double stop = 3.0,
        EnergyMethod method = EnergyMethod.Sampling,
        long? delta = null,
        double? baseline = null,
        double rate = 10.0,
        bool fallback = false) =>
        new(
            Samples: samples!,
            WindowStartSeconds: start,
            WindowStopSeconds: stop,
            Method: method,
            CounterDeltaMillijoules: delta,
            BaselineMeanWatts: baseline,
            SampleRateHz: rate,
            IsSamplingFallback: fallback);

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

    private static void AssertNoValueIsZero(EnergyReading reading)
    {
        foreach (Metric metric in new[] { reading.GrossJoules, reading.NetJoules, reading.AverageWatts, reading.PeakWatts })
        {
            Assert.True(metric.Value is not 0.0, "No metric of the reading may carry the value 0.");
        }
    }

    [Fact]
    public void ThreeSecondsAtSixtyWattsAgainstATwentyWattBaselineGivesTheFullReading()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input(Constant(31, 60.0), baseline: 20.0));

        AssertMeasured(reading.GrossJoules, 180.0, "J");
        AssertMeasured(reading.NetJoules, 120.0, "J");
        AssertMeasured(reading.AverageWatts, 60.0, "W");
        AssertMeasured(reading.PeakWatts, 60.0, "W");
        Assert.Equal(3.0, reading.DurationSeconds, Tolerance);
        Assert.Equal(EnergyMethod.Sampling, reading.Method);
        Assert.Equal(31, reading.SampleCount);
        Assert.Equal(10.0, reading.SampleRateHz);
        Assert.Equal(ReadingFlags.None, reading.Flags);
        Assert.Null(reading.ThrottleReasons);
    }

    [Fact]
    public void SampleRateHzIsTheConfiguredRateNotTheObservedOne()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input(Constant(31, 60.0), rate: 25.0));

        Assert.Equal(25.0, reading.SampleRateHz);
        Assert.Equal(31, reading.SampleCount);
    }

    [Fact]
    public void DurationIsStopMinusStart()
    {
        PowerSample[] samples = [Sample(1.1, 60.0), Sample(1.2, 60.0), Sample(1.3, 60.0)];

        EnergyReading reading = ReadingCalculator.Compute(Input(samples, start: 1.1, stop: 1.3));

        Assert.Equal(1.3 - 1.1, reading.DurationSeconds);
    }

    [Fact]
    public void AverageIsTheIntegralDividedByTheWindowDurationAndPeakIsTheMaximumSample()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(1.0, 100.0), Sample(2.0, 50.0)];

        EnergyReading reading = ReadingCalculator.Compute(Input(samples, stop: 2.0));

        AssertMeasured(reading.GrossJoules, 150.0, "J");
        AssertMeasured(reading.AverageWatts, 75.0, "W");
        AssertMeasured(reading.PeakWatts, 100.0, "W");
        Assert.Equal(3, reading.SampleCount);
    }

    [Fact]
    public void SamplesInsideTheWindowAreHeldToTheEdgesAndTheAverageDividesByTheWholeWindow()
    {
        PowerSample[] samples = [Sample(0.5, 60.0), Sample(1.5, 60.0)];

        EnergyReading reading = ReadingCalculator.Compute(Input(samples, stop: 2.0));

        AssertMeasured(reading.GrossJoules, 120.0, "J");
        AssertMeasured(reading.AverageWatts, 60.0, "W");
        Assert.Equal(2, reading.SampleCount);
    }

    [Fact]
    public void AWindowBelowTheBaselineKeepsTheNegativeNetValueAndFlagsIt()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input(Constant(31, 15.0), baseline: 20.0));

        AssertMeasured(reading.GrossJoules, 45.0, "J");
        AssertMeasured(reading.NetJoules, -15.0, "J");
        Assert.Equal(ReadingFlags.BelowBaseline, reading.Flags);
    }

    [Fact]
    public void ANetValueOfExactlyZeroIsMeasuredAndNotFlaggedBelowBaseline()
    {
        // The counter delta is exact (180 J), so net is exactly 0; a sampling integral carries float noise and could land on either side.
        EnergyReading reading = ReadingCalculator.Compute(
            Input(Constant(31, 60.0), method: EnergyMethod.Counter, delta: 180_000L, baseline: 60.0));

        AssertMeasured(reading.NetJoules, 0.0, "J");
        Assert.Equal(ReadingFlags.None, reading.Flags);
    }

    [Fact]
    public void AZeroBaselineGivesNetEqualToGross()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input(Constant(31, 60.0), baseline: 0.0));

        AssertMeasured(reading.NetJoules, 180.0, "J");
        Assert.Equal(ReadingFlags.None, reading.Flags);
    }

    [Fact]
    public void CounterMethodTakesGrossFromTheCounterAndTheRestFromTheSamples()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input(Constant(31, 60.0), method: EnergyMethod.Counter, delta: 170_000L));

        AssertMeasured(reading.GrossJoules, 170.0, "J");
        AssertMeasured(reading.AverageWatts, 60.0, "W");
        AssertMeasured(reading.PeakWatts, 60.0, "W");
        Assert.Equal(EnergyMethod.Counter, reading.Method);
        Assert.Equal(31, reading.SampleCount);
        Assert.Equal(10.0, reading.SampleRateHz);
    }

    [Fact]
    public void CounterMethodNetIsTheCounterGrossMinusBaselineTimesDuration()
    {
        EnergyReading reading = ReadingCalculator.Compute(
            Input(Constant(31, 60.0), method: EnergyMethod.Counter, delta: 170_000L, baseline: 20.0));

        AssertMeasured(reading.NetJoules, 110.0, "J");
    }

    [Fact]
    public void ACounterDeltaOfZeroIsAMeasuredZeroNotAMissingValue()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input(Constant(31, 60.0), method: EnergyMethod.Counter, delta: 0L));

        AssertMeasured(reading.GrossJoules, 0.0, "J");
    }

    [Fact]
    [Trait("Spec", "EDGE-003")]
    public void AWindowOf150MillisecondsAtTenHertzIsFlaggedLowResolutionAndKept()
    {
        PowerSample[] samples = [Sample(0.0, 60.0), Sample(0.1, 60.0), Sample(0.15, 60.0)];

        EnergyReading reading = ReadingCalculator.Compute(Input(samples, stop: 0.15));

        Assert.True(reading.Flags.HasFlag(ReadingFlags.LowResolution));
        AssertMeasured(reading.GrossJoules, 9.0, "J");
        AssertMeasured(reading.AverageWatts, 60.0, "W");
        Assert.Equal(3, reading.SampleCount);
    }

    [Theory]
    [Trait("Spec", "EDGE-003")]
    [InlineData(0.15, 10.0, true)]
    [InlineData(0.199, 10.0, true)]
    [InlineData(0.1999999, 10.0, true)]
    [InlineData(0.2, 10.0, false)]
    [InlineData(0.25, 10.0, false)]
    [InlineData(0.015, 100.0, true)]
    [InlineData(0.02, 100.0, false)]
    [InlineData(1.99, 1.0, true)]
    [InlineData(2.0, 1.0, false)]
    public void LowResolutionMeansStrictlyShorterThanTwoOverTheSampleRate(double windowSeconds, double rate, bool expected)
    {
        PowerSample[] samples = [Sample(0.0, 60.0), Sample(windowSeconds / 2.0, 60.0), Sample(windowSeconds, 60.0)];

        EnergyReading reading = ReadingCalculator.Compute(Input(samples, stop: windowSeconds, rate: rate));

        Assert.Equal(expected, reading.Flags.HasFlag(ReadingFlags.LowResolution));
    }

    [Fact]
    [Trait("Spec", "EDGE-003")]
    public void TheLowResolutionComparisonIsInIntegerTicksSoFloatNoiseInTheWindowDoesNotFlagIt()
    {
        // 1.3 - 1.1 is not exactly 0.2 in binary floating point; as ticks it is exactly 200 ms.
        PowerSample[] samples = [Sample(1.1, 60.0), Sample(1.2, 60.0), Sample(1.3, 60.0)];

        EnergyReading reading = ReadingCalculator.Compute(Input(samples, start: 1.1, stop: 1.3));

        Assert.False(reading.Flags.HasFlag(ReadingFlags.LowResolution));
    }

    [Fact]
    public void NoBaselineLeavesNetNotReportedWhileGrossStaysMeasured()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input(Constant(31, 60.0), baseline: null));

        AssertMeasured(reading.GrossJoules, 180.0, "J");
        AssertNotReported(reading.NetJoules, "no-baseline");
        AssertMeasured(reading.AverageWatts, 60.0, "W");
        Assert.Equal(ReadingFlags.None, reading.Flags);
    }

    [Fact]
    public void ACounterThatWentBackwardsMakesGrossAndNetNotReportedAndNeverZero()
    {
        EnergyReading reading = ReadingCalculator.Compute(
            Input(Constant(31, 60.0), method: EnergyMethod.Counter, delta: -5_000L, baseline: 20.0));

        AssertNotReported(reading.GrossJoules, "counter-went-backwards");
        AssertNotReported(reading.NetJoules, "counter-went-backwards");
        AssertNoValueIsZero(reading);
        Assert.Equal(ReadingFlags.None, reading.Flags);
    }

    [Fact]
    public void ABackwardsCounterWithoutABaselineStillNamesTheCounterAsTheReasonForNet()
    {
        EnergyReading reading = ReadingCalculator.Compute(
            Input(Constant(31, 60.0), method: EnergyMethod.Counter, delta: -5_000L, baseline: null));

        AssertNotReported(reading.GrossJoules, "counter-went-backwards");
        AssertNotReported(reading.NetJoules, "counter-went-backwards");
    }

    [Fact]
    public void ABackwardsCounterDoesNotDiscardTheSampleDerivedValues()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input(Constant(31, 60.0), method: EnergyMethod.Counter, delta: -5_000L));

        AssertMeasured(reading.AverageWatts, 60.0, "W");
        AssertMeasured(reading.PeakWatts, 60.0, "W");
        Assert.Equal(31, reading.SampleCount);
    }

    [Fact]
    public void ACounterThatWasNotReadMakesGrossAndNetNotReported()
    {
        EnergyReading reading = ReadingCalculator.Compute(
            Input(Constant(31, 60.0), method: EnergyMethod.Counter, delta: null, baseline: 20.0));

        AssertNotReported(reading.GrossJoules, "counter-not-read");
        AssertNotReported(reading.NetJoules, "counter-not-read");
        AssertMeasured(reading.AverageWatts, 60.0, "W");
        AssertNoValueIsZero(reading);
    }

    [Fact]
    public void ASingleSampleWithMethodSamplingMakesGrossAverageAndPeakNotReported()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input([Sample(1.5, 60.0)], baseline: 20.0));

        AssertNotReported(reading.GrossJoules, "insufficient-samples");
        AssertNotReported(reading.AverageWatts, "insufficient-samples");
        AssertNotReported(reading.PeakWatts, "insufficient-samples");
        AssertNotReported(reading.NetJoules, "insufficient-samples");
        AssertNoValueIsZero(reading);
        Assert.Equal(1, reading.SampleCount);
    }

    [Fact]
    public void NoSamplesAtAllIsTreatedLikeASingleSample()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input([]));

        AssertNotReported(reading.GrossJoules, "insufficient-samples");
        AssertNotReported(reading.AverageWatts, "insufficient-samples");
        AssertNotReported(reading.PeakWatts, "insufficient-samples");
        Assert.Equal(0, reading.SampleCount);
    }

    [Fact]
    public void ASingleSampleWithMethodCounterOnlyLosesAverageAndPeak()
    {
        EnergyReading reading = ReadingCalculator.Compute(
            Input([Sample(1.5, 60.0)], method: EnergyMethod.Counter, delta: 170_000L, baseline: 20.0));

        AssertMeasured(reading.GrossJoules, 170.0, "J");
        AssertMeasured(reading.NetJoules, 110.0, "J");
        AssertNotReported(reading.AverageWatts, "insufficient-samples");
        AssertNotReported(reading.PeakWatts, "insufficient-samples");
        Assert.Equal(1, reading.SampleCount);
    }

    [Fact]
    public void SamplingFallbackIsFlaggedOnlyWhenTheCallerSaysAutoFellBack()
    {
        EnergyReading fellBack = ReadingCalculator.Compute(Input(Constant(31, 60.0), fallback: true));
        EnergyReading forced = ReadingCalculator.Compute(Input(Constant(31, 60.0), fallback: false));

        Assert.Equal(ReadingFlags.SamplingFallback, fellBack.Flags);
        Assert.Equal(ReadingFlags.None, forced.Flags);
    }

    [Fact]
    public void FlagsCombineWithoutDisplacingEachOther()
    {
        PowerSample[] samples = [Sample(0.0, 15.0), Sample(0.1, 15.0), Sample(0.15, 15.0)];

        EnergyReading reading = ReadingCalculator.Compute(Input(samples, stop: 0.15, baseline: 20.0, fallback: true));

        Assert.Equal(ReadingFlags.SamplingFallback | ReadingFlags.LowResolution | ReadingFlags.BelowBaseline, reading.Flags);
    }

    [Fact]
    public void ASingleSampleWithMethodSamplingAndNoBaselineMakesNetNotReportedForTheSameReasonAsGross()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input([Sample(1.5, 60.0)], baseline: null));

        AssertNotReported(reading.GrossJoules, "insufficient-samples");
        AssertNotReported(reading.NetJoules, "insufficient-samples");
        AssertNotReported(reading.AverageWatts, "insufficient-samples");
        AssertNotReported(reading.PeakWatts, "insufficient-samples");
    }

    // Float noise can make the integral of a flat window land a hair below baseline x duration. There is no epsilon: REQ-005 forbids
    // clamping, and an epsilon would publish a negative number without its flag. The flag must always agree with the sign of net.
    [Theory]
    [InlineData(20.0, 21)]
    [InlineData(20.0, 31)]
    [InlineData(60.0, 21)]
    [InlineData(60.0, 31)]
    public void TheBelowBaselineFlagAlwaysAgreesWithTheSignOfNetOnAWindowFlatAtTheBaseline(double watts, int count)
    {
        EnergyReading reading = ReadingCalculator.Compute(Input(Constant(count, watts), stop: (count - 1) / 10.0, baseline: watts));

        Assert.NotNull(reading.NetJoules.Value);
        double net = reading.NetJoules.Value.Value;
        Assert.Equal(0.0, net, Tolerance);
        Assert.Equal(net < 0.0, reading.Flags.HasFlag(ReadingFlags.BelowBaseline));
    }

    [Fact]
    public void AnArrayAListAndAnyOtherListGiveTheSameReading()
    {
        PowerSample[] array = Constant(31, 60.0);

        EnergyReading fromArray = ReadingCalculator.Compute(Input(array, baseline: 20.0));
        List<PowerSample> list = [.. array];
        EnergyReading fromList = ReadingCalculator.Compute(Input(list, baseline: 20.0));
        EnergyReading fromOther = ReadingCalculator.Compute(Input(new ReadOnlyCollection<PowerSample>(array), baseline: 20.0));

        Assert.Equal(fromArray, fromList);
        Assert.Equal(fromArray, fromOther);
        Assert.Equal(31, fromOther.SampleCount);
    }

    private static void AssertRejected(ReadingInput input, string fragment)
    {
        // The exact type, so a guard that is deleted cannot be masked by the integrator or Metric throwing a subclass or another type.
        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReadingCalculator.Compute(input));

        Assert.Equal("input", failure.ParamName);
        Assert.Contains(fragment, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3.0, 3.0)]
    [InlineData(3.0, 1.0)]
    [InlineData(0.0, double.NaN)]
    [InlineData(0.0, double.PositiveInfinity)]
    [InlineData(double.NaN, 3.0)]
    [InlineData(double.NegativeInfinity, 3.0)]
    [InlineData(double.PositiveInfinity, 3.0)]
    [InlineData(-double.MaxValue, double.MaxValue)]
    public void AWindowThatIsNotFiniteOrDoesNotEndAfterItStartsIsRejected(double start, double stop)
    {
        AssertRejected(Input(Constant(31, 60.0), start: start, stop: stop), "ReadingInput window");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonPositiveOrNonFiniteSampleRateIsRejected(double rate)
    {
        AssertRejected(Input(Constant(31, 60.0), rate: rate), "ReadingInput.SampleRateHz");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-20.0)]
    [InlineData(-double.Epsilon)]
    [InlineData(1e308)]
    public void ABaselineThatIsNegativeNonFiniteOrOverflowsTheNetProductIsRejected(double baseline)
    {
        AssertRejected(Input(Constant(31, 60.0), baseline: baseline), "ReadingInput.BaselineMeanWatts");
    }

    [Fact]
    public void ANegativeZeroBaselineIsAcceptedAndGivesNetEqualToGross()
    {
        EnergyReading reading = ReadingCalculator.Compute(Input(Constant(31, 60.0), baseline: -0.0));

        Assert.Equal(reading.GrossJoules.Value, reading.NetJoules.Value);
    }

    [Theory]
    [InlineData((EnergyMethod)0)]
    [InlineData((EnergyMethod)3)]
    [InlineData((EnergyMethod)99)]
    public void AnUndefinedMethodIsRejected(EnergyMethod method)
    {
        AssertRejected(Input(Constant(31, 60.0), method: method), "ReadingInput.Method");
    }

    [Fact]
    public void ASamplingFallbackFlagWithTheCounterMethodIsRejected()
    {
        AssertRejected(
            Input(Constant(31, 60.0), method: EnergyMethod.Counter, delta: 170_000L, fallback: true),
            "ReadingInput.IsSamplingFallback");
    }

    [Fact]
    public void ACounterDeltaWithTheSamplingMethodIsRejected()
    {
        AssertRejected(Input(Constant(31, 60.0), method: EnergyMethod.Sampling, delta: 170_000L), "ReadingInput.CounterDeltaMillijoules");
    }

    [Fact]
    public void ANullSampleListIsRejected()
    {
        AssertRejected(Input(null), "ReadingInput.Samples");
    }

    [Fact]
    public void ADefaultInputIsRejectedAsHavingNoSamples()
    {
        AssertRejected(default, "ReadingInput.Samples");
    }

    // ---- AJ-030: the idle baseline (BaselineCalculator, BaselineResult, ConsoleReporter, SampledEnergyMeter.MeasureBaselineAsync) ----

    // Samples at i / 10 s alternating between two levels, the first level at the even indexes; an even count gives each level half.
    private static PowerSample[] Alternating(int count, double first, double second) =>
        [.. Enumerable.Range(0, count).Select(i => Sample(i / 10.0, i % 2 == 0 ? first : second))];

    private static (string Output, string Error) Report(BaselineResult baseline)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        new ConsoleReporter(output, error).ReportBaseline(baseline);
        return (output.ToString(), error.ToString());
    }

    [Fact]
    public void Constant_20_watts_for_30_seconds_is_a_stable_baseline()
    {
        BaselineResult baseline = BaselineCalculator.Compute(Constant(301, 20.0), 0.0, 30.0, Threshold);

        AssertMeasured(baseline.MeanW, 20.0, "W");
        AssertMeasured(baseline.StdDevW, 0.0, "W");
        Assert.Equal(30.0, baseline.Seconds, Tolerance);
        Assert.Equal(301, baseline.SampleCount);
        Assert.NotNull(baseline.CoefficientOfVariation);
        Assert.Equal(0.0, baseline.CoefficientOfVariation.Value, Tolerance);
        Assert.Equal(Threshold, baseline.MaxCoefficientOfVariation);
        Assert.Equal(ReadingFlags.None, baseline.Flags);
    }

    [Fact]
    [Trait("Spec", "EDGE-005")]
    public void Alternating_20_and_30_watts_gives_mean_25_deviation_5_cv_020_and_baseline_unstable()
    {
        BaselineResult baseline = BaselineCalculator.Compute(Alternating(300, 20.0, 30.0), 0.0, 30.0, Threshold);

        AssertMeasured(baseline.MeanW, 25.0, "W");
        AssertMeasured(baseline.StdDevW, 5.0, "W");
        Assert.NotNull(baseline.CoefficientOfVariation);
        Assert.Equal(0.20, baseline.CoefficientOfVariation.Value, Tolerance);
        Assert.Equal(ReadingFlags.BaselineUnstable, baseline.Flags);
        Assert.Equal("baseline-unstable", Assert.Single(ReadingFlagNames.ToStrings(baseline.Flags)));
        Assert.Equal(30.0, baseline.Seconds, Tolerance);
        Assert.Equal(300, baseline.SampleCount);
    }

    [Fact]
    [Trait("Spec", "EDGE-005")]
    public void An_unstable_baseline_still_gives_gross_and_net_and_net_uses_its_mean()
    {
        BaselineResult baseline = BaselineCalculator.Compute(Alternating(300, 20.0, 30.0), 0.0, 30.0, Threshold);
        Assert.NotNull(baseline.MeanW.Value);

        EnergyReading reading = ReadingCalculator.Compute(Input(Constant(31, 60.0), baseline: baseline.MeanW.Value));

        AssertMeasured(reading.GrossJoules, 180.0, "J");
        AssertMeasured(reading.NetJoules, 180.0 - (25.0 * 3.0), "J");
        Assert.Equal(ReadingFlags.None, reading.Flags);
    }

    [Fact]
    [Trait("Spec", "EDGE-005")]
    public void The_console_warning_for_an_unstable_baseline_is_exactly_one_line_on_the_error_writer()
    {
        BaselineResult baseline = BaselineCalculator.Compute(Alternating(300, 20.0, 30.0), 0.0, 30.0, Threshold);

        (string output, string error) = Report(baseline);

        Assert.Equal(UnstableLine + Environment.NewLine, error);
        Assert.Equal(string.Empty, output);
        Assert.Contains("baseline", error, StringComparison.Ordinal);
        Assert.Contains("unstable", error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "EDGE-005")]
    public void The_console_warning_prints_both_numbers_with_two_decimals()
    {
        // Mean 11, deviation 2: CV 0.1818..., printed 0.18; the threshold 0.12 is printed 0.12.
        BaselineResult baseline = BaselineCalculator.Compute(Alternating(300, 9.0, 13.0), 0.0, 30.0, 0.12);

        (string output, string error) = Report(baseline);

        Assert.Equal("Warning: idle baseline unstable (CV 0.18 > 0.12)" + Environment.NewLine, error);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    [Trait("Spec", "EDGE-005")]
    public void The_console_warning_uses_the_invariant_culture()
    {
        BaselineResult baseline = BaselineCalculator.Compute(Alternating(300, 20.0, 30.0), 0.0, 30.0, Threshold);
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaCulture.NumberFormat.NumberDecimalSeparator = ",";
        try
        {
            CultureInfo.CurrentCulture = commaCulture;

            (_, string error) = Report(baseline);

            Assert.Equal(UnstableLine + Environment.NewLine, error);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    [Trait("Spec", "EDGE-005")]
    public void The_console_reporter_writes_nothing_for_a_stable_baseline()
    {
        BaselineResult baseline = BaselineCalculator.Compute(Constant(301, 20.0), 0.0, 30.0, Threshold);

        (string output, string error) = Report(baseline);

        Assert.Equal(string.Empty, output);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void The_console_reporter_writes_nothing_for_a_baseline_that_was_not_reported(int sampleCount)
    {
        BaselineResult baseline = BaselineCalculator.Compute(Constant(sampleCount, 20.0), 0.0, 30.0, Threshold);

        (string output, string error) = Report(baseline);

        Assert.Equal(string.Empty, output);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void The_console_reporter_rejects_null_arguments()
    {
        using StringWriter writer = new();

        Assert.Throws<ArgumentNullException>(() => new ConsoleReporter(null!, writer));
        Assert.Throws<ArgumentNullException>(() => new ConsoleReporter(writer, null!));
        Assert.Throws<ArgumentNullException>(() => new ConsoleReporter(writer, writer).ReportBaseline(null!));
        Assert.Equal(string.Empty, writer.ToString());
    }

    // Mean 10 W and population deviation 1 W from integer samples, so a two-pass calculation is exact: CV = 1 / 10, which is the
    // double 0.10. A running (Welford) update or an epsilon in the comparison would not be.
    [Fact]
    [Trait("Spec", "EDGE-005")]
    public void Cv_equal_to_threshold_is_not_flagged()
    {
        BaselineResult baseline = BaselineCalculator.Compute(Alternating(300, 9.0, 11.0), 0.0, 30.0, Threshold);

        AssertMeasured(baseline.MeanW, 10.0, "W");
        AssertMeasured(baseline.StdDevW, 1.0, "W");
        Assert.NotNull(baseline.CoefficientOfVariation);
        Assert.Equal(Threshold, baseline.CoefficientOfVariation.Value);
        Assert.Equal(ReadingFlags.None, baseline.Flags);
        Assert.Empty(ReadingFlagNames.ToStrings(baseline.Flags));
    }

    [Theory]
    [Trait("Spec", "EDGE-005")]
    [InlineData(0.11, false)]
    [InlineData(0.10, false)]
    [InlineData(0.0999, true)]
    [InlineData(0.05, true)]
    public void The_flag_is_set_only_when_the_cv_is_strictly_greater_than_the_threshold(double threshold, bool expectedFlagged)
    {
        BaselineResult baseline = BaselineCalculator.Compute(Alternating(300, 9.0, 11.0), 0.0, 30.0, threshold);

        Assert.Equal(expectedFlagged, baseline.Flags.HasFlag(ReadingFlags.BaselineUnstable));
        Assert.Equal(threshold, baseline.MaxCoefficientOfVariation);
    }

    [Fact]
    [Trait("Spec", "EDGE-005")]
    public void Population_standard_deviation()
    {
        PowerSample[] samples = [Sample(0.0, 10.0), Sample(1.0, 20.0)];

        BaselineResult baseline = BaselineCalculator.Compute(samples, 0.0, 1.0, Threshold);

        AssertMeasured(baseline.StdDevW, 5.0, "W");
        Assert.NotEqual(7.07, baseline.StdDevW.Value.GetValueOrDefault(), 2);
        AssertMeasured(baseline.MeanW, 15.0, "W");
    }

    [Fact]
    public void The_mean_is_unweighted_by_the_time_between_samples()
    {
        // A time-weighted mean would be dominated by the long gap before the 40 W sample; the baseline is a plain mean of the samples.
        PowerSample[] samples = [Sample(0.0, 10.0), Sample(0.1, 10.0), Sample(5.0, 40.0)];

        BaselineResult baseline = BaselineCalculator.Compute(samples, 0.0, 5.0, Threshold);

        AssertMeasured(baseline.MeanW, 20.0, "W");
        AssertMeasured(baseline.StdDevW, Math.Sqrt(200.0), "W");
        Assert.Equal(3, baseline.SampleCount);
        Assert.Equal(5.0, baseline.Seconds, Tolerance);
    }

    [Fact]
    public void The_wattage_overload_gives_the_same_result_as_the_sample_overload()
    {
        PowerSample[] samples = Alternating(300, 20.0, 30.0);
        double[] watts = [.. samples.Select(sample => sample.Watts)];

        BaselineResult fromSamples = BaselineCalculator.Compute(samples, 0.0, 30.0, Threshold);
        BaselineResult fromWatts = BaselineCalculator.Compute(watts, 0.0, 30.0, Threshold);

        Assert.Equal(fromSamples, fromWatts);
    }

    [Theory]
    [InlineData(new[] { 0.0, 0.0, 0.0 }, 0.0)]
    [InlineData(new[] { -5.0, 5.0 }, 5.0)]
    [InlineData(new[] { -4.0, -6.0 }, 1.0)]
    public void A_mean_of_zero_or_less_is_not_reported_and_no_cv_is_computed(double[] watts, double expectedStandardDeviation)
    {
        BaselineResult baseline = BaselineCalculator.Compute(watts, 0.0, 3.0, Threshold);

        AssertNotReported(baseline.MeanW, "baseline-mean-nonpositive");
        Assert.Null(baseline.CoefficientOfVariation);
        Assert.Equal(ReadingFlags.None, baseline.Flags);
        AssertMeasured(baseline.StdDevW, expectedStandardDeviation, "W");
        Assert.Equal(3.0, baseline.Seconds, Tolerance);
        Assert.Equal(watts.Length, baseline.SampleCount);
    }

    [Fact]
    public void A_zero_watt_baseline_over_30_seconds_is_not_reported()
    {
        BaselineResult baseline = BaselineCalculator.Compute(Constant(301, 0.0), 0.0, 30.0, Threshold);

        AssertNotReported(baseline.MeanW, "baseline-mean-nonpositive");
        Assert.Null(baseline.CoefficientOfVariation);
        Assert.Equal(ReadingFlags.None, baseline.Flags);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Fewer_than_two_samples_are_not_reported_with_insufficient_samples(int sampleCount)
    {
        BaselineResult baseline = BaselineCalculator.Compute(Constant(sampleCount, 20.0), 0.0, 30.0, Threshold);

        AssertNotReported(baseline.MeanW, "insufficient-samples");
        AssertNotReported(baseline.StdDevW, "insufficient-samples");
        Assert.Null(baseline.CoefficientOfVariation);
        Assert.Equal(ReadingFlags.None, baseline.Flags);
        Assert.Equal(sampleCount, baseline.SampleCount);
        Assert.Equal(30.0, baseline.Seconds, Tolerance);
    }

    [Theory]
    [InlineData(0.0, 0.0, "stopSeconds")]
    [InlineData(1.0, 0.5, "stopSeconds")]
    [InlineData(double.NaN, 3.0, "startSeconds")]
    [InlineData(double.NegativeInfinity, 3.0, "startSeconds")]
    [InlineData(0.0, double.NaN, "stopSeconds")]
    [InlineData(0.0, double.PositiveInfinity, "stopSeconds")]
    public void A_window_that_is_not_finite_or_does_not_end_after_it_starts_is_rejected(double start, double stop, string parameter)
    {
        PowerSample[] samples = Constant(31, 20.0);
        double[] watts = [.. samples.Select(sample => sample.Watts)];

        ArgumentOutOfRangeException fromSamples = Assert.Throws<ArgumentOutOfRangeException>(() => BaselineCalculator.Compute(samples, start, stop, Threshold));
        ArgumentOutOfRangeException fromWatts = Assert.Throws<ArgumentOutOfRangeException>(() => BaselineCalculator.Compute(watts, start, stop, Threshold));

        Assert.Equal(parameter, fromSamples.ParamName);
        Assert.Equal(parameter, fromWatts.ParamName);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_threshold_that_is_not_finite_and_positive_is_rejected_even_when_there_are_too_few_samples(double threshold)
    {
        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => BaselineCalculator.Compute(Constant(1, 20.0), 0.0, 30.0, threshold));

        Assert.Equal("maxCoefficientOfVariation", failure.ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_sample_wattage_that_is_not_finite_is_rejected(double watts)
    {
        double[] wattages = [20.0, watts, 20.0];
        PowerSample[] samples = [Sample(0.0, 20.0), Sample(1.0, watts), Sample(2.0, 20.0)];

        ArgumentException fromWatts = Assert.Throws<ArgumentException>(() => BaselineCalculator.Compute(wattages, 0.0, 3.0, Threshold));
        ArgumentException fromSamples = Assert.Throws<ArgumentException>(() => BaselineCalculator.Compute(samples, 0.0, 3.0, Threshold));

        Assert.Equal("watts", fromWatts.ParamName);
        Assert.Equal("samples", fromSamples.ParamName);
    }

    [Fact]
    public void A_coefficient_of_variation_that_overflows_is_rejected_instead_of_being_reported_as_infinite()
    {
        // The mean is a subnormal 3.3e-311 W and the deviation about 8e9 W, so the quotient exceeds the largest double.
        double[] watts = [-1e10, 1e10, 1e-310];

        ArgumentException failure = Assert.Throws<ArgumentException>(() => BaselineCalculator.Compute(watts, 0.0, 3.0, Threshold));

        Assert.Equal("watts", failure.ParamName);
        Assert.Contains("coefficient of variation", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sum_that_overflows_is_rejected()
    {
        double[] watts = [1.5e308, 1.5e308];

        ArgumentException failure = Assert.Throws<ArgumentException>(() => BaselineCalculator.Compute(watts, 0.0, 3.0, Threshold));

        Assert.Equal("watts", failure.ParamName);
        Assert.Contains("overflow", failure.Message, StringComparison.Ordinal);
    }

    // ---- AJ-030: BaselineResult validates every invariant in its public constructor ----

    private static BaselineResult BuildResult(
        Metric? mean = null,
        Metric? deviation = null,
        double seconds = 30.0,
        int count = 300,
        double? cv = 0.20,
        double max = 0.10,
        ReadingFlags flags = ReadingFlags.BaselineUnstable) =>
        new(mean ?? Metric.Measured(25.0, "W"), deviation ?? Metric.Measured(5.0, "W"), seconds, count, cv, max, flags);

    [Fact]
    public void A_result_built_by_hand_equals_the_one_the_calculator_builds()
    {
        BaselineResult computed = BaselineCalculator.Compute(Alternating(300, 20.0, 30.0), 0.0, 30.0, Threshold);

        Assert.Equal(computed, BuildResult(cv: computed.CoefficientOfVariation));
    }

    [Fact]
    public void A_result_survives_a_json_round_trip_with_its_flags_as_vocabulary_strings()
    {
        BaselineResult computed = BaselineCalculator.Compute(Alternating(300, 20.0, 30.0), 0.0, 30.0, Threshold);

        string json = JsonSerializer.Serialize(computed);
        BaselineResult read = JsonSerializer.Deserialize<BaselineResult>(json)!;

        Assert.Contains("\"baseline-unstable\"", json, StringComparison.Ordinal);
        Assert.Equal(computed, read);
    }

    [Fact]
    public void A_result_that_is_not_reported_is_accepted_and_has_no_coefficient()
    {
        BaselineResult result = BuildResult(
            mean: Metric.NotReported("baseline-mean-nonpositive"),
            cv: null,
            flags: ReadingFlags.None);

        Assert.Null(result.CoefficientOfVariation);
        Assert.Equal(ReadingFlags.None, result.Flags);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_result_with_seconds_that_are_not_positive_and_finite_is_rejected(double seconds)
    {
        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => BuildResult(seconds: seconds));

        Assert.Equal("seconds", failure.ParamName);
    }

    [Fact]
    public void A_result_with_a_negative_sample_count_is_rejected()
    {
        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => BuildResult(count: -1));

        Assert.Equal("sampleCount", failure.ParamName);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_result_with_a_threshold_that_is_not_positive_and_finite_is_rejected(double max)
    {
        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => BuildResult(max: max));

        Assert.Equal("maxCoefficientOfVariation", failure.ParamName);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_result_with_a_coefficient_that_is_negative_or_not_finite_is_rejected(double cv)
    {
        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => BuildResult(cv: cv));

        Assert.Equal("coefficientOfVariation", failure.ParamName);
    }

    [Fact]
    public void A_result_without_its_metrics_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new BaselineResult(null!, Metric.Measured(5.0, "W"), 30.0, 300, 0.2, 0.1, ReadingFlags.BaselineUnstable));
        Assert.Throws<ArgumentNullException>(() => new BaselineResult(Metric.Measured(25.0, "W"), null!, 30.0, 300, 0.2, 0.1, ReadingFlags.BaselineUnstable));
    }

    [Fact]
    public void A_result_whose_metric_is_not_measured_or_not_reported_or_has_the_wrong_unit_is_rejected()
    {
        ArgumentException estimated = Assert.Throws<ArgumentException>(() => BuildResult(mean: Metric.EstimatedQuantity(25.0, "W")));
        ArgumentException notApplicable = Assert.Throws<ArgumentException>(() => BuildResult(deviation: Metric.NotApplicable("x")));
        ArgumentException wrongUnit = Assert.Throws<ArgumentException>(() => BuildResult(mean: Metric.Measured(25.0, "J")));
        ArgumentException wrongDeviationUnit = Assert.Throws<ArgumentException>(() => BuildResult(deviation: Metric.Measured(5.0, "J")));

        Assert.Equal("meanW", estimated.ParamName);
        Assert.Equal("stdDevW", notApplicable.ParamName);
        Assert.Equal("meanW", wrongUnit.ParamName);
        Assert.Equal("stdDevW", wrongDeviationUnit.ParamName);
    }

    [Fact]
    public void A_result_has_a_coefficient_exactly_when_its_mean_is_measured()
    {
        ArgumentException missing = Assert.Throws<ArgumentException>(() => BuildResult(cv: null, flags: ReadingFlags.None));
        ArgumentException unexpected = Assert.Throws<ArgumentException>(
            () => BuildResult(mean: Metric.NotReported("baseline-mean-nonpositive"), cv: 0.2, flags: ReadingFlags.BaselineUnstable));

        Assert.Equal("coefficientOfVariation", missing.ParamName);
        Assert.Equal("coefficientOfVariation", unexpected.ParamName);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(0.0)]
    public void A_result_with_a_measured_mean_that_is_not_positive_is_rejected(double mean)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => BuildResult(mean: Metric.Measured(mean, "W")));

        Assert.Equal("meanW", failure.ParamName);
    }

    [Fact]
    public void A_result_with_a_measured_negative_deviation_is_rejected()
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => BuildResult(deviation: Metric.Measured(-1.0, "W")));

        Assert.Equal("stdDevW", failure.ParamName);
    }

    [Fact]
    public void A_result_with_a_zero_deviation_is_accepted()
    {
        BaselineResult result = BuildResult(deviation: Metric.Measured(0.0, "W"), cv: 0.0, flags: ReadingFlags.None);

        AssertMeasured(result.StdDevW, 0.0, "W");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_result_below_two_samples_needs_both_metrics_not_reported_as_insufficient(int count)
    {
        ArgumentException measured = Assert.Throws<ArgumentException>(() => BuildResult(count: count));
        ArgumentException deviationOnly = Assert.Throws<ArgumentException>(
            () => BuildResult(mean: Metric.Measured(25.0, "W"), deviation: Metric.NotReported("insufficient-samples"), count: count));
        ArgumentException meanOnly = Assert.Throws<ArgumentException>(
            () => BuildResult(mean: Metric.NotReported("insufficient-samples"), count: count, cv: null, flags: ReadingFlags.None));

        Assert.Equal("sampleCount", measured.ParamName);
        Assert.Equal("sampleCount", deviationOnly.ParamName);
        Assert.Equal("sampleCount", meanOnly.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_result_below_two_samples_with_both_metrics_insufficient_is_accepted(int count)
    {
        BaselineResult result = BuildResult(
            mean: Metric.NotReported("insufficient-samples"),
            deviation: Metric.NotReported("insufficient-samples"),
            count: count,
            cv: null,
            flags: ReadingFlags.None);

        Assert.Equal(count, result.SampleCount);
    }

    [Fact]
    public void A_result_of_two_or_more_samples_allows_no_insufficient_metric()
    {
        ArgumentException mean = Assert.Throws<ArgumentException>(
            () => BuildResult(mean: Metric.NotReported("insufficient-samples"), count: 2, cv: null, flags: ReadingFlags.None));
        ArgumentException both = Assert.Throws<ArgumentException>(
            () => BuildResult(
                mean: Metric.NotReported("insufficient-samples"),
                deviation: Metric.NotReported("insufficient-samples"),
                count: 2,
                cv: null,
                flags: ReadingFlags.None));

        Assert.Equal("sampleCount", mean.ParamName);
        Assert.Equal("sampleCount", both.ParamName);
    }

    [Fact]
    public void A_not_reported_reason_outside_the_vocabulary_is_rejected()
    {
        ArgumentException mean = Assert.Throws<ArgumentException>(
            () => BuildResult(mean: Metric.NotReported("because"), cv: null, flags: ReadingFlags.None));
        ArgumentException deviation = Assert.Throws<ArgumentException>(() => BuildResult(deviation: Metric.NotReported("because")));
        ArgumentException deviationWithMeanReason = Assert.Throws<ArgumentException>(
            () => BuildResult(deviation: Metric.NotReported("baseline-mean-nonpositive")));

        Assert.Equal("meanW", mean.ParamName);
        Assert.Equal("stdDevW", deviation.ParamName);
        Assert.Equal("stdDevW", deviationWithMeanReason.ParamName);
    }

    [Fact]
    public void A_nonpositive_mean_requires_a_measured_deviation()
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => BuildResult(
                mean: Metric.NotReported("baseline-mean-nonpositive"),
                deviation: Metric.NotReported("insufficient-samples"),
                cv: null,
                flags: ReadingFlags.None));

        Assert.Equal("sampleCount", failure.ParamName);
    }

    [Theory]
    [InlineData(0.20, 0.10, ReadingFlags.None)]
    [InlineData(0.10, 0.10, ReadingFlags.BaselineUnstable)]
    [InlineData(0.05, 0.10, ReadingFlags.BaselineUnstable)]
    [InlineData(0.20, 0.10, ReadingFlags.BaselineUnstable | ReadingFlags.BelowBaseline)]
    [InlineData(0.20, 0.10, ReadingFlags.BelowBaseline)]
    public void A_result_carries_baseline_unstable_exactly_when_the_coefficient_is_strictly_above_the_threshold_and_no_other_flag(double cv, double max, ReadingFlags flags)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => BuildResult(cv: cv, max: max, flags: flags));

        Assert.Equal("flags", failure.ParamName);
    }

    // ---- AJ-030: MeasureBaselineAsync on the real meter over a scripted NVML session and a fake clock (no wall-clock wait) ----

    [Fact]
    public async Task MeasureBaselineAsync_waits_30_seconds_on_the_fake_clock_and_collects_301_samples_at_10_Hz()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);

        Task<BaselineResult> measuring = rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), Threshold, CancellationToken.None).AsTask();
        Assert.False(measuring.IsCompleted);

        await rig.TickAsync(299);
        Assert.False(measuring.IsCompleted);

        await rig.TickAsync(1);
        BaselineResult baseline = await MeterRig.CompleteAsync(measuring);

        // Edge sample at 0 s (it replaces the start sample of the same instant), 299 ticks, and the tick and stop edge at 30 s.
        Assert.Equal(30.0, baseline.Seconds, Tolerance);
        Assert.Equal(301, baseline.SampleCount);
        AssertMeasured(baseline.MeanW, 25.0, "W");
        AssertMeasured(baseline.StdDevW, 0.0, "W");
        Assert.Equal(ReadingFlags.None, baseline.Flags);
        Assert.Equal(Threshold, baseline.MaxCoefficientOfVariation);
    }

    [Fact]
    public async Task The_stored_baseline_mean_is_what_the_next_window_computes_its_net_against()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        BaselineResult baseline = await rig.MeasureAsync(30);
        Assert.Equal(301, baseline.SampleCount);

        rig.Api.ConstantPowerWatts = 60.0;
        EnergyReading reading = await rig.WindowAsync();

        AssertMeasured(reading.GrossJoules, 180.0, "J");
        AssertMeasured(reading.NetJoules, 180.0 - (25.0 * 3.0), "J");
        Assert.Equal(ReadingFlags.None, reading.Flags);
    }

    [Fact]
    public async Task A_window_before_any_baseline_has_no_net()
    {
        await using MeterRig rig = MeterRig.Create(watts: 60.0);
        await rig.Meter.StartAsync(CancellationToken.None);

        EnergyReading reading = await rig.WindowAsync();

        AssertMeasured(reading.GrossJoules, 180.0, "J");
        AssertNotReported(reading.NetJoules, "no-baseline");
    }

    [Fact]
    [Trait("Spec", "EDGE-005")]
    public async Task An_alternating_baseline_on_the_meter_is_flagged_unstable_and_the_next_net_still_uses_its_mean()
    {
        await using MeterRig rig = MeterRig.Create(
            watts: 20.0,
            script: api => api.WattsAt = seconds => Math.Round(seconds * 10.0) % 2.0 == 0.0 ? 20.0 : 30.0);
        await rig.Meter.StartAsync(CancellationToken.None);

        BaselineResult baseline = await rig.MeasureAsync(30);

        // 151 samples at 20 W and 150 at 30 W: the mean is exactly 7520 / 301 W and the population deviation follows from it; the
        // sample (n - 1) deviation would differ by a factor of sqrt(301 / 300), far outside the tolerance.
        double mean = 7520.0 / 301.0;
        double variance = ((151.0 * Math.Pow(20.0 - mean, 2)) + (150.0 * Math.Pow(30.0 - mean, 2))) / 301.0;
        Assert.Equal(301, baseline.SampleCount);
        AssertMeasured(baseline.MeanW, mean, "W");
        AssertMeasured(baseline.StdDevW, Math.Sqrt(variance), "W");
        Assert.NotNull(baseline.CoefficientOfVariation);
        Assert.Equal(Math.Sqrt(variance) / mean, baseline.CoefficientOfVariation.Value, Tolerance);
        Assert.Equal(ReadingFlags.BaselineUnstable, baseline.Flags);
        Assert.NotNull(baseline.MeanW.Value);

        rig.Api.ConstantPowerWatts = 60.0;
        EnergyReading reading = await rig.WindowAsync();

        AssertMeasured(reading.GrossJoules, 180.0, "J");
        AssertMeasured(reading.NetJoules, 180.0 - (baseline.MeanW.Value.Value * 3.0), "J");
    }

    [Fact]
    public async Task A_duration_shorter_than_one_period_still_has_its_two_edge_samples()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);

        Task<BaselineResult> measuring = rig.Meter.MeasureBaselineAsync(TimeSpan.FromMilliseconds(50), Threshold, CancellationToken.None).AsTask();
        rig.Time.Advance(TimeSpan.FromMilliseconds(50));
        BaselineResult baseline = await MeterRig.CompleteAsync(measuring);

        Assert.Equal(0.05, baseline.Seconds, Tolerance);
        Assert.Equal(2, baseline.SampleCount);
        AssertMeasured(baseline.MeanW, 25.0, "W");
    }

    [Fact]
    public async Task A_second_baseline_replaces_the_first()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        _ = await rig.MeasureAsync(2);

        rig.Api.ConstantPowerWatts = 40.0;
        BaselineResult second = await rig.MeasureAsync(1);
        AssertMeasured(second.MeanW, 40.0, "W");

        rig.Api.ConstantPowerWatts = 60.0;
        EnergyReading reading = await rig.WindowAsync();

        AssertMeasured(reading.NetJoules, 180.0 - (40.0 * 3.0), "J");
    }

    [Fact]
    public async Task A_baseline_that_is_not_reported_clears_the_stored_one_so_net_is_not_reported()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        _ = await rig.MeasureAsync(2);

        rig.Api.ConstantPowerWatts = 0.0;
        BaselineResult idle = await rig.MeasureAsync(1);
        AssertNotReported(idle.MeanW, "baseline-mean-nonpositive");
        Assert.Null(idle.CoefficientOfVariation);
        Assert.Equal(ReadingFlags.None, idle.Flags);

        rig.Api.ConstantPowerWatts = 60.0;
        EnergyReading reading = await rig.WindowAsync();

        AssertMeasured(reading.GrossJoules, 180.0, "J");
        AssertNotReported(reading.NetJoules, "no-baseline");
    }

    [Fact]
    public async Task A_duration_of_exactly_one_millisecond_is_accepted()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);

        Task<BaselineResult> measuring = rig.Meter.MeasureBaselineAsync(TimeSpan.FromMilliseconds(1), Threshold, CancellationToken.None).AsTask();
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        BaselineResult baseline = await MeterRig.CompleteAsync(measuring);

        Assert.Equal(0.001, baseline.Seconds, Tolerance);
        Assert.Equal(2, baseline.SampleCount);
    }

    [Fact]
    public async Task A_pre_cancelled_token_throws_and_leaves_the_stored_baseline_and_the_source_untouched()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        _ = await rig.MeasureAsync(2);
        rig.Api.ConstantPowerWatts = 40.0;
        int callsBefore = rig.PowerCalls;
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), Threshold, cancellation.Token));

        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.Equal(callsBefore, rig.PowerCalls);
        rig.Api.ConstantPowerWatts = 60.0;
        EnergyReading reading = await rig.WindowAsync();
        AssertMeasured(reading.NetJoules, 180.0 - (25.0 * 3.0), "J");
    }

    [Fact]
    public async Task Cancelling_during_the_wait_throws_and_leaves_the_stored_baseline_unchanged()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        _ = await rig.MeasureAsync(2);
        rig.Api.ConstantPowerWatts = 40.0;
        using CancellationTokenSource cancellation = new();

        Task<BaselineResult> measuring = rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), Threshold, cancellation.Token).AsTask();
        await rig.TickAsync(50);
        await cancellation.CancelAsync();

        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measuring);
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        rig.Api.ConstantPowerWatts = 60.0;
        EnergyReading reading = await rig.WindowAsync();
        AssertMeasured(reading.NetJoules, 180.0 - (25.0 * 3.0), "J");
    }

    [Fact]
    public async Task Disposing_the_meter_during_the_wait_ends_it_at_once_with_ObjectDisposedException_and_no_further_reads()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        Task<BaselineResult> measuring = rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), Threshold, CancellationToken.None).AsTask();
        await rig.TickAsync(10);
        DateTimeOffset now = rig.Time.GetUtcNow();

        await rig.Meter.DisposeAsync();
        int callsAfterDispose = rig.PowerCalls;

        // Fake time is not advanced again: the wait ended because the meter was disposed, not because 30 s passed.
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await MeterRig.CompleteAsync(measuring));
        Assert.Equal(now, rig.Time.GetUtcNow());
        Assert.Equal(callsAfterDispose, rig.PowerCalls);
    }

    [Fact]
    public async Task A_loop_fault_during_the_wait_ends_it_at_once_with_the_fault_as_the_inner_exception()
    {
        EntryPointNotFoundException fault = new("scripted missing export");
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        Task<BaselineResult> measuring = rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), Threshold, CancellationToken.None).AsTask();
        await rig.TickAsync(10);
        rig.Api.FaultFor = member => member == nameof(INvmlApi.GetPowerUsage) ? fault : null;

        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        DateTimeOffset now = rig.Time.GetUtcNow();

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await MeterRig.CompleteAsync(measuring));
        Assert.Same(fault, failure.InnerException);
        Assert.Equal(now, rig.Time.GetUtcNow());
        Assert.Same(fault, rig.Meter.LoopFault);
    }

    [Fact]
    public async Task A_source_disposed_under_the_meter_during_the_wait_ends_it_at_once_naming_the_source_type()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        Task<BaselineResult> measuring = rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), Threshold, CancellationToken.None).AsTask();
        await rig.TickAsync(10);
        rig.Source.Dispose();

        // The tick reads the disposed source, the loop ends without a fault, and the wait ends with the loop.
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        DateTimeOffset now = rig.Time.GetUtcNow();

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await MeterRig.CompleteAsync(measuring));
        Assert.Equal("The sampling loop stopped.", failure.Message);
        ObjectDisposedException cause = Assert.IsType<ObjectDisposedException>(failure.InnerException);
        Assert.Contains(rig.Source.GetType().Name, cause.ObjectName, StringComparison.Ordinal);
        Assert.Null(rig.Meter.LoopFault);
        Assert.Equal(now, rig.Time.GetUtcNow());
    }

    [Fact]
    public async Task A_meter_whose_loop_faulted_refuses_a_new_baseline_without_reading()
    {
        EntryPointNotFoundException fault = new("scripted missing export");
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        rig.Api.FaultFor = member => member == nameof(INvmlApi.GetPowerUsage) ? fault : null;
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        await MeterRig.SettleAsync(() => rig.Meter.LoopFault is not null);
        int callsBefore = rig.PowerCalls;

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), Threshold, CancellationToken.None));

        Assert.Same(fault, failure.InnerException);
        Assert.Equal(callsBefore, rig.PowerCalls);
    }

    [Fact]
    public async Task Overlapping_measurements_leave_the_one_that_completed_last_as_the_stored_baseline()
    {
        // 20 W before 5 s and 40 W from then on, so the two overlapping windows have different means.
        await using MeterRig rig = MeterRig.Create(watts: 20.0, script: api => api.WattsAt = seconds => seconds < 5.0 ? 20.0 : 40.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        Task<BaselineResult> first = rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(10), Threshold, CancellationToken.None).AsTask();
        await rig.TickAsync(10);
        Task<BaselineResult> second = rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(5), Threshold, CancellationToken.None).AsTask();
        await rig.TickAsync(50);
        BaselineResult secondResult = await MeterRig.CompleteAsync(second);
        Assert.False(first.IsCompleted);
        await rig.TickAsync(40);
        BaselineResult firstResult = await MeterRig.CompleteAsync(first);

        // First: 101 samples, 50 at 20 W and 51 at 40 W. Second (1.0 to 6.0 s): 51 samples, 40 at 20 W and 11 at 40 W.
        AssertMeasured(firstResult.MeanW, 3040.0 / 101.0, "W");
        AssertMeasured(secondResult.MeanW, 1240.0 / 51.0, "W");
        rig.Api.ConstantPowerWatts = 60.0;
        EnergyReading reading = await rig.WindowAsync();

        AssertMeasured(reading.NetJoules, 180.0 - (3040.0 / 101.0 * 3.0), "J");
    }

    [Fact]
    public async Task MeasureBaselineAsync_before_StartAsync_throws_InvalidOperationException_and_reads_nothing()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), Threshold, CancellationToken.None));

        Assert.Equal(0, rig.PowerCalls);
    }

    [Fact]
    public async Task MeasureBaselineAsync_after_DisposeAsync_throws_ObjectDisposedException()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        await rig.Meter.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), Threshold, CancellationToken.None));
    }

    // Task.Delay waits in whole milliseconds, so anything under 1 ms would complete at once with both edges at the same instant.
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1000.0)]
    [InlineData(-1.0)]
    [InlineData(0.5)]
    [InlineData(0.9999)]
    public async Task A_duration_under_one_millisecond_is_rejected_before_any_sample(double milliseconds)
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        int callsBefore = rig.PowerCalls;

        ArgumentOutOfRangeException failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await rig.Meter.MeasureBaselineAsync(TimeSpan.FromMilliseconds(milliseconds), Threshold, CancellationToken.None));

        Assert.Equal("duration", failure.ParamName);
        Assert.Equal(callsBefore, rig.PowerCalls);
    }

    [Fact]
    public async Task A_duration_of_five_thousand_ticks_is_rejected_before_any_sample()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        int callsBefore = rig.PowerCalls;

        ArgumentOutOfRangeException failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await rig.Meter.MeasureBaselineAsync(TimeSpan.FromTicks(5000), Threshold, CancellationToken.None));

        Assert.Equal("duration", failure.ParamName);
        Assert.Equal(callsBefore, rig.PowerCalls);
    }

    [Fact]
    public async Task A_duration_above_what_a_timer_can_wait_is_rejected_before_any_sample()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        int callsBefore = rig.PowerCalls;

        ArgumentOutOfRangeException failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await rig.Meter.MeasureBaselineAsync(TimeSpan.FromMilliseconds(uint.MaxValue), Threshold, CancellationToken.None));

        Assert.Equal("duration", failure.ParamName);
        Assert.Equal(callsBefore, rig.PowerCalls);
    }

    [Fact]
    public async Task An_infinite_timeout_is_rejected_as_a_negative_duration()
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);

        ArgumentOutOfRangeException failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await rig.Meter.MeasureBaselineAsync(Timeout.InfiniteTimeSpan, Threshold, CancellationToken.None));

        Assert.Equal("duration", failure.ParamName);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task A_threshold_that_is_not_finite_and_positive_is_rejected_before_any_sample(double threshold)
    {
        await using MeterRig rig = MeterRig.Create(watts: 25.0);
        await rig.Meter.StartAsync(CancellationToken.None);
        int callsBefore = rig.PowerCalls;

        ArgumentOutOfRangeException failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(30), threshold, CancellationToken.None));

        Assert.Equal("maxCoefficientOfVariation", failure.ParamName);
        Assert.Equal(callsBefore, rig.PowerCalls);
    }

    /// <summary>A started-on-demand sampled meter over a scripted NVML session; fake time only, so nothing waits on the wall clock.</summary>
    private sealed class MeterRig : IAsyncDisposable
    {
        private MeterRig(FakeTimeProvider time, ScriptedNvmlApi api, IGpuTelemetrySource source, SampledEnergyMeter meter)
        {
            Time = time;
            Api = api;
            Source = source;
            Meter = meter;
            PowerCallsAtCreation = api.PowerCallCount;
        }

        private int PowerCallsAtCreation { get; }

        public FakeTimeProvider Time { get; }

        public ScriptedNvmlApi Api { get; }

        public SampledEnergyMeter Meter { get; }

        public IGpuTelemetrySource Source { get; }

        public int PowerCalls => Api.PowerCallCount - PowerCallsAtCreation;

        public static MeterRig Create(double watts, Action<ScriptedNvmlApi>? script = null)
        {
            FakeTimeProvider time = new();
            ScriptedNvmlApi api = new(time) { ConstantPowerWatts = watts, CounterSupported = true };
            script?.Invoke(api);
            GpuOpenResult open = new NvmlGpuTelemetrySourceFactory(api).Open(0);
            Assert.True(open.IsAvailable, open.Reason);
            MeterCapabilities capabilities = MeterMethodSelector.Select("sampling", open.Source, 10.0);
            return new MeterRig(time, api, open.Source, new SampledEnergyMeter(open.Source, capabilities, time));
        }

        /// <summary>Advances one 0.1 s sampling period at a time, yielding (never waiting on the wall clock) until the loop took its tick.</summary>
        public async Task TickAsync(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                int target = Api.PowerCallCount + 1;
                Time.Advance(TimeSpan.FromMilliseconds(100));
                for (int yields = 0; Api.PowerCallCount < target; yields++)
                {
                    Assert.True(yields < MaxYields, "The sampling loop did not take its tick.");
                    await Task.Yield();
                }

                // Reading the count takes the meter lock, which the loop holds until its sample is stored.
                _ = Meter.SampleCount;
            }
        }

        /// <summary>Yields (never waiting on the wall clock, never advancing the fake clock) until the task completes, then awaits it.</summary>
        public static async Task<T> CompleteAsync<T>(Task<T> task)
        {
            await SettleAsync(() => task.IsCompleted);
            return await task;
        }

        public static async Task SettleAsync(Func<bool> condition)
        {
            for (int yields = 0; !condition(); yields++)
            {
                Assert.True(yields < MaxYields, "The awaited condition did not hold.");
                await Task.Yield();
            }
        }

        /// <summary>Measures a baseline of the given whole seconds at the default threshold and advances the fake clock through it.</summary>
        public async Task<BaselineResult> MeasureAsync(int seconds)
        {
            Task<BaselineResult> measuring = Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(seconds), Threshold, CancellationToken.None).AsTask();
            await TickAsync(seconds * 10);
            return await CompleteAsync(measuring);
        }

        /// <summary>Opens a window, advances the fake clock three seconds (30 ticks) and closes it.</summary>
        public async Task<EnergyReading> WindowAsync()
        {
            IMeterWindow window = Meter.OpenWindow();
            await TickAsync(30);
            return window.Close();
        }

        public async ValueTask DisposeAsync()
        {
            await Meter.DisposeAsync();
            Source.Dispose();
            while (Api.IsInitialised)
            {
                Api.Shutdown();
            }
        }
    }
}