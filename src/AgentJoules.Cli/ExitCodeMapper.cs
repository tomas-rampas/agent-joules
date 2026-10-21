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

    /// <summary>
    /// The documented meaning of <paramref name="code"/>. The texts are the documented contract (criterion 1 of AJ-058) and the exit-code table of the README (M6) will quote them; nothing else calls
    /// this in M2. The texts follow the exit-code list of the prompt: <see cref="ExitCode.Unreachable"/> covers an unreachable endpoint or local runtime, exhausted retries, a failed step, and a run
    /// interrupted before completion by host shutdown or a lost connection (or no free loopback port).
    /// </summary>
    /// <param name="code">The exit code.</param>
    /// <returns>The meaning text.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="code"/> is not a defined value.</exception>
    public static string Describe(ExitCode code) => code switch
    {
        ExitCode.Success => "success",
        ExitCode.Unexpected => "unexpected",
        ExitCode.InvalidArguments => "invalid arguments or configuration",
        ExitCode.MissingKey => "missing API key",
        ExitCode.ModelNotFound => "model not found",
        ExitCode.Unreachable => "unreachable, retries exhausted, step failed or run interrupted",
        ExitCode.PreconditionFailed => "measurement precondition failed",
        ExitCode.ContentDiffers => "generated content differs",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a defined exit code."),
    };
}