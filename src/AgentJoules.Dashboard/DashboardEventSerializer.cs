using System.Buffers;
using System.Text;
using System.Text.Json;

using AgentJoules.Core.Live;

namespace AgentJoules.Dashboard;

/// <summary>
/// Turns a live event into the one-line JSON envelope a server-sent event carries (design 3.10): <c>{"type":...,"atUtc":...,"replay":...,"data":{...}}</c>. The envelope is written with a
/// <see cref="Utf8JsonWriter"/> (no indentation, so an SSE data line is one line), <c>atUtc</c> in ISO 8601 with its offset. There are nine type names, one per event type plus
/// <c>snapshot</c>. AJ-089 writes <c>"data":{}</c>; AJ-090 fills the data through the source-generated context.
/// </summary>
public static class DashboardEventSerializer
{
    /// <summary>The type name of the snapshot payload a connection receives first.</summary>
    internal const string SnapshotType = "snapshot";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>The wire name of the event type.</summary>
    /// <param name="liveEvent">The event.</param>
    /// <returns><c>run-started</c>, <c>baseline</c>, <c>progress</c>, <c>step-started</c>, <c>step-progress</c>, <c>step-completed</c>, <c>power</c> or <c>run-completed</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="liveEvent"/> is null.</exception>
    /// <exception cref="NotSupportedException">The event is of a type the dashboard does not know.</exception>
    public static string TypeName(LiveEvent liveEvent)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);

        return liveEvent switch
        {
            RunStartedEvent => "run-started",
            BaselineEvent => "baseline",
            ProgressEvent => "progress",
            StepStartedEvent => "step-started",
            StepProgressEvent => "step-progress",
            StepCompletedEvent => "step-completed",
            PowerEvent => "power",
            RunCompletedEvent => "run-completed",
            _ => throw new NotSupportedException($"The dashboard has no wire name for the live event type {liveEvent.GetType().Name}."),
        };
    }

    /// <summary>The envelope of <paramref name="liveEvent"/>, as of the state after it was applied.</summary>
    /// <param name="liveEvent">The event.</param>
    /// <param name="state">The dashboard state after the event was applied.</param>
    /// <returns>One line of JSON.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="NotSupportedException">The event is of a type the dashboard does not know.</exception>
    public static string Serialize(LiveEvent liveEvent, DashboardSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return Envelope(TypeName(liveEvent), liveEvent.AtUtc, liveEvent.Replay);
    }

    /// <summary>The snapshot envelope of <paramref name="state"/>: what a client that connects now needs to draw the current state.</summary>
    /// <param name="state">The dashboard state.</param>
    /// <param name="atUtc">The instant of the snapshot.</param>
    /// <returns>One line of JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    public static string SerializeSnapshot(DashboardSnapshot state, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(state);

        return Envelope(SnapshotType, atUtc, state.Replay);
    }

    private static string Envelope(string type, DateTimeOffset atUtc, bool replay)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("atUtc", atUtc);
            writer.WriteBoolean("replay", replay);
            writer.WritePropertyName("data");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}