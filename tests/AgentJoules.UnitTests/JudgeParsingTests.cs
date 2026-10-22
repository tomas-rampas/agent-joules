using System.Globalization;

using AgentJoules.Core.Labels;
using AgentJoules.Core.Quality;
using AgentJoules.Core.Security;

using Xunit;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-010")]
public sealed class JudgeParsingTests
{
    private const string ReasonPrefix = "parse-error: ";

    private const int FragmentLength = 8;
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static JudgeParseResult Parse(string reply) => JudgeScoreParser.Parse(reply, new SecretRedactor());

    // A deterministic stand-in for a secret, built from a seeded pattern at run time, so no key-shaped text exists in the source.
    private static string FakeSecret(int seed, int length) =>
        string.Create(length, seed, static (span, s) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[((i * i * 7) + (i * 13) + s) % Alphabet.Length];
            }
        });

    private static void AssertNoFragment(string secret, params string?[] texts)
    {
        for (int start = 0; start + FragmentLength <= secret.Length; start++)
        {
            string fragment = secret.Substring(start, FragmentLength);
            foreach (string? text in texts)
            {
                Assert.True(text is null || !text.Contains(fragment, StringComparison.Ordinal), $"A {FragmentLength}-character fragment of the secret at offset {start} reached the output.");
            }
        }
    }

    private static string Chars(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

    private static bool IsUnsafeCharacter(char c) => char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static void AssertParsed(JudgeParseResult result, int expected)
    {
        Assert.True(result.Parsed);
        Assert.Equal(expected, result.Score);
        Assert.Null(result.Excerpt);
        Assert.Equal((double)expected, result.Quality.Value);
        Assert.Equal(MetricLabel.ModelJudged, result.Quality.Label);
        Assert.Null(result.Quality.Reason);
    }

    private static void AssertFailed(JudgeParseResult result, string expectedExcerpt)
    {
        Assert.False(result.Parsed);
        Assert.Null(result.Score);
        Assert.Equal(expectedExcerpt, result.Excerpt);
        Assert.Null(result.Quality.Value);
        Assert.Equal(MetricLabel.NotApplicable, result.Quality.Label);
        Assert.Equal(ReasonPrefix + expectedExcerpt, result.Quality.Reason);
    }

    [Theory]
    [InlineData("4", 4)]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData(" 4\n", 4)]
    [InlineData("\r\n\t 3 \r\n", 3)]
    public void Plain_integer_parses(string reply, int expected) => AssertParsed(Parse(reply), expected);

    [Theory]
    [InlineData("<think>the answer is fine</think>5", 5)]
    [InlineData("<think>1 2 3</think>4", 4)]
    [InlineData("<think>line one\nline two\r\nline three</think>\n\n 2 \n", 2)]
    [InlineData("<THINK>shouting</Think>3", 3)]
    [InlineData("<think></think>1", 1)]
    [InlineData("<think>a</think>4<think>b</think>", 4)]
    [InlineData("<think>a</think>\n<think>b</think>\n 5", 5)]
    [InlineData("4<think>afterthought</think>", 4)]
    public void Think_block_is_stripped(string reply, int expected) => AssertParsed(Parse(reply), expected);

    [Theory]
    [InlineData("4/5", "4/5")]
    [InlineData("6", "6")]
    [InlineData("0", "0")]
    [InlineData("3.5", "3.5")]
    [InlineData("", "(empty)")]
    [InlineData("four", "four")]
    [InlineData("4 because it is good", "4 because it is good")]
    public void Non_integer_reply_is_NA(string reply, string expectedExcerpt) => AssertFailed(Parse(reply), expectedExcerpt);

    [Fact]
    public void Score_is_integer_not_clamped()
    {
        JudgeParseResult scored = Parse("5");
        int? score = scored.Score;

        Assert.Equal(5, score);
        Assert.Equal(5.0, scored.Quality.Value);

        JudgeParseResult tooHigh = Parse("6");

        Assert.Null(tooHigh.Score);
        Assert.Null(tooHigh.Quality.Value);
        Assert.Equal(MetricLabel.NotApplicable, tooHigh.Quality.Label);

        JudgeParseResult tooLow = Parse("0");

        Assert.Null(tooLow.Score);
        Assert.Null(tooLow.Quality.Value);
    }

    [Theory]
    [InlineData("05")]
    [InlineData("+4")]
    [InlineData("-1")]
    [InlineData("4.0")]
    [InlineData("4 5")]
    [InlineData("44")]
    [InlineData("4.")]
    [InlineData("1e0")]
    public void Other_malformed_numbers_are_NA(string reply) => AssertFailed(Parse(reply), reply);

    [Theory]
    [InlineData(0xFF14)]
    [InlineData(0x0664)]
    [InlineData(0x00B3)]
    public void Digits_outside_ASCII_are_NA(int codePoint)
    {
        string reply = Chars(codePoint);

        AssertFailed(Parse(reply), reply);
    }

    [Theory]
    [InlineData("<think>only thinking</think>")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    [InlineData("<think>a</think> <think>b</think>")]
    public void Reply_without_visible_text_is_NA_with_the_empty_excerpt(string reply) => AssertFailed(Parse(reply), "(empty)");

    [Fact]
    public void Excerpt_is_capped_at_60_characters()
    {
        JudgeParseResult result = Parse(new string('x', 200));

        AssertFailed(result, new string('x', 60));
        Assert.Equal(60, JudgeScoreParser.ExcerptMaxLength);
    }

    [Theory]
    [InlineData(59, 59)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    public void Excerpt_cap_boundary(int replyLength, int excerptLength) =>
        AssertFailed(Parse(new string('y', replyLength)), new string('y', excerptLength));

    [Fact]
    public void Excerpt_does_not_split_a_surrogate_pair() =>
        AssertFailed(Parse(new string('a', 59) + Chars(0x1F600) + "tail"), new string('a', 59));

    [Fact]
    public void Excerpt_keeps_a_whole_surrogate_pair_that_fits() =>
        AssertFailed(Parse(new string('a', 58) + Chars(0x1F600) + "tail"), new string('a', 58) + Chars(0x1F600));

    [Fact]
    public void Excerpt_replaces_control_bidi_and_line_characters()
    {
        string reply = "ab" + Chars(0x202E) + "cd" + Chars(0x07) + "ef" + Chars(0x0D, 0x0A) + "gh" + Chars(0x2028, 0x2029) + "ij" + Chars(0x85) + "kl" + Chars(0x1B) + "[31m" + Chars(0x2066) + "mn" + Chars(0x00AD);

        JudgeParseResult result = Parse(reply);

        AssertFailed(result, "ab?cd?ef??gh??ij?kl?[31m?mn?");
        Assert.DoesNotContain(result.Excerpt!, IsUnsafeCharacter);
        Assert.DoesNotContain(result.Quality.Reason!, IsUnsafeCharacter);
    }

    [Fact]
    public void Excerpt_is_trimmed_and_taken_after_the_think_block() =>
        AssertFailed(Parse("<think>hidden reasoning</think>\n\n   The answer is four.  \n"), "The answer is four.");

    [Fact]
    public void Excerpt_counts_the_text_after_the_think_block_not_the_block() =>
        AssertFailed(Parse("<think>" + new string('t', 100) + "</think>" + new string('z', 100)), new string('z', 60));

    [Fact]
    public void Repeated_think_blocks_are_all_removed() =>
        AssertParsed(Parse("<think>one</think><think>two</think><think>three</think>2"), 2);

    [Fact]
    public void Nested_think_blocks_are_not_recovered() =>
        // The block ends at the first closing tag, so the outer closing tag stays behind and the reply is not an integer.
        AssertFailed(Parse("<think>a<think>b</think>c</think>5"), "c</think>5");

    [Fact]
    public void Unterminated_think_block_is_left_in_place_and_fails() =>
        AssertFailed(Parse("<think>still reasoning about it, the answer is 4"), "<think>still reasoning about it, the answer is 4");

    [Fact]
    public void Unterminated_think_block_after_a_closed_one_fails() =>
        AssertFailed(Parse("<think>a</think><think>b 4"), "<think>b 4");

    [Fact]
    public void Stray_closing_tag_fails() =>
        AssertFailed(Parse("</think>4"), "</think>4");

    [Fact]
    public void Null_reply_throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => Parse(null!));

        Assert.Equal("reply", exception.ParamName);
    }

    [Fact]
    public void Null_redactor_throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => JudgeScoreParser.Parse("4", null!));

        Assert.Equal("redactor", exception.ParamName);
    }

    [Theory]
    [InlineData("My key is {0} and the score is 4")]
    [InlineData("<think>my key is {0}</think>4")]
    [InlineData("{0}")]
    [InlineData("Authorization: Bearer in header {0} then the score is 4")]
    [InlineData("pppppppppppppppppppppppppppppppppppppppppppppppppp{0}")]
    [InlineData("My key is {a}<think>x</think>{b} and the score is 4")]
    [InlineData("{a}<think>x</think>{b}")]
    public void Registered_key_never_reaches_the_excerpt_and_does_not_change_the_outcome(string template)
    {
        // {0} is the whole key; {a} and {b} are its two halves, so a template can split the key with a complete think block.
        string key = FakeSecret(7, 80);
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(key));

        JudgeParseResult result = JudgeScoreParser.Parse(
            template.Replace("{0}", key, StringComparison.Ordinal).Replace("{a}", key[..40], StringComparison.Ordinal).Replace("{b}", key[40..], StringComparison.Ordinal),
            redactor);
        JudgeParseResult withoutKey = Parse(template.Replace("{0}", string.Empty, StringComparison.Ordinal).Replace("{a}", string.Empty, StringComparison.Ordinal).Replace("{b}", string.Empty, StringComparison.Ordinal));

        Assert.Equal(withoutKey.Parsed, result.Parsed);
        Assert.Equal(withoutKey.Score, result.Score);
        AssertNoFragment(key, result.Excerpt, result.Quality.Reason);
        if (!result.Parsed)
        {
            Assert.Contains("****", result.Excerpt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nvapi_token_is_masked_in_the_excerpt()
    {
        string body = FakeSecret(11, 48);
        string token = "nvapi-" + body;

        JudgeParseResult inText = JudgeScoreParser.Parse("the judge saw " + token + " and refused", new SecretRedactor());
        JudgeParseResult afterPadding = JudgeScoreParser.Parse(new string('p', 50) + token, new SecretRedactor());

        JudgeParseResult splitByBlock = JudgeScoreParser.Parse("the judge saw nvapi-" + body[..24] + "<think>x</think>" + body[24..] + " and refused", new SecretRedactor());

        AssertFailed(inText, "the judge saw nvapi-**** and refused");
        AssertFailed(afterPadding, new string('p', 50) + "nvapi-****");
        AssertFailed(splitByBlock, "the judge saw nvapi-**** and refused");
        AssertNoFragment(body, inText.Excerpt, inText.Quality.Reason, afterPadding.Excerpt, afterPadding.Quality.Reason, splitByBlock.Excerpt, splitByBlock.Quality.Reason);
    }
}