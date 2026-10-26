using System.Globalization;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Content;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-144 (REQ-026, REQ-016): the content template renderer resolves <c>{{results.&lt;tier&gt;.&lt;dot.path&gt;}}</c> against the run file of a tier (array segments as <c>[index]</c>, a label object as <c>.value</c>, an optional
/// <c>|F0</c> or <c>|F1</c> format), renders a null value as its label text, leaves <c>{{repo.url}}</c> alone, puts a <c>Sources:</c> line first, splits the variants on <c>## Variant:</c> and refuses a variant above 1 300
/// characters. A failure is a <see cref="ConfigurationException"/> (exit 2) that names the placeholder, the tier or the variant. The tests use run documents built in memory: no file, no network, no key.
/// </summary>
[Trait("Spec", "REQ-026")]
public sealed class ContentRenderingTests
{
    private static Dictionary<Tier, ContentSource> Sources(params Tier[] tiers) =>
        tiers.ToDictionary(tier => tier, tier => new ContentSource(RunDocumentFixtures.CommittedFileName(tier), RunDocumentFixtures.CommittedText(tier)));

    private static string Body(string output) => output.Split('\n')[Array.FindIndex(output.Split('\n'), static line => line.StartsWith("## Variant:", StringComparison.Ordinal)) + 1];

    private static string RenderOne(string placeholder, Dictionary<Tier, ContentSource> sources) => Body(ContentTemplateRenderer.Render("## Variant: One\n" + placeholder + "\n", sources));

    private static Dictionary<Tier, ContentSource> LocalWith(Action<System.Text.Json.Nodes.JsonObject> edit) =>
        new() { [Tier.Local] = new ContentSource(RunDocumentFixtures.CommittedFileName(Tier.Local), RunDocumentFixtures.Edit(RunDocumentFixtures.CommittedText(Tier.Local), edit)) };

    [Fact]
    public void A_dotted_path_resolves_to_the_number_of_the_file()
    {
        Dictionary<Tier, ContentSource> sources = LocalWith(root => root["totals"]!["netJoules"]!["value"] = 1234.5);

        Assert.Equal("1234.5", RenderOne("{{results.local.totals.netJoules.value}}", sources));
    }

    [Fact]
    public void An_index_segment_selects_an_array_element()
    {
        Dictionary<Tier, ContentSource> sources = LocalWith(root =>
        {
            root["steps"]![0]!["netJoules"]!["value"] = 12.34;
            root["steps"]![1]!["netJoules"]!["value"] = 56.78;
        });

        Assert.Equal("12.34 and 56.78", RenderOne("{{results.local.steps[0].netJoules.value}} and {{results.local.steps[1].netJoules.value}}", sources));
    }

    [Theory]
    [InlineData("F0", "12")]
    [InlineData("F1", "12.3")]
    public void A_format_suffix_formats_with_the_invariant_culture(string format, string expected)
    {
        Dictionary<Tier, ContentSource> sources = LocalWith(root => root["steps"]![0]!["netJoules"]!["value"] = 12.34);
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Assert.Equal(expected, RenderOne("{{results.local.steps[0].netJoules.value|" + format + "}}", sources));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_null_value_renders_its_label_text_never_zero_null_or_empty()
    {
        Dictionary<Tier, ContentSource> sources = Sources(Tier.Nvidia);

        string text = RenderOne("{{results.nvidia.totals.grossJoules.value}}", sources);
        string formatted = RenderOne("{{results.nvidia.totals.grossJoules.value|F1}}", sources);

        Assert.Equal("N/A", text);
        Assert.Equal("N/A", formatted);
    }

    [Fact]
    public void A_null_value_labelled_not_reported_renders_not_reported()
    {
        Dictionary<Tier, ContentSource> sources = LocalWith(root =>
        {
            var tokens = root["totals"]!["inputTokens"]!.AsObject();
            tokens["value"] = null;
            tokens["label"] = "not-reported";
        });

        Assert.Equal("not-reported", RenderOne("{{results.local.totals.inputTokens.value}}", sources));
    }

    [Fact]
    public void The_repository_placeholder_is_left_unresolved()
    {
        string output = ContentTemplateRenderer.Render("## Variant: One\nSee {{repo.url}} now\n", Sources(Tier.Local));

        Assert.Equal("See {{repo.url}} now", Body(output));
    }

    [Fact]
    public void The_first_line_names_the_files_used_and_only_those()
    {
        Dictionary<Tier, ContentSource> sources = Sources(Tier.Local, Tier.Nvidia, Tier.Nebius);

        string output = ContentTemplateRenderer.Render("## Variant: One\n{{results.nvidia.totals.netJoules.value}} {{results.local.totals.netJoules.value}}\n", sources);
        string first = output.Split('\n')[0];

        Assert.StartsWith("Sources: ", first, StringComparison.Ordinal);
        Assert.Contains(sources[Tier.Local].FileName, first, StringComparison.Ordinal);
        Assert.Contains(sources[Tier.Nvidia].FileName, first, StringComparison.Ordinal);
        Assert.DoesNotContain(sources[Tier.Nebius].FileName, first, StringComparison.Ordinal);
    }

    [Fact]
    public void The_output_is_deterministic_and_uses_LF_only()
    {
        const string template = "## Variant: A\r\nx {{results.local.totals.netJoules.value|F0}}\r\n\r\n## Variant: B\r\ny {{repo.url}}\r\n";

        string first = ContentTemplateRenderer.Render(template, Sources(Tier.Local));

        Assert.Equal(first, ContentTemplateRenderer.Render(template, Sources(Tier.Local)));
        Assert.DoesNotContain('\r', first);
        Assert.Equal(2, first.Split("## Variant:").Length - 1);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_path_throws_naming_the_placeholder()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.totals.joulesPerToken.value}}", Sources(Tier.Local)));

        Assert.Contains("at line 2: {{results.local.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("joulesPerToken", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_past_the_end_throws_naming_the_placeholder()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.steps[9999].netJoules.value}}", Sources(Tier.Local)));

        Assert.Contains("at line 2: {{results.local.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("9999", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tier_without_a_file_throws_naming_the_tier()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.cloudgpu.totals.netJoules.value}}", Sources(Tier.Local)));

        Assert.Contains("cloudgpu", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variant_of_1300_characters_passes_and_one_of_1301_fails_naming_the_variant_and_its_length()
    {
        string ok = ContentTemplateRenderer.Render("## Variant: Edge\n" + new string('x', 1300) + "\n", Sources(Tier.Local));
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => ContentTemplateRenderer.Render("## Variant: Edge\n" + new string('x', 1301) + "\n", Sources(Tier.Local)));

        Assert.Contains(new string('x', 1300), ok, StringComparison.Ordinal);
        Assert.Contains("Edge", failure.Message, StringComparison.Ordinal);
        Assert.Contains("1301", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_length_is_that_of_the_rendered_variant_not_of_the_template()
    {
        string template = "## Variant: Edge\n" + string.Concat(Enumerable.Repeat("{{results.local.totals.netJoules.value|F0}}", 200)) + "\n";

        string output = ContentTemplateRenderer.Render(template, Sources(Tier.Local));

        Assert.StartsWith("Sources: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Variants_are_split_on_the_headings_and_each_is_measured_alone()
    {
        string template = "## Variant: One\n" + new string('a', 1000) + "\n\n## Variant: Two\n" + new string('b', 1000) + "\n";

        string output = ContentTemplateRenderer.Render(template, Sources(Tier.Local));

        Assert.Contains("## Variant: One", output, StringComparison.Ordinal);
        Assert.Contains("## Variant: Two", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/docs/x")]
    [InlineData("\\docs\\x")]
    [InlineData("C:\\docs\\x")]
    public void A_rooted_repository_path_is_refused_on_every_platform(string path) =>
        Assert.Throws<ArgumentException>(() => RepositoryRoot.Resolve(path));

    private static Dictionary<Tier, ContentSource> Custom(string json) => new() { [Tier.Local] = new ContentSource("custom-local.json", json) };

    [Theory]
    [InlineData("{{results.local}}")]
    [InlineData("{{ results.local.totals.netJoules.value }}")]
    [InlineData("{{results.local.totals.netJoules.value}")]
    [InlineData("{{results..totals}}")]
    [InlineData("{{}}")]
    [InlineData("{results.local.a.b}}")]
    [InlineData("{{{results.local.a.b}}}")]
    [InlineData("{{{repo.url}}}")]
    [InlineData("{{repo.url}}}")]
    public void A_malformed_placeholder_throws_naming_the_token_and_is_never_left_in_the_output(string token)
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("see " + token + " here", Custom("{\"a\":{\"b\":5}}")));

        Assert.Contains("malformed placeholder at line 2", failure.Message, StringComparison.Ordinal);
        Assert.Contains(token[..Math.Min(16, token.Length)], failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("## Variant: {{results.local.totals.netJoules.value}}")]
    [InlineData("## Variant: One {{repo.url}}")]
    [InlineData("## Variant: One }}")]
    [InlineData("## Variant: {{")]
    public void A_brace_pair_in_a_variant_heading_is_refused_naming_the_heading_line(string heading)
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => ContentTemplateRenderer.Render("intro\n" + heading + "\nbody\n", Sources(Tier.Local)));

        Assert.Contains("malformed placeholder at line 2", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("netJoules", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_label_text_with_markup_is_refused_like_any_other_string()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.m.value}}", Custom("{\"m\":{\"value\":null,\"label\":\"[x](y)\"}}")));

        Assert.Contains("not allowed in a post", failure.Message, StringComparison.Ordinal);
        Assert.Contains("at line 2: {{results.local.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_placeholder_that_spans_lines_is_refused_as_malformed()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.a.b\n}}", Custom("{\"a\":{\"b\":5}}")));

        Assert.Contains("malformed placeholder", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_over_long_placeholder_path_gives_a_bounded_message()
    {
        string placeholder = "{{results.local.a." + new string('q', 5000) + "}}";

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne(placeholder, Custom("{\"a\":{\"b\":5}}")));

        Assert.True(failure.Message.Length < 250, failure.Message);
    }

    [Fact]
    public void A_malformed_token_is_cut_to_a_bounded_length_in_the_message()
    {
        string token = "{{" + new string('q', 500);

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne(token, Sources(Tier.Local)));

        Assert.True(failure.Message.Length < 250, failure.Message);
    }

    [Fact]
    public void A_null_with_no_label_throws_naming_the_placeholder()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.a.b}}", Custom("{\"a\":{\"b\":null}}")));

        Assert.Contains("at line 2: {{results.local.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no label", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_member_that_is_null_in_an_object_that_is_not_a_label_throws()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.a.value}}", Custom("{\"a\":{\"value\":null}}")));

        Assert.Contains("no label", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("F2")]
    [InlineData("")]
    [InlineData("f0")]
    public void An_unknown_format_throws_naming_the_placeholder(string format)
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.totals.netJoules.value|" + format + "}}", Sources(Tier.Local)));

        Assert.Contains("unknown format", failure.Message, StringComparison.Ordinal);
        Assert.Contains("at line 2: {{results.local.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("netJoules", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_without_a_placeholder_has_the_sources_line_none()
    {
        string output = ContentTemplateRenderer.Render("## Variant: One\nplain text\n", Sources(Tier.Local, Tier.Nvidia));

        Assert.Equal("Sources: none", output.Split('\n')[0]);
    }

    [Fact]
    public void Nested_index_segments_select_an_element_of_an_element()
    {
        Assert.Equal("3 and 2", RenderOne("{{results.local.m[1][0]}} and {{results.local.m[0][1]}}", Custom("{\"m\":[[1,2],[3,4]]}")));
    }

    [Fact]
    public void A_nested_index_past_the_end_throws()
    {
        _ = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.m[0][2]}}", Custom("{\"m\":[[1,2]]}")));
    }

    [Fact]
    public void Whitespace_only_lines_at_both_ends_of_a_variant_are_not_counted()
    {
        string template = "## Variant: Edge\n  \n\t\n" + new string('x', 1300) + "\n   \n";

        string output = ContentTemplateRenderer.Render(template, Sources(Tier.Local));

        Assert.Equal(new string('x', 1300), Body(output));
    }

    [Fact]
    public void A_variant_with_a_non_finite_number_is_refused_naming_the_placeholder()
    {
        ConfigurationException formatted = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.a.b|F0}}", Custom("{\"a\":{\"b\":1e400}}")));
        ConfigurationException plain = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.a.b}}", Custom("{\"a\":{\"b\":1e400}}")));

        Assert.Contains("not a finite number", formatted.Message, StringComparison.Ordinal);
        Assert.Contains("at line 2: {{results.local.", formatted.Message, StringComparison.Ordinal);
        Assert.Contains("not a finite number", plain.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_file_with_a_duplicate_property_is_a_configuration_failure_not_a_crash()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.a}}", Custom("{\"a\":1,\"a\":2}")));

        Assert.Contains("custom-local.json", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_tier_in_a_placeholder_throws_naming_the_placeholder()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.mars.totals.netJoules.value}}", Sources(Tier.Local)));

        Assert.Contains("at line 2: {{results.mars.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("names no tier", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_names_the_line_and_never_echoes_more_than_16_characters_of_a_key_typed_into_the_template()
    {
        // A 40-character key typed in a placeholder: no registered cut form covers it, so the message must not carry more than its first 16 characters.
        string key = "q7w2e9r4t6y1u3i5o8p0a2s4d6f8g1h3j5k7l9z0";
        string[] templates = ["{{" + key, "x {{results.local." + key + "}} y", "{{results." + key + ".a}}", "{{results.local.a|" + key + "}}", "{{results.local.a." + key + "|F0}}"];

        foreach (string template in templates)
        {
            ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne(template, Custom("{\"a\":{\"b\":5}}")));

            Assert.DoesNotContain(key[..20], failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', failure.Message);
        }
    }

    [Fact]
    public void A_variant_name_in_a_message_is_cut_to_16_characters()
    {
        string name = new('v', 200);

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => ContentTemplateRenderer.Render("## Variant: " + name + "\n" + new string('x', 1301) + "\n", Sources(Tier.Local)));

        Assert.Contains("the variant at line 1", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('v', 17), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_above_64_KiB_is_refused_and_one_of_exactly_64_KiB_passes_the_size_check()
    {
        string at = "## Variant: A\n" + new string('x', ContentTemplateRenderer.MaxTemplateBytes - 14 - 1) + "\n";
        string over = "## Variant: A\n" + new string('x', ContentTemplateRenderer.MaxTemplateBytes - 14 + 1);

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => ContentTemplateRenderer.Render(over, Sources(Tier.Local)));
        ConfigurationException exact = Assert.Throws<ConfigurationException>(() => ContentTemplateRenderer.Render(at, Sources(Tier.Local)));

        Assert.Contains("larger than 65536 bytes", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the variant at line 1", exact.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_before_the_first_variant_is_bounded_like_a_variant()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => ContentTemplateRenderer.Render(new string('x', 1301) + "\n## Variant: A\nok\n", Sources(Tier.Local)));
        string ok = ContentTemplateRenderer.Render(new string('x', 1300) + "\n## Variant: A\nok\n", Sources(Tier.Local));

        Assert.Contains("the text before the first variant", failure.Message, StringComparison.Ordinal);
        Assert.Contains(new string('x', 1300), ok, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resolved_string_above_1300_characters_is_refused_naming_the_line()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.a}}", Custom("{\"a\":\"" + new string('s', 1301) + "\"}")));
        string ok = RenderOne("{{results.local.a}}", Custom("{\"a\":\"" + new string('s', 1300) + "\"}"));

        Assert.Contains("at line 2: {{results.local.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("longer than 1300", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1300, ok.Length);
    }

    [Fact]
    public void A_section_over_the_limit_is_refused_while_it_is_built_not_after_the_replacement()
    {
        // 2 000 placeholders of a 1 000-character value would be 2 000 000 characters; the section stops at the second one.
        string template = "## Variant: A\n" + string.Concat(Enumerable.Repeat("{{results.local.a}}", 2000)) + "\n";

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => ContentTemplateRenderer.Render(template, Custom("{\"a\":\"" + new string('s', 1000) + "\"}")));

        Assert.Contains("the variant at line 1 (A) is at least 2000 characters long; the limit is 1300", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[click](https://evil.example/x)")]
    [InlineData("<img src=x>")]
    [InlineData("![t](https://evil.example/x.png)")]
    [InlineData("a|b")]
    [InlineData("`code`")]
    [InlineData("{{repo.url}}")]
    public void A_string_with_a_markup_character_is_refused_without_echoing_it(string value)
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.a}}", Custom("{\"a\":\"" + value + "\"}")));

        Assert.Contains("at line 2: {{results.local.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x\\n## Variant: Forged")]
    [InlineData("x\\u202Ey")]
    [InlineData("x\\u0000y")]
    [InlineData("x\\u001By")]
    [InlineData("caf\\u00E9")]
    public void A_string_with_a_line_break_a_control_or_a_non_ASCII_character_is_refused(string escaped)
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => RenderOne("{{results.local.a}}", Custom("{\"a\":\"" + escaped + "\"}")));

        Assert.Contains("not allowed in a post", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Forged", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_ASCII_string_numbers_and_labels_are_unaffected()
    {
        string plain = RenderOne("{{results.local.a}}", Custom("{\"a\":\"Ubuntu 24.04 x86_64 - a, b; c: d? 'e' \\\"f\\\" #1 %2 $3 &4 *5 +6 =7 @8 ~9 ^0 _ \\\\\"}"));
        string number = RenderOne("{{results.local.n|F1}}", Custom("{\"n\":12.34}"));
        string label = RenderOne("{{results.local.m.value}}", Custom("{\"m\":{\"value\":null,\"label\":\"N/A\"}}"));

        Assert.Equal("Ubuntu 24.04 x86_64 - a, b; c: d? 'e' \"f\" #1 %2 $3 &4 *5 +6 =7 @8 ~9 ^0 _ \\", plain);
        Assert.Equal("12.3", number);
        Assert.Equal("N/A", label);
    }
}