using System.Buffers;
using System.Text;
using System.Text.Json;

using AgentJoules.Core.Live;
using AgentJoules.Core.Security;

namespace AgentJoules.Dashboard;

/// <summary>
/// Turns a live event into the one-line JSON envelope a server-sent event carries (design 3.10): <c>{"type":...,"atUtc":...,"replay":...,"data":{...}}</c>. The envelope is written with a
/// <see cref="Utf8JsonWriter"/> (no indentation, so an SSE data line is one line), <c>atUtc</c> in ISO 8601 with its offset; <c>data</c> is serialized through
/// <see cref="DashboardJsonContext"/>, so every labelled value is the label object of the results file and a value that does not exist is null with its label, never 0. There are nine type
/// names, one per event type plus <c>snapshot</c>.
/// </summary>
/// <remarks>
/// Every <c>data</c> payload passes the redactor of the state before it is written: the registered keys of the host and the <c>nvapi-</c> pattern are masked in every string of the payload, because
/// a <see cref="AgentJoules.Core.Results.StepReading"/> in memory is not redacted (the results file is redacted when it is written). The text of a model output never reaches a payload.
/// </remarks>
public static class DashboardEventSerializer
{
    /// <summary>The type name of the snapshot payload a connection receives first.</summary>
    internal const string SnapshotType = "snapshot";

    // The reverse solidus, the start of every escape sequence of a JSON string.
    private const byte Backslash = 0x5C;

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
        ArgumentNullException.ThrowIfNull(liveEvent);
        ArgumentNullException.ThrowIfNull(state);

