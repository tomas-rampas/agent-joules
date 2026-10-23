using Microsoft.Extensions.Logging;

namespace AgentJoules.TestSupport;

/// <summary>One record a <see cref="RecordingLogger"/> received.</summary>
/// <param name="Level">The level of the record.</param>
/// <param name="EventId">The event id of the record.</param>
/// <param name="Message">The formatted message.</param>
public sealed record RecordedLogEntry(LogLevel Level, EventId EventId, string Message);

/// <summary>
/// An <see cref="ILogger"/> that keeps every record it receives, at every level, with its event id and its formatted message (P-50). Safe to write from several threads; the
/// exception of a record is not kept, so a test can only assert what the message itself says.
/// </summary>
public sealed class RecordingLogger : ILogger
{
    private readonly Lock _gate = new();
    private readonly List<RecordedLogEntry> _entries = [];

    /// <summary>A copy of the records so far, in the order they arrived.</summary>
    public IReadOnlyList<RecordedLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>The records at <paramref name="level"/>, in order.</summary>
    /// <param name="level">The level.</param>
    /// <returns>A copy.</returns>
    public IReadOnlyList<RecordedLogEntry> At(LogLevel level) => [.. Entries.Where(entry => entry.Level == level)];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        string message = formatter(state, exception);
        lock (_gate)
        {
            _entries.Add(new RecordedLogEntry(logLevel, eventId, message));
        }
    }
}