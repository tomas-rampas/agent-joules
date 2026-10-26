using AgentJoules.Metering;

namespace AgentJoules.TestSupport;

/// <summary>
/// A serialized gate for the tests of the cloudgpu stop (AJ-123, EDGE-016): it wraps a <see cref="SerializedMeterGate"/>, tells when a second caller waits for the slot that a first caller holds, and can keep a releaser
/// busy for a short real time after the slot was handed on. The first lets a test hold a dropping stream open until its sibling really queues; the second makes a gate that is stopped only after it was released visible,
/// because the waiter is then admitted before the stop and sends its request.
/// </summary>
/// <param name="releaseLag">Real time that the disposal of a releaser takes after the slot was given back to a queued waiter; zero for none. Bounded by the caller.</param>
public sealed class QueueObservingMeterGate(TimeSpan releaseLag = default) : IMeterGate, IDisposable
{
    private readonly SerializedMeterGate _inner = new();
    private readonly TimeSpan _releaseLag = releaseLag;
    private readonly TaskCompletionSource _waiterQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _holders;
    private int _waiting;
    private int _acquisitions;

    /// <summary>Completes when a caller asks for the gate while another caller holds it, that is when a sibling is queued behind a holder.</summary>
    public Task WaiterQueued => _waiterQueued.Task;

    /// <summary>How many callers got the slot.</summary>
    public int Acquisitions => Volatile.Read(ref _acquisitions);

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _holders) > 0)
        {
            _ = _waiterQueued.TrySetResult();
        }

        _ = Interlocked.Increment(ref _waiting);
        IAsyncDisposable inner;
        try
        {
            inner = await _inner.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _waiting);
        }

        _ = Interlocked.Increment(ref _holders);
        _ = Interlocked.Increment(ref _acquisitions);
        return new Releaser(this, inner);
    }

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();

    private sealed class Releaser(QueueObservingMeterGate owner, IAsyncDisposable inner) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // The lag applies only to a release that hands the slot to a queued waiter; the give-back of the refused waiter has nobody behind it and is not delayed.
            bool waiterQueued = Volatile.Read(ref owner._waiting) > 0;
            _ = Interlocked.Decrement(ref owner._holders);
            await inner.DisposeAsync().ConfigureAwait(false);
            if (waiterQueued && owner._releaseLag > TimeSpan.Zero)
            {
                await Task.Delay(owner._releaseLag).ConfigureAwait(false);
            }
        }
    }
}