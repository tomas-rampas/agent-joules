using System.Buffers;
using System.Globalization;
using System.Text;

using AgentJoules.Core.Labels;
using AgentJoules.Core.Security;

namespace AgentJoules.Reporting.Render;

/// <summary>
/// The cleaning every report surface applies to a text that comes from a document before it escapes the text for its own syntax (AJ-083, P-18): an <c>nvapi-</c> key is masked as
/// <see cref="SecretRedactor"/> masks it (a run file from elsewhere, or one edited by hand, may carry one), the text is cut as <see cref="TextEcho.ForMessage(string, int)"/> cuts it, and then every
/// control character, format character (for example U+202E or the tag characters of plane 14), line or paragraph separator, lone surrogate and non-character (U+FFFE, U+FFFF and their twins in the other planes)
/// is replaced by <c>?</c>, so nothing a serializer or a parser later refuses reaches the output. The order is mask, cut, replace; the caller escapes afterwards, so a surface cannot put a character inside a key
/// and hide it from a secret scan.
/// </summary>
public static class ReportText
{
    /// <summary>The most characters of a name (a task, an agent, a model ID, a GPU name, a file fact) a report prints before the cut marker.</summary>
    public const int NameMaxLength = 128;

    /// <summary>The most characters of a reason a report prints before the cut marker.</summary>
    public const int ReasonMaxLength = 80;

    // The prefix pattern only: the keys registered with the redactor of a command are not available to a report, which reads files.
    private static readonly SecretRedactor KeyMask = new();

    /// <summary>
    /// Masks <c>nvapi-</c> keys, cuts the text to <paramref name="maxLength"/> characters (appending <c>...</c> when cut) and replaces every control character, format character (BMP or supplementary), line or
    /// paragraph separator, lone surrogate and non-character with <c>?</c>. A valid surrogate pair that is none of these is kept.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="maxLength">The most characters of the masked text to keep, at least 1.</param>
    /// <returns>The cleaned text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is less than 1.</exception>
    public static string Clean(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);

        string echoed = TextEcho.ForMessage(KeyMask.Redact(text), maxLength);
        var clean = new StringBuilder(echoed.Length);
        ReadOnlySpan<char> rest = echoed;
        while (!rest.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(rest, out Rune rune, out int consumed);
            if (status == OperationStatus.Done && !IsUnsafe(rune))
            {
                clean.Append(rune);
            }
            else
            {
                clean.Append('?');
            }

            rest = rest[consumed..];
        }

        return clean.ToString();
    }

    // The same classes TextEcho replaces in the BMP, for every plane, and the non-characters U+FFFE and U+FFFF of every plane.
    private static bool IsUnsafe(Rune rune) =>
        (rune.Value & 0xFFFE) == 0xFFFE
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}