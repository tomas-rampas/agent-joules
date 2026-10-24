using System.Buffers;
using System.Text;

namespace AgentJoules.Reporting.Render;

/// <summary>
/// The escaping of a text that comes from a document into Markdown (AJ-083, P-18): <see cref="ReportText.Clean(string, int)"/>, then a backslash before every character that Markdown or the table syntax would act
/// on, so a model ID, a task name, an agent name or a reason cannot start an inline link, image, HTML tag, code span or math span, nor split a table row into more cells. The set is inline-only: a text that is put at
/// the start of a line must also have a leading list or heading marker escaped by its caller (<see cref="ReportHeader"/> does). Fixed text and formatted numbers are not escaped.
/// </summary>
public static class MarkdownText
{
    // The backslash and the backtick are in the set: the backslash would cancel the escape of the next character, the backtick would open code. The dollar sign is there because GitHub renders $...$ as math
    // (an amendment of P-18, AJ-083 fix round); \$ is a valid CommonMark escape. The set is inline-only: a marker that makes a whole line a list item or a heading is handled by the caller that puts a text at the start of a line.
    private static readonly SearchValues<char> Special = SearchValues.Create("\\`*_~[]()<>!|&#$");

    /// <summary>
    /// Cleans the text with <see cref="ReportText.Clean(string, int)"/> and puts a backslash before each of <c>\ ` * _ ~ [ ] ( ) &lt; &gt; ! | &amp; # $</c>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="maxLength">The most characters of <paramref name="text"/> to keep before the escaping; <see cref="ReportText.NameMaxLength"/> unless given.</param>
    /// <returns>The escaped text; it holds no line break, no control character and no unescaped pipe.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is less than 1.</exception>
    public static string Escape(string text, int maxLength = ReportText.NameMaxLength)
    {
        string clean = ReportText.Clean(text, maxLength);
        if (clean.AsSpan().IndexOfAny(Special) < 0)
        {
            return clean;
        }

        var escaped = new StringBuilder(clean.Length + 8);
        foreach (char c in clean)
        {
            if (Special.Contains(c))
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }
}