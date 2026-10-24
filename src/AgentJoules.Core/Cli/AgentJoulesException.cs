namespace AgentJoules.Core.Cli;

/// <summary>An expected failure that ends the process with a specific <see cref="Cli.ExitCode"/>.</summary>
/// <remarks>The subclasses of design 3.2 are added by the story that first needs them (<see cref="ContentDiffersException"/> by AJ-058, ahead of the M6 story that throws it).</remarks>
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
/// A call was rejected as overloaded (HTTP 429, 503, 500 with ResourceExhausted, or the same as an error inside an event stream) on every one of its attempts (EDGE-006); exits with <see cref="Cli.ExitCode.Unreachable"/> (5).
/// </summary>
/// <remarks>
/// <para>
/// The message holds only the attempt count and the status code, and the exception wraps nothing: the response of a rejected call can echo the credential
/// (the message of the provider's own exception carries the response body), so none of its text, headers or request URI may travel in this exception.
/// </para>
/// <para>
/// One form states an error inside an event stream (a <see cref="ProviderErrorException"/> on every attempt): the message then reads <c>error code 500 in the event stream after 5 attempts</c> and never calls
/// the code an HTTP status, because the response was a 200. That form keeps the last <see cref="ProviderErrorException"/>, whose text is masked and bounded by construction.
/// </para>
/// </remarks>
public sealed class RetriesExhaustedException : AgentJoulesException
{
    /// <summary>Creates the exception for a call whose last attempt was answered with an HTTP status.</summary>
    /// <param name="attempts">The HTTP attempts made, from 1.</param>
    /// <param name="statusCode">The status of the last attempt.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempts"/> is below 1 or <paramref name="statusCode"/> is not an HTTP status (100 to 599).</exception>
    public RetriesExhaustedException(int attempts, int statusCode)
        : base(ExitCode.Unreachable, Describe(attempts, statusCode))
    {
        Attempts = attempts;
        StatusCode = statusCode;
    }

    /// <summary>Creates the exception for a call whose last attempt ended with an error inside the event stream.</summary>
    /// <param name="attempts">The attempts made, from 1.</param>
    /// <param name="lastError">The error of the last attempt; its <see cref="ProviderErrorException.ErrorCode"/> is the code of the message.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lastError"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempts"/> is below 1 or the error code is not from 100 to 599.</exception>
    public RetriesExhaustedException(int attempts, ProviderErrorException lastError)
        : base(ExitCode.Unreachable, DescribeInStream(attempts, lastError))
    {
        Attempts = attempts;
        StatusCode = lastError.ErrorCode!.Value;
        LastProviderError = lastError;
    }

    /// <summary>The attempts the call made.</summary>
    public int Attempts { get; }

    /// <summary>The HTTP status of the last attempt, or the code of the last error inside the event stream.</summary>
    public int StatusCode { get; }

    /// <summary>The error of the last attempt when it was inside the event stream, or null for an HTTP status.</summary>
    public ProviderErrorException? LastProviderError { get; }

    private static string DescribeInStream(int attempts, ProviderErrorException lastError)
    {
        ArgumentNullException.ThrowIfNull(lastError);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        int code = lastError.ErrorCode ?? throw new ArgumentOutOfRangeException(nameof(lastError), "The error of an exhausted retry carries a code.");
        ArgumentOutOfRangeException.ThrowIfLessThan(code, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(code, 599);

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"error code {code} in the event stream after {attempts} {(attempts == 1 ? "attempt" : "attempts")}");
    }

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
/// The message holds only the agent name, the type name of the failure and, when the failure had one, its HTTP status; except for the line a <see cref="ProviderErrorException"/> states
/// (see below), none of the text of the failure travels in <see cref="Exception.Message"/>, because the message of a provider exception carries the response body and a rejected call can echo the credential. The
/// failure itself may be attached as <see cref="Exception.InnerException"/>: the error line of the command line prints the message and the type of the outer
/// exception only, and the chain reaches the log at Debug level only, where the text is masked by the redactor like every other line. The attached exception must never be
/// printed outside Debug-level logging (not in an error line, a results file or a dashboard event): a test pins that the error line shows none of it. This differs from
/// <see cref="RetriesExhaustedException"/>, which wraps nothing.
/// </para>
/// <para>The names are echoed through <see cref="Labels.TextEcho.ForMessage(string, int)"/>, so a name that is long or holds a control character cannot break the error line.</para>
/// <para>
/// One failure carries text of its own: a <see cref="ProviderErrorException"/> (an in-stream <c>error</c> chunk, AJ-104). Its message is masked and bounded by construction, so the step failure may
/// state it as <see cref="Detail"/>, and the message then reads <c>Step Planner failed with ProviderErrorException; provider-error: internal_server_error 500 ResourceExhausted: ...</c>.
/// </para>
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
    /// <param name="detail">One line of text the message may state after the failure type, or null; only text that is masked and bounded already (the <see cref="ProviderErrorException.StepText"/>), never the message of an arbitrary failure.</param>
    /// <exception cref="ArgumentException"><paramref name="agent"/> or <paramref name="failureType"/> is null, empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not an HTTP status (100 to 599).</exception>
    public StepFailedException(string agent, string failureType, int? statusCode, Exception inner, string? detail = null)
        : base(ExitCode.Unreachable, Describe(agent, failureType, statusCode, detail: detail), inner)
    {
        Agent = agent;
        FailureType = failureType;
        StatusCode = statusCode;
        Detail = detail;
    }

