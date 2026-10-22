using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Quality;

/// <summary>
/// The outcome of parsing one judge reply (REQ-010, EDGE-012): either an integer score from 1 to 5 with the quality <see cref="Metric"/> labelled <c>model-judged</c>, or a parse failure with
/// the quality <c>N/A</c> and the reason <c>parse-error: &lt;excerpt&gt;</c>. The instance only comes from <see cref="JudgeScoreParser"/>, so the four members always agree.
/// </summary>
public sealed record JudgeParseResult
{
    private JudgeParseResult(Metric quality, int? score, string? excerpt, bool thinkTagLeft)
    {
        Quality = quality;
        Score = score;
        Excerpt = excerpt;
        ThinkTagLeft = thinkTagLeft;
    }

    /// <summary>The quality metric to put on the step reading: model-judged with the integer value, or N/A with a <c>parse-error:</c> reason.</summary>
    public Metric Quality { get; }

    /// <summary>The integer score from 1 to 5; null exactly when the reply did not parse.</summary>
    public int? Score { get; }

    /// <summary>
    /// The redacted and cleaned start of the reply that did not parse (at most <see cref="JudgeScoreParser.ExcerptMaxLength"/> characters; registered keys and <c>nvapi-</c> tokens masked; control, format, bidi, line and
    /// paragraph characters of the Basic Multilingual Plane replaced by <c>?</c>, while supplementary format characters such as Unicode tag characters pass through <see cref="TextEcho"/> today; <c>(empty)</c>
    /// when nothing was left after the think blocks and the whitespace); null exactly when the reply parsed. It is not raw model text.
    /// </summary>
    public string? Excerpt { get; }

    /// <summary>True when the reply was exactly an integer from 1 to 5.</summary>
    public bool Parsed => Score is not null;

    /// <summary>
    /// True when the reply did not parse and the text left after the complete think blocks were removed still holds a <c>&lt;think&gt;</c> or <c>&lt;/think&gt;</c> tag (compared without regard to case): a block
    /// that was never closed, or a stray closing tag, was left in place. The redactor matches whole registered forms only, so a key split by such a tag is not masked and the excerpt may show part of it; a caller that keeps the reason of the
    /// failure should then use a fixed text instead of the excerpt. False for a reply that parsed.
    /// </summary>
    public bool ThinkTagLeft { get; }

    internal static JudgeParseResult Success(int score) => new(Metric.ModelJudged(score), score, null, false);

    internal static JudgeParseResult Failure(string excerpt, bool thinkTagLeft = false) => new(Metric.NotApplicable(JudgeScoreParser.ReasonPrefix + excerpt), null, excerpt, thinkTagLeft);
}