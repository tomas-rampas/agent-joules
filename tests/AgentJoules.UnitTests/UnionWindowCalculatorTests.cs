using AgentJoules.Core.Energy;
using AgentJoules.Core.Energy.Testing;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-047: the union of windows (REQ-013, EDGE-004, design 3.2 "Union"): the strict overlap rule, the merge, the per-window shared decision, the sampling and
/// the counter computation, the shape of <see cref="UnionEnergy"/>, and <see cref="IEnergyMeter.ComputeUnion"/> on the deterministic and the hosted meter.
/// Every value is a fixture value computed by hand.
/// </summary>
[Trait("Spec", "REQ-013")]
[Trait("Spec", "EDGE-004")]
public sealed class UnionWindowCalculatorTests
{
    private const double Tolerance = 1e-9;
    private static readonly ThrottleReasons Quiet = new(0UL, true);

    // Windows are written as flat bound lists (start, stop, start, stop, ...), which the test runner can serialize row by row.
    public static TheoryData<double[], double[]> Merges => new()
    {
        { [0, 2, 1, 3], [0, 3] },
        { [0, 10, 2, 3], [0, 10] },
        { [0, 1, 1, 2], [0, 1, 1, 2] },
        { [5, 6, 0, 1], [0, 1, 5, 6] },
        { [0, 1, 0, 1], [0, 1] },
        { [3, 5, 0, 2, 1, 4, 6, 7], [0, 5, 6, 7] },
        { [1, 3, 0, 1, 3, 4], [0, 1, 1, 3, 3, 4] },
    };

    public static TheoryData<double[], bool[]> SharedDecisions => new()
    {
        { [0, 2, 1, 3, 3, 4, 5, 6], [true, true, false, false] },
        { [0, 10, 2, 3, 11, 12], [true, true, false] },
        { [5, 6, 0, 2, 5.5, 7, 1, 1.5], [true, true, true, true] },
        { [3, 4, 0, 1, 1, 3], [false, false, false] },
        { [0, 1], [false] },
        { [0, 1, 0, 1], [true, true] },
    };

    public static TheoryData<double[]> InvalidLists => new()
    {
        { [] },
        { [1, 1] },
        { [0, 1, 2, 1] },
        { [double.NaN, 1] },
        { [0, double.PositiveInfinity] },
        { [double.NegativeInfinity, 0] },
    };

    [Theory]
    [InlineData(0, 1, 0.5, 2, true)]
    [InlineData(0, 1, 0, 1, true)]
    [InlineData(0, 10, 2, 3, true)]
    [InlineData(0, 1, 1, 2, false)]
    [InlineData(1, 2, 0, 1, false)]
    [InlineData(0, 1, 2, 3, false)]
    public void Overlap_is_strict(double aStart, double aStop, double bStart, double bStop, bool expected)
    {
        WindowSpan a = new(aStart, aStop);
        WindowSpan b = new(bStart, bStop);

        Assert.Equal(expected, UnionWindowCalculator.Overlaps(a, b));
        Assert.Equal(expected, UnionWindowCalculator.Overlaps(b, a));
    }

    [Theory]
    [MemberData(nameof(Merges))]
    public void Merge_joins_strictly_overlapping_windows_in_start_order_and_keeps_touching_ones_apart(double[] windows, double[] expected)
    {
        Assert.Equal(Spans(expected), UnionWindowCalculator.Merge(Spans(windows)));
    }

    [Theory]
    [MemberData(nameof(SharedDecisions))]
    public void A_window_is_shared_exactly_when_it_strictly_overlaps_another_one(double[] windows, bool[] expected)
    {
        Assert.Equal(expected, UnionWindowCalculator.OverlapsAnother(Spans(windows)));
    }

    [Theory]
    [MemberData(nameof(InvalidLists))]
    public void A_union_of_nothing_or_of_an_open_window_is_an_argument_error(double[] bounds)
    {
        WindowSpan[] windows = Spans(bounds);
        Assert.Equal("windows", Assert.Throws<ArgumentException>(() => UnionWindowCalculator.Merge(windows)).ParamName);
        Assert.Equal("windows", Assert.Throws<ArgumentException>(() => UnionWindowCalculator.OverlapsAnother(windows)).ParamName);
        Assert.Equal("windows", Assert.Throws<ArgumentException>(() => UnionWindowCalculator.ComputeFromSamples(windows, Constant(60, 0, 1, 2), null)).ParamName);
        Assert.Equal("windows", Assert.Throws<ArgumentException>(() => UnionWindowCalculator.ComputeFromCounterMarks(windows, [Open(0, 0)], null)).ParamName);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Equal("windows", Assert.Throws<ArgumentNullException>(() => UnionWindowCalculator.Merge(null!)).ParamName);
        Assert.Equal("windows", Assert.Throws<ArgumentNullException>(() => UnionWindowCalculator.OverlapsAnother(null!)).ParamName);
    }

