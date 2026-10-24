using System.Globalization;
using System.Text;
using System.Text.Json;

using AgentJoules.Core.Labels;
using AgentJoules.Reporting.Json;
using AgentJoules.Reporting.Schema;

namespace AgentJoules.Reporting.Results;

/// <summary>
/// Checks one run file the way <c>AJ validate</c> does (AJ-080, REQ-015): the text is read as strict UTF-8 (at most <see cref="MaxBytes"/>), parsed, checked against the embedded schema
/// (<see cref="SchemaValidator"/>) and, only when the schema passes, read through the results model, so a file the report reader would reject never counts as valid. The result is the list of failure lines
/// <c>&lt;file&gt;: &lt;location&gt; &lt;message&gt;</c>; an empty list means the file is valid. Everything a line holds that comes from the document or from the exception of a parser is bounded and
/// cleaned with <see cref="TextEcho"/>: the location to 256 characters (the empty pointer is printed as <c>(root)</c>), a schema message to 160, a model message to 256. A file lists at most
/// <see cref="MaxFailureLines"/> failures, then one line <c>&lt;file&gt;: (root) and &lt;N&gt; more failures</c>; no line holds a directory or the text of a syntax error.
/// A schema line names its location as a JSON pointer (<c>/steps/1/cost</c>) and states the rule; a range rule states the number it refused, so a line may hold one number of the document. A model line names its
/// location as the path of the serializer (<c>$.run.startUtc</c>) or <c>(model)</c> for a rule of a constructor, and never holds a value. A check that fails unexpectedly, or cannot be made, is the one line
/// <c>&lt;file&gt;: (root) could not be checked (&lt;exception type&gt;)</c>, so one file never ends the scan of the others.
/// </summary>
public static class RunFileValidator
{
    /// <summary>The most bytes of a run file that are read: 16 MiB. A larger file is one failure line and is not parsed.</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    /// <summary>The most failure lines listed for one file before the line that counts the rest.</summary>
    public const int MaxFailureLines = 20;

    private const int LocationLength = 256;
    private const int SchemaMessageLength = 160;
    private const int ModelMessageLength = 256;
    private const int FileNameLength = 256;
    private const int InitialBuffer = 8192;
    private const string RootLocation = "(root)";
    private const string ModelLocation = "(model)";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Checks the text of a run file.</summary>
    /// <param name="fileName">The name of the file, used in every line; a directory in it is dropped.</param>
    /// <param name="text">The text of the file.</param>
    /// <returns>The failure lines in the order of the checks; empty when the file is valid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> or <paramref name="text"/> is null.</exception>
    public static IReadOnlyList<string> Check(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(text);

        string name = NameOf(fileName);
        return Guarded(name, () => CheckText(name, text));
    }

    private static IReadOnlyList<string> CheckText(string name, string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException failure)
        {
            // The position only: the text of the exception can quote the document.
            return [Line(name, RootLocation, SyntaxMessage(failure))];
        }
        catch (ArgumentException)
        {
            // Text that cannot be transcoded to UTF-8 (a lone surrogate) is not JSON either.
            return [Line(name, RootLocation, "not valid JSON")];
        }

        using (document)
        {
            IReadOnlyList<SchemaFailure> failures = SchemaValidator.Validate(document.RootElement);
            if (failures.Count > 0)
            {
                return Listed(name, failures);
            }
        }

