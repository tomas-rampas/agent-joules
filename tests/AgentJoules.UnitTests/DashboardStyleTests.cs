using AgentJoules.Dashboard;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-090 (REQ-032 R6): the visual design of the live page is a set of checkable rules: one stylesheet of custom properties, a dark default and a light theme through a media query, a system
/// font stack with tabular numerals, four named regions with the specified content, a palette whose every text colour meets WCAG AA (4.5:1) on the panel in both themes, and a layout that fits
/// 1280 px without a fixed wide width. The rules are text checks over the page source; the visual check at 1280 and 1920 px belongs to the demo rehearsal.
/// </summary>
[Trait("Spec", "REQ-032")]
public sealed class DashboardStyleTests
{
    // ---- criterion 13: tokens, themes, regions ----

    [Fact]
    public void Page_defines_design_tokens_and_regions()
    {
        string page = DashboardPage.Render(null);

        Assert.Empty(DashboardPageRules.DesignViolations(page));
        string style = DashboardPageRules.Style(page);
        Assert.True(DashboardPageRules.ColourPropertyCount(style) >= 6);
        foreach (string token in DashboardPageRules.RequiredTokens)
        {
            Assert.Contains(token + ":", style, StringComparison.Ordinal);
        }

        Assert.Contains("--font-size-small:", style, StringComparison.Ordinal);
        Assert.Contains("--font-size-value:", style, StringComparison.Ordinal);
        Assert.Contains("--radius:", style, StringComparison.Ordinal);

        // Dark is the default; the light theme overrides the colour properties only.
        Assert.Contains("@media (prefers-color-scheme: light)", style, StringComparison.Ordinal);
        (IReadOnlyDictionary<string, string> dark, IReadOnlyDictionary<string, string> light) = DashboardPageRules.Palettes(page);
        Assert.NotEqual(dark["--bg"], light["--bg"]);
        Assert.True(Luminance(dark["--bg"]) < Luminance(light["--bg"]), "The default theme is the dark one.");

        // One style block, no link, no import, no external url, no image.
        Assert.Equal(1, DashboardPageRules.CountOf(page, "<style"));
        Assert.DoesNotContain("<link", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", style, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", page, StringComparison.OrdinalIgnoreCase);

        // A system font stack and tabular numerals.
        Assert.Matches(@"font-family:\s*system-ui,", style);
        Assert.Contains("font-variant-numeric: tabular-nums", style, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_colour_gap_and_font_size_in_a_rule_uses_a_custom_property()
    {
        string style = DashboardPageRules.Style(DashboardPage.Render(null));

        // Outside the :root blocks that define the properties, a declaration of a colour, a gap or a font size holds a var(...) or a keyword, never a literal colour or pixel size.
        string withoutRoot = DashboardPageRules.WithoutRoot(style);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b", withoutRoot);
        Assert.DoesNotMatch(@"(?<![\w-])(?:padding|margin|gap|font-size)\s*:[^;{}]*\b\d+px", withoutRoot);
    }

    // ---- criterion 14: the regions and their content ----

    [Fact]
    public void Regions_have_required_content()
    {
        string page = DashboardPage.Render(RepositoryLink());
        string text = DashboardPageRules.VisibleText(page);

        // The four regions, in document order.
        int header = page.IndexOf("id=\"header\"", StringComparison.Ordinal);
        int agents = page.IndexOf("id=\"agents\"", StringComparison.Ordinal);
        int chart = page.IndexOf("id=\"chart\"", StringComparison.Ordinal);
        int footer = page.IndexOf("id=\"footer\"", StringComparison.Ordinal);
        Assert.True(0 < header && header < agents && agents < chart && chart < footer, "The regions are header, agents, chart, footer in this order.");
        Assert.Matches(@"<header id=""header"">", page);
        Assert.Matches(@"<footer id=""footer"">", page);

        // The header: title, tier, model IDs with precision, mode, progress, flags and the two badges.
        Assert.Contains("Agent Joules", text, StringComparison.Ordinal);
        foreach (string id in new[] { "hdr-tier", "hdr-models", "hdr-precision", "hdr-mode", "hdr-progress", "hdr-flags", "hdr-replay", "hdr-reconnecting" })
        {
            Assert.Contains($"id=\"{id}\"", page, StringComparison.Ordinal);
        }

        // The agents region is a grid of cards that fit their column.
        string style = DashboardPageRules.Style(page);
        Assert.Matches(@"#agents\s*\{[^}]*display:\s*grid", style);
        Assert.Matches(@"grid-template-columns:\s*repeat\(auto-fit,\s*minmax\(", style);

        // The card: watts, cumulative net joules, tokens, latency, status and the label badges, each card in the colour of its chart line.
        string script = DashboardPageRules.Script(page);
        string builder = DashboardPageRules.FunctionBody(script, DashboardPageRules.CardBuilder);
        foreach (string row in new[] { "watts", "running", "chunks", "net", "cumulative", "avg", "tokens-in", "tokens-out", "latency", "flags" })
        {
            Assert.Contains($"\"{row}\"", builder, StringComparison.Ordinal);
        }

        Assert.Contains("\"pill ", builder, StringComparison.Ordinal);

        foreach (string badge in new[] { ".badge.measured", ".badge.estimated", ".badge.na", ".badge.notreported" })
        {
            Assert.Contains(badge, style, StringComparison.Ordinal);
        }

        Assert.Matches(@"\.badge\.measured\s*\{[^}]*var\(--measured\)", style);
        Assert.Matches(@"\.badge\.estimated\s*\{[^}]*var\(--estimated\)", style);
        Assert.Matches(@"\.badge\.na\s*\{[^}]*var\(--na\)", style);
        Assert.Contains("agentColour(", script, StringComparison.Ordinal);
        Assert.Contains("--agent-", script, StringComparison.Ordinal);

        // The chart: axis labels and a legend with the baseline.
        Assert.Contains("id=\"axis-x\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"axis-y\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"legend\"", page, StringComparison.Ordinal);
        Assert.Contains("idle baseline", text, StringComparison.Ordinal);
        Assert.Contains("shared-window", text, StringComparison.Ordinal);
        Assert.Contains("seconds", text, StringComparison.Ordinal);

        // The footer: the sentence and the repository link.
        string footerText = page[footer..];
        Assert.Contains("Energy is GPU-board energy only.", footerText, StringComparison.Ordinal);
        Assert.Contains("id=\"repo-link\"", footerText, StringComparison.Ordinal);
    }

    // ---- criterion 15: contrast and layout ----

    [Fact]
    public void Palette_meets_wcag_aa()
    {
        (IReadOnlyDictionary<string, string> dark, IReadOnlyDictionary<string, string> light) = DashboardPageRules.Palettes(DashboardPage.Render(null));

        Assert.Empty(DashboardPageRules.ContrastViolations("dark", dark));
        Assert.Empty(DashboardPageRules.ContrastViolations("light", light));
        foreach (string token in DashboardPageRules.TextColourTokens)
        {
            Assert.True(DashboardPageRules.ContrastRatio(dark[token], dark["--panel"]) >= 4.5, $"{token} on the dark panel");
            Assert.True(DashboardPageRules.ContrastRatio(light[token], light["--panel"]) >= 4.5, $"{token} on the light panel");
        }

        // The formula is the WCAG one: black on white is 21, a colour on itself 1.
        Assert.Equal(21.0, DashboardPageRules.ContrastRatio("#000000", "#ffffff"), 6);
        Assert.Equal(1.0, DashboardPageRules.ContrastRatio("#58a6ff", "#58a6ff"), 6);
    }

    [Fact]
    public void A_low_contrast_palette_fails_and_names_the_pair()
    {
        (IReadOnlyDictionary<string, string> dark, _) = DashboardPageRules.Palettes(DashboardPage.Render(null));
        var palette = new Dictionary<string, string>(dark, StringComparer.Ordinal) { ["--panel"] = "#ffffff", ["--muted"] = "#959595" };
        double ratio = DashboardPageRules.ContrastRatio(palette["--muted"], palette["--panel"]);

        IReadOnlyList<string> violations = DashboardPageRules.ContrastViolations("test", palette);

        Assert.InRange(ratio, 2.95, 3.05);
        Assert.Contains(violations, static violation => violation.Contains("muted-on-panel", StringComparison.Ordinal));
    }

    [Fact]
    public void Layout_has_no_fixed_wide_width()
    {
        string page = DashboardPage.Render(null);
        string style = DashboardPageRules.Style(page);

        Assert.Empty(DashboardPageRules.LayoutViolations(page));
        Assert.DoesNotContain("overflow-x: scroll", style, StringComparison.Ordinal);
        Assert.Contains("minmax(", style, StringComparison.Ordinal);
        foreach (double width in DashboardPageRules.FixedWidthsPx(style))
        {
            Assert.True(width <= 1248.0, $"{width} px");
        }

        // max-width is exempt and is what keeps the page from stretching on a wide window; children can shrink.
        Assert.Contains("max-width: 1600px", style, StringComparison.Ordinal);
        Assert.Contains("min-width: 0", style, StringComparison.Ordinal);

        // A source with a 1400 px width or a forced scrollbar fails the same check.
        Assert.NotEmpty(DashboardPageRules.LayoutViolations(page.Replace("</style>", ".wide { width: 1400px; }\n</style>", StringComparison.Ordinal)));
        Assert.NotEmpty(DashboardPageRules.LayoutViolations(page.Replace("</style>", ".wide { overflow-x: scroll; }\n</style>", StringComparison.Ordinal)));
    }

    [Fact]
    public void Long_values_and_reasons_wrap_while_numeric_cells_keep_their_ch_width()
    {
        string style = DashboardPageRules.Style(DashboardPage.Render(null));

        // A flags value such as "shared-window, low-resolution" and a reason such as "provider-omitted-usage" wrap instead of being cut with an ellipsis ...
        Assert.Matches(@"\.val\.long, \.badge\.long\s*\{[^}]*width: auto;[^}]*white-space: normal;[^}]*overflow-wrap: anywhere;", style);

        // ... while the numeric cell keeps its fixed width and the badge its width in ch.
        Assert.Matches(@"\.val\s*\{[^}]*\bwidth: \d+ch;[^}]*font-variant-numeric: tabular-nums", style);
        Assert.Matches(@"\.badge\s*\{[^}]*\bwidth: \d+ch;", style);
        string script = DashboardPageRules.Script(DashboardPage.Render(null));
        Assert.Contains("\"val long\"", script, StringComparison.Ordinal);
        Assert.Contains(" long\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_legend_swatch_of_the_shared_window_band_has_the_opacity_of_the_band()
    {
        string page = DashboardPage.Render(null);

        Assert.Contains("opacity: 0.2;", DashboardPageRules.Style(page), StringComparison.Ordinal);
        Assert.Contains("ctx.globalAlpha = 0.2;", page, StringComparison.Ordinal);
    }

    // ---- the negative sources ----

    [Fact]
    public void A_source_without_the_media_query_or_with_an_import_or_a_link_fails_the_design_check()
    {
        string page = DashboardPage.Render(null);

        Assert.Contains(DashboardPageRules.DesignViolations(page.Replace("prefers-color-scheme: light", "min-width: 1", StringComparison.Ordinal)), static violation => violation.Contains("media query", StringComparison.Ordinal));
        Assert.Contains(DashboardPageRules.DesignViolations(page.Replace("</style>", "@import 'x';\n</style>", StringComparison.Ordinal)), static violation => violation.Contains("@import", StringComparison.Ordinal));
        Assert.Contains(DashboardPageRules.DesignViolations(page.Replace("</head>", "<link rel=\"stylesheet\">\n</head>", StringComparison.Ordinal)), static violation => violation.Contains("link", StringComparison.Ordinal));
        Assert.Contains(DashboardPageRules.DesignViolations(page.Replace("<body>", "<body><img>", StringComparison.Ordinal)), static violation => violation.Contains("img", StringComparison.Ordinal));
        Assert.Contains(DashboardPageRules.DesignViolations(page.Replace("<header id=\"header\">", "<header>", StringComparison.Ordinal)), static violation => violation.Contains("header", StringComparison.Ordinal));
        Assert.Contains(DashboardPageRules.DesignViolations(page.Replace("--estimated:", "--estimate:", StringComparison.Ordinal)), static violation => violation.Contains("--estimated", StringComparison.Ordinal));
    }

    // ---- helpers ----

    private static Uri RepositoryLink() => new(string.Concat(Uri.UriSchemeHttps, "://", "example.test/owner/repo"));

    private static double Luminance(string colour) => DashboardPageRules.ContrastRatio(colour, "#000000");
}