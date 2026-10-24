using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentJoules.TestSupport;

/// <summary>
/// The static rules of the live dashboard page (AJ-090, REQ-032 R4 and R6): what the render model, the design tokens and the palette demand of the page source. The checks are text checks over the
/// source (the suite has no browser engine and no HTML parser), written once so that the unit tests and the Specs scenarios apply the same rules, and so that a deliberately broken source can be
/// handed to the same check to prove that it fails and names the rule. Each check returns the violations as short sentences that name the offending token; no violation means the rule holds.
/// </summary>
public static partial class DashboardPageRules
{
    /// <summary>The name of the function that builds the agent cards, the only place that may create an element.</summary>
    public const string CardBuilder = "buildAgentCards";

    /// <summary>The name of the repaint loop.</summary>
    public const string Loop = "frame";

    /// <summary>The name of the handler of the <c>snapshot</c> item, the only caller of the reset function.</summary>
    public const string SnapshotHandler = "handleSnapshot";

    /// <summary>The name of the reset function.</summary>
    public const string ResetFunction = "resetState";

    /// <summary>The custom properties that carry a text colour and are held to the contrast ratio on the panel.</summary>
    public static readonly IReadOnlyList<string> TextColourTokens = ["--fg", "--muted", "--na", "--accent", "--measured", "--estimated", "--agent-1", "--agent-2", "--agent-3", "--agent-4"];

    /// <summary>The custom properties the design names (REQ-032 R6).</summary>
    public static readonly IReadOnlyList<string> RequiredTokens =
    [
        "--bg", "--panel", "--fg", "--muted", "--accent", "--measured", "--estimated", "--na", "--grid",
        "--agent-1", "--agent-2", "--agent-3", "--agent-4", "--space-1", "--space-2", "--space-3", "--font-size-base",
    ];

    /// <summary>The region ids the design names.</summary>
    public static readonly IReadOnlyList<string> Regions = ["header", "agents", "chart", "footer"];

    /// <summary>The minimum contrast ratio of WCAG 2.x level AA for normal text.</summary>
    public const double MinimumContrast = 4.5;

