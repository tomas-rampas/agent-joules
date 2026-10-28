using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// The hosted-replay spec (specs/hosted-replay.md): the two committed event captures under docs/replay/, the replay page docs/replay/index.html, its links from the landing page and the README, and the records. Every test
/// reads committed files; none runs the CLI.
/// </summary>
[Trait("Spec", "hosted-replay")]
public sealed partial class HostedReplayTests
{
    private const string RepositoryLink = "https://github.com/tomas-rampas/agent-joules";

    private static readonly (string File, string RunId)[] Captures =
    [
        ("events-cloudgpu.sse", "20261026T124721Z-cloudgpu"),
        ("events-local.sse", "20261027T095519Z-local"),
    ];

    private static readonly string[] CountWords = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    private static readonly string[] PageTexts = ["1x", "4x", "8x", "replay", "events-cloudgpu.sse", "events-local.sse"];

    private static readonly string[] SelectorTexts = ["NVIDIA H100 80GB HBM3", "nvidia/nemotron-3-nano", "NVIDIA GeForce RTX 3080 Ti Laptop GPU", "nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1", "NIM"];

    private static readonly string[] CspDirectives = ["default-src 'none'", "connect-src 'self'", "img-src 'none'", "base-uri 'none'", "form-action 'none'", "script-src 'sha256-", "style-src 'sha256-"];

    private static readonly string[] ActionMarkers = ["1. ", "2. ", "3. ", "4. "];

    [GeneratedRegex("^[0-9]+\\. ", RegexOptions.CultureInvariant)]
    private static partial Regex ListMarker();