    [Fact]
    public void The_sampling_method_integrates_the_buffer_over_each_merged_segment_and_never_over_a_gap()
    {
        // 60 W everywhere except one sample of 1000 W in the gap between the segments [0, 1.5] and [2, 3]; the gap is not part of the union.
        PowerSample[] samples =
        [
            Sample(0.0, 60), Sample(0.5, 60), Sample(1.0, 60), Sample(1.5, 60), Sample(1.75, 1000), Sample(2.0, 60), Sample(2.5, 60), Sample(3.0, 60),
        ];

        UnionEnergy union = UnionWindowCalculator.ComputeFromSamples([new(0, 1), new(0.5, 1.5), new(2, 3)], samples, baselineMeanWatts: 20.0);

        // Gross 60 W x 2.5 s = 150 J; net 150 J - 20 W x 2.5 s = 100 J.
        AssertMeasured(union.GrossJoules, 150.0);
        AssertMeasured(union.NetJoules, 100.0);
        Assert.Equal(2.5, union.UnionSeconds, Tolerance);
        Assert.Equal(2, union.SegmentCount);
    }

    [Fact]
    public void A_segment_with_fewer_than_two_samples_makes_the_gross_and_the_net_not_reported()
    {
        PowerSample[] samples = [Sample(0.0, 60), Sample(1.0, 60), Sample(2.5, 60)];

        UnionEnergy union = UnionWindowCalculator.ComputeFromSamples([new(0, 1), new(2, 3)], samples, baselineMeanWatts: 20.0);

        Assert.Equal(Metric.NotReported("insufficient-samples"), union.GrossJoules);
        Assert.Equal(Metric.NotReported("insufficient-samples"), union.NetJoules);
        Assert.Equal(2.0, union.UnionSeconds, Tolerance);
    }

    [Fact]
    public void Samples_out_of_order_are_an_argument_error()
    {
        PowerSample[] samples = [Sample(0.0, 60), Sample(1.0, 60), Sample(0.5, 60)];

        Assert.Equal("samples", Assert.Throws<ArgumentException>(() => UnionWindowCalculator.ComputeFromSamples([new(0, 1)], samples, null)).ParamName);
    }

    [Fact]
    public void Without_a_baseline_the_net_is_not_reported_no_baseline_and_the_gross_is_kept()
    {
        UnionEnergy union = UnionWindowCalculator.ComputeFromSamples([new(0, 2)], Constant(30, 0, 1, 2), baselineMeanWatts: null);

        AssertMeasured(union.GrossJoules, 60.0);
        Assert.Equal(Metric.NotReported("no-baseline"), union.NetJoules);
    }

