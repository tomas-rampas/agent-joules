namespace AgentJoules.Core.Live;

/// <summary>
/// Receives the live events of a run (REQ-032, EDGE-019, design 3.10). An implementation never blocks and never throws: the producers are the measured code path (the sampling loop of
/// the meter, the metered client, the orchestrator), and a live view must never delay a measured window or change a result. Producers call
/// <see cref="LiveEventSinkExtensions.EmitSafely"/>, so even a sink that breaks that rule cannot reach a measurement.
/// </summary>
public interface ILiveEventSink
{
    /// <summary>Hands one event to the sink and returns at once; the sink may drop it.</summary>
    /// <param name="liveEvent">The event.</param>
    void Emit(LiveEvent liveEvent);
}

/// <summary>The one way every producer emits a live event.</summary>
public static class LiveEventSinkExtensions
{
    /// <summary>
    /// Calls <see cref="ILiveEventSink.Emit"/> inside a catch-all: whatever the sink throws is dropped here, so a broken sink can never reach a measurement, the sampling loop or the
    /// exit code. A null sink or a null event emits nothing.
    /// </summary>
    /// <param name="sink">The sink.</param>
    /// <param name="liveEvent">The event.</param>
    public static void EmitSafely(this ILiveEventSink sink, LiveEvent liveEvent)
    {
        if (sink is null || liveEvent is null)
        {
            return;
        }

        try
        {
            sink.Emit(liveEvent);
        }
        catch (Exception)
        {
            // Dropped on purpose: the run continues as if no live view existed.
        }
    }
}