        string type = TypeName(liveEvent);
        return Envelope(type, liveEvent.AtUtc, liveEvent.Replay, DataOf(liveEvent, state), state.Redactor);
    }

    /// <summary>The snapshot envelope of <paramref name="state"/>: what a client that connects now needs to draw the current state.</summary>
    /// <param name="state">The dashboard state.</param>
    /// <param name="atUtc">The instant of the snapshot.</param>
    /// <returns>One line of JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    public static string SerializeSnapshot(DashboardSnapshot state, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(state);

        // A snapshot must always be producible, because a client that connects asks for one: a state that cannot be serialized (a value the model refuses, a number the writer refuses) gives a
        // smaller snapshot with the facts of the run and the notice, and as a last resort an empty one. The next full snapshot is sent as soon as the state serializes again.
        try
        {
            return Envelope(SnapshotType, atUtc, state.Replay, JsonSerializer.SerializeToUtf8Bytes(FullSnapshot(state), DashboardJsonContext.Default.SnapshotData), state.Redactor);
        }
        catch (Exception failure)
        {
            // Whatever the state holds that the writer refuses (a number that is not finite, a text that is not valid UTF-16, a value the model refuses), the client gets the smaller snapshot, which says
            // that it is one (the page shows a fixed sentence), and the owner of the state is told once with the type of the failure.
            state.NoteDegraded(failure);
            return MinimalSnapshot(state, atUtc);
        }
    }

    private static SnapshotData FullSnapshot(DashboardSnapshot state) =>
        new(
            state.Run?.Run,
            state.Run?.Environment,
            state.Run?.PowerLimitW,
            state.Baseline,
            state.Progress is { } progress ? ProgressOf(progress) : null,
            state.Completed is { } completed ? new RunCompletedData(completed.Run, completed.Totals) : null,
            state.AgentViews(),
            state.ActiveAgents,
            state.CompletedViews(),
            state.LastWatts,
            state.LastSampleSeconds,
            state.AgentsOmitted);

    private static string MinimalSnapshot(DashboardSnapshot state, DateTimeOffset atUtc)
    {
        var facts = new SnapshotData(state.Run?.Run, state.Run?.Environment, state.Run?.PowerLimitW, null, null, null, [], [], [], null, null, state.AgentsOmitted, SnapshotDegraded: true);
        var empty = new SnapshotData(null, null, null, null, null, null, [], [], [], null, null, state.AgentsOmitted, SnapshotDegraded: true);
        foreach (SnapshotData data in new[] { facts, empty })
        {
            try
            {
                return Envelope(SnapshotType, atUtc, state.Replay, JsonSerializer.SerializeToUtf8Bytes(data, DashboardJsonContext.Default.SnapshotData), state.Redactor);
            }
            catch (Exception)
            {
                // Try the smaller one.
            }
        }

        return Envelope(SnapshotType, atUtc, replay: false, JsonSerializer.SerializeToUtf8Bytes(empty, DashboardJsonContext.Default.SnapshotData), state.Redactor);
    }

    private static byte[] DataOf(LiveEvent liveEvent, DashboardSnapshot state) =>
        liveEvent switch
        {
            RunStartedEvent e => JsonSerializer.SerializeToUtf8Bytes(new RunStartedData(e.Run, e.Environment, e.PowerLimitW), DashboardJsonContext.Default.RunStartedData),
            BaselineEvent e => JsonSerializer.SerializeToUtf8Bytes(new BaselineData(e.Baseline), DashboardJsonContext.Default.BaselineData),
            ProgressEvent e => JsonSerializer.SerializeToUtf8Bytes(ProgressOf(e), DashboardJsonContext.Default.ProgressData),
            StepStartedEvent e => JsonSerializer.SerializeToUtf8Bytes(new StepStartedData(e.Step, e.StartSeconds, state.AgentViews(), state.ActiveAgents, state.AgentsOmitted), DashboardJsonContext.Default.StepStartedData),
            StepProgressEvent e => JsonSerializer.SerializeToUtf8Bytes(new StepProgressData(e.Step, e.OutputChunks, state.AgentViews(), state.ActiveAgents, state.AgentsOmitted), DashboardJsonContext.Default.StepProgressData),
            StepCompletedEvent e => JsonSerializer.SerializeToUtf8Bytes(new StepCompletedData(e.Reading, state.FlagsOf(e.Reading), state.AgentViews(), state.ActiveAgents, state.AgentsOmitted), DashboardJsonContext.Default.StepCompletedData),
            PowerEvent e => JsonSerializer.SerializeToUtf8Bytes(new PowerData(e.Watts, e.SampleSeconds, state.AgentViews(), state.ActiveAgents, state.AgentsOmitted), DashboardJsonContext.Default.PowerData),
            RunCompletedEvent e => JsonSerializer.SerializeToUtf8Bytes(new RunCompletedData(e.Run, e.Totals), DashboardJsonContext.Default.RunCompletedData),
            _ => throw new NotSupportedException($"The dashboard cannot serialize the live event type {liveEvent.GetType().Name}."),
        };

    // A progress event without a task name is not-reported: the member is null and the page says so.
    private static ProgressData ProgressOf(ProgressEvent e) => new(string.IsNullOrWhiteSpace(e.Task) ? null : e.Task, e.TaskIndex, e.TaskCount, e.Repeat, e.RepeatCount);

    private static string Envelope(string type, DateTimeOffset atUtc, bool replay, byte[] data, SecretRedactor redactor)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Max(256, data.Length + 128));
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("atUtc", atUtc);
            writer.WriteBoolean("replay", replay);
            writer.WritePropertyName("data");
            if (IsClean(data, redactor))
            {
                writer.WriteRawValue(data, skipInputValidation: true);
            }
            else
            {
                WriteRedacted(writer, data, redactor);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // The common payload needs no rewriting: it is plain printable ASCII with no escape sequence in it (so no control character, no separator, no surrogate and nothing the serializer had to escape), and
    // the redactor finds nothing to mask in its text. Judged on the whole text, a key that is split by a token boundary is a false alarm at worst, never a miss; the payload then takes the token walk.
    private static bool IsClean(byte[] json, SecretRedactor redactor)
    {
        if (json.AsSpan().IndexOfAnyExceptInRange((byte)' ', (byte)'~') >= 0 || json.AsSpan().IndexOf(Backslash) >= 0)
        {
            return false;
        }

        string text = Encoding.ASCII.GetString(json);
        return string.Equals(redactor.Redact(text), text, StringComparison.Ordinal);
    }

    // A string of a payload as a page may show it: the registered keys and the nvapi- pattern masked, then every control, format and separator character, lone surrogate and non-character replaced
    // (the rule of the report pages), so a text cannot reorder or hide what is around it on the page.
    private static string Shown(string? text, SecretRedactor redactor) => UnsafeText.Replace(redactor.Redact(text));

    // Copies a JSON value token by token with every string (and every member name) passed through the redactor, so a masked value can never break the structure; a number is copied as it was
    // written. The strings are written back with the default encoder, as the context wrote them, so a payload with nothing to mask is unchanged.
    private static void WriteRedacted(Utf8JsonWriter writer, ReadOnlySpan<byte> json, SecretRedactor redactor)
    {
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    writer.WriteStartObject();
                    break;
                case JsonTokenType.EndObject:
                    writer.WriteEndObject();
                    break;
                case JsonTokenType.StartArray:
                    writer.WriteStartArray();
                    break;
                case JsonTokenType.EndArray:
                    writer.WriteEndArray();
                    break;
                case JsonTokenType.PropertyName:
                    writer.WritePropertyName(Shown(reader.GetString(), redactor));
                    break;
                case JsonTokenType.String:
                    writer.WriteStringValue(Shown(reader.GetString(), redactor));
                    break;
                case JsonTokenType.Number:
                    writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                    break;
                case JsonTokenType.True:
                    writer.WriteBooleanValue(true);
                    break;
                case JsonTokenType.False:
                    writer.WriteBooleanValue(false);
                    break;
                case JsonTokenType.Null:
                    writer.WriteNullValue();
                    break;
                default:
                    break;
            }
        }
    }
}