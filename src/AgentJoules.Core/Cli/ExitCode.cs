namespace AgentJoules.Core.Cli;

/// <summary>The process exit codes of the command line; the numbers are part of the documented contract.</summary>
public enum ExitCode
{
    /// <summary>The command completed.</summary>
    Success = 0,

    /// <summary>A failure no other code describes.</summary>
    Unexpected = 1,

    /// <summary>A command-line argument or a configuration value is invalid.</summary>
    InvalidArguments = 2,

    /// <summary>A required API key is not set.</summary>
    MissingKey = 3,

    /// <summary>The requested model does not exist at the endpoint.</summary>
    ModelNotFound = 4,

    /// <summary>An endpoint or the local runtime is unreachable, retries are exhausted, a workflow step failed, or the run was interrupted by host shutdown.</summary>
    Unreachable = 5,

    /// <summary>A precondition of the measurement is not met, for example a forced counter on a GPU without one.</summary>
    PreconditionFailed = 6,

    /// <summary>A verified result differs from the expected content.</summary>
    ContentDiffers = 7,
}