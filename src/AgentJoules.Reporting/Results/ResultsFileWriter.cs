using System.Security;
using System.Text;
using System.Text.Json;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Reporting.Json;

namespace AgentJoules.Reporting.Results;

/// <summary>
/// Writes the run file after every (task, repeat) so that a crash or a failed write never leaves a truncated file and never loses finished steps
/// (REQ-017, ADR-17). A checkpoint is serialised through <see cref="AgentJoulesJsonContext"/>, passed through the <see cref="SecretRedactor"/> when one is
/// given (defence in depth: the results model holds no key, but the file is committed; the text must still parse afterwards, or the write fails), written to
/// <c>&lt;name&gt;.json.partial</c> and flushed to disk, and then moved onto <c>&lt;name&gt;.json</c> with <see cref="File.Move(string, string, bool)"/>,
/// which replaces the target in one step on the same volume. A failure at any point leaves the previous complete file as it was and removes the partial
/// file where it can. The file is UTF-8 without a byte order mark, indented with LF line breaks, ends with one LF, and holds numbers in invariant form
/// (the serializer is culture independent).
/// </summary>
/// <remarks>
/// <para>
/// The constructor probes the directory (it is created when missing; a probe file is created and deleted), so an output directory that cannot be written
/// ends the command with exit code 1 before the first call to a provider. The instance is for one run file and one caller at a time: a call that starts
/// while another is in progress is refused with <see cref="InvalidOperationException"/>, because both would use the same partial file.
/// </para>
/// <para>
/// The first checkpoint of an instance never replaces a complete file that already exists under the name (two runs of one tier that start in the same
/// second share a name): it fails with exit code 1 naming the file. Every later checkpoint of the same instance replaces its own file. A stale partial
/// file is deleted first and the new one is created exclusively, so a link planted at the partial path is never written through.
/// </para>
/// </remarks>
public sealed class ResultsFileWriter : IRunCheckpointWriter
{
    private const string PartialSuffix = ".partial";
    private const int MaxDirectoryEchoLength = 400;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly SecretRedactor? _redactor;
    private int _writing;
    private bool _fileWritten;

    /// <summary>Creates the writer of one run file and probes that its directory can be written.</summary>
    /// <param name="directory">The output directory; created when it does not exist.</param>
    /// <param name="fileName">The run file name, <see cref="RunFileName.IsValid(string?)"/>.</param>
    /// <param name="redactor">Masks keys in the serialised text before it is written, or null for no masking.</param>
    /// <param name="displayDirectory">
    /// How the failure line names the directory, for example the <c>--out</c> value as the user gave it; null names the absolute path. The <c>run</c> command passes its
    /// <c>--out</c> value, so the line never carries the absolute path of a working directory.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is empty or <paramref name="fileName"/> is not a run file name.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> or <paramref name="fileName"/> is null.</exception>
    /// <exception cref="AgentJoulesException">The directory cannot be created or written (exit code 1); the one-line message names the directory.</exception>
    public ResultsFileWriter(string directory, string fileName, SecretRedactor? redactor = null, string? displayDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(fileName);
        if (!RunFileName.IsValid(fileName))
        {
            throw new ArgumentException("The file name is not a run file name.", nameof(fileName));
        }

        _redactor = redactor;
        string fullDirectory = ProbeWritable(directory, redactor, displayDirectory);
        FilePath = Path.Combine(fullDirectory, fileName);
    }

    /// <inheritdoc />
    public string FilePath { get; }

    /// <summary>
    /// A test seam, visible to the unit and specification tests only: called with the path of the partial file after it was written, flushed and closed and
    /// before it is moved. An exception from it is a failed write, so a test can fail a checkpoint at the moment the story names. Null in production.
    /// </summary>
    internal Action<string>? BeforeMove { get; init; }

    /// <summary>
    /// Refuses a complete file that already exists under the name (two runs of one tier that start in the same second share a name), with the message of the first write; the <c>run</c>
    /// command calls it when it builds its writers, so the collision ends the command before any completion call instead of after the first (task, repeat).
    /// </summary>
    /// <exception cref="AgentJoulesException">A file exists under the name (exit code 1): <c>The run file &lt;name&gt; exists already and is not replaced; wait a second and run again.</c></exception>
    public void ThrowIfFileExists()
    {
        if (File.Exists(FilePath))
        {
            throw new AgentJoulesException(
                ExitCode.Unexpected,
                $"The run file {Path.GetFileName(FilePath)} exists already and is not replaced; wait a second and run again.");
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Another write of this instance is in progress.</exception>
    /// <exception cref="AgentJoulesException">The first write found a complete file under the name, or masking left the text invalid JSON (exit code 1).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the previous file is intact.</exception>
    public async Task WriteAsync(RunDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The run file is being written already; checkpoints must not overlap.");
        }

        try
        {
            await WriteCheckpointAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }
    }

    private async Task WriteCheckpointAsync(RunDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_fileWritten)
        {
            ThrowIfFileExists();
        }

        string text = RedactedText(JsonSerializer.Serialize(document, AgentJoulesJsonContext.Default.RunDocument));
        byte[] bytes = Utf8.GetBytes(text + "\n");
        string partial = FilePath + PartialSuffix;

        try
        {
            // A stale partial file (a crashed run) is removed by name, so a link planted there is dropped and never written through.
            File.Delete(partial);

            var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

                // Flush to the device before the move, so a power loss after the move cannot leave a complete name over unwritten data.
                stream.Flush(flushToDisk: true);
            }

            BeforeMove?.Invoke(partial);
            cancellationToken.ThrowIfCancellationRequested();

            // One MoveFileEx replace on the same volume: the file is the old one or the new one, never a mixture. The first checkpoint replaces nothing.
            File.Move(partial, FilePath, overwrite: _fileWritten);
            _fileWritten = true;
        }
        catch
        {
            RemoveQuietly(partial);
            throw;
        }
    }

    // The masked text, which must still be JSON: a registered form such as `true,` would otherwise install a broken file as the complete one.
    private string RedactedText(string text)
    {
        if (_redactor is null)
        {
            return text;
        }

        string redacted = _redactor.Redact(text);
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
            throw new AgentJoulesException(ExitCode.Unexpected, "Masking the run file text left it invalid JSON; the previous file was kept.");
        }

        return redacted;
    }

    // Creates the directory, then a probe file and deletes it; returns the full path of the directory.
    private static string ProbeWritable(string directory, SecretRedactor? redactor, string? displayDirectory)
    {
        string full = directory;
        try
        {
            full = Path.GetFullPath(directory);
            _ = Directory.CreateDirectory(full);

            string probe = Path.Combine(full, ".aj-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1);
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                RemoveQuietly(probe);
            }

            return full;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException)
        {
            // The exception text is not used: it can echo a path or provider text. The type name says why, the directory says where. The path is masked
            // before it is cut, because a key that straddles the cut would otherwise be left unmasked in part.
            string shown = displayDirectory ?? full;
            string echoed = TextEcho.ForMessage(redactor is null ? shown : redactor.Redact(shown), MaxDirectoryEchoLength);
            throw new AgentJoulesException(ExitCode.Unexpected, $"The output directory \"{echoed}\" cannot be written ({failure.GetType().Name}).", failure);
        }
    }

    private static void RemoveQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // The file stays; the next checkpoint removes a partial file first, and the complete file was not touched.
        }
    }
}