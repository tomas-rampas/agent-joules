using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The checks of AJ-137 (the long-form article, REQ-027): the four citations with links, the Limitations and What is new sections, the source and label rule for every quantity, the environment header of each results
/// table against the run file it names, the ranking words, the cloudgpu-absent rule, the length and the character set. Pure functions over the article text; the run files and the existence of files are injected.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The text of the line the build log carries after the article links were requested.</summary>
    public const string ArticleLinksCheckedMarker = "Article links checked";

    /// <summary>The fewest words of the article.</summary>
    public const int ArticleMinWords = 800;

    /// <summary>The most words of the article.</summary>
    public const int ArticleMaxWords = 2500;

    /// <summary>The four citations of the article, each of which needs a link.</summary>
    public static IReadOnlyList<string> ArticleCitationNames { get; } = ["2407.16893", "TokenPowerBench", "2604.09048", "greentoken"];

    /// <summary>The words the Limitations section must contain.</summary>
    public static IReadOnlyList<string> LimitationTerms { get; } = ["GPU-only", "display", "power limit", "sampling", "offload", "hosted", "cloud"];

    private const string NotAvailable = "N/A";

    private static readonly string[] NewTerms = [".NET", "Microsoft Agent Framework", "Nemotron", "2026-10-17"];

    /// <summary>The ranking words the article may not use.</summary>
    public static IReadOnlyList<string> ArticleRankingWords { get; } = ["greener", "more efficient than", "leaderboard", "outperforms", "beats"];

    [GeneratedRegex(@"per[- ]agent[- ]step", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PerAgentStep();

    [GeneratedRegex(@"\b(measured|estimated|model-judged|N/A|not-reported)\b", RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceLabelWord();

    [GeneratedRegex(@"^GPU: (.+?); driver: (.+?); run date \(UTC\): (.+?); local runtime: (.+?); model: (.+) \(([^()]+)\); file: (\S+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentHeader();

    /// <summary>The citation check: each citation occurs as written (case-sensitive: criterion 1 greps the lowercase <c>greentoken</c>) and has a link, that is a line holding the citation and an address.</summary>
    /// <param name="article">The article text.</param>
    /// <returns>One <c>missing citation: &lt;name&gt;</c> or <c>missing link: &lt;name&gt;</c> per citation.</returns>
    public static IReadOnlyList<string> ArticleCitations(string article)
    {
        string[] lines = Lines(article);
        var failures = new List<string>();
        foreach (string name in ArticleCitationNames)
        {
            string[] with = [.. lines.Where(line => line.Contains(name, StringComparison.Ordinal))];
            if (with.Length == 0)
            {
                failures.Add("missing citation: " + name);
            }
            else if (!with.Any(static line => UrlPattern().IsMatch(line)))
            {
                failures.Add("missing link: " + name);
            }
        }

        return failures;
    }

    /// <summary>The distinct addresses on the lines that hold a citation: the links the integration scenario requests.</summary>
    /// <param name="article">The article text.</param>
    /// <returns>The addresses without trailing punctuation.</returns>
    public static IReadOnlyList<string> ArticleLinks(string article) =>
    [
        .. Lines(article)
            .Where(line => ArticleCitationNames.Any(name => line.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(static line => UrlPattern().Matches(line).Select(static match => match.Value.TrimEnd('.', ',', ';', ':')))
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>The build-log check: a line with <c>Article links checked</c> and a date (<c>yyyy-MM-dd</c>).</summary>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <returns>Null when logged, otherwise a failure text.</returns>
    public static string? ArticleLinksLogged(string buildLog) =>
        Lines(buildLog).Any(static line => line.Contains(ArticleLinksCheckedMarker, StringComparison.Ordinal) && DatePattern().IsMatch(line)) ? null : "article link check is not logged with a date";

    /// <summary>The section checks: one <c>Limitations</c> heading whose section holds the limitation terms and the energy sentence; a <c>What is new</c> section that names per-agent-step attribution, .NET, Microsoft Agent Framework, Nemotron and the search of 2026-10-17.</summary>
    /// <param name="article">The article text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ArticleSections(string article)
    {
        var failures = new List<string>();
        string? limitations = HasHeadingAtAnyLevel(article, "Limitations");
        if (limitations is not null)
        {
            failures.Add(limitations);
        }
        else
        {
            string section = string.Join('\n', SectionAtAnyLevel(article, "Limitations"));
            failures.AddRange(LimitationTerms.Where(term => !section.Contains(term, StringComparison.OrdinalIgnoreCase)).Select(static term => "Limitations lacks: " + term));
            if (!section.Contains(EnergyScopeSentence, StringComparison.Ordinal))
            {
                failures.Add("Limitations lacks: " + EnergyScopeSentence);
            }
        }

        string? isNew = HasHeadingAtAnyLevel(article, "What is new");
        if (isNew is not null)
        {
            failures.Add(isNew);
        }
        else
        {
            string section = string.Join('\n', SectionAtAnyLevel(article, "What is new"));
            if (!PerAgentStep().IsMatch(section))
            {
                failures.Add("What is new lacks: per-agent-step attribution");
            }

            failures.AddRange(NewTerms.Where(term => !section.Contains(term, StringComparison.Ordinal)).Select(static term => "What is new lacks: " + term));
        }

        return failures;
    }

    /// <summary>The quantity rule (criterion 4): every line with a quantity names an existing <c>results/</c> file or <c>docs/cloudgpu-run.md</c> and holds a label word.</summary>
    /// <param name="article">The article text.</param>
    /// <param name="exists">Whether a repository-relative file exists.</param>
    /// <returns>One <c>number without source: &lt;match&gt;</c> (or <c>source file missing: ...</c> or <c>number without label: ...</c>) per quantity.</returns>
    public static IReadOnlyList<string> ArticleQuantities(string article, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var failures = new List<string>();
        foreach (string line in Lines(article))
        {
            MatchCollection quantities = QuantityPattern().Matches(line);
            if (quantities.Count == 0)
            {
                continue;
            }

            IReadOnlyList<string> files = RunFilesMentioned(line);
            bool cloudRun = line.Contains(CloudGpuRunDocument, StringComparison.Ordinal);
            foreach (Match quantity in quantities)
            {
                if (files.Count == 0 && !cloudRun)
                {
                    failures.Add("number without source: " + quantity.Value);
                }
                else if (!EvidenceLabelWord().IsMatch(line))
                {
                    failures.Add("number without label: " + quantity.Value);
                }
            }

            failures.AddRange(files.Where(file => !exists("results/" + file)).Select(static file => "source file missing: results/" + file));
            if (cloudRun && !exists(CloudGpuRunDocument))
            {
                failures.Add("source file missing: " + CloudGpuRunDocument);
            }
        }

        return failures;
    }

    /// <summary>The ranking check: none of the ranking words occurs (ignoring case).</summary>
    /// <param name="article">The article text.</param>
    /// <returns>One <c>ranking claim: &lt;word&gt;</c> per word that occurs.</returns>
    public static IReadOnlyList<string> ArticleRanking(string article) =>
        [.. ArticleRankingWords.Where(word => ContainsWord(article, word)).Select(static word => "ranking claim: " + word)];

    /// <summary>Whether a text holds a word or phrase on word boundaries, ignoring case: <c>beats</c> is not found in <c>heartbeats</c>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="word">The word or phrase.</param>
    /// <returns>True when found.</returns>
    public static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, @"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The length check: 800 to 2 500 words as <c>wc -w</c> counts them.</summary>
    /// <param name="article">The article text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ArticleLength(string article)
    {
        int words = WordCount(article);

        return words is >= ArticleMinWords and <= ArticleMaxWords ? [] : [$"word count {words} is outside {ArticleMinWords} to {ArticleMaxWords}"];
    }

    /// <summary>
    /// The cloudgpu-absent rule modelled in memory: when no cloudgpu run file exists, the article names the cloudgpu tier only on lines that say it did not run or was not run, and says so at least once. When the file exists the
    /// rule has nothing to check.
    /// </summary>
    /// <param name="article">The article text.</param>
    /// <param name="cloudGpuRan">Whether a completed or partial cloudgpu run file exists.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> CloudGpuAbsence(string article, bool cloudGpuRan)
    {
        if (cloudGpuRan)
        {
            return [];
        }

        string[] named = [.. Lines(article).Where(static line => line.Contains("cloudgpu", StringComparison.OrdinalIgnoreCase))];
        var failures = new List<string>(named.Where(static line => !line.Contains("did not run", StringComparison.OrdinalIgnoreCase) && !line.Contains("not run", StringComparison.OrdinalIgnoreCase)).Select(static line => "cloudgpu result without a run file: " + Cut(line)));
        if (named.Length == 0)
        {
            failures.Add("missing sentence: the cloudgpu tier did not run");
        }

        return failures;
    }

    /// <summary>
    /// The environment check (criterion 5): for each results table that names a run file, the header line above the table, <c>GPU: ...; driver: ...; run date (UTC): ...; local runtime: ...; model: ... (...); file: results/...</c>,
    /// is parsed and each field is compared with the run file: GPU name, driver version, run date (UTC, <c>yyyy-MM-dd</c>), local runtime (<c>&lt;runtime&gt; &lt;version&gt;</c>), the model IDs with the precision, and the file. A value that
    /// is null in the file is written <c>N/A</c>.
    /// </summary>
    /// <param name="article">The article text.</param>
    /// <param name="readRunFile">The text of <c>results/&lt;name&gt;</c> by file name, or null when the file does not exist.</param>
    /// <returns>One <c>table environment differs from &lt;file&gt;: &lt;field&gt; &lt;expected&gt;</c> per differing field, and the same with <c>header</c> as the field when the header line is missing.</returns>
    public static IReadOnlyList<string> ArticleEnvironment(string article, Func<string, string?> readRunFile)
    {
        ArgumentNullException.ThrowIfNull(readRunFile);
        var failures = new List<string>();
        foreach (MarkdownTable table in Tables(article))
        {
            string tableText = string.Join('\n', table.Rows.SelectMany(static row => row.Cells).Concat(table.Header));
            foreach (string file in RunFilesMentioned(tableText))
            {
                string? text = readRunFile(file);
                if (text is null)
                {
                    failures.Add("table names a missing run file: " + file);
                    continue;
                }

                Match header = table.Preceding.Select(static line => EnvironmentHeader().Match(line.Trim())).LastOrDefault(static match => match.Success) ?? Match.Empty;
                if (!header.Success)
                {
                    failures.Add($"table environment differs from {file}: header missing");
                    continue;
                }

                failures.AddRange(
                    ExpectedHeaderFields(text, file)
                        .Where(field => header.Groups[field.Group].Value != field.Expected)
                        .Select(field => $"table environment differs from {file}: {field.Field} {field.Expected}"));
            }
        }

        return failures;
    }

    // The header fields that a run file dictates, with the group of EnvironmentHeader that holds each.
    private static IEnumerable<(string Field, int Group, string Expected)> ExpectedHeaderFields(string runFileText, string file)
    {
        JsonNode root = JsonNode.Parse(runFileText) ?? throw new InvalidOperationException("The run file is empty.");
        JsonNode? environment = root["environment"];
        string? runtime = environment?["localRuntime"]?.GetValue<string>();
        string? version = environment?["runtimeVersion"]?.GetValue<string>();
        string? start = root["run"]?["startUtc"]?.GetValue<string>();
        string[] models = [.. (environment?["models"]?.AsArray() ?? []).Select(static entry => entry?["modelId"]?.GetValue<string>()).OfType<string>().Distinct(StringComparer.Ordinal)];

        yield return ("gpuName", 1, environment?["gpuName"]?.GetValue<string>() ?? NotAvailable);
        yield return ("driverVersion", 2, environment?["driverVersion"]?.GetValue<string>() ?? NotAvailable);
        yield return ("runDate", 3, start is null ? NotAvailable : DateTimeOffset.Parse(start, CultureInfo.InvariantCulture).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        yield return ("localRuntime", 4, runtime is null ? NotAvailable : version is null ? runtime : runtime + " " + version);
        yield return ("modelId", 5, string.Join(", ", models));
        yield return ("precision", 6, environment?["precision"]?.GetValue<string>() ?? NotAvailable);
        yield return ("file", 7, "results/" + file);
    }

    /// <summary>The strings an article table must repeat for a run file.</summary>
    /// <param name="runFileText">The text of the run file.</param>
    /// <returns>The field and the string, for the fields that are not null.</returns>
    public static IReadOnlyList<(string Field, string Value)> RunFileStrings(string runFileText)
    {
        JsonNode root = JsonNode.Parse(runFileText) ?? throw new InvalidOperationException("The run file is empty.");
        JsonNode? environment = root["environment"];
        var strings = new List<(string, string)>();
        void Add(string field, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                strings.Add((field, value));
            }
        }

        Add("gpuName", environment?["gpuName"]?.GetValue<string>());
        Add("driverVersion", environment?["driverVersion"]?.GetValue<string>());
        string? start = root["run"]?["startUtc"]?.GetValue<string>();
        Add("runDate", start is null ? null : DateTimeOffset.Parse(start, CultureInfo.InvariantCulture).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("localRuntime", environment?["localRuntime"]?.GetValue<string>());
        Add("precision", environment?["precision"]?.GetValue<string>());
        foreach (string? model in (environment?["models"]?.AsArray() ?? []).Select(static entry => entry?["modelId"]?.GetValue<string>()).Distinct(StringComparer.Ordinal))
        {
            Add("modelId", model);
        }

        return strings;
    }

    /// <summary>Every check of AJ-137 over an article.</summary>
    /// <param name="article">The article text.</param>
    /// <param name="exists">Whether a repository-relative file exists.</param>
    /// <param name="readRunFile">The text of a results file by name, or null.</param>
    /// <param name="cloudGpuRan">Whether a completed or partial cloudgpu run file exists.</param>
    /// <returns>Every failure text.</returns>
    public static IReadOnlyList<string> ArticleChecks(string article, Func<string, bool> exists, Func<string, string?> readRunFile, bool cloudGpuRan) =>
    [
        .. ArticleCitations(article),
        .. ArticleSections(article),
        .. ArticleQuantities(article, exists),
        .. ArticleEnvironment(article, readRunFile),
        .. ArticleRanking(article),
        .. CloudGpuAbsence(article, cloudGpuRan),
        .. ArticleLength(article),
        .. AsciiAndLf(article),
    ];
}