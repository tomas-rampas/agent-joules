using System.Collections.ObjectModel;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;

using Xunit;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-005")]
public sealed class AttributionBaselineTests
{
    private const double Tolerance = 1e-9;

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
}