using AgentJoules.Core.Cli;
using AgentJoules.Core.Cost;
using AgentJoules.TestSupport;

using Json.Schema;

using Xunit;

using static AgentJoules.TestSupport.PriceFileFixtures;

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
}