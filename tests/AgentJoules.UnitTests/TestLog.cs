using Microsoft.Extensions.Logging;

namespace AgentJoules.UnitTests;

/// <summary>Log messages the console-logger tests write (CA1848: source-generated, so no analyzer suppression is needed).</summary>
internal static partial class TestLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "plain record {Value}")]
    internal static partial void Plain(ILogger logger, string value);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "coloured {Value} record")]
    internal static partial void Coloured(ILogger logger, string value);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "failed {Value}")]
    internal static partial void Failed(ILogger logger, Exception exception, string value);
}