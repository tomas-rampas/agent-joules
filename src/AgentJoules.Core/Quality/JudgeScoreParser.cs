using System.Text;

using AgentJoules.Core.Labels;
using AgentJoules.Core.Security;

namespace AgentJoules.Core.Quality;

/// <summary>
/// Turns the text of a judge reply into a quality score (REQ-010, EDGE-012, design 3.2). Every <c>&lt;think&gt;...&lt;/think&gt;</c> block is removed, the whitespace around what is left is trimmed,
/// and the reply is accepted only when that text is exactly one ASCII digit from 1 to 5. Anything else (<c>4/5</c>, <c>6</c>, <c>0</c>, <c>3.5</c>, <c>four</c>, <c>4 because it is good</c>, an
/// empty reply) is a parse failure: the quality is N/A with the reason <c>parse-error: &lt;excerpt&gt;</c>, and a number is never invented, rounded or clamped into range.
/// </summary>
/// <remarks>
/// <para>
/// The think blocks are found with ordinal, case-insensitive searches for the two tags, from left to right, so the work is linear in the length of the reply and no regular expression is involved.
/// A block runs from an opening tag to the first closing tag after it and may span lines; two blocks that follow each other are both removed. A nested block ends at the first closing tag, which
/// leaves the outer closing tag in the text, so the reply does not parse. An opening tag with no closing tag after it is left in place and the reply does not parse either, with an excerpt that
/// starts with <c>&lt;think&gt;</c>: no attempt is made to guess a score out of unfinished reasoning.
/// </para>
/// <para>
/// The excerpt is the start of the text that was left after the blocks and the whitespace were removed, at most <see cref="ExcerptMaxLength"/> UTF-16 code units (one fewer when the cut would split
/// a surrogate pair), with every control, format (bidi override), line and paragraph separator character of the Basic Multilingual Plane replaced by <c>?</c> through <see cref="TextEcho"/>.
/// Supplementary (astral) format characters such as the Unicode tag characters are not replaced, because <see cref="TextEcho"/> judges UTF-16 code units, not runes; rune-based cleaning is a
/// follow-up outside this class. The excerpt lands in the results file and on the console, so it is never the raw reply; <c>(empty)</c> stands for no text at all.
/// </para>
/// <para>
/// The parser removes the think blocks, then redacts the remaining text through the <see cref="SecretRedactor"/> the caller passes, then trims it and checks the digit, so a registered key or an
/// <c>nvapi-</c> token can never reach the excerpt cut. A key inside a block disappears with the block, and a key split by a block is reassembled first and then masked. Redaction cannot change the
/// outcome: a mask is <c>****</c> or <c>nvapi-****</c>, which is non-empty and holds no digit and no whitespace, and a registered form is at least five characters long, so a reply that was exactly one
/// digit from 1 to 5 is untouched and a reply that was not cannot become one. The redactor matches exact registered forms only (whole values, 64-character prefixes, forms ending in <c>...</c>), so a key
/// split by a bare unterminated <c>&lt;think&gt;</c> or altered by one character stays partly visible in the excerpt.
/// </para>
/// </remarks>
public static class JudgeScoreParser
{
    /// <summary>The most characters of a reply that an excerpt keeps.</summary>
    public const int ExcerptMaxLength = 60;

    /// <summary>The text before the excerpt in the reason of a quality that did not parse.</summary>
    internal const string ReasonPrefix = "parse-error: ";

    private const string EmptyExcerpt = "(empty)";
    private const string ThinkOpen = "<think>";
    private const string ThinkClose = "</think>";

    /// <summary>Parses one judge reply.</summary>
    /// <param name="reply">The text the judge model returned.</param>
    /// <param name="redactor">The redactor that holds the registered keys; it masks the text that is left after the think blocks are removed, before the text is trimmed and checked.</param>
    /// <returns>The score and its <c>model-judged</c> quality, or the parse failure with its N/A quality; never throws for non-null arguments.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reply"/> or <paramref name="redactor"/> is null.</exception>
    public static JudgeParseResult Parse(string reply, SecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(redactor);

        string text = redactor.Redact(StripThinkBlocks(reply)).Trim();

        return text.Length == 1 && text[0] is >= '1' and <= '5'
            ? JudgeParseResult.Success(text[0] - '0')
            : JudgeParseResult.Failure(ExcerptOf(text));
    }

    private static string StripThinkBlocks(string reply)
    {
        StringBuilder? kept = null;
        int position = 0;

        while (true)
        {
            int open = reply.IndexOf(ThinkOpen, position, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                break;
            }

            int close = reply.IndexOf(ThinkClose, open + ThinkOpen.Length, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                // Unterminated: the opening tag and everything after it stay in the text.
                break;
            }

            kept ??= new StringBuilder(reply.Length);
            kept.Append(reply, position, open - position);
            position = close + ThinkClose.Length;
        }

        return kept is null ? reply : kept.Append(reply, position, reply.Length - position).ToString();
    }

    private static string ExcerptOf(string text)
    {
        if (text.Length == 0)
        {
            return EmptyExcerpt;
        }

        int length = Math.Min(text.Length, ExcerptMaxLength);

        // Do not leave the first half of a surrogate pair at the end of the excerpt.
        if (length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return TextEcho.ForMessage(text[..length], ExcerptMaxLength);
    }
}