using System.Runtime.CompilerServices;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Nvml;
using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-029: the sampled meter over a scripted NVML session and a <see cref="FakeTimeProvider"/>. Nothing here waits on the wall
/// clock. <c>FakeTimeProvider.Advance</c> runs every due tick inline on the calling thread (measured: 50 of 50 runs of a 2.0 s
/// advance made 21 reads, all on the caller's thread, before <c>Advance</c> returned), so the loop has normally taken a tick when
/// <c>Advance</c> returns. The tests still advance and then yield until the fake NVML has counted the read (see
/// <see cref="Rig.AdvanceAsync"/>) as a hedge against a clock that runs ticks elsewhere; a test that asserts on counts advances one
/// period at a time so that every tick is settled before the next one fires.
/// </summary>
[Trait("Spec", "REQ-004")]
[Trait("Spec", "REQ-005")]
public sealed class SampledEnergyMeterTests
{
    private const double Tolerance = 1e-9;
    private const int MaxYields = 2_000_000;
    private const NvmlReturn Failure = (NvmlReturn)999;

    [Fact]
    public async Task Constructor_rejects_null_arguments()
    {
        await using Rig rig = Rig.Create();

        Assert.Throws<ArgumentNullException>(() => Build(null!, rig.Capabilities, rig.Time));
        Assert.Throws<ArgumentNullException>(() => Build(rig.Source, null!, rig.Time));
        Assert.Throws<ArgumentNullException>(() => Build(rig.Source, rig.Capabilities, null!));
    }

    [Fact]
    public async Task Capabilities_are_the_ones_the_meter_was_built_with()
    {
        await using Rig rig = Rig.Create("sampling");

        Assert.Same(rig.Capabilities, rig.Meter.Capabilities);
    }

    [Fact]
    public async Task Start_takes_the_first_sample_immediately()
    {
        await using Rig rig = Rig.Create();

        await rig.StartAsync();

        Assert.Equal(1, rig.PowerReads);
        Assert.Equal(1, rig.Meter.SampleCount);
    }

    [Fact]
    public async Task Ten_hertz_for_two_seconds_in_tenth_second_steps_takes_21_reads()
    {
        await using Rig rig = Rig.Create();
        await rig.StartAsync();

        await rig.TickAsync(20);

        // One read at start plus twenty ticks, one per 0.1 s step, each at its own instant.
        Assert.Equal(21, rig.PowerReads);
        Assert.Equal(21, rig.Meter.SampleCount);
    }

    [Fact]
    public async Task One_two_second_advance_stores_one_sample_at_the_final_instant()
    {
        await using Rig rig = Rig.Create();
        await rig.StartAsync();

        rig.Advance(TimeSpan.FromSeconds(2.0));
        await Rig.SettleAsync(() => rig.PowerCalls >= 21, "all twenty ticks of the advance", MaxYields);

        // Measured (50 of 50 runs, also with a thread-id probe): Advance sets the final time, then fires the twenty due callbacks
        // inline on this thread, so the loop reads 20 times at 2.0 s before Advance returns. With the start sample that is 21 reads,
        // and the buffer keeps one sample for the twenty ticks that share an instant: the start sample at 0 and one at 2.0 s.
        Assert.Equal(21, rig.PowerReads);
        Assert.Equal(2, rig.Meter.SampleCount);
    }

    [Fact]
    public async Task A_second_start_is_rejected_and_the_single_loop_keeps_one_read_per_tick()
    {
        await using Rig rig = Rig.Create();
        await rig.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await rig.Meter.StartAsync(CancellationToken.None));
        await rig.TickAsync(3);

