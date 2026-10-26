using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>One <c>## Variant: &lt;name&gt;</c> section of the LinkedIn template or of the rendered posts.</summary>
/// <param name="Name">The text after <c>## Variant: </c>.</param>
/// <param name="Lines">The lines below the heading up to the next <c>## </c> heading.</param>
public sealed record ContentVariant(string Name, IReadOnlyList<string> Lines)
{
    /// <summary>The body: the lines joined with LF and trimmed of blank lines at both ends.</summary>
    public string Body => string.Join('\n', Lines).Trim('\n', '\r', ' ', '\t');

    /// <summary>The first line of the body that is not blank, trimmed; empty when there is none.</summary>
    public string FirstLine => Lines.FirstOrDefault(static line => line.Trim().Length > 0)?.Trim() ?? string.Empty;
}

/// <summary>One markdown table with the heading and the text it sits under.</summary>
/// <param name="Heading">The text of the nearest heading above the table, any level; empty when there is none.</param>
/// <param name="Preceding">The lines between the heading (or the previous table) and the table.</param>
/// <param name="Header">The cells of the header row.</param>
/// <param name="Rows">The body rows with the zero-based index of their line in the document.</param>
public sealed record MarkdownTable(string Heading, IReadOnlyList<string> Preceding, IReadOnlyList<string> Header, IReadOnlyList<TableRow> Rows);

/// <summary>One body row of a <see cref="MarkdownTable"/>.</summary>
/// <param name="LineIndex">The zero-based index of the line in the document.</param>
/// <param name="Cells">The trimmed cells, with <c>\|</c> restored to <c>|</c>.</param>
public sealed record TableRow(int LineIndex, IReadOnlyList<string> Cells);

