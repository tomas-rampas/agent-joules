using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>AJ-040: the meter of the hosted tiers and the unmetered method it needs in the capabilities and in the energy reading (REQ-008).</summary>
public sealed class UnmeteredEnergyMeterTests
{
    private static readonly Metric NotApplicable = Metric.NotApplicable("hosted-tier");

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_closed_window_reports_all_four_energy_metrics_as_NA_hosted_tier()
    {
        FakeTimeProvider time = new();
        await using UnmeteredEnergyMeter meter = new(time);
        await meter.StartAsync(CancellationToken.None);

        IMeterWindow window = meter.OpenWindow();
        time.Advance(TimeSpan.FromMilliseconds(1500));
        EnergyReading reading = window.Close();

        Assert.Equal(NotApplicable, reading.GrossJoules);
        Assert.Equal(NotApplicable, reading.NetJoules);
        Assert.Equal(NotApplicable, reading.AverageWatts);
        Assert.Equal(NotApplicable, reading.PeakWatts);
        Assert.Equal(EnergyMethod.Unmetered, reading.Method);
        Assert.Equal(0, reading.SampleCount);
        Assert.Equal(0.0, reading.SampleRateHz);
        Assert.Equal(1.5, reading.DurationSeconds, 9);
        Assert.Equal(ReadingFlags.None, reading.Flags);
        Assert.Null(reading.ThrottleReasons);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task The_window_span_counts_seconds_since_the_meter_started()
    {
        FakeTimeProvider time = new();
        await using UnmeteredEnergyMeter meter = new(time);
        await meter.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(4));

        IMeterWindow window = meter.OpenWindow();
        Assert.Equal(new WindowSpan(4.0, 4.0), window.Span);
        time.Advance(TimeSpan.FromSeconds(2));
        window.Close();

        Assert.Equal(new WindowSpan(4.0, 6.0), window.Span);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task The_capabilities_say_there_is_no_counter_no_rate_and_no_fallback()
    {
        await using UnmeteredEnergyMeter meter = new(new FakeTimeProvider());

        MeterCapabilities capabilities = meter.Capabilities;

        Assert.Equal(EnergyMethod.Unmetered, capabilities.Method);
        Assert.False(capabilities.CounterSupported);
        Assert.Equal("hosted-tier", capabilities.CounterUnsupportedReason);
        Assert.Equal(0.0, capabilities.SampleRateHz);
        Assert.False(capabilities.IsFallback);
        Assert.Equal("Energy method: unmetered (hosted tier)", capabilities.Describe());
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_hosted_tier_has_no_baseline_and_asking_for_one_is_an_error()
    {
        await using UnmeteredEnergyMeter meter = new(new FakeTimeProvider());
        await meter.StartAsync(CancellationToken.None);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await meter.MeasureBaselineAsync(TimeSpan.FromSeconds(1), 0.1, CancellationToken.None));

        Assert.Contains("hosted", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_baseline_asked_of_a_disposed_meter_faults_with_object_disposed()
    {
        UnmeteredEnergyMeter meter = new(new FakeTimeProvider());
        await meter.StartAsync(CancellationToken.None);
        await meter.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await meter.MeasureBaselineAsync(TimeSpan.FromSeconds(1), 0.1, CancellationToken.None));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_window_cannot_open_before_the_meter_started()
    {
        await using UnmeteredEnergyMeter meter = new(new FakeTimeProvider());

        Assert.Throws<InvalidOperationException>(() => meter.OpenWindow());
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task The_meter_starts_once_and_honours_a_cancelled_token()
    {
        await using UnmeteredEnergyMeter meter = new(new FakeTimeProvider());
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await meter.StartAsync(cancelled.Token));
        await meter.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await meter.StartAsync(CancellationToken.None));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_window_closes_once_and_needs_a_duration()
    {
        FakeTimeProvider time = new();
        await using UnmeteredEnergyMeter meter = new(time);
        await meter.StartAsync(CancellationToken.None);

        IMeterWindow instant = meter.OpenWindow();
        Assert.Throws<InvalidOperationException>(() => instant.Close());

        IMeterWindow window = meter.OpenWindow();
        time.Advance(TimeSpan.FromSeconds(1));
        window.Close();
        Assert.Throws<InvalidOperationException>(() => window.Close());
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public async Task A_disposed_meter_refuses_every_call()
    {
        FakeTimeProvider time = new();
        UnmeteredEnergyMeter meter = new(time);
        await meter.StartAsync(CancellationToken.None);
        IMeterWindow window = meter.OpenWindow();
        time.Advance(TimeSpan.FromSeconds(1));

        await meter.DisposeAsync();
        await meter.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => meter.OpenWindow());
        Assert.Throws<ObjectDisposedException>(() => window.Close());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await meter.StartAsync(CancellationToken.None));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_null_time_provider_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new UnmeteredEnergyMeter(null!));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void Unmetered_capabilities_accept_only_the_hosted_combination()
    {
        MeterCapabilities hosted = new(EnergyMethod.Unmetered, false, "hosted-tier", 0.0, false);
        Assert.Equal(EnergyMethod.Unmetered, hosted.Method);

        Assert.Throws<ArgumentException>(() => new MeterCapabilities(EnergyMethod.Unmetered, true, null, 0.0, false));
        Assert.Throws<ArgumentException>(() => new MeterCapabilities(EnergyMethod.Unmetered, false, "counter-not-supported: x", 0.0, false));
        Assert.Throws<ArgumentException>(() => new MeterCapabilities(EnergyMethod.Unmetered, false, "hosted-tier", 0.0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeterCapabilities(EnergyMethod.Unmetered, false, "hosted-tier", 10.0, false));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void A_zero_rate_stays_illegal_for_the_measuring_methods()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeterCapabilities(EnergyMethod.Sampling, false, "counter-not-supported: x", 0.0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeterCapabilities(EnergyMethod.Counter, true, null, 0.0, false));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void An_unmetered_energy_reading_accepts_only_NA_metrics_and_no_samples()
    {
        EnergyReading valid = Unmetered();
        Assert.Equal(EnergyMethod.Unmetered, valid.Method);

        Assert.Equal("grossJoules", Assert.Throws<ArgumentException>(() => Unmetered(gross: Metric.Measured(1, "J"))).ParamName);
        Assert.Equal("netJoules", Assert.Throws<ArgumentException>(() => Unmetered(net: Metric.NotReported("x"))).ParamName);
        Assert.Equal("sampleCount", Assert.Throws<ArgumentException>(() => Unmetered(count: 3)).ParamName);
        Assert.Equal("sampleRateHz", Assert.Throws<ArgumentException>(() => Unmetered(rate: 10.0)).ParamName);
        Assert.Equal("flags", Assert.Throws<ArgumentException>(() => Unmetered(flags: ReadingFlags.LowResolution)).ParamName);
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void The_calculator_never_computes_an_unmetered_reading()
    {
        ReadingInput input = new(
            WindowStartSeconds: 0.0,
            WindowStopSeconds: 3.0,
            Method: EnergyMethod.Unmetered,
            CounterDeltaMillijoules: null,
            Samples: [new PowerSample(0.0, 60.0, ThrottleReasons.Unsupported), new PowerSample(3.0, 60.0, ThrottleReasons.Unsupported)],
            SampleRateHz: 10.0,
            BaselineMeanWatts: null,
            IsSamplingFallback: false);

        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReadingCalculator.Compute(input));

        Assert.Equal("input", failure.ParamName);
        Assert.Contains("ReadingInput.Method", failure.Message, StringComparison.Ordinal);
    }

    private static EnergyReading Unmetered(
        Metric? gross = null,
        Metric? net = null,
        int count = 0,
        double rate = 0.0,
        ReadingFlags flags = ReadingFlags.None) =>
        new(
            grossJoules: gross ?? NotApplicable,
            netJoules: net ?? NotApplicable,
            averageWatts: NotApplicable,
            peakWatts: NotApplicable,
            durationSeconds: 2.0,
            method: EnergyMethod.Unmetered,
            sampleCount: count,
            sampleRateHz: rate,
            flags: flags,
            throttleReasons: null);
}