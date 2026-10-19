using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

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

    private const double SelectedRateHz = MeterSelectionFixtures.SelectedRateHz;
    private const string FallbackSentence = "Energy method: sampling (counter unsupported)";
    private const string NotSupportedReason = MeterSelectionFixtures.NotSupportedReason;

    private static ScriptedNvmlApi NewApi(NvmlReturn counter = NvmlReturn.Success) => MeterSelectionFixtures.NewApi(counter);

    private static IGpuTelemetrySource OpenSource(ScriptedNvmlApi api) => MeterSelectionFixtures.OpenSource(api);

    private static MeterCapabilities Select(string? configured, NvmlReturn counter = NvmlReturn.Success)
    {
        using IGpuTelemetrySource source = OpenSource(NewApi(counter));
        return MeterMethodSelector.Select(configured, source, SelectedRateHz);
    }

    private static T SelectionFails<T>(string? configured, NvmlReturn counter = NvmlReturn.Success, double rateHz = SelectedRateHz)
        where T : AgentJoulesException => MeterSelectionFixtures.SelectionFails<T>(configured, counter, rateHz);

    // A one second window at 50 W computed from nothing but what the selector decided.
    private static EnergyReading ReadingFor(MeterCapabilities capabilities) =>
        ReadingCalculator.Compute(new ReadingInput(
            Samples: [Sample(0.0, 50.0), Sample(1.0, 50.0)],
            WindowStartSeconds: 0.0,
            WindowStopSeconds: 1.0,
            Method: capabilities.Method,
            CounterDeltaMillijoules: capabilities.Method == EnergyMethod.Counter ? 50000L : null,
            BaselineMeanWatts: null,
            SampleRateHz: capabilities.SampleRateHz,
            IsSamplingFallback: capabilities.IsFallback));

    [Theory]
    [InlineData(0, null)]
    [InlineData(3, NotSupportedReason)]
    [InlineData(999, "counter-not-supported: 999")]
    public void Opening_and_selecting_auto_reads_the_counter_exactly_once(int code, string? reason)
    {
        ScriptedNvmlApi api = NewApi((NvmlReturn)code);

        using IGpuTelemetrySource source = OpenSource(api);
        Assert.Equal(1, api.CounterCallCount);

        MeterCapabilities capabilities = MeterMethodSelector.Select("auto", source, SelectedRateHz);

        // The selector reads the state the factory decided at Open; it never asks NVML again.
        Assert.Equal(1, api.CounterCallCount);
        Assert.Equal(code == 0, capabilities.CounterSupported);
        Assert.Equal(reason, capabilities.CounterUnsupportedReason);
    }

    [Fact]
    public void Auto_with_the_counter_supported_selects_the_counter_and_is_not_a_fallback()
    {
        MeterCapabilities capabilities = Select("auto");

        Assert.Equal(EnergyMethod.Counter, capabilities.Method);
        Assert.False(capabilities.IsFallback);
        Assert.True(capabilities.CounterSupported);
        Assert.Null(capabilities.CounterUnsupportedReason);
        Assert.Equal(SelectedRateHz, capabilities.SampleRateHz);
        Assert.Equal("Energy method: counter", capabilities.Describe());
    }

    [Fact]
    [Trait("Spec", "EDGE-002")]
    public void Auto_with_the_counter_unsupported_falls_back_to_sampling_and_flags_every_reading()
    {
        MeterCapabilities capabilities = Select("auto", NvmlReturn.NotSupported);

        Assert.Equal(EnergyMethod.Sampling, capabilities.Method);
        Assert.True(capabilities.IsFallback);
        Assert.False(capabilities.CounterSupported);
        Assert.Equal(NotSupportedReason, capabilities.CounterUnsupportedReason);
        Assert.Equal(SelectedRateHz, capabilities.SampleRateHz);

        EnergyReading reading = ReadingFor(capabilities);

        Assert.Equal(EnergyMethod.Sampling, reading.Method);
        Assert.True(reading.Flags.HasFlag(ReadingFlags.SamplingFallback));
        Assert.Contains("sampling-fallback", ReadingFlagNames.ToStrings(reading.Flags));
    }

    [Fact]
    [Trait("Spec", "EDGE-002")]
    public void The_fallback_is_disclosed_with_the_exact_console_sentence()
    {
        Assert.Equal(FallbackSentence, Select("auto", NvmlReturn.NotSupported).Describe());
    }

    [Theory]
    [InlineData(1, "counter-not-supported: 1")]
    [InlineData(2, "counter-not-supported: 2")]
    [InlineData(3, NotSupportedReason)]
    [InlineData(999, "counter-not-supported: 999")]
    [Trait("Spec", "EDGE-002")]
    public void Any_non_zero_counter_return_code_falls_back_and_carries_the_reason(int code, string reason)
    {
        MeterCapabilities capabilities = Select("auto", (NvmlReturn)code);

        Assert.Equal(EnergyMethod.Sampling, capabilities.Method);
        Assert.True(capabilities.IsFallback);
        Assert.Equal(reason, capabilities.CounterUnsupportedReason);
        Assert.Equal(FallbackSentence, capabilities.Describe());
    }

    [Fact]
    public void Forced_sampling_with_the_counter_supported_is_not_a_fallback_and_the_reading_has_no_flag()
    {
        MeterCapabilities capabilities = Select("sampling");

        Assert.Equal(EnergyMethod.Sampling, capabilities.Method);
        Assert.False(capabilities.IsFallback);
        Assert.True(capabilities.CounterSupported);
        Assert.Equal("Energy method: sampling", capabilities.Describe());

        EnergyReading reading = ReadingFor(capabilities);

        Assert.False(reading.Flags.HasFlag(ReadingFlags.SamplingFallback));
        Assert.DoesNotContain("sampling-fallback", ReadingFlagNames.ToStrings(reading.Flags));
    }

    [Fact]
    [Trait("Spec", "EDGE-002")]
    public void Forced_sampling_with_the_counter_unsupported_is_still_not_a_fallback()
    {
        MeterCapabilities capabilities = Select("sampling", NvmlReturn.NotSupported);

        Assert.Equal(EnergyMethod.Sampling, capabilities.Method);
        Assert.False(capabilities.IsFallback);
        Assert.False(capabilities.CounterSupported);
        Assert.Equal("Energy method: sampling", capabilities.Describe());
        Assert.False(ReadingFor(capabilities).Flags.HasFlag(ReadingFlags.SamplingFallback));
    }

    [Fact]
    public void Forced_counter_with_the_counter_supported_selects_the_counter()
    {
        MeterCapabilities capabilities = Select("counter");

        Assert.Equal(EnergyMethod.Counter, capabilities.Method);
        Assert.False(capabilities.IsFallback);
        Assert.Equal("Energy method: counter", capabilities.Describe());
    }

    [Fact]
    public void An_unknown_method_message_names_the_offending_value()
    {
        ConfigurationException failure = SelectionFails<ConfigurationException>("turbo");

        Assert.Contains("turbo", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AUTO", EnergyMethod.Counter)]
    [InlineData("Auto", EnergyMethod.Counter)]
    [InlineData(" counter ", EnergyMethod.Counter)]
    [InlineData("COUNTER", EnergyMethod.Counter)]
    [InlineData("SAMPLING", EnergyMethod.Sampling)]
    [InlineData("\tsampling\n", EnergyMethod.Sampling)]
    public void The_method_is_matched_case_insensitively_after_trimming(string configured, EnergyMethod expected)
    {
        MeterCapabilities capabilities = Select(configured);

        Assert.Equal(expected, capabilities.Method);
        Assert.False(capabilities.IsFallback);
    }

    [Fact]
    public void The_method_is_validated_before_the_sample_rate()
    {
        ConfigurationException failure = SelectionFails<ConfigurationException>("turbo", rateHz: double.NaN);

        Assert.Contains("Meter:Method", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Meter:SampleRateHz", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_source_is_an_ArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(() => { _ = MeterMethodSelector.Select("auto", null!, SelectedRateHz); });
    }

    private sealed class FakeSource(bool supported, string? reason) : IGpuTelemetrySource
    {
        public GpuIdentity Identity { get; } = new("Fake GPU", "GPU-0", "0", null, null, 85.0, "disabled");

        public bool EnergyCounterSupported => supported;

        public string? EnergyCounterUnsupportedReason => reason;

        public double ReadPowerWatts() => 50.0;

        public double? ReadEnergyMillijoules() => null;

        public ThrottleReasons ReadThrottleReasons() => ThrottleReasons.Unsupported;

        public void Dispose()
        {
        }
    }

    private static readonly string HostileMethod =
        "x\u001b[31mRED\u001b[0m\nEnergy method: counter\r\u202e" + new string('A', 5000);

    [Fact]
    public void A_long_hostile_method_value_is_not_echoed_only_its_length()
    {
        ConfigurationException failure = SelectionFails<ConfigurationException>(HostileMethod);

        Assert.All(failure.Message, c => Assert.True(c >= ' ', "The message holds a control character."));
        Assert.DoesNotContain('\u202e', failure.Message);
        Assert.True(failure.Message.Length < 200, $"The message is {failure.Message.Length} characters long.");
        Assert.Contains("Meter:Method", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"a value of {HostileMethod.Trim().Length} characters", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Energy method", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_hostile_method_value_is_echoed_with_control_and_format_characters_replaced()
    {
        ConfigurationException failure = SelectionFails<ConfigurationException>("x\u001b[31m\u202e");

        Assert.Contains("'x?[31m?'", failure.Message, StringComparison.Ordinal);
        Assert.All(failure.Message, c => Assert.True(c >= ' ', "The message holds a control character."));
    }

    [Fact]
    public void A_method_value_of_16_characters_is_echoed_and_one_of_17_is_only_counted()
    {
        string sixteen = new('q', 16);
        string seventeen = new('q', 17);

        string echoed = SelectionFails<ConfigurationException>(sixteen).Message;
        string counted = SelectionFails<ConfigurationException>(seventeen).Message;

        Assert.Contains(sixteen, echoed, StringComparison.Ordinal);
        Assert.DoesNotContain(seventeen, counted, StringComparison.Ordinal);
        Assert.DoesNotContain("qqqq", counted, StringComparison.Ordinal);
        Assert.Contains("a value of 17 characters", counted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_method_is_printed_as_null()
    {
        Assert.Contains("(null)", SelectionFails<ConfigurationException>(null).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExitCode.Success)]
    [InlineData((ExitCode)42)]
    [InlineData((ExitCode)(-1))]
    public void AgentJoulesException_rejects_success_and_undefined_codes(ExitCode code)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new AgentJoulesException(code, "message"));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new AgentJoulesException(code, "message", new InvalidOperationException("inner")));
    }

    [Fact]
    public void The_unsupported_reason_from_the_source_is_filtered_before_it_reaches_a_message_or_the_capabilities()
    {
        using var source = new FakeSource(supported: false, reason: "bad\u001b[31m\nreason");

        MeterCapabilities capabilities = MeterMethodSelector.Select("auto", source, SelectedRateHz);
        MeasurementPreconditionException failure = Assert.Throws<MeasurementPreconditionException>(
            () => MeterMethodSelector.Select("counter", source, SelectedRateHz));

        Assert.Equal("bad?[31m?reason", capabilities.CounterUnsupportedReason);
        Assert.Contains("bad?[31m?reason", failure.Message, StringComparison.Ordinal);
        Assert.All(failure.Message, c => Assert.True(c >= ' ', "The message holds a control character."));
    }

    [Fact]
    public void A_very_long_unsupported_reason_is_bounded()
    {
        using var source = new FakeSource(supported: false, reason: new string('r', 5000));

        MeterCapabilities capabilities = MeterMethodSelector.Select("auto", source, SelectedRateHz);

        Assert.NotNull(capabilities.CounterUnsupportedReason);
        Assert.True(capabilities.CounterUnsupportedReason.Length <= 70);
    }

    [Fact]
    public void A_missing_reason_on_an_unsupported_source_is_filled_in()
    {
        using var source = new FakeSource(supported: false, reason: null);

        Assert.Equal("counter-not-supported", MeterMethodSelector.Select("auto", source, SelectedRateHz).CounterUnsupportedReason);
    }

    [Fact]
    public void MeterCapabilities_accepts_the_three_consistent_combinations_and_describes_them()
    {
        var counter = new MeterCapabilities(EnergyMethod.Counter, true, null, 10.0, false);
        var sampling = new MeterCapabilities(EnergyMethod.Sampling, true, null, 10.0, false);
        var forcedWithoutCounter = new MeterCapabilities(EnergyMethod.Sampling, false, "why", 10.0, false);
        var fallback = new MeterCapabilities(EnergyMethod.Sampling, false, "why", 10.0, true);

        Assert.Equal("Energy method: counter", counter.Describe());
        Assert.Equal("Energy method: sampling", sampling.Describe());
        Assert.Equal("Energy method: sampling", forcedWithoutCounter.Describe());
        Assert.Equal(FallbackSentence, fallback.Describe());
        Assert.Equal(10.0, fallback.SampleRateHz);
    }

    [Fact]
    public void MeterCapabilities_rejects_an_undefined_method()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MeterCapabilities(default, true, null, 10.0, false));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MeterCapabilities((EnergyMethod)99, true, null, 10.0, false));
    }

    [Fact]
    public void MeterCapabilities_rejects_a_fallback_that_is_not_sampling_without_the_counter()
    {
        _ = Assert.ThrowsAny<ArgumentException>(() => new MeterCapabilities(EnergyMethod.Counter, true, null, 10.0, true));
        _ = Assert.ThrowsAny<ArgumentException>(() => new MeterCapabilities(EnergyMethod.Sampling, true, null, 10.0, true));
    }

    [Fact]
    public void MeterCapabilities_rejects_the_counter_method_without_a_supported_counter()
    {
        _ = Assert.ThrowsAny<ArgumentException>(() => new MeterCapabilities(EnergyMethod.Counter, false, "why", 10.0, false));
    }

    [Fact]
    public void MeterCapabilities_requires_the_reason_exactly_when_the_counter_is_unsupported()
    {
        _ = Assert.ThrowsAny<ArgumentException>(() => new MeterCapabilities(EnergyMethod.Sampling, true, "why", 10.0, false));
        _ = Assert.ThrowsAny<ArgumentException>(() => new MeterCapabilities(EnergyMethod.Sampling, false, null, 10.0, false));
        _ = Assert.ThrowsAny<ArgumentException>(() => new MeterCapabilities(EnergyMethod.Sampling, false, string.Empty, 10.0, false));
        _ = Assert.ThrowsAny<ArgumentException>(() => new MeterCapabilities(EnergyMethod.Sampling, false, "   ", 10.0, false));
        _ = Assert.ThrowsAny<ArgumentException>(() => new MeterCapabilities(EnergyMethod.Sampling, true, string.Empty, 10.0, false));
    }

    [Theory]
    [InlineData(0x200B)]
    [InlineData(0x0085)]
    [InlineData(0x202E)]
    public void A_reason_of_invisible_characters_only_is_treated_as_missing(int character)
    {
        string reason = new((char)character, 3);

        MeterCapabilities capabilities = MeterMethodSelector.Select("auto", false, reason, SelectedRateHz);
        MeasurementPreconditionException failure = Assert.Throws<MeasurementPreconditionException>(
            () => MeterMethodSelector.Select("counter", false, reason, SelectedRateHz));

        Assert.Equal("counter-not-supported", capabilities.CounterUnsupportedReason);
        Assert.Contains("(counter-not-supported)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reason_with_visible_text_next_to_invisible_characters_keeps_the_text()
    {
        string reason = "why" + (char)0x200B;

        Assert.Equal("why?", MeterMethodSelector.Select("auto", false, reason, SelectedRateHz).CounterUnsupportedReason);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MeterCapabilities_rejects_a_sample_rate_that_is_not_positive_and_finite(double rateHz)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MeterCapabilities(EnergyMethod.Sampling, true, null, rateHz, false));
    }

    [Theory]
    [InlineData("auto", MeterMethodSetting.Auto)]
    [InlineData(" AUTO ", MeterMethodSetting.Auto)]
    [InlineData("Counter", MeterMethodSetting.Counter)]
    [InlineData("\tsampling\n", MeterMethodSetting.Sampling)]
    public void Parse_trims_and_ignores_case(string configured, MeterMethodSetting expected)
    {
        Assert.Equal(expected, MeterMethodSelector.Parse(configured));
    }

    [Theory]
    [InlineData("turbo")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Parse_rejects_anything_else_with_a_configuration_error_naming_the_setting(string? configured)
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => MeterMethodSelector.Parse(configured));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains("Meter:Method", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrySelect_returns_true_with_the_capabilities_for_a_supported_counter()
    {
        using IGpuTelemetrySource source = OpenSource(NewApi());

        bool selected = MeterMethodSelector.TrySelect(MeterMethodSetting.Auto, source, SelectedRateHz, out MeterCapabilities? capabilities);

        Assert.True(selected);
        Assert.NotNull(capabilities);
        Assert.Equal(EnergyMethod.Counter, capabilities.Method);
        Assert.False(capabilities.IsFallback);
    }

    [Fact]
    public void TrySelect_auto_without_the_counter_returns_true_with_the_fallback()
    {
        using IGpuTelemetrySource source = OpenSource(NewApi(NvmlReturn.NotSupported));

        bool selected = MeterMethodSelector.TrySelect(MeterMethodSetting.Auto, source, SelectedRateHz, out MeterCapabilities? capabilities);

        Assert.True(selected);
        Assert.NotNull(capabilities);
        Assert.True(capabilities.IsFallback);
        Assert.Equal(NotSupportedReason, capabilities.CounterUnsupportedReason);
    }

    [Fact]
    public void TrySelect_forced_sampling_returns_true_and_is_not_a_fallback()
    {
        using IGpuTelemetrySource source = OpenSource(NewApi(NvmlReturn.NotSupported));

        bool selected = MeterMethodSelector.TrySelect(MeterMethodSetting.Sampling, source, SelectedRateHz, out MeterCapabilities? capabilities);

        Assert.True(selected);
        Assert.NotNull(capabilities);
        Assert.Equal(EnergyMethod.Sampling, capabilities.Method);
        Assert.False(capabilities.IsFallback);
    }

    [Fact]
    [Trait("Spec", "EDGE-002")]
    public void TrySelect_returns_false_with_a_null_value_only_for_a_forced_counter_that_is_unsupported()
    {
        using IGpuTelemetrySource source = OpenSource(NewApi(NvmlReturn.NotSupported));

        bool selected = MeterMethodSelector.TrySelect(MeterMethodSetting.Counter, source, SelectedRateHz, out MeterCapabilities? capabilities);

        Assert.False(selected);
        Assert.Null(capabilities);
    }

    [Fact]
    public void TrySelect_guards_its_arguments()
    {
        using IGpuTelemetrySource source = OpenSource(NewApi());

        _ = Assert.Throws<ArgumentNullException>(() => MeterMethodSelector.TrySelect(MeterMethodSetting.Auto, null!, SelectedRateHz, out _));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => MeterMethodSelector.TrySelect(default, source, SelectedRateHz, out _));
        ConfigurationException rate = Assert.Throws<ConfigurationException>(
            () => MeterMethodSelector.TrySelect(MeterMethodSetting.Auto, source, double.NaN, out _));
        Assert.Contains("Meter:SampleRateHz", rate.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MeterMethodSetting.Auto, 0, true)]
    [InlineData(MeterMethodSetting.Counter, 0, true)]
    [InlineData(MeterMethodSetting.Sampling, 3, true)]
    [InlineData(MeterMethodSetting.Counter, 3, false)]
    public void TrySelect_agrees_with_Select(MeterMethodSetting setting, int code, bool expected)
    {
        using IGpuTelemetrySource source = OpenSource(NewApi((NvmlReturn)code));

        bool selected = MeterMethodSelector.TrySelect(setting, source, SelectedRateHz, out MeterCapabilities? capabilities);

        Assert.Equal(expected, selected);
        if (expected)
        {
            Assert.Equal(MeterMethodSelector.Select(setting.ToString(), source, SelectedRateHz), capabilities);
        }
        else
        {
            _ = Assert.Throws<MeasurementPreconditionException>(() => MeterMethodSelector.Select(setting.ToString(), source, SelectedRateHz));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_or_blank_reason_becomes_counter_not_supported_for_a_source_and_for_values(string? reason)
    {
        using var source = new FakeSource(supported: false, reason: reason);

        MeterCapabilities fromSource = MeterMethodSelector.Select("auto", source, SelectedRateHz);
        MeterCapabilities fromValues = MeterMethodSelector.Select("auto", false, reason, SelectedRateHz);
        MeasurementPreconditionException failure = Assert.Throws<MeasurementPreconditionException>(
            () => MeterMethodSelector.Select("counter", false, reason, SelectedRateHz));

        Assert.Equal("counter-not-supported", fromSource.CounterUnsupportedReason);
        Assert.Equal(fromSource, fromValues);
        Assert.Contains("(counter-not-supported)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_supported_counter_drops_a_stale_reason()
    {
        using var source = new FakeSource(supported: true, reason: "stale");

        MeterCapabilities fromSource = MeterMethodSelector.Select("auto", source, SelectedRateHz);
        MeterCapabilities fromValues = MeterMethodSelector.Select("sampling", true, "stale", SelectedRateHz);

        Assert.Equal(EnergyMethod.Counter, fromSource.Method);
        Assert.True(fromSource.CounterSupported);
        Assert.Null(fromSource.CounterUnsupportedReason);
        Assert.Null(fromValues.CounterUnsupportedReason);
    }

    [Fact]
    public void The_source_free_Select_decides_like_the_source_overload()
    {
        MeterCapabilities fallback = MeterMethodSelector.Select("auto", false, NotSupportedReason, SelectedRateHz);
        MeterCapabilities counter = MeterMethodSelector.Select(" Counter ", true, null, SelectedRateHz);

        Assert.Equal(Select("auto", NvmlReturn.NotSupported), fallback);
        Assert.Equal(Select("counter"), counter);
        Assert.Equal(FallbackSentence, fallback.Describe());
    }

    [Fact]
    public void The_source_free_Select_rejects_a_bad_method_and_a_forced_unsupported_counter()
    {
        _ = Assert.Throws<ConfigurationException>(() => MeterMethodSelector.Select("turbo", true, null, SelectedRateHz));
        MeasurementPreconditionException failure = Assert.Throws<MeasurementPreconditionException>(
            () => MeterMethodSelector.Select("counter", false, NotSupportedReason, SelectedRateHz));

        Assert.Equal(ExitCode.PreconditionFailed, failure.ExitCode);
        Assert.Contains("Meter:Method=counter", failure.Message, StringComparison.Ordinal);
        Assert.Contains(NotSupportedReason, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_source_free_TrySelect_returns_false_only_for_a_forced_unsupported_counter()
    {
        Assert.True(MeterMethodSelector.TrySelect(MeterMethodSetting.Auto, false, "why", SelectedRateHz, out MeterCapabilities? fallback));
        Assert.True(fallback.IsFallback);
        Assert.Equal("why", fallback.CounterUnsupportedReason);

        Assert.True(MeterMethodSelector.TrySelect(MeterMethodSetting.Counter, true, null, SelectedRateHz, out MeterCapabilities? counter));
        Assert.Equal(EnergyMethod.Counter, counter.Method);

        Assert.False(MeterMethodSelector.TrySelect(MeterMethodSetting.Counter, false, "why", SelectedRateHz, out MeterCapabilities? none));
        Assert.Null(none);
    }

    [Fact]
    public void The_source_free_TrySelect_guards_the_setting_and_the_rate()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => MeterMethodSelector.TrySelect(default, true, null, SelectedRateHz, out _));
        _ = Assert.Throws<ConfigurationException>(() => MeterMethodSelector.TrySelect(MeterMethodSetting.Auto, true, null, 0.0, out _));
    }

    [Fact]
    [Trait("Spec", "EDGE-002")]
    public void The_sample_rate_is_validated_before_the_forced_counter_precondition()
    {
        using IGpuTelemetrySource source = OpenSource(NewApi(NvmlReturn.NotSupported));

        ConfigurationException fromSource = Assert.Throws<ConfigurationException>(
            () => MeterMethodSelector.Select("counter", source, double.NaN));
        ConfigurationException fromValues = Assert.Throws<ConfigurationException>(
            () => MeterMethodSelector.Select("counter", false, NotSupportedReason, double.NaN));

        Assert.Contains("Meter:SampleRateHz", fromSource.Message, StringComparison.Ordinal);
        Assert.Contains("Meter:SampleRateHz", fromValues.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExitCode.Success, 0)]
    [InlineData(ExitCode.Unexpected, 1)]
    [InlineData(ExitCode.InvalidArguments, 2)]
    [InlineData(ExitCode.MissingKey, 3)]
    [InlineData(ExitCode.ModelNotFound, 4)]
    [InlineData(ExitCode.Unreachable, 5)]
    [InlineData(ExitCode.PreconditionFailed, 6)]
    [InlineData(ExitCode.ContentDiffers, 7)]
    public void ExitCode_members_carry_the_documented_process_exit_values(ExitCode code, int value)
    {
        Assert.Equal(value, (int)code);
    }

    [Fact]
    public void ExitCode_has_exactly_the_eight_documented_members()
    {
        Assert.Equal(8, Enum.GetValues<ExitCode>().Length);
    }

    [Fact]
    public void AgentJoulesException_exposes_its_exit_code_message_and_inner_exception()
    {
        var inner = new InvalidOperationException("inner");

        var plain = new AgentJoulesException(ExitCode.Unreachable, "unreachable");
        var wrapped = new AgentJoulesException(ExitCode.ModelNotFound, "model missing", inner);

        Assert.IsType<Exception>(plain, exactMatch: false);
        Assert.Equal(ExitCode.Unreachable, plain.ExitCode);
        Assert.Equal("unreachable", plain.Message);
        Assert.Null(plain.InnerException);
        Assert.Equal(ExitCode.ModelNotFound, wrapped.ExitCode);
        Assert.Equal("model missing", wrapped.Message);
        Assert.Same(inner, wrapped.InnerException);
    }

    [Fact]
    public void ConfigurationException_is_fixed_to_exit_code_2()
    {
        var plain = new ConfigurationException("bad setting");
        var wrapped = new ConfigurationException("bad setting", new FormatException("inner"));

        Assert.IsType<AgentJoulesException>(plain, exactMatch: false);
        Assert.Equal(ExitCode.InvalidArguments, plain.ExitCode);
        Assert.Equal(ExitCode.InvalidArguments, wrapped.ExitCode);
        Assert.Equal("bad setting", plain.Message);
        Assert.IsType<FormatException>(wrapped.InnerException);
        Assert.True(typeof(ConfigurationException).IsSealed);
    }

    [Fact]
    public void MeasurementPreconditionException_is_fixed_to_exit_code_6()
    {
        var plain = new MeasurementPreconditionException("no counter");
        var wrapped = new MeasurementPreconditionException("no counter", new FormatException("inner"));

        Assert.IsType<AgentJoulesException>(plain, exactMatch: false);
        Assert.Equal(ExitCode.PreconditionFailed, plain.ExitCode);
        Assert.Equal(ExitCode.PreconditionFailed, wrapped.ExitCode);
        Assert.Equal("no counter", plain.Message);
        Assert.IsType<FormatException>(wrapped.InnerException);
        Assert.True(typeof(MeasurementPreconditionException).IsSealed);
    }

    // AJ-028 (EDGE-013): throttle detection. Every window below has samples at i / 10 s, both edges included, 50 W each.
    private const ulong GpuIdleBit = 0x1;
    private const ulong ApplicationsClocksBit = 0x2;
    private const ulong SwPowerCapBit = 0x4;
    private const ulong HwSlowdownBit = 0x8;
    private const ulong SyncBoostBit = 0x10;
    private const ulong SwThermalSlowdownBit = 0x20;
    private const ulong HwThermalSlowdownBit = 0x40;
    private const ulong HwPowerBrakeSlowdownBit = 0x80;

    private static readonly string[] AllThrottleNames =
        ["SwPowerCap", "HwSlowdown", "SwThermalSlowdown", "HwThermalSlowdown", "HwPowerBrakeSlowdown"];

    private static readonly JsonSerializerOptions ThrottleJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static ThrottleReasons Read(ulong bits) => new(bits, true);

    private static PowerSample[] ThrottleWindow(int count, Func<int, ThrottleReasons> reasonsAt) =>
        [.. Enumerable.Range(0, count).Select(i => new PowerSample(i / 10.0, 50.0, reasonsAt(i)))];

    private static PowerSample[] ThrottleWindowOf(params ThrottleReasons[] reasons) =>
        ThrottleWindow(reasons.Length, i => reasons[i]);

    private static EnergyReading ThrottleReading(PowerSample[] samples, EnergyMethod method = EnergyMethod.Sampling, long? deltaMillijoules = null) =>
        ReadingCalculator.Compute(new ReadingInput(
            Samples: samples,
            WindowStartSeconds: 0.0,
            WindowStopSeconds: samples[^1].Seconds,
            Method: method,
            CounterDeltaMillijoules: deltaMillijoules,
            BaselineMeanWatts: null,
            SampleRateHz: 10.0,
            IsSamplingFallback: false));

    private static string[] ReasonNames(EnergyReading reading) => [.. Assert.IsType<IEnumerable<string>>(reading.ThrottleReasons, exactMatch: false)];

    private static string[] NamesOf(ThrottleReasons reasons) => [.. reasons.Names];

    private static bool IsFlaggedThrottled(EnergyReading reading) => reading.Flags.HasFlag(ReadingFlags.Throttled);

    private static EnergyReading BuildReading(ReadingFlags flags, IReadOnlyList<string>? reasons) =>
        new(
            Metric.Measured(180.0, "J"),
            Metric.NotReported("no-baseline"),
            Metric.Measured(60.0, "W"),
            Metric.Measured(60.0, "W"),
            3.0,
            EnergyMethod.Sampling,
            31,
            10.0,
            flags,
            reasons);

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_power_cap_bit_in_every_sample_flags_the_reading_and_names_SwPowerCap()
    {
        PowerSample[] samples = ThrottleWindow(11, _ => Read(0x4));

        EnergyReading reading = ThrottleReading(samples);

        Assert.True(IsFlaggedThrottled(reading));
        Assert.Contains("throttled", ReadingFlagNames.ToStrings(reading.Flags));
        Assert.Equal(["SwPowerCap"], ReasonNames(reading));
    }

    [Theory]
    [Trait("Spec", "EDGE-013")]
    [InlineData(SwPowerCapBit, "SwPowerCap")]
    [InlineData(HwSlowdownBit, "HwSlowdown")]
    [InlineData(SwThermalSlowdownBit, "SwThermalSlowdown")]
    [InlineData(HwThermalSlowdownBit, "HwThermalSlowdown")]
    [InlineData(HwPowerBrakeSlowdownBit, "HwPowerBrakeSlowdown")]
    public void Each_of_the_five_throttle_bits_alone_flags_the_reading_and_yields_its_name(ulong bits, string name)
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(5, _ => Read(bits)));

        Assert.True(IsFlaggedThrottled(reading));
        Assert.Equal([name], ReasonNames(reading));
        Assert.True(Read(bits).IsThrottled);
        Assert.Equal([name], NamesOf(Read(bits)));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void The_bit_values_are_the_nvml_clock_event_constants()
    {
        Assert.Equal(typeof(ulong), Enum.GetUnderlyingType(typeof(ThrottleReasonBits)));
        Assert.Equal(0x1UL, (ulong)ThrottleReasonBits.GpuIdle);
        Assert.Equal(0x2UL, (ulong)ThrottleReasonBits.ApplicationsClocksSetting);
        Assert.Equal(0x4UL, (ulong)ThrottleReasonBits.SwPowerCap);
        Assert.Equal(0x8UL, (ulong)ThrottleReasonBits.HwSlowdown);
        Assert.Equal(0x10UL, (ulong)ThrottleReasonBits.SyncBoost);
        Assert.Equal(0x20UL, (ulong)ThrottleReasonBits.SwThermalSlowdown);
        Assert.Equal(0x40UL, (ulong)ThrottleReasonBits.HwThermalSlowdown);
        Assert.Equal(0x80UL, (ulong)ThrottleReasonBits.HwPowerBrakeSlowdown);
        Assert.Equal(8, Enum.GetValues<ThrottleReasonBits>().Length);
        Assert.Equal(0xECUL, ThrottleReasons.ThrottlingMask);
    }

    [Theory]
    [Trait("Spec", "EDGE-013")]
    [InlineData(GpuIdleBit)]
    [InlineData(ApplicationsClocksBit)]
    [InlineData(SyncBoostBit)]
    [InlineData(GpuIdleBit | ApplicationsClocksBit | SyncBoostBit)]
    [InlineData(0x0UL)]
    public void Idle_applications_clocks_and_sync_boost_bits_do_not_flag_and_leave_an_empty_list(ulong bits)
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(11, _ => Read(bits)));

        Assert.False(IsFlaggedThrottled(reading));
        Assert.DoesNotContain("throttled", ReadingFlagNames.ToStrings(reading.Flags));
        Assert.NotNull(reading.ThrottleReasons);
        Assert.Empty(reading.ThrottleReasons);
        Assert.False(Read(bits).IsThrottled);
        Assert.Empty(Read(bits).Names);
    }

    [Theory]
    [Trait("Spec", "EDGE-013")]
    [InlineData(0x100UL)]
    [InlineData(0x200UL)]
    [InlineData(0x400UL)]
    [InlineData(0x8000000000000000UL)]
    public void A_bit_outside_the_five_throttling_bits_does_not_flag_the_reading(ulong bits)
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(11, _ => Read(bits)));

        Assert.False(IsFlaggedThrottled(reading));
        Assert.Empty(ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void An_idle_bit_next_to_a_throttle_bit_names_only_the_throttle_reason()
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(11, _ => Read(GpuIdleBit | SwPowerCapBit | SyncBoostBit)));

        Assert.True(IsFlaggedThrottled(reading));
        Assert.Equal(["SwPowerCap"], ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_reason_in_only_the_last_sample_still_flags_the_reading()
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(11, i => Read(i == 10 ? HwThermalSlowdownBit : 0UL)));

        Assert.True(IsFlaggedThrottled(reading));
        Assert.Equal(["HwThermalSlowdown"], ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_reason_in_only_the_first_sample_still_flags_the_reading()
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(11, i => Read(i == 0 ? HwThermalSlowdownBit : 0UL)));

        Assert.True(IsFlaggedThrottled(reading));
        Assert.Equal(["HwThermalSlowdown"], ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_reason_in_only_a_middle_sample_still_flags_the_reading()
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(11, i => Read(i == 5 ? HwSlowdownBit : 0UL)));

        Assert.True(IsFlaggedThrottled(reading));
        Assert.Equal(["HwSlowdown"], ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void Two_different_bits_in_different_samples_give_both_names_in_bit_order()
    {
        // The higher bit comes first in time; the names still follow the bit value, not the time order.
        PowerSample[] samples = ThrottleWindow(11, i => Read(i == 1 ? HwThermalSlowdownBit : i == 9 ? SwPowerCapBit : 0UL));

        EnergyReading reading = ThrottleReading(samples);

        Assert.True(IsFlaggedThrottled(reading));
        Assert.Equal(["SwPowerCap", "HwThermalSlowdown"], ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void The_same_bit_in_many_samples_appears_once()
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(11, i => Read(i % 2 == 0 ? SwPowerCapBit : SwPowerCapBit | HwSlowdownBit)));

        Assert.Equal(["SwPowerCap", "HwSlowdown"], ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void All_five_bits_spread_over_the_window_give_the_five_names_in_bit_order_without_duplicates()
    {
        ulong[] byTimeDescending =
            [HwPowerBrakeSlowdownBit, HwThermalSlowdownBit, SwThermalSlowdownBit, HwSlowdownBit, SwPowerCapBit];
        PowerSample[] samples = ThrottleWindow(10, i => Read(byTimeDescending[i / 2]));

        EnergyReading reading = ThrottleReading(samples);

        Assert.Equal(AllThrottleNames, ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_throttled_reading_with_the_counter_method_keeps_the_flag_and_the_reasons()
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(11, _ => Read(SwPowerCapBit)), EnergyMethod.Counter, 50000L);

        Assert.True(IsFlaggedThrottled(reading));
        Assert.Equal(["SwPowerCap"], ReasonNames(reading));
        Assert.Equal(EnergyMethod.Counter, reading.Method);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void The_throttled_flag_is_the_only_flag_the_throttle_adds()
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(11, _ => Read(SwPowerCapBit)));

        Assert.Equal(ReadingFlags.Throttled, reading.Flags);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void When_every_sample_is_unsupported_the_reasons_are_null_and_no_flag_is_set()
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(11, _ => ThrottleReasons.Unsupported));

        Assert.Null(reading.ThrottleReasons);
        Assert.False(IsFlaggedThrottled(reading));
        Assert.Equal(ReadingFlags.None, reading.Flags);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void Mixed_supported_and_unsupported_samples_give_the_union_of_the_supported_ones()
    {
        PowerSample[] samples = ThrottleWindowOf(
            ThrottleReasons.Unsupported,
            Read(SwPowerCapBit),
            ThrottleReasons.Unsupported,
            Read(HwSlowdownBit),
            ThrottleReasons.Unsupported);

        EnergyReading reading = ThrottleReading(samples);

        Assert.True(IsFlaggedThrottled(reading));
        Assert.Equal(["SwPowerCap", "HwSlowdown"], ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void An_empty_list_needs_every_sample_supported_so_one_unsupported_sample_with_nothing_active_gives_null()
    {
        PowerSample[] samples = ThrottleWindowOf(ThrottleReasons.Unsupported, Read(0UL), ThrottleReasons.Unsupported);

        EnergyReading reading = ThrottleReading(samples);

        Assert.Null(reading.ThrottleReasons);
        Assert.False(IsFlaggedThrottled(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_throttling_bit_between_unsupported_samples_still_flags_and_names_it()
    {
        PowerSample[] samples = ThrottleWindowOf(ThrottleReasons.Unsupported, Read(SwPowerCapBit), ThrottleReasons.Unsupported);

        EnergyReading reading = ThrottleReading(samples);

        Assert.True(IsFlaggedThrottled(reading));
        Assert.Equal(["SwPowerCap"], ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_window_without_samples_makes_no_claim_about_throttling()
    {
        EnergyReading reading = ReadingCalculator.Compute(new ReadingInput(
            Samples: [],
            WindowStartSeconds: 0.0,
            WindowStopSeconds: 1.0,
            Method: EnergyMethod.Sampling,
            CounterDeltaMillijoules: null,
            BaselineMeanWatts: null,
            SampleRateHz: 10.0,
            IsSamplingFallback: false));

        Assert.Null(reading.ThrottleReasons);
        Assert.False(IsFlaggedThrottled(reading));
    }

    [Theory]
    [Trait("Spec", "EDGE-013")]
    [InlineData(SwPowerCapBit, true)]
    [InlineData(SwPowerCapBit, false)]
    public void ReadingNames_lists_a_throttling_name_on_any_evidence_whatever_the_coverage(ulong bits, bool everyReadSupported)
    {
        IReadOnlyList<string>? names = Read(bits).ReadingNames(everyReadSupported);

        Assert.NotNull(names);
        string[] actual = [.. names];
        Assert.Equal(["SwPowerCap"], actual);
    }

    [Theory]
    [Trait("Spec", "EDGE-013")]
    [InlineData(0UL)]
    [InlineData(GpuIdleBit)]
    public void ReadingNames_is_an_empty_list_only_when_every_read_was_supported_and_nothing_is_active(ulong bits)
    {
        IReadOnlyList<string>? full = Read(bits).ReadingNames(everyReadSupported: true);

        Assert.NotNull(full);
        Assert.Empty(full);
        Assert.Null(Read(bits).ReadingNames(everyReadSupported: false));
    }

    [Theory]
    [Trait("Spec", "EDGE-013")]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadingNames_is_null_for_an_unsupported_union(bool everyReadSupported)
    {
        Assert.Null(ThrottleReasons.Unsupported.ReadingNames(everyReadSupported));
    }

    // EDGE-013 names a fake NVML: the samples below take their reasons from NvmlGpuTelemetrySource.ReadThrottleReasons().
    private static PowerSample[] SamplesFromFake(ScriptedNvmlApi api, int count, Action<int> scriptSample)
    {
        using IGpuTelemetrySource source = OpenSource(api);
        PowerSample[] samples = new PowerSample[count];
        for (int i = 0; i < count; i++)
        {
            scriptSample(i);
            samples[i] = new PowerSample(i / 10.0, 50.0, source.ReadThrottleReasons());
        }

        return samples;
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_power_cap_bit_scripted_in_the_fake_nvml_drives_a_throttled_reading()
    {
        ScriptedNvmlApi api = NewApi();
        PowerSample[] samples = SamplesFromFake(api, 11, _ => api.ClockEventReasons = SwPowerCapBit);

        EnergyReading reading = ThrottleReading(samples);

        Assert.All(samples, sample => Assert.Equal(Read(SwPowerCapBit), sample.Reasons));
        Assert.True(IsFlaggedThrottled(reading));
        Assert.Equal(["SwPowerCap"], ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_bit_that_appears_part_way_through_the_fake_window_is_enough()
    {
        // The fake derives the bits from its clock: the cap starts at 0.5 s and each sample advances the clock by 100 ms.
        var time = new FakeTimeProvider();
        var api = new ScriptedNvmlApi(time) { ConstantPowerWatts = 50, CounterSupported = true, ClockEventReasonsAt = seconds => seconds >= 0.5 ? SwPowerCapBit : 0UL };
        PowerSample[] samples = SamplesFromFake(api, 11, i =>
        {
            if (i > 0)
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
            }
        });

        EnergyReading reading = ThrottleReading(samples);

        Assert.Equal(Read(0UL), samples[0].Reasons);
        Assert.Equal(Read(SwPowerCapBit), samples[^1].Reasons);
        Assert.True(IsFlaggedThrottled(reading));
        Assert.Equal(["SwPowerCap"], ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void An_idle_bit_scripted_in_the_fake_nvml_gives_no_flag_and_an_empty_list()
    {
        ScriptedNvmlApi api = NewApi();
        PowerSample[] samples = SamplesFromFake(api, 11, _ => api.ClockEventReasons = GpuIdleBit);

        EnergyReading reading = ThrottleReading(samples);

        Assert.False(IsFlaggedThrottled(reading));
        Assert.Empty(ReasonNames(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_fake_nvml_that_reports_the_clock_events_as_not_supported_gives_null_reasons_and_no_flag()
    {
        ScriptedNvmlApi api = NewApi();
        api.ClockEventReturn = NvmlReturn.NotSupported;
        PowerSample[] samples = SamplesFromFake(api, 11, _ => api.ClockEventReasons = SwPowerCapBit);

        EnergyReading reading = ThrottleReading(samples);

        Assert.All(samples, sample => Assert.Equal(ThrottleReasons.Unsupported, sample.Reasons));
        Assert.Null(reading.ThrottleReasons);
        Assert.False(IsFlaggedThrottled(reading));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void The_bits_of_an_unsupported_sample_never_count()
    {
        // An unsupported value should carry no bits, but a hand-built one that does must still be ignored.
        PowerSample[] samples = ThrottleWindowOf(new ThrottleReasons(HwPowerBrakeSlowdownBit, false), Read(0UL), Read(0UL));

        EnergyReading reading = ThrottleReading(samples);

        // The first sample is unsupported, so the window cannot claim that nothing was active: null, not an empty list.
        Assert.False(IsFlaggedThrottled(reading));
        Assert.Null(reading.ThrottleReasons);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void Compute_reads_the_throttle_bits_without_changing_the_energy_numbers()
    {
        EnergyReading throttled = ThrottleReading(ThrottleWindow(11, _ => Read(SwPowerCapBit)));
        EnergyReading plain = ThrottleReading(ThrottleWindow(11, _ => ThrottleReasons.Unsupported));

        Assert.Equal(plain.GrossJoules, throttled.GrossJoules);
        Assert.Equal(plain.NetJoules, throttled.NetJoules);
        Assert.Equal(plain.AverageWatts, throttled.AverageWatts);
        Assert.Equal(plain.PeakWatts, throttled.PeakWatts);
        Assert.Equal(plain.SampleCount, throttled.SampleCount);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void The_reading_serialises_the_reason_names_as_an_array_and_unsupported_as_null()
    {
        EnergyReading throttled = ThrottleReading(ThrottleWindow(11, _ => Read(SwPowerCapBit)));
        EnergyReading unsupported = ThrottleReading(ThrottleWindow(11, _ => ThrottleReasons.Unsupported));
        EnergyReading idle = ThrottleReading(ThrottleWindow(11, _ => Read(GpuIdleBit)));

        string throttledJson = JsonSerializer.Serialize(throttled, ThrottleJsonOptions);
        string unsupportedJson = JsonSerializer.Serialize(unsupported, ThrottleJsonOptions);
        string idleJson = JsonSerializer.Serialize(idle, ThrottleJsonOptions);

        Assert.Contains("\"throttleReasons\":[\"SwPowerCap\"]", throttledJson, StringComparison.Ordinal);
        Assert.Contains("\"flags\":[\"throttled\"]", throttledJson, StringComparison.Ordinal);
        Assert.Contains("\"throttleReasons\":null", unsupportedJson, StringComparison.Ordinal);
        Assert.Contains("\"flags\":[]", unsupportedJson, StringComparison.Ordinal);
        Assert.Contains("\"throttleReasons\":[]", idleJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_throttled_reading_round_trips_through_json()
    {
        EnergyReading reading = ThrottleReading(ThrottleWindow(11, i => Read(i < 5 ? SwPowerCapBit : HwThermalSlowdownBit)));

        EnergyReading? copy = JsonSerializer.Deserialize<EnergyReading>(JsonSerializer.Serialize(reading, ThrottleJsonOptions), ThrottleJsonOptions);

        Assert.NotNull(copy);
        Assert.Equal(["SwPowerCap", "HwThermalSlowdown"], ReasonNames(copy));
        Assert.True(IsFlaggedThrottled(copy));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void A_reading_may_carry_the_flag_with_names_or_no_flag_with_an_empty_list_or_null()
    {
        EnergyReading flagged = BuildReading(ReadingFlags.Throttled, ["SwPowerCap", "HwThermalSlowdown"]);
        EnergyReading allFive = BuildReading(ReadingFlags.Throttled, AllThrottleNames);
        EnergyReading empty = BuildReading(ReadingFlags.None, []);
        EnergyReading unsupported = BuildReading(ReadingFlags.None, null);

        Assert.Equal(["SwPowerCap", "HwThermalSlowdown"], ReasonNames(flagged));
        Assert.Equal(AllThrottleNames, ReasonNames(allFive));
        Assert.Empty(ReasonNames(empty));
        Assert.Null(unsupported.ThrottleReasons);
    }

    // The rejection tests below live here beside the throttle tests and carry both ids; each asserts the exact exception type and the parameter.
    private static void AssertRejected(string parameterName, Func<object> build)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => build());

        Assert.Equal(parameterName, failure.ParamName);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    [Trait("Spec", "REQ-005")]
    public void The_throttled_flag_without_any_reasons_is_rejected()
    {
        AssertRejected("flags", () => BuildReading(ReadingFlags.Throttled, null));
        AssertRejected("flags", () => BuildReading(ReadingFlags.Throttled, []));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    [Trait("Spec", "REQ-005")]
    public void Reasons_without_the_throttled_flag_are_rejected()
    {
        AssertRejected("flags", () => BuildReading(ReadingFlags.None, ["SwPowerCap"]));
        AssertRejected("flags", () => BuildReading(ReadingFlags.BelowBaseline | ReadingFlags.LowResolution, ["HwSlowdown"]));
    }

    [Theory]
    [Trait("Spec", "EDGE-013")]
    [Trait("Spec", "REQ-005")]
    [InlineData("sw-power-cap")]
    [InlineData("swpowercap")]
    [InlineData("GpuIdle")]
    [InlineData("SyncBoost")]
    [InlineData("ApplicationsClocksSetting")]
    [InlineData("Unknown")]
    public void A_reason_that_is_not_one_of_the_five_names_is_rejected(string name)
    {
        AssertRejected("throttleReasons", () => BuildReading(ReadingFlags.Throttled, ["SwPowerCap", name]));
        AssertRejected("throttleReasons", () => BuildReading(ReadingFlags.Throttled, [name]));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    [Trait("Spec", "REQ-005")]
    public void A_duplicate_reason_is_rejected()
    {
        AssertRejected("throttleReasons", () => BuildReading(ReadingFlags.Throttled, ["SwPowerCap", "SwPowerCap"]));
        AssertRejected("throttleReasons", () => BuildReading(ReadingFlags.Throttled, ["SwPowerCap", "HwSlowdown", "SwPowerCap"]));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    [Trait("Spec", "REQ-005")]
    public void Reasons_out_of_bit_order_are_rejected()
    {
        AssertRejected("throttleReasons", () => BuildReading(ReadingFlags.Throttled, ["HwThermalSlowdown", "SwPowerCap"]));
        AssertRejected("throttleReasons", () => BuildReading(ReadingFlags.Throttled, ["HwSlowdown", "SwPowerCap"]));
        AssertRejected("throttleReasons", () => BuildReading(ReadingFlags.Throttled, [.. AllThrottleNames.Reverse()]));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    [Trait("Spec", "REQ-005")]
    public void A_document_with_the_flag_and_no_reasons_or_the_reverse_is_rejected_on_deserialisation()
    {
        const string flagOnly = "{\"grossJoules\":{\"value\":180,\"label\":\"measured\",\"unit\":\"J\"},\"netJoules\":{\"value\":null,\"label\":\"not-reported\",\"reason\":\"no-baseline\"},"
            + "\"avgW\":{\"value\":60,\"label\":\"measured\",\"unit\":\"W\"},\"peakW\":{\"value\":60,\"label\":\"measured\",\"unit\":\"W\"},"
            + "\"durationSeconds\":3,\"method\":\"sampling\",\"sampleCount\":31,\"sampleRateHz\":10,\"flags\":[\"throttled\"],\"throttleReasons\":null}";
        string reasonsOnly = flagOnly.Replace("\"flags\":[\"throttled\"],\"throttleReasons\":null", "\"flags\":[],\"throttleReasons\":[\"SwPowerCap\"]", StringComparison.Ordinal);

        Assert.NotEqual(flagOnly, reasonsOnly);
        AssertRejected("flags", () => JsonSerializer.Deserialize<EnergyReading>(flagOnly, ThrottleJsonOptions)!);
        AssertRejected("flags", () => JsonSerializer.Deserialize<EnergyReading>(reasonsOnly, ThrottleJsonOptions)!);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void Names_lists_the_set_throttle_bits_in_bit_order()
    {
        Assert.Equal(AllThrottleNames, NamesOf(Read(ThrottleReasons.ThrottlingMask)));
        Assert.Equal(AllThrottleNames, NamesOf(Read(ulong.MaxValue)));
        Assert.Equal(["HwSlowdown", "HwPowerBrakeSlowdown"], NamesOf(Read(HwPowerBrakeSlowdownBit | HwSlowdownBit | GpuIdleBit)));
        Assert.Empty(Read(0UL).Names);
        Assert.Empty(ThrottleReasons.Unsupported.Names);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void An_unsupported_value_is_never_throttled_and_has_no_names_whatever_its_bits_say()
    {
        var odd = new ThrottleReasons(SwPowerCapBit, false);

        Assert.False(odd.IsThrottled);
        Assert.Empty(odd.Names);
        Assert.False(ThrottleReasons.Unsupported.IsThrottled);
    }

    [Theory]
    [Trait("Spec", "EDGE-013")]
    [InlineData(GpuIdleBit, false)]
    [InlineData(ApplicationsClocksBit, false)]
    [InlineData(SyncBoostBit, false)]
    [InlineData(SwPowerCapBit, true)]
    [InlineData(HwSlowdownBit, true)]
    [InlineData(SwThermalSlowdownBit, true)]
    [InlineData(HwThermalSlowdownBit, true)]
    [InlineData(HwPowerBrakeSlowdownBit, true)]
    [InlineData(GpuIdleBit | SwPowerCapBit, true)]
    [InlineData(0UL, false)]
    public void IsThrottled_is_true_only_for_a_supported_value_with_a_throttle_bit(ulong bits, bool expected)
    {
        Assert.Equal(expected, Read(bits).IsThrottled);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void Union_of_no_sample_or_only_unsupported_samples_is_unsupported()
    {
        Assert.Equal(ThrottleReasons.Unsupported, ThrottleReasons.Union([]));
        Assert.Equal(
            ThrottleReasons.Unsupported,
            ThrottleReasons.Union(ThrottleWindow(4, _ => ThrottleReasons.Unsupported).Select(sample => sample.Reasons)));
        Assert.False(ThrottleReasons.Union([]).Supported);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void Union_ors_the_bits_of_the_supported_samples_and_is_supported()
    {
        PowerSample[] samples = ThrottleWindowOf(
            Read(SwPowerCapBit),
            ThrottleReasons.Unsupported,
            Read(SwPowerCapBit | GpuIdleBit),
            new ThrottleReasons(HwPowerBrakeSlowdownBit, false),
            Read(HwThermalSlowdownBit));

        ThrottleReasons union = ThrottleReasons.Union(samples.Select(sample => sample.Reasons));

        Assert.True(union.Supported);
        Assert.Equal(SwPowerCapBit | GpuIdleBit | HwThermalSlowdownBit, union.Bits);
        Assert.Equal(["SwPowerCap", "HwThermalSlowdown"], NamesOf(union));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void Union_of_one_supported_sample_with_no_bits_is_supported_with_zero_bits()
    {
        ThrottleReasons union = ThrottleReasons.Union(ThrottleWindowOf(Read(0UL), ThrottleReasons.Unsupported).Select(sample => sample.Reasons));

        Assert.Equal(new ThrottleReasons(0UL, true), union);
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void Union_over_a_sequence_of_reads_follows_the_same_rules()
    {
        ThrottleReasons[] reads = [Read(SwPowerCapBit), ThrottleReasons.Unsupported, Read(HwSlowdownBit)];

        Assert.Equal(new ThrottleReasons(SwPowerCapBit | HwSlowdownBit, true), ThrottleReasons.Union(reads));
        ThrottleReasons[] onlyUnsupported = [ThrottleReasons.Unsupported];
        Assert.Equal(ThrottleReasons.Unsupported, ThrottleReasons.Union(onlyUnsupported));
        Assert.Equal(ThrottleReasons.Unsupported, ThrottleReasons.Union([]));
        Assert.Throws<ArgumentNullException>(() => ThrottleReasons.Union(null!));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void Or_combines_two_reads_and_ignores_the_bits_of_an_unsupported_one()
    {
        var unsupportedWithBits = new ThrottleReasons(HwSlowdownBit, false);

        Assert.Equal(Read(SwPowerCapBit | HwSlowdownBit), Read(SwPowerCapBit).Or(Read(HwSlowdownBit)));
        Assert.Equal(Read(SwPowerCapBit), Read(SwPowerCapBit).Or(unsupportedWithBits));
        Assert.Equal(Read(SwPowerCapBit), unsupportedWithBits.Or(Read(SwPowerCapBit)));
        Assert.Equal(ThrottleReasons.Unsupported, unsupportedWithBits.Or(ThrottleReasons.Unsupported));
        Assert.Equal(ThrottleReasons.Unsupported, ThrottleReasons.Unsupported.Or(ThrottleReasons.Unsupported));
    }

    [Fact]
    [Trait("Spec", "EDGE-013")]
    public void The_throttling_mask_equals_the_or_of_the_bits_the_five_names_stand_for()
    {
        ulong fromNames = 0;
        foreach (string name in ThrottleReasons.AllNames)
        {
            fromNames |= (ulong)Enum.Parse<ThrottleReasonBits>(name);
        }

        Assert.Equal(ThrottleReasons.ThrottlingMask, fromNames);
        Assert.Equal(AllThrottleNames, ThrottleReasons.AllNames.ToArray());
        Assert.Same(ThrottleReasons.AllNames, ThrottleReasons.AllNames);
        Assert.IsNotType<string[]>(ThrottleReasons.AllNames);
    }
}