using System.Buffers;
using System.Globalization;
using System.Text;

namespace AgentJoules.Core.Security;

/// <summary>
/// The one rule for a text that comes from a document and is shown to a person (AJ-083, AJ-090): every control character, format character (for example U+202E or the tag characters of plane 14),
/// line or paragraph separator, lone surrogate and non-character (U+FFFE, U+FFFF and their twins in the other planes) is replaced by <c>?</c>, so nothing a serializer, a parser or a display later
/// mishandles reaches the output. The report surfaces and the live payloads share it.
/// </summary>
internal static class UnsafeText
{
    /// <summary>
    /// Replaces every unsafe character of <paramref name="text"/> with <c>?</c>; a valid surrogate pair that is none of these is kept. A text with no unsafe character is scanned without allocating
    /// and is returned as it is, which is nearly every text of a live payload.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with no unsafe character; the same instance when there is none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    internal static string Replace(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int first = FirstUnsafe(text);
        if (first < 0)
        {
            return text;
        }

        var clean = new StringBuilder(text.Length);
        _ = clean.Append(text, 0, first);
        ReadOnlySpan<char> rest = text.AsSpan(first);
        while (!rest.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(rest, out Rune rune, out int consumed);
            _ = status == OperationStatus.Done && !IsUnsafe(rune) ? clean.Append(rune) : clean.Append('?');
            rest = rest[consumed..];
        }

        return clean.ToString();
    }

    // The index of the first character that Replace changes, or -1; plain ASCII is decided without decoding a rune.
    private static int FirstUnsafe(string text)
    {
        int index = 0;
        while (index < text.Length)
        {
            char c = text[index];
            if (c < '\u0080')
            {
                if (c < ' ' || c == '\u007F')
                {
                    return index;
                }

                index++;
                continue;
            }

            OperationStatus status = Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out int consumed);
            if (status != OperationStatus.Done || IsUnsafe(rune))
            {
                return index;
            }

            index += consumed;
        }

        return -1;
    }

    // The same classes TextEcho replaces in the BMP, for every plane, and the non-characters U+FFFE and U+FFFF of every plane.
    private static bool IsUnsafe(Rune rune) =>
        (rune.Value & 0xFFFE) == 0xFFFE
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}