        return ReadThroughModel(name, text);
    }

    /// <summary>Reads a run file (strict UTF-8, at most <see cref="MaxBytes"/> bytes) and checks its text.</summary>
    /// <param name="path">The path of the file; only its name appears in a line.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The failure lines; empty when the file is valid. A file that is larger than <see cref="MaxBytes"/>, is not valid UTF-8 or cannot be read is one line each, and its content is not parsed.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<IReadOnlyList<string>> CheckFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string name = NameOf(path);
        try
        {
            return await ReadAndCheckAsync(path, name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Whatever else goes wrong with one file is that file's failure, never the end of the scan.
            return [CouldNotBeChecked(name, failure)];
        }
    }

    // Runs a check; an exception other than a cancellation becomes the one line "could not be checked (<exception type>)", without the text of the exception.
    internal static IReadOnlyList<string> Guarded(string name, Func<IReadOnlyList<string>> check)
    {
        try
        {
            return check();
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return [CouldNotBeChecked(name, failure)];
        }
    }

    private static string CouldNotBeChecked(string name, Exception failure) => Line(name, RootLocation, "could not be checked (" + failure.GetType().Name + ")");

    private static async Task<IReadOnlyList<string>> ReadAndCheckAsync(string path, string name, CancellationToken cancellationToken)
    {
        byte[] buffer;
        int length;
        try
        {
            (buffer, length) = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            // The message of the exception names the full path, which a line never holds.
            return [Line(name, RootLocation, "could not be read")];
        }

        if (length > MaxBytes)
        {
            return [Line(name, RootLocation, string.Create(CultureInfo.InvariantCulture, $"is larger than {MaxBytes} bytes"))];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(buffer, 0, length);
        }
        catch (DecoderFallbackException)
        {
            return [Line(name, RootLocation, "is not valid UTF-8")];
        }

        return Check(name, text);
    }

    // Reads at most MaxBytes + 1 bytes: a result longer than MaxBytes means the file is over the cap, and the content past the cap is never held.
    private static async Task<(byte[] Buffer, int Length)> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        };
        var stream = new FileStream(path, options);
        await using (stream.ConfigureAwait(false))
        {
            // A pipe or a device is not a run file: it may never end or never answer, so it is refused at once (the caller reports it as a file that could not be read).
            if (!stream.CanSeek)
            {
                throw new IOException("The path is not a regular file.");
            }

            long size = stream.Length;
            if (size > MaxBytes)
            {
                return ([], MaxBytes + 1);
            }

            byte[] buffer = new byte[size > 0 ? (int)size + 1 : InitialBuffer];
            int length = 0;
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (buffer.Length > MaxBytes)
                    {
                        return ([], MaxBytes + 1);
                    }

                    Array.Resize(ref buffer, (int)Math.Min(buffer.Length * 2L, MaxBytes + 1L));
                }

                int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return (buffer, length);
                }

                length += read;
            }
        }
    }

    private static string[] ReadThroughModel(string name, string text)
    {
        try
        {
            return JsonSerializer.Deserialize(text, AgentJoulesJsonContext.Default.RunDocument) is null
                ? [Line(name, RootLocation, "holds no run document")]
                : [];
        }
        catch (JsonException failure)
        {
            // The unknown-member message of the strict context repeats a member name of the document, so the path and the message are bounded like the rest.
            string location = string.IsNullOrEmpty(failure.Path) ? RootLocation : TextEcho.ForMessage(failure.Path, LocationLength);
            return [Line(name, location, TextEcho.ForMessage(failure.Message, ModelMessageLength))];
        }
        catch (ArgumentException failure)
        {
            // A model rule (the pairing of the judge readings, a cost map) raised by a constructor: the exception names the member and the index, never a value.
            return [Line(name, ModelLocation, TextEcho.ForMessage(failure.Message, ModelMessageLength))];
        }
    }

    private static List<string> Listed(string name, IReadOnlyList<SchemaFailure> failures)
    {
        int shown = Math.Min(failures.Count, MaxFailureLines);
        var lines = new List<string>(shown + 1);
        for (int index = 0; index < shown; index++)
        {
            SchemaFailure failure = failures[index];
            string location = failure.InstanceLocation.Length == 0 ? RootLocation : TextEcho.ForMessage(failure.InstanceLocation, LocationLength);
            lines.Add(Line(name, location, TextEcho.ForMessage(failure.Message, SchemaMessageLength)));
        }

        if (failures.Count > shown)
        {
            lines.Add(Line(name, RootLocation, string.Create(CultureInfo.InvariantCulture, $"and {failures.Count - shown} more failures")));
        }

        return lines;
    }

    private static string SyntaxMessage(JsonException failure) =>
        failure is { LineNumber: { } line, BytePositionInLine: { } position }
            ? string.Create(CultureInfo.InvariantCulture, $"not valid JSON at line {line + 1}, byte {position}")
            : "not valid JSON";

    private static string Line(string name, string location, string message) => string.Concat(name, ": ", location, " ", message);

    // The file name only, never a directory, and never more than a bounded clean text.
    private static string NameOf(string path) => TextEcho.ForMessage(Path.GetFileName(path), FileNameLength);
}