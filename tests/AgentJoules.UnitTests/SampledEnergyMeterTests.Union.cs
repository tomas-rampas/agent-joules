using AgentJoules.Core.Energy;

using Xunit;

namespace AgentJoules.UnitTests;

// AJ-047: ComputeUnion on the sampled meter over the scripted NVML session (counter and sampling method) and the stub source, on the fake clock. The union is
// computed while the loop runs and after the windows were closed (the buffer and the counter marks are never trimmed in M2).
public sealed partial class SampledEnergyMeterTests
{
    [Theory]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    [InlineData("auto", 0.0)]
    [InlineData("sampling", Tolerance)]
    public async Task ComputeUnion_counts_the_overlap_of_two_windows_once(string method, double tolerance)
    {
        await using Rig rig = Rig.Create(method);
        await rig.StartAsync();

        // [0, 1.5] and [1, 2] overlap from 1 s to 1.5 s; their union is [0, 2]: 60 W x 2 s = 120 J, where the readings add up to 90 J + 60 J.
        IMeterWindow first = rig.Meter.OpenWindow();
        await rig.TickAsync(10);
        IMeterWindow second = rig.Meter.OpenWindow();
        await rig.TickAsync(5);
        EnergyReading firstReading = first.Close();
        await rig.TickAsync(5);
        EnergyReading secondReading = second.Close();

        UnionEnergy union = rig.Meter.ComputeUnion([first.Span, second.Span]);

        AssertMeasured(union.GrossJoules, 120.0, "J", tolerance);
        Assert.Equal(1, union.SegmentCount);
        Assert.Equal(2.0, union.UnionSeconds, 9);
        AssertNotReported(union.NetJoules, "no-baseline");
        AssertMeasured(firstReading.GrossJoules, 90.0, "J", tolerance);
        AssertMeasured(secondReading.GrossJoules, 60.0, "J", tolerance);
    }