/// <summary>
/// The content checks shared by AJ-134 (the LinkedIn posts), AJ-136 (the demo script) and AJ-137 (the article): the quantity rule (a measured quantity is never typed by hand), the markdown table parser, the variant
/// parser, the label-adjacency rule, the hook rule and the scope-sentence rule. Pure functions over strings; the committed files are read by the callers.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The scope sentence of a variant that cites laptop numbers only (REQ-026).</summary>
    public const string LaptopScope = "GPU-board energy measured on one laptop";

    /// <summary>The scope sentence of a variant that cites a cloudgpu placeholder (REQ-026).</summary>
    public const string CloudScope = "GPU-board energy measured on one laptop and on one Nebius AI Cloud GPU, reported separately";

    /// <summary>The placeholder the renderer leaves for the user to fill before posting.</summary>
    public const string RepoUrlPlaceholder = "{{repo.url}}";

    /// <summary>The sentence of the methodology that every long document carries.</summary>
    public const string EnergyScopeSentence = "Energy is GPU-board energy only.";

    /// <summary>The label that precedes a hook refinement in the build log.</summary>
    public const string HookRefinementLabel = "Hook refinement:";

    /// <summary>The widest a rendered variant may be, in characters (<c>wc -m</c>).</summary>
    public const int MaxVariantCharacters = 1300;

    /// <summary>The three variant names of the LinkedIn template, in order.</summary>
    public static IReadOnlyList<string> VariantNames { get; } = ["launch", "lesson-learned", "hackathon"];

    /// <summary>The label words a metric placeholder must be followed by.</summary>
    public static IReadOnlyList<string> LabelWords { get; } = ["measured", "estimated", "model-judged"];

    /// <summary>The five labels of the evidence model.</summary>
    public static IReadOnlyList<string> EvidenceLabels { get; } = ["measured", "estimated", "model-judged", "N/A", "not-reported"];

    /// <summary>The quantity rule of the stories: a number with a unit. A unit that continues as a word (<c>4 Writers</c>, <c>12 June</c>) is not a quantity.</summary>
    [GeneratedRegex(@"[0-9]+(\.[0-9]+)? ?(J|W|Wh|kJ|kWh|ms|tokens|%|EUR|USD|(?i:joules|watts|seconds|percent))(?![A-Za-z])", RegexOptions.CultureInvariant)]
    public static partial Regex QuantityPattern();

    [GeneratedRegex(@"\{\{[^{}]*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderToken();

    [GeneratedRegex(@"\{\{results\.([a-z]+)\.([^}|]+?)(\|[^}]*)?\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ResultsToken();

    [GeneratedRegex(@"\[[0-9]+\]|[^.\[\]]+", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegment();

    [GeneratedRegex("^- Hook refinement: \"(.+)\"$", RegexOptions.CultureInvariant)]
    private static partial Regex HookRefinementLine();

    [GeneratedRegex(@"(?<![A-Za-z])(J|W|ms|s|EUR|USD)(?![A-Za-z])|%|(?i:tokens|joules|watts|seconds|milliseconds|percent|score|points)", RegexOptions.CultureInvariant)]
    private static partial Regex UnitWord();

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSpace();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"results/([A-Za-z0-9._-]+\.json)", RegexOptions.CultureInvariant)]
    private static partial Regex ResultsFileMention();

    /// <summary>The text with every <c>{{...}}</c> token removed.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text without placeholders.</returns>
    public static string WithoutPlaceholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return PlaceholderToken().Replace(text, string.Empty);
    }

    /// <summary>The hand-typed-quantity check: after removing every placeholder, no number with a unit remains.</summary>
    /// <param name="text">The document text.</param>
    /// <returns>One <c>number without source: &lt;match&gt;</c> per quantity, in order.</returns>
    public static IReadOnlyList<string> HandTypedQuantities(string text) =>
        [.. QuantityPattern().Matches(WithoutPlaceholders(text)).Select(static match => "number without source: " + match.Value)];

    /// <summary>The variants of a template or of rendered posts: every <c>## Variant: &lt;name&gt;</c> section.</summary>
    /// <param name="text">The document text.</param>
    /// <returns>The variants in document order.</returns>
    public static IReadOnlyList<ContentVariant> Variants(string text)
    {
        const string prefix = "## Variant: ";
        var variants = new List<ContentVariant>();
        string? name = null;
        var lines = new List<string>();
        foreach (string line in Lines(text))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (name is not null)
                {
                    variants.Add(new ContentVariant(name, [.. lines]));
                }

                name = line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..].Trim() : null;
                lines.Clear();
            }
            else if (name is not null)
            {
                lines.Add(line);
            }
        }

        if (name is not null)
        {
            variants.Add(new ContentVariant(name, [.. lines]));
        }

        return variants;
    }

    /// <summary>The candidate hooks of the prompt: the quoted bullets under the line that starts <c>Candidate hooks</c>.</summary>
    /// <param name="prompt">The text of <c>docs/agent-joules.prompt.md</c>.</param>
    /// <returns>The hook sentences without the quotes.</returns>
    public static IReadOnlyList<string> PromptHooks(string prompt)
    {
        string[] lines = Lines(prompt);
        int at = Array.FindIndex(lines, static line => line.StartsWith("Candidate hooks", StringComparison.Ordinal));
        if (at < 0)
        {
            return [];
        }

        var hooks = new List<string>();
        for (int index = at + 1; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.StartsWith("- \"", StringComparison.Ordinal) && line.EndsWith('"'))
            {
                hooks.Add(line[3..^1]);
            }
            else if (line.Length > 0 && hooks.Count > 0)
            {
                break;
            }
        }

        return hooks;
    }

    /// <summary>The recorded refinements of a hook: the quoted text of a build-log line that starts <c>- Hook refinement: "&lt;text&gt;"</c>.</summary>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <returns>The refinement sentences.</returns>
    public static IReadOnlyList<string> HookRefinements(string buildLog) =>
    [
        .. Lines(buildLog)
            .Select(static line => HookRefinementLine().Match(line))
            .Where(static match => match.Success)
            .Select(static match => match.Groups[1].Value),
    ];

    /// <summary>The variant-count check: exactly three headings <c>launch</c>, <c>lesson-learned</c>, <c>hackathon</c> in that order.</summary>
    /// <param name="template">The template text.</param>
    /// <returns>The failures; empty when the three headings are there.</returns>
    public static IReadOnlyList<string> VariantHeadings(string template)
    {
        IReadOnlyList<ContentVariant> variants = Variants(template);
        var failures = new List<string>();
        if (variants.Count != VariantNames.Count)
        {
            failures.Add($"expected 3 variants, found {variants.Count}");
        }

        failures.AddRange(VariantNames.Where(name => variants.All(variant => variant.Name != name)).Select(static name => "missing variant: " + name));

        return failures;
    }

    /// <summary>The per-variant content rules (criterion 2): a results placeholder, the string GPU, one <c>{{repo.url}}</c>, and exactly one of the two scope sentences, chosen by whether the variant cites a cloudgpu placeholder.</summary>
    /// <param name="template">The template text.</param>
    /// <returns>The failures, naming the variant.</returns>
    public static IReadOnlyList<string> VariantContent(string template)
    {
        var failures = new List<string>();
        foreach (ContentVariant variant in Variants(template))
        {
            string body = variant.Body;
            if (!body.Contains("{{results.", StringComparison.Ordinal))
            {
                failures.Add("missing {{results.}} placeholder in variant: " + variant.Name);
            }

            if (!body.Contains("GPU", StringComparison.Ordinal))
            {
                failures.Add("missing GPU in variant: " + variant.Name);
            }

            int repo = CountOf(body, RepoUrlPlaceholder);
            if (repo == 0)
            {
                failures.Add("missing {{repo.url}} in variant: " + variant.Name);
            }
            else if (repo > 1)
            {
                failures.Add("repeated {{repo.url}} in variant: " + variant.Name);
            }

            failures.AddRange(ScopeSentence(variant));
        }

        return failures;
    }

    /// <summary>The label-adjacency check (criterion 4): each placeholder that resolves to a metric value (a <c>.value</c> path under <c>totals.</c>, <c>steps[</c>, <c>baseline.</c> or <c>judge[</c>) is followed on its line, within 40 characters and before the next placeholder, by the label word that the run file of its tier carries for that value, and by a unit.</summary>
    /// <param name="text">The template text.</param>
    /// <param name="runFileText">The text of the latest run file of a tier, by tier name, or null when the tier has none.</param>
    /// <returns>One <c>placeholder without label and unit: &lt;token&gt;</c> per placeholder that fails (also when the run file or its label cannot be found).</returns>
    public static IReadOnlyList<string> LabelAdjacency(string text, Func<string, string?> runFileText)
    {
        ArgumentNullException.ThrowIfNull(runFileText);
        var failures = new List<string>();
        var roots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (string line in Lines(text))
        {
            foreach (Match token in ResultsToken().Matches(line))
            {
                string path = token.Groups[2].Value;
                bool metric = path.EndsWith(".value", StringComparison.Ordinal)
                    && (path.StartsWith("totals.", StringComparison.Ordinal) || path.StartsWith("steps[", StringComparison.Ordinal) || path.StartsWith("baseline.", StringComparison.Ordinal) || path.StartsWith("judge[", StringComparison.Ordinal));
                if (!metric)
                {
                    continue;
                }

                string tier = token.Groups[1].Value;
                if (!roots.TryGetValue(tier, out JsonNode? root))
                {
                    string? json = runFileText(tier);
                    roots[tier] = root = json is null ? null : JsonNode.Parse(json);
                }

                string? label = root is null ? null : ResolveJson(root, path[..^".value".Length])?["label"]?.GetValue<string>();
                int from = token.Index + token.Length;
                string window = line.Substring(from, Math.Min(40, line.Length - from));
                int next = window.IndexOf("{{", StringComparison.Ordinal);
                if (next >= 0)
                {
                    window = window[..next];
                }

                if (label is null || !HasLabelWord(window, label) || !UnitWord().IsMatch(window))
                {
                    failures.Add("placeholder without label and unit: " + token.Value);
                }
            }
        }

        return failures;
    }

    // The label word as a whole word, so that measured does not match inside unmeasured (case-insensitive, as the label check always was).
    private static bool HasLabelWord(string window, string label) =>
        Regex.IsMatch(window, @"\b" + Regex.Escape(label) + @"\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // The node of a path such as steps[0].avgW or totals.netJoules; null when a segment is missing.
    private static JsonNode? ResolveJson(JsonNode root, string path)
    {
        JsonNode? node = root;
        foreach (Match segment in PathSegment().Matches(path))
        {
            string name = segment.Value;
            node = name.StartsWith('[')
                ? node is JsonArray array && int.TryParse(name[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < array.Count ? array[index] : null
                : node is JsonObject obj ? obj[name] : null;
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>The hook rule (criterion 8): the first line of each variant starts with one of the prompt hooks or a recorded refinement, and holds no digit and no results placeholder.</summary>
    /// <param name="template">The template text.</param>
    /// <param name="hooks">The hooks of the prompt, from <see cref="PromptHooks"/>.</param>
    /// <param name="refinements">The recorded refinements, from <see cref="HookRefinements"/>.</param>
    /// <returns>The failures, naming the variant.</returns>
    public static IReadOnlyList<string> Hooks(string template, IReadOnlyList<string> hooks, IReadOnlyList<string> refinements)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(refinements);
        var failures = new List<string>();
        foreach (ContentVariant variant in Variants(template))
        {
            string line = variant.FirstLine.TrimStart(['"', '\'', '>', '*', '_', ' ']);
            if (!hooks.Concat(refinements).Any(hook => line.StartsWith(hook, StringComparison.Ordinal)))
            {
                failures.Add("hook not from the prompt or the build log in variant: " + variant.Name);
            }

            if (line.Any(char.IsAsciiDigit) || line.Contains("{{results.", StringComparison.Ordinal))
            {
                failures.Add("number in the hook line in variant: " + variant.Name);
            }
        }

        return failures;
    }

    /// <summary>The whole template check of AJ-134: variant headings, variant content, hand-typed quantities, label adjacency and hooks.</summary>
    /// <param name="template">The template text.</param>
    /// <param name="prompt">The text of <c>docs/agent-joules.prompt.md</c>.</param>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <param name="runFileText">The text of the latest run file of a tier, or null.</param>
    /// <returns>Every failure text, empty when the template passes.</returns>
    public static IReadOnlyList<string> LinkedInTemplate(string template, string prompt, string buildLog, Func<string, string?> runFileText) =>
    [
        .. VariantHeadings(template),
        .. VariantContent(template),
        .. HandTypedQuantities(template),
        .. LabelAdjacency(template, runFileText),
        .. Hooks(template, PromptHooks(prompt), HookRefinements(buildLog)),
    ];

    /// <summary>The template with the body of one variant replaced by <paramref name="edit"/> of the body; the heading stays.</summary>
    /// <param name="template">The template text.</param>
    /// <param name="variant">The variant name.</param>
    /// <param name="edit">The edit of the body text.</param>
    /// <returns>The new text; unchanged when the variant is missing.</returns>
    public static string WithVariantBody(string template, string variant, Func<string, string> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        string[] lines = Lines(template);
        int start = Array.IndexOf(lines, "## Variant: " + variant);
        if (start < 0)
        {
            return template;
        }

        int end = Array.FindIndex(lines, start + 1, static line => line.StartsWith("## ", StringComparison.Ordinal));
        if (end < 0)
        {
            end = lines.Length;
        }

        string body = edit(string.Join('\n', lines[(start + 1)..end]));

        return string.Join('\n', [.. lines[..(start + 1)], body, .. lines[end..]]);
    }

    /// <summary>The rendered-posts check (criteria 5 and 6): the first line is <c>Sources:</c> naming files that exist, no results placeholder is left, <c>{{repo.url}}</c> is left once per variant, and each variant is within the limit.</summary>
    /// <param name="content">The rendered file.</param>
    /// <param name="runFileExists">Whether a run file of that name exists.</param>
    /// <param name="variantCount">The number of variants expected.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> RenderedPosts(string content, Func<string, bool> runFileExists, int variantCount)
    {
        ArgumentNullException.ThrowIfNull(runFileExists);
        var failures = new List<string>();
        string first = Lines(content)[0];
        if (!first.StartsWith("Sources:", StringComparison.Ordinal))
        {
            failures.Add("first line does not start with Sources:");
        }
        else
        {
            failures.AddRange(first["Sources:".Length..].Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Where(name => name != "none" && !runFileExists(name)).Select(static name => "source file missing: " + name));
        }

        if (CountOf(content, "{{results.") != 0)
        {
            failures.Add("unresolved {{results.}} placeholder");
        }

        int repo = CountOf(content, RepoUrlPlaceholder);
        if (repo != variantCount)
        {
            failures.Add($"{{{{repo.url}}}} occurs {repo} times, expected {variantCount}");
        }

        IReadOnlyList<ContentVariant> variants = Variants(content);
        if (variants.Count != variantCount)
        {
            failures.Add($"expected {variantCount} variants, found {variants.Count}");
        }

        failures.AddRange(variants.Where(static variant => variant.Body.Length > MaxVariantCharacters).Select(static variant => $"variant {variant.Name} has {variant.Body.Length} characters"));

        return failures;
    }

    /// <summary>The ASCII-and-LF rule of the content files.</summary>
    /// <param name="text">The raw text of the file, line endings included.</param>
    /// <returns>The failures: the first non-ASCII character and the first carriage return, with their lines.</returns>
    public static IReadOnlyList<string> AsciiAndLf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var failures = new List<string>();
        int line = 1;
        bool nonAscii = false;
        bool carriageReturn = false;
        foreach (char c in text)
        {
            if (c > '\u007f' && !nonAscii)
            {
                failures.Add($"non-ASCII character at line {line}");
                nonAscii = true;
            }

            if (c == '\r' && !carriageReturn)
            {
                failures.Add($"CR line ending at line {line}");
                carriageReturn = true;
            }

            if (c == '\n')
            {
                line++;
            }
        }

        return failures;
    }

    /// <summary>The number of words as <c>wc -w</c> counts them: runs of non-space characters.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The count.</returns>
    public static int WordCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return NonSpace().Count(text);
    }

    /// <summary>The number of non-overlapping occurrences of a part in a text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="part">The part.</param>
    /// <returns>The count.</returns>
    public static int CountOf(string text, string part)
    {
        int count = 0;
        int at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += part.Length;
        }

        return count;
    }

    /// <summary>The names of the <c>results/&lt;file&gt;.json</c> files a text mentions, distinct, in order.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The file names without the folder.</returns>
    public static IReadOnlyList<string> RunFilesMentioned(string text) =>
        [.. ResultsFileMention().Matches(text).Select(static match => match.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>The markdown tables of a document outside fenced blocks, each with the heading and the text above it.</summary>
    /// <param name="text">The document text.</param>
    /// <returns>The tables in order.</returns>
    public static IReadOnlyList<MarkdownTable> Tables(string text)
    {
        string[] lines = Lines(text);
        var tables = new List<MarkdownTable>();
        bool fenced = false;
        string heading = string.Empty;
        var preceding = new List<string>();
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                preceding.Add(line);
                continue;
            }

            if (fenced)
            {
                preceding.Add(line);
                continue;
            }

            Match headingLine = HeadingLine().Match(line);
            if (headingLine.Success)
            {
                heading = headingLine.Groups[2].Value;
                preceding.Clear();
                continue;
            }

            if (line.TrimStart().StartsWith('|') && index + 1 < lines.Length && TableSeparator().IsMatch(lines[index + 1]) && lines[index + 1].Contains('-', StringComparison.Ordinal))
            {
                string[] header = SplitRow(line);
                var rows = new List<TableRow>();
                int next = index + 2;
                while (next < lines.Length && lines[next].TrimStart().StartsWith('|'))
                {
                    rows.Add(new TableRow(next, SplitRow(lines[next])));
                    next++;
                }

                tables.Add(new MarkdownTable(heading, [.. preceding], header, rows));
                preceding.Clear();
                index = next - 1;
                continue;
            }

            preceding.Add(line);
        }

        return tables;
    }

    /// <summary>The cells of a table row: split on <c>|</c> that is not escaped, outer empty cells dropped, cells trimmed, <c>\|</c> restored.</summary>
    /// <param name="line">The row line.</param>
    /// <returns>The cells.</returns>
    public static string[] SplitRow(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        List<string> cells = [.. line.Replace("\\|", "\u0001", StringComparison.Ordinal).Split('|')];
        if (cells.Count > 0 && cells[0].Trim().Length == 0)
        {
            cells.RemoveAt(0);
        }

        if (cells.Count > 0 && cells[^1].Trim().Length == 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return [.. cells.Select(static cell => cell.Trim().Replace("\u0001", "|", StringComparison.Ordinal))];
    }

    private static List<string> ScopeSentence(ContentVariant variant)
    {
        string body = variant.Body;
        bool cloud = body.Contains("{{results.cloudgpu.", StringComparison.Ordinal);
        int cloudSentences = CountOf(body, CloudScope);
        int laptopOnly = CountOf(body, LaptopScope) - cloudSentences;
        if (cloud)
        {
            return cloudSentences >= 1 && laptopOnly == 0 ? [] : ["missing scope sentence in variant: " + variant.Name];
        }

        if (cloudSentences > 0)
        {
            return ["wrong scope sentence in variant: " + variant.Name];
        }

        return laptopOnly >= 1 ? [] : ["missing scope sentence in variant: " + variant.Name];
    }

    /// <summary>The formatted ratio used in failure texts: one decimal, invariant culture.</summary>
    /// <param name="ratio">The ratio.</param>
    /// <returns>The text, for example <c>3.3</c>.</returns>
    public static string Ratio(double ratio) => ratio.ToString("F1", CultureInfo.InvariantCulture);
}