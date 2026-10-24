using System.Globalization;
using System.Text;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// A NIM attempt document built in memory for the tests of AJ-102: it starts as a document that satisfies every rule (the four headings, Started and Ended 62 minutes apart, a login through
/// <c>--password-stdin</c>, a logout and <c>Local runtime: ollama</c> as the last line of <c>Decision</c>) and each test changes the one part it is about. Nothing here is read from or written to a file.
/// </summary>
public sealed class NimAttemptDraft
{
    /// <summary>A login line that satisfies the rule: the key travels through standard input.</summary>
    public const string LoginLine = "printf '%s' \"$NGC_API_KEY\" | docker login nvcr.io --username '$oauthtoken' --password-stdin";

    /// <summary>A logout line that satisfies the rule.</summary>
    public const string LogoutLine = "docker logout nvcr.io";

    /// <summary>The start used by <see cref="Between"/>.</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string FractionTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Creates a draft that satisfies every rule.</summary>
    public NimAttemptDraft() => Between(TimeSpan.FromMinutes(62));

    /// <summary>The <c>## </c> headings in order; a body is written for the first word of a heading (<c>Commands</c>, <c>Image</c>, <c>Outcome</c>, <c>Decision</c>).</summary>
    public List<string> Headings { get; } = ["Commands", "Image", "Outcome", "Decision"];

    /// <summary>The text after <c>Started:</c>, or null for no such line.</summary>
    public string? Started { get; set; }

    /// <summary>The text after <c>Ended:</c>, or null for no such line.</summary>
    public string? Ended { get; set; }

    /// <summary>The text after <c>Box exceeded:</c>, or null for no such line.</summary>
    public string? BoxExceeded { get; set; }

    /// <summary>The lines of the <c>Commands</c> section.</summary>
    public List<string> CommandLines { get; } = [LoginLine, LogoutLine];

    /// <summary>The lines of the <c>Outcome</c> section.</summary>
    public List<string> OutcomeLines { get; } = ["The first models call returned a model id.", "17:40Z memory.used 13449 MiB, memory.total 16384 MiB"];

    /// <summary>The lines of the <c>Decision</c> section.</summary>
    public List<string> DecisionLines { get; } = ["The container served the models call inside the box.", string.Empty, "Local runtime: ollama"];

    /// <summary>Formats a time the way the document does.</summary>
    /// <param name="time">The time.</param>
    /// <param name="fraction">True for the form with milliseconds.</param>
    /// <returns>The text.</returns>
    public static string Format(DateTimeOffset time, bool fraction = false) =>
        time.UtcDateTime.ToString(fraction ? FractionTimeFormat : TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>Sets Started to <see cref="DefaultStart"/> and Ended to the given span after it.</summary>
    /// <param name="span">The elapsed time.</param>
    /// <param name="fraction">True for the form with milliseconds.</param>
    /// <returns>This draft.</returns>
    public NimAttemptDraft Between(TimeSpan span, bool fraction = false)
    {
        Started = Format(DefaultStart, fraction);
        Ended = Format(DefaultStart + span, fraction);
        return this;
    }

    /// <summary>Renders the document with LF line endings and a final line feed.</summary>
    /// <returns>The text.</returns>
    public string Render()
    {
        var text = new StringBuilder();
        _ = text.Append("# NIM-local attempt (REQ-019)\n\n");
        AppendIfSet(text, "Started: ", Started);
        AppendIfSet(text, "Ended: ", Ended);
        AppendIfSet(text, "Box exceeded: ", BoxExceeded);
        foreach (string heading in Headings)
        {
            _ = text.Append("\n## ").Append(heading).Append('\n');
            foreach (string line in BodyOf(heading))
            {
                _ = text.Append(line).Append('\n');
            }
        }

        return text.ToString();
    }

    private static void AppendIfSet(StringBuilder text, string label, string? value)
    {
        if (value is not null)
        {
            _ = text.Append(label).Append(value).Append('\n');
        }
    }

    private List<string> BodyOf(string heading) =>
        heading.Split(' ')[0] switch
        {
            "Commands" => CommandLines,
            "Outcome" => OutcomeLines,
            "Decision" => DecisionLines,
            "Image" => ["The catalog entry, the image path, the tag and the digest."],
            _ => [],
        };
}