namespace AgentJoules.Core.Cli;

/// <summary>An expected failure that ends the process with a specific <see cref="Cli.ExitCode"/>.</summary>
/// <remarks>The other subclasses of design 3.2 are added by the story that first throws them.</remarks>
public class AgentJoulesException : Exception
{
    /// <summary>Creates an exception that maps to <paramref name="code"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="code"/> is <see cref="Cli.ExitCode.Success"/> or not a defined value.</exception>
    public AgentJoulesException(ExitCode code, string message)
        : base(message)
    {
        ExitCode = Failure(code);
    }

    /// <summary>Creates an exception that maps to <paramref name="code"/> and wraps <paramref name="inner"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="code"/> is <see cref="Cli.ExitCode.Success"/> or not a defined value.</exception>
    public AgentJoulesException(ExitCode code, string message, Exception inner)
        : base(message, inner)
    {
        ExitCode = Failure(code);
    }

    /// <summary>The process exit code this failure maps to; never <see cref="Cli.ExitCode.Success"/> and always a defined value.</summary>
    public ExitCode ExitCode { get; }

    // A failure must never exit 0 and never exit with a number the contract does not define.
    private static ExitCode Failure(ExitCode code) =>
        code != ExitCode.Success && Enum.IsDefined(code) ? code : throw new ArgumentOutOfRangeException(nameof(code));
}

/// <summary>A setting or argument is invalid; exits with <see cref="Cli.ExitCode.InvalidArguments"/>.</summary>
public sealed class ConfigurationException : AgentJoulesException
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public ConfigurationException(string message)
        : base(ExitCode.InvalidArguments, message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> wrapping <paramref name="inner"/>.</summary>
    public ConfigurationException(string message, Exception inner)
        : base(ExitCode.InvalidArguments, message, inner)
    {
    }
}

/// <summary>A precondition of the measurement is not met; exits with <see cref="Cli.ExitCode.PreconditionFailed"/>.</summary>
public sealed class MeasurementPreconditionException : AgentJoulesException
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public MeasurementPreconditionException(string message)
        : base(ExitCode.PreconditionFailed, message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> wrapping <paramref name="inner"/>.</summary>
    public MeasurementPreconditionException(string message, Exception inner)
        : base(ExitCode.PreconditionFailed, message, inner)
    {
    }
}

/// <summary>
/// A call was answered with HTTP 429 on every one of its attempts (EDGE-006); exits with <see cref="Cli.ExitCode.Unreachable"/> (5).
/// </summary>
/// <remarks>
/// The message holds only the attempt count and the status code, and the exception wraps nothing: the response of a rejected call can echo the credential
/// (the message of the provider's own exception carries the response body), so none of its text, headers or request URI may travel in this exception.
/// </remarks>
/// <param name="attempts">The HTTP attempts made, from 1.</param>
/// <param name="statusCode">The status of the last attempt.</param>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="attempts"/> is below 1 or <paramref name="statusCode"/> is not an HTTP status (100 to 599).</exception>
public sealed class RetriesExhaustedException(int attempts, int statusCode) : AgentJoulesException(ExitCode.Unreachable, Describe(attempts, statusCode))
{
    /// <summary>The HTTP attempts the call made.</summary>
    public int Attempts { get; } = attempts;

    /// <summary>The HTTP status of the last attempt.</summary>
    public int StatusCode { get; } = statusCode;

    private static string Describe(int attempts, int statusCode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(statusCode, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(statusCode, 599);

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"HTTP {statusCode} after {attempts} {(attempts == 1 ? "attempt" : "attempts")}");
    }
}