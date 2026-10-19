using System.Collections.ObjectModel;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;

using Xunit;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-004")]
public sealed class PowerIntegrationTests
{
    private const double Tolerance = 1e-9;

    private static PowerSample Sample(double seconds, double watts) =>
        new(seconds, watts, ThrottleReasons.Unsupported);

    // These helpers call the span overloads, so the tests below pin them; the list overloads have their own tests.
    private static double? Integrate(PowerSample[] samples, double startSeconds, double stopSeconds) =>
        PowerIntegrator.IntegrateJoules(samples.AsSpan(), startSeconds, stopSeconds);

    private static double? Peak(PowerSample[] samples, double startSeconds, double stopSeconds) =>
        PowerIntegrator.PeakWatts(samples.AsSpan(), startSeconds, stopSeconds);

    private static ArgumentException Rejected(PowerSample[] samples, double startSeconds, double stopSeconds) =>
        Assert.ThrowsAny<ArgumentException>(() => { _ = Integrate(samples, startSeconds, stopSeconds); });

    private static ArgumentException PeakRejected(PowerSample[] samples, double startSeconds, double stopSeconds) =>
        Assert.ThrowsAny<ArgumentException>(() => { _ = Peak(samples, startSeconds, stopSeconds); });

    private static PowerSample[] Constant(IEnumerable<double> timestamps, double watts) =>
        [.. timestamps.Select(t => Sample(t, watts))];

    private static double[] DividedTimestamps(int count) =>
        [.. Enumerable.Range(0, count).Select(i => i / 10.0)];

    // Accumulates the step the way a loop that does "t += 0.1" would, so the timestamps carry float noise.
    private static double[] AccumulatedTimestamps(int count, double step)
    {
        double[] timestamps = new double[count];
        double t = 0.0;
        for (int i = 0; i < count; i++)
        {
            timestamps[i] = t;
            t += step;
        }

        return timestamps;
    }

    private static IReadOnlyList<PowerSample> AsList(int kind, PowerSample[] samples)
    {
        // A List<T> (viewed in place) and a ReadOnlyCollection<T> (copied once) exercise both non-array paths.
        List<PowerSample> list = [.. samples];
        return kind switch
        {
            0 => samples,
            1 => list,
            _ => new ReadOnlyCollection<PowerSample>(samples),
        };
    }

    [Fact]
    public void Constant50WattsOverTwoSecondsIsAbout100Joules()
    {
        // 50000 mW is 50 W; 21 samples at i / 10.0 include both edges.
        PowerSample[] samples = Constant(DividedTimestamps(21), 50000 / 1000.0);

        double? joules = Integrate(samples, 0.0, 2.0);

        Assert.NotNull(joules);
        Assert.InRange(joules.Value, 99.0, 101.0);
        Assert.Equal(100.0, joules.Value, Tolerance);
    }

    [Fact]
    public void AccumulatedTimestampsWithFloatNoiseStillGiveAbout100Joules()
    {
        double[] timestamps = AccumulatedTimestamps(21, 0.1);
        PowerSample[] samples = Constant(timestamps, 50.0);

        // Twenty additions of 0.1 land a few ULPs past 2.0, so the window closes at the last sample's own timestamp,
        // as a meter does when it takes the closing sample at the exact close instant.
        double? joules = Integrate(samples, 0.0, timestamps[^1]);

        Assert.NotNull(joules);
        Assert.InRange(joules.Value, 99.0, 101.0);
    }

    [Fact]
    public void LinearRampAtIrregularTimestampsMatchesTheAnalyticIntegral()
    {
        double[] timestamps = [0.0, 0.05, 0.40, 1.00, 1.75, 2.0];
        PowerSample[] samples = [.. timestamps.Select(t => Sample(t, 40.0 + 10.0 * t))];

        double? joules = Integrate(samples, 0.0, 2.0);

        Assert.NotNull(joules);
        Assert.InRange(joules.Value, 99.0, 101.0);

        // The trapezoid is exact for a linear ramp, so the match is limited by rounding only.
        Assert.InRange(Math.Abs(joules.Value - 100.0) / 100.0, 0.0, Tolerance);
    }

    [Fact]
    public void FirstAndLastSamplesAreHeldToTheWindowEdges()
    {
        PowerSample[] samples = [Sample(0.2, 50.0), Sample(0.8, 50.0)];

        double? joules = Integrate(samples, 0.0, 1.0);

        // 50 W x 0.2 s held, 50 W x 0.6 s between the samples, 50 W x 0.2 s held; a tolerance because 0.8 - 0.2 is not exactly 0.6.
        Assert.NotNull(joules);
        Assert.Equal(50.0, joules.Value, Tolerance);
    }

