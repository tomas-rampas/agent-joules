using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using AgentJoules.Core.Cli;
using AgentJoules.Reporting.Results;

namespace AgentJoules.Cli.Commands;

/// <summary>
/// The file <c>probe --save</c> writes (AJ-037): <c>results/probe-&lt;yyyyMMddTHHmmssZ&gt;[-&lt;suffix&gt;].json</c> under the content root, holding the
/// bytes <c>probe --json</c> prints. The name never matches the run-file pattern of design 3.6 (<c>&lt;timestamp&gt;-&lt;tier&gt;.json</c>), because it starts
/// with <c>probe-</c>, so <c>validate</c> and <c>report</c> cannot mistake a probe for a run. A file is never overwritten: the bytes go to a hidden temporary
/// file created with <see cref="FileMode.CreateNew"/>, which is then moved onto the final name without overwriting. A create-new open of the final name
/// would follow a dangling file symbolic link on Windows and create its target outside the results folder; the move never overwrites, so a link (or any
/// other entry) at the final name is refused as <c>file exists</c> and no link target is created. The temporary name carries a fresh GUID, so no name
/// exists beforehand that an attacker could plant a link at for the create-new open to follow. A <c>results</c> folder that is itself a junction is
/// followed, which is harmless because nothing can be overwritten.
/// </summary>
internal static partial class ProbeFile
{
    private const string Folder = "results";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const int MaxEchoedSuffixLength = 16;

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    // The second line of defence of Save: whatever reaches it must have the shape of a probe file name.
    [GeneratedRegex(@"^probe-[0-9]{8}T[0-9]{6}Z(-[A-Za-z0-9]{1,20})?\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProbeFileName();

    /// <summary>
    /// Checks the combination of <c>--save</c> and <c>--suffix</c> before any hardware is opened or file is touched.
    /// </summary>
    /// <param name="save">True when <c>--save</c> was given.</param>
    /// <param name="suffix">The <c>--suffix</c> value; null when the option was not given.</param>
    /// <exception cref="ConfigurationException"><c>--suffix</c> without <c>--save</c>, or a suffix that is not 1 to 20 ASCII letters or digits (a path separator, <c>..</c> and a dot all fail the rule); the message echoes at most the first 16 characters of the value.</exception>
    internal static void ValidateOptions(bool save, string? suffix)
    {
        if (suffix is null)
        {
            return;
        }

        if (!save)
        {
            throw new ConfigurationException("--suffix needs --save: it names the file --save writes");
        }

        if (!CalibrationFile.IsValidSuffix(suffix))
        {
            throw new ConfigurationException($"--suffix must match ^[A-Za-z0-9]{{1,20}}$, but it is '{Echo(suffix)}'");
        }
    }

    /// <summary>The file name for <paramref name="utcNow"/> and <paramref name="suffix"/>: <c>probe-20261019T093015Z.json</c> or <c>probe-20261019T093015Z-cloudgpu.json</c>.</summary>
    /// <param name="utcNow">The instant of the save from the host clock; formatted in UTC with the invariant culture.</param>
    /// <param name="suffix">An already validated suffix, or null.</param>
    internal static string Name(DateTimeOffset utcNow, string? suffix) =>
        "probe-" + utcNow.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + (suffix is null ? string.Empty : "-" + suffix) + ".json";

    /// <summary>
    /// Creates the results folder when it is missing and writes <paramref name="json"/> as UTF-8 without a byte order mark to a new file in it.
    /// </summary>
    /// <param name="contentRoot">The content root; <c>results</c> lies directly under it.</param>
    /// <param name="name">The file name from <see cref="Name"/>.</param>
    /// <param name="json">The exact text <c>probe --json</c> prints, trailing line feed included.</param>
    /// <returns>The path for the <c>saved:</c> line: <c>results/&lt;name&gt;</c>, relative with forward slashes.</returns>
    /// <exception cref="InvalidOperationException">
    /// The name is not a probe file name, or the resolved path is not directly inside the results folder (neither message holds a path); the file
    /// already exists, which includes a directory at that name (<c>file exists: results/&lt;name&gt;</c>, the file is left unchanged); or the folder or
    /// file cannot be created or written (<c>cannot write results/&lt;name&gt;: &lt;exception type&gt;</c>). No message holds an absolute path.
    /// </exception>
    internal static string Save(string contentRoot, string name, string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentRoot);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(json);

        if (!ProbeFileName().IsMatch(name))
        {
            throw new InvalidOperationException("the file name is not a probe file name");
        }

        string display = Folder + "/" + name;
        string folder = Path.GetFullPath(Path.Combine(contentRoot, Folder));
        string path = Path.GetFullPath(Path.Combine(folder, name));

        // The suffix rule already excludes separators and dots; this is another line of defence for the path itself.
        if (!string.Equals(Path.GetDirectoryName(path), folder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the save path is not directly inside the results folder");
        }

        // The bytes come first, so only I/O is left inside the cleanup region below.
        byte[] bytes = Utf8WithoutBom.GetBytes(json);
        string temporary = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(name) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        bool moved = false;
        try
        {
            Directory.CreateDirectory(folder);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
            }

            // Not overwriting: an existing file, a directory or a dangling link at the final name makes this throw.
            File.Move(temporary, path, overwrite: false);
            moved = true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Whatever the framework says carries an absolute path, so only the display path and the exception type leave this method. The failure
            // is deliberately not the inner exception: the Debug log of a failed command prints the whole exception chain, and the message of this
            // one holds the absolute path (measured at Debug level).
            throw Path.Exists(path)
                ? new InvalidOperationException("file exists: " + display)
                : new InvalidOperationException($"cannot write {display}: {failure.GetType().Name}");
        }
        finally
        {
            // Only a temporary file this call created is removed, never one that was already there.
            if (created && !moved)
            {
                TryDelete(temporary);
            }
        }

        return display;
    }

    // At most 16 characters of the value, cut before a split surrogate pair, with every control, format and separator character shown as '?'.
    // The text reaches stderr through ErrorLine, which scrubs it again; the cut is what bounds it.
    private static string Echo(string value)
    {
        int cut = value.Length > MaxEchoedSuffixLength && char.IsHighSurrogate(value[MaxEchoedSuffixLength - 1]) ? MaxEchoedSuffixLength - 1 : MaxEchoedSuffixLength;
        string bounded = value.Length > MaxEchoedSuffixLength ? string.Concat(value.AsSpan(0, cut), "...") : value;
        var builder = new StringBuilder(bounded.Length);
        foreach (char character in bounded)
        {
            bool unsafeCharacter = char.IsControl(character)
                || CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            builder.Append(unsafeCharacter ? '?' : character);
        }

        return builder.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The original failure is the one to report.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}