using System.Globalization;
using System.Text;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;

namespace AgentJoules.Workflow.Judge;

/// <summary>
/// Loads the judge rubric, the text that defines what a score from 1 to 5 means (REQ-010, design 3.4 "Judge"). The caller resolves <c>Paths:Rubric</c> (through
/// <c>ContentPaths.Resolve</c>, which refuses a rooted path, a parent segment and a link) and gives the full path; this class adds no option and no service registration.
/// </summary>
/// <remarks>
/// <para>
/// The text is returned as it is in the file: not trimmed, and with its line ends unchanged, so the judge prompt embeds the rubric unchanged and a score of 4 is judged
/// against the same words in every run. The only change is that one leading byte order mark is removed. The check that the rubric defines every level runs on the same
/// text and does not change it: a level line is a line that starts with a digit from 1 to 5, a full stop and a space, and each of the five levels must occur at least once, in any order.
/// </para>
/// <para>
/// The text goes into every judge prompt, so it may hold no character that is invisible or that acts on a terminal or a model: after the decoding and the removal of the one leading mark,
/// a control character other than tab, line feed and carriage return, and a character whose category is format (a zero-width space, a right-to-left override, a second byte order
/// mark, a Unicode tag), private use, line separator, paragraph separator or not assigned, is refused (a surrogate cannot occur after the strict decoding). Characters are examined as Unicode scalar values, so a supplementary character is classified as one.
/// </para>
/// <para>
/// Every failure is a <see cref="ConfigurationException"/> (exit code 2) whose message names the file by its name only, with the problem as a fixed phrase. Neither the folder,
/// nor any text of the rubric, nor the text of an I/O failure (which carries the full path) reaches a message, and no inner exception is attached. A file that cannot be read from the
/// start to the end as a regular file is refused as unreadable, because a read of it could block for ever or return nothing: on Windows a named pipe or a device such as the null device.
/// On Linux a FIFO inside the content root blocks at open, before this check (a documented residual; the content root is the repository checkout the operator controls).
/// </para>
/// </remarks>
public static class RubricLoader
{
    private const int MaxFileBytes = 64 * 1024;
    private const int LevelCount = 5;
    private const string UnnamedFile = "the rubric file";

    // Strict: a byte sequence that is not UTF-8 throws instead of becoming a replacement character.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads the rubric at <paramref name="path"/> and checks that it defines the levels 1 to 5.</summary>
    /// <param name="path">The full path of the rubric file, <c>Paths:Rubric</c> resolved by the caller.</param>
    /// <returns>The text of the file, verbatim, without one leading byte order mark.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or white space.</exception>
    /// <exception cref="ConfigurationException">
    /// The file does not exist (the message names the file and <c>Paths:Rubric</c>), cannot be read (including a path the host refuses and a stream that cannot seek), is larger than
    /// 64 KiB, is not valid UTF-8 or holds a control or invisible character, or no line starts with <c>N. </c> for a level N from 1 to 5 (the message names the lowest level that is missing).
    /// </exception>
    public static string Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fileName = Path.GetFileName(path);
        string label = fileName.Length == 0 ? UnnamedFile : TextEcho.ForMessage(fileName);
        string text = Decode(label, ReadBounded(path, label));
        RefuseInvisibleCharacters(label, text);

        bool[] present = LevelsPresent(text);
        for (int level = 1; level <= LevelCount; level++)
        {
            if (!present[level - 1])
            {
                throw new ConfigurationException($"{label}: level {level} is missing");
            }
        }

        return text;
    }

    private static byte[] ReadBounded(string path, string label)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // A pipe can block a read for ever and the null device opens as an empty file; neither is a rubric.
            if (!stream.CanSeek)
            {
                throw new ConfigurationException($"{label} cannot be read ({PathsOptions.RubricKey})");
            }

            // One byte more than the cap: reading it proves the file is larger, and no more of a large file is ever read.
            byte[] buffer = new byte[MaxFileBytes + 1];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            return read > MaxFileBytes ? throw new ConfigurationException($"{label} is larger than 64 KiB") : buffer.AsSpan(0, read).ToArray();
        }
        catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ConfigurationException($"{label} does not exist ({PathsOptions.RubricKey})");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // ArgumentException and NotSupportedException: a path the host refuses, such as one with a NUL character. Not attached as the inner exception: its message can carry the path.
            throw new ConfigurationException($"{label} cannot be read ({PathsOptions.RubricKey})");
        }
    }

    private static string Decode(string label, byte[] bytes)
    {
        ReadOnlySpan<byte> content = bytes;
        if (content.StartsWith(Encoding.UTF8.Preamble))
        {
            content = content[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            return StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new ConfigurationException($"{label} is not valid UTF-8");
        }
    }

    private static void RefuseInvisibleCharacters(string label, string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            bool allowedControl = rune.Value is '\t' or '\n' or '\r';
            if ((category == UnicodeCategory.Control && !allowedControl)
                || category is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned)
            {
                throw new ConfigurationException($"{label} holds a control or invisible character");
            }
        }
    }

    // A level line starts, at the beginning of the text or right after a line feed, with a digit from 1 to 5, a full stop and a space.
    private static bool[] LevelsPresent(string text)
    {
        bool[] present = new bool[LevelCount];
        int start = 0;
        while (start < text.Length)
        {
            if (text.Length - start >= 3 && text[start] is >= '1' and <= '5' && text[start + 1] == '.' && text[start + 2] == ' ')
            {
                present[text[start] - '1'] = true;
            }

            int lineFeed = text.IndexOf('\n', start);
            if (lineFeed < 0)
            {
                break;
            }

            start = lineFeed + 1;
        }

        return present;
    }
}