    [Fact]
    public void HeldEdgesUseEachSamplesOwnWattsAndHoldWidth()
    {
        // 40 W x 0.1 s held at the start, 50 W x 0.6 s between, 60 W x 0.3 s held at the end.
        // A swapped hold (first watts to the end, last watts to the start) gives 48 J; a mean-watts hold gives 50 J.
        PowerSample[] samples = [Sample(0.1, 40.0), Sample(0.7, 60.0)];

        double? joules = Integrate(samples, 0.0, 1.0);

        Assert.NotNull(joules);
        Assert.Equal(52.0, joules.Value, Tolerance);
    }

    [Fact]
    public void ActualTimestampsBeatTheNominalInterval()
    {
        double[] timestamps = [0.0, 0.1, 0.5, 0.6, 1.0];
        PowerSample[] samples = [.. timestamps.Select(t => Sample(t, 100.0 * t))];

        double? joules = Integrate(samples, 0.0, 1.0);

        // The 1 percent band around 50 J is the proof: the integral lies inside it, and the nominal-interval alternatives below do not.
        Assert.NotNull(joules);
        Assert.InRange(joules.Value, 49.5, 50.5);

        // Four gaps over one second make a nominal interval of 0.25 s.
        // Sum of watts x 0.25 s = 220 x 0.25 = 55 J; nominal trapezoid = (5 + 30 + 55 + 80) x 0.25 = 42.5 J.
        double nominalSumJoules = samples.Sum(s => s.Watts) * 0.25;
        double nominalTrapezoidJoules = 0.0;
        for (int i = 1; i < samples.Length; i++)
        {
            nominalTrapezoidJoules += 0.5 * (samples[i - 1].Watts + samples[i].Watts) * 0.25;
        }

        Assert.Equal(55.0, nominalSumJoules, Tolerance);
        Assert.Equal(42.5, nominalTrapezoidJoules, Tolerance);
        Assert.NotInRange(nominalSumJoules, 49.5, 50.5);
        Assert.NotInRange(nominalTrapezoidJoules, 49.5, 50.5);
    }

    [Fact]
    public void ListOverloadGivesTheSameResultAsTheSpanOverload()
    {
        List<PowerSample> list = [Sample(0.0, 40.0), Sample(1.0, 50.0), Sample(2.0, 60.0)];
        IReadOnlyList<PowerSample> readOnly = list;

        double? fromList = PowerIntegrator.IntegrateJoules(readOnly, 0.0, 2.0);
        double? fromSpan = Integrate([.. list], 0.0, 2.0);

        Assert.NotNull(fromList);
        Assert.Equal(100.0, fromList.Value, Tolerance);
        Assert.Equal(fromSpan, fromList);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ListOverloadsOfArrayListAndReadOnlyCollectionMatchTheSpanOverloads(int kind)
    {
        PowerSample[] samples = [Sample(0.0, 40.0), Sample(1.0, 50.0), Sample(2.0, 60.0)];
        IReadOnlyList<PowerSample> list = AsList(kind, samples);

        double? joules = PowerIntegrator.IntegrateJoules(list, 0.0, 2.0);
        double? average = PowerIntegrator.AverageWatts(list, 0.0, 2.0);
        double? peak = PowerIntegrator.PeakWatts(list, 0.0, 2.0);

        Assert.NotNull(joules);
        Assert.NotNull(average);
        Assert.NotNull(peak);
        Assert.Equal(Integrate(samples, 0.0, 2.0)!.Value, joules.Value, Tolerance);
        Assert.Equal(PowerIntegrator.AverageWatts(samples.AsSpan(), 0.0, 2.0)!.Value, average.Value, Tolerance);
        Assert.Equal(Peak(samples, 0.0, 2.0)!.Value, peak.Value, Tolerance);
        Assert.Equal(100.0, joules.Value, Tolerance);
        Assert.Equal(50.0, average.Value, Tolerance);
        Assert.Equal(60.0, peak.Value, Tolerance);
    }

    [Fact]
    public void ANullListIsRejectedByEveryListOverload()
    {
        IReadOnlyList<PowerSample> none = null!;

        Assert.Throws<ArgumentNullException>(() => { _ = PowerIntegrator.IntegrateJoules(none, 0.0, 1.0); });
        Assert.Throws<ArgumentNullException>(() => { _ = PowerIntegrator.AverageWatts(none, 0.0, 1.0); });
        Assert.Throws<ArgumentNullException>(() => { _ = PowerIntegrator.PeakWatts(none, 0.0, 1.0); });
    }

    [Fact]
    public void EqualTimestampsAreRejectedAndTheMessageNamesTheIndex()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(0.5, 50.0), Sample(0.5, 50.0)];

