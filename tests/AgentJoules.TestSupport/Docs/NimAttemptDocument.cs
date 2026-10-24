namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// What <see cref="NimAttemptDocumentParser"/> reads from <c>docs/nim-local-attempt.md</c> (AJ-102, REQ-019, EDGE-010). Nothing here is judged: a missing or malformed part is null or kept as text,
/// and <see cref="NimAttemptRules"/> turns it into a violation.
/// </summary>
/// <param name="Raw">The text as given.</param>
/// <param name="Lines">The lines of the text without line terminators; line number N of a message is <c>Lines[N - 1]</c>.</param>
/// <param name="Headings">The names of the <c>## </c> headings in order (trimmed, compared ordinally); a heading inside a code fence does not count.</param>
/// <param name="Sections">The body text of each heading, up to the next <c>## </c> heading; the first heading of a name wins.</param>
/// <param name="Started">The time of the first <c>Started:</c> line, or null when there is none or it is malformed.</param>
/// <param name="StartedText">The text after <c>Started:</c>, trimmed, or null when there is no such line.</param>
/// <param name="Ended">The time of the first <c>Ended:</c> line, or null when there is none or it is malformed.</param>
/// <param name="EndedText">The text after <c>Ended:</c>, trimmed, or null when there is no such line.</param>
/// <param name="BoxExceededReason">The trimmed text after the first <c>Box exceeded:</c> line (empty when the line has no reason), or null when there is no such line.</param>
/// <param name="DecisionRuntime">The value (<c>nim</c> or <c>ollama</c>) of the last <c>Local runtime:</c> line of the <c>Decision</c> section, or null.</param>
public sealed record NimAttemptDocument(
    string Raw,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> Headings,
    IReadOnlyDictionary<string, string> Sections,
    DateTimeOffset? Started,
    string? StartedText,
    DateTimeOffset? Ended,
    string? EndedText,
    string? BoxExceededReason,
    string? DecisionRuntime);