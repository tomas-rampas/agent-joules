using AgentJoules.Core.Labels;
using AgentJoules.Core.Quality;
using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-073: parse judge replies into an integer score from 1 to 5 (REQ-010). Scoped to the feature because sentences such as "the reply is parsed" are reused by other stories with other meanings.
/// The state lives in <see cref="JudgeParsingContext"/>; the real <see cref="JudgeScoreParser"/> runs over the reply text, with no HTTP and no judge model. A reply written in a scenario uses
/// the two-character escapes <c>\n</c>, <c>\r</c> and <c>\t</c> for control characters, because a Gherkin line cannot hold a line break.
/// </summary>
[Binding]
[Scope(Feature = "AJ-073 Parse judge replies into an integer score from 1 to 5")]
public sealed class JudgeParsingSteps(JudgeParsingContext context)
{
    private readonly JudgeParsingContext _context = context;

    private JudgeParseResult Result => _context.Result ?? throw new XunitException("The reply was not parsed.");

    private static string Unescape(string text) =>
        text.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\r", "\r", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal);

    private static MetricLabel LabelNamed(string name) => name switch
    {
        "model-judged" => MetricLabel.ModelJudged,
        "N/A" => MetricLabel.NotApplicable,
        _ => throw new XunitException($"Unknown label word {name}."),
    };

    [Given(@"^a judge reply ""(.*)""$")]
    public void GivenAJudgeReply(string reply) => _context.Reply = Unescape(reply);

    [Given(@"^a judge reply of (\d+) characters that is not an integer$")]
    public void GivenALongReplyThatIsNotAnInteger(int length) => _context.Reply = new string('x', length);

    [When(@"^the reply is parsed$")]
    public void WhenTheReplyIsParsed() => _context.Parse();

    [Then(@"^the quality is (\d+) with label ""([^""]*)""$")]
    public void ThenTheQualityIsScored(int score, string label)
    {
        Assert.True(Result.Parsed);
        Assert.Equal(score, Result.Score);
        Assert.Null(Result.Excerpt);
        Assert.Equal((double)score, Result.Quality.Value);
        Assert.Equal(LabelNamed(label), Result.Quality.Label);
        Assert.Null(Result.Quality.Reason);
    }

    [Then(@"^the quality has label ""([^""]*)"" and a null value$")]
    public void ThenTheQualityIsAbsent(string label)
    {
        Assert.False(Result.Parsed);
        Assert.Null(Result.Score);
        Assert.Null(Result.Quality.Value);
        Assert.Equal(LabelNamed(label), Result.Quality.Label);
    }

    [Then(@"^the reason starts with ""([^""]*)""$")]
    public void ThenTheReasonStartsWith(string prefix)
    {
        Assert.NotNull(Result.Quality.Reason);
        Assert.StartsWith(prefix, Result.Quality.Reason, StringComparison.Ordinal);
    }

    [Then(@"^the recorded excerpt has at most (\d+) characters$")]
    public void ThenTheExcerptIsCapped(int limit)
    {
        Assert.NotNull(Result.Excerpt);
        Assert.InRange(Result.Excerpt.Length, 1, limit);
        Assert.EndsWith(Result.Excerpt, Result.Quality.Reason, StringComparison.Ordinal);
    }
}