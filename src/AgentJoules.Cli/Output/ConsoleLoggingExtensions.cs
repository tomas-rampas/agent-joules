using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace AgentJoules.Cli.Output;

/// <summary>Registers the console logger the way the CLI uses it.</summary>
public static class ConsoleLoggingExtensions
{
    /// <summary>
    /// Adds the console logger with <see cref="OneLineConsoleFormatter"/>, writing every level to standard error
    /// (<c>LogToStandardErrorThreshold = Trace</c>), so standard output carries only command output.
    /// </summary>
    public static ILoggingBuilder AddAgentJoulesConsole(this ILoggingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddConsoleFormatter<OneLineConsoleFormatter, ConsoleFormatterOptions>();
        builder.AddConsole(options =>
        {
            options.FormatterName = OneLineConsoleFormatter.FormatterName;
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        return builder;
    }
}