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