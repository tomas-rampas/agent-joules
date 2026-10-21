namespace AgentJoules.Core.Energy;

/// <summary>
/// One process-wide meter that samples GPU power in the background and hands out measurement windows (design 3.2).
/// Every window is computed from the same sample buffer, with or without the energy counter.
/// </summary>
/// <remarks>
/// <see cref="ComputeUnion"/> computes the run-level energy of a concurrent run over the union of its windows (REQ-013, EDGE-004), from the same
/// buffer and counter marks the windows were computed from.
/// </remarks>
public interface IEnergyMeter : IAsyncDisposable
{
    /// <summary>What the meter measures with and why; the method, the counter state and whether sampling is a fallback.</summary>
    MeterCapabilities Capabilities { get; }

    /// <summary>Takes the first sample immediately and starts the background sampling loop.</summary>
    /// <param name="cancellationToken">Checked before the meter starts; the loop itself ends when the meter is disposed.</param>
    /// <exception cref="InvalidOperationException">The meter was already started.</exception>
    /// <exception cref="ObjectDisposedException">The meter was disposed.</exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled.</exception>
    ValueTask StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Measures the idle baseline: takes an edge sample now, waits <paramref name="duration"/> on the meter's time provider while the
    /// sampling loop collects, takes an edge sample at the end and computes the baseline statistics of the samples in between. The
    /// meter keeps the mean as its baseline (the most recently completed measurement replaces an earlier one; a baseline whose mean is not reported clears it), and every
    /// window closed afterwards computes its net energy against it.
    /// </summary>
    /// <param name="duration">
    /// How long to collect, waited in whole milliseconds (a fraction of a millisecond is dropped); at least 1 ms and no longer than a
    /// timer can wait (<c>uint.MaxValue - 1</c> ms). The <c>Seconds</c> of the result is the window actually measured, stop minus start.
    /// </param>
    /// <param name="maxCoefficientOfVariation">
    /// The stability threshold (<c>Baseline:MaxCoefficientOfVariation</c>); finite and positive. It is a parameter because the
    /// capabilities describe the device, not the configuration.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait; nothing is stored then.</param>
    /// <returns>The baseline; <see cref="Labels.ReadingFlags.BaselineUnstable"/> is set when its coefficient of variation exceeds the threshold.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> or <paramref name="maxCoefficientOfVariation"/> is out of range; no sample was taken.</exception>
    /// <exception cref="InvalidOperationException">The meter was not started, the clock went backwards or did not advance, or the sampling loop stopped before or during the wait (the inner exception is the cause); a stopped loop ends the wait at once.</exception>
    /// <exception cref="ObjectDisposedException">The meter was disposed, before or during the wait; disposal ends the wait at once.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before or during the wait.</exception>
    ValueTask<BaselineResult> MeasureBaselineAsync(TimeSpan duration, double maxCoefficientOfVariation, CancellationToken cancellationToken);

    /// <summary>Opens a window: takes an immediate sample (and counter read) so the window has an edge sample at its exact start.</summary>
    /// <returns>The open window; its owner closes it exactly once.</returns>
    /// <exception cref="InvalidOperationException">The meter was not started, the clock went backwards, or the sampling loop stopped (the inner exception is the cause).</exception>
    /// <exception cref="ObjectDisposedException">The meter was disposed.</exception>
    IMeterWindow OpenWindow();

    /// <summary>
    /// Computes the energy of the union of <paramref name="windows"/>, the concurrent-mode totals (REQ-013, design 3.2 "Union"): the windows are
    /// merged where they strictly overlap (<see cref="UnionWindowCalculator.Merge"/>; windows that only touch stay two segments), the counter method
    /// sums the counter deltas between the marks recorded at the segment boundaries, the sampling method integrates the sample buffer over each
    /// segment, and the net subtracts the stored baseline power times the summed segment length. A hosted meter returns N/A <c>hosted-tier</c>.
    /// </summary>
    /// <param name="windows">The spans of closed windows of this meter, in any order; at least one.</param>
    /// <returns>The energy of the union; a value that cannot be computed is not-reported with its reason, never 0.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="windows"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="windows"/> is empty, or a window has a bound that is not finite or does not stop after it starts (an open window); checked before anything else.</exception>
    /// <exception cref="InvalidOperationException">A measuring meter was not started.</exception>
    /// <exception cref="ObjectDisposedException">A measuring meter was disposed.</exception>
    UnionEnergy ComputeUnion(IReadOnlyList<WindowSpan> windows);
}

/// <summary>
/// One measurement window of an <see cref="IEnergyMeter"/>: a slice of the meter's sample buffer between two edge samples.
/// </summary>
public interface IMeterWindow
{
    /// <summary>The slice the window covers; <see cref="WindowSpan.StopSeconds"/> equals the start until <see cref="Close"/> returns.</summary>
    WindowSpan Span { get; }

    /// <summary>Closes the window: takes an immediate sample (and counter read) and computes the reading of the slice.</summary>
    /// <returns>The reading of the window.</returns>
    /// <exception cref="InvalidOperationException">The window was already closed, it has no duration, the clock went backwards, or the sampling loop stopped (the inner exception is the cause).</exception>
    /// <exception cref="ObjectDisposedException">The meter was disposed.</exception>
    EnergyReading Close();
}