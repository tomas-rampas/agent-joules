using AgentJoules.Cli;
using AgentJoules.Cli.Hosting;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Security;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Runs;

using Json.Schema;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit;

using static AgentJoules.TestSupport.PriceFileFixtures;

using RunMode = AgentJoules.Core.Runs.RunMode;
using RunTier = AgentJoules.Core.Runs.Tier;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-070 (REQ-009): the price table. <c>config/prices.schema.json</c> makes a missing source, a missing date and a price of 0 unrepresentable, and
/// <see cref="PriceTable.Load"/> enforces the same rules in code because Core does not reference the schema library (design 2.4). The committed file is
/// validated by both, and <see cref="Loader_and_schema_reject_the_same_invalid_rows"/> keeps the two in step. Written files live in a temporary
/// folder; the repository is only read.
/// </summary>
[Trait("Spec", "REQ-009")]
public sealed class CostModelTests
{
    private const string Host = "https://example.invalid/";

    public static TheoryData<string, string, string> InvalidRows => new()
    {
        { "no tier", Row(tier: null), "tier" },
        { "unknown tier", Row(tier: Quote("bogus")), "tier" },
        { "tier in another letter case", Row(tier: Quote("Nebius")), "tier" },
        { "tier as number", Row(tier: "1"), "tier" },
        { "no modelId", Row(modelId: null), "modelId" },
        { "empty modelId", Row(modelId: Quote(string.Empty)), "modelId" },
        { "blank modelId", Row(modelId: Quote("   ")), "modelId" },
        { "modelId as number", Row(modelId: "5"), "modelId" },
        { "no inputUsdPer1M", Row(inputUsdPer1M: null), "inputUsdPer1M" },
        { "inputUsdPer1M null", Row(inputUsdPer1M: "null"), "inputUsdPer1M" },
        { "input price 0", Row(inputUsdPer1M: "0"), "inputUsdPer1M" },
        { "input price negative", Row(inputUsdPer1M: "-0.5"), "inputUsdPer1M" },
        { "input price as text", Row(inputUsdPer1M: Quote("0.06")), "inputUsdPer1M" },
        { "no outputUsdPer1M", Row(outputUsdPer1M: null), "outputUsdPer1M" },
        { "output price 0", Row(outputUsdPer1M: "0"), "outputUsdPer1M" },
        { "output price negative", Row(outputUsdPer1M: "-1"), "outputUsdPer1M" },
        { "no sourceUrl", Row(sourceUrl: null), "sourceUrl" },
        { "empty sourceUrl", Row(sourceUrl: Quote(string.Empty)), "sourceUrl" },
        { "sourceUrl as number", Row(sourceUrl: "7"), "sourceUrl" },
        { "sourceUrl not https", Row(sourceUrl: Quote("ftp://example.invalid/p")), "sourceUrl" },
        { "sourceUrl with markup", Row(sourceUrl: Quote(Host + "a\"><script>x</script>")), "sourceUrl" },
        { "sourceUrl with a space inside", Row(sourceUrl: Quote(Host + "a b")), "sourceUrl" },
        { "sourceUrl with a trailing space", Row(sourceUrl: Quote(Host + "p ")), "sourceUrl" },
        { "sourceUrl with a non-ASCII letter", Row(sourceUrl: Quote(Host + char.ConvertFromUtf32(0xFC))), "sourceUrl" },
        { "sourceUrl with a right-to-left override", Row(sourceUrl: Quote(Host + char.ConvertFromUtf32(0x202E))), "sourceUrl" },
        { "sourceUrl with a bell", Row(sourceUrl: Quote(Host + "a" + char.ConvertFromUtf32(7))), "sourceUrl" },
        { "sourceUrl with a line feed inside", Row(sourceUrl: Quote(Host + "a\nb")), "sourceUrl" },
        { "sourceUrl with a trailing line feed", Row(sourceUrl: Quote(Host + "p\n")), "sourceUrl" },
        { "sourceUrl with a caret", Row(sourceUrl: Quote(Host + "a^b")), "sourceUrl" },
        { "sourceUrl with credentials", Row(sourceUrl: Quote("https://user:pw@example.invalid/p")), "sourceUrl" },
        { "no retrievedDate", Row(retrievedDate: null), "retrievedDate" },
        { "malformed date", Row(retrievedDate: Quote("22-10-2026")), "retrievedDate" },
        { "impossible date", Row(retrievedDate: Quote("2026-02-30")), "retrievedDate" },
        { "date as number", Row(retrievedDate: "20261022"), "retrievedDate" },
        { "date with a time", Row(retrievedDate: Quote("2026-10-22T00:00:00Z")), "retrievedDate" },
        { "extra property", Row(extra: ",\"extra\":1"), "extra" },
    };

    // A JSON escape of a lone surrogate, written without a backslash-u sequence in this source: the loader must refuse it with exit 2, not fail with an unhandled exception.
    private static string LoneSurrogate => "\\" + "uDC00";

    private static string PriceFile(TempWorkspace workspace, params string[] rows) => workspace.WriteFile("prices.json", "[" + string.Join(",", rows) + "]");

    private static string RepositoryFile(params string[] segments) => Path.Combine([ConfigurationFixtures.RepositoryRoot(), .. segments]);

    private static ConfigurationException LoadFails(string path) => Assert.Throws<ConfigurationException>(() => PriceTable.Load(path));

    [Fact]
    public void Committed_prices_validate()
    {
        string path = RepositoryFile("config", "prices.json");

        EvaluationResults result = Evaluate(File.ReadAllText(path));
        PriceTable table = PriceTable.Load(path);

        Assert.True(result.IsValid, "config/prices.json does not validate against config/prices.schema.json.");
        Assert.All(table.Rows, row =>
        {
            Assert.True(row.InputUsdPer1M > 0);
            Assert.True(row.OutputUsdPer1M > 0);
        });
    }

    [Fact]
    public void Committed_prices_have_no_row_for_the_tiers_priced_by_electricity_or_rental()
    {
        PriceTable table = PriceTable.Load(RepositoryFile("config", "prices.json"));

        Assert.DoesNotContain(table.Rows, row => row.Tier is "local" or "cloudgpu");
    }

    [Theory]
    [MemberData(nameof(InvalidRows))]
    public void Invalid_row_is_rejected_with_exit_2_naming_the_row_index_and_the_field(string description, string row, string field)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = PriceFile(workspace, Row(modelId: Quote("vendor/valid")), row);

        ConfigurationException failure = LoadFails(path);

