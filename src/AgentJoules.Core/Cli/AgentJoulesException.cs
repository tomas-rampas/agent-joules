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
/// <para>
/// EDGE-006: when the orchestrator wrote the partial results file of the stopped tier, it rethrows a copy made by <see cref="WithPartialFile"/>, whose message ends with
/// <c>; partial results in &lt;path&gt;</c>, so the last line of the command (the error line) names the file and, through the message, the failing agent. A failed write of the file
/// rethrows the original exception, which makes no claim about a file.
/// </para>
/// </remarks>
public sealed class StepFailedException : AgentJoulesException
{
    /// <summary>The most characters of <see cref="PartialFile"/> the message echoes: the cut the console uses for the <c>results:</c> line, far longer than a path.</summary>
    public const int MaxPartialFileLength = 1024;

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

    // The copy made by WithPartialFile: the same failure, and the partial file named in the message.
    private StepFailedException(StepFailedException source, string partialFile)
        : base(ExitCode.Unreachable, Describe(source.Agent, source.FailureType, source.StatusCode, partialFile))
    {
        Agent = source.Agent;
        FailureType = source.FailureType;
        StatusCode = source.StatusCode;
        PartialFile = partialFile;
    }

    private StepFailedException(StepFailedException source, string partialFile, Exception inner)
        : base(ExitCode.Unreachable, Describe(source.Agent, source.FailureType, source.StatusCode, partialFile), inner)
    {
        Agent = source.Agent;
        FailureType = source.FailureType;
        StatusCode = source.StatusCode;
        PartialFile = partialFile;
    }

    /// <summary>The name of the agent whose step failed.</summary>
    public string Agent { get; }

    /// <summary>The type name of the failure; never its message.</summary>
    public string FailureType { get; }

    /// <summary>The HTTP status the failure carried, or null when it had none.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// The partial results file as the user names it (the <c>--out</c> value as given, a forward slash and the file name), or null when no partial file was written. It is kept whole here;
    /// the message echoes at most <see cref="MaxPartialFileLength"/> characters of it.
    /// </summary>
    /// <remarks>
    /// The value is the <c>--out</c> text of the operator, raw: it can be long or hold control characters. It must be echoed only through
    /// <c>TextEcho.ForMessage(path, MaxPartialFileLength)</c> and never printed as it is.
    /// </remarks>
    public string? PartialFile { get; }

    /// <summary>
    /// A copy of this exception that names the partial results file: the same <see cref="Agent"/>, <see cref="FailureType"/>, <see cref="StatusCode"/>, exit code and
    /// <see cref="Exception.InnerException"/>, and a message that ends with <c>; partial results in &lt;displayPath&gt;</c>. The path is echoed through
    /// <see cref="Labels.TextEcho.ForMessage(string, int)"/>, so a long or control-character path cannot break the error line; nothing else enters the message. This exception is unchanged.
    /// </summary>
    /// <param name="displayPath">The file as the user names it; not blank.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="displayPath"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="displayPath"/> is empty or white space.</exception>
    public StepFailedException WithPartialFile(string displayPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPath);

        return InnerException is { } inner ? new StepFailedException(this, displayPath, inner) : new StepFailedException(this, displayPath);
    }

    private static string Describe(string agent, string failureType, int? statusCode, string? partialFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureType);

        string text = $"Step {Labels.TextEcho.ForMessage(agent)} failed with {Labels.TextEcho.ForMessage(failureType)}";
        if (statusCode is { } status)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(status, 100);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(status, 599);

            text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{text} and HTTP status {status}");
        }

        return partialFile is null ? text : $"{text}; partial results in {Labels.TextEcho.ForMessage(partialFile, MaxPartialFileLength)}";
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

