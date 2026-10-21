using System.Globalization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Energy;

/// <summary>
/// The union of measurement windows (REQ-013, EDGE-004, design 3.2 "Union"): the strict overlap rule, the merge of overlapping windows into segments, the
/// per-window <c>shared-window</c> decision, and the energy of the union from the sample buffer or from the counter marks of the window edges. Pure: the
/// same input always gives the same result, and nothing here reads a clock or a device.
/// </summary>
/// <remarks>
/// <para>
/// Overlap is strict: a overlaps b when <c>a.StartSeconds &lt; b.StopSeconds</c> and <c>b.StartSeconds &lt; a.StopSeconds</c>. Windows that only touch
/// (one stops at the instant the other starts) do not overlap: they are two segments and neither is shared. The energy is the same either way; the segment
/// count is not.
/// </para>
/// <para>
/// A segment is a maximal set of windows linked by strict overlaps, from the earliest start to the latest stop among them. Every boundary of a segment is
/// some window's open or close, where the meter took an edge sample and, under the counter method, a counter mark, so the union needs no value that was
/// not read. The sampling method integrates the samples of each segment with <see cref="PowerIntegrator"/> and sums; the counter method sums the
/// counter deltas from the open mark at the start to the close mark at the stop of each segment, rounded to whole millijoules as a window's delta is, so
/// the union of one window is its own delta and two touching windows unite to the sum of their deltas. The net subtracts the
/// baseline power times the summed segment length. A value that cannot be computed is not-reported with the same reason a window's reading would give
/// (<c>insufficient-samples</c>, <c>counter-not-read</c>, <c>counter-went-backwards</c>, <c>no-baseline</c>), never 0.
/// </para>
/// <para>
/// Arguments. Every method refuses a null list, an empty list and a window whose bounds are not finite or that does not stop after it starts (an open
/// window has a stop equal to its start) with an <see cref="ArgumentException"/> naming <c>windows</c>: a union of nothing is not a measurement.
/// </para>
/// </remarks>
public static class UnionWindowCalculator
{
    // 2^63: the first double above every long.
    private const double LongUpperBound = 9223372036854775808.0;

    /// <summary>Whether two windows strictly overlap: each starts before the other stops. Windows that only touch do not.</summary>
    /// <param name="a">One window.</param>
    /// <param name="b">The other window.</param>
    /// <returns>True when the windows share an interval of positive length.</returns>
    public static bool Overlaps(WindowSpan a, WindowSpan b) => a.StartSeconds < b.StopSeconds && b.StartSeconds < a.StopSeconds;

    /// <summary>Merges strictly overlapping windows into segments, ordered by start; windows that only touch stay two segments.</summary>
    /// <param name="windows">The closed windows, in any order; at least one.</param>
    /// <returns>The disjoint segments in start order; a segment touches the next one at most.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="windows"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="windows"/> is empty or holds a window that is not finite or does not stop after it starts.</exception>
    public static WindowSpan[] Merge(IReadOnlyList<WindowSpan> windows)
    {
        (WindowSpan[] segments, _) = Group(windows);
        return segments;
    }

    /// <summary>
    /// For every window, whether it strictly overlaps at least one other window of the list: the <c>shared-window</c> decision of the run assembler. A
    /// window is shared exactly when its segment holds more than one window.
    /// </summary>
    /// <param name="windows">The closed windows, in any order; at least one.</param>
    /// <returns>One decision per window, at the window's index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="windows"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="windows"/> is empty or holds a window that is not finite or does not stop after it starts.</exception>
    public static bool[] OverlapsAnother(IReadOnlyList<WindowSpan> windows)
    {
        (WindowSpan[] segments, int[] segmentOf) = Group(windows);
        int[] members = new int[segments.Length];
        foreach (int segment in segmentOf)
        {
            members[segment]++;
        }

        bool[] shared = new bool[segmentOf.Length];
        for (int i = 0; i < shared.Length; i++)
        {
            shared[i] = members[segmentOf[i]] > 1;
        }

        return shared;
    }

