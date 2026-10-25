using System.Globalization;
using System.Text;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// Reads <c>docs/nim-local-attempt.md</c> (AJ-102, REQ-019, EDGE-010) into a <see cref="NimAttemptDocument"/>. It judges nothing: <see cref="NimAttemptRules"/> does. The reading is line based:
/// <c>## </c> headings (outside code fences) cut the text into sections, and the lines <c>Started:</c>, <c>Ended:</c> and <c>Box exceeded:</c> are read from anywhere in the document outside code fences, the first of each kind.
/// </summary>
public static class NimAttemptDocumentParser
{
    /// <summary>The label of the start line.</summary>
    public const string StartedLabel = "Started:";

    /// <summary>The label of the end line.</summary>
    public const string EndedLabel = "Ended:";

    /// <summary>The label of the line that states why the 90-minute box ran out.</summary>
    public const string BoxExceededLabel = "Box exceeded:";

    /// <summary>The prefix of the decision line, which ends the <c>Decision</c> section.</summary>
    public const string RuntimeLabel = "Local runtime:";

    /// <summary>The name of the section that holds the decision.</summary>
    public const string DecisionHeading = "Decision";

    private const string HeadingPrefix = "## ";
    private const string FenceMarker = "```";

    private static readonly string[] TimeFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.fff'Z'"];

    /// <summary>The full path of the committed <c>docs/nim-local-attempt.md</c>, which may not exist yet.</summary>
    /// <returns>The path.</returns>
    public static string CommittedPath() => Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "nim-local-attempt.md");

    /// <summary>Reads the document.</summary>
    /// <param name="markdown">The text; carriage returns are ignored.</param>
    /// <returns>The reading.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="markdown"/> is null.</exception>
    public static NimAttemptDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        List<string> headings = [];
        Dictionary<string, string> sections = new(StringComparer.Ordinal);
        string? current = null;
        StringBuilder body = new();
        bool inFence = false;
        string? startedText = null;
        string? endedText = null;
        string? boxExceeded = null;

        foreach (string line in lines)
        {
            if (line.TrimStart().StartsWith(FenceMarker, StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            if (!inFence)
            {
                startedText ??= TextAfter(line, StartedLabel);
                endedText ??= TextAfter(line, EndedLabel);
                boxExceeded ??= TextAfter(line, BoxExceededLabel);
            }

            if (!inFence && line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
            {
                Close(sections, current, body);
                current = line[HeadingPrefix.Length..].Trim();
                headings.Add(current);
                _ = body.Clear();
                continue;
            }

            if (current is not null)
            {
                _ = body.Append(line).Append('\n');
            }
        }

        Close(sections, current, body);

        return new NimAttemptDocument(
            markdown,
            lines,
            headings,
            sections,
            ParseTime(startedText),
            startedText,
            ParseTime(endedText),
            endedText,
            boxExceeded,
            RuntimeOf(sections.GetValueOrDefault(DecisionHeading)));
    }

    /// <summary>Reads a time in one of the two forms of the contract.</summary>
    /// <param name="text">The text after the label.</param>
    /// <returns>The time in UTC, or null when the text is null or has another form (a zone offset, no <c>Z</c>, a missing part).</returns>
    public static DateTimeOffset? ParseTime(string? text) =>
        text is not null && DateTimeOffset.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time) ? time : null;

    /// <summary>The value of a decision line.</summary>
    /// <param name="line">One line.</param>
    /// <returns><c>nim</c> or <c>ollama</c> when the trimmed line is exactly <c>Local runtime: nim</c> or <c>Local runtime: ollama</c>; otherwise null.</returns>
    public static string? RuntimeOfLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        string trimmed = line.Trim();
        if (!trimmed.StartsWith(RuntimeLabel + " ", StringComparison.Ordinal))
        {
            return null;
        }

        string value = trimmed[(RuntimeLabel.Length + 1)..];
        return value is "nim" or "ollama" ? value : null;
    }

    private static string? RuntimeOf(string? decision) =>
        decision?.Split('\n').Select(RuntimeOfLine).LastOrDefault(static value => value is not null);

    // The text after a label when the line starts with it; trimmed, and empty when nothing follows.
    private static string? TextAfter(string line, string label) =>
        line.StartsWith(label, StringComparison.Ordinal) ? line[label.Length..].Trim() : null;

    private static void Close(Dictionary<string, string> sections, string? heading, StringBuilder body)
    {
        if (heading is not null)
        {
            _ = sections.TryAdd(heading, body.ToString());
        }
    }
}