    [GeneratedRegex("\"atUtc\":\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AtUtc();

    [GeneratedRegex("\"watts\":\\{\"value\":[0-9.]+,\"label\":\"measured\"", RegexOptions.CultureInvariant)]
    private static partial Regex MeasuredWatts();

    [GeneratedRegex("https?://", RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    [GeneratedRegex("<meta[^>]*http-equiv=\"Content-Security-Policy\"[^>]*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CspMeta();

    [GeneratedRegex("<noscript[^>]*>(.*?)</noscript>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Noscript();

    [GeneratedRegex("<h2 id=\"judges\">(.*?)</h2>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex JudgesHeading();

    [GeneratedRegex("<ol class=\"acts\">(.*?)</ol>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ActsList();

    [GeneratedRegex("<li[ >]", RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();

    private static string PathOf(params string[] parts) => Path.Combine([ConfigurationFixtures.RepositoryRoot(), .. parts]);

    private static string Read(params string[] parts) => File.ReadAllText(PathOf(parts));

    private static string ReplayPage() => Read("docs", "replay", "index.html");

    private static string[] DataLines(string capture) => [.. Read("docs", "replay", capture).Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.StartsWith("data:", StringComparison.Ordinal))];

    [Fact]
    public void REQ_001_each_capture_holds_the_run_in_155_ordered_records_without_a_key()
    {
        foreach ((string file, string runId) in Captures)
        {
            string path = PathOf("docs", "replay", file);
            Assert.True(File.Exists(path), $"{file} does not exist");

            string[] lines = [.. File.ReadAllText(path).Split('\n').Select(static line => line.TrimEnd('\r'))];
            string[] data = [.. lines.Where(static line => line.StartsWith("data:", StringComparison.Ordinal))];

            Assert.True(data.Length == 155, $"{file} has {data.Length} data lines, expected 155");
            Assert.Single(data, static line => line.Contains("run-completed", StringComparison.Ordinal));
            Assert.Contains("\"type\":\"snapshot\"", data[0], StringComparison.Ordinal);
            Assert.Contains(runId, data[0], StringComparison.Ordinal);
            Assert.DoesNotContain(lines, static line => line.Contains("nvapi-", StringComparison.Ordinal));

            DateTimeOffset[] stamps = [.. data.Select(static line => DateTimeOffset.Parse(AtUtc().Match(line).Groups[1].Value, CultureInfo.InvariantCulture))];
            for (int i = 1; i < stamps.Length; i++)
            {
                Assert.True(stamps[i] >= stamps[i - 1], $"{file}: atUtc of data line {i + 1} precedes the line before it");
            }
        }
    }

    [Fact]
    public void REQ_002_gitattributes_pins_the_capture_line_endings()
    {
        string[] lines = [.. Read(".gitattributes").Split('\n').Select(static line => line.TrimEnd('\r'))];

        Assert.Contains("docs/replay/*.sse -text", lines);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void REQ_002_three_records_parse_in_either_line_ending_form(string newLine)
    {
        string sample = string.Join(newLine, ["event: aj", "data: {\"type\":\"snapshot\"}", "", "event: aj", "data: {\"type\":\"power\"}", "", "event: aj", "data: {\"type\":\"run-completed\"}", ""]);

        int records = sample.Split('\n').Select(static line => line.TrimEnd('\r')).Count(static line => line.StartsWith("data:", StringComparison.Ordinal));

        Assert.Equal(3, records);
        Assert.Contains("split(/\\r?\\n/)", ReplayPage(), StringComparison.Ordinal);
    }

    [Fact]
    public void REQ_003_the_page_is_self_contained_and_has_no_event_source()
    {
        string path = PathOf("docs", "replay", "index.html");
        Assert.True(File.Exists(path), "docs/replay/index.html does not exist");

        string page = File.ReadAllText(path);

        Assert.DoesNotContain("EventSource(", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<link ", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src", page, StringComparison.Ordinal);
        foreach (Match match in Url().Matches(page))
        {
            Assert.True(string.CompareOrdinal(page, match.Index, RepositoryLink, 0, RepositoryLink.Length) == 0, $"The page holds an address other than the repository link at offset {match.Index}");
        }
    }

    [Fact]
    public void REQ_004_the_page_offers_the_speeds_and_names_the_capture_files()
    {
        string page = ReplayPage();

        Assert.All(PageTexts, text => Assert.Contains(text, page, StringComparison.Ordinal));
    }

    [Fact]
    public void REQ_014_the_chart_shows_the_whole_recorded_run_not_a_sliding_window()
    {
        string page = ReplayPage();

        Assert.DoesNotContain("over the last 120 seconds", page, StringComparison.Ordinal);
        Assert.Contains("whole recorded run", page, StringComparison.Ordinal);
    }

    [Fact]
    public void REQ_015_the_page_drops_the_chunks_row_and_names_the_recorded_running_energy()
    {
        string page = ReplayPage();

        Assert.DoesNotContain("Output chunks", page, StringComparison.Ordinal);
        Assert.DoesNotContain("no-progress", page, StringComparison.Ordinal);
        Assert.Contains("recorded net energy", page, StringComparison.Ordinal);
    }

    [Fact]
    public void REQ_009_the_content_security_policy_hashes_match_the_inline_script_and_style()
    {
        string page = ReplayPage();
        Match meta = CspMeta().Match(page);

        Assert.True(meta.Success, "no Content-Security-Policy meta tag");
        Assert.Contains("'sha256-" + HashOf(page, "<script>", "</script>") + "'", meta.Value, StringComparison.Ordinal);
        Assert.Contains("'sha256-" + HashOf(page, "<style>", "</style>") + "'", meta.Value, StringComparison.Ordinal);
    }

    private static string HashOf(string page, string open, string close)
    {
        int start = page.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, open + " not found");
        start += open.Length;
        int end = page.IndexOf(close, start, StringComparison.Ordinal);
        Assert.True(end >= 0, close + " not found");
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(page[start..end])));
    }

    [Fact]
    public void REQ_016_the_page_has_the_cost_rows()
    {
        string page = ReplayPage();

        Assert.Contains("Cost (cumulative)", page, StringComparison.Ordinal);
        Assert.Contains("Cost (run)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void REQ_016_each_capture_carries_one_currency_of_estimated_step_costs_summing_to_the_run_total()
    {
        foreach ((string file, _) in Captures)
        {
            string[] data = DataLines(file);
            var currencies = new HashSet<string>(StringComparer.Ordinal);
            double sum = 0;
            int steps = 0;
            foreach (string line in data.Where(static line => line.Contains("\"type\":\"step-completed\"", StringComparison.Ordinal)))
            {
                using JsonDocument record = JsonDocument.Parse(line["data:".Length..]);
                JsonElement cost = record.RootElement.GetProperty("data").GetProperty("step").GetProperty("cost");
                Assert.Equal("estimated", cost.GetProperty("label").GetString());
                Assert.Equal(JsonValueKind.Number, cost.GetProperty("value").ValueKind);
                _ = currencies.Add(cost.GetProperty("currency").GetString() ?? string.Empty);
                sum += cost.GetProperty("value").GetDouble();
                steps++;
            }

            Assert.Equal(36, steps);
            string currency = Assert.Single(currencies);
            using JsonDocument completed = JsonDocument.Parse(data[^1]["data:".Length..]);
            JsonElement total = completed.RootElement.GetProperty("data").GetProperty("totals").GetProperty("costByCurrency").GetProperty(currency);
            Assert.Equal("estimated", total.GetProperty("label").GetString());
            Assert.Equal(total.GetProperty("value").GetDouble(), sum, 9);
        }
    }

    [Fact]
    public void REQ_006_the_selector_labels_name_the_tiers_gpus_and_models()
    {
        string page = ReplayPage();

        Assert.All(
            SelectorTexts,
            text => Assert.Contains(text, page, StringComparison.Ordinal));
    }

    [Fact]
    public void REQ_008_every_power_record_of_each_capture_carries_measured_watts()
    {
        foreach ((string file, _) in Captures)
        {
            string[] data = DataLines(file);
            int power = data.Count(static line => line.Contains("\"type\":\"power\"", StringComparison.Ordinal));
            int measured = data.Count(static line => MeasuredWatts().IsMatch(line));

            Assert.True(power == 72, $"{file} has {power} power records, expected 72");
            Assert.True(measured == 72, $"{file} has {measured} measured watts records, expected 72");
        }
    }

    [Fact]
    public void REQ_009_the_page_carries_a_content_security_policy_and_a_noscript_fallback()
    {
        string page = ReplayPage();

        Match meta = CspMeta().Match(page);
        Assert.True(meta.Success, "The page has no Content-Security-Policy meta tag");
        Assert.All(
            CspDirectives,
            directive => Assert.Contains(directive, meta.Value, StringComparison.Ordinal));

        Match noscript = Noscript().Match(page);
        Assert.True(noscript.Success, "The page has no noscript element");
        Assert.Contains("report/", noscript.Groups[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void REQ_011_the_landing_page_links_the_replay_and_counts_its_actions()
    {
        string landing = Read("docs", "index.html");

        Assert.Contains("href=\"replay/\"", landing, StringComparison.Ordinal);

        string heading = JudgesHeading().Match(landing).Groups[1].Value;
        Assert.NotEmpty(heading);

        int count = Array.FindIndex(CountWords, word => heading.Contains(word + " actions", StringComparison.Ordinal)) + 1;
        Assert.True(count > 0, $"The heading names no count word: {heading}");

        string list = ActsList().Match(landing).Groups[1].Value;
        Assert.Equal(count, ListItem().Count(list));
    }

    [Fact]
    public void REQ_012_the_readme_keyless_actions_name_the_cloudgpu_run_and_the_hosted_page()
    {
        string readme = Read("README.md");
        string[] lines = readme.Split('\n');

        Assert.DoesNotContain(lines, static line => line.Contains("20261025T133440Z-nebius.json --speed", StringComparison.Ordinal));
        Assert.Contains(lines, static line => line.Contains("https://tomas-rampas.github.io/agent-joules/replay/", StringComparison.Ordinal));
        Assert.Single(lines, static line => line == "### Judges without a GPU");

        int start = Array.IndexOf(lines, "### Judges without a GPU");
        string[] section = [.. lines.Skip(start + 1).TakeWhile(static line => !line.StartsWith("## ", StringComparison.Ordinal))];
        Assert.Equal(ActionMarkers, section.Where(static line => ListMarker().IsMatch(line)).Select(static line => line[..3]));
        Assert.Equal(254, readme.Count(static c => c == '\n'));
        Assert.All(readme, static c => Assert.True(c < 128, "README.md holds a non-ASCII character"));
    }

    [Fact]
    public void REQ_013_the_records_name_the_replay()
    {
        Assert.Contains("docs/replay/", Read("docs", "build-log.md"), StringComparison.Ordinal);
        Assert.Contains("agent-joules/replay/", Read("docs", "devpost-checklist.md"), StringComparison.Ordinal);
        Assert.Contains("docs/replay", Read("docs", "design", "technical-design.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void EDGE_005_the_landing_page_and_the_report_page_keep_no_script()
    {
        Assert.DoesNotContain("<script", Read("docs", "index.html"), StringComparison.Ordinal);
        Assert.DoesNotContain("<script", Read("docs", "report", "index.html"), StringComparison.Ordinal);
    }
}