/// <summary>
/// A configured model ID is not listed by the models endpoint of its tier (EDGE-008): <c>GET {BaseUrl}/models</c> answered a list that lacks
/// the <c>ModelId</c> or an <c>AgentModelIds</c> value of a selected tier, or the <c>Judge:ModelId</c> of the judge tier, or answered an empty list. Exits with
/// <see cref="Cli.ExitCode.ModelNotFound"/> (4).
/// </summary>
/// <remarks>
/// <para>
/// The message is one line that names the tier and the missing IDs (for an empty list it also says <c>no models listed</c>). The available IDs do not travel in the message
/// but in <see cref="AvailableIds"/>, because the error line of the command line folds a message into one line; the command prints them under a header before it rethrows.
/// </para>
/// <para>
/// Every ID is provider or configuration text: the creator passes IDs that are already masked by the redactor (a provider can echo a key), and the message cuts and scrubs each ID with
/// <see cref="Labels.TextEcho.ForMessage(string, int)"/>, so an ID that is long or holds a control character cannot break the error line.
/// </para>
/// </remarks>
/// <param name="tierName">The lowercase name of the tier whose list lacks the IDs (for the judge model, the judge tier); not blank.</param>
/// <param name="missingIds">The configured IDs the list lacks, at least one, in the order the check met them.</param>
/// <param name="availableIds">The IDs to show the user: at most the first 20 of the list, Nemotron and Qwen entries first; empty when the list is empty.</param>
/// <param name="availableCount">The number of distinct IDs the endpoint listed before the cap; 0 for an empty list, never below the number of <paramref name="availableIds"/>.</param>
/// <exception cref="ArgumentException"><paramref name="tierName"/> is blank or <paramref name="missingIds"/> is empty.</exception>
/// <exception cref="ArgumentNullException"><paramref name="missingIds"/> or <paramref name="availableIds"/> is null.</exception>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="availableCount"/> is negative or below the number of <paramref name="availableIds"/>.</exception>
public sealed class ModelNotFoundException(string tierName, IReadOnlyList<string> missingIds, IReadOnlyList<string> availableIds, int availableCount)
    : AgentJoulesException(ExitCode.ModelNotFound, Describe(tierName, missingIds, availableIds, availableCount))
{
    /// <summary>The lowercase name of the tier whose list lacks the IDs.</summary>
    public string TierName { get; } = tierName;

    /// <summary>The configured IDs the list lacks, at least one, in the order the check met them.</summary>
    public IReadOnlyList<string> MissingIds { get; } = [.. missingIds];

    /// <summary>The IDs to show the user (at most 20, Nemotron and Qwen entries first); empty when the endpoint listed none.</summary>
    public IReadOnlyList<string> AvailableIds { get; } = [.. availableIds];

    /// <summary>The number of distinct IDs the endpoint listed before the cap; 0 when it listed none.</summary>
    public int AvailableCount { get; } = availableCount;

    private static string Describe(string tierName, IReadOnlyList<string> missingIds, IReadOnlyList<string> availableIds, int availableCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tierName);
        ArgumentNullException.ThrowIfNull(missingIds);
        ArgumentNullException.ThrowIfNull(availableIds);
        ArgumentOutOfRangeException.ThrowIfNegative(availableCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(availableCount, availableIds.Count);
        if (missingIds.Count == 0)
        {
            throw new ArgumentException("At least one missing ID is needed.", nameof(missingIds));
        }

        string tier = Labels.TextEcho.ForMessage(tierName);
        string missing = $"model {(missingIds.Count == 1 ? "ID" : "IDs")} not listed: {string.Join(", ", missingIds.Select(static id => Labels.TextEcho.ForMessage(id)))}";
        return availableCount == 0 ? $"tier {tier}: no models listed; {missing}" : $"tier {tier}: {missing}";
    }
}

/// <summary>
/// An endpoint cannot be used: it refused the connection, did not answer in time, answered a status other than success, or answered something that is not what the command asked for. Exits
/// with <see cref="Cli.ExitCode.Unreachable"/> (5). The model check of the preflight throws it for the models route (EDGE-008); the local runtime check reuses it (M4).
/// </summary>
/// <remarks>
/// The message holds the tier and the route, a status number or a fixed phrase, and never a word of the response: the response of a rejected call can echo the credential. The failure that
/// caused it may be attached as <see cref="Exception.InnerException"/>; like the one of <see cref="StepFailedException"/> it is for Debug-level logging only and is never printed in an error line.
/// </remarks>
public sealed class EndpointUnreachableException : AgentJoulesException
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    /// <param name="message">One line that names the tier and the route; no response text.</param>
    public EndpointUnreachableException(string message)
        : base(ExitCode.Unreachable, message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> wrapping <paramref name="inner"/>.</summary>
    /// <param name="message">One line that names the tier and the route; no response text.</param>
    /// <param name="inner">The failure that made the endpoint unusable; for Debug-level logging only.</param>
    public EndpointUnreachableException(string message, Exception inner)
        : base(ExitCode.Unreachable, message, inner)
    {
    }
}