namespace AgentJoules.Metering;

/// <summary>
/// A gate that wraps the gate of the mode and, once stopped, refuses every acquisition (AJ-123, EDGE-016). The cloudgpu tier stops it when a call ends in any way but <c>ok</c> or cancelled (a lost connection above all; the step fails either way):
/// the sibling that waits for the gate, or asks for it later, gets an <see cref="IncompleteRunException"/> and sends no request, so no further call and no retry follows the drop.
/// </summary>
/// <remarks>
/// The stop is set by the call that dropped, before it gives the gate back, so a waiter that is handed the slot sees the stop and gives the slot straight back. In concurrent mode the inner gate
/// admits everyone at once and a sibling is already on the wire; the stop then only keeps back the calls that start later. Thread-safe.
/// </remarks>
/// <param name="inner">The gate of the mode.</param>
public sealed class StopAfterDropMeterGate(IMeterGate inner) : IMeterGate, IStoppableMeterGate
{
    private readonly IMeterGate _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    private int _stopped;

    /// <summary>True once <see cref="Halt"/> was called.</summary>
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    /// <inheritdoc />
    public void Halt() => Volatile.Write(ref _stopped, 1);

    /// <inheritdoc />
    /// <exception cref="IncompleteRunException">The gate was stopped before the caller got the turn.</exception>
    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) =>
        IsStopped ? throw new IncompleteRunException() : AcquireCoreAsync(cancellationToken);

    private async ValueTask<IAsyncDisposable> AcquireCoreAsync(CancellationToken cancellationToken)
    {
        IAsyncDisposable releaser = await _inner.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (IsStopped)
        {
            await releaser.DisposeAsync().ConfigureAwait(false);
            throw new IncompleteRunException();
        }

        return releaser;
    }
}

/// <summary>A metered call was refused because an earlier call of the run ended <c>incomplete</c> (a lost connection on the cloudgpu tier); the run makes no further request.</summary>
public sealed class IncompleteRunException : InvalidOperationException
{
    /// <summary>Creates the exception with its fixed message.</summary>
    public IncompleteRunException()
        : base("The run stopped after a lost connection; no further request is made.")
    {
    }
}