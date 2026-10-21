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

/// <summary>
/// A step of the workflow failed (REQ-011): an agent's call ended with an error (a provider status, an unreachable endpoint, retries that ran out). Exits with
/// <see cref="Cli.ExitCode.Unreachable"/> (5).
/// </summary>
/// <remarks>
/// <para>
/// The message holds only the agent name, the type name of the failure and, when the failure had one, its HTTP status; none of the text of the failure travels in
/// <see cref="Exception.Message"/>, because the message of a provider exception carries the response body and a rejected call can echo the credential. The
/// failure itself may be attached as <see cref="Exception.InnerException"/>: the error line of the command line prints the message and the type of the outer
/// exception only, and the chain reaches the log at Debug level only, where the text is masked by the redactor like every other line. The attached exception must never be
/// printed outside Debug-level logging (not in an error line, a results file or a dashboard event): a test pins that the error line shows none of it. This differs from
/// <see cref="RetriesExhaustedException"/>, which wraps nothing.
/// </para>
/// <para>The names are echoed through <see cref="Labels.TextEcho.ForMessage(string, int)"/>, so a name that is long or holds a control character cannot break the error line.</para>
/// </remarks>
public sealed class StepFailedException : AgentJoulesException
{
    /// <summary>Creates the exception for the failure of <paramref name="agent"/> with no attached failure.</summary>
    /// <param name="agent">The name of the agent whose step failed; not blank.</param>
    /// <param name="failureType">The type name of the failure, for example <c>ClientResultException</c>; not blank.</param>
    /// <param name="statusCode">The HTTP status the failure carried, or null.</param>
    /// <exception cref="ArgumentException"><paramref name="agent"/> or <paramref name="failureType"/> is null, empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not an HTTP status (100 to 599).</exception>
    public StepFailedException(string agent, string failureType, int? statusCode = null)
        : base(ExitCode.Unreachable, Describe(agent, failureType, statusCode))
    {
        Agent = agent;
        FailureType = failureType;
        StatusCode = statusCode;
    }

    /// <summary>Creates the exception for the failure of <paramref name="agent"/> and attaches the failure that ended the step.</summary>
    /// <param name="agent">The name of the agent whose step failed; not blank.</param>
    /// <param name="failureType">The type name of the failure, for example <c>ClientResultException</c>; not blank.</param>
    /// <param name="statusCode">The HTTP status the failure carried, or null.</param>
    /// <param name="inner">The failure; kept as <see cref="Exception.InnerException"/> and never copied into the message.</param>
    /// <exception cref="ArgumentException"><paramref name="agent"/> or <paramref name="failureType"/> is null, empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not an HTTP status (100 to 599).</exception>
    public StepFailedException(string agent, string failureType, int? statusCode, Exception inner)
        : base(ExitCode.Unreachable, Describe(agent, failureType, statusCode), inner)
    {
        Agent = agent;
        FailureType = failureType;
        StatusCode = statusCode;
    }

    /// <summary>The name of the agent whose step failed.</summary>
    public string Agent { get; }

    /// <summary>The type name of the failure; never its message.</summary>
    public string FailureType { get; }

    /// <summary>The HTTP status the failure carried, or null when it had none.</summary>
    public int? StatusCode { get; }

    private static string Describe(string agent, string failureType, int? statusCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureType);

        string text = $"Step {Labels.TextEcho.ForMessage(agent)} failed with {Labels.TextEcho.ForMessage(failureType)}";
        if (statusCode is not { } status)
        {
            return text;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(status, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(status, 599);

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{text} and HTTP status {status}");
    }
}

/// <summary>
/// One or more required variables are not set (EDGE-011): the API key of a selected tier, the SSH variables of the <c>cloudgpu</c> tier, and, in a dry run, the key of the
/// judge. Exits with <see cref="Cli.ExitCode.MissingKey"/> (3).
/// </summary>
/// <remarks>
/// The message is formatted once for every missing variable (the formatter lives with the preflight in the workflow assembly) and holds the variable names and the
/// user-secrets alternative only, never a value: this exception exists because a value is absent, and the check reads no value into a message.
/// </remarks>
public sealed class MissingKeyException : AgentJoulesException
{
    /// <summary>Creates the exception for <paramref name="variables"/> with the already formatted <paramref name="message"/>.</summary>
    /// <param name="message">The single message that names every missing variable.</param>
    /// <param name="variables">The names of the missing variables, at least one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="variables"/> is empty.</exception>
    public MissingKeyException(string message, IReadOnlyList<string> variables)
        : base(ExitCode.MissingKey, message)
    {
        ArgumentNullException.ThrowIfNull(variables);
        Variables = variables.Count > 0 ? [.. variables] : throw new ArgumentException("At least one missing variable is needed.", nameof(variables));
    }

    /// <summary>The names of the missing variables, in the order the check found them.</summary>
    public IReadOnlyList<string> Variables { get; }
}