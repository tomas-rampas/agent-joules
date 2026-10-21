namespace AgentJoules.Core.Energy;

/// <summary>
/// One read of the GPU energy counter at a window edge, as the meter recorded it (design 3.2 "Union"): every boundary of a merged segment is some
/// window's open or close, where such a mark was taken, so the union of the windows is computed from these marks under the counter method. A segment
/// start reads an <see cref="CounterEdge.Open"/> mark and a segment stop a <see cref="CounterEdge.Close"/> mark, so two windows that touch at one instant
/// never trade marks: each segment is measured with the marks of its own windows.
/// </summary>
/// <param name="Seconds">The instant of the edge, in seconds since the meter started; exactly the start or the stop of the window's span.</param>
/// <param name="Millijoules">The counter value in millijoules, or null when the read failed or gave no usable value (the boundary then reads <c>counter-not-read</c>).</param>
/// <param name="Edge">Whether the mark was taken when a window opened or when it closed.</param>
internal readonly record struct CounterMark(double Seconds, double? Millijoules, CounterEdge Edge);