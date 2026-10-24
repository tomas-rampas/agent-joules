using System.Globalization;
using System.Text.RegularExpressions;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Readme;
using AgentJoules.Reporting.Render;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Runs;

using Xunit;

using static AgentJoules.TestSupport.ReportRunFixtures;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-087 (REQ-016, EDGE-002): the README results block as a deterministic function of the report model. The block is the heading, the header lines of <see cref="ReportHeader"/> (the same lines the Markdown
/// summary and the HTML page print), and one table of eleven columns with one row per run file from the totals of the file as written, a <c>not run</c> row per tier without a primary file, the
/// concurrent cost sentence under the table and, only when the caller says the screenshot exists, the dashboard image line. The text is ASCII, LF only, free of a timestamp and equal across cultures.
/// </summary>
[Trait("Spec", "REQ-016")]
public sealed partial class ReadmeBlockTests
{
    private const string DashboardLine = "![Live dashboard during the demo rehearsal](docs/report/dashboard.png)";
    private const string Heading = "### Results";
    private const string Joiner = "&#8288;";

    private static readonly string[] ExpectedHeaders =
    [
        "tier", "GPU", "run date", "status", "net J (run total)", "gross J (run total)", "input tokens", "output tokens", "cost (run total)", "labels", "flags",
    ];

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapedPipe();

    [GeneratedRegex(@"(?<!\\)(\]\(|!\[|<)", RegexOptions.CultureInvariant)]
    private static partial Regex ActiveMarkup();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", RegexOptions.CultureInvariant)]
    private static partial Regex Stamp();

    [GeneratedRegex(@"&#(\d+);", RegexOptions.CultureInvariant)]
    private static partial Regex NumericReference();

    // What a link filter of GitHub would find after the numeric references are decoded: its own character classes (an address ends at the word joiner, which is no character of a host name).
    [GeneratedRegex("htt" + @"ps?://|www\.|[A-Za-z0-9._%+-]@[A-Za-z0-9-]+\.|:[a-z0-9_+-]+:|\$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Linkable();

    private static string Block(params SelectedRun[] runs) => ReadmeResultsBlock.Render(ReportAggregator.Build(runs), dashboardScreenshotExists: false);

    private static string[] LinesOf(string text) => text.Split('\n');

    // The text as it reads on screen: the word joiners of a text from a file removed.
    private static string Plain(string text) => text.Replace(Joiner, string.Empty, StringComparison.Ordinal);

    private static string Decoded(string text) => NumericReference().Replace(text, match => char.ConvertFromUtf32(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));

    // A text from a file as the block writes it when it holds the given characters: a word joiner before each of the dots.
    private static string Dotted(string text) => text.Replace(".", Joiner + ".", StringComparison.Ordinal);

    private static string[] TableLines(string text) => [.. LinesOf(text).Where(line => line.StartsWith('|'))];

    private static string[] CellsOf(string line) => [.. UnescapedPipe().Split(line).Skip(1).SkipLast(1).Select(cell => cell.Trim())];

    private static string[][] RowsOf(string text) => [.. TableLines(text).Skip(2).Select(CellsOf)];

    private static string[] RowOf(string text, string tier) => Assert.Single(RowsOf(text), cells => cells[0] == tier);

    private static int Column(string header) => Array.IndexOf(ExpectedHeaders, header);

    private static SelectedRun CommittedRun(Tier tier, RunVariant variant = RunVariant.Primary) => Wrap(RunDocumentFixtures.Committed(tier), variant);

    private static SelectedRun[] ThreeTiers() => [CommittedRun(Tier.Local), CommittedRun(Tier.Nvidia), CommittedRun(Tier.Nebius)];

    private static string FakeKey() => string.Concat("nvapi", "-", "Zq9_xK3", "_mP7w");

    private static Totals TotalsOf(Totals source, Metric? gross = null, Metric? net = null, Metric? input = null, Metric? output = null, IReadOnlyDictionary<string, Metric>? cost = null) =>
        new(
            gross ?? source.GrossJoules,
            net ?? source.NetJoules,
            input ?? source.InputTokens,
            output ?? source.OutputTokens,
            cost ?? source.CostByCurrency,
            source.WallClockMs,
            source.Judge,
            source.Gco2e);

    private static SelectedRun WithTotals(Tier tier, Func<Totals, Totals> edit, RunVariant variant = RunVariant.Primary)
    {
        RunDocument document = RunDocumentFixtures.Committed(tier);

        return Wrap(document with { Totals = edit(document.Totals) }, variant);
    }

    private static SelectedRun WithEnvironment(Tier tier, Func<EnvironmentInfo, EnvironmentInfo> edit)
    {
        RunDocument document = RunDocumentFixtures.Committed(tier);

        return Wrap(document with { Environment = edit(document.Environment) });
    }

    // ---- the rows (criterion 1, scenario 1) ----

    [Fact]
    public void Block_has_a_row_per_tier()
    {
        string text = Block(ThreeTiers());

        string[][] rows = RowsOf(text);
        Assert.Equal(["local", "cloudgpu", "nvidia", "nebius"], rows.Select(cells => cells[0]));
        Assert.Equal("| cloudgpu | not run |" + string.Concat(Enumerable.Repeat(" |", ExpectedHeaders.Length - 2)), Assert.Single(TableLines(text), line => line.StartsWith("| cloudgpu", StringComparison.Ordinal)));
        Assert.Equal("Fixture GPU", RowOf(text, "local")[Column("GPU")]);
        Assert.Equal("N/A", RowOf(text, "nvidia")[Column("GPU")]);
    }

    [Fact]
    public void Table_has_the_eleven_columns_in_every_line()
    {
        string text = Block(ThreeTiers());

        string[] table = TableLines(text);
        Assert.Equal(ExpectedHeaders, CellsOf(table[0]));
        Assert.Equal("|" + string.Concat(Enumerable.Repeat("---|", ExpectedHeaders.Length)), table[1]);
        Assert.All(table, line => Assert.Equal(ExpectedHeaders.Length, CellsOf(line).Length));
        Assert.Equal(2 + 4, table.Length);
    }

    [Fact]
    public void A_baseline_file_is_the_row_after_its_tier()
    {
        string text = Block(CommittedRun(Tier.Nebius), CommittedRun(Tier.Local), CommittedRun(Tier.Nebius, RunVariant.Baseline), CommittedRun(Tier.Local, RunVariant.Baseline));

        Assert.Equal(["local", "local baseline", "cloudgpu", "nvidia", "nebius", "nebius baseline"], RowsOf(text).Select(cells => cells[0]));
    }

    [Fact]
    public void A_tier_with_only_a_baseline_file_has_its_not_run_row_before_the_baseline_row()
    {
        string text = Block(CommittedRun(Tier.Local, RunVariant.Baseline));

        string[][] rows = RowsOf(text);
        Assert.Equal(["local", "local baseline", "cloudgpu", "nvidia", "nebius"], rows.Select(cells => cells[0]));
        Assert.Equal(ReportSentences.NotRun, rows[0][1]);
        Assert.NotEqual(ReportSentences.NotRun, rows[1][1]);
    }

    [Fact]
    public void Every_tier_not_run_is_a_not_run_row()
    {
        string[][] rows = RowsOf(Block());

        Assert.Equal(["local", "cloudgpu", "nvidia", "nebius"], rows.Select(cells => cells[0]));
        Assert.All(rows, cells => Assert.Equal(ReportSentences.NotRun, cells[1]));
    }

    [Fact]
    public void A_file_without_rows_still_has_its_row_with_the_partial_flag()
    {
        // The totals are the ones the assembler writes for a run that stopped before its first step: not-reported (no-steps), as the file states them; P-40 prints them as written.
        RunDocument partial = RunDocumentFixtures.Partial(Tier.Local);
        SelectedRun empty = Wrap(RunAssembler.Assemble(partial.Run, partial.Environment, probe: partial.Probe, baseline: partial.Baseline, [], [], wallClockMs: null));

        string[] row = RowOf(Block(empty), "local");

        Assert.Equal("partial", row[Column("status")]);
        Assert.Equal("partial", row[Column("flags")]);
        Assert.Equal("not-reported (no-steps)", row[Column("net J (run total)")]);
        Assert.Equal("not-reported (no-steps)", row[Column("gross J (run total)")]);
        Assert.Equal("not-reported (no-steps)", row[Column("input tokens")]);
        Assert.Equal("not-reported (no-steps)", row[Column("output tokens")]);
        Assert.Equal("N/A (no priced step)", row[Column("cost (run total)")]);
    }

    // ---- the cells (criterion 1, 5; scenario 4) ----

    [Fact]
    public void The_cells_are_the_totals_of_the_file_as_written()
    {
        var cost = new Dictionary<string, Metric>(StringComparer.Ordinal) { ["USD"] = Metric.Estimated(0.5, "USD"), ["EUR"] = Metric.Estimated(0.25, "EUR") };
        SelectedRun run = WithTotals(
            Tier.Local,
            totals => TotalsOf(totals, gross: Metric.Measured(2000.04, "J"), net: Metric.Measured(1234.5, "J"), input: Metric.Measured(1500.0, "tokens"), output: Metric.Measured(300.0, "tokens"), cost: cost));

        string[] row = RowOf(Block(run), "local");

        Assert.Equal(
            ["local", "Fixture GPU", "2026-10-23", "completed", "1234.5", "2000.0", "1500", "300", "0.2500 EUR estimated; 0.5000 USD estimated", "measured, estimated", string.Empty],
            row);
    }

    [Fact]
    public void Totals_are_never_summed_across_files()
    {
        SelectedRun local = WithTotals(Tier.Local, totals => TotalsOf(totals, net: Metric.Measured(111.0, "J"), gross: Metric.Measured(222.0, "J")));
        SelectedRun cloud = Wrap(RunDocumentFixtures.CloudGpuPartialSampling());

        string text = Block(local, cloud);

        Assert.Equal("111.0", RowOf(text, "local")[Column("net J (run total)")]);
        Assert.DoesNotContain("333.0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hosted_energy_is_NA_and_never_a_number()
    {
        string[] row = RowOf(Block(ThreeTiers()), "nvidia");

        foreach (string header in new[] { "net J (run total)", "gross J (run total)" })
        {
            string cell = row[Column(header)];

            Assert.StartsWith("N/A", cell, StringComparison.Ordinal);
            Assert.False(double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            Assert.Equal("N/A (hosted-tier)", cell);
        }

        Assert.Contains("N/A", row[Column("labels")].Split(", "));
    }

    [Fact]
    public void A_hosted_cost_keeps_its_currency_and_is_never_converted()
    {
        string[] nebius = RowOf(Block(ThreeTiers()), "nebius");

        Assert.Matches(@"^\d+\.\d{4} USD estimated$", nebius[Column("cost (run total)")]);
        Assert.Equal("measured, estimated, N/A", nebius[Column("labels")]);
    }

    [Fact]
    public void Empty_cost_map_is_NA()
    {
        SelectedRun run = WithTotals(Tier.Local, totals => TotalsOf(totals, cost: new Dictionary<string, Metric>(StringComparer.Ordinal)));

        string[] row = RowOf(Block(run), "local");

        Assert.Equal("N/A (no priced step)", row[Column("cost (run total)")]);
        Assert.Contains("N/A", row[Column("labels")].Split(", "));
    }

    [Fact]
    public void A_cost_that_is_not_applicable_prints_its_reason()
    {
        string[] row = RowOf(Block(Wrap(RunDocumentFixtures.HostedNoUsage())), "nvidia");

        Assert.StartsWith("N/A", row[Column("cost (run total)")], StringComparison.Ordinal);
        Assert.StartsWith("not-reported", row[Column("input tokens")], StringComparison.Ordinal);
        Assert.Equal("N/A, not-reported", row[Column("labels")]);
    }

    [Fact]
    public void A_cost_entry_without_a_value_names_its_currency_and_its_reason()
    {
        var cost = new Dictionary<string, Metric>(StringComparer.Ordinal) { ["USD"] = Metric.NotApplicable("tokens-not-reported") };
        SelectedRun run = WithTotals(Tier.Local, totals => TotalsOf(totals, cost: cost));

        string[] row = RowOf(Block(run), "local");

        Assert.Equal("N/A USD (tokens-not-reported)", row[Column("cost (run total)")]);
        Assert.Contains("N/A", row[Column("labels")].Split(", "));
    }

    [Fact]
    public void Two_cost_entries_without_a_value_print_in_key_order_each_with_its_currency()
    {
        var cost = new Dictionary<string, Metric>(StringComparer.Ordinal)
        {
            ["USD"] = Metric.NotApplicable("no-price-usd"),
            ["EUR"] = Metric.NotApplicable("no-price-eur"),
        };
        SelectedRun run = WithTotals(Tier.Local, totals => TotalsOf(totals, cost: cost));

        Assert.Equal("N/A EUR (no-price-eur); N/A USD (no-price-usd)", RowOf(Block(run), "local")[Column("cost (run total)")]);
    }

    [Fact]
    public void A_valued_and_an_unvalued_cost_entry_print_side_by_side()
    {
        var cost = new Dictionary<string, Metric>(StringComparer.Ordinal)
        {
            ["EUR"] = Metric.Estimated(0.25, "EUR"),
            ["USD"] = Metric.NotApplicable("no-price-usd"),
        };
        SelectedRun run = WithTotals(Tier.Local, totals => TotalsOf(totals, cost: cost));

        Assert.Equal("0.2500 EUR estimated; N/A USD (no-price-usd)", RowOf(Block(run), "local")[Column("cost (run total)")]);
    }

    [Fact]
    public void Judge_totals_are_not_printed()
    {
        var judgeCost = new Dictionary<string, Metric>(StringComparer.Ordinal) { ["USD"] = Metric.Estimated(7.7777, "USD") };
        SelectedRun run = WithTotals(
            Tier.Nvidia,
            totals => new Totals(totals.GrossJoules, totals.NetJoules, totals.InputTokens, totals.OutputTokens, totals.CostByCurrency, totals.WallClockMs, new JudgeTotals(Metric.Measured(8123.0, "tokens"), Metric.Measured(457.0, "tokens"), judgeCost), totals.Gco2e));

        string text = Block(run);

        Assert.DoesNotContain("7.7777", text, StringComparison.Ordinal);
        Assert.DoesNotContain("8123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("457", text, StringComparison.Ordinal);
    }

    // ---- the header (criteria 2 and 4; scenario 3) ----

    [Fact]
    public void The_block_starts_with_the_heading_and_then_the_header_of_the_report()
    {
        ReportModel model = ReportAggregator.Build(ThreeTiers());

        string[] lines = LinesOf(ReadmeResultsBlock.Render(model, dashboardScreenshotExists: false));

        Assert.Equal(Heading, lines[0]);
        Assert.Equal(ReportHeader.Lines(model, text => MarkdownText.Escape(text)), lines.Skip(1).Take(ReportHeader.Lines(model, text => MarkdownText.Escape(text)).Count).Select(Plain));
    }

    [Fact]
    public void The_header_lists_the_gpu_driver_run_date_runtime_models_and_the_scope_sentence()
    {
        string text = Block(CommittedRun(Tier.Local));

        string raw = Assert.Single(LinesOf(text), line => line.StartsWith("- local:", StringComparison.Ordinal));
        string bullet = Plain(raw);
        Assert.Contains("; driver 555" + Joiner + ".00;", raw, StringComparison.Ordinal);
        Assert.Contains("; run date 2026-10-23T09:30:00Z;", raw, StringComparison.Ordinal);
        Assert.Contains("; GPU Fixture GPU;", bullet, StringComparison.Ordinal);
        Assert.Contains("; driver 555.00;", bullet, StringComparison.Ordinal);
        Assert.Contains("; run date 2026-10-23T09:30:00Z;", bullet, StringComparison.Ordinal);
        Assert.Contains("; local runtime ollama 0.0.0;", bullet, StringComparison.Ordinal);
        Assert.Contains("nemotron-3-nano:30b", bullet, StringComparison.Ordinal);
        Assert.Contains(ReportSentences.GpuBoardOnly, LinesOf(text));
        Assert.Contains(ReportSentences.JudgeExcluded, LinesOf(text));
    }

    [Fact]
    public void The_judge_model_is_named_and_self_judging_is_disclosed()
    {
        string text = Block(Wrap(RunDocumentFixtures.SelfJudged(Tier.Nvidia)));

        string bullet = Assert.Single(LinesOf(text), line => line.StartsWith("- nvidia:", StringComparison.Ordinal));
        Assert.Contains("; judge model-a;", bullet, StringComparison.Ordinal);
        Assert.Contains("; self-judging yes \\(judge model model-a equals the step model id\\);", Plain(bullet), StringComparison.Ordinal);
        Assert.Contains(ReportSentences.JudgeExcluded, LinesOf(text));
        Assert.Contains(ReportSentences.SelfJudged, LinesOf(text));
    }

    [Fact]
    public void A_run_that_is_not_self_judged_does_not_disclose_self_judging()
    {
        string text = Block(CommittedRun(Tier.Local), CommittedRun(Tier.Nebius));

        Assert.DoesNotContain(ReportSentences.SelfJudged, text, StringComparison.Ordinal);
        // Two tiers that run the same model under another ID than the judge's: the line prints both IDs and says that the model IDs cannot decide it, never "no".
        string plain = Plain(text);
        Assert.Contains("; judge " + RunDocumentFixtures.CommittedJudgeModel + "; self-judging not determinable from the model ids \\(judge model " + RunDocumentFixtures.CommittedJudgeModel + "; step model ", plain, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"self-judging no\b", plain);
    }

    [Fact]
    public void Tiers_that_ran_different_models_get_the_models_differ_line_and_no_comparison()
    {
        string text = Block(ThreeTiers());

        string line = Assert.Single(LinesOf(text), line => line.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal));
        Assert.Contains("local=nemotron-3-nano:30b \\(Q4\\_K\\_M\\)", Plain(line), StringComparison.Ordinal);
    }

    // ---- partial and sampling (criterion 3; scenario 5; EDGE-002) ----

    [Fact]
    public void A_partial_sampling_run_is_labelled_partial_and_sampling()
    {
        string text = Block(Wrap(RunDocumentFixtures.CloudGpuPartialSampling()));

        string[] row = RowOf(text, "cloudgpu");
        Assert.Equal("partial", row[Column("status")]);
        Assert.Equal("partial, sampling", row[Column("flags")]);
        Assert.Equal("Cloud GPU", row[Column("GPU")]);
        Assert.Matches(@"^\d+\.\d{4} USD estimated$", row[Column("cost (run total)")]);
        string bullet = Assert.Single(LinesOf(text), line => line.StartsWith("- cloudgpu:", StringComparison.Ordinal));
        Assert.Contains("; status partial;", bullet, StringComparison.Ordinal);
        Assert.EndsWith("; energy method sampling", bullet, StringComparison.Ordinal);
        Assert.DoesNotContain(ReportSentences.SamplingFallback, text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "EDGE-002")]
    public void A_sampling_fallback_run_says_sampling_in_its_row_and_discloses_the_fallback()
    {
        string text = Block(Wrap(RunDocumentFixtures.SamplingFallback()));

        string[] row = RowOf(text, "local");
        Assert.Equal("sampling", row[Column("flags")]);
        string[] lines = LinesOf(text);
        int bullet = Array.FindIndex(lines, line => line.StartsWith("- local:", StringComparison.Ordinal));
        Assert.Equal("  - " + ReportSentences.SamplingFallback, lines[bullet + 1]);
        Assert.Equal(1, text.Split(ReportSentences.SamplingFallback).Length - 1);
    }

    [Fact]
    [Trait("Spec", "EDGE-002")]
    public void A_forced_sampling_run_says_sampling_without_the_fallback_sentence()
    {
        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 1.0))]);
        TierReport report = Assert.Single(model.Tiers);

        string text = ReadmeResultsBlock.Render(model with { Tiers = [report with { EnergyMethods = [EnergyMethod.Sampling], SamplingFallback = false }] }, dashboardScreenshotExists: false);

        Assert.Equal("sampling", RowOf(text, "local")[Column("flags")]);
        Assert.DoesNotContain(ReportSentences.SamplingFallback, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_flags_are_the_run_flags_then_partial_then_sampling_then_concurrent()
    {
        ReportModel model = ReportAggregator.Build(
            [RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Run = document.Run with { Status = RunStatus.Partial, StopReason = "budget-reached", Mode = RunMode.Concurrent, Flags = [RunFlagNames.PartialOffload] } })]);
        TierReport report = Assert.Single(model.Tiers);

        string text = ReadmeResultsBlock.Render(model with { Tiers = [report with { EnergyMethods = [EnergyMethod.Sampling] }] }, dashboardScreenshotExists: false);

        Assert.Equal("partial-offload, partial, sampling, concurrent", RowOf(text, "local")[Column("flags")]);
    }

    // ---- concurrent mode (C-17) ----

    [Fact]
    public void Concurrent_cost_overlap_disclosed()
    {
        string concurrent = Block(Wrap(RunDocumentFixtures.Concurrent(Tier.Local)));
        string serialized = Block(CommittedRun(Tier.Local));

        string[] lines = LinesOf(concurrent);
        _ = Assert.Single(Array.FindAll(lines, line => line == ReportSentences.ConcurrentCostOverlap));
        Assert.Contains(ReportSentences.ConcurrentOverlap, lines);
        Assert.True(Array.IndexOf(lines, ReportSentences.ConcurrentCostOverlap) > Array.FindLastIndex(lines, line => line.StartsWith('|')));
        Assert.Contains("concurrent", RowOf(concurrent, "local")[Column("flags")].Split(", "));
        Assert.DoesNotContain(ReportSentences.ConcurrentCostOverlap, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(ReportSentences.ConcurrentOverlap, serialized, StringComparison.Ordinal);
    }

    // ---- determinism (criterion 6) ----

    [Fact]
    public void Two_renders_identical()
    {
        ReportModel model = ReportAggregator.Build([.. ThreeTiers(), Wrap(RunDocumentFixtures.Partial(Tier.Local), RunVariant.Baseline)]);

        string first = ReadmeResultsBlock.Render(model, dashboardScreenshotExists: false);
        string second = ReadmeResultsBlock.Render(model, dashboardScreenshotExists: false);

        Assert.Equal(first, second);
        Assert.DoesNotContain('\r', first);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.False(first.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void The_text_does_not_depend_on_the_culture(string culture)
    {
        ReportModel model = ReportAggregator.Build(ThreeTiers());
        string expected = ReadmeResultsBlock.Render(model, dashboardScreenshotExists: false);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            Assert.Equal(expected, ReadmeResultsBlock.Render(model, dashboardScreenshotExists: false));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void No_generation_timestamp()
    {
        string text = Block(ThreeTiers());

        // The only timestamps are the run dates of the files; nothing says when the block was made.
        string[] stamps = [.. Stamp().Matches(text).Select(match => match.Value).Distinct()];
        Assert.Equal(["2026-10-23T09:30:00Z"], stamps);
        Assert.DoesNotContain("generated", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_function_writes_no_marker()
    {
        string text = Block(ThreeTiers());

        Assert.DoesNotContain("results:begin", text, StringComparison.Ordinal);
        Assert.DoesNotContain("results:end", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_cross_tier_word()
    {
        string text = Block([.. ThreeTiers(), CommittedRun(Tier.Nebius, RunVariant.Baseline), Wrap(RunDocumentFixtures.Concurrent(Tier.Local), RunVariant.Baseline)]);

        foreach (string word in CrossTierWords)
        {
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotMatch(@"\d\s*x\b|x-fold|%", text);
    }

    // ---- the dashboard line (design 3.9, REQ-032 R6) ----

    [Fact]
    public void The_dashboard_line_is_the_last_line_and_only_when_the_screenshot_exists()
    {
        ReportModel model = ReportAggregator.Build(ThreeTiers());

        string without = ReadmeResultsBlock.Render(model, dashboardScreenshotExists: false);
        string with = ReadmeResultsBlock.Render(model, dashboardScreenshotExists: true);

        Assert.DoesNotContain("dashboard.png", without, StringComparison.Ordinal);
        Assert.Equal(without + "\n" + DashboardLine + "\n", with);
        Assert.Equal(1, with.Split(DashboardLine).Length - 1);
    }

    [Fact]
    public void The_dashboard_line_comes_after_the_concurrent_cost_sentence()
    {
        ReportModel model = ReportAggregator.Build([Wrap(RunDocumentFixtures.Concurrent(Tier.Local))]);

        string[] lines = LinesOf(ReadmeResultsBlock.Render(model, dashboardScreenshotExists: true));

        Assert.True(Array.IndexOf(lines, DashboardLine) > Array.IndexOf(lines, ReportSentences.ConcurrentCostOverlap));
    }

    // ---- text from a file (P-18, ASCII) ----

    [Fact]
    public void Hostile_text_adds_no_cell_link_image_or_tag()
    {
        const string hostile = "a|b [x](y) ![i](z) <b>bold</b> `code` $math$";
        SelectedRun run = WithEnvironment(
            Tier.Local,
            environment => environment with { GpuName = hostile, DriverVersion = hostile, Models = [new EnvironmentModel("Planner", hostile, Baseline: false)], Precision = hostile });

        string text = Block(run);

        Assert.All(TableLines(text), line => Assert.Equal(ExpectedHeaders.Length, CellsOf(line).Length));
        Assert.Empty(ActiveMarkup().Matches(text));
        Assert.Contains("a\\|b", RowOf(text, "local")[Column("GPU")], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("see htt" + "ps://h.example/a now")]
    [InlineData("www.h.example")]
    [InlineData("a@h.example")]
    [InlineData(":shipit:")]
    [InlineData("a$b$c")]
    public void A_link_an_address_a_shortcode_or_math_in_a_file_text_is_defused(string payload)
    {
        // The Markdown escape does not stop the filters GitHub runs after the rendering: they link a bare address, a www host and an e-mail address, turn :name: into an image and read a dollar pair as math.
        var cost = new Dictionary<string, Metric>(StringComparer.Ordinal) { [payload] = Metric.Estimated(1.0, payload) };
        RunDocument document = RunDocumentFixtures.Committed(Tier.Local);
        RunDocument edited = document with
        {
            Environment = document.Environment with { GpuName = payload, Models = [new EnvironmentModel("Planner", payload, Baseline: false)] },
            Totals = TotalsOf(document.Totals, net: Metric.NotApplicable(payload), cost: cost),
        };

        string text = Block(Wrap(edited));

        Assert.All(text, character => Assert.True(character <= '~', $"U+{(int)character:X4} is not ASCII"));
        Assert.All(TableLines(text), line => Assert.Equal(ExpectedHeaders.Length, CellsOf(line).Length));
        // The run dates are formatted by the tool, not read from a file text; they hold colons and are the one place a pair of colons can enclose digits.
        Assert.Empty(Linkable().Matches(Stamp().Replace(Decoded(text), string.Empty)).Select(match => match.Value));
        Assert.Contains(Joiner, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_defused_text_reads_the_same_on_screen()
    {
        SelectedRun run = WithEnvironment(Tier.Local, environment => environment with { GpuName = "www.h.example a@h.example :shipit:" });

        Assert.Equal("www.h.example a@h.example :shipit:", Plain(RowOf(Block(run), "local")[Column("GPU")]));
    }

    [Fact]
    public void The_committed_local_cost_cell_and_every_text_the_tool_writes_hold_no_word_joiner()
    {
        string text = Block(ThreeTiers());

        Assert.Equal("0.000075 EUR estimated", RowOf(text, "local")[Column("cost (run total)")]);
        Assert.DoesNotContain(Joiner, string.Join('\n', TableLines(text)), StringComparison.Ordinal);
        Assert.Contains("; run date 2026-10-23T09:30:00Z;", text, StringComparison.Ordinal);
        Assert.Contains("; status completed; mode serialized;", text, StringComparison.Ordinal);
        Assert.Contains(ReportSentences.GpuBoardOnly, LinesOf(text));
        Assert.Contains(ReportSentences.JudgeExcluded, LinesOf(text));
        Assert.Contains(ReportSentences.ConcurrentCostOverlap, LinesOf(Block(Wrap(RunDocumentFixtures.Concurrent(Tier.Local)))));
        Assert.Contains(ReportSentences.NotRun + " |", TableLines(text).Single(line => line.StartsWith("| cloudgpu", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_currency_key_that_holds_a_dot_gets_the_joiner()
    {
        var cost = new Dictionary<string, Metric>(StringComparer.Ordinal) { ["US.D"] = Metric.Estimated(1.0, "US.D") };
        SelectedRun run = WithTotals(Tier.Local, totals => TotalsOf(totals, cost: cost));

        Assert.Equal("1.0000 US" + Joiner + ".D estimated", RowOf(Block(run), "local")[Column("cost (run total)")]);
    }

    [Fact]
    public void A_key_in_a_file_is_masked()
    {
        string key = FakeKey();
        SelectedRun run = WithEnvironment(Tier.Local, environment => environment with { GpuName = key, Models = [new EnvironmentModel("Planner", key, Baseline: false)] });

        string text = Block(run);

        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        Assert.DoesNotContain(key[(key.IndexOf('-', StringComparison.Ordinal) + 1)..], text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_block_is_ASCII_whatever_the_file_holds()
    {
        // The euro sign, two CJK ideographs and a character outside the BMP, built from code points so the source stays ASCII.
        string wide = $"G{char.ConvertFromUtf32(0x20AC)}PU {char.ConvertFromUtf32(0x4E2D)}{char.ConvertFromUtf32(0x6587)} {char.ConvertFromUtf32(0x1F600)}";
        SelectedRun run = WithEnvironment(Tier.Local, environment => environment with { GpuName = wide, Models = [new EnvironmentModel("Planner", wide, Baseline: false)] });

        string text = Block(run);

        Assert.All(text, character => Assert.True(character <= '~', $"U+{(int)character:X4} is not ASCII"));
        Assert.Equal("G&#8364;PU &#20013;&#25991; &#128512;", RowOf(text, "local")[Column("GPU")]);
        Assert.Contains("G&#8364;PU", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_currency_code_from_a_file_is_ASCII_too()
    {
        string euro = char.ConvertFromUtf32(0x20AC);
        var cost = new Dictionary<string, Metric>(StringComparer.Ordinal) { [euro] = Metric.Estimated(1.0, euro) };
        SelectedRun run = WithTotals(Tier.Local, totals => TotalsOf(totals, cost: cost));

        string text = Block(run);

        Assert.All(text, character => Assert.True(character <= '~'));
        Assert.Equal("1.0000 &#8364; estimated", RowOf(text, "local")[Column("cost (run total)")]);
    }

    [Fact]
    public void Every_committed_fixture_renders_ASCII_with_no_carriage_return()
    {
        foreach (Tier tier in new[] { Tier.Local, Tier.Nvidia, Tier.Nebius })
        {
            string text = Block(CommittedRun(tier));

            Assert.All(text, character => Assert.True(character is '\n' or (>= ' ' and <= '~')));
        }
    }

    // ---- the contract ----

    [Fact]
    public void A_null_model_is_refused()
    {
        _ = Assert.Throws<ArgumentNullException>(() => ReadmeResultsBlock.Render(null!, dashboardScreenshotExists: false));
    }

    [Fact]
    public void The_new_fixtures_pass_the_run_file_check()
    {
        RunDocument[] documents = [RunDocumentFixtures.CloudGpuPartialSampling(), RunDocumentFixtures.SelfJudged(Tier.Nvidia), RunDocumentFixtures.SelfJudged(Tier.Local)];

        foreach (RunDocument document in documents)
        {
            Assert.Empty(RunFileValidator.Check(NameOf(document.Run.Tier, RunVariant.Primary), RunDocumentFixtures.Serialise(document)));
        }
    }

    [Fact]
    public void The_new_fixtures_describe_the_case_they_are_named_for()
    {
        RunDocument cloud = RunDocumentFixtures.CloudGpuPartialSampling();
        StepReading step = Assert.Single(cloud.Steps);
        Assert.Equal(Tier.CloudGpu, cloud.Run.Tier);
        Assert.Equal(RunStatus.Partial, cloud.Run.Status);
        Assert.Equal(EnergyMethod.Sampling, step.Method);
        Assert.False(step.Flags.HasFlag(ReadingFlags.SamplingFallback));
        Assert.Equal(MetricLabel.Measured, step.NetJoules.Label);
        Assert.Equal("USD", Assert.Single(cloud.Totals.CostByCurrency).Key);

        RunDocument judged = RunDocumentFixtures.SelfJudged(Tier.Nvidia);
        Assert.True(judged.Run.SelfJudged);
        Assert.Contains(judged.Steps, workflowStep => workflowStep.ModelId == Assert.Single(judged.Judge).ModelId);
    }

    [Fact]
    public void The_cloudgpu_fixture_carries_a_probe_a_baseline_and_a_vm_cost()
    {
        RunDocument cloud = RunDocumentFixtures.CloudGpuPartialSampling();

        Assert.NotNull(cloud.Probe);
        Assert.NotNull(cloud.Baseline);
        Assert.Equal(MetricLabel.Measured, cloud.Environment.CloudCostUsd.Label);
        Assert.Equal(0.45, cloud.Environment.CloudCostUsd.Value);
        Assert.NotNull(cloud.Environment.CloudCostObservedUtc);
    }

    // ---- the gaps the gate found: one concurrent baseline, the whole committed row, the empty and tilde names, the cuts ----

    [Fact]
    public void The_cost_sentence_is_printed_once_when_only_a_baseline_file_is_concurrent()
    {
        string text = Block(CommittedRun(Tier.Local), CommittedRun(Tier.Nebius), Wrap(RunDocumentFixtures.Concurrent(Tier.Local), RunVariant.Baseline));

        Assert.Single(Array.FindAll(LinesOf(text), line => line == ReportSentences.ConcurrentCostOverlap));
    }

    [Fact]
    public void The_committed_local_row_is_exact()
    {
        Assert.Equal(
            ["local", "Fixture GPU", "2026-10-23", "completed", "906.0", "1266.0", "1638", "1026", "0.000075 EUR estimated", "measured, estimated", string.Empty],
            RowOf(Block(CommittedRun(Tier.Local)), "local"));
    }

    [Fact]
    public void A_blank_gpu_name_is_NA()
    {
        SelectedRun run = WithEnvironment(Tier.Local, environment => environment with { GpuName = string.Empty });

        Assert.Equal("N/A", RowOf(Block(run), "local")[Column("GPU")]);
    }

    [Fact]
    public void A_trailing_backslash_next_to_a_dot_renders_as_an_escaped_backslash()
    {
        SelectedRun run = WithEnvironment(Tier.Local, environment => environment with { GpuName = "a.b\\" });

        Assert.Equal("a" + Joiner + ".b\\\\", RowOf(Block(run), "local")[Column("GPU")]);
    }

    [Fact]
    public void A_dollar_becomes_a_bare_question_mark_and_never_an_escaped_one()
    {
        SelectedRun run = WithEnvironment(Tier.Local, environment => environment with { GpuName = "a$b\\$c" });

        Assert.Equal("a?b\\\\?c", RowOf(Block(run), "local")[Column("GPU")]);
    }

    [Fact]
    public void An_at_sign_gets_no_joiner_because_only_the_dot_ends_an_address()
    {
        SelectedRun run = WithEnvironment(Tier.Local, environment => environment with { GpuName = "a@b.c" });

        Assert.Equal("a@b" + Joiner + ".c", RowOf(Block(run), "local")[Column("GPU")]);
    }

    [Fact]
    public void An_unvalued_cost_key_is_defused()
    {
        var cost = new Dictionary<string, Metric>(StringComparer.Ordinal) { ["www.h.example"] = Metric.NotApplicable("r") };
        SelectedRun run = WithTotals(Tier.Local, totals => TotalsOf(totals, cost: cost));

        Assert.Equal("N/A www" + Joiner + ".h" + Joiner + ".example (r)", RowOf(Block(run), "local")[Column("cost (run total)")]);
    }

    [Fact]
    public void An_unvalued_and_a_valued_cost_key_are_cut_at_the_same_length()
    {
        string key = new('k', 200);
        var cost = new Dictionary<string, Metric>(StringComparer.Ordinal) { [key] = Metric.NotApplicable("r"), [key + "z"] = Metric.Estimated(1.0, key + "z") };
        SelectedRun run = WithTotals(Tier.Local, totals => TotalsOf(totals, cost: cost));

        string cell = RowOf(Block(run), "local")[Column("cost (run total)")];

        string cut = new string('k', 80) + Dotted("...");
        Assert.Equal("N/A " + cut + " (r); 1.0000 " + cut + " estimated", cell);
    }

    [Fact]
    public void A_tilde_next_to_a_wide_character_stays_an_escaped_tilde()
    {
        SelectedRun run = WithEnvironment(Tier.Local, environment => environment with { GpuName = "a~" + char.ConvertFromUtf32(0xE9) });

        Assert.Equal("a\\~&#233;", RowOf(Block(run), "local")[Column("GPU")]);
    }

    [Fact]
    public void A_long_reason_is_cut_at_80()
    {
        SelectedRun run = WithTotals(Tier.Local, totals => TotalsOf(totals, net: Metric.NotApplicable(new string('r', 200))));

        Assert.Equal("N/A (" + new string('r', 80) + Dotted("...") + ")", RowOf(Block(run), "local")[Column("net J (run total)")]);
    }

    [Fact]
    public void A_long_gpu_name_is_cut_at_128()
    {
        SelectedRun run = WithEnvironment(Tier.Local, environment => environment with { GpuName = new string('g', 500) });

        Assert.Equal(new string('g', 128) + Dotted("..."), RowOf(Block(run), "local")[Column("GPU")]);
    }

    [Fact]
    public void The_row_helper_is_the_one_the_summary_and_the_page_use()
    {
        Assert.Equal(15, ReportTable.Headers.Count);
        string summary = SummaryMarkdownRenderer.Render(ReportAggregator.Build(ThreeTiers()));
        string[] header = [.. LinesOf(summary).Where(line => line.StartsWith('|')).Take(1).SelectMany(line => CellsOf(line))];

        Assert.Equal(ReportTable.Headers, header);
        Assert.Equal("measured, estimated, model-judged, N/A, not-reported", ReportTable.Labels([MetricLabel.NotReported, MetricLabel.NotApplicable, MetricLabel.ModelJudged, MetricLabel.Estimated, MetricLabel.Measured, MetricLabel.Measured]));
        Assert.Equal("local baseline", ReportTable.TierName(Tier.Local, RunVariant.Baseline));
    }
}