    // The copy made by WithPartialFile: the same failure, and the partial file named in the message.
    private StepFailedException(StepFailedException source, string partialFile)
        : base(ExitCode.Unreachable, Describe(source.Agent, source.FailureType, source.StatusCode, partialFile, source.Detail))
    {
        Agent = source.Agent;
        FailureType = source.FailureType;
        StatusCode = source.StatusCode;
        Detail = source.Detail;
        PartialFile = partialFile;
    }

    private StepFailedException(StepFailedException source, string partialFile, Exception inner)
        : base(ExitCode.Unreachable, Describe(source.Agent, source.FailureType, source.StatusCode, partialFile, source.Detail), inner)
    {
        Agent = source.Agent;
        FailureType = source.FailureType;
        StatusCode = source.StatusCode;
        Detail = source.Detail;
        PartialFile = partialFile;
    }

    /// <summary>The name of the agent whose step failed.</summary>
    public string Agent { get; }

    /// <summary>The type name of the failure; never its message.</summary>
    public string FailureType { get; }

    /// <summary>The HTTP status the failure carried, or null when it had none.</summary>
    public int? StatusCode { get; }

    /// <summary>The line the message states after the failure type (<c>provider-error: &lt;type&gt; &lt;code&gt; &lt;message&gt;</c> for an in-stream error), or null; masked and bounded by its source.</summary>
    public string? Detail { get; }

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

    private static string Describe(string agent, string failureType, int? statusCode, string? partialFile = null, string? detail = null)
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

