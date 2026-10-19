namespace AgentJoules.Core.Energy;

/// <summary>
/// One process-wide meter that samples GPU power in the background and hands out measurement windows (design 3.2).
/// Every window is computed from the same sample buffer, with or without the energy counter.
/// </summary>
/// <remarks>
/// The baseline measurement joins this interface with AJ-030 and the union computation with REQ-013 (M2); neither is declared
/// here, so no member of this interface is a stub.
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

    /// <summary>Opens a window: takes an immediate sample (and counter read) so the window has an edge sample at its exact start.</summary>
    /// <returns>The open window; its owner closes it exactly once.</returns>
    /// <exception cref="InvalidOperationException">The meter was not started, the clock went backwards, or the sampling loop stopped (the inner exception is the cause).</exception>
    /// <exception cref="ObjectDisposedException">The meter was disposed.</exception>
    IMeterWindow OpenWindow();
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