    [Theory]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    [InlineData("auto", 0.0)]
    [InlineData("sampling", Tolerance)]
    public async Task ComputeUnion_never_counts_the_gap_between_disjoint_windows(string method, double tolerance)
    {
        await using Rig rig = Rig.Create(method);
        await rig.StartAsync();
        IMeterWindow first = rig.Meter.OpenWindow();
        await rig.TickAsync(10);
        _ = first.Close();

        // A second of 500 W between the windows is in the buffer and the counter, and in no window.
        rig.Api.ConstantPowerWatts = 500.0;
        await rig.TickAsync(10);
        rig.Api.ConstantPowerWatts = 60.0;
        IMeterWindow second = rig.Meter.OpenWindow();
        await rig.TickAsync(10);
        _ = second.Close();

        UnionEnergy union = rig.Meter.ComputeUnion([second.Span, first.Span]);

        AssertMeasured(union.GrossJoules, 120.0, "J", tolerance);
        Assert.Equal(2, union.SegmentCount);
        Assert.Equal(2.0, union.UnionSeconds, 9);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    public async Task ComputeUnion_subtracts_the_stored_baseline_over_the_union_length()
    {
        await using Rig rig = Rig.Create("auto", watts: 20.0);
        await rig.StartAsync();
        Task<BaselineResult> measuring = rig.Meter.MeasureBaselineAsync(TimeSpan.FromSeconds(1), 0.5, CancellationToken.None).AsTask();
        await rig.TickAsync(10);
        BaselineResult baseline = await measuring.WaitAsync(TimeSpan.FromSeconds(30));
        AssertMeasured(baseline.MeanW, 20.0, "W", Tolerance);

        rig.Api.ConstantPowerWatts = 60.0;
        IMeterWindow first = rig.Meter.OpenWindow();
        IMeterWindow second = rig.Meter.OpenWindow();
        await rig.TickAsync(10);
        _ = first.Close();
        _ = second.Close();

        UnionEnergy union = rig.Meter.ComputeUnion([first.Span, second.Span]);

        // 60 W x 1 s = 60 J gross; 60 J - 20 W x 1 s = 40 J net.
        AssertMeasured(union.GrossJoules, 60.0, "J", 0.0);
        AssertMeasured(union.NetJoules, 40.0, "J", Tolerance);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    public async Task ComputeUnion_under_the_counter_method_reads_counter_not_read_when_a_boundary_mark_failed()
    {
        // The counter calls of the meter: 1 at the first open, 2 at the second open, 3 at the first close (fails), 4 at the second close.
        StubSource stub = new() { Energy = static call => call == 3 ? null : 1000.0 * call };
        await using Rig rig = Rig.CreateStub(stub, "counter");
        await rig.StartAsync();
        IMeterWindow first = rig.Meter.OpenWindow();
        await rig.TickAsync(5);
        IMeterWindow second = rig.Meter.OpenWindow();
        await rig.TickAsync(5);
        _ = first.Close();
        await rig.TickAsync(5);
        _ = second.Close();

        // The union [0, 1.5] needs the marks at 0 s and 1.5 s, which were read; the failed mark of the first close is an inner edge.
        UnionEnergy overlapping = rig.Meter.ComputeUnion([first.Span, second.Span]);
        AssertMeasured(overlapping.GrossJoules, 3.0, "J", 0.0);

        // The first window alone ends at the failed mark.
        UnionEnergy alone = rig.Meter.ComputeUnion([first.Span]);
        AssertNotReported(alone.GrossJoules, "counter-not-read");
        AssertNotReported(alone.NetJoules, "counter-not-read");
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    public async Task ComputeUnion_reads_counter_went_backwards_when_the_counter_fell_over_the_union()
    {
        StubSource stub = new() { Energy = static call => 10_000.0 - (1000.0 * call) };
        await using Rig rig = Rig.CreateStub(stub, "counter");
        await rig.StartAsync();
        IMeterWindow window = rig.Meter.OpenWindow();
        await rig.TickAsync(5);
        _ = window.Close();

        UnionEnergy union = rig.Meter.ComputeUnion([window.Span]);

        AssertNotReported(union.GrossJoules, "counter-went-backwards");
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    public async Task ComputeUnion_refuses_before_start_after_dispose_and_for_a_list_that_is_not_a_union()
    {
        Rig rig = Rig.Create();
        try
        {
            _ = Assert.Throws<InvalidOperationException>(() => rig.Meter.ComputeUnion([new(0, 1)]));
            await rig.StartAsync();
            Assert.Equal("windows", Assert.Throws<ArgumentException>(() => rig.Meter.ComputeUnion([])).ParamName);
            Assert.Equal("windows", Assert.Throws<ArgumentException>(() => rig.Meter.ComputeUnion([new(1, 1)])).ParamName);
            _ = Assert.Throws<ArgumentNullException>(() => rig.Meter.ComputeUnion(null!));
        }
        finally
        {
            await rig.DisposeAsync();
        }

        _ = Assert.Throws<ObjectDisposedException>(() => rig.Meter.ComputeUnion([new(0, 1)]));
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    public async Task ComputeUnion_of_touching_windows_is_the_sum_of_their_readings_and_of_one_window_its_reading()
    {
        // The counter calls: 1 opens the first window (0 mJ), 2 closes it (1000 mJ), 3 opens the second at the same instant (1200 mJ), 4 closes it (2500 mJ).
        // The two reads at the shared instant differ, so a segment that took the other window's mark would be off by 200 mJ.
        double[] counter = [0.0, 1000.0, 1200.0, 2500.0];
        StubSource stub = new() { Energy = call => counter[call - 1] };
        await using Rig rig = Rig.CreateStub(stub, "counter");
        await rig.StartAsync();
        IMeterWindow first = rig.Meter.OpenWindow();
        await rig.TickAsync(10);
        EnergyReading firstReading = first.Close();
        IMeterWindow second = rig.Meter.OpenWindow();
        await rig.TickAsync(10);
        EnergyReading secondReading = second.Close();
        Assert.Equal(first.Span.StopSeconds, second.Span.StartSeconds);

        AssertMeasured(firstReading.GrossJoules, 1.0, "J", 0.0);
        AssertMeasured(secondReading.GrossJoules, 1.3, "J", 0.0);
        AssertMeasured(rig.Meter.ComputeUnion([first.Span]).GrossJoules, 1.0, "J", 0.0);
        AssertMeasured(rig.Meter.ComputeUnion([second.Span]).GrossJoules, 1.3, "J", 0.0);
        UnionEnergy both = rig.Meter.ComputeUnion([first.Span, second.Span]);
        AssertMeasured(both.GrossJoules, 2.3, "J", 0.0);
        Assert.Equal(2, both.SegmentCount);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    public async Task ComputeUnion_still_computes_the_closed_windows_after_the_sampling_loop_faulted()
    {
        StubSource stub = new() { Energy = static call => 1000.0 * call };
        await using Rig rig = Rig.CreateStub(stub, "counter");
        await rig.StartAsync();
        IMeterWindow first = rig.Meter.OpenWindow();
        await rig.TickAsync(5);
        IMeterWindow second = rig.Meter.OpenWindow();
        await rig.TickAsync(5);
        _ = first.Close();
        await rig.TickAsync(5);
        _ = second.Close();

        // A fault the loop does not skip (the real binding can raise it) ends the loop.
        stub.Power = static _ => throw new EntryPointNotFoundException("test fault");
        rig.Advance(rig.Period);
        await Rig.SettleAsync(() => rig.Meter.LoopFault is not null, "the loop fault", MaxYields);

        // The windows closed before the fault are still valid: open mark 1000 mJ at 0 s to close mark 4000 mJ at 1.5 s. No new window is served from a dead loop.
        UnionEnergy union = rig.Meter.ComputeUnion([first.Span, second.Span]);
        AssertMeasured(union.GrossJoules, 3.0, "J", 0.0);
        _ = Assert.IsType<EntryPointNotFoundException>(Assert.Throws<InvalidOperationException>(() => rig.Meter.OpenWindow()).InnerException);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task An_edge_earlier_than_the_last_counter_mark_is_a_clock_that_went_backwards_and_the_union_stays_computable()
    {
        SkewedTimeProvider? skewed = null;
        await using Rig rig = Rig.Create("auto", rateHz: 1.0, clock: time => skewed = new SkewedTimeProvider(time));
        await rig.StartAsync();
        rig.Advance(TimeSpan.FromMilliseconds(500));

        // The power read of the first edge fails, so the last sample stays at 0 s while the counter mark is recorded at 0.5 s.
        rig.FailPowerReadNumber(1);
        IMeterWindow first = rig.Meter.OpenWindow();
        Assert.Equal(1, rig.Meter.SkippedReadCount);

        // The clock steps back to 0.25 s: later than the last sample but earlier than the last mark.
        skewed!.GoBack(TimeSpan.FromMilliseconds(250));
        InvalidOperationException backwards = Assert.Throws<InvalidOperationException>(() => rig.Meter.OpenWindow());
        Assert.Contains("clock went backwards", backwards.Message, StringComparison.Ordinal);

        skewed.GoBack(TimeSpan.Zero);
        rig.Advance(TimeSpan.FromMilliseconds(250));
        _ = first.Close();

        // The marks stayed in time order, so the union is computed: 60 W x 0.25 s.
        AssertMeasured(rig.Meter.ComputeUnion([first.Span]).GrossJoules, 15.0, "J", 0.0);
    }
}