using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Quality;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Reporting.Json;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Runs;

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

    // ---- AJ-074: derived metrics (joules and cost per quality point) ----

    private static void AssertNotApplicable(Metric metric, string reason)
    {
        Assert.Equal(MetricLabel.NotApplicable, metric.Label);
        Assert.Null(metric.Value);
        Assert.Equal(reason, metric.Reason);
    }

    private static void AssertJudged(Metric metric, double value, string? currency = null)
    {
        Assert.Equal(MetricLabel.ModelJudged, metric.Label);
        Assert.Equal(value, metric.Value);
        Assert.Null(metric.Reason);
        Assert.Null(metric.Unit);
        Assert.Equal(currency, metric.Currency);
    }

    [Fact]
    public void Joules_per_point_is_net_over_quality()
    {
        Metric result = DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(120, "J"), Metric.ModelJudged(4));

        AssertJudged(result, 30.0);
        Assert.Equal("""{"value":30,"label":"model-judged"}""", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Joules_per_point_uses_an_estimated_net_energy_too() =>
        AssertJudged(DerivedMetrics.JoulesPerQualityPoint(Metric.EstimatedQuantity(90, "J"), Metric.ModelJudged(3)), 30.0);

    [Fact]
    public void Cost_per_point_keeps_the_currency_of_the_cost()
    {
        Metric estimated = DerivedMetrics.CostPerQualityPoint(Metric.Estimated(0.08, "EUR"), Metric.ModelJudged(4));
        Metric measured = DerivedMetrics.CostPerQualityPoint(Metric.MeasuredCost(0.5, "USD"), Metric.ModelJudged(5));

        AssertJudged(estimated, 0.02, "EUR");
        AssertJudged(measured, 0.1, "USD");
        Assert.Equal("""{"value":0.02,"label":"model-judged","currency":"EUR"}""", JsonSerializer.Serialize(estimated));
        Assert.Equal(estimated, JsonSerializer.Deserialize<Metric>(JsonSerializer.Serialize(estimated)));
    }

    [Fact]
    public void A_measured_zero_gives_a_judged_zero_not_a_missing_value()
    {
        AssertJudged(DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(0, "J"), Metric.ModelJudged(4)), 0.0);
        AssertJudged(DerivedMetrics.CostPerQualityPoint(Metric.Estimated(0, "EUR"), Metric.ModelJudged(4)), 0.0, "EUR");
    }

    [Fact]
    public void A_hosted_step_has_no_joules_per_point_but_keeps_its_cost_per_point()
    {
        Metric energy = Metric.NotApplicable("hosted-tier");

        AssertNotApplicable(DerivedMetrics.JoulesPerQualityPoint(energy, Metric.ModelJudged(4)), "missing-input");
        AssertJudged(DerivedMetrics.CostPerQualityPoint(Metric.Estimated(0.08, "USD"), Metric.ModelJudged(4)), 0.02, "USD");
    }

    [Fact]
    public void Energy_that_was_not_reported_gives_missing_input()
    {
        AssertNotApplicable(DerivedMetrics.JoulesPerQualityPoint(Metric.NotReported("meter-lost"), Metric.ModelJudged(4)), DerivedMetrics.MissingInputReason);
        Assert.Equal("missing-input", DerivedMetrics.MissingInputReason);
    }

    [Theory]
    [InlineData(false, "not-scored")]
    [InlineData(false, "step-not-ok")]
    [InlineData(false, "empty-output")]
    [InlineData(false, ReasonPrefix + "four")]
    [InlineData(true, "judge-omitted")]
    public void A_quality_that_is_absent_gives_both_derived_metrics_missing_input(bool notReported, string reason)
    {
        Metric quality = notReported ? Metric.NotReported(reason) : Metric.NotApplicable(reason);

        AssertNotApplicable(DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(120, "J"), quality), "missing-input");
        AssertNotApplicable(DerivedMetrics.CostPerQualityPoint(Metric.Estimated(0.08, "EUR"), quality), "missing-input");
    }

    [Fact]
    public void A_quality_that_is_not_model_judged_is_not_a_quality()
    {
        Metric measured = Metric.Measured(4, "points");

        AssertNotApplicable(DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(120, "J"), measured), "missing-input");
        AssertNotApplicable(DerivedMetrics.CostPerQualityPoint(Metric.Estimated(0.08, "EUR"), measured), "missing-input");
    }

    [Theory]
    [InlineData("cost-not-configured")]
    [InlineData("not-priced")]
    public void A_cost_that_is_absent_gives_cost_per_point_missing_input_and_leaves_joules_alone(string reason)
    {
        AssertNotApplicable(DerivedMetrics.CostPerQualityPoint(Metric.NotApplicable(reason), Metric.ModelJudged(4)), "missing-input");
        AssertNotApplicable(DerivedMetrics.CostPerQualityPoint(Metric.NotReported(reason), Metric.ModelJudged(4)), "missing-input");
        AssertJudged(DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(120, "J"), Metric.ModelJudged(4)), 30.0);
    }

    [Fact]
    public void A_below_baseline_step_has_no_joules_per_point_but_keeps_its_cost_per_point()
    {
        Metric net = Metric.Measured(-5, "J");

        Metric joules = DerivedMetrics.JoulesPerQualityPoint(net, Metric.ModelJudged(3), ReadingFlags.BelowBaseline);
        Metric cost = DerivedMetrics.CostPerQualityPoint(Metric.Estimated(0.09, "EUR"), Metric.ModelJudged(3));

        AssertNotApplicable(joules, "below-baseline");
        Assert.Equal("below-baseline", DerivedMetrics.BelowBaselineReason);
        AssertJudged(cost, 0.03, "EUR");
    }

    [Fact]
    public void Other_flags_do_not_stop_joules_per_point()
    {
        ReadingFlags flags = ReadingFlags.SamplingFallback | ReadingFlags.LowResolution | ReadingFlags.SharedWindow | ReadingFlags.Throttled;

        AssertJudged(DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(120, "J"), Metric.ModelJudged(4), flags), 30.0);
    }

    [Fact]
    public void A_missing_input_outranks_the_below_baseline_flag_in_the_order_quality_energy_flag()
    {
        Metric negative = Metric.Measured(-5, "J");

        AssertNotApplicable(DerivedMetrics.JoulesPerQualityPoint(negative, Metric.NotApplicable("not-scored"), ReadingFlags.BelowBaseline), "missing-input");
        AssertNotApplicable(DerivedMetrics.JoulesPerQualityPoint(Metric.NotApplicable("hosted-tier"), Metric.ModelJudged(3), ReadingFlags.BelowBaseline), "missing-input");
    }

    [Fact]
    public void No_derived_metric_is_ever_labelled_measured_or_estimated_and_a_missing_value_is_never_a_number()
    {
        Metric[] nets = [Metric.Measured(120, "J"), Metric.Measured(0, "J"), Metric.Measured(-5, "J"), Metric.EstimatedQuantity(40, "J"), Metric.NotApplicable("hosted-tier"), Metric.NotReported("x")];
        Metric[] costs = [Metric.Estimated(0.08, "EUR"), Metric.MeasuredCost(0.3, "USD"), Metric.NotApplicable("cost-not-configured"), Metric.NotReported("not-priced")];
        Metric[] qualities = [Metric.ModelJudged(1), Metric.ModelJudged(5), Metric.NotApplicable("not-scored"), Metric.Measured(4, "points")];
        ReadingFlags[] flagSets = [ReadingFlags.None, ReadingFlags.BelowBaseline];

        foreach (Metric quality in qualities)
        {
            foreach (ReadingFlags flags in flagSets)
            {
                foreach (Metric net in nets)
                {
                    AssertDerived(DerivedMetrics.JoulesPerQualityPoint(net, quality, flags));
                }
            }

            foreach (Metric cost in costs)
            {
                AssertDerived(DerivedMetrics.CostPerQualityPoint(cost, quality));
            }
        }

        static void AssertDerived(Metric metric)
        {
            Assert.True(metric.Label is MetricLabel.ModelJudged or MetricLabel.NotApplicable, $"A derived metric is {metric.Label}.");
        }
    }

    [Fact]
    public void Null_arguments_throw_ArgumentNullException()
    {
        Metric some = Metric.Measured(1, "J");

        Assert.Throws<ArgumentNullException>(() => DerivedMetrics.JoulesPerQualityPoint(null!, Metric.ModelJudged(4)));
        Assert.Throws<ArgumentNullException>(() => DerivedMetrics.JoulesPerQualityPoint(some, null!));
        Assert.Throws<ArgumentNullException>(() => DerivedMetrics.CostPerQualityPoint(null!, Metric.ModelJudged(4)));
        Assert.Throws<ArgumentNullException>(() => DerivedMetrics.CostPerQualityPoint(Metric.Estimated(1, "EUR"), null!));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(0.5)]
    [InlineData(double.Epsilon)]
    public void A_quality_below_one_is_a_programmer_error(double score)
    {
        Metric quality = Metric.ModelJudged(score);

        ArgumentOutOfRangeException joules = Assert.Throws<ArgumentOutOfRangeException>(() => DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(120, "J"), quality));
        ArgumentOutOfRangeException cost = Assert.Throws<ArgumentOutOfRangeException>(() => DerivedMetrics.CostPerQualityPoint(Metric.Estimated(0.08, "EUR"), quality));
        Assert.Equal("quality", joules.ParamName);
        Assert.Equal("quality", cost.ParamName);
    }

    [Fact]
    public void A_small_quality_cannot_overflow_the_quotient_into_another_exception()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(double.MaxValue, "J"), Metric.ModelJudged(0.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(60, "J"), Metric.ModelJudged(double.Epsilon)));
        AssertJudged(DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(double.MaxValue, "J"), Metric.ModelJudged(1.0)), double.MaxValue);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(5.0)]
    public void The_ends_of_the_scale_are_accepted(double score)
    {
        AssertJudged(DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(10, "J"), Metric.ModelJudged(score)), 10.0 / score);
        AssertJudged(DerivedMetrics.CostPerQualityPoint(Metric.Estimated(10, "EUR"), Metric.ModelJudged(score)), 10.0 / score, "EUR");
    }

    [Fact]
    public void A_quotient_that_is_not_exact_is_neither_rounded_nor_truncated()
    {
        AssertJudged(DerivedMetrics.JoulesPerQualityPoint(Metric.Measured(100, "J"), Metric.ModelJudged(3)), 100.0 / 3);
        AssertJudged(DerivedMetrics.CostPerQualityPoint(Metric.Estimated(0.00001, "USD"), Metric.ModelJudged(3)), 0.00001 / 3, "USD");
    }

    [Fact]
    public void A_cost_without_a_currency_is_a_programmer_error()
    {
        Assert.Throws<ArgumentException>(() => DerivedMetrics.CostPerQualityPoint(Metric.Measured(5, "tokens"), Metric.ModelJudged(4)));
    }

    // ---- AJ-074: RunAssembler applies the derived metrics to every step ----

    private static RunDocument AssembleLocal(params StepReading[] steps)
    {
        RunInfo run = RunAssembler.BuildRunInfo("20261022T091530Z-local", Tier.Local, ["summarise"], RunMode.Serialized, 1, StepReadingFixtures.StartUtc, RunStatus.Completed);
        EnvironmentInfo environment = RunAssembler.BuildEnvironment(Tier.Local, new TierOptions { ModelId = "model-a", ReasoningMode = "on" }, gpu: null, runtime: null, modelLoad: null);

        return RunAssembler.Assemble(run, environment, probe: null, baseline: null, steps, [], wallClockMs: 4000.0);
    }

    private static StepReading Scored(Metric quality, Metric? net = null, Metric? cost = null, ReadingFlags flags = ReadingFlags.None, WindowSpan? window = null, string? outputText = null) =>
        StepReadingFixtures.Metered(netJoules: net, cost: cost, flags: flags, quality: quality, window: window, outputText: outputText);

    [Fact]
    public void WithDerivedMetrics_replaces_only_the_two_derived_members()
    {
        StepReading reading = Scored(Metric.ModelJudged(4), net: Metric.Measured(120, "J"), cost: Metric.Estimated(0.08, "EUR"), window: new WindowSpan(1.0, 3.0), outputText: "the answer");

        StepReading result = RunAssembler.WithDerivedMetrics(reading);

        AssertJudged(result.JoulesPerQualityPoint, 30.0);
        AssertJudged(result.CostPerQualityPoint, 0.02, "EUR");
        Assert.Equal("the answer", result.OutputText);
        Assert.Equal(new WindowSpan(1.0, 3.0), result.Window);
        string[] derived = [nameof(StepReading.JoulesPerQualityPoint), nameof(StepReading.CostPerQualityPoint)];
        foreach (PropertyInfo property in typeof(StepReading).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => !derived.Contains(p.Name)))
        {
            Assert.Equal(property.GetValue(reading), property.GetValue(result));
        }
    }

    [Fact]
    public void WithDerivedMetrics_gives_a_reading_without_a_quality_missing_input_not_not_scored()
    {
        StepReading reading = StepReadingFixtures.Metered(cost: Metric.Estimated(0.08, "EUR"));

        Assert.Equal(Metric.NotApplicable("not-scored"), reading.JoulesPerQualityPoint);

        StepReading result = RunAssembler.WithDerivedMetrics(reading);

        AssertNotApplicable(result.JoulesPerQualityPoint, "missing-input");
        AssertNotApplicable(result.CostPerQualityPoint, "missing-input");
        Assert.Equal(reading.Quality, result.Quality);
        Assert.Equal(reading.QualityReason, result.QualityReason);
    }

    [Fact]
    public void WithDerivedMetrics_reads_the_below_baseline_flag_of_the_reading()
    {
        StepReading reading = Scored(Metric.ModelJudged(3), net: Metric.Measured(-60, "J"), cost: Metric.Estimated(0.09, "EUR"), flags: ReadingFlags.BelowBaseline);

        StepReading result = RunAssembler.WithDerivedMetrics(reading);

        AssertNotApplicable(result.JoulesPerQualityPoint, "below-baseline");
        AssertJudged(result.CostPerQualityPoint, 0.03, "EUR");
    }

    [Fact]
    public async Task WithDerivedMetrics_gives_a_hosted_step_no_joules_per_point_but_keeps_its_cost_per_point()
    {
        StepReading hosted = await StepReadingFixtures.HostedAsync(Tier.Nvidia, quality: Metric.ModelJudged(4), cost: Metric.Estimated(0.08, "USD"));

        StepReading result = RunAssembler.WithDerivedMetrics(hosted);

        AssertNotApplicable(result.JoulesPerQualityPoint, "missing-input");
        Assert.Equal(Metric.ModelJudgedCost(0.02, "USD"), result.CostPerQualityPoint);
    }

    [Fact]
    public void WithDerivedMetrics_rejects_a_null_reading() =>
        Assert.Throws<ArgumentNullException>(() => RunAssembler.WithDerivedMetrics(null!));

    [Fact]
    public void Assemble_derives_the_metrics_of_every_step_from_its_own_quality()
    {
        StepReading scored = Scored(Metric.ModelJudged(4), net: Metric.Measured(120, "J"), cost: Metric.Estimated(0.08, "EUR"));
        StepReading unscored = StepReadingFixtures.Metered(cost: Metric.Estimated(0.08, "EUR"));
        StepReading failed = Scored(Metric.NotApplicable("step-not-ok"), cost: Metric.Estimated(0.08, "EUR"));
        StepReading below = Scored(Metric.ModelJudged(3), net: Metric.Measured(-60, "J"), cost: Metric.Estimated(0.09, "EUR"), flags: ReadingFlags.BelowBaseline);

        RunDocument document = AssembleLocal(scored, unscored, failed, below);

        AssertJudged(document.Steps[0].JoulesPerQualityPoint, 30.0);
        AssertJudged(document.Steps[0].CostPerQualityPoint, 0.02, "EUR");
        AssertNotApplicable(document.Steps[1].JoulesPerQualityPoint, "missing-input");
        AssertNotApplicable(document.Steps[1].CostPerQualityPoint, "missing-input");
        AssertNotApplicable(document.Steps[2].JoulesPerQualityPoint, "missing-input");
        AssertNotApplicable(document.Steps[2].CostPerQualityPoint, "missing-input");
        AssertNotApplicable(document.Steps[3].JoulesPerQualityPoint, "below-baseline");
        AssertJudged(document.Steps[3].CostPerQualityPoint, 0.03, "EUR");
        Assert.Equal(Metric.NotApplicable("not-scored"), unscored.JoulesPerQualityPoint);
    }

    [Fact]
    public void Assemble_writes_the_derived_metrics_into_the_file_with_the_model_judged_label()
    {
        RunDocument document = AssembleLocal(Scored(Metric.ModelJudged(4), net: Metric.Measured(120, "J"), cost: Metric.Estimated(0.08, "EUR")));

        string json = JsonSerializer.Serialize(document, AgentJoulesJsonContext.Default.RunDocument);
        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement step = parsed.RootElement.GetProperty("steps")[0];

        JsonElement joules = step.GetProperty("joulesPerQualityPoint");
        JsonElement cost = step.GetProperty("costPerQualityPoint");
        Assert.Equal((30.0, "model-judged"), (joules.GetProperty("value").GetDouble(), joules.GetProperty("label").GetString()));
        Assert.False(joules.TryGetProperty("currency", out _));
        Assert.Equal((0.02, "model-judged", "EUR"), (cost.GetProperty("value").GetDouble(), cost.GetProperty("label").GetString(), cost.GetProperty("currency").GetString()));
    }

    // ---- AJ-074: the shape of the judge and cost members of a reading ----

    private static StepReading ReadingWith(string member, Metric metric) => member switch
    {
        "cost" => StepReadingFixtures.Metered(cost: metric),
        "quality" => StepReadingFixtures.Metered(quality: metric),
        "joulesPerQualityPoint" => StepReadingFixtures.Metered(joulesPerQualityPoint: metric),
        "costPerQualityPoint" => StepReadingFixtures.Metered(costPerQualityPoint: metric),
        _ => throw new ArgumentOutOfRangeException(nameof(member)),
    };

    private static Metric BadShape(string shape) => shape switch
    {
        "measured-cost" => Metric.MeasuredCost(1, "USD"),
        "estimated-quantity" => Metric.EstimatedQuantity(1, "J"),
        "model-judged" => Metric.ModelJudged(4),
        "model-judged-cost" => Metric.ModelJudgedCost(4, "EUR"),
        "model-judged-unit" => Metric.ModelJudged(4, "points"),
        "measured" => Metric.Measured(4, "points"),
        "estimated-cost" => Metric.Estimated(4, "EUR"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    [Theory]
    [InlineData("cost", "measured-cost")]
    [InlineData("cost", "estimated-quantity")]
    [InlineData("cost", "model-judged")]
    [InlineData("cost", "model-judged-cost")]
    [InlineData("quality", "measured")]
    [InlineData("quality", "estimated-cost")]
    [InlineData("quality", "model-judged-cost")]
    [InlineData("quality", "model-judged-unit")]
    [InlineData("joulesPerQualityPoint", "measured")]
    [InlineData("joulesPerQualityPoint", "estimated-quantity")]
    [InlineData("joulesPerQualityPoint", "model-judged-cost")]
    [InlineData("joulesPerQualityPoint", "model-judged-unit")]
    [InlineData("costPerQualityPoint", "model-judged")]
    [InlineData("costPerQualityPoint", "model-judged-unit")]
    [InlineData("costPerQualityPoint", "measured-cost")]
    [InlineData("costPerQualityPoint", "estimated-cost")]
    public void The_reading_rejects_a_member_of_the_wrong_shape_naming_only_the_member(string member, string shape)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReadingWith(member, BadShape(shape)));

        Assert.Equal(member, failure.ParamName);
    }

    [Theory]
    [InlineData("cost")]
    [InlineData("quality")]
    [InlineData("joulesPerQualityPoint")]
    [InlineData("costPerQualityPoint")]
    public void The_reading_accepts_an_absent_member_of_either_kind(string member)
    {
        Assert.NotNull(ReadingWith(member, Metric.NotApplicable("x")));
        Assert.NotNull(ReadingWith(member, Metric.NotReported("x")));
    }

    [Fact]
    public void The_reading_accepts_the_shapes_the_producers_make()
    {
        StepReading reading = StepReadingFixtures.Metered(
            cost: Metric.Estimated(0.08, "EUR"),
            quality: JudgeScoreParser.Parse("4", new SecretRedactor()).Quality,
            joulesPerQualityPoint: Metric.ModelJudged(30),
            costPerQualityPoint: Metric.ModelJudgedCost(0.02, "EUR"));

        Assert.Equal(MetricLabel.ModelJudged, reading.CostPerQualityPoint.Label);
    }

    [Theory]
    [InlineData("quality", """{"value":4,"label":"model-judged","currency":"EUR"}""")]
    [InlineData("quality", """{"value":4,"label":"measured","unit":"points"}""")]
    [InlineData("joulesPerQualityPoint", """{"value":30,"label":"model-judged","currency":"EUR"}""")]
    [InlineData("costPerQualityPoint", """{"value":0.02,"label":"model-judged","unit":"J"}""")]
    [InlineData("costPerQualityPoint", """{"value":0.02,"label":"model-judged"}""")]
    [InlineData("cost", """{"value":0.08,"label":"measured","currency":"EUR"}""")]
    [InlineData("cost", """{"value":0.08,"label":"model-judged","currency":"EUR"}""")]
    public void A_document_read_back_with_a_member_of_the_wrong_shape_is_rejected(string member, string metricJson)
    {
        string json = MutatedStepJson(member, metricJson);

        ArgumentException failure = Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize(json, AgentJoulesJsonContext.Default.RunDocument));

        Assert.Equal(member, failure.ParamName);
    }

    // ---- AJ-074: the range of the judge and cost members of a reading ----

    [Theory]
    [InlineData("quality", """{"value":0,"label":"model-judged"}""")]
    [InlineData("quality", """{"value":0.5,"label":"model-judged"}""")]
    [InlineData("quality", """{"value":2.5,"label":"model-judged"}""")]
    [InlineData("quality", """{"value":6,"label":"model-judged"}""")]
    [InlineData("quality", """{"value":-1,"label":"model-judged"}""")]
    [InlineData("cost", """{"value":-1,"label":"estimated","currency":"EUR"}""")]
    [InlineData("joulesPerQualityPoint", """{"value":-30,"label":"model-judged"}""")]
    [InlineData("costPerQualityPoint", """{"value":-0.02,"label":"model-judged","currency":"EUR"}""")]
    public void A_document_read_back_with_a_member_out_of_range_is_rejected(string member, string metricJson)
    {
        string json = MutatedStepJson(member, metricJson);

        ArgumentException failure = Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize(json, AgentJoulesJsonContext.Default.RunDocument));

        Assert.Equal(member, failure.ParamName);
    }

    [Theory]
    [InlineData("quality", """{"value":1,"label":"model-judged"}""")]
    [InlineData("quality", """{"value":5,"label":"model-judged"}""")]
    [InlineData("cost", """{"value":0,"label":"estimated","currency":"EUR"}""")]
    [InlineData("joulesPerQualityPoint", """{"value":0,"label":"model-judged"}""")]
    [InlineData("costPerQualityPoint", """{"value":0,"label":"model-judged","currency":"EUR"}""")]
    public void A_document_read_back_with_a_member_at_the_edge_of_its_range_is_accepted(string member, string metricJson)
    {
        string json = MutatedStepJson(member, metricJson);

        Assert.Single(JsonSerializer.Deserialize(json, AgentJoulesJsonContext.Default.RunDocument)!.Steps);
    }

    [Theory]
    [InlineData("quality", 0.0)]
    [InlineData("quality", 0.5)]
    [InlineData("quality", 1e-300)]
    [InlineData("quality", 2.5)]
    [InlineData("quality", 6.0)]
    [InlineData("quality", -1.0)]
    [InlineData("cost", -1.0)]
    [InlineData("joulesPerQualityPoint", -30.0)]
    [InlineData("costPerQualityPoint", -0.02)]
    public void The_reading_rejects_a_member_out_of_range_naming_only_the_member(string member, double value)
    {
        Metric metric = member == "cost" ? Metric.Estimated(value, "EUR") : member == "costPerQualityPoint" ? Metric.ModelJudgedCost(value, "EUR") : Metric.ModelJudged(value);

        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReadingWith(member, metric));

        Assert.Equal(member, failure.ParamName);
        Assert.DoesNotContain(value.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    [InlineData(5.0)]
    public void The_reading_accepts_every_whole_score_from_one_to_five(double score) =>
        Assert.Equal(score, ReadingWith("quality", Metric.ModelJudged(score)).Quality.Value);

    [Theory]
    [InlineData("quality", """{"value":4,"label":"model-judged"}""")]
    [InlineData("joulesPerQualityPoint", """{"value":30,"label":"model-judged"}""")]
    [InlineData("costPerQualityPoint", """{"value":0.02,"label":"model-judged","currency":"EUR"}""")]
    [InlineData("cost", """{"value":0.08,"label":"estimated","currency":"EUR"}""")]
    public void A_document_read_back_with_a_member_of_the_right_shape_is_accepted(string member, string metricJson)
    {
        string json = MutatedStepJson(member, metricJson);

        RunDocument document = JsonSerializer.Deserialize(json, AgentJoulesJsonContext.Default.RunDocument)!;

        Assert.Single(document.Steps);
    }

    private static string MutatedStepJson(string member, string metricJson)
    {
        RunDocument document = AssembleLocal(Scored(Metric.ModelJudged(4), net: Metric.Measured(120, "J"), cost: Metric.Estimated(0.08, "EUR")));
        JsonNode root = JsonNode.Parse(JsonSerializer.Serialize(document, AgentJoulesJsonContext.Default.RunDocument))!;
        root["steps"]![0]![member] = JsonNode.Parse(metricJson);

        return root.ToJsonString();
    }

    private const string RubricFileName = "judge-rubric.md";
    private const int RubricCapBytes = 64 * 1024;

    private static readonly string[] LevelDigits = ["1", "2", "3", "4", "5"];

    // Text a task-agnostic rubric must not hold: an address, or the name of a vendor or of a model family.
    private static readonly string[] ForbiddenRubricWords = ["http", "://", "www.", "nemotron", "nvidia", "nebius", "openai", "gpt", "claude", "anthropic", "llama", "qwen", "gemini"];

    private static string CommittedRubricPath() => Path.Combine(ConfigurationFixtures.RepositoryRoot(), "config", RubricFileName);

    private static string CommittedRubricText() => File.ReadAllText(CommittedRubricPath());

    private static bool HasLevelLine(string text, int level) => Regex.IsMatch(text, $"^{level}\\. ", RegexOptions.Multiline, TimeSpan.FromSeconds(1));

    private static string RubricWith(params int[] levels) => "Score one step.\n\n" + string.Concat(levels.Select(level => $"{level}. level {level}\n"));

    private static string LoadFromTemp(string text)
    {
        using var workspace = TempWorkspace.CreateEmpty();
        return RubricLoader.Load(workspace.WriteFile(RubricFileName, text));
    }

    private static ConfigurationException LoadFailure(byte[] content, string fileName = RubricFileName)
    {
        using var workspace = TempWorkspace.CreateEmpty();
        string path = Path.Combine(workspace.Root, fileName);
        File.WriteAllBytes(path, content);
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RubricLoader.Load(path));
        Assert.DoesNotContain(workspace.Root, failure.Message, StringComparison.OrdinalIgnoreCase);
        return failure;
    }

    private static ConfigurationException LoadFailure(string text) => LoadFailure(System.Text.Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void The_committed_rubric_loads_and_holds_the_level_line(int level)
    {
        string text = RubricLoader.Load(CommittedRubricPath());

        Assert.True(HasLevelLine(text, level), $"The committed rubric has no line that starts with \"{level}. \".");
    }

    [Fact]
    public void The_committed_rubric_defines_each_level_once_and_in_order()
    {
        string[] levelLines = [.. CommittedRubricText().Split('\n').Where(line => line.Length > 2 && line[0] is >= '1' and <= '5' && line[1] == '.' && line[2] == ' ')];

        Assert.Equal(LevelDigits, levelLines.Select(line => line[..1]));
    }

    [Fact]
    public void The_committed_rubric_text_is_returned_verbatim()
    {
        Assert.Equal(CommittedRubricText(), RubricLoader.Load(CommittedRubricPath()));
    }

    [Theory]
    [InlineData(1, "1. Off-task or wrong")]
    [InlineData(2, "2. Major gaps")]
    [InlineData(3, "3. Usable with gaps")]
    [InlineData(4, "4. Correct and complete")]
    [InlineData(5, "5. Correct, complete and concise")]
    public void The_committed_level_line_starts_with_the_phrase_of_the_design(int level, string phrase)
    {
        string line = CommittedRubricText().Split('\n').Single(candidate => candidate.StartsWith($"{level}. ", StringComparison.Ordinal));

        Assert.StartsWith(phrase, line, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_digit_of_the_committed_rubric_opens_a_level_line_or_follows_the_word_level()
    {
        string[] lines = CommittedRubricText().Split('\n');

        foreach (string line in lines)
        {
            for (int index = 0; index < line.Length; index++)
            {
                if (!char.IsAsciiDigit(line[index]))
                {
                    continue;
                }

                bool opensLevelLine = index == 0 && line[0] is >= '1' and <= '5' && line.Length > 2 && line[1] == '.' && line[2] == ' ';
                bool followsLevel = index >= 6 && line[index] is >= '1' and <= '5' && string.CompareOrdinal(line, index - 6, "level ", 0, 6) == 0;
                Assert.True(opensLevelLine || followsLevel, $"A digit at offset {index} of a line is neither a level number nor the number after the word level.");
            }
        }
    }

    [Fact]
    public void The_committed_rubric_is_ascii_with_lf_line_ends_and_one_final_line_end_under_two_kib()
    {
        byte[] bytes = File.ReadAllBytes(CommittedRubricPath());

        Assert.InRange(bytes.Length, 1, 2047);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.All(bytes, value => Assert.True(value < 0x80, "The rubric holds a byte outside ASCII."));
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
    }

    [Fact]
    public void The_committed_rubric_contains_no_key_and_no_run_of_32_letters_and_digits()
    {
        string text = CommittedRubricText();

        Assert.DoesNotContain("nvapi-", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("[A-Za-z0-9]{32,}", RegexOptions.None, TimeSpan.FromSeconds(1)), text);
    }

    [Fact]
    public void The_committed_rubric_names_the_four_roles_and_no_model_vendor_or_address()
    {
        string text = CommittedRubricText();

        Assert.All(AgentCatalog.Names, name => Assert.Contains(name, text, StringComparison.Ordinal));
        Assert.All(ForbiddenRubricWords, word => Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_committed_rubric_leaves_the_closing_instruction_to_the_prompt()
    {
        string text = CommittedRubricText();

        Assert.DoesNotContain("Reply with one integer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing else", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Score one step.  \n\n1. a\n2. b  \n3. c\n4. d\n5. e\n\n")]
    [InlineData("1. a\n2. b\n3. c\n4. d\n5. e")]
    [InlineData("Score.\r\n\r\n1. a\r\n2. b\r\n3. c\r\n4. d\r\n5. e\r\n")]
    [InlineData("5. e\n4. d\n3. c\n2. b\n1. a\n")]
    [InlineData("1. a\n2. b\n3. c\n4. d\n5. e\n6. f\n")]
    [InlineData("1. a\t(tab)\n2. b\r\n3. c\n4. d\n5. e\n")]
    public void The_text_of_a_valid_rubric_is_returned_verbatim(string text) => Assert.Equal(text, LoadFromTemp(text));

    [Fact]
    public void A_byte_order_mark_is_removed_from_the_returned_text()
    {
        string text = RubricWith(1, 2, 3, 4, 5);
        using var workspace = TempWorkspace.CreateEmpty();
        string path = Path.Combine(workspace.Root, RubricFileName);
        File.WriteAllBytes(path, [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(text)]);

        string loaded = RubricLoader.Load(path);

        Assert.Equal(text, loaded);
    }

    [Fact]
    public void A_missing_file_exits_2_naming_the_file_and_the_key_and_no_folder()
    {
        using var workspace = TempWorkspace.CreateEmpty();

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RubricLoader.Load(Path.Combine(workspace.Root, RubricFileName)));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("judge-rubric.md does not exist (Paths:Rubric)", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(workspace.Root, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_folder_exits_2_naming_the_file_and_the_key()
    {
        using var workspace = TempWorkspace.CreateEmpty();

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RubricLoader.Load(Path.Combine(workspace.Root, "gone", RubricFileName)));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("judge-rubric.md does not exist (Paths:Rubric)", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void A_rubric_without_one_level_exits_2_naming_that_level(int missing)
    {
        int[] levels = [.. Enumerable.Range(1, 5).Where(level => level != missing)];

        ConfigurationException failure = LoadFailure(RubricWith(levels));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal($"judge-rubric.md: level {missing} is missing", failure.Message);
    }

    [Fact]
    public void A_rubric_without_several_levels_names_the_lowest_missing_one()
    {
        ConfigurationException failure = LoadFailure(RubricWith(2, 5));

        Assert.Equal("judge-rubric.md: level 1 is missing", failure.Message);
    }

    [Fact]
    public void An_empty_rubric_exits_2_naming_level_1()
    {
        ConfigurationException failure = LoadFailure(string.Empty);

        Assert.Equal("judge-rubric.md: level 1 is missing", failure.Message);
    }

    [Theory]
    [InlineData("3.x")]
    [InlineData("3.")]
    [InlineData(" 3. indented")]
    [InlineData("13. thirteen")]
    [InlineData("3) paren")]
    [InlineData("- 3. bullet")]
    [InlineData("level 3. inside a line")]
    public void A_line_that_is_not_a_level_line_does_not_define_the_level(string line)
    {
        string text = "1. a\n2. b\n" + line + "\n4. d\n5. e\n";

        ConfigurationException failure = LoadFailure(text);

        Assert.Equal("judge-rubric.md: level 3 is missing", failure.Message);
    }

    [Fact]
    public void A_level_line_that_ends_the_file_without_a_line_end_still_counts()
    {
        Assert.Equal("1. a\n2. b\n3. c\n4. d\n5. e", LoadFromTemp("1. a\n2. b\n3. c\n4. d\n5. e"));
    }

    [Fact]
    public void A_failure_message_never_carries_text_of_the_rubric()
    {
        const string marker = "distinctive-marker-sentence";

        ConfigurationException failure = LoadFailure($"{marker}\n1. a\n2. b\n4. d\n5. e\n");

        Assert.DoesNotContain(marker, failure.Message, StringComparison.Ordinal);
        Assert.Equal("judge-rubric.md: level 3 is missing", failure.Message);
    }

    [Fact]
    public void A_file_of_exactly_64_KiB_loads_and_one_byte_more_exits_2_naming_the_file()
    {
        string levels = RubricWith(1, 2, 3, 4, 5);
        string atCap = levels + new string('x', RubricCapBytes - levels.Length - 1) + "\n";
        string overCap = atCap + "x";

        Assert.Equal(RubricCapBytes, atCap.Length);
        Assert.Equal(atCap, LoadFromTemp(atCap));

        ConfigurationException failure = LoadFailure(overCap);

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("judge-rubric.md is larger than 64 KiB", failure.Message);
    }

    [Fact]
    public void A_file_that_is_not_valid_utf8_exits_2_naming_the_file()
    {
        byte[] bytes = [.. System.Text.Encoding.UTF8.GetBytes(RubricWith(1, 2, 3, 4, 5)), 0xC3, 0x28];

        ConfigurationException failure = LoadFailure(bytes);

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("judge-rubric.md is not valid UTF-8", failure.Message);
    }

    [Fact]
    public void A_folder_given_as_the_file_exits_2_naming_the_file()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        string path = Path.Combine(workspace.Root, RubricFileName);
        Directory.CreateDirectory(path);

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RubricLoader.Load(path));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("judge-rubric.md cannot be read (Paths:Rubric)", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(workspace.Root, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_path_with_a_nul_character_exits_2_as_unreadable_without_the_path_in_the_message()
    {
        string path = Path.Combine(Path.GetTempPath(), "agentjoules-nul" + (char)0 + "-" + RubricFileName);

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RubricLoader.Load(path));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.EndsWith("cannot be read (Paths:Rubric)", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(Path.GetTempPath(), failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Windows only: on Linux the named pipe is a Unix socket under /tmp whose open fails before the seek check,
    // and its file-name label differs, so the test would exercise the open-failure path with a mismatched message.
    [WindowsFact]
    public async Task A_stream_that_cannot_seek_is_refused_as_unreadable_without_blocking()
    {
        string pipeName = "agentjoules-rubric-" + Guid.NewGuid().ToString("N");
        string path = @"\\.\pipe\" + pipeName;
        using var server = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);

        // The load runs on its own task and the wait is bounded (a TimeoutException), so a regression that reads the pipe fails this test instead of wedging the run.
        Task<ConfigurationException> attempt = Task.Run(() => Assert.Throws<ConfigurationException>(() => RubricLoader.Load(path)));
        ConfigurationException failure = await attempt.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal(pipeName + " cannot be read (Paths:Rubric)", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0007)]
    [InlineData(0x001B)]
    [InlineData(0x007F)]
    [InlineData(0x0085)]
    [InlineData(0x200B)]
    [InlineData(0x202E)]
    [InlineData(0xFEFF)]
    [InlineData(0xE000)]
    [InlineData(0xE0041)]
    [InlineData(0xF0000)]
    [InlineData(0x0378)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    public void A_control_or_invisible_character_exits_2_without_echo(int codePoint)
    {
        string character = char.ConvertFromUtf32(codePoint);

        ConfigurationException failure = LoadFailure("Score" + character + " one step.\n" + RubricWith(1, 2, 3, 4, 5));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("judge-rubric.md holds a control or invisible character", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void A_second_byte_order_mark_at_the_start_exits_2()
    {
        byte[] preamble = System.Text.Encoding.UTF8.GetPreamble();
        byte[] bytes = [.. preamble, .. preamble, .. System.Text.Encoding.UTF8.GetBytes(RubricWith(1, 2, 3, 4, 5))];

        ConfigurationException failure = LoadFailure(bytes);

        Assert.Equal("judge-rubric.md holds a control or invisible character", failure.Message);
    }

    [Fact]
    public void A_control_character_is_refused_before_the_missing_level_is_named()
    {
        ConfigurationException failure = LoadFailure("1. a\n2. b\n4. d\n5. e" + (char)0 + "\n");

        Assert.Equal("judge-rubric.md holds a control or invisible character", failure.Message);
    }

    [Fact]
    public void Tab_line_feed_carriage_return_and_ordinary_non_ascii_letters_are_accepted()
    {
        string text = "Tab\there\r\nLine\n1. a\n2. b\n3. c\n4. d\n5. e " + char.ConvertFromUtf32(0x00E9) + char.ConvertFromUtf32(0x1F600) + "\n";

        Assert.Equal(text, LoadFromTemp(text));
    }

    [Fact]
    public void A_null_path_is_refused_as_a_programming_error() =>
        Assert.Throws<ArgumentNullException>(() => RubricLoader.Load(null!));
}