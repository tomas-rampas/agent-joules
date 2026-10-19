using Microsoft.Extensions.Logging;

namespace AgentJoules.Cli;

/// <summary>Source-generated log messages of the CLI (CA1848). Every record goes to stderr; the stack trace of a failed command is Debug only.</summary>
internal static partial class Log
{
    /// <summary>The category of every message written by the CLI itself.</summary>
    internal const string Category = "AgentJoules.Cli";

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Command failed")]
    internal static partial void CommandFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "The host failed to stop; the exit code of the command is kept")]
    internal static partial void HostStopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Opening the GPU at index {GpuIndex}")]
    internal static partial void OpeningGpu(ILogger logger, int gpuIndex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "No GPU: {Reason}")]
    internal static partial void GpuUnavailable(ILogger logger, string reason);
}