        ArgumentException failure = Rejected(samples, 0.0, 1.0);

        Assert.Contains("index 2", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecreasingTimestampsAreRejectedAndTheMessageNamesTheIndex()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(0.3, 50.0), Sample(0.2, 50.0)];

        ArgumentException failure = Rejected(samples, 0.0, 2.0);

        Assert.Contains("index 2", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASampleBeforeTheWindowStartIsRejectedAndTheMessageNamesTheIndex()
    {
        PowerSample[] samples = [Sample(-0.1, 50.0), Sample(0.5, 50.0), Sample(1.0, 50.0)];

        ArgumentException failure = Rejected(samples, 0.0, 1.0);

        Assert.Contains("index 0", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASampleAfterTheWindowStopIsRejectedAndTheMessageNamesTheIndex()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(1.0, 50.0), Sample(2.1, 50.0)];

        ArgumentException failure = Rejected(samples, 0.0, 2.0);

        Assert.Contains("index 2", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASampleRejectionNamesTheSamplesParameter()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(0.5, -1.0)];

        ArgumentException failure = Rejected(samples, 0.0, 1.0);

        Assert.Equal("samples", failure.ParamName);
    }

    [Fact]
    public void ALoneSampleOutsideTheWindowIsRejectedRatherThanReportedAsNull()
    {
        // Validation runs before the fewer-than-two-samples shortcut, so a bad lone sample is an error, not a null.
        PowerSample[] samples = [Sample(2.5, 50.0)];

        ArgumentException failure = Rejected(samples, 0.0, 2.0);

        Assert.Contains("index 0", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SamplesExactlyOnTheWindowEdgesAreAccepted()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(1.0, 50.0)];

        double? joules = Integrate(samples, 0.0, 1.0);

        Assert.NotNull(joules);
        Assert.Equal(50.0, joules.Value, Tolerance);
    }

    [Fact]
    public void ASingleSampleReturnsNullNeverZero()
    {
        PowerSample[] samples = [Sample(1.0, 50.0)];

        double? joules = Integrate(samples, 0.0, 2.0);

        Assert.Null(joules);
    }

    [Fact]
    public void NoSamplesReturnNullNeverZero()
    {
        ReadOnlySpan<PowerSample> none = [];

        double? joules = PowerIntegrator.IntegrateJoules(none, 0.0, 2.0);

        Assert.Null(joules);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.0)]
    public void AnEmptyOrInvertedWindowIsRejected(double startSeconds, double stopSeconds)
    {
        // No samples: the window is validated before the fewer-than-two-samples shortcut, and no sample can be blamed instead.
        PowerSample[] samples = [];

        _ = Rejected(samples, startSeconds, stopSeconds);
    }