        Assert.Equal(4, rig.PowerReads);
        Assert.Equal(4, rig.Meter.SampleCount);
    }

    [Fact]
    public async Task Irregular_advances_store_the_actual_timestamps()
    {
        // Power is 100 W per second of fake time, so a sample read at time t is 100 t watts. The trapezoid over the samples at
        // 0, 0.13 and 0.27 s is exactly the integral 50 t^2 = 3.645 J; samples placed at the nominal 0.1 and 0.2 s would give 4.05 J.
        await using Rig rig = Rig.Create("sampling");
        rig.Api.WattsAt = static t => 100.0 * t;
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();

        rig.Advance(TimeSpan.FromMilliseconds(70));
        await rig.AdvanceAsync(TimeSpan.FromMilliseconds(60), 1);
        await rig.AdvanceAsync(TimeSpan.FromMilliseconds(140), 1);
        EnergyReading reading = window.Close();

        Assert.Equal(3, reading.SampleCount);
        AssertMeasured(reading.GrossJoules, 3.645, "J", 1e-6);
    }

    [Fact]
    public async Task Counter_window_of_three_seconds_at_sixty_watts_is_exactly_180_joules()
    {
        await using Rig rig = Rig.Create("auto");
        Assert.Equal(EnergyMethod.Counter, rig.Capabilities.Method);

        EnergyReading reading = await ThreeSecondWindowAsync(rig);

        // The counter difference over the window, not the absolute counter (the fake adds an offset to every value).
        AssertMeasured(reading.GrossJoules, 180.0, "J", 0.0);
        AssertMeasured(reading.AverageWatts, 60.0, "W", Tolerance);
        AssertMeasured(reading.PeakWatts, 60.0, "W", Tolerance);
        Assert.Equal(31, reading.SampleCount);
        Assert.Equal(10.0, reading.SampleRateHz);
        Assert.Equal(EnergyMethod.Counter, reading.Method);
        Assert.Equal(3.0, reading.DurationSeconds, 9);
        Assert.Equal(ReadingFlags.None, reading.Flags);
        Assert.NotNull(reading.ThrottleReasons);
        Assert.Empty(reading.ThrottleReasons);
    }

    [Fact]
    public async Task Sampling_window_of_three_seconds_at_sixty_watts_is_180_joules_within_one_percent()
    {
        await using Rig rig = Rig.Create("sampling");

        EnergyReading reading = await ThreeSecondWindowAsync(rig);

        AssertMeasured(reading.GrossJoules, 180.0, "J", 1.8);
        AssertMeasured(reading.AverageWatts, 60.0, "W", Tolerance);
        AssertMeasured(reading.PeakWatts, 60.0, "W", Tolerance);
        Assert.Equal(31, reading.SampleCount);
        Assert.Equal(10.0, reading.SampleRateHz);
        Assert.Equal(EnergyMethod.Sampling, reading.Method);
        Assert.Equal(0, rig.CounterCalls);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("sampling")]
    public async Task Overlapping_windows_report_own_energy(string method)
    {
        await using Rig rig = Rig.Create(method);
        await rig.StartAsync();
        IMeterWindow first = rig.Meter.OpenWindow();
        await rig.TickAsync(10);
        IMeterWindow second = rig.Meter.OpenWindow();
        await rig.TickAsync(20);

        EnergyReading firstReading = first.Close();
        EnergyReading secondReading = second.Close();

        AssertMeasured(firstReading.GrossJoules, 180.0, "J", 1e-6);
        AssertMeasured(secondReading.GrossJoules, 120.0, "J", 1e-6);
        Assert.Equal(31, firstReading.SampleCount);
        Assert.Equal(21, secondReading.SampleCount);
        Assert.Equal(0.0, first.Span.StartSeconds, 9);
        Assert.Equal(3.0, first.Span.StopSeconds, 9);
        Assert.Equal(1.0, second.Span.StartSeconds, 9);
        Assert.Equal(3.0, second.Span.StopSeconds, 9);
        Assert.Equal(2.0, second.Span.DurationSeconds, 9);
    }

    [Fact]
    public async Task A_short_window_between_ticks_still_has_two_edge_samples_and_an_exact_counter_gross()
    {
        // 2 Hz puts the next tick 0.5 s out, so a 150 ms window sees no tick at all.
        await using Rig rig = Rig.Create("auto", rateHz: 2.0);
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();

        rig.Advance(TimeSpan.FromMilliseconds(150));
        EnergyReading reading = window.Close();

        Assert.Equal(2, reading.SampleCount);
        AssertMeasured(reading.GrossJoules, 9.0, "J", 0.0);
        AssertMeasured(reading.AverageWatts, 60.0, "W", Tolerance);
        AssertMeasured(reading.PeakWatts, 60.0, "W", Tolerance);
        Assert.True(reading.Flags.HasFlag(ReadingFlags.LowResolution));
        Assert.Equal(3, rig.PowerReads);
        Assert.Equal(2, rig.CounterCalls);
        Assert.Equal(2.0, reading.SampleRateHz);
    }

    [Fact]
    public async Task Start_and_ticks_never_read_the_counter()
    {
        await using Rig counter = Rig.Create("auto");
        await using Rig sampling = Rig.Create("sampling");

        await counter.StartAsync();
        await sampling.StartAsync();
        await counter.TickAsync(3);
        await sampling.TickAsync(3);

        // Start and ticks never read the counter; only OpenWindow and Close do.
        Assert.Equal(0, counter.CounterCalls);
        Assert.Equal(0, sampling.CounterCalls);
    }

    [Fact]
    public async Task Opening_a_window_before_start_is_rejected()
    {
        await using Rig rig = Rig.Create();

        Assert.Throws<InvalidOperationException>(() => rig.Meter.OpenWindow());

        Assert.Equal(0, rig.PowerReads);
    }

    [Fact]
    public async Task Closing_a_window_twice_is_rejected()
    {
        await using Rig rig = Rig.Create();
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        rig.Advance(TimeSpan.FromMilliseconds(50));
        _ = window.Close();
        int reads = rig.PowerReads;

        Assert.Throws<InvalidOperationException>(() => window.Close());

        Assert.Equal(reads, rig.PowerReads);
    }

    [Fact]
    public async Task Closing_at_the_instant_of_opening_is_rejected_because_the_window_has_no_duration()
    {
        // The calculator needs a window that ends after it starts; the meter reports that as InvalidOperationException
        // instead of letting the calculator's ArgumentException escape.
        await using Rig rig = Rig.Create();
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        int reads = rig.PowerReads;

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => window.Close());

        // The message names the zero duration, no source read was made, and the window is still open: a later Close succeeds.
        Assert.Contains("zero duration", failure.Message, StringComparison.Ordinal);
        Assert.Equal(reads, rig.PowerReads);
        rig.Advance(TimeSpan.FromMilliseconds(50));
        EnergyReading reading = window.Close();
        Assert.Equal(2, reading.SampleCount);
        AssertMeasured(reading.GrossJoules, 3.0, "J", 0.0);
    }

    [Fact]
    public async Task A_failed_power_read_is_skipped_never_recorded_as_zero_and_the_loop_continues()
    {
        await using Rig rig = Rig.Create();
        rig.FailPowerReadNumber(3);
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();

        // Read 1 is the start sample, read 2 the window edge, read 3 the tick at 0.1 s: it throws.
        await rig.TickAsync(10);
        EnergyReading reading = window.Close();

        Assert.Equal(1, rig.Meter.SkippedReadCount);

        // Start, window open, ten ticks and window close are thirteen calls; the one that threw returned no value.
        Assert.Equal(12, rig.PowerReads);

        // Samples at 0 and 0.2 to 1.0 s: the failed tick left a gap and not a 0 W sample, which would pull the average below 60 W.
        Assert.Equal(10, reading.SampleCount);
        AssertMeasured(reading.AverageWatts, 60.0, "W", Tolerance);
        AssertMeasured(reading.PeakWatts, 60.0, "W", Tolerance);
        AssertMeasured(reading.GrossJoules, 60.0, "J", 0.0);

        await rig.TickAsync(1);
        Assert.Equal(11, rig.Meter.SampleCount);
        Assert.Equal(1, rig.Meter.SkippedReadCount);
    }

    [Fact]
    public async Task A_window_with_one_sample_reports_average_and_peak_as_insufficient_samples()
    {
        // Read 3 is the close edge and it fails, so the window [0, 0.05 s] holds only the open sample.
        await using Rig rig = Rig.Create("auto");
        rig.FailPowerReadNumber(3);
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();

        rig.Advance(TimeSpan.FromMilliseconds(50));
        EnergyReading reading = window.Close();

        Assert.Equal(1, reading.SampleCount);
        AssertNotReported(reading.AverageWatts, "insufficient-samples");
        AssertNotReported(reading.PeakWatts, "insufficient-samples");
        AssertMeasured(reading.GrossJoules, 3.0, "J", 0.0);
        Assert.Equal(1, rig.Meter.SkippedReadCount);
    }

    [Fact]
    public async Task A_sampling_window_with_one_sample_reports_gross_as_insufficient_samples_too()
    {
        await using Rig rig = Rig.Create("sampling");
        rig.FailPowerReadNumber(3);
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();

        rig.Advance(TimeSpan.FromMilliseconds(50));
        EnergyReading reading = window.Close();

        Assert.Equal(1, reading.SampleCount);
        AssertNotReported(reading.GrossJoules, "insufficient-samples");
        AssertNotReported(reading.AverageWatts, "insufficient-samples");
        AssertNotReported(reading.PeakWatts, "insufficient-samples");
    }

    [Fact]
    public async Task A_sample_at_the_instant_of_the_last_one_replaces_it_so_the_buffer_stays_strictly_increasing()
    {
        await using Rig rig = Rig.Create();
        await rig.StartAsync();
        await rig.TickAsync(1);
        Assert.Equal(2, rig.Meter.SampleCount);

        // The edge sample is taken at exactly the instant of the tick sample: it supersedes it and the buffer does not grow.
        IMeterWindow window = rig.Meter.OpenWindow();
        Assert.Equal(2, rig.Meter.SampleCount);
        Assert.Equal(3, rig.PowerReads);

        await rig.TickAsync(2);
        EnergyReading reading = window.Close();

        // Samples at 0.1, 0.2 and 0.3 s; a duplicate at either edge would make the calculator reject the window.
        Assert.Equal(3, reading.SampleCount);
        AssertMeasured(reading.AverageWatts, 60.0, "W", Tolerance);
        Assert.Equal(4, rig.Meter.SampleCount);
    }

    [Fact]
    public async Task A_clock_that_goes_backwards_is_rejected()
    {
        SkewedTimeProvider? skewed = null;
        await using Rig rig = Rig.Create(rateHz: 1.0, clock: time => skewed = new SkewedTimeProvider(time));
        await rig.StartAsync();
        rig.Advance(TimeSpan.FromMilliseconds(300));
        _ = rig.Meter.OpenWindow();
        Assert.Equal(2, rig.Meter.SampleCount);

        skewed!.GoBack(TimeSpan.FromMilliseconds(200));

        Assert.Throws<InvalidOperationException>(() => rig.Meter.OpenWindow());
        Assert.Equal(2, rig.Meter.SampleCount);
    }

    [Fact]
    public async Task Disposing_the_meter_stops_the_sampling()
    {
        await using Rig rig = Rig.Create();
        await rig.StartAsync();
        await rig.TickAsync(10);
        int calls = rig.PowerCalls;

        await rig.Meter.DisposeAsync();
        for (int i = 0; i < 50; i++)
        {
            rig.Advance(TimeSpan.FromMilliseconds(100));
        }

        rig.Advance(TimeSpan.FromSeconds(5.0));
        Assert.Equal(calls, rig.PowerCalls);
        Assert.Equal(0, rig.CounterCalls);
    }

    [Fact]
    public async Task Disposing_twice_is_safe_and_a_window_cannot_be_opened_afterwards()
    {
        await using Rig rig = Rig.Create();
        await rig.StartAsync();

        await rig.Meter.DisposeAsync();
        await rig.Meter.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => rig.Meter.OpenWindow());
    }

    [Fact]
    public async Task Closing_a_window_after_the_meter_was_disposed_throws_object_disposed()
    {
        await using Rig rig = Rig.Create();
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        rig.Advance(TimeSpan.FromMilliseconds(50));
        await rig.Meter.DisposeAsync();
        int reads = rig.PowerReads;

        Assert.Throws<ObjectDisposedException>(() => window.Close());

        Assert.Equal(reads, rig.PowerReads);
    }

    [Fact]
    public async Task Starting_after_the_meter_was_disposed_throws_object_disposed_and_reads_nothing()
    {
        await using Rig rig = Rig.Create();
        await rig.Meter.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await rig.Meter.StartAsync(CancellationToken.None));

        Assert.Equal(0, rig.PowerCalls);
    }

    [Fact]
    public async Task Starting_with_a_cancelled_token_throws_operation_canceled_and_reads_nothing()
    {
        await using Rig rig = Rig.Create();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await rig.Meter.StartAsync(cancelled.Token));

        Assert.Equal(0, rig.PowerCalls);
        Assert.Equal(0, rig.Meter.SampleCount);
    }

    [Fact]
    public async Task Disposing_a_meter_that_was_never_started_is_safe()
    {
        await using Rig rig = Rig.Create();

        await rig.Meter.DisposeAsync();
        await rig.Meter.DisposeAsync();

        Assert.Equal(0, rig.PowerCalls);
    }

    [Fact]
    public async Task The_meter_leaves_the_source_to_its_owner()
    {
        await using Rig rig = Rig.Create();
        await rig.StartAsync();

        await rig.Meter.DisposeAsync();

        // The caller opened the source and disposes it; a disposed source would throw ObjectDisposedException here.
        Assert.Equal(60.0, rig.Source.ReadPowerWatts());
    }

    [Fact]
    public async Task A_source_that_was_disposed_ends_the_loop_instead_of_counting_skipped_reads()
    {
        // ObjectDisposedException derives from InvalidOperationException, so a loop that skips failed reads must catch it first.
        StubSource stub = new() { Power = static call => call == 3 ? throw new ObjectDisposedException(nameof(StubSource)) : 60.0 };
        await using Rig rig = Rig.CreateStub(stub, "sampling");
        await rig.StartAsync();
        await rig.TickAsync(1);

        await rig.TickAsync(1);
        Assert.Equal(0, rig.Meter.SkippedReadCount);

        rig.Advance(TimeSpan.FromMilliseconds(500));
        for (int i = 0; i < 1_000; i++)
        {
            await Task.Yield();
        }

        await rig.Meter.DisposeAsync();
        Assert.Equal(3, stub.PowerCalls);
        Assert.Equal(0, rig.Meter.SkippedReadCount);
    }

    [Fact]
    public async Task A_window_opened_during_a_tick_waits_for_the_meter_lock_and_never_reads_concurrently()
    {
        // The stub has no lock of its own. The tick (call 2) blocks on the gate inside the source, holding the meter lock; a second
        // thread then opens a window and must wait on the meter lock, so the source sees one read in flight and one entry.
        using ManualResetEventSlim gate = new(false);
        int inFlight = 0;
        int maxInFlight = 0;
        StubSource stub = new()
        {
            Power = call =>
            {
                int now = Interlocked.Increment(ref inFlight);
                int seen;
                do
                {
                    seen = Volatile.Read(ref maxInFlight);
                }
                while (now > seen && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen);

                if (call == 2)
                {
                    gate.Wait();
                }

                _ = Interlocked.Decrement(ref inFlight);
                return 60.0;
            },
        };
        await using Rig rig = Rig.CreateStub(stub, "sampling");
        await rig.StartAsync();
        Exception? tickFault = null;
        Exception? openFault = null;
        IMeterWindow? window = null;
        Thread tickThread = new(() =>
        {
            try
            {
                rig.Advance(rig.Period);
            }
            catch (Exception ex)
            {
                tickFault = ex;
            }
        })
        {
            IsBackground = true,
        };
        Thread openThread = new(() =>
        {
            try
            {
                window = rig.Meter.OpenWindow();
            }
            catch (Exception ex)
            {
                openFault = ex;
            }
        })
        {
            IsBackground = true,
        };

        try
        {
            tickThread.Start();
            await AssertReachedAsync(() => Volatile.Read(ref inFlight) == 1, gate, "the tick to enter the source");

            openThread.Start();
            await AssertReachedAsync(() => (openThread.ThreadState & ThreadState.WaitSleepJoin) != 0, gate, "the window thread to block");

            // The window thread is blocked, and the source was entered once: it waits on the meter lock, not inside the source.
            Assert.Equal(2, stub.PowerCalls);
            Assert.Equal(1, Volatile.Read(ref inFlight));
        }
        finally
        {
            gate.Set();
        }

        await AssertReachedAsync(() => !tickThread.IsAlive && !openThread.IsAlive, gate, "both threads to finish");
        tickThread.Join();
        openThread.Join();

        Assert.Null(tickFault);
        Assert.Null(openFault);
        Assert.Equal(1, Volatile.Read(ref maxInFlight));
        Assert.Equal(3, stub.PowerCalls);

        // The tick sample and the edge sample share the instant 0.1 s, so the buffer holds the start sample and one at 0.1 s.
        Assert.NotNull(window);
        Assert.Equal(0.1, window.Span.StartSeconds, 9);
        Assert.Equal(2, rig.Meter.SampleCount);
    }

    [Fact]
    public async Task The_timer_period_and_the_reading_rate_come_from_the_capabilities()
    {
        await using Rig rig = Rig.Create("sampling", rateHz: 5.0);
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();

        await rig.TickAsync(5);
        EnergyReading reading = window.Close();

        // 5 Hz is a 0.2 s period: five ticks reach 1.0 s. Start, window open, five ticks and window close are eight reads.
        Assert.Equal(1.0, window.Span.StopSeconds, 9);
        Assert.Equal(8, rig.PowerReads);
        Assert.Equal(6, reading.SampleCount);
        Assert.Equal(5.0, reading.SampleRateHz);
    }

    [Fact]
    public async Task A_meter_built_from_fallback_capabilities_flags_every_window()
    {
        await using Rig rig = Rig.Create("auto", counterSupported: false);
        Assert.True(rig.Capabilities.IsFallback);
        Assert.Equal("Energy method: sampling (counter unsupported)", rig.Capabilities.Describe());
        await rig.StartAsync();
        IMeterWindow first = rig.Meter.OpenWindow();
        await rig.TickAsync(5);
        IMeterWindow second = rig.Meter.OpenWindow();
        await rig.TickAsync(5);

        EnergyReading firstReading = first.Close();
        EnergyReading secondReading = second.Close();

        Assert.Contains("sampling-fallback", ReadingFlagNames.ToStrings(firstReading.Flags));
        Assert.Contains("sampling-fallback", ReadingFlagNames.ToStrings(secondReading.Flags));
        Assert.Equal(EnergyMethod.Sampling, firstReading.Method);
        AssertMeasured(firstReading.GrossJoules, 60.0, "J", 1e-6);
        Assert.Equal(0, rig.CounterCalls);
    }

    [Fact]
    public async Task Sampling_that_was_forced_is_not_flagged_as_a_fallback()
    {
        await using Rig rig = Rig.Create("sampling");

        EnergyReading reading = await ThreeSecondWindowAsync(rig);

        Assert.DoesNotContain("sampling-fallback", ReadingFlagNames.ToStrings(reading.Flags));
    }

    [Fact]
    public async Task A_power_cap_throttle_reason_flags_the_window_end_to_end()
    {
        await using Rig rig = Rig.Create("auto", script: api => api.ClockEventReasons = 0x4);
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();

        await rig.TickAsync(10);
        EnergyReading reading = window.Close();

        Assert.Contains("throttled", ReadingFlagNames.ToStrings(reading.Flags));
        Assert.NotNull(reading.ThrottleReasons);
        Assert.Contains(nameof(ThrottleReasonBits.SwPowerCap), reading.ThrottleReasons);
    }

    [Fact]
    public async Task A_throttle_bit_that_only_the_closing_edge_sees_still_flags_the_window()
    {
        // The bit appears after the last tick (2.9 s), so only the edge sample taken by Close at 2.95 s carries it.
        await using Rig rig = Rig.Create("auto", script: api => api.ClockEventReasonsAt = static t => t > 2.92 ? 0x4UL : 0UL);
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        await rig.TickAsync(29);

        rig.Advance(TimeSpan.FromMilliseconds(50));
        EnergyReading reading = window.Close();

        Assert.Contains("throttled", ReadingFlagNames.ToStrings(reading.Flags));
        Assert.NotNull(reading.ThrottleReasons);
        Assert.Contains(nameof(ThrottleReasonBits.SwPowerCap), reading.ThrottleReasons);
    }

    [Fact]
    public async Task Only_non_throttling_bits_give_an_empty_list_and_no_flag()
    {
        await using Rig rig = Rig.Create("auto", script: api => api.ClockEventReasons = (ulong)ThrottleReasonBits.GpuIdle);
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();

        await rig.TickAsync(10);
        EnergyReading reading = window.Close();

        Assert.DoesNotContain("throttled", ReadingFlagNames.ToStrings(reading.Flags));
        Assert.NotNull(reading.ThrottleReasons);
        Assert.Empty(reading.ThrottleReasons);
    }

    [Fact]
    public async Task A_failed_throttle_read_keeps_the_power_sample_and_makes_the_reasons_unknown()
    {
        await using Rig rig = Rig.Create("auto");
        rig.Api.ClockEventReturn = Failure;
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();

        await rig.TickAsync(10);
        EnergyReading reading = window.Close();

        // Start, window open, ten ticks and window close each read the throttle bits once.
        Assert.Equal(13, rig.Meter.ThrottleReadFailures);
        Assert.Equal(0, rig.Meter.SkippedReadCount);
        Assert.Equal(11, reading.SampleCount);
        AssertMeasured(reading.AverageWatts, 60.0, "W", Tolerance);

        // No sample claims "not throttled", so the reading claims neither way.
        Assert.Null(reading.ThrottleReasons);
        Assert.DoesNotContain("throttled", ReadingFlagNames.ToStrings(reading.Flags));
    }

    [Fact]
    public async Task Every_sample_including_both_edges_reads_the_throttle_reasons()
    {
        StubSource stub = new();
        await using Rig rig = Rig.CreateStub(stub, "sampling");
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        await rig.TickAsync(3);

        _ = window.Close();

        // Start, open, three ticks and close: six power reads and six throttle reads.
        Assert.Equal(6, stub.PowerCalls);
        Assert.Equal(6, stub.ThrottleCalls);
        Assert.Equal(0, stub.EnergyCalls);
    }

    [Fact]
    public async Task A_counter_read_that_fails_at_close_gives_gross_counter_not_read_and_keeps_the_samples()
    {
        await using Rig rig = Rig.Create("auto");
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        await rig.TickAsync(10);
        rig.Api.CounterReturn = Failure;

        EnergyReading reading = window.Close();

        AssertNotReported(reading.GrossJoules, "counter-not-read");
        AssertMeasured(reading.AverageWatts, 60.0, "W", Tolerance);
        Assert.Equal(11, reading.SampleCount);
    }

    [Fact]
    public async Task A_counter_read_that_fails_at_open_gives_gross_counter_not_read()
    {
        await using Rig rig = Rig.Create("auto");
        await rig.StartAsync();
        rig.Api.CounterReturn = Failure;
        IMeterWindow window = rig.Meter.OpenWindow();
        rig.Api.CounterReturn = NvmlReturn.Success;
        await rig.TickAsync(10);

        EnergyReading reading = window.Close();

        AssertNotReported(reading.GrossJoules, "counter-not-read");
    }

    [Fact]
    public async Task A_null_counter_read_maps_to_counter_not_read_never_to_zero()
    {
        StubSource stub = new() { Energy = static call => call == 1 ? 1000.0 : null };
        await using Rig rig = Rig.CreateStub(stub, "counter");
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        await rig.TickAsync(10);

        EnergyReading reading = window.Close();

        AssertNotReported(reading.GrossJoules, "counter-not-read");
        Assert.Equal(2, stub.EnergyCalls);
    }

    [Fact]
    public async Task A_counter_that_went_backwards_is_reported_as_such_and_not_clamped()
    {
        StubSource stub = new() { Energy = static call => call == 1 ? 5000.0 : 4000.0 };
        await using Rig rig = Rig.CreateStub(stub, "counter");
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        await rig.TickAsync(10);

        EnergyReading reading = window.Close();

        AssertNotReported(reading.GrossJoules, "counter-went-backwards");
    }

    [Fact]
    public async Task A_counter_difference_beyond_the_range_of_a_long_maps_to_counter_not_read()
    {
        StubSource stub = new() { Energy = static call => call == 1 ? 0.0 : 1e30 };
        await using Rig rig = Rig.CreateStub(stub, "counter");
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        await rig.TickAsync(10);

        EnergyReading reading = window.Close();

        AssertNotReported(reading.GrossJoules, "counter-not-read");
    }

    [Fact]
    public async Task A_source_exception_the_loop_does_not_skip_stops_it_and_stops_serving_windows()
    {
        // The real binding can raise EntryPointNotFoundException or BadImageFormatException; neither is a failed read.
        EntryPointNotFoundException fault = new("scripted missing export");
        StubSource stub = new() { Power = call => call == 2 ? throw fault : 60.0 };
        await using Rig rig = Rig.CreateStub(stub, "sampling");
        await rig.StartAsync();

        rig.Advance(rig.Period);
        await Rig.SettleAsync(() => rig.Meter.LoopFault is not null, "the loop to record its fault", MaxYields);

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => rig.Meter.OpenWindow());
        Assert.Same(fault, refusal.InnerException);
        Assert.Same(fault, rig.Meter.LoopFault);
        for (int i = 0; i < 5; i++)
        {
            rig.Advance(rig.Period);
        }

        Assert.Equal(2, stub.PowerCalls);

        // The fault is observed by the first dispose, which does not rethrow it, and the second call is a no-op.
        Assert.Null(await Record.ExceptionAsync(async () => await rig.Meter.DisposeAsync()));
        Assert.Null(await Record.ExceptionAsync(async () => await rig.Meter.DisposeAsync()));
        Assert.Same(fault, rig.Meter.LoopFault);
    }

    [Fact]
    public async Task A_window_that_was_open_when_the_loop_faulted_cannot_be_closed()
    {
        BadImageFormatException fault = new("scripted bad image");
        StubSource stub = new() { Power = call => call == 3 ? throw fault : 60.0 };
        await using Rig rig = Rig.CreateStub(stub, "sampling");
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();

        rig.Advance(rig.Period);
        await Rig.SettleAsync(() => rig.Meter.LoopFault is not null, "the loop to record its fault", MaxYields);

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => window.Close());
        Assert.Same(fault, refusal.InnerException);
        Assert.Equal(3, stub.PowerCalls);
    }

    [Fact]
    public async Task A_running_loop_reports_no_fault()
    {
        await using Rig rig = Rig.Create();
        await rig.StartAsync();
        await rig.TickAsync(2);

        Assert.Null(rig.Meter.LoopFault);
    }

    [Theory]
    [InlineData(2000.0)]
    [InlineData(double.Epsilon)]
    [InlineData(1e-13)]
    public void A_rate_that_gives_no_valid_timer_period_is_rejected_before_any_read(double rateHz)
    {
        StubSource stub = new();
        MeterCapabilities capabilities = MeterMethodSelector.Select("sampling", true, null, rateHz);

        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => Build(stub, capabilities, new FakeTimeProvider()));

        Assert.Equal(rateHz, Assert.IsType<double>(failure.ActualValue));
        Assert.Contains("SampleRateHz", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stub.PowerCalls);
    }

    [Fact]
    public async Task A_rate_of_1000_hertz_is_accepted_and_ticks_every_millisecond()
    {
        StubSource stub = new();
        await using Rig rig = Rig.CreateStub(stub, "sampling", rateHz: 1000.0);
        await rig.StartAsync();

        await rig.TickAsync(3);

        Assert.Equal(4, stub.PowerCalls);
        Assert.Equal(4, rig.Meter.SampleCount);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    [InlineData(double.MaxValue)]
    [InlineData(4294968.0)]
    public async Task An_unusable_power_value_is_skipped_and_counted_never_stored(double watts)
    {
        StubSource stub = new() { Power = call => call == 2 ? watts : 60.0 };
        await using Rig rig = Rig.CreateStub(stub, "sampling");
        await rig.StartAsync();

        await rig.TickAsync(1);

        Assert.Equal(2, stub.PowerCalls);
        Assert.Equal(1, rig.Meter.SkippedReadCount);
        Assert.Equal(1, rig.Meter.SampleCount);
        await rig.TickAsync(1);
        Assert.Equal(2, rig.Meter.SampleCount);
    }

    [Fact]
    public async Task A_huge_power_value_never_reaches_the_calculator_and_close_still_returns()
    {
        StubSource stub = new() { Power = static call => call <= 2 ? 60.0 : double.MaxValue };
        await using Rig rig = Rig.CreateStub(stub, "sampling");
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        await rig.TickAsync(10);

        EnergyReading reading = window.Close();

        // Every tick and the closing edge were skipped, so the window holds its opening sample only.
        Assert.Equal(11, rig.Meter.SkippedReadCount);
        Assert.Equal(1, reading.SampleCount);
        AssertNotReported(reading.GrossJoules, "insufficient-samples");
        AssertNotReported(reading.AverageWatts, "insufficient-samples");
    }

    [Fact]
    public async Task A_clock_that_goes_backwards_inside_the_loop_skips_and_counts_the_tick_and_the_loop_resumes()
    {
        SkewedTimeProvider? skewed = null;
        await using Rig rig = Rig.Create(clock: time => skewed = new SkewedTimeProvider(time));
        await rig.StartAsync();
        await rig.TickAsync(1);
        Assert.Equal(2, rig.Meter.SampleCount);
        int reads = rig.PowerReads;

        // The next tick would be stamped -0.3 s, before the sample at 0.1 s: it is skipped without a source read.
        skewed!.GoBack(TimeSpan.FromMilliseconds(500));
        rig.Advance(rig.Period);
        await Rig.SettleAsync(() => rig.Meter.SkippedReadCount == 1, "the tick to be skipped", MaxYields);
        Assert.Equal(reads, rig.PowerReads);
        Assert.Equal(2, rig.Meter.SampleCount);

        // With the clock whole again the loop is still running: the tick at 0.3 s is stored.
        skewed.GoBack(TimeSpan.Zero);
        await rig.TickAsync(1);
        Assert.Equal(3, rig.Meter.SampleCount);
        Assert.Equal(1, rig.Meter.SkippedReadCount);
        Assert.Null(rig.Meter.LoopFault);
    }

    [Fact]
    public async Task Closing_before_the_start_throws_and_leaves_the_window_open_for_a_later_close()
    {
        SkewedTimeProvider? skewed = null;
        await using Rig rig = Rig.Create(rateHz: 1.0, clock: time => skewed = new SkewedTimeProvider(time));
        await rig.StartAsync();
        rig.Advance(TimeSpan.FromMilliseconds(300));
        IMeterWindow window = rig.Meter.OpenWindow();
        int reads = rig.PowerReads;

        skewed!.GoBack(TimeSpan.FromMilliseconds(500));
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => window.Close());
        Assert.Contains("backwards", failure.Message, StringComparison.Ordinal);
        Assert.Equal(reads, rig.PowerReads);

        skewed.GoBack(TimeSpan.Zero);
        rig.Advance(TimeSpan.FromMilliseconds(100));
        EnergyReading reading = window.Close();

        Assert.Equal(2, reading.SampleCount);
        Assert.Equal(0.1, reading.DurationSeconds, 9);
        Assert.Equal(0.4, window.Span.StopSeconds, 9);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task A_non_finite_counter_mark_maps_to_counter_not_read(double mark)
    {
        EnergyReading reading = await CounterWindowAsync(call => call == 1 ? 1000.0 : mark);

        AssertNotReported(reading.GrossJoules, "counter-not-read");
    }

    [Fact]
    public async Task A_non_finite_opening_counter_mark_maps_to_counter_not_read()
    {
        EnergyReading reading = await CounterWindowAsync(static call => call == 1 ? double.NaN : 1000.0);

        AssertNotReported(reading.GrossJoules, "counter-not-read");
    }

    [Fact]
    public async Task A_counter_difference_below_the_range_of_a_long_maps_to_counter_not_read()
    {
        EnergyReading reading = await CounterWindowAsync(static call => call == 1 ? 1e30 : 0.0);

        AssertNotReported(reading.GrossJoules, "counter-not-read");
    }

    [Fact]
    public async Task A_tiny_negative_counter_difference_is_a_backwards_counter_and_not_a_measured_zero()
    {
        // 1.0 to 0.6 mJ is -0.4 mJ, which rounds to 0; it must read as went-backwards, never as an idle window.
        EnergyReading reading = await CounterWindowAsync(static call => call == 1 ? 1.0 : 0.6);

        AssertNotReported(reading.GrossJoules, "counter-went-backwards");
    }

    [Fact]
    public async Task Two_threads_racing_to_close_one_window_have_exactly_one_winner()
    {
        await using Rig rig = Rig.Create();
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        rig.Advance(TimeSpan.FromMilliseconds(50));
        using Barrier barrier = new(2);
        using CancellationTokenSource giveUp = new();
        EnergyReading?[] readings = new EnergyReading?[2];
        Exception?[] failures = new Exception?[2];
        Thread[] threads =
        [
            .. Enumerable.Range(0, 2).Select(index => new Thread(() =>
            {
                try
                {
                    // Cancelling the token releases a thread that never meets its partner.
                    barrier.SignalAndWait(giveUp.Token);
                    readings[index] = window.Close();
                }
                catch (Exception ex)
                {
                    failures[index] = ex;
                }
            })
            {
                IsBackground = true,
            }),
        ];

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        // The wait is a bounded number of yields, never the wall clock; on running out it releases the barrier and fails.
        if (!await Rig.TrySettleAsync(() => threads.All(static thread => !thread.IsAlive), MaxYields))
        {
            await giveUp.CancelAsync();
            Assert.Fail($"The closing threads did not finish. Recorded: {string.Join("; ", failures.Where(static failure => failure is not null))}");
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        // One thread got the reading; the other was refused with exactly InvalidOperationException (not a disposal or any other fault).
        Assert.Equal(1, readings.Count(static reading => reading is not null));
        Exception loser = Assert.Single(failures, static failure => failure is not null)!;
        Assert.IsType<InvalidOperationException>(loser);
    }

    [Fact]
    public async Task A_tick_that_fires_inside_the_meters_own_clock_read_during_close_is_dropped()
    {
        ReentrantTickProvider? clock = null;
        await using Rig rig = Rig.Create(clock: time => clock = new ReentrantTickProvider(time));
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        rig.Advance(TimeSpan.FromMilliseconds(50));

        // Close reads the clock under the meter lock; that read advances the fake by one period, which fires the tick inline on this
        // thread, inside the locked section, and returns the time from before the advance.
        clock!.ArmNextRead(rig.Period);
        EnergyReading reading = window.Close();

        // The nested tick was dropped: no source read for it, no sample at its time, and Close did not see a clock going backwards.
        Assert.Equal(3, rig.PowerReads);
        Assert.Equal(2, rig.Meter.SampleCount);
        Assert.Equal(2, reading.SampleCount);
        Assert.Equal(0.05, window.Span.StopSeconds, 9);
        Assert.Equal(0, rig.Meter.SkippedReadCount);
        Assert.Null(rig.Meter.LoopFault);

        // The loop is alive: the next tick is stored.
        await rig.TickAsync(1);
        Assert.Equal(3, rig.Meter.SampleCount);
    }

    [Fact]
    public async Task A_tick_that_fires_inside_the_meters_own_clock_read_during_open_is_dropped()
    {
        ReentrantTickProvider? clock = null;
        await using Rig rig = Rig.Create(clock: time => clock = new ReentrantTickProvider(time));
        await rig.StartAsync();
        rig.Advance(TimeSpan.FromMilliseconds(50));

        clock!.ArmNextRead(rig.Period);
        IMeterWindow window = rig.Meter.OpenWindow();

        Assert.Equal(0.05, window.Span.StartSeconds, 9);
        Assert.Equal(2, rig.PowerReads);
        Assert.Equal(2, rig.Meter.SampleCount);
        await rig.TickAsync(1);
        EnergyReading reading = window.Close();

        // The edges at 0.05 s and 0.25 s; the tick at 0.25 s and the closing edge share the instant, so the buffer stays strictly increasing.
        Assert.Equal(2, reading.SampleCount);
        Assert.Equal(0, rig.Meter.SkippedReadCount);
        Assert.Null(rig.Meter.LoopFault);
    }

    private static async Task<EnergyReading> CounterWindowAsync(Func<int, double?> energy)
    {
        StubSource stub = new() { Energy = energy };
        await using Rig rig = Rig.CreateStub(stub, "counter");
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        await rig.TickAsync(10);
        return window.Close();
    }

    // Yields until the condition holds; when the budget runs out it opens the gate (so no thread stays blocked) and fails the test.
    private static async Task AssertReachedAsync(Func<bool> condition, ManualResetEventSlim gate, string description)
    {
        if (!await Rig.TrySettleAsync(condition, MaxYields))
        {
            gate.Set();
            Assert.Fail($"Gave up waiting for {description}.");
        }
    }

    private static SampledEnergyMeter Build(IGpuTelemetrySource source, MeterCapabilities capabilities, TimeProvider time) =>
        new(source, capabilities, time);

    private static async Task<EnergyReading> ThreeSecondWindowAsync(Rig rig)
    {
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        await rig.TickAsync(30);
        return window.Close();
    }

    private static void AssertMeasured(Metric metric, double expected, string unit, double tolerance)
    {
        Assert.Equal(MetricLabel.Measured, metric.Label);
        Assert.Equal(unit, metric.Unit);
        double actual = metric.Value ?? throw new InvalidOperationException("The metric has no value.");
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
    }

    private static void AssertNotReported(Metric metric, string reason)
    {
        Assert.Equal(MetricLabel.NotReported, metric.Label);
        Assert.Equal(reason, metric.Reason);
        Assert.Null(metric.Value);
    }

    /// <summary>
    /// A meter over either a scripted NVML session opened through the factory, or a stub source. Reads are counted from the moment
    /// the rig was built, so whatever the factory read when it opened the source does not count.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ScriptedNvmlApi? _api;
        private readonly StubSource? _stub;
        private readonly (int PowerCalls, int PowerReads, int CounterCalls) _baseline;

        private Rig(FakeTimeProvider time, TimeProvider clock, ScriptedNvmlApi? api, StubSource? stub, IGpuTelemetrySource source, MeterCapabilities capabilities)
        {
            Time = time;
            _api = api;
            _stub = stub;
            Source = source;
            Capabilities = capabilities;
            Meter = new SampledEnergyMeter(source, capabilities, clock);
            _baseline = (api?.PowerCallCount ?? 0, api?.PowerReadCount ?? 0, api?.CounterCallCount ?? 0);
        }

        public FakeTimeProvider Time { get; }

        public IGpuTelemetrySource Source { get; }

        public MeterCapabilities Capabilities { get; }

        public SampledEnergyMeter Meter { get; }

        public ScriptedNvmlApi Api => _api ?? throw new InvalidOperationException("This rig has no scripted NVML session.");

        /// <summary>The timer period the capabilities imply.</summary>
        public TimeSpan Period => TimeSpan.FromSeconds(1.0 / Capabilities.SampleRateHz);

        /// <summary>Power calls since the rig was built, whatever they returned or threw.</summary>
        public int PowerCalls => _stub?.PowerCalls ?? Api.PowerCallCount - _baseline.PowerCalls;

        /// <summary>Power calls that returned a value since the rig was built.</summary>
        public int PowerReads => _stub is null ? Api.PowerReadCount - _baseline.PowerReads : _stub.PowerCalls;

        /// <summary>Counter calls since the rig was built, the factory's probe at open excluded.</summary>
        public int CounterCalls => _stub?.EnergyCalls ?? Api.CounterCallCount - _baseline.CounterCalls;

        /// <param name="method">The <c>Meter:Method</c> value the selector receives.</param>
        /// <param name="watts">The constant power the fake draws.</param>
        /// <param name="counterSupported">False scripts a GPU whose energy counter answers NOT_SUPPORTED.</param>
        /// <param name="rateHz">The sampling rate the capabilities carry.</param>
        /// <param name="clock">Wraps the fake clock; the NVML fake keeps reading the unwrapped one.</param>
        /// <param name="script">Configures the scripted NVML before the factory opens it.</param>
        public static Rig Create(
            string method = "auto",
            double watts = 60.0,
            bool counterSupported = true,
            double rateHz = 10.0,
            Func<FakeTimeProvider, TimeProvider>? clock = null,
            Action<ScriptedNvmlApi>? script = null)
        {
            FakeTimeProvider time = new();
            ScriptedNvmlApi api = new(time) { ConstantPowerWatts = watts, CounterSupported = counterSupported };
            script?.Invoke(api);
            GpuOpenResult open = new NvmlGpuTelemetrySourceFactory(api).Open(0);
            Assert.True(open.IsAvailable, open.Reason);
            MeterCapabilities capabilities = MeterMethodSelector.Select(method, open.Source, rateHz);
            return new Rig(time, clock?.Invoke(time) ?? time, api, null, open.Source, capabilities);
        }

        public static Rig CreateStub(StubSource stub, string method, double rateHz = 10.0)
        {
            FakeTimeProvider time = new();
            MeterCapabilities capabilities = MeterMethodSelector.Select(method, true, null, rateHz);
            return new Rig(time, time, null, stub, stub, capabilities);
        }

        /// <summary>Makes the n-th power call from now on (the first being 1) throw, as a failed NVML read does.</summary>
        public void FailPowerReadNumber(int number) => Api.ThrowOnPowerReadNumber = Api.PowerCallCount + number;

        public async Task StartAsync() => await Meter.StartAsync(CancellationToken.None);

        /// <summary>Advances fake time by a step that fires no tick; nothing is awaited.</summary>
        public void Advance(TimeSpan step) => Time.Advance(step);

        /// <summary>Advances fake time and yields until the loop has taken the expected number of ticks and finished the last one.</summary>
        public async Task AdvanceAsync(TimeSpan step, int expectedTicks)
        {
            int target = PowerCalls + expectedTicks;
            Time.Advance(step);
            await SettleAsync(() => PowerCalls >= target, $"{expectedTicks} tick(s) after advancing {step}", MaxYields);

            // Reading the count takes the meter lock, which the loop holds from before its first NVML call until its sample is stored.
            _ = Meter.SampleCount;
        }

        /// <summary>Advances one timer period at a time, settling every tick before the next one fires.</summary>
        public async Task TickAsync(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                await AdvanceAsync(Period, 1);
            }
        }

        public static async Task SettleAsync(Func<bool> condition, string description, int maxYields)
        {
            if (!await TrySettleAsync(condition, maxYields))
            {
                Assert.Fail($"The sampling loop did not reach: {description}.");
            }
        }

        /// <summary>Yields the thread, never the wall clock, until the condition holds or the budget of yields is spent.</summary>
        public static async Task<bool> TrySettleAsync(Func<bool> condition, int maxYields)
        {
            for (int i = 0; i < maxYields; i++)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Yield();
            }

            return condition();
        }

        public async ValueTask DisposeAsync()
        {
            await Meter.DisposeAsync();
            Source.Dispose();
            if (_api is not null)
            {
                _api.FaultFor = null;
                while (_api.IsInitialised)
                {
                    _api.Shutdown();
                }
            }
        }
    }

    /// <summary>A hand-written source for the failures a scripted NVML session cannot produce; the functions receive the 1-based call number.</summary>
    private sealed class StubSource : IGpuTelemetrySource
    {
        private readonly StrongBox<int> _powerCalls = new();
        private readonly StrongBox<int> _energyCalls = new();
        private readonly StrongBox<int> _throttleCalls = new();

        public Func<int, double> Power { get; set; } = static _ => 60.0;

        public Func<int, double?> Energy { get; set; } = static _ => 0.0;

        public Func<int, ThrottleReasons> Throttle { get; set; } = static _ => new ThrottleReasons(0UL, true);

        public int PowerCalls => Volatile.Read(ref _powerCalls.Value);

        public int EnergyCalls => Volatile.Read(ref _energyCalls.Value);

        public int ThrottleCalls => Volatile.Read(ref _throttleCalls.Value);

        public GpuIdentity Identity { get; } = new("Stub GPU", "GPU-stub", "0.0", null, null, 100.0, "disabled");

        public bool EnergyCounterSupported => true;

        public string? EnergyCounterUnsupportedReason => null;

        public double ReadPowerWatts() => Power(Interlocked.Increment(ref _powerCalls.Value));

        public double? ReadEnergyMillijoules() => Energy(Interlocked.Increment(ref _energyCalls.Value));

        public ThrottleReasons ReadThrottleReasons() => Throttle(Interlocked.Increment(ref _throttleCalls.Value));

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Makes the fake clock behave like one that fires due timers on the thread that reads it: when armed, the next timestamp read
    /// first advances the inner fake by a period (so the due tick runs inline on the reading thread) and then returns the timestamp
    /// from before the advance.
    /// </summary>
    private sealed class ReentrantTickProvider(FakeTimeProvider inner) : TimeProvider
    {
        private TimeSpan _advanceOnNextRead = TimeSpan.Zero;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public void ArmNextRead(TimeSpan advance) => _advanceOnNextRead = advance;

        public override long GetTimestamp()
        {
            long before = inner.GetTimestamp();
            TimeSpan advance = _advanceOnNextRead;
            if (advance > TimeSpan.Zero)
            {
                _advanceOnNextRead = TimeSpan.Zero;
                inner.Advance(advance);
            }

            return before;
        }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>Reports the inner clock's timestamps shifted by a settable offset, so a test can make time run backwards.</summary>
    private sealed class SkewedTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private long _skewTicks;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public void GoBack(TimeSpan by) =>
            Volatile.Write(ref _skewTicks, -(long)(by.TotalSeconds * inner.TimestampFrequency));

        public override long GetTimestamp() => inner.GetTimestamp() + Volatile.Read(ref _skewTicks);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }
}