    [Fact]
    public void A_net_below_the_baseline_is_kept_as_a_measured_negative_value()
    {
        UnionEnergy union = UnionWindowCalculator.ComputeFromSamples([new(0, 2)], Constant(30, 0, 1, 2), baselineMeanWatts: 40.0);

        AssertMeasured(union.NetJoules, -20.0);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_baseline_that_is_not_an_idle_power_is_rejected(double baseline)
    {
        Assert.Equal("baselineMeanWatts", Assert.Throws<ArgumentOutOfRangeException>(() => UnionWindowCalculator.ComputeFromSamples([new(0, 2)], Constant(30, 0, 1, 2), baseline)).ParamName);
        Assert.Equal("baselineMeanWatts", Assert.Throws<ArgumentOutOfRangeException>(() => UnionWindowCalculator.ComputeFromCounterMarks([new(0, 2)], [Open(0, 0), Close(2, 1)], baseline)).ParamName);
    }

    [Fact]
    public void The_counter_method_sums_the_deltas_between_the_marks_at_the_segment_boundaries()
    {
        // Windows [0, 1] and [0.5, 2] merge to [0, 2] (open mark 1000 to close mark 4000 mJ: 3 J); [3, 4] is its own segment (5000 to 5500 mJ: 0.5 J).
        // The marks at 0.5 s and 1 s are inner edges and are not needed.
        CounterMark[] marks = [Open(0, 1000), Open(0.5, 1500), Close(1, 2000), Close(2, 4000), Open(3, 5000), Close(4, 5500)];

        UnionEnergy union = UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1), new(0.5, 2), new(3, 4)], marks, baselineMeanWatts: 1.0);

        AssertMeasured(union.GrossJoules, 3.5);
        AssertMeasured(union.NetJoules, 0.5);
        Assert.Equal(3.0, union.UnionSeconds, Tolerance);
        Assert.Equal(2, union.SegmentCount);
    }

    [Fact]
    public void Touching_windows_unite_to_the_sum_of_their_own_deltas_when_the_marks_at_the_shared_instant_differ()
    {
        // The first window closes at 1 s with 1000 mJ and the second opens at 1 s with 1200 mJ (the counter moved between the two reads). Each segment uses
        // the marks of its own window: (1000 - 0) + (2500 - 1200) = 2300 mJ, the sum of the two window deltas; never 2700 mJ (the open mark taken as the stop of
        // the first) and never 2500 mJ (the counter's whole movement, which counts the gap between the two reads).
        CounterMark[] marks = [Open(0, 0), Close(1, 1000), Open(1, 1200), Close(2, 2500)];

        UnionEnergy union = UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1), new(1, 2)], marks, baselineMeanWatts: null);

        AssertMeasured(union.GrossJoules, 2.3);
        Assert.Equal(2, union.SegmentCount);
    }

    [Fact]
    public void The_union_of_one_window_is_that_window_s_own_delta()
    {
        // The open mark of a later window at the stop instant is not the stop of this window.
        CounterMark[] marks = [Open(0, 0), Close(1, 1000), Open(1, 1200), Close(2, 2500)];

        AssertMeasured(UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1)], marks, baselineMeanWatts: null).GrossJoules, 1.0);
        AssertMeasured(UnionWindowCalculator.ComputeFromCounterMarks([new(1, 2)], marks, baselineMeanWatts: null).GrossJoules, 1.3);
    }

    [Fact]
    public void Overlapping_windows_count_the_overlap_once()
    {
        // [0, 1.5] and [1, 2]: the union runs from the open mark at 0 s to the close mark at 2 s; the window deltas (1500 and 1000 mJ) would count [1, 1.5] twice.
        CounterMark[] marks = [Open(0, 0), Open(1, 1000), Close(1.5, 1500), Close(2, 2000)];

        UnionEnergy union = UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1.5), new(1, 2)], marks, baselineMeanWatts: null);

        AssertMeasured(union.GrossJoules, 2.0);
        Assert.Equal(1, union.SegmentCount);
    }

    [Fact]
    public void Several_windows_at_one_instant_use_the_earliest_usable_open_and_the_latest_usable_close()
    {
        // Two windows open at 0 s and two close at 1 s; a failed read at each instant is covered by the other read of the same kind.
        CounterMark[] marks = [Open(0, null), Open(0, 100), Close(1, 1000), Close(1, null)];

        UnionEnergy union = UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1), new(0, 1)], marks, baselineMeanWatts: null);

        AssertMeasured(union.GrossJoules, 0.9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_missing_boundary_mark_makes_the_gross_not_reported_counter_not_read(bool markIsNull)
    {
        // The stop of the segment [0, 2] has no usable close mark: either nothing was recorded at 2 s or the read there failed. The inner mark at 1 s does not stand in for it.
        CounterMark[] marks = markIsNull ? [Open(0, 0), Close(1, 1000), Close(2, null)] : [Open(0, 0), Close(1, 1000)];

        UnionEnergy union = UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1), new(0.5, 2)], marks, baselineMeanWatts: 10.0);

        Assert.Equal(Metric.NotReported("counter-not-read"), union.GrossJoules);
        Assert.Equal(Metric.NotReported("counter-not-read"), union.NetJoules);
    }

    [Fact]
    public void A_boundary_with_only_a_mark_of_the_other_edge_reads_counter_not_read()
    {
        // The only mark at 1 s is an open mark: it cannot be the stop of [0, 1]. The only mark at 0 s is a close mark: it cannot be the start.
        Assert.Equal(Metric.NotReported("counter-not-read"), UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1)], [Open(0, 0), Open(1, 1000)], null).GrossJoules);
        Assert.Equal(Metric.NotReported("counter-not-read"), UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1)], [Close(0, 0), Close(1, 1000)], null).GrossJoules);
    }

    [Fact]
    public void A_negative_total_delta_makes_the_gross_not_reported_counter_went_backwards()
    {
        CounterMark[] marks = [Open(0, 5000), Close(1, 4000)];

        UnionEnergy union = UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1)], marks, baselineMeanWatts: 10.0);

        Assert.Equal(Metric.NotReported("counter-went-backwards"), union.GrossJoules);
        Assert.Equal(Metric.NotReported("counter-went-backwards"), union.NetJoules);
    }

    [Fact]
    public void One_segment_that_went_backwards_is_not_hidden_by_a_positive_total()
    {
        // -1000 mJ over [0, 1] and +3000 mJ over [2, 3]: the total is positive, but the counter went backwards in one segment.
        CounterMark[] marks = [Open(0, 5000), Close(1, 4000), Open(2, 0), Close(3, 3000)];

        UnionEnergy union = UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1), new(2, 3)], marks, baselineMeanWatts: null);

        Assert.Equal(Metric.NotReported("counter-went-backwards"), union.GrossJoules);
    }

    [Fact]
    public void Marks_out_of_order_not_finite_or_of_an_undefined_edge_are_an_argument_error()
    {
        Assert.Equal("marks", Assert.Throws<ArgumentException>(() => UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1)], [Close(1, 10), Open(0, 0)], null)).ParamName);
        Assert.Equal("marks", Assert.Throws<ArgumentException>(() => UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1)], [Open(double.NaN, 0), Close(1, 10)], null)).ParamName);
        Assert.Equal("marks", Assert.Throws<ArgumentException>(() => UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1)], [Open(0, double.NaN), Close(1, 10)], null)).ParamName);
        Assert.Equal("marks", Assert.Throws<ArgumentException>(() => UnionWindowCalculator.ComputeFromCounterMarks([new(0, 1)], [new(0, 0, (CounterEdge)9), Close(1, 10)], null)).ParamName);
    }

    [Fact]
    public void A_union_energy_that_exists_is_well_formed()
    {
        UnionEnergy union = new(Metric.Measured(10, "J"), Metric.Measured(-2, "J"), unionSeconds: 1.5, segmentCount: 1);
        Assert.Equal((10.0, -2.0, 1.5, 1), (union.GrossJoules.Value, union.NetJoules.Value, union.UnionSeconds, union.SegmentCount));

        UnionEnergy hosted = new(Metric.NotApplicable("hosted-tier"), Metric.NotApplicable("hosted-tier"), unionSeconds: 1.0, segmentCount: 1);
        Assert.Equal(MetricLabel.NotApplicable, hosted.GrossJoules.Label);

        Assert.Equal("grossJoules", Assert.Throws<ArgumentNullException>(() => new UnionEnergy(null!, Metric.Measured(1, "J"), 1.0, 1)).ParamName);
        Assert.Equal("netJoules", Assert.Throws<ArgumentNullException>(() => new UnionEnergy(Metric.Measured(1, "J"), null!, 1.0, 1)).ParamName);
        Assert.Equal("grossJoules", Assert.Throws<ArgumentException>(() => new UnionEnergy(Metric.Measured(1, "W"), Metric.Measured(1, "J"), 1.0, 1)).ParamName);
        Assert.Equal("netJoules", Assert.Throws<ArgumentException>(() => new UnionEnergy(Metric.Measured(1, "J"), Metric.EstimatedQuantity(1, "J"), 1.0, 1)).ParamName);
        Assert.Equal("netJoules", Assert.Throws<ArgumentException>(() => new UnionEnergy(Metric.NotReported("counter-not-read"), Metric.Measured(1, "J"), 1.0, 1)).ParamName);
        Assert.Equal("unionSeconds", Assert.Throws<ArgumentOutOfRangeException>(() => new UnionEnergy(Metric.Measured(1, "J"), Metric.Measured(1, "J"), 0.0, 1)).ParamName);
        Assert.Equal("unionSeconds", Assert.Throws<ArgumentOutOfRangeException>(() => new UnionEnergy(Metric.Measured(1, "J"), Metric.Measured(1, "J"), double.NaN, 1)).ParamName);
        Assert.Equal("segmentCount", Assert.Throws<ArgumentOutOfRangeException>(() => new UnionEnergy(Metric.Measured(1, "J"), Metric.Measured(1, "J"), 1.0, 0)).ParamName);
    }

    [Theory]
    [InlineData(MeterMethodSetting.Auto, true)]
    [InlineData(MeterMethodSetting.Sampling, true)]
    [InlineData(MeterMethodSetting.Auto, false)]
    public async Task The_deterministic_meter_keeps_its_exactness_over_a_union(MeterMethodSetting method, bool counterSupported)
    {
        FakeTimeProvider time = new();
        await using DeterministicEnergyMeter meter = new(time, static _ => 60.0, sampleRateHz: 10.0, counterSupported, method, baselineMeanWatts: 20.0);
        await meter.StartAsync(CancellationToken.None);
        double tolerance = meter.Capabilities.Method == EnergyMethod.Counter ? 0.0 : Tolerance;

        UnionEnergy same = meter.ComputeUnion([new(0, 1), new(0, 1)]);
        UnionEnergy touching = meter.ComputeUnion([new(0, 1), new(1, 2)]);
        UnionEnergy apart = meter.ComputeUnion([new(0.25, 1.25), new(0.5, 1.0), new(3, 3.15)]);

        AssertMeasured(same.GrossJoules, 60.0, tolerance);
        AssertMeasured(same.NetJoules, 40.0, tolerance);
        Assert.Equal(1, same.SegmentCount);
        AssertMeasured(touching.GrossJoules, 120.0, tolerance);
        Assert.Equal(2, touching.SegmentCount);
        AssertMeasured(apart.GrossJoules, 60.0 * 1.15, 1e-6);
        Assert.Equal(1.15, apart.UnionSeconds, Tolerance);
    }

    [Fact]
    public async Task The_deterministic_meter_refuses_a_union_before_start_and_after_dispose()
    {
        FakeTimeProvider time = new();
        DeterministicEnergyMeter meter = new(time, static _ => 60.0, sampleRateHz: 10.0, counterSupported: true, MeterMethodSetting.Auto);

        _ = Assert.Throws<InvalidOperationException>(() => meter.ComputeUnion([new(0, 1)]));
        await meter.StartAsync(CancellationToken.None);
        Assert.Equal("windows", Assert.Throws<ArgumentException>(() => meter.ComputeUnion([])).ParamName);
        await meter.DisposeAsync();
        _ = Assert.Throws<ObjectDisposedException>(() => meter.ComputeUnion([new(0, 1)]));
    }

    [Fact]
    public async Task The_hosted_meter_returns_NA_hosted_tier_for_a_valid_list_and_never_a_number()
    {
        FakeTimeProvider time = new();
        UnmeteredEnergyMeter meter = new(time);
        await meter.StartAsync(CancellationToken.None);
        IMeterWindow first = meter.OpenWindow();
        time.Advance(TimeSpan.FromSeconds(1));
        IMeterWindow second = meter.OpenWindow();
        time.Advance(TimeSpan.FromSeconds(1));
        _ = first.Close();
        _ = second.Close();

        UnionEnergy union = meter.ComputeUnion([first.Span, second.Span]);

        Assert.Equal(Metric.NotApplicable(MeterCapabilities.HostedTierReason), union.GrossJoules);
        Assert.Equal(Metric.NotApplicable(MeterCapabilities.HostedTierReason), union.NetJoules);
        Assert.Equal(2.0, union.UnionSeconds, Tolerance);
        Assert.Equal(1, union.SegmentCount);
        Assert.Equal("windows", Assert.Throws<ArgumentException>(() => meter.ComputeUnion([new(1, 1)])).ParamName);

        // Nothing is measured, so nothing can go stale: the answer is the same after the meter was disposed.
        await meter.DisposeAsync();
        Assert.Equal(union, meter.ComputeUnion([first.Span, second.Span]));
    }

    private static WindowSpan[] Spans(double[] bounds) => [.. Enumerable.Range(0, bounds.Length / 2).Select(i => new WindowSpan(bounds[2 * i], bounds[(2 * i) + 1]))];

    private static CounterMark Open(double seconds, double? millijoules) => new(seconds, millijoules, CounterEdge.Open);

    private static CounterMark Close(double seconds, double? millijoules) => new(seconds, millijoules, CounterEdge.Close);

    private static PowerSample Sample(double seconds, double watts) => new(seconds, watts, Quiet);

    private static PowerSample[] Constant(double watts, params double[] seconds) => [.. seconds.Select(instant => Sample(instant, watts))];

    private static void AssertMeasured(Metric metric, double expected, double tolerance = Tolerance)
    {
        Assert.Equal(MetricLabel.Measured, metric.Label);
        Assert.Equal("J", metric.Unit);
        Assert.InRange(metric.Value ?? double.NaN, expected - tolerance, expected + tolerance);
    }
}