    [Fact]
    public void ANonFiniteWindowBoundIsRejected()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(1.0, 50.0)];

        _ = Rejected(samples, double.NaN, 1.0);
        _ = Rejected(samples, 0.0, double.PositiveInfinity);
    }

    [Fact]
    public void AWindowWhoseDurationOverflowsIsRejectedAndTheMessageNamesTheWindow()
    {
        PowerSample[] samples = [];

        ArgumentException failure = Rejected(samples, -1e308, 1e308);

        Assert.Contains("window", failure.Message, StringComparison.Ordinal);
        Assert.Contains("window", PeakRejected(samples, -1e308, 1e308).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIntegralThatOverflowsThrowsArgumentOutOfRangeNamingTheSamples()
    {
        PowerSample[] samples = [Sample(0.0, 1e308), Sample(10.0, 1e308)];

        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => { _ = Integrate(samples, 0.0, 10.0); });

        Assert.Equal("samples", failure.ParamName);
    }

    [Fact]
    public void NaNWattsAreRejectedAndTheMessageNamesTheIndex()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(0.5, double.NaN), Sample(1.0, 50.0)];

        ArgumentException failure = Rejected(samples, 0.0, 1.0);

        Assert.Contains("index 1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InfiniteWattsAreRejectedAndTheMessageNamesTheIndex()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(0.5, double.PositiveInfinity), Sample(1.0, 50.0)];

        ArgumentException failure = Rejected(samples, 0.0, 1.0);

        Assert.Contains("index 1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InfiniteTimestampIsRejectedAndTheMessageNamesTheIndex()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(double.PositiveInfinity, 50.0)];

        ArgumentException failure = Rejected(samples, 0.0, 1.0);

        Assert.Contains("index 1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NaNTimestampIsRejectedAndTheMessageNamesTheIndex()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(double.NaN, 50.0)];

        ArgumentException failure = Rejected(samples, 0.0, 1.0);

        Assert.Contains("index 1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeWattsAreRejectedAndTheMessageNamesTheIndex()
    {
        PowerSample[] samples = [Sample(0.0, 50.0), Sample(0.5, -1.0), Sample(1.0, 50.0)];

        ArgumentException failure = Rejected(samples, 0.0, 1.0);

        Assert.Contains("index 1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeZeroWattsIntegrateToPositiveZero()
    {
        PowerSample[] samples = [Sample(0.0, -0.0), Sample(1.0, -0.0)];

        double? joules = Integrate(samples, 0.0, 1.0);

        Assert.NotNull(joules);
        Assert.Equal(0.0, joules.Value);
        Assert.False(double.IsNegative(joules.Value));

        double? peak = Peak(samples, 0.0, 1.0);

        Assert.NotNull(peak);
        Assert.Equal(0.0, peak.Value);
        Assert.False(double.IsNegative(peak.Value));
    }

    [Fact]
    public void AverageWattsIsTheIntegralOverTheWindowDuration()
    {
        // 100 J over a 2 s window.
        PowerSample[] samples = Constant(DividedTimestamps(21), 50.0);

        double? average = PowerIntegrator.AverageWatts(samples.AsSpan(), 0.0, 2.0);

        Assert.NotNull(average);
        Assert.Equal(50.0, average.Value, Tolerance);
    }

    [Fact]
    public void AverageWattsIsNullWhenTheIntegralIsNull()
    {
        PowerSample[] samples = [Sample(1.0, 50.0)];

        double? average = PowerIntegrator.AverageWatts(samples.AsSpan(), 0.0, 2.0);

        Assert.Null(average);
    }

    [Fact]
    public void PeakWattsIsTheLargestSampleWatts()
    {
        PowerSample[] samples = [Sample(0.0, 40.0), Sample(0.5, 75.5), Sample(1.0, 60.0)];

        double? peak = Peak(samples, 0.0, 1.0);

        Assert.NotNull(peak);
        Assert.Equal(75.5, peak.Value, Tolerance);
    }

    [Fact]
    public void PeakWattsOfTheRampSamplesIs100Watts()
    {
        double[] timestamps = [0.0, 0.1, 0.5, 0.6, 1.0];
        PowerSample[] samples = [.. timestamps.Select(t => Sample(t, 100.0 * t))];

        double? peak = Peak(samples, 0.0, 1.0);

        Assert.NotNull(peak);
        Assert.Equal(100.0, peak.Value, Tolerance);
    }

    [Fact]
    public void PeakWattsIsNullForFewerThanTwoSamples()
    {
        PowerSample[] one = [Sample(0.5, 50.0)];

        Assert.Null(Peak(one, 0.0, 1.0));
        Assert.Null(Peak([], 0.0, 1.0));
    }

    [Fact]
    public void PeakWattsRejectsNaNInfiniteAndNegativeWattsNamingTheIndex()
    {
        PowerSample[] nan = [Sample(0.0, 50.0), Sample(0.5, double.NaN), Sample(1.0, 50.0)];
        PowerSample[] infinite = [Sample(0.0, 50.0), Sample(0.5, double.PositiveInfinity), Sample(1.0, 50.0)];
        PowerSample[] negative = [Sample(0.0, 50.0), Sample(0.5, -1.0), Sample(1.0, 50.0)];

        Assert.Contains("index 1", PeakRejected(nan, 0.0, 1.0).Message, StringComparison.Ordinal);
        Assert.Contains("index 1", PeakRejected(infinite, 0.0, 1.0).Message, StringComparison.Ordinal);
        Assert.Contains("index 1", PeakRejected(negative, 0.0, 1.0).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PeakWattsRejectsASampleOutsideTheWindowAndAnInvertedWindow()
    {
        PowerSample[] outside = [Sample(0.0, 50.0), Sample(1.5, 50.0)];

        Assert.Contains("index 1", PeakRejected(outside, 0.0, 1.0).Message, StringComparison.Ordinal);
        _ = PeakRejected([], 1.0, 1.0);
    }
}