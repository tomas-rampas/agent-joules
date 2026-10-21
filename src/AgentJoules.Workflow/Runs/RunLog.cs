using Microsoft.Extensions.Logging;

namespace AgentJoules.Workflow.Runs;

/// <summary>Source-generated log messages of the run orchestration (CA1848). Every message names an exception type only, never its message: a provider message can echo a key.</summary>
internal static partial class RunLog
{
    [LoggerMessage(EventId = 101, Level = LogLevel.Debug, Message = "The run listener failed ({ExceptionType}); the reading was kept and the run continues")]
    internal static partial void ListenerFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 102, Level = LogLevel.Debug, Message = "The incomplete checkpoint of the shutdown could not be written ({ExceptionType}); the last complete checkpoint was kept")]
    internal static partial void ShutdownCheckpointFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 103, Level = LogLevel.Debug, Message = "The partial file of the failed step could not be written ({ExceptionType}); the last complete checkpoint, if any, was kept")]
    internal static partial void StepFailureCheckpointFailed(ILogger logger, string exceptionType);
}