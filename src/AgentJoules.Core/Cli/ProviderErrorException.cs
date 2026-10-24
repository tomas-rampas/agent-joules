using System.Globalization;

using AgentJoules.Core.Security;

namespace AgentJoules.Core.Cli;

/// <summary>
/// A provider answered a streamed completion with HTTP 200 and then sent an <c>error</c> object instead of a completion chunk (user decision 3 of the M3 exit, AJ-104): the saturated free-tier worker of
/// the live runs sent <c>{"error":{"message":"ResourceExhausted: ...","type":"internal_server_error","code":500}}</c> and <c>[DONE]</c>, and the step was recorded <c>ok</c> with no output. The transport
/// layer throws this exception while the stream is read, so the step ends as <c>error</c>, never <c>ok</c>. Exits with <see cref="Cli.ExitCode.Unreachable"/> (5) like every step failure.
/// </summary>
/// <remarks>
/// The text of a provider can echo the credential, so the constructor takes the raw text together with the <see cref="SecretRedactor"/> and keeps only the masked, cleaned and bounded form: the message and
/// the type are redacted first (a key cut in two by the bound would no longer match), then every control, format, separator and unpaired surrogate character becomes <c>?</c>, then the text is cut.
/// Nothing of the provider reaches this exception unmasked, so the message may be shown in the failure of the step (<see cref="StepFailedException.Detail"/>) and in the warning of the judge.
/// </remarks>
/// <param name="httpStatus">The HTTP status of the response that carried the error (200 for an in-stream error), or null.</param>
/// <param name="errorCode">The <c>error.code</c> as a number, or null when the object had none or it was not a number.</param>
/// <param name="errorType">The <c>error.type</c>, or null.</param>
/// <param name="rawMessage">The <c>error.message</c> as the provider wrote it, or null; masked, cleaned and cut here.</param>
/// <param name="redactor">The redactor of the process; it masks a registered key in the message and the type.</param>
/// <exception cref="ArgumentNullException"><paramref name="redactor"/> is null.</exception>
public sealed class ProviderErrorException(int? httpStatus, int? errorCode, string? errorType, string? rawMessage, SecretRedactor redactor)
    : AgentJoulesException(ExitCode.Unreachable, Clean(rawMessage, MaxMessageLength, redactor ?? throw new ArgumentNullException(nameof(redactor))))
{
    /// <summary>The most characters of the provider message the exception keeps.</summary>
    public const int MaxMessageLength = 200;

    /// <summary>The most characters of the provider error type the exception keeps.</summary>
    public const int MaxTypeLength = 64;

    /// <summary>The most characters <see cref="StepText"/> can have: the prefix, the type, the code and the message, with room for the separators.</summary>
    public const int MaxStepTextLength = 320;

    /// <summary>The text before the type, the code and the message in <see cref="StepText"/>.</summary>
    public const string StepPrefix = "provider-error:";

    /// <summary>The HTTP status of the response that carried the error: 200 for an in-stream error, or null.</summary>
    public int? HttpStatus { get; } = httpStatus;

    /// <summary>The <c>error.code</c> as a number, or null.</summary>
    public int? ErrorCode { get; } = errorCode;

    /// <summary>The <c>error.type</c>, masked, cleaned and cut to <see cref="MaxTypeLength"/> characters, or null.</summary>
    public string? ErrorType { get; } = errorType is null ? null : Clean(errorType, MaxTypeLength, redactor);

    /// <summary>
    /// The line a step failure states: <c>provider-error: &lt;type&gt; &lt;code&gt; &lt;message&gt;</c>; a part the provider did not send is left out.
    /// </summary>
    public string StepText
    {
        get
        {
            List<string> parts = [StepPrefix];
            if (!string.IsNullOrEmpty(ErrorType))
            {
                parts.Add(ErrorType);
            }

            if (ErrorCode is { } code)
            {
                parts.Add(code.ToString(CultureInfo.InvariantCulture));
            }

            if (Message.Length > 0)
            {
                parts.Add(Message);
            }

            return string.Join(' ', parts);
        }
    }

    // Redact first, clean second, cut third; the cut never splits a surrogate pair.
    private static string Clean(string? text, int maxLength, SecretRedactor redactor)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string clean = UnsafeText.Replace(redactor.Redact(text));
        if (clean.Length <= maxLength)
        {
            return clean;
        }

        int length = char.IsHighSurrogate(clean[maxLength - 1]) ? maxLength - 1 : maxLength;
        return clean[..length];
    }
}