    /// <summary>The render-model violations (R4) of <paramref name="source"/>: the forbidden calls, the counts of the one stream and the one loop, the places elements are created, the repaint loop and the handlers.</summary>
    /// <param name="source">The page source.</param>
    /// <returns>The violations; empty when the source obeys the render model.</returns>
    public static IReadOnlyList<string> RenderViolations(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var violations = new List<string>();
        foreach (string token in new[] { "innerHTML", "createElementNS", "setInterval", "setTimeout", "fetch(", "transition", "document.write" })
        {
            if (source.Contains(token, StringComparison.Ordinal))
            {
                violations.Add($"the page uses {token}");
            }
        }

        AddCount(violations, source, "EventSource", 1);
        AddCount(violations, source, "EventSource(\"/events\")", 1);
        AddCount(violations, source, "addEventListener(\"aj\"", 1);
        AddCount(violations, source, "requestAnimationFrame(", 1);
        AddCount(violations, source, "<script", 1);
        AddCount(violations, source, "<style", 1);

        if (source.Contains("<script src", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("the page has a script with a src");
        }

        string builder = FunctionBody(source, CardBuilder);
        int total = CountOf(source, "createElement");
        int inside = CountOf(builder, "createElement");
        if (inside == 0 || total != inside)
        {
            violations.Add($"createElement is used outside {CardBuilder} ({total - inside} of {total} calls)");
        }

        AddLoopViolations(violations, source);
        AddHandlerViolations(violations, source);
        AddStyleViolations(violations, source);
        violations.AddRange(FormatterViolations(source));
        return violations;
    }

    /// <summary>The violations of the formatter rule: one <c>fmt</c> function whose branch for a value that is null returns the label text of the metric, never a number or an empty text.</summary>
    /// <param name="source">The page source.</param>
    /// <returns>The violations; empty when the formatter prints the label for a null value.</returns>
    public static IReadOnlyList<string> FormatterViolations(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var violations = new List<string>();
        string script = Script(source);
        string formatter = FunctionBody(script, "fmt");
        if (formatter.Length == 0 || CountOf(script, "function fmt(") != 1)
        {
            violations.Add("the page does not have exactly one fmt formatter");
        }
        else if (!NullBranchReturnsLabel().IsMatch(formatter))
        {
            violations.Add("the null branch of fmt does not return the label text of the metric");
        }

        return violations;
    }

    /// <summary>The design violations (R6) of <paramref name="source"/>: the stylesheet, the tokens, the themes and the regions.</summary>
    /// <param name="source">The page source.</param>
    /// <returns>The violations; empty when the source meets the design rules.</returns>
    public static IReadOnlyList<string> DesignViolations(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var violations = new List<string>();
        if (CountOf(source, "<style") != 1)
        {
            violations.Add("the page does not have exactly one style block");
        }

        string style = Style(source);
        int properties = CustomPropertyNames().Matches(style).Select(static match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Count();
        if (properties < 6)
        {
            violations.Add($"the stylesheet defines {properties} custom properties, not at least 6");
        }

        foreach (string token in RequiredTokens)
        {
            if (!style.Contains(token + ":", StringComparison.Ordinal))
            {
                violations.Add($"the stylesheet does not define {token}");
            }
        }

        if (!source.Contains("prefers-color-scheme: light", StringComparison.Ordinal))
        {
            violations.Add("the page has no prefers-color-scheme media query");
        }

        if (!style.Contains("system-ui", StringComparison.Ordinal))
        {
            violations.Add("the stylesheet has no system font stack");
        }

        if (!style.Contains("font-variant-numeric: tabular-nums", StringComparison.Ordinal))
        {
            violations.Add("the stylesheet has no tabular numerals");
        }

        foreach (string region in Regions)
        {
            if (!source.Contains($"id=\"{region}\"", StringComparison.Ordinal))
            {
                violations.Add($"the page has no region with id {region}");
            }
        }

        if (source.Contains("<link", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("the page has a link element");
        }

        if (source.Contains("@import", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("the page has an @import rule");
        }

        if (ExternalUrl().IsMatch(source))
        {
            violations.Add("the page has an external url");
        }

        if (source.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("the page has an img element");
        }

        return violations;
    }

    /// <summary>The layout violations of the stylesheet of <paramref name="source"/>: a fixed width above 1248 px, a forced horizontal scrollbar, a grid without <c>minmax</c>.</summary>
    /// <param name="source">The page source.</param>
    /// <returns>The violations; empty when the layout fits 1280 px.</returns>
    public static IReadOnlyList<string> LayoutViolations(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var violations = new List<string>();
        string style = Style(source);
        foreach (Match match in FixedWidth().Matches(style))
        {
            double width = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (width > 1248.0)
            {
                violations.Add($"the stylesheet declares a fixed width of {match.Groups[1].Value}px");
            }
        }

        if (style.Contains("overflow-x: scroll", StringComparison.Ordinal))
        {
            violations.Add("the stylesheet forces overflow-x: scroll");
        }

        if (!style.Contains("minmax(", StringComparison.Ordinal))
        {
            violations.Add("the card grid does not use minmax");
        }

        return violations;
    }

    /// <summary>The violations of the contrast rule for one theme: every text-colour token below the minimum ratio on <c>--panel</c>.</summary>
    /// <param name="themeName">The name of the theme, for the message.</param>
    /// <param name="theme">The custom properties of the theme by name (with <c>--panel</c>).</param>
    /// <returns>The violations, each naming the pair, such as <c>--muted on --panel</c>.</returns>
    public static IReadOnlyList<string> ContrastViolations(string themeName, IReadOnlyDictionary<string, string> theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var violations = new List<string>();
        if (!theme.TryGetValue("--panel", out string? panel))
        {
            violations.Add($"the {themeName} theme has no --panel");
            return violations;
        }

        foreach (string token in TextColourTokens)
        {
            if (!theme.TryGetValue(token, out string? colour))
            {
                violations.Add($"the {themeName} theme has no {token}");
                continue;
            }

            double ratio = ContrastRatio(colour, panel);
            if (ratio < MinimumContrast)
            {
                violations.Add($"{token.TrimStart('-')}-on-panel ({token} on --panel) in the {themeName} theme is {ratio.ToString("0.00", CultureInfo.InvariantCulture)}, below {MinimumContrast.ToString("0.0", CultureInfo.InvariantCulture)}");
            }
        }

        return violations;
    }

    /// <summary>The custom properties of the dark theme (the plain <c>:root</c> block) and of the light theme (the <c>:root</c> block inside the <c>prefers-color-scheme: light</c> query, over the dark ones).</summary>
    /// <param name="source">The page source.</param>
    /// <returns>The dark and the light theme, each by custom property name.</returns>
    public static (IReadOnlyDictionary<string, string> Dark, IReadOnlyDictionary<string, string> Light) Palettes(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string style = Style(source);
        int media = style.IndexOf("@media (prefers-color-scheme: light)", StringComparison.Ordinal);
        string darkText = media < 0 ? style : style[..media];
        string lightText = media < 0 ? string.Empty : style[media..];
        Dictionary<string, string> dark = Declarations(darkText);
        Dictionary<string, string> light = new(dark, StringComparer.Ordinal);
        foreach ((string name, string value) in Declarations(lightText))
        {
            light[name] = value;
        }

        return (dark, light);
    }

    /// <summary>The contrast ratio of two colours by the WCAG 2.x relative-luminance formula.</summary>
    /// <param name="foreground">A colour as <c>#rrggbb</c>.</param>
    /// <param name="background">A colour as <c>#rrggbb</c>.</param>
    /// <returns>The ratio, from 1 to 21.</returns>
    public static double ContrastRatio(string foreground, string background)
    {
        double a = Luminance(foreground);
        double b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>The content of the one style block.</summary>
    /// <param name="source">The page source.</param>
    /// <returns>The text between <c>&lt;style&gt;</c> and <c>&lt;/style&gt;</c>, or empty.</returns>
    public static string Style(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        int open = source.IndexOf("<style>", StringComparison.Ordinal);
        int close = source.IndexOf("</style>", StringComparison.Ordinal);
        return open < 0 || close < open ? string.Empty : source[(open + "<style>".Length)..close];
    }

    /// <summary>The content of the one inline script.</summary>
    /// <param name="source">The page source.</param>
    /// <returns>The text between <c>&lt;script&gt;</c> and <c>&lt;/script&gt;</c>, or empty.</returns>
    public static string Script(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        int open = source.IndexOf("<script>", StringComparison.Ordinal);
        int close = source.IndexOf("</script>", StringComparison.Ordinal);
        return open < 0 || close < open ? string.Empty : source[(open + "<script>".Length)..close];
    }

    /// <summary>The body of the function <paramref name="name"/> (the text between its braces), found by matching the braces; the page script keeps no brace in a string or a comment.</summary>
    /// <param name="source">The page source.</param>
    /// <param name="name">The function name.</param>
    /// <returns>The body, or empty when there is no such function.</returns>
    public static string FunctionBody(string source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);

        int start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        return start < 0 ? string.Empty : BraceBlock(source, source.IndexOf('{', start));
    }

    /// <summary>The block that follows <paramref name="marker"/> (a function expression such as <c>onerror = function</c>).</summary>
    /// <param name="source">The page source.</param>
    /// <param name="marker">The text before the opening brace.</param>
    /// <returns>The body, or empty when the marker is absent.</returns>
    public static string BlockAfter(string source, string marker)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(marker);

        int start = source.IndexOf(marker, StringComparison.Ordinal);
        return start < 0 ? string.Empty : BraceBlock(source, source.IndexOf('{', start));
    }

    /// <summary>The number of times <paramref name="token"/> occurs in <paramref name="text"/>, counted by ordinal comparison.</summary>
    /// <param name="text">The text.</param>
    /// <param name="token">The token.</param>
    /// <returns>The count, without overlap.</returns>
    public static int CountOf(string text, string token)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(token);

        int count = 0;
        for (int at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The number of calls of the reset function in <paramref name="script"/>: its occurrences with a call parenthesis, less its definition.</summary>
    /// <param name="script">The page script.</param>
    /// <returns>The number of calls.</returns>
    public static int ResetCalls(string script) => CountOf(script, ResetFunction + "(") - CountOf(script, "function " + ResetFunction + "(");

    /// <summary>The names of the functions that handle the stream: those that start with <c>on</c> or <c>handle</c>.</summary>
    /// <param name="script">The page script.</param>
    /// <returns>The names, in source order.</returns>
    public static IReadOnlyList<string> HandlerNames(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        return [.. HandlerFunction().Matches(script).Select(static match => match.Groups[1].Value)];
    }

    /// <summary>The number of custom properties the stylesheet defines with a colour.</summary>
    /// <param name="style">The stylesheet.</param>
    /// <returns>The number of distinct properties.</returns>
    public static int ColourPropertyCount(string style)
    {
        ArgumentNullException.ThrowIfNull(style);

        return ColourProperty().Matches(style).Select(static match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>The stylesheet without the <c>:root</c> blocks that define the custom properties.</summary>
    /// <param name="style">The stylesheet.</param>
    /// <returns>The remaining rules.</returns>
    public static string WithoutRoot(string style)
    {
        ArgumentNullException.ThrowIfNull(style);

        return RootBlock().Replace(style, string.Empty);
    }

    /// <summary>The fixed widths in pixels the stylesheet declares (<c>max-width</c> and <c>min-width</c> are not widths).</summary>
    /// <param name="style">The stylesheet.</param>
    /// <returns>The widths in source order.</returns>
    public static IReadOnlyList<double> FixedWidthsPx(string style)
    {
        ArgumentNullException.ThrowIfNull(style);

        return [.. FixedWidth().Matches(style).Select(static match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))];
    }

    /// <summary>The page text with the tags and the script and style blocks removed, for the sentences a viewer reads.</summary>
    /// <param name="source">The page source.</param>
    /// <returns>The text.</returns>
    public static string VisibleText(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string text = ScriptOrStyleBlock().Replace(source, " ");
        return Whitespace().Replace(Tag().Replace(text, " "), " ").Trim();
    }

    private static string BraceBlock(string source, int open)
    {
        if (open < 0)
        {
            return string.Empty;
        }

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[(open + 1)..i];
            }
        }

        return string.Empty;
    }

    private static void AddCount(List<string> violations, string source, string token, int expected)
    {
        int count = CountOf(source, token);
        if (count != expected)
        {
            violations.Add($"{token} appears {count} times, not {expected}");
        }
    }

    private static void AddLoopViolations(List<string> violations, string source)
    {
        string loop = FunctionBody(source, Loop);
        string trimmed = loop.TrimStart();
        if (!trimmed.StartsWith("if (document.hidden)", StringComparison.Ordinal))
        {
            violations.Add($"the {Loop} loop does not start with a document.hidden check");
        }

        if (!BlockAfter(loop, "finally {").Contains("schedule();", StringComparison.Ordinal) || loop.IndexOf("finally {", StringComparison.Ordinal) < loop.IndexOf("paint(", StringComparison.Ordinal))
        {
            violations.Add($"the {Loop} loop does not request the next frame in a finally block");
        }

        int hidden = loop.IndexOf("document.hidden", StringComparison.Ordinal);
        int paint = loop.IndexOf("paint(", StringComparison.Ordinal);
        string hiddenBranch = hidden < 0 ? string.Empty : BraceBlock(loop, loop.IndexOf('{', hidden));
        if (hidden < 0 || paint < 0 || paint < hidden || !hiddenBranch.Contains("return", StringComparison.Ordinal) || hiddenBranch.Contains("paint(", StringComparison.Ordinal))
        {
            violations.Add($"the {Loop} loop does not return without painting while the document is hidden");
        }
    }

    private static void AddHandlerViolations(List<string> violations, string source)
    {
        string script = Script(source);
        foreach (Match function in HandlerFunction().Matches(script))
        {
            string name = function.Groups[1].Value;
            string body = FunctionBody(script, name);
            foreach (string forbidden in new[] { "document.", "textContent", "getContext", "paint(", CardBuilder + "(", "classList", "style." })
            {
                if (body.Contains(forbidden, StringComparison.Ordinal))
                {
                    violations.Add($"the handler {name} touches the page ({forbidden})");
                }
            }
        }

        string dispatcher = FunctionBody(script, "onEvent");
        if (!dispatcher.Contains("dirty = true", StringComparison.Ordinal))
        {
            violations.Add("the event handler onEvent does not set the dirty flag");
        }

        string errorHandler = BlockAfter(script, ".onerror = function");
        if (errorHandler.Length == 0)
        {
            violations.Add("the page has no error handler");
        }
        else if (ResetCall().IsMatch(errorHandler) || StateAssignment().IsMatch(errorHandler) || errorHandler.Contains(ResetFunction, StringComparison.Ordinal))
        {
            violations.Add("the error handler resets the state");
        }

        string snapshot = FunctionBody(script, SnapshotHandler);
        int calls = ResetCalls(script);
        if (calls != 1 || !snapshot.Contains($"{ResetFunction}(", StringComparison.Ordinal))
        {
            violations.Add($"{ResetFunction} is called {calls} times, not once from {SnapshotHandler}");
        }
    }

    private static void AddStyleViolations(List<string> violations, string source)
    {
        string style = Style(source);
        if (!ValueCell().IsMatch(style))
        {
            violations.Add("the value cells have no tabular numerals with a width in ch");
        }
    }

    private static Dictionary<string, string> Declarations(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in CustomPropertyValue().Matches(text))
        {
            result[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return result;
    }

    private static double Luminance(string colour)
    {
        if (colour.Length != 7 || colour[0] != '#')
        {
            throw new FormatException($"The colour {colour} is not #rrggbb.");
        }

        double Channel(int index)
        {
            double value = int.Parse(colour.AsSpan(1 + (index * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(1)) + (0.0722 * Channel(2));
    }

    [GeneratedRegex(@"(--[a-z0-9-]+)\s*:")]
    private static partial Regex CustomPropertyNames();

    [GeneratedRegex(@"(--[a-z0-9-]+)\s*:\s*(#[0-9a-fA-F]{6})\s*;")]
    private static partial Regex CustomPropertyValue();

    [GeneratedRegex(@"url\(\s*['""]?(?:ht)tps?:", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalUrl();

    [GeneratedRegex(@"(?<![\w-])width\s*:\s*([0-9.]+)px")]
    private static partial Regex FixedWidth();

    [GeneratedRegex(@"function ((?:on|handle)[A-Za-z]*)\(")]
    private static partial Regex HandlerFunction();

    [GeneratedRegex(@"\bresetState\s*\(")]
    private static partial Regex ResetCall();

    [GeneratedRegex(@"(?<![.\w])state\s*=(?!=)|=\s*\[\s*\]|=\s*\{\s*\}|\.length\s*=\s*0")]
    private static partial Regex StateAssignment();

    [GeneratedRegex(@"\.val\s*\{[^}]*font-variant-numeric:\s*tabular-nums[^}]*\bwidth:\s*[0-9.]+ch|\.val\s*\{[^}]*\bwidth:\s*[0-9.]+ch[^}]*font-variant-numeric:\s*tabular-nums")]
    private static partial Regex ValueCell();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyleBlock();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"if \(metric\.value === null\) \{\s*return \{ text: metric\.label,")]
    private static partial Regex NullBranchReturnsLabel();

    [GeneratedRegex(@"(--[a-z0-9-]+)\s*:\s*#")]
    private static partial Regex ColourProperty();

    [GeneratedRegex(@":root\s*\{[^}]*\}")]
    private static partial Regex RootBlock();
}