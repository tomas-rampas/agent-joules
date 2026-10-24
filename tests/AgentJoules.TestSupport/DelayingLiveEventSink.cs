using System.Collections.Concurrent;

using AgentJoules.Core.Live;

namespace AgentJoules.TestSupport;

/// <summary>
/// The delayed variant of the latency check (REQ-032 R1, P-77): a live sink in front of the real one that hands every event on <see cref="Delay"/> late with its original
/// <c>AtUtc</c>, so a client sees each event arrive that much after its emit instant and the 250 ms check must fail. <see cref="Emit"/> never blocks and never throws, like every live
/// sink; <see cref="DrainAsync"/> completes once every event emitted before the call was handed on.
/// </summary>
/// <param name="inner">The sink every event is handed to.</param>
/// <param name="delay">How late each event is handed on.</param>
/// <param name="time">The clock of the delay.</param>
public sealed class DelayingLiveEventSink(ILiveEventSink inner, TimeSpan delay, TimeProvider time) : ILiveEventSink
{
    private readonly ILiveEventSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ConcurrentQueue<Task> _pending = new();

    /// <summary>How late each event is handed on.</summary>
    public TimeSpan Delay { get; } = delay;

    /// <inheritdoc />
    public void Emit(LiveEvent liveEvent)
    {
        try
        {
            _pending.Enqueue(HandOnAsync(liveEvent));
        }
        catch (Exception)
        {
            // A live sink never throws into the run.
        }
    }

    /// <summary>Completes when every event emitted before the call was handed on.</summary>
    /// <returns>The wait.</returns>
    public Task DrainAsync() => Task.WhenAll([.. _pending]);

    private async Task HandOnAsync(LiveEvent liveEvent)
    {
        await Task.Delay(Delay, _time).ConfigureAwait(false);
        _inner.EmitSafely(liveEvent);
    }
}