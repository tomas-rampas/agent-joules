namespace AgentJoules.Metering;

/// <summary>
/// The gate of serialized mode: one holder at a time, so measurement windows never overlap (REQ-005, design 3.3). It wraps one
/// <see cref="SemaphoreSlim"/> with a count of one.
/// </summary>
/// <remarks>
/// <para>
/// Order. Waiters are admitted in the order they called <see cref="AcquireAsync"/> (first in, first out): <see cref="SemaphoreSlim"/> queues
/// asynchronous waiters in order and hands a released slot to the one at the head. The gate is not re-entrant: a task that asks again while
/// it holds the gate waits for itself, until its token fires.
/// </para>
/// <para>
/// Using the releaser. A caller must start the <c>try</c>/<c>finally</c> (or <c>await using</c>) that disposes the releaser immediately after
/// <see cref="AcquireAsync"/> returns, before <c>OpenWindow</c> or anything else that can throw. There is one slot for the whole process: a
/// releaser that is never disposed loses it for every later caller.
/// </para>
/// <para>
/// Release is idempotent. Each acquire returns its own releaser, and that releaser gives the slot back exactly once; disposing it a second
/// time does nothing, so a double dispose admits only one waiter and never lets a later holder's slot be released by a stale releaser.
/// </para>
/// <para>
/// Cancellation. A waiter whose token is cancelled never consumes the count, so the gate stays usable and no slot leaks. A waiter cancelled
/// while queued ends with exactly <see cref="OperationCanceledException"/>; an already cancelled token ends with
/// <see cref="TaskCanceledException"/> (the task is created with <see cref="Task.FromCanceled(CancellationToken)"/>), even when the gate is
/// free. Callers and tests should catch the base type, <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// Disposal. <see cref="Dispose"/> disposes the semaphore and is safe to call twice. <see cref="AcquireAsync"/> after that throws
/// <see cref="ObjectDisposedException"/>, synchronously when <see cref="Dispose"/> completed before the call; a <see cref="Dispose"/> racing
/// the call surfaces the same exception through the returned task. A releaser disposed after the gate is disposed does nothing and does
/// not throw. A waiter still queued when the gate is disposed never completes, even if its token is later cancelled
/// (<see cref="SemaphoreSlim.Dispose()"/> discards its queue); dispose the gate only after every metered call has finished. Release and
/// disposal take one private lock, so a release can never meet a disposed semaphore.
/// </para>
/// </remarks>
public sealed class SerializedMeterGate : IMeterGate, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Lock _sync = new();

    private bool _disposed;

    /// <summary>
    /// The number of free slots: 1 when no task holds the gate, 0 while one does (a slot handed straight to a waiter stays at 0). Exposed for
    /// tests. After <see cref="Dispose"/> it keeps reporting the count at disposal.
    /// </summary>
    public int CurrentCount => _semaphore.CurrentCount;

    /// <inheritdoc />
    /// <remarks>
    /// Throws <see cref="ObjectDisposedException"/>, synchronously when <see cref="Dispose"/> completed before the call; a
    /// <see cref="Dispose"/> racing the call surfaces the same exception through the returned task.
    /// </remarks>
    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        return AcquireCoreAsync(cancellationToken);
    }

    /// <summary>Disposes the semaphore. Safe to call twice; a releaser disposed afterwards does nothing.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _semaphore.Dispose();
        }
    }

    private async ValueTask<IAsyncDisposable> AcquireCoreAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(this);
    }

    private void Release()
    {
        // Under the lock with Dispose, so the disposed flag cannot change between the check and the release; no exception is caught.
        lock (_sync)
        {
            if (!_disposed)
            {
                _semaphore.Release();
            }
        }
    }

    private sealed class Releaser(SerializedMeterGate owner) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}