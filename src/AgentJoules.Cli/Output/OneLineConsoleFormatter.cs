using System.Globalization;
using System.Text;

using AgentJoules.Core.Security;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace AgentJoules.Cli.Output;

/// <summary>
/// The console formatter of the CLI: each log record is one <c>WriteLine</c> call with no colour sequences, so the record reaches the console as
/// one write and the redacting writer behind it sees whole lines. A key is masked first, the message is folded to one line, and every control
/// character other than the line feeds of a stack trace becomes <c>?</c>. Format: <c>&lt;level&gt;: &lt;category&gt;[&lt;event id&gt;] &lt;message&gt;</c>,
/// followed by the exception text (message and stack trace, one line each) when the record carries one.
/// </summary>
/// <param name="redactor">The redactor that masks keys before scrubbing; null leaves masking to the writer behind the console.</param>
public sealed class OneLineConsoleFormatter(SecretRedactor? redactor = null) : ConsoleFormatter(FormatterName)
{
    /// <summary>The name the console logger selects this formatter by.</summary>
    public const string FormatterName = "agentjoules";

    /// <inheritdoc />
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);

        string message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception) ?? string.Empty;
        if (message.Length == 0 && logEntry.Exception is null)
        {
            return;
        }

        var record = new StringBuilder();
        record.Append(Abbreviate(logEntry.LogLevel))
            .Append(": ")
            .Append(ErrorLine.Sanitize(logEntry.Category, redactor))
            .Append('[')
            .Append(logEntry.EventId.Id.ToString(CultureInfo.InvariantCulture))
            .Append("] ")
            .Append(ErrorLine.Sanitize(message, redactor));

        if (logEntry.Exception is { } exception)
        {
            string text = redactor is null ? exception.ToString() : redactor.Redact(exception.ToString());
            record.Append(Environment.NewLine).Append(ErrorLine.ScrubKeepingLines(text).Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        }

        textWriter.WriteLine(record.ToString());
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };
}