        Assert.True(failure.ExitCode == ExitCode.InvalidArguments, description);
        Assert.Contains($"row 1: {field}", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("row 0", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidRows))]
    public void Loader_and_schema_reject_the_same_invalid_rows(string description, string row, string field)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = PriceFile(workspace, row);

        EvaluationResults schemaResult = Evaluate("[" + row + "]");
        ConfigurationException failure = LoadFails(path);

        Assert.False(schemaResult.IsValid, "The schema accepts: " + description);
        Assert.Contains($"row 0: {field}", failure.Message, StringComparison.Ordinal);
        Assert.Contains(field, Findings(schemaResult), StringComparison.Ordinal);
    }

    // The schema accepts these on purpose (a regular expression cannot judge a URI); the loader is stricter and refuses them.
    [Theory]
    [InlineData("https://example.invalid/%zz")]
    [InlineData("https://example.invalid/%")]
    [InlineData("https:///p")]
    [InlineData("https://example.invalid:99999/p")]
    [InlineData("https://example.invalid/a[b]")]
    [InlineData("https://example.invalid/p#a#b")]
    [InlineData("https://a..b/p")]
    public void Loader_refuses_url_shapes_the_schema_accepts(string url)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string row = Row(sourceUrl: Quote(url));
        string path = PriceFile(workspace, Row(modelId: Quote("vendor/valid")), row);

        Assert.True(Evaluate("[" + row + "]").IsValid, "The schema is expected to accept this shape.");
        ConfigurationException failure = LoadFails(path);

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains("row 1: sourceUrl", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Committed_source_url_passes_the_source_rule_of_both()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string url = PriceTable.Load(RepositoryFile("config", "prices.json")).Rows[0].SourceUrl;
        string row = Row(sourceUrl: Quote(url));

        Assert.True(Evaluate("[" + row + "]").IsValid);
        Assert.Equal(url, PriceTable.Load(PriceFile(workspace, row)).Rows[0].SourceUrl);
    }

    [Fact]
    public void Rejection_message_never_echoes_the_offending_value()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        string first = LoadFails(PriceFile(workspace, Row(sourceUrl: Quote("ftp://leaky-host-1234.example.invalid/secret")))).Message;
        string second = LoadFails(PriceFile(workspace, Row(inputUsdPer1M: "-987.125"))).Message;

        Assert.DoesNotContain("leaky-host-1234", first, StringComparison.Ordinal);
        Assert.DoesNotContain("987.125", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejection_message_names_the_file_and_not_its_folder()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        string message = LoadFails(PriceFile(workspace, Row(sourceUrl: null))).Message;

        Assert.Equal("prices.json: row 0: sourceUrl is required", message);
        Assert.DoesNotContain(workspace.Root, message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_member_is_named_with_its_row()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = PriceFile(workspace, Row(extra: ",\"tier\":\"nvidia\""));

        ConfigurationException failure = LoadFails(path);

        Assert.Equal("prices.json: row 0: tier is repeated", failure.Message);
    }

    [Fact]
    public void A_member_name_over_32_characters_is_not_echoed()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string longName = new('x', 64);
        string path = PriceFile(workspace, Row(extra: $",\"{longName}\":1"));

        string message = LoadFails(path).Message;

        Assert.Equal("prices.json: row 0: a member name of 64 characters is not a known member", message);
    }

    [Theory]
    [InlineData("tier")]
    [InlineData("modelId")]
    [InlineData("sourceUrl")]
    [InlineData("retrievedDate")]
    public void A_lone_surrogate_escape_in_a_value_exits_2_naming_the_row(string member)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string broken = $"\"{LoneSurrogate}\"";
        string row = member switch
        {
            "tier" => Row(tier: broken),
            "modelId" => Row(modelId: broken),
            "sourceUrl" => Row(sourceUrl: broken),
            _ => Row(retrievedDate: broken),
        };

        ConfigurationException failure = LoadFails(PriceFile(workspace, row));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("prices.json: row 0: a string holds an invalid escape", failure.Message);
    }

    [Fact]
    public void A_lone_surrogate_escape_in_a_member_name_exits_2_naming_the_row()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = PriceFile(workspace, Row(extra: $",\"{LoneSurrogate}\":1"));

        ConfigurationException failure = LoadFails(path);

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("prices.json: row 0: a member name holds an invalid escape", failure.Message);
    }

    [Fact]
    public void Two_rows_with_the_same_tier_and_model_exit_2_naming_both_rows()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = PriceFile(workspace, Row(), Row(modelId: Quote("vendor/other")), Row());

        ConfigurationException failure = LoadFails(path);

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("prices.json: rows 0 and 2 have the same tier and modelId", failure.Message);
    }

    [Fact]
    public void Duplicate_check_ignores_letter_case_like_the_configured_model_ids()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = PriceFile(workspace, Row(modelId: Quote("Vendor/Model-A")), Row(modelId: Quote("vendor/model-a")));

        Assert.Equal(ExitCode.InvalidArguments, LoadFails(path).ExitCode);
    }

    [Fact]
    public void A_tier_in_another_letter_case_is_refused_as_a_tier_before_any_duplicate_check()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = PriceFile(workspace, Row(), Row(tier: Quote("Nebius")));

        ConfigurationException failure = LoadFails(path);

        Assert.StartsWith("prices.json: row 1: tier", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_model_on_two_tiers_is_not_a_duplicate()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = PriceFile(workspace, Row(tier: Quote("nebius")), Row(tier: Quote("nvidia")));

        PriceTable table = PriceTable.Load(path);

        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public void Find_returns_the_row_of_the_tier_and_model()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        PriceTable table = PriceTable.Load(PriceFile(workspace, Row(), Row(modelId: Quote("vendor/other"), inputUsdPer1M: "1.5")));

        PriceRow? row = table.Find(Tier, "vendor/other");

        Assert.NotNull(row);
        Assert.Equal(1.5, row.InputUsdPer1M);
        Assert.Equal(0.24, row.OutputUsdPer1M);
        Assert.Equal(new DateOnly(2026, 10, 22), row.RetrievedDate);
    }

    [Fact]
    public void Find_ignores_letter_case_of_tier_and_model()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        PriceTable table = PriceTable.Load(PriceFile(workspace, Row()));

        Assert.NotNull(table.Find("NEBIUS", "VENDOR/MODEL-A"));
    }

    [Fact]
    public void Find_returns_null_for_a_model_or_a_tier_without_a_row()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        PriceTable table = PriceTable.Load(PriceFile(workspace, Row()));

        Assert.Null(table.Find(Tier, "vendor/unpriced"));
        Assert.Null(table.Find("nvidia", ModelId));
        Assert.Null(table.Find("nebius|vendor", "model-a"));
    }

    [Fact]
    public void An_empty_array_is_a_table_without_rows()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        PriceTable table = PriceTable.Load(workspace.WriteFile("prices.json", "[]"));

        Assert.Empty(table.Rows);
        Assert.Null(table.Find(Tier, ModelId));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("not json")]
    [InlineData("")]
    public void A_file_that_is_not_an_array_of_row_objects_exits_2(string content)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        ConfigurationException failure = LoadFails(workspace.WriteFile("prices.json", content));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
    }

    [Fact]
    public void A_missing_file_exits_2_naming_the_file_and_not_its_folder()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Path.Combine(workspace.Root, "absent-prices.json");

        ConfigurationException failure = LoadFails(path);

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("absent-prices.json: the price file does not exist", failure.Message);
    }

    [Fact]
    public void A_file_of_exactly_1_MiB_is_read()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string content = "[" + new string(' ', PriceTable.MaxFileBytes - 2) + "]";

        PriceTable table = PriceTable.Load(workspace.WriteFile("prices.json", content));

        Assert.Empty(table.Rows);
    }

    [Fact]
    public void A_file_of_1_MiB_and_one_byte_is_refused()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string content = "[" + new string(' ', PriceTable.MaxFileBytes - 1) + "]";

        ConfigurationException failure = LoadFails(workspace.WriteFile("prices.json", content));

        Assert.Equal("prices.json: the price file is larger than 1 MiB", failure.Message);
    }

    [Fact]
    public void A_file_that_is_not_valid_UTF8_is_refused()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Path.Combine(workspace.Root, "prices.json");
        File.WriteAllBytes(path, [0xFF]);

        ConfigurationException failure = LoadFails(path);

        Assert.Equal("prices.json: the price file is not valid UTF-8", failure.Message);
    }

    // ---- AJ-071: the hosted cost ----

    public static TheoryData<string, string?, string?, long, long> UnrepresentableProducts => new()
    {
        { "denormal input price", "5E-324", "0.24", 1_000_000, 500_000 },
        { "denormal output price", "0.06", "5E-324", 1_000_000, 500_000 },
        { "input price above what a decimal holds", "1E+300", "0.24", 1_000_000, 500_000 },
        { "output price above what a decimal holds", "0.06", "1E+300", 1_000_000, 500_000 },
        { "price of the largest double", "1.7976931348623157E+308", "0.24", 1, 1 },
        { "product below the smallest decimal", "1E-25", "1E-25", 1, 1 },
        { "one count zero and the other product below the smallest decimal", "0.06", "1E-25", 0, 1 },
        { "product above what a decimal holds", "1E+22", "1E+22", long.MaxValue, long.MaxValue },
    };

    private static CostCalculator CalculatorOf(TempWorkspace workspace, params string[] rows) => new(PriceTable.Load(PriceFile(workspace, rows)), CostFixtures.ShippedTiers());

    private static Metric Cost(CostCalculator calculator, RunTier tier, string modelId, TokenCounts tokens, string agent = "Writer") =>
        calculator.Compute(CostFixtures.Identity(tier, modelId, agent), tokens, CostFixtures.HostedEnergy(), 1.0);

    private static void AssertNotApplicable(Metric cost, string reason)
    {
        Assert.Equal(MetricLabel.NotApplicable, cost.Label);
        Assert.Equal(reason, cost.Reason);
        Assert.Null(cost.Value);
        Assert.Null(cost.Currency);
    }

    private static IHost BuildHost(string workingDirectory) =>
        AgentJoulesHostFactory.Build(null, null, new CliHostOverrides(WorkingDirectory: workingDirectory), new KeyRegistry(new SecretRedactor()), TextWriter.Null, TextWriter.Null);

    [Fact]
    public void Hosted_cost_example()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row(inputUsdPer1M: "0.10", outputUsdPer1M: "0.40"));

        Metric cost = Cost(calculator, RunTier.Nebius, ModelId, TokenCounts.From(1_000_000, 500_000));

        Assert.Equal(Metric.Estimated(0.30, "USD"), cost);
        Assert.Equal(0.30, cost.Value);
        Assert.Equal(MetricLabel.Estimated, cost.Label);
        Assert.Equal("USD", cost.Currency);
    }

    [Theory]
    [InlineData("0.1", "0.2", 1_000_000L, 1_000_000L, 0.3)]
    [InlineData("0.06", "0.24", 1L, 1L, 3E-07)]
    [InlineData("0.06", "0.24", 1_000_000L, 0L, 0.06)]
    [InlineData("3", "15", 123_456L, 7_890L, 0.488718)]
    // Under double arithmetic these two give 3.0000000000000004E-07.
    [InlineData("0.1", "0.2", 1L, 1L, 3E-07)]
    [InlineData("0.1", "0.7", 3L, 0L, 3E-07)]
    public void The_product_is_computed_in_decimal_so_no_binary_residue_reaches_the_cost(string input, string output, long inputCount, long outputCount, double expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row(inputUsdPer1M: input, outputUsdPer1M: output));

        Metric cost = Cost(calculator, RunTier.Nebius, ModelId, TokenCounts.From(inputCount, outputCount));

        Assert.Equal(expected, cost.Value);
        Assert.Equal(MetricLabel.Estimated, cost.Label);
    }

    [Fact]
    public void Nvidia_without_row_is_NA_not_zero()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row());

        Metric cost = Cost(calculator, RunTier.Nvidia, ModelId, TokenCounts.From(1_000_000, 500_000));

        Assert.Equal(Metric.NotApplicable("free-tier-no-price"), cost);
        AssertNotApplicable(cost, CostFixtures.FreeTierReason);
    }

    [Theory]
    [InlineData("vendor/unpriced")]
    [InlineData("vendor/model-a-v2")]
    [InlineData("vendor/model")]
    public void Nebius_without_row_is_NA_with_the_default_reason(string model)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row());

        AssertNotApplicable(Cost(calculator, RunTier.Nebius, model, TokenCounts.From(10, 10)), CostFixtures.DefaultMissingReason);
    }

    [Fact]
    public void A_row_of_another_tier_does_not_price_the_model()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row(tier: Quote("nvidia")));

        AssertNotApplicable(Cost(calculator, RunTier.Nebius, ModelId, TokenCounts.From(10, 10)), CostFixtures.DefaultMissingReason);
        Assert.Equal(MetricLabel.Estimated, Cost(calculator, RunTier.Nvidia, ModelId, TokenCounts.From(10, 10)).Label);
    }

    [Fact]
    public void The_missing_row_reason_is_the_one_configured_for_the_tier()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Dictionary<string, TierOptions> tiers = CostFixtures.ShippedTiers();
        tiers["nebius"].MissingPriceReason = "contract-price-pending";
        tiers["nvidia"].MissingPriceReason = "   ";
        var calculator = new CostCalculator(PriceTable.Load(PriceFile(workspace, Row(modelId: Quote("vendor/priced")))), tiers);

        AssertNotApplicable(Cost(calculator, RunTier.Nebius, ModelId, TokenCounts.From(1, 1)), "contract-price-pending");
        AssertNotApplicable(Cost(calculator, RunTier.Nvidia, ModelId, TokenCounts.From(1, 1)), CostFixtures.DefaultMissingReason);
    }

    [Fact]
    public void A_tier_without_configuration_gets_the_default_reason_and_a_later_change_of_the_options_is_not_seen()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Dictionary<string, TierOptions> tiers = new(StringComparer.OrdinalIgnoreCase) { ["nvidia"] = new TierOptions { MissingPriceReason = "free-tier-no-price" } };
        var calculator = new CostCalculator(PriceTable.Load(PriceFile(workspace)), tiers);
        tiers["nvidia"].MissingPriceReason = "changed-after-construction";

        AssertNotApplicable(Cost(calculator, RunTier.Nebius, ModelId, TokenCounts.From(1, 1)), CostFixtures.DefaultMissingReason);
        AssertNotApplicable(Cost(calculator, RunTier.Nvidia, ModelId, TokenCounts.From(1, 1)), CostFixtures.FreeTierReason);
    }

    [Theory]
    [InlineData(null, 500_000L)]
    [InlineData(1_000_000L, null)]
    [InlineData(null, null)]
    [InlineData(-1L, 500_000L)]
    [InlineData(1_000_000L, -1L)]
    public void A_step_with_either_token_count_not_reported_is_NA_tokens_not_reported(long? input, long? output)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row());

        Metric cost = Cost(calculator, RunTier.Nebius, ModelId, TokenCounts.From(input, output));

        AssertNotApplicable(cost, "tokens-not-reported");
    }

    [Fact]
    public void A_step_without_any_usage_is_NA_tokens_not_reported()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row());

        AssertNotApplicable(Cost(calculator, RunTier.Nebius, ModelId, TokenCounts.NotReported), "tokens-not-reported");
    }

    [Fact]
    public void The_missing_row_is_checked_before_the_token_counts_so_a_free_tier_never_reports_a_price_problem()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row());

        AssertNotApplicable(Cost(calculator, RunTier.Nvidia, ModelId, TokenCounts.NotReported), CostFixtures.FreeTierReason);
        AssertNotApplicable(Cost(calculator, RunTier.Nebius, "vendor/unpriced", TokenCounts.From(null, 5)), CostFixtures.DefaultMissingReason);
    }

    [Fact]
    public void A_stated_count_of_zero_is_a_count_and_costs_an_estimated_zero_not_NA()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row());

        Metric cost = Cost(calculator, RunTier.Nebius, ModelId, TokenCounts.From(0, 0));

        Assert.Equal(Metric.Estimated(0, "USD"), cost);
    }

    [Theory]
    [MemberData(nameof(UnrepresentableProducts))]
    public void A_hosted_product_that_is_not_finite_or_not_above_0_is_NA_price_not_representable(string description, string? input, string? output, long inputCount, long outputCount)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string row = Row(inputUsdPer1M: input, outputUsdPer1M: output);
        CostCalculator calculator = CalculatorOf(workspace, row);

        // The schema accepts these prices: they are finite and above 0 as doubles; the cost is where they stop being usable.
        Assert.True(Evaluate("[" + row + "]").IsValid, description);
        Metric cost = Cost(calculator, RunTier.Nebius, ModelId, TokenCounts.From(inputCount, outputCount));

        AssertNotApplicable(cost, "price-not-representable");
    }

    [Fact]
    public void Each_agent_model_is_priced_from_its_own_row_when_AgentModelIds_overrides_the_tier_model()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(
            workspace,
            Row(modelId: Quote("vendor/model-a"), inputUsdPer1M: "0.10", outputUsdPer1M: "0.40"),
            Row(modelId: Quote("vendor/model-b"), inputUsdPer1M: "1.00", outputUsdPer1M: "2.00"));
        var tier = new TierOptions { ModelId = "vendor/model-a", AgentModelIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["writer"] = "vendor/model-b" } };
        TokenCounts tokens = TokenCounts.From(1_000_000, 1_000_000);

        Metric writer = Cost(calculator, RunTier.Nebius, tier.ResolveModelId("Writer"), tokens, "Writer");
        Metric planner = Cost(calculator, RunTier.Nebius, tier.ResolveModelId("Planner"), tokens, "Planner");

        Assert.Equal(Metric.Estimated(3.00, "USD"), writer);
        Assert.Equal(Metric.Estimated(0.50, "USD"), planner);
    }

    [Fact]
    public void The_model_id_is_matched_without_regard_to_letter_case()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row(inputUsdPer1M: "0.10", outputUsdPer1M: "0.40"));

        Metric cost = Cost(calculator, RunTier.Nebius, "VENDOR/MODEL-A", TokenCounts.From(1_000_000, 500_000));

        Assert.Equal(Metric.Estimated(0.30, "USD"), cost);
    }

    [Theory]
    [InlineData(RunTier.Local, CostCalculator.EnergyNotMeasuredReason)]
    [InlineData(RunTier.CloudGpu, CostCalculator.HourlyPriceNotConfiguredReason)]
    public void The_measured_tiers_answer_with_their_own_reason_and_no_longer_cost_not_configured(RunTier tier, string reason)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row(tier: Quote(TierOptions.NameOf(tier))));

        // The hosted-tier window has no energy and a default calculator has no hourly price: each branch names what is missing (AJ-072 replaced the AJ-071 placeholder).
        AssertNotApplicable(Cost(calculator, tier, ModelId, TokenCounts.From(1_000_000, 500_000)), reason);
    }

    [Fact]
    public void The_calculator_checks_its_arguments()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        PriceTable table = PriceTable.Load(PriceFile(workspace, Row()));
        CostCalculator calculator = new(table, CostFixtures.ShippedTiers());
        var identity = CostFixtures.Identity(RunTier.Nebius, ModelId);
        EnergyReading energy = CostFixtures.HostedEnergy();

        _ = Assert.Throws<ArgumentNullException>(() => new CostCalculator(null!, CostFixtures.ShippedTiers()));
        _ = Assert.Throws<ArgumentNullException>(() => new CostCalculator(table, null!));
        _ = Assert.Throws<ArgumentNullException>(() => calculator.Compute(null!, TokenCounts.NotReported, energy, 1.0));
        _ = Assert.Throws<ArgumentNullException>(() => calculator.Compute(identity, null!, energy, 1.0));
        _ = Assert.Throws<ArgumentNullException>(() => calculator.Compute(identity, TokenCounts.NotReported, null!, 1.0));
    }

    [Fact]
    public void One_calculator_answers_the_same_from_many_threads()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = CalculatorOf(workspace, Row(inputUsdPer1M: "0.10", outputUsdPer1M: "0.40"));
        EnergyReading energy = CostFixtures.HostedEnergy();
        var priced = CostFixtures.Identity(RunTier.Nebius, ModelId);
        var unpriced = CostFixtures.Identity(RunTier.Nvidia, ModelId);
        TokenCounts tokens = TokenCounts.From(1_000_000, 500_000);

        Metric[] costs = new Metric[4000];
        _ = Parallel.For(0, costs.Length, index => costs[index] = calculator.Compute(index % 2 == 0 ? priced : unpriced, tokens, energy, 1.0));

        Assert.All(costs.Where((_, index) => index % 2 == 0), cost => Assert.Equal(Metric.Estimated(0.30, "USD"), cost));
        Assert.All(costs.Where((_, index) => index % 2 == 1), cost => Assert.Equal(Metric.NotApplicable(CostFixtures.FreeTierReason), cost));
    }

    [Fact]
    public void The_host_registers_CostCalculator_and_every_cost_it_returns_is_estimated_with_a_currency_or_NA()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        using IHost host = BuildHost(workspace.Root);
        string pricedModel = PriceTable.Load(RepositoryFile("config", "prices.json")).Rows[0].ModelId;
        EnergyReading energy = CostFixtures.HostedEnergy();

        ICostCalculator calculator = host.Services.GetRequiredService<ICostCalculator>();

        _ = Assert.IsType<CostCalculator>(calculator);
        Assert.Same(calculator, host.Services.GetRequiredService<ICostCalculator>());
        int estimated = 0;
        foreach (RunTier tier in Enum.GetValues<RunTier>())
        {
            foreach (string model in new[] { pricedModel, "vendor/unpriced" })
            {
                foreach (TokenCounts tokens in new[] { TokenCounts.NotReported, TokenCounts.From(0, 0), TokenCounts.From(1_000_000, 2_000_000), TokenCounts.From(5, null) })
                {
                    Metric cost = calculator.Compute(CostFixtures.Identity(tier, model), tokens, energy, 1.0);

                    Assert.NotEqual(MetricLabel.Measured, cost.Label);
                    if (cost.Label == MetricLabel.Estimated)
                    {
                        estimated++;
                        Assert.Equal("USD", cost.Currency);
                        Assert.Null(cost.Unit);
                        Assert.NotNull(cost.Value);
                    }
                    else
                    {
                        Assert.Equal(MetricLabel.NotApplicable, cost.Label);
                        Assert.False(string.IsNullOrWhiteSpace(cost.Reason));
                        Assert.Null(cost.Value);
                    }
                }
            }
        }

        // Only the committed nebius row with both counts reported is priced: two of the 32 combinations (zero tokens and a million tokens).
        Assert.Equal(2, estimated);
    }

    [Fact]
    public void The_host_takes_the_missing_row_reasons_from_the_shipped_configuration()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        using IHost host = BuildHost(workspace.Root);
        ICostCalculator calculator = host.Services.GetRequiredService<ICostCalculator>();
        EnergyReading energy = CostFixtures.HostedEnergy();
        TokenCounts tokens = TokenCounts.From(1_000_000, 500_000);
        string pricedModel = PriceTable.Load(RepositoryFile("config", "prices.json")).Rows[0].ModelId;

        Metric nvidia = calculator.Compute(CostFixtures.Identity(RunTier.Nvidia, pricedModel), tokens, energy, 1.0);
        Metric nebius = calculator.Compute(CostFixtures.Identity(RunTier.Nebius, "vendor/unpriced"), tokens, energy, 1.0);
        Metric priced = calculator.Compute(CostFixtures.Identity(RunTier.Nebius, pricedModel), tokens, energy, 1.0);

        Assert.Equal(Metric.NotApplicable("free-tier-no-price"), nvidia);
        Assert.Equal(Metric.NotApplicable("price-not-found"), nebius);
        Assert.Equal(MetricLabel.Estimated, priced.Label);
        Assert.Equal("USD", priced.Currency);
    }

    [Fact]
    public void Building_the_host_never_reads_the_price_file_only_resolving_the_calculator_does()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        File.Delete(Path.Combine(workspace.Root, "config", "prices.json"));

        using IHost host = BuildHost(workspace.Root);
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => host.Services.GetRequiredService<ICostCalculator>());

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains("Paths:Prices", failure.Message, StringComparison.Ordinal);
        Assert.Contains("prices.json", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_price_file_that_does_not_validate_names_the_key_the_row_and_the_field_and_never_the_value()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        _ = workspace.WriteFile(Path.Combine("config", "prices.json"), "[" + Row(inputUsdPer1M: "-987.125") + "]");

        using IHost host = BuildHost(workspace.Root);
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => host.Services.GetRequiredService<ICostCalculator>());

        Assert.Equal("Paths:Prices: prices.json: row 0: inputUsdPer1M must be above 0", failure.Message);
        Assert.DoesNotContain("987.125", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_Prices_is_resolved_under_the_content_root()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        _ = workspace.WriteFile(Path.Combine("data", "my-prices.json"), "[" + Row(inputUsdPer1M: "0.10", outputUsdPer1M: "0.40") + "]");
        var overrides = new CliHostOverrides(Configuration: new Dictionary<string, string?> { ["AgentJoules:Paths:Prices"] = "data/my-prices.json" }, WorkingDirectory: workspace.Root);
        using IHost host = AgentJoulesHostFactory.Build(null, null, overrides, new KeyRegistry(new SecretRedactor()), TextWriter.Null, TextWriter.Null);

        ICostCalculator calculator = host.Services.GetRequiredService<ICostCalculator>();

        Assert.Equal(Metric.Estimated(0.30, "USD"), calculator.Compute(CostFixtures.Identity(RunTier.Nebius, ModelId), TokenCounts.From(1_000_000, 500_000), CostFixtures.HostedEnergy(), 1.0));
    }

    [Theory]
    [InlineData("../prices.json")]
    [InlineData("config/../prices.json")]
    [InlineData("..")]
    [InlineData("<absolute>")]
    public async Task A_rooted_or_climbing_Paths_Prices_exits_2_naming_only_the_key(string value)
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        string configured = value == "<absolute>" ? Path.Combine(workspace.Root, "config", "prices.json") : value;
        RunCommandSetup setup = RunCommandSetup.Create([new KeyValuePair<string, string?>("AgentJoules:Paths:Prices", configured)]);
        setup.WorkingDirectory = workspace.Root;

        CliRunResult dryRun = await RunCommandHarness.RunAsync("run --tier nvidia --dry-run", setup);
        CliRunResult measured = await RunCommandHarness.RunAsync("run --tier nvidia", setup);

        foreach (CliRunResult result in new[] { dryRun, measured })
        {
            Assert.Equal(2, result.ExitCode);
            string error = Assert.Single(result.ErrorLines);
            Assert.Contains("Paths:Prices", error, StringComparison.Ordinal);
            Assert.DoesNotContain(workspace.Root, result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFileName(workspace.Root), result.Stderr, StringComparison.OrdinalIgnoreCase);
            if (configured.Length > 2)
            {
                Assert.DoesNotContain(configured, result.Stderr, StringComparison.Ordinal);
            }
        }

        Assert.Equal(0, setup.Orchestrator.Runs);
    }

    [Fact]
    public async Task A_missing_or_invalid_price_file_exits_2_for_the_run_command_dry_run_included()
    {
        using TempWorkspace missing = RunCommandHarness.CreateWorkspace();
        File.Delete(Path.Combine(missing.Root, "config", "prices.json"));
        using TempWorkspace invalid = RunCommandHarness.CreateWorkspace();
        _ = invalid.WriteFile(Path.Combine("config", "prices.json"), "[" + Row(inputUsdPer1M: "0") + "]");

        foreach ((TempWorkspace workspace, string detail) in new[] { (missing, "the price file does not exist"), (invalid, "row 0: inputUsdPer1M must be above 0") })
        {
            foreach (string arguments in new[] { "run --tier nvidia --dry-run", "run --tier nvidia" })
            {
                RunCommandSetup setup = RunCommandSetup.Create([]);
                setup.WorkingDirectory = workspace.Root;

                CliRunResult result = await RunCommandHarness.RunAsync(arguments, setup);

                Assert.Equal(2, result.ExitCode);
                string error = Assert.Single(result.ErrorLines);
                Assert.Contains("Paths:Prices: prices.json: " + detail, error, StringComparison.Ordinal);
                Assert.DoesNotContain(workspace.Root, result.Stderr, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, setup.Orchestrator.Runs);
            }
        }
    }
    [Fact]
    public void AddAgentJoulesCost_checks_its_arguments_keeps_a_calculator_registered_first_and_registers_once()
    {
        _ = Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddAgentJoulesCost(null!, "root"));
        _ = Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentJoulesCost(null!));
        _ = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAgentJoulesCost("   "));
        _ = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAgentJoulesCost(string.Empty));

        var fixedCost = new FixedCostCalculator(Metric.NotApplicable("fixed-for-the-test"));
        var first = new ServiceCollection();
        _ = first.AddSingleton<ICostCalculator>(fixedCost);
        _ = first.AddAgentJoulesCost(Path.GetTempPath());
        using (ServiceProvider provider = first.BuildServiceProvider())
        {
            Assert.Same(fixedCost, provider.GetRequiredService<ICostCalculator>());
        }

        Assert.Single(first, descriptor => descriptor.ServiceType == typeof(ICostCalculator));
        var twice = new ServiceCollection();
        _ = twice.AddAgentJoulesCost(Path.GetTempPath()).AddAgentJoulesCost(Path.GetTempPath());
        Assert.Single(twice, descriptor => descriptor.ServiceType == typeof(ICostCalculator));
    }

    // ---- AJ-072: the local and cloudgpu cost, the emission and the assumptions ----

    public static TheoryData<double, double> UnrepresentableLocalProducts => new()
    {
        { 5E-324, 3_600_000.0 }, // denormal price
        { 1E+300, 3_600_000.0 }, // price above what a decimal holds
        { 0.30, 1E+300 }, // net above what a decimal holds
        { 1E-20, 1E-20 }, // product below the smallest decimal
        { 0.30, 5E-324 }, // denormal net, which a decimal reads as 0 although a joule was measured
    };

    private static CostCalculator LocalAndCloudCalculator(TempWorkspace workspace, CostOptions? cost = null, CloudGpuOptions? cloudGpu = null) =>
        new(PriceTable.Load(PriceFile(workspace)), CostFixtures.ShippedTiers(), cost, cloudGpu);

    private static Metric LocalCost(CostCalculator calculator, EnergyReading energy) =>
        calculator.Compute(CostFixtures.Identity(RunTier.Local, "local/model"), TokenCounts.From(10, 20), energy, 1.0);

    private static Metric CloudCost(CostCalculator calculator, double wallSeconds, EnergyReading? energy = null) =>
        calculator.Compute(CostFixtures.Identity(RunTier.CloudGpu, "cloud/model"), TokenCounts.From(10, 20), energy ?? CostFixtures.LocalEnergy(100.0), wallSeconds);

    private static Totals TotalsOf(IReadOnlyList<StepReading> steps, double? factor, RunTier tier = RunTier.Local, RunMode mode = RunMode.Serialized) =>
        RunAssembler.ComputeTotals(tier, mode, steps, [], wallClockMs: 10.0, cost: new CostOptions { GCo2ePerKwh = factor });

    [Fact]
    public void Local_cost_example()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = LocalAndCloudCalculator(workspace, new CostOptions { ElectricityPricePerKwh = 0.30, Currency = "EUR" });

        Metric cost = LocalCost(calculator, CostFixtures.LocalEnergy(3_600_000.0));

        // Metric equality covers the value, the label, the currency and the absence of a reason.
        Assert.Equal(Metric.Estimated(0.30, "EUR"), cost);
    }

    [Fact]
    public void The_local_cost_defaults_to_30_cents_in_EUR_when_no_options_are_given()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        Metric cost = LocalCost(LocalAndCloudCalculator(workspace), CostFixtures.LocalEnergy(3_600_000.0));

        Assert.Equal(Metric.Estimated(0.30, "EUR"), cost);
    }

    [Theory]
    [InlineData(1_800_000.0, 0.30, 0.15)]
    [InlineData(3_600_000.0, 0.10, 0.10)]
    [InlineData(36_000_000.0, 0.30, 3.0)]
    [InlineData(7_200_000.0, 0.28, 0.56)]
    [InlineData(360_000.0, 0.70, 0.07)]
    public void The_local_cost_is_net_kilowatt_hours_times_the_price_in_decimal_so_no_binary_residue_reaches_the_cost(double netJoules, double price, double expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = LocalAndCloudCalculator(workspace, new CostOptions { ElectricityPricePerKwh = price });

        Metric cost = LocalCost(calculator, CostFixtures.LocalEnergy(netJoules));

        Assert.True(cost.Value == expected, $"Expected exactly {expected:R}, got {cost.Value:R}.");
        Assert.Equal("EUR", cost.Currency);
    }

    [Fact]
    public void The_price_and_the_currency_come_from_the_cost_options_and_not_from_the_tokens()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = LocalAndCloudCalculator(workspace, new CostOptions { ElectricityPricePerKwh = 0.25, Currency = "SEK" });

        Metric withTokens = LocalCost(calculator, CostFixtures.LocalEnergy(7_200_000.0));
        Metric withoutTokens = calculator.Compute(CostFixtures.Identity(RunTier.Local, "local/model"), TokenCounts.NotReported, CostFixtures.LocalEnergy(7_200_000.0), 1.0);

        Assert.Equal(Metric.Estimated(0.50, "SEK"), withTokens);
        Assert.Equal(withTokens, withoutTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_local_step_with_energy_not_available_is_NA_energy_not_measured(bool notReported)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = LocalAndCloudCalculator(workspace);

        Metric cost = LocalCost(calculator, CostFixtures.LocalEnergyAbsent("counter-and-sampling-unavailable", notReported));

        AssertNotApplicable(cost, CostCalculator.EnergyNotMeasuredReason);
    }

    [Fact]
    public void A_below_baseline_step_is_NA_below_baseline_and_its_negative_joules_stay_in_the_reading()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = LocalAndCloudCalculator(workspace);
        EnergyReading energy = CostFixtures.LocalEnergy(-60.0);

        Metric cost = LocalCost(calculator, energy);

        AssertNotApplicable(cost, CostCalculator.BelowBaselineReason);
        // Structural: EnergyReading is an immutable record the calculator cannot change, and its constructor ties the negative net to the flag.
        Assert.Equal(-60.0, energy.NetJoules.Value);
    }

    [Fact]
    public void A_measured_net_of_exactly_zero_costs_an_estimated_zero_not_NA()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        Metric cost = LocalCost(LocalAndCloudCalculator(workspace), CostFixtures.LocalEnergy(0.0));

        Assert.Equal(Metric.Estimated(0.0, "EUR"), cost);
    }

    [Theory]
    [MemberData(nameof(UnrepresentableLocalProducts))]
    public void A_local_product_that_a_decimal_cannot_hold_or_that_is_0_for_a_measured_joule_is_NA_price_not_representable(double price, double netJoules)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = LocalAndCloudCalculator(workspace, new CostOptions { ElectricityPricePerKwh = price });

        Metric cost = LocalCost(calculator, CostFixtures.LocalEnergy(netJoules));

        AssertNotApplicable(cost, CostCalculator.PriceNotRepresentableReason);
    }

    [Fact]
    public void Cloudgpu_cost_example()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = LocalAndCloudCalculator(workspace, cloudGpu: new CloudGpuOptions { HourlyPriceUsd = 3.60 });

        Metric cost = CloudCost(calculator, 1000.0);

        Assert.Equal(Metric.Estimated(1.00, "USD"), cost);
    }

    [Theory]
    [InlineData(3.60, 1000.0, 1.00)]
    [InlineData(0.10, 36.0, 0.001)]
    [InlineData(2.5, 3600.0, 2.5)]
    [InlineData(3.60, 0.0, 0.0)]
    [InlineData(1.10, 9.0, 0.00275)]
    public void The_cloudgpu_cost_is_the_hourly_price_times_wall_seconds_over_3600_in_decimal(double hourly, double wallSeconds, double expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = LocalAndCloudCalculator(workspace, cloudGpu: new CloudGpuOptions { HourlyPriceUsd = hourly });

        Metric cost = CloudCost(calculator, wallSeconds);

        Assert.True(cost.Value == expected, $"Expected exactly {expected:R}, got {cost.Value:R}.");
        Assert.Equal(MetricLabel.Estimated, cost.Label);
        Assert.Equal("USD", cost.Currency);
    }

    [Fact]
    public void The_cloudgpu_cost_is_in_USD_whatever_the_electricity_currency_and_applies_no_electricity_model_to_the_energy()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = LocalAndCloudCalculator(workspace, new CostOptions { ElectricityPricePerKwh = 99.0, Currency = "SEK" }, new CloudGpuOptions { HourlyPriceUsd = 3.60 });

        Metric measured = CloudCost(calculator, 1000.0, CostFixtures.LocalEnergy(36_000_000.0));
        Metric unavailable = CloudCost(calculator, 1000.0, CostFixtures.LocalEnergyAbsent("counter-and-sampling-unavailable"));
        Metric belowBaseline = CloudCost(calculator, 1000.0, CostFixtures.LocalEnergy(-60.0));

        Assert.All([measured, unavailable, belowBaseline], cost => Assert.Equal(Metric.Estimated(1.00, "USD"), cost));
    }

    [Fact]
    public void An_unset_hourly_price_gives_NA_hourly_price_not_configured()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        AssertNotApplicable(CloudCost(LocalAndCloudCalculator(workspace), 1000.0), CostCalculator.HourlyPriceNotConfiguredReason);
        AssertNotApplicable(CloudCost(LocalAndCloudCalculator(workspace, cloudGpu: new CloudGpuOptions()), 1000.0), CostCalculator.HourlyPriceNotConfiguredReason);
    }

    [Theory]
    [InlineData(0.0, 1000.0)]
    [InlineData(-3.6, 1000.0)]
    [InlineData(double.NaN, 1000.0)]
    [InlineData(5E-324, 1000.0)]
    [InlineData(1E+300, 1000.0)]
    [InlineData(3.6, -1.0)]
    [InlineData(3.6, double.NaN)]
    [InlineData(3.6, double.PositiveInfinity)]
    [InlineData(3.6, 1E+300)]
    public void A_cloudgpu_product_that_is_not_finite_negative_or_not_representable_is_NA_price_not_representable(double hourly, double wallSeconds)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = LocalAndCloudCalculator(workspace, cloudGpu: new CloudGpuOptions { HourlyPriceUsd = hourly });

        AssertNotApplicable(CloudCost(calculator, wallSeconds), CostCalculator.PriceNotRepresentableReason);
    }

    [Fact]
    public void The_options_are_copied_at_construction_so_a_later_change_is_not_seen()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        var cost = new CostOptions { ElectricityPricePerKwh = 0.30, Currency = "EUR" };
        var cloudGpu = new CloudGpuOptions { HourlyPriceUsd = 3.60 };
        CostCalculator calculator = LocalAndCloudCalculator(workspace, cost, cloudGpu);

        cost.ElectricityPricePerKwh = 9.0;
        cost.Currency = "SEK";
        cloudGpu.HourlyPriceUsd = null;

        Assert.Equal(Metric.Estimated(0.30, "EUR"), LocalCost(calculator, CostFixtures.LocalEnergy(3_600_000.0)));
        Assert.Equal(Metric.Estimated(1.00, "USD"), CloudCost(calculator, 1000.0));
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("")]
    [InlineData("eur")]
    [InlineData("EURO")]
    public void A_currency_that_is_not_a_code_is_refused_when_the_calculator_is_built_naming_the_parameter_and_not_the_value(string currency)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        ArgumentException failure = Assert.Throws<ArgumentException>(() => LocalAndCloudCalculator(workspace, new CostOptions { Currency = currency }));

        Assert.Equal("cost", failure.ParamName);
        Assert.DoesNotContain("eur", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_hosted_branch_is_unchanged_by_the_cost_options()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        var calculator = new CostCalculator(
            PriceTable.Load(PriceFile(workspace, Row(inputUsdPer1M: "0.10", outputUsdPer1M: "0.40"))),
            CostFixtures.ShippedTiers(),
            new CostOptions { ElectricityPricePerKwh = 5.0, Currency = "SEK" },
            new CloudGpuOptions { HourlyPriceUsd = 50.0 });

        Assert.Equal(Metric.Estimated(0.30, "USD"), Cost(calculator, RunTier.Nebius, ModelId, TokenCounts.From(1_000_000, 500_000)));
        AssertNotApplicable(Cost(calculator, RunTier.Nvidia, ModelId, TokenCounts.From(1_000_000, 500_000)), CostFixtures.FreeTierReason);
    }

    [Fact]
    public void Every_measured_tier_cost_is_estimated_with_a_currency_or_NA_never_measured_or_zero_for_a_missing_value()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        CostCalculator calculator = LocalAndCloudCalculator(workspace, cloudGpu: new CloudGpuOptions { HourlyPriceUsd = 3.60 });
        EnergyReading[] energies = [CostFixtures.LocalEnergy(3_600_000.0), CostFixtures.LocalEnergy(-1.0), CostFixtures.LocalEnergy(0.0), CostFixtures.LocalEnergyAbsent("x"), CostFixtures.LocalEnergyAbsent("x", notReported: true)];

        foreach (RunTier tier in new[] { RunTier.Local, RunTier.CloudGpu })
        {
            foreach (EnergyReading energy in energies)
            {
                Metric cost = calculator.Compute(CostFixtures.Identity(tier, "m"), TokenCounts.From(1, 1), energy, 10.0);

                Assert.True(cost.Label is MetricLabel.Estimated or MetricLabel.NotApplicable, $"{tier}: {cost.Label}");
                Assert.Equal(cost.Label == MetricLabel.Estimated, cost.Currency is not null);
                Assert.Equal(cost.Label == MetricLabel.NotApplicable, cost.Value is null);
            }
        }
    }

    [Fact]
    public void The_host_hands_the_cost_and_cloudgpu_options_to_the_calculator()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        _ = workspace.WriteFile(Path.Combine("data", "my-prices.json"), "[]");
        var overrides = new CliHostOverrides(
            Configuration: new Dictionary<string, string?>
            {
                ["AgentJoules:Paths:Prices"] = "data/my-prices.json",
                ["AgentJoules:Cost:ElectricityPricePerKwh"] = "0.25",
                ["AgentJoules:Cost:Currency"] = "SEK",
                ["AgentJoules:CloudGpu:HourlyPriceUsd"] = "7.20",
            },
            WorkingDirectory: workspace.Root);
        using IHost host = AgentJoulesHostFactory.Build(null, null, overrides, new KeyRegistry(new SecretRedactor()), TextWriter.Null, TextWriter.Null);
        ICostCalculator calculator = host.Services.GetRequiredService<ICostCalculator>();

        Metric local = calculator.Compute(CostFixtures.Identity(RunTier.Local, "m"), TokenCounts.From(1, 1), CostFixtures.LocalEnergy(7_200_000.0), 1.0);
        Metric cloud = calculator.Compute(CostFixtures.Identity(RunTier.CloudGpu, "m"), TokenCounts.From(1, 1), CostFixtures.LocalEnergy(7_200_000.0), 500.0);

        Assert.Equal(Metric.Estimated(0.50, "SEK"), local);
        Assert.Equal(Metric.Estimated(1.00, "USD"), cloud);
    }

    [Fact]
    public void Gco2e_is_NA_not_configured_when_no_factor_is_set()
    {
        StepReading[] steps = [StepReadingFixtures.Metered(netJoules: Metric.Measured(3_600_000.0, "J"))];

        foreach (Totals totals in new[] { TotalsOf(steps, null), RunAssembler.ComputeTotals(RunTier.Local, RunMode.Serialized, steps, [], wallClockMs: 10.0) })
        {
            AssertNotApplicable(totals.Gco2e, "not-configured");
        }
    }

    [Theory]
    [InlineData(3_600_000.0, 400.0, 400.0)]
    [InlineData(1_800_000.0, 400.0, 200.0)]
    [InlineData(7_200_000.0, 475.0, 950.0)]
    [InlineData(360_000.0, 400.5, 40.05)]
    [InlineData(360_000.0, 0.7, 0.07)]
    public void Gco2e_is_net_kilowatt_hours_times_the_factor_labelled_estimated(double netJoules, double factor, double expected)
    {
        Totals totals = TotalsOf([StepReadingFixtures.Metered(netJoules: Metric.Measured(netJoules, "J"))], factor);

        Assert.Equal(Metric.EstimatedQuantity(expected, "gCO2e"), totals.Gco2e);
    }

    [Fact]
    public void Gco2e_uses_the_summed_net_energy_of_all_steps()
    {
        StepReading[] steps =
        [
            StepReadingFixtures.Metered("Planner", netJoules: Metric.Measured(1_800_000.0, "J")),
            StepReadingFixtures.Metered("Writer", netJoules: Metric.Measured(1_800_000.0, "J")),
        ];

        Assert.Equal(Metric.EstimatedQuantity(400.0, "gCO2e"), TotalsOf(steps, 400.0).Gco2e);
    }

    [Fact]
    public void Gco2e_is_NA_energy_not_measured_when_the_net_total_does_not_exist()
    {
        StepReading[] notReported = [StepReadingFixtures.Metered(netJoules: Metric.NotReported("counter-and-sampling-unavailable"))];
        StepReading[] notApplicable = [StepReadingFixtures.Metered(netJoules: Metric.NotApplicable("counter-and-sampling-unavailable"))];
        StepReading[] concurrent = [StepReadingFixtures.Metered(), StepReadingFixtures.Metered()];

        AssertNotApplicable(TotalsOf(notReported, 400.0).Gco2e, CostCalculator.EnergyNotMeasuredReason);
        AssertNotApplicable(TotalsOf(notApplicable, 400.0).Gco2e, CostCalculator.EnergyNotMeasuredReason);
        AssertNotApplicable(TotalsOf([], 400.0).Gco2e, CostCalculator.EnergyNotMeasuredReason);

        // A concurrent run on a measured tier without a union function: the net total is not-reported union-not-computed.
        AssertNotApplicable(TotalsOf(concurrent, 400.0, mode: RunMode.Concurrent).Gco2e, CostCalculator.EnergyNotMeasuredReason);
    }

    [Fact]
    public async Task Gco2e_of_a_hosted_tier_is_NA_energy_not_measured_even_with_a_factor()
    {
        StepReading hosted = await StepReadingFixtures.HostedAsync(RunTier.Nvidia);

        AssertNotApplicable(TotalsOf([hosted], 400.0, RunTier.Nvidia).Gco2e, CostCalculator.EnergyNotMeasuredReason);
    }

    [Fact]
    public void Gco2e_of_a_negative_net_total_is_NA_below_baseline_and_a_zero_net_total_is_an_estimated_zero()
    {
        StepReading negative = StepReadingFixtures.Metered(netJoules: Metric.Measured(-60.0, "J"), flags: ReadingFlags.BelowBaseline);
        StepReading zero = StepReadingFixtures.Metered(netJoules: Metric.Measured(0.0, "J"));

        AssertNotApplicable(TotalsOf([negative], 400.0).Gco2e, CostCalculator.BelowBaselineReason);
        Assert.Equal(Metric.EstimatedQuantity(0.0, "gCO2e"), TotalsOf([zero], 400.0).Gco2e);
    }

    [Theory]
    [InlineData(1E+300, 3_600_000.0)]
    [InlineData(400.0, 1E+300)]
    [InlineData(5E-324, 3_600_000.0)]
    public void A_gco2e_product_that_a_decimal_cannot_hold_is_NA_price_not_representable(double factor, double netJoules)
    {
        Totals totals = TotalsOf([StepReadingFixtures.Metered(netJoules: Metric.Measured(netJoules, "J"))], factor);

        AssertNotApplicable(totals.Gco2e, CostCalculator.PriceNotRepresentableReason);
    }

    [Fact]
    public void The_assumptions_are_the_labelled_price_and_a_factor_that_is_estimated_or_NA_not_configured()
    {
        EnvironmentInfo defaults = RunAssembler.BuildEnvironment(RunTier.Local, new TierOptions { ModelId = "m" }, gpu: null, runtime: null, modelLoad: null);
        EnvironmentInfo configured = RunAssembler.BuildEnvironment(
            RunTier.Nebius,
            new TierOptions { ModelId = "m" },
            gpu: null,
            runtime: null,
            modelLoad: null,
            new CostOptions { ElectricityPricePerKwh = 0.25, Currency = "SEK", GCo2ePerKwh = 400.0 });

        Assert.Equal(Metric.Estimated(0.30, "EUR"), defaults.Assumptions.ElectricityPrice);
        AssertNotApplicable(defaults.Assumptions.Gco2ePerKwh, "not-configured");
        Assert.Equal(Metric.Estimated(0.25, "SEK"), configured.Assumptions.ElectricityPrice);
        Assert.Equal(Metric.EstimatedQuantity(400.0, "gCO2e/kWh"), configured.Assumptions.Gco2ePerKwh);
    }
}