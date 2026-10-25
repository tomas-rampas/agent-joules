using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Results;
using AgentJoules.Core.Security;
using AgentJoules.Reporting.Json;

namespace AgentJoules.Reporting.Results;

/// <summary>
/// The name and the writer of <c>results/calibration-&lt;yyyyMMddTHHmmssZ&gt;.json</c> (REQ-021, AJ-109). The name carries the start of the command, in UTC, so a name never matches
/// <see cref="RunFileName.Pattern"/>: the run-file selector, the report and <c>validate</c> never open a calibration file. The file is written once, whole, after the window: to a partial file first,
/// flushed to the device, then moved onto its name without replacing anything, so a name that exists is refused (exit code 1) both before the measurement starts (<see cref="Prepare"/>) and when the file is installed
/// (<see cref="Write"/>).
/// </summary>
public static partial class CalibrationFile
{
    /// <summary>The pattern of a calibration file name.</summary>
    public const string Pattern = "^calibration-\\d{8}T\\d{6}Z\\.json$";

    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const string PartialSuffix = ".partial";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The name of the calibration file of a command that started at <paramref name="startUtc"/>.</summary>
    /// <param name="startUtc">The start of the command; converted to UTC.</param>
    /// <returns>For example <c>calibration-20261021T100000Z.json</c>.</returns>
    public static string Name(DateTimeOffset startUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"calibration-{startUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}.json");

    /// <summary>True when <paramref name="name"/> is exactly a calibration file name: nothing before it, nothing after it (a final line feed included).</summary>
    /// <param name="name">The file name, without a folder.</param>
    /// <returns>Whether it matches <see cref="Pattern"/>.</returns>
    public static bool IsValid(string? name)
    {
        if (name is null)
        {
            return false;
        }

        // `$` also matches before a final line feed, so the match must span the whole text.
        foreach (ValueMatch match in NamePattern().EnumerateMatches(name))
        {
            return match.Index == 0 && match.Length == name.Length;
        }

        return false;
    }

    /// <summary>
    /// Creates the folder when it is missing, proves that it is writable and that <paramref name="name"/> does not exist in it, and returns the full path of the file. Called before the measurement, so a folder that cannot
    /// be written or a name that is taken ends the command before any request, not after a window.
    /// </summary>
    /// <param name="directory">The folder, relative to the working directory or absolute.</param>
    /// <param name="name">A calibration file name (<see cref="Name"/>).</param>
    /// <param name="redactor">Masks key values in the text of a failure; none when null.</param>
    /// <param name="displayDirectory">The folder as the user gave it, for the failure line; <paramref name="directory"/> when null.</param>
    /// <returns>The full path of the file, which does not exist yet.</returns>
    /// <exception cref="ArgumentException">The name is not a calibration file name, or the folder is blank.</exception>
    /// <exception cref="AgentJoulesException">The folder cannot be written, or the file exists already (exit code 1).</exception>
    public static string Prepare(string directory, string name, SecretRedactor? redactor = null, string? displayDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        RequireName(name);

        string path = Path.Combine(ResultsFileWriter.ProbeWritable(directory, redactor, displayDirectory), name);
        ThrowIfExists(path);
        return path;
    }

    /// <summary>
    /// Writes <paramref name="document"/> to <paramref name="path"/> as UTF-8 without a byte order mark, with LF line endings and one final LF, and never replaces a file. A stale partial file is removed by name first, so a link
    /// planted there is dropped and never written through; the partial file is removed again when anything fails.
    /// </summary>
    /// <param name="path">The full path from <see cref="Prepare"/>; its file name must be a calibration file name.</param>
    /// <param name="document">The document.</param>
    /// <param name="redactor">Masks key values in the text; when masking changes it, it must still be JSON.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The file name is not a calibration file name.</exception>
    /// <exception cref="AgentJoulesException">The file exists, or masking left invalid JSON, or the file cannot be written (exit code 1).</exception>
    public static void Write(string path, CalibrationDocument document, SecretRedactor? redactor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        RequireName(Path.GetFileName(path));

        string text = Redacted(JsonSerializer.Serialize(document, AgentJoulesJsonContext.Default.CalibrationDocument), redactor);
        byte[] bytes = Utf8.GetBytes(text + "\n");
        string partial = path + PartialSuffix;
        try
        {
            File.Delete(partial);
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096))
            {
                stream.Write(bytes);

                // Flushed to the device before the move, so a power loss after the move cannot leave a complete name over unwritten data.
                stream.Flush(flushToDisk: true);
            }

            File.Move(partial, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            ResultsFileWriter.RemoveQuietly(partial);
            throw Exists(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // The text of the failure can quote the absolute path; the type says why, the name says which file.
            ResultsFileWriter.RemoveQuietly(partial);
            throw new AgentJoulesException(ExitCode.Unexpected, $"The calibration file {Path.GetFileName(path)} cannot be written ({failure.GetType().Name}).");
        }
        catch
        {
            ResultsFileWriter.RemoveQuietly(partial);
            throw;
        }
    }

    private static void RequireName(string? name)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException("The file name is not a calibration file name.", nameof(name));
        }
    }

    private static void ThrowIfExists(string path)
    {
        if (File.Exists(path))
        {
            throw Exists(path);
        }
    }

    private static AgentJoulesException Exists(string path) =>
        new(ExitCode.Unexpected, $"The calibration file {Path.GetFileName(path)} exists already and is not replaced; wait a second and run again.");

    // The masked text, which must still be JSON: a registered form such as `true,` would otherwise install a broken file.
    private static string Redacted(string text, SecretRedactor? redactor)
    {
        if (redactor is null)
        {
            return text;
        }

        string redacted = redactor.Redact(text);
        if (string.Equals(redacted, text, StringComparison.Ordinal))
        {
            return text;
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(redacted);
        }
        catch (JsonException)
        {
            // The parser's message can quote the text, so it is not used.
            throw new AgentJoulesException(ExitCode.Unexpected, "Masking the calibration file text left it invalid JSON; nothing was written.");
        }

        return redacted;
    }

    // ECMAScript makes \d mean 0-9 only; without it .NET matches the digits of every script.
    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant | RegexOptions.ECMAScript)]
    private static partial Regex NamePattern();
}