    /// <summary>
    /// The energy of the union under the sampling method: the windows are merged, the samples of each segment (those at or inside its bounds) are
    /// integrated with <see cref="PowerIntegrator"/>, and the integrals are summed. A sample between two segments is in no segment. The samples are
    /// read in place, never copied: the caller passes a snapshot it owns (the sampled meter copies only the samples inside the segments, under its lock).
    /// </summary>
    /// <param name="windows">The closed windows, in any order; at least one. Merged segments may be passed again: merging them changes nothing.</param>
    /// <param name="samples">The samples in strictly increasing timestamp order; it may hold samples outside the windows.</param>
    /// <param name="baselineMeanWatts">The idle baseline power (finite, not negative), or null when none is stored.</param>
    /// <returns>The energy; the gross is not-reported <c>insufficient-samples</c> when any segment has fewer than two samples.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="windows"/> is null.</exception>
    /// <exception cref="ArgumentException">The windows are not a union (see the remarks), or the samples are not in strictly increasing timestamp order or are invalid (see <see cref="PowerIntegrator"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="baselineMeanWatts"/> is negative or not finite, or a computed energy is not finite.</exception>
    internal static UnionEnergy ComputeFromSamples(IReadOnlyList<WindowSpan> windows, ReadOnlySpan<PowerSample> samples, double? baselineMeanWatts)
    {
        WindowSpan[] segments = Merge(windows);
        RequireBaseline(baselineMeanWatts);
        for (int i = 1; i < samples.Length; i++)
        {
            if (!(samples[i].Seconds > samples[i - 1].Seconds))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"The sample at index {i} at {samples[i].Seconds} s is not after the previous sample at {samples[i - 1].Seconds} s."),
                    nameof(samples));
            }
        }

        double joules = 0.0;
        bool missing = false;
        foreach (WindowSpan segment in segments)
        {
            int low = FirstIndex(samples, segment.StartSeconds, inclusive: true);
            int high = FirstIndex(samples, segment.StopSeconds, inclusive: false);
            if (PowerIntegrator.IntegrateJoules(samples[low..high], segment.StartSeconds, segment.StopSeconds) is { } integral)
            {
                joules += integral;
            }
            else
            {
                missing = true;
            }
        }

        Metric gross = missing ? Metric.NotReported(EnergyReasons.InsufficientSamples) : Metric.Measured(Finite(joules, nameof(samples)), EnergyShapeRules.JouleUnit);
        return Build(gross, segments, baselineMeanWatts);
    }

    /// <summary>
    /// The energy of the union under the counter method: the windows are merged and, for every segment, the counter delta from the mark at its start to
    /// the mark at its stop is rounded to whole millijoules (a negative difference is floored, as a window's delta is) and the deltas are summed. A
    /// segment start reads an <see cref="CounterEdge.Open"/> mark recorded at that instant (the earliest usable one when several windows opened then), and
    /// a segment stop reads a <see cref="CounterEdge.Close"/> mark recorded at that instant (the latest usable one when several closed then). So the union
    /// of one window is that window's own delta, two windows that touch unite to the sum of their two deltas, and an overlap is counted once.
    /// </summary>
    /// <param name="windows">The closed windows, in any order; at least one. Merged segments may be passed again: merging them changes nothing.</param>
    /// <param name="marks">The counter marks recorded at window edges, in non-decreasing time order, read in place; a null value is a failed read.</param>
    /// <param name="baselineMeanWatts">The idle baseline power (finite, not negative), or null when none is stored.</param>
    /// <returns>
    /// The energy; the gross is not-reported <c>counter-not-read</c> when a segment boundary has no usable mark of the kind it needs (none recorded at that
    /// instant, every read there failed, or a delta that does not fit a whole number of millijoules), and <c>counter-went-backwards</c> when the delta of any
    /// segment, and therefore when the total, is negative.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="windows"/> is null.</exception>
    /// <exception cref="ArgumentException">The windows are not a union (see the remarks), or a mark has a time or a value that is not finite or an undefined edge, or the marks are not in time order.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="baselineMeanWatts"/> is negative or not finite, or a computed energy is not finite.</exception>
    internal static UnionEnergy ComputeFromCounterMarks(IReadOnlyList<WindowSpan> windows, ReadOnlySpan<CounterMark> marks, double? baselineMeanWatts)
    {
        WindowSpan[] segments = Merge(windows);
        RequireBaseline(baselineMeanWatts);
        for (int i = 0; i < marks.Length; i++)
        {
            CounterMark mark = marks[i];
            if (!double.IsFinite(mark.Seconds) || (mark.Millijoules is { } value && !double.IsFinite(value)) || !Enum.IsDefined(mark.Edge))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The counter mark at index {i} has a time or a value that is not finite or an undefined edge."), nameof(marks));
            }

            if (i > 0 && mark.Seconds < marks[i - 1].Seconds)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"The counter mark at index {i} at {mark.Seconds} s is earlier than the previous mark at {marks[i - 1].Seconds} s."),
                    nameof(marks));
            }
        }

        double millijoules = 0.0;
        bool notRead = false;
        bool backwards = false;
        foreach (WindowSpan segment in segments)
        {
            long? delta = CounterDeltaOf(OpenMarkAt(marks, segment.StartSeconds), CloseMarkAt(marks, segment.StopSeconds));
            if (delta is not { } whole)
            {
                notRead = true;
            }
            else
            {
                backwards |= whole < 0;
                millijoules += whole;
            }
        }

        // A missing boundary wins: without it the total cannot be stated at all. A segment that went backwards is never hidden by the others.
        Metric gross = notRead
            ? Metric.NotReported(EnergyReasons.CounterNotRead)
            : backwards || millijoules < 0.0
                ? Metric.NotReported(EnergyReasons.CounterWentBackwards)
                : Metric.Measured(Finite(millijoules / 1000.0, nameof(marks)), EnergyShapeRules.JouleUnit);
        return Build(gross, segments, baselineMeanWatts);
    }

    /// <summary>
    /// The counter delta between two marks in whole millijoules, the one rounding rule of a window and of a union segment: a negative difference is floored,
    /// so a counter that went backwards never rounds to 0 and reads as an idle window, and a difference that does not fit a <see cref="long"/> has no delta.
    /// </summary>
    /// <param name="openMark">The mark at the start, or null when it was not read.</param>
    /// <param name="closeMark">The mark at the stop, or null when it was not read.</param>
    /// <returns>The delta, or null when a mark is missing or the delta does not fit.</returns>
    internal static long? CounterDeltaOf(double? openMark, double? closeMark)
    {
        if (openMark is not { } open || closeMark is not { } close)
        {
            return null;
        }

        double difference = close - open;
        double delta = difference < 0.0 ? Math.Floor(difference) : Math.Round(difference);
        return double.IsFinite(delta) && delta >= long.MinValue && delta < LongUpperBound ? (long)delta : null;
    }

    // Validates the windows and merges them: the segments in start order and, for every window at its index, the segment it belongs to.
    private static (WindowSpan[] Segments, int[] SegmentOf) Group(IReadOnlyList<WindowSpan> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0)
        {
            throw new ArgumentException("The union of no window is not a measurement: at least one window is needed.", nameof(windows));
        }

        int[] order = new int[windows.Count];
        for (int i = 0; i < order.Length; i++)
        {
            WindowSpan window = windows[i];
            if (!double.IsFinite(window.StartSeconds) || !double.IsFinite(window.StopSeconds) || !(window.StopSeconds > window.StartSeconds))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"The window at index {i} must have finite bounds and stop after it starts (a closed window), but it is {window.StartSeconds} to {window.StopSeconds} s."),
                    nameof(windows));
            }

            order[i] = i;
        }

        // By start, ties by index: a total order, so the result never depends on the sort algorithm.
        Array.Sort(order, (x, y) =>
        {
            int byStart = windows[x].StartSeconds.CompareTo(windows[y].StartSeconds);
            return byStart != 0 ? byStart : x.CompareTo(y);
        });

        List<WindowSpan> segments = [];
        int[] segmentOf = new int[order.Length];
        double start = windows[order[0]].StartSeconds;
        double stop = windows[order[0]].StopSeconds;
        foreach (int index in order)
        {
            WindowSpan window = windows[index];

            // In start order, a window strictly overlaps the open segment exactly when it starts before the segment's latest stop; touching starts a new one.
            if (window.StartSeconds >= stop)
            {
                segments.Add(new WindowSpan(start, stop));
                (start, stop) = (window.StartSeconds, window.StopSeconds);
            }
            else
            {
                stop = Math.Max(stop, window.StopSeconds);
            }

            segmentOf[index] = segments.Count;
        }

        segments.Add(new WindowSpan(start, stop));
        return ([.. segments], segmentOf);
    }

    private static UnionEnergy Build(Metric gross, WindowSpan[] segments, double? baselineMeanWatts)
    {
        double unionSeconds = 0.0;
        foreach (WindowSpan segment in segments)
        {
            unionSeconds += segment.DurationSeconds;
        }

        unionSeconds = Finite(unionSeconds, "windows");

        // The net follows the gross: a missing gross carries its reason, whether or not a baseline exists; a negative net is kept (REQ-005).
        Metric net = gross.Value is not { } grossJoules
            ? Metric.NotReported(gross.Reason!)
            : baselineMeanWatts is { } baseline
                ? Metric.Measured(Finite(grossJoules - (baseline * unionSeconds), nameof(baselineMeanWatts)), EnergyShapeRules.JouleUnit)
                : Metric.NotReported(EnergyReasons.NoBaseline);
        return new UnionEnergy(gross, net, unionSeconds, segments.Length);
    }

    private static void RequireBaseline(double? baselineMeanWatts)
    {
        if (baselineMeanWatts is { } baseline && (!double.IsFinite(baseline) || baseline < 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(baselineMeanWatts), baseline, "A baseline is an idle power: finite and not negative.");
        }
    }

    // A computed energy or length that overflowed names the input it came from.
    private static double Finite(double value, string parameterName) =>
        double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(parameterName, value, "The energy or the length of the union is not finite.");

    // The earliest usable open mark recorded at exactly this instant (a segment start), or null; a close mark at the instant never stands in for it.
    private static double? OpenMarkAt(ReadOnlySpan<CounterMark> marks, double seconds)
    {
        for (int i = FirstIndex(marks, seconds, inclusive: true); i < marks.Length && marks[i].Seconds == seconds; i++)
        {
            if (marks[i].Edge == CounterEdge.Open && marks[i].Millijoules is { } value)
            {
                return value;
            }
        }

        return null;
    }

    // The latest usable close mark recorded at exactly this instant (a segment stop), or null; an open mark at the instant never stands in for it.
    private static double? CloseMarkAt(ReadOnlySpan<CounterMark> marks, double seconds)
    {
        for (int i = FirstIndex(marks, seconds, inclusive: false) - 1; i >= 0 && marks[i].Seconds == seconds; i--)
        {
            if (marks[i].Edge == CounterEdge.Close && marks[i].Millijoules is { } value)
            {
                return value;
            }
        }

        return null;
    }

    // Index of the first sample at or after (inclusive) or strictly after (not inclusive) the instant, in samples ordered by time; the length when there is none.
    private static int FirstIndex(ReadOnlySpan<PowerSample> samples, double seconds, bool inclusive)
    {
        int low = 0;
        int high = samples.Length;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            double at = samples[mid].Seconds;
            if (inclusive ? at >= seconds : at > seconds)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }

    // The same search over counter marks ordered by time.
    private static int FirstIndex(ReadOnlySpan<CounterMark> marks, double seconds, bool inclusive)
    {
        int low = 0;
        int high = marks.Length;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            double at = marks[mid].Seconds;
            if (inclusive ? at >= seconds : at > seconds)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }
}