        if (!string.IsNullOrWhiteSpace(detail))
        {
            text = $"{text}; {Labels.TextEcho.ForMessage(detail, ProviderErrorException.MaxStepTextLength + 100)}";
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
/// <para>
/// A second form (AJ-100, <c>validate --config</c>) carries the problem lines of the configuration check against <c>docs/endpoints.md</c>: its message is those lines joined with line feeds, and it has no tier,
/// no missing-ID list and no available IDs, so <see cref="TierName"/> is empty and nothing is listed after it.
/// </para>
/// </remarks>
public sealed class ModelNotFoundException : AgentJoulesException
{
    // A fix command is a fixed verb and a model ID that is already cut at 64 characters by the caller; the bound only keeps a hostile command from breaking the error line.
    /// <summary>The most characters of one fix command the message states; a longer command is not stated, because a cut command would be a different command.</summary>
    public const int MaxFixCommandLength = 160;

    /// <summary>Creates the exception for a tier whose models list lacks configured IDs.</summary>
    /// <param name="tierName">The lowercase name of the tier whose list lacks the IDs (for the judge model, the judge tier); not blank.</param>
    /// <param name="missingIds">The configured IDs the list lacks, at least one, in the order the check met them.</param>
    /// <param name="availableIds">The IDs to show the user: at most the first 20 of the list, Nemotron and Qwen entries first; empty when the list is empty.</param>
    /// <param name="availableCount">The number of distinct IDs the endpoint listed before the cap; 0 for an empty list, never below the number of <paramref name="availableIds"/>.</param>
    /// <param name="fixCommands">The commands that fix the failure, one per missing ID that has a command in the order of <paramref name="missingIds"/> (for the local tier on Ollama, <c>ollama pull &lt;tag&gt;</c>); the message ends with them after <c>; fix: </c>. Null or empty for a tier that has none.</param>
    /// <param name="handFixIds">The missing IDs that have no command because they are not a valid Ollama name; the message names each (already masked and cut by the caller) and says it must be pulled by hand. Null or empty for none.</param>
    /// <exception cref="ArgumentException"><paramref name="tierName"/> is blank or <paramref name="missingIds"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="missingIds"/> or <paramref name="availableIds"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="availableCount"/> is negative or below the number of <paramref name="availableIds"/>.</exception>
    public ModelNotFoundException(string tierName, IReadOnlyList<string> missingIds, IReadOnlyList<string> availableIds, int availableCount, IReadOnlyList<string>? fixCommands = null, IReadOnlyList<string>? handFixIds = null)
        : base(ExitCode.ModelNotFound, Describe(tierName, missingIds, availableIds, availableCount, fixCommands, handFixIds))
    {
        TierName = tierName;
        MissingIds = [.. missingIds];
        AvailableIds = [.. availableIds];
        AvailableCount = availableCount;
        FixCommands = [.. fixCommands ?? []];
        HandFixIds = [.. handFixIds ?? []];
    }

    /// <summary>Creates the exception for the configuration check of <c>validate --config</c>.</summary>
    /// <param name="problems">The problem lines the check printed, at least one; the message is these lines joined with line feeds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="problems"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="problems"/> is empty.</exception>
    public ModelNotFoundException(IReadOnlyList<string> problems)
        : base(ExitCode.ModelNotFound, Join(problems))
    {
        TierName = string.Empty;
        MissingIds = [];
        AvailableIds = [];
        AvailableCount = 0;
        FixCommands = [];
        HandFixIds = [];
    }

    /// <summary>The lowercase name of the tier whose list lacks the IDs; empty for the configuration check of <c>validate --config</c>.</summary>
    public string TierName { get; }

    /// <summary>The configured IDs the list lacks, at least one, in the order the check met them; empty for the configuration check.</summary>
    public IReadOnlyList<string> MissingIds { get; }

    /// <summary>The IDs to show the user (at most 20, Nemotron and Qwen entries first); empty when the endpoint listed none.</summary>
    public IReadOnlyList<string> AvailableIds { get; }

    /// <summary>The commands that fix the failure, one per missing ID in the order of <see cref="MissingIds"/> (<c>ollama pull &lt;tag&gt;</c> for the local tier on Ollama); empty for a tier that has none.</summary>
    public IReadOnlyList<string> FixCommands { get; }

    /// <summary>The missing IDs that must be fetched by hand because they are not a valid name for the runtime, as the message names them (masked and cut); empty for none.</summary>
    public IReadOnlyList<string> HandFixIds { get; }

    /// <summary>The number of distinct IDs the endpoint listed before the cap; 0 when it listed none.</summary>
    public int AvailableCount { get; }

    private static string Join(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return problems.Count == 0 ? throw new ArgumentException("At least one problem line is needed.", nameof(problems)) : string.Join('\n', problems);
    }

    private static string Describe(string tierName, IReadOnlyList<string> missingIds, IReadOnlyList<string> availableIds, int availableCount, IReadOnlyList<string>? fixCommands, IReadOnlyList<string>? handFixIds)
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
        string message = availableCount == 0 ? $"tier {tier}: no models listed; {missing}" : $"tier {tier}: {missing}";
        List<string> fixes = [.. (fixCommands ?? []).Select(static command => Labels.TextEcho.ForMessage(command, MaxFixCommandLength))];
        fixes.AddRange((handFixIds ?? []).Select(static id => $"{Labels.TextEcho.ForMessage(id)} must be pulled by hand"));
        return fixes.Count > 0 ? $"{message}; fix: {string.Join("; ", fixes)}" : message;
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

/// <summary>
/// A verified result differs from the expected content: the generated block is not the one on disk. Exits with <see cref="Cli.ExitCode.ContentDiffers"/> (7).
/// </summary>
/// <remarks>Thrown by <c>report --verify-readme</c> (M6, AJ-144); nothing in M2 throws it. Like every other message it holds no response text and no key.</remarks>
public sealed class ContentDiffersException : AgentJoulesException
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public ContentDiffersException(string message)
        : base(ExitCode.ContentDiffers, message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> wrapping <paramref name="inner"/>.</summary>
    public ContentDiffersException(string message, Exception inner)
        : base(ExitCode.ContentDiffers, message, inner)
    {
    }
}