using System.Globalization;
using System.Text;

using AgentJoules.Core.Security;

namespace AgentJoules.Cli.Output;

/// <summary>
/// Formats the one line a failure prints on stderr: <c>error: &lt;message&gt; (&lt;exception type&gt;)</c>, never more than one line and never with a
/// control character: a key is masked first (a registered value may itself hold a control character, which scrubbing would otherwise break),
/// every run of line terminators and tabs is then folded into one space, and every remaining control, format, line-separator and paragraph-separator
/// character becomes <c>?</c>, so an escape sequence in a message cannot reach the terminal.
/// </summary>
public static class ErrorLine
{
    /// <summary>The line for <paramref name="exception"/>.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="redactor">Masks keys before the text is folded; null leaves masking to the writer in front of stderr.</param>
    public static string Format(Exception exception, SecretRedactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Format(exception.Message, exception.GetType().Name, redactor);
    }

    /// <summary>The line <c>error: &lt;message&gt; (&lt;type&gt;)</c> for an already known message and type name.</summary>
    public static string Format(string? message, string typeName, SecretRedactor? redactor = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        return "error: " + Sanitize(message, redactor) + " (" + typeName + ")";
    }

    /// <summary>Writes <see cref="Format(Exception, SecretRedactor?)"/> to <paramref name="writer"/> with exactly one <c>WriteLine</c> call.</summary>
    public static void Write(TextWriter writer, Exception exception, SecretRedactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(Format(exception, redactor));
    }

    /// <summary>
    /// Masks keys, folds every run of line terminators and tabs into one space, trims, and replaces every other control character with <c>?</c>. A tab is folded rather
    /// than replaced because System.CommandLine lays out its parse errors with them (<c>Must be one of:</c> followed by a tab before each value).
    /// </summary>
    public static string Sanitize(string? text, SecretRedactor? redactor = null) =>
        Scrub(Fold(redactor is null ? text : redactor.Redact(text), foldTabs: true));

    /// <summary>Replaces every run of line terminators (CR, LF, VT, FF, U+0085, U+2028, U+2029) with one space and trims the ends.</summary>
    public static string Fold(string? text) => Fold(text, foldTabs: false);

    private static string Fold(string? text, bool foldTabs)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        bool inRun = false;
        foreach (char character in text)
        {
            if (IsTerminator(character) || (foldTabs && character == '\t'))
            {
                if (!inRun)
                {
                    builder.Append(' ');
                    inRun = true;
                }
            }
            else
            {
                builder.Append(character);
                inRun = false;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>Replaces every control (Cc), format (Cf), line-separator (Zl) and paragraph-separator (Zp) character with <c>?</c>; line feeds are replaced too, so fold first.</summary>
    public static string Scrub(string? text) => Scrub(text, keepLineFeed: false);

    /// <summary>
    /// As <see cref="Scrub(string?)"/> but keeps the line feed, for the text of a stack trace whose lines stay separate; a carriage return is
    /// dropped when it precedes a line feed and replaced otherwise.
    /// </summary>
    internal static string ScrubKeepingLines(string? text) => Scrub(text?.Replace("\r\n", "\n", StringComparison.Ordinal), keepLineFeed: true);

    private static string Scrub(string? text, bool keepLineFeed)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            bool control = category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            if (control && !(keepLineFeed && rune.Value == '\n'))
            {
                builder.Append('?');
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    private static bool IsTerminator(char character) =>
        character is '\r' or '\n' or '\v' or '\f' or (char)0x0085 or (char)0x2028 or (char)0x2029;
}