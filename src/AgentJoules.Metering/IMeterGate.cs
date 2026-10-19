namespace AgentJoules.Metering;

/// <summary>
/// The process-wide gate that decides whether measurement windows may overlap (REQ-005, design 3.3). One instance exists per process and
/// every <c>MeteredChatClient</c>, including the one that wraps the judge, shares it, so "one agent at a time" is enforced in one place and
/// concurrent mode is a configuration choice, not a second code path. The instance is chosen by <c>--mode</c> in M2:
/// <see cref="SerializedMeterGate"/> for serialized mode, <see cref="NoOpMeterGate"/> for concurrent mode.
/// </summary>
/// <remarks>
/// <para>
/// A caller takes the gate before it opens a measurement window and disposes the returned releaser after it closes the window, in a
/// <c>finally</c> or an <c>await using</c>. The gate never looks at the window, the meter or the reading; it only admits tasks.
/// </para>
/// <para>
/// The serialized implementation is not re-entrant: a second acquire from the same logical flow deadlocks until its token fires. The
/// releaser must be disposed in a <c>finally</c> that begins right after the acquire returns, before anything that can throw, or the single
/// slot is lost for the process.
/// </para>
/// </remarks>
public interface IMeterGate
{
    /// <summary>Waits until the caller may open a measurement window and returns the releaser that gives the turn back.</summary>
    /// <param name="cancellationToken">Cancels the wait. While the gate is not disposed, a cancelled wait ends with an <see cref="OperationCanceledException"/> (or a subclass) and takes nothing from the gate.</param>
    /// <returns>The releaser; the caller disposes it exactly once when its window is closed. Disposing it again does nothing.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled before or while the caller waited.</exception>
    /// <exception cref="ObjectDisposedException">The gate was disposed (a gate that owns state; the no-op gate owns none).</exception>
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
}