using AgentJoules.Core.Cli;

using Microsoft.Extensions.Options;

namespace AgentJoules.Cli;

/// <summary>Maps a failure to the documented process exit code (design 3.6, exit mapping).</summary>
public static class ExitCodeMapper
{
    /// <summary>
    /// <see cref="AgentJoulesException"/> returns its own code; <see cref="OptionsValidationException"/> returns 2;
    /// an <see cref="OperationCanceledException"/> caused by host shutdown (Ctrl+C, SIGTERM, SIGHUP) returns 5 and any other cancellation
    /// returns 1; anything else returns 1.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="shutdownRequested">True when the command token was cancelled by the host or by the process termination signal.</param>
    /// <returns>The process exit code.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public static int Map(Exception exception, bool shutdownRequested)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            AgentJoulesException failure => (int)failure.ExitCode,
            OptionsValidationException => (int)ExitCode.InvalidArguments,
            OperationCanceledException when shutdownRequested => (int)ExitCode.Unreachable,
            _ => (int)ExitCode.Unexpected,
        };
    }
}