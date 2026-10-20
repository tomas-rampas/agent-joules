namespace AgentJoules.Metering;

/// <summary>
/// The gate of concurrent mode: it admits every caller at once, so measurement windows may overlap (REQ-005, design 3.3). It exists so that
/// concurrent mode is a configuration choice and not a second code path: the middleware always takes a gate, and in concurrent mode the
/// gate it is given does nothing.
/// </summary>
/// <remarks>
/// Stateless and thread-safe. Every <see cref="AcquireAsync"/> returns an already completed <see cref="ValueTask{TResult}"/> holding the
/// same cached releaser, whichever instance of this class it was called on, and that releaser's <c>DisposeAsync</c> does nothing, so it can
/// be disposed any number of times by any number of holders. The token is honoured at the door and is never awaited: an already cancelled
/// token gives a cancelled <see cref="ValueTask{TResult}"/> that carries the caller's token, as the serialized gate does. That path
/// allocates the cancelled <see cref="Task"/>, which is acceptable because it only happens at shutdown.
/// </remarks>
public sealed class NoOpMeterGate : IMeterGate
{
    // One releaser for the whole process: it holds no state, so every holder can share it.
    private static readonly IAsyncDisposable CachedReleaser = new Releaser();

    /// <summary>Creates a no-op gate; the gate holds no state, so <see cref="Instance"/> serves wherever one shared object is wanted.</summary>
    public NoOpMeterGate()
    {
    }

    /// <summary>The shared instance for the process.</summary>
    public static NoOpMeterGate Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<IAsyncDisposable>(cancellationToken)
            : ValueTask.FromResult(CachedReleaser);

    private sealed class Releaser : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}