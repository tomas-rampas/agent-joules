using System.Globalization;
using System.Text.RegularExpressions;

using AgentJoules.TestSupport.Docs;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The content honesty audit of AJ-143 (REQ-025 to REQ-028): nine rules over the public texts, as one pure function over the texts, the existence of files and the state of the cloudgpu tier. Nothing is read from the disk here: the caller supplies the texts, so a negative scenario mutates a copy in memory.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The sentence the scope rule (rule 4) looks for.</summary>
    public const string ScopePhrase = "GPU-board energy";

    /// <summary>The document that records the NIM attempt on the laptop, the third accepted source of rule 1.</summary>
    public const string NimAttemptDocument = "docs/nim-local-attempt.md";

    /// <summary>The methodology document, which rule 9 reads in addition to the audited files.</summary>
    public const string MethodologyDocument = "docs/methodology.md";

    private const string ReadmeFile = "README.md";
    private const string PostsFile = "content/linkedin-posts.md";
    private const string DemoScriptFile = "content/demo-script.md";
    private const string ArticleFile = "content/article.md";
    private const string StoryFile = "docs/devpost-story.md";
    private const string FeedbackFile = "docs/devpost-feedback.md";
    private const string PitchFile = "docs/devpost-pitch.md";
    private const string RubricHeading = "## Rubric and scores";
    private const int HonestyExcerptLength = 80;

    private static readonly string[] AllowedL40SWords = ["planned", "exhausted", "instead", "replaced", "replaces", "in place of", "not available"];

    private static readonly string[] DocumentSources = [CloudGpuRunDocument, NimAttemptDocument];

    private static readonly string[] ScopeFiles = [ReadmeFile, ArticleFile, DemoScriptFile, StoryFile];

    private static readonly string[] BrowserFiles = [ReadmeFile, DemoScriptFile, MethodologyDocument];

    /// <summary>The files the audit covers (criterion 1).</summary>
    public static IReadOnlyList<string> HonestyFiles { get; } =
        [ReadmeFile, PostsFile, DemoScriptFile, ArticleFile, StoryFile, FeedbackFile, "docs/devpost-checklist.md", PitchFile];

    /// <summary>The files a caller supplies to <see cref="HonestyAudit"/>: the audited files and the methodology document that rule 9 reads.</summary>
    public static IReadOnlyList<string> HonestyInputFiles { get; } = [.. HonestyFiles, MethodologyDocument];

    [GeneratedRegex(@"(?:\S+\s+)?(?:energy|joules).{0,20}[^0-9.]0 ?(?:joules|J)(?![A-Za-z])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HiddenZero();

    [GeneratedRegex(@"\b(?:nvidia|nebius)\b[^|\n]{0,40}\benergy\b[^|\n]{0,20}(?<![0-9.])0(?:\.0+)? ?(?:joules|kJ|kWh|Wh|J)(?![A-Za-z])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HiddenZeroHosted();

    [GeneratedRegex(@"greener|more efficient than|leaderboard|outperforms|beats |model [A-Za-z0-9-]+ is greener", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RankingWord();

    [GeneratedRegex(@"[0-9]+(\.[0-9]+)? ?(J|W|Wh|kJ|kWh|ms|tokens|%|EUR|USD|(?i:joules?|watts?))(?![A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex HonestyQuantity();

    [GeneratedRegex(@"nvapi-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyShape();

    [GeneratedRegex(@"[^\x00-\x7F]", RegexOptions.CultureInvariant)]
    private static partial Regex NonAsciiChar();

    [GeneratedRegex(@"(?<![\w-])(?<!(?i:not) )(?:measured|estimated|model-judged|N/A|not-reported)(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex HonestyLabel();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z\[""(`*])", RegexOptions.CultureInvariant)]
    private static partial Regex HonestySentenceBreak();

    [GeneratedRegex(@"\b(?:GH|GB|H|L|A|B|V)[0-9]{2,3}[A-Z]?\b|\bT4\b|\bL4\b|\bRTX [0-9]{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GpuFamilyToken();

    [GeneratedRegex(@"[A-Z]+[0-9]+[A-Z]*", RegexOptions.CultureInvariant)]
    private static partial Regex GpuNameToken();

    [GeneratedRegex(@"cloudgpu|AI Cloud|cloud GPU", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CloudMention();

    [GeneratedRegex(@"recorded without a browser\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BrowserStatement();

    [GeneratedRegex(@"recorded without (?:a browser|one)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MethodologyBrowserStatement();

    [GeneratedRegex(@"\bNebius\b.*\b(?:virtual machine|VM|instance)\b|\b(?:virtual machine|VM|instance)\b.*\bNebius\b", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex NebiusInstance();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>The first token of a GPU name that matches <c>[A-Z]+[0-9]+[A-Z]*</c>: <c>H100</c> for <c>NVIDIA H100 80GB HBM3</c>.</summary>
    /// <param name="gpuName">The <c>gpuName</c> of a run file.</param>
    /// <returns>The token, or null when the name has none.</returns>
    public static string? GpuTokenOf(string? gpuName) => string.IsNullOrWhiteSpace(gpuName) ? null : GpuNameToken().Match(gpuName) is { Success: true } match ? match.Value : null;

    /// <summary>Runs the nine honesty rules of AJ-143 over the texts.</summary>
    /// <param name="files">The texts by repository-relative path (forward slashes): the audited files and <see cref="MethodologyDocument"/>. The README is read outside the generated results block.</param>
    /// <param name="exists">Whether a repository-relative file exists (the source files of rule 1).</param>
    /// <param name="cloudGpu">The state of the cloudgpu tier; <see cref="CloudGpuTaglineRule.Absent"/> selects the fallback wording of rule 8.</param>
    /// <returns>The findings, empty when every rule holds.</returns>
    public static IReadOnlyList<string> HonestyAudit(IReadOnlyDictionary<string, string> files, Func<string, bool> exists, CloudGpuTierState cloudGpu)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(cloudGpu);

        var findings = new List<string>();
        bool cloudPresent = cloudGpu.State != CloudGpuTaglineRule.Absent;
        string? cloudToken = cloudPresent ? GpuTokenOf(cloudGpu.GpuName) : null;
        foreach (string path in HonestyFiles)
        {
            if (!files.TryGetValue(path, out string? raw))
            {
                findings.Add(Finding(1, "audited file missing", path, 1));
                continue;
            }

            string text = path == ReadmeFile ? ReadmeOutsideBlock(raw) : raw;
            AuditLines(findings, path, text.Split('\n'));
            AuditQuantities(findings, path, text, exists, SourcesLineValid(path, text, exists));
            AuditCloudGpu(findings, path, text, cloudPresent, cloudToken);
        }

        AuditScope(findings, files);
        foreach (string path in BrowserFiles)
        {
            string? text = files.GetValueOrDefault(path);
            Regex statement = path == MethodologyDocument ? MethodologyBrowserStatement() : BrowserStatement();
            if (text is null || !statement.IsMatch(path == ReadmeFile ? ReadmeOutsideBlock(text) : text))
            {
                findings.Add(Finding(9, "browser statement missing: " + path, path, 1));
            }
        }

        return findings;
    }

    // The generated block is replaced by as many line feeds as it spans, so the line numbers of the findings stay those of the file.
    private static string ReadmeOutsideBlock(string readme) =>
        GeneratedBlock().Replace(readme, static match => new string('\n', match.Value.Count(static c => c == '\n')), 1);

    private static string Finding(int rule, string text, string file, int line) =>
        string.Create(CultureInfo.InvariantCulture, $"rule {rule}: {text} ({file}:{line})");

    private static string Excerpt(string text)
    {
        string flat = Whitespace().Replace(text.Trim(), " ");

        return flat.Length <= HonestyExcerptLength ? flat : flat[..HonestyExcerptLength];
    }

    // Rules 3, 5, 6 and 7 look at one line at a time.
    private static void AuditLines(List<string> findings, string path, string[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            int number = index + 1;
            if (line.Contains('\r', StringComparison.Ordinal))
            {
                findings.Add(Finding(7, "CRLF", path, number));
            }

            Match zero = HiddenZero().Match(line);
            if (!zero.Success)
            {
                zero = HiddenZeroHosted().Match(line);
            }

            if (zero.Success)
            {
                findings.Add(Finding(3, "hidden zero: " + Excerpt(zero.Value), path, number));
            }

            foreach (Match ranking in RankingWord().Matches(line))
            {
                findings.Add(Finding(5, "ranking claim: " + ranking.Value.Trim(), path, number));
            }

            if (KeyShape().IsMatch(line))
            {
                // The excerpt is left out on purpose: a key-shaped value is never printed.
                findings.Add(Finding(6, "key material", path, number));
            }

            Match nonAscii = NonAsciiChar().Match(line);
            if (nonAscii.Success)
            {
                findings.Add(Finding(7, "non-ASCII character U+" + char.ConvertToUtf32(line, nonAscii.Index).ToString("X4", CultureInfo.InvariantCulture), path, number));
            }
        }
    }

    // Amendment (c): the Sources first line of the rendered posts names at least one results file and every one exists.
    private static bool SourcesLineValid(string path, string text, Func<string, bool> exists)
    {
        if (path != PostsFile)
        {
            return false;
        }

        string first = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')[0];
        if (!first.StartsWith("Sources:", StringComparison.Ordinal))
        {
            return false;
        }

        string[] names = [.. first["Sources:".Length..].Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Where(static name => name != "none")];

        return names.Length > 0 && names.All(name => exists("results/" + name));
    }

    // Rules 1 and 2: every quantity sits in a sentence or table row that holds a label word, and in a sentence, row or paragraph that names an existing source.
    private static void AuditQuantities(List<string> findings, string path, string text, Func<string, bool> exists, bool postsSourced)
    {
        foreach (HonestyUnit unit in Units(text, includeFenced: false))
        {
            List<Match> quantities = [.. HonestyQuantity().Matches(WithoutPlaceholders(unit.Text)).Cast<Match>()];
            if (quantities.Count == 0)
            {
                continue;
            }

            bool sourced = postsSourced || RunFilesMentioned(unit.Paragraph).Any(name => exists("results/" + name))
                || DocumentSources.Any(source => unit.Paragraph.Contains(source, StringComparison.Ordinal) && exists(source));
            bool labelled = HonestyLabel().IsMatch(unit.Text);
            foreach (Match quantity in quantities)
            {
                if (!sourced)
                {
                    findings.Add(Finding(1, "number without source: " + quantity.Value, path, unit.Line));
                }

                if (!labelled)
                {
                    findings.Add(Finding(2, "finding: no label: " + quantity.Value, path, unit.Line));
                }
            }
        }
    }

    // Rule 8: the cloudgpu wording agrees with the run file, or with its absence.
    private static void AuditCloudGpu(List<string> findings, string path, string text, bool present, string? token)
    {
        HashSet<int> exempt = ExemptLines(path, text.Split('\n'), present);
        foreach (HonestyUnit unit in Units(text, includeFenced: true).Where(unit => !exempt.Contains(unit.Line)))
        {
            bool hasL40S = unit.Text.Contains(CloudGpuTaglineRule.GpuToken, StringComparison.Ordinal);
            if (!present)
            {
                bool claim = hasL40S || unit.Text.Contains("Nebius AI Cloud", StringComparison.Ordinal);
                if (claim && !unit.Text.Contains("not run", StringComparison.Ordinal))
                {
                    findings.Add(Finding(8, "cloudgpu claim without run file: " + Excerpt(unit.Text), path, unit.Line));
                }

                continue;
            }

            if (hasL40S && !AllowedL40SWords.Any(word => unit.Text.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(Finding(8, "L40S outside a planned-or-replaced sentence: " + Excerpt(unit.Text), path, unit.Line));
            }

            if (token is not null && (CloudMention().IsMatch(unit.Text) || NebiusInstance().IsMatch(unit.Text)))
            {
                foreach (string other in GpuFamilyToken().Matches(unit.Text).Select(static match => match.Value).Where(value => value != token && value != CloudGpuTaglineRule.GpuToken).Distinct(StringComparer.Ordinal))
                {
                    findings.Add(Finding(8, $"cloudgpu measurement on {other}, the run file names {token}: " + Excerpt(unit.Text), path, unit.Line));
                }
            }
        }
    }

    // Rule 4: the scope sentence in the README, the article, the demo script, the story and every post variant.
    private static void AuditScope(List<string> findings, IReadOnlyDictionary<string, string> files)
    {
        foreach (string path in ScopeFiles)
        {
            string? text = files.GetValueOrDefault(path);
            if (text is not null && path == ReadmeFile)
            {
                text = ReadmeOutsideBlock(text);
            }

            if (text is null || !text.Contains(ScopePhrase, StringComparison.Ordinal))
            {
                findings.Add(Finding(4, "scope sentence missing: " + path, path, 1));
            }
        }

        if (!files.TryGetValue(PostsFile, out string? posts))
        {
            return;
        }

        IReadOnlyList<ContentVariant> variants = Variants(posts);
        string[] lines = Lines(posts);
        if (variants.Count == 0)
        {
            findings.Add(Finding(4, "scope sentence missing: " + PostsFile, PostsFile, 1));
        }

        foreach (ContentVariant variant in variants.Where(static variant => !string.Join('\n', variant.Lines).Contains(ScopePhrase, StringComparison.Ordinal)))
        {
            int heading = Array.IndexOf(lines, "## Variant: " + variant.Name);
            findings.Add(Finding(4, "scope sentence missing: " + variant.Name, PostsFile, heading + 1));
        }
    }

    // The lines the cloudgpu rule does not read: the historical drafts. With a run file the non-chosen pitch candidates B and C and the whole scoring section; without one also candidate A, and the Q5 design answer.
    private static HashSet<int> ExemptLines(string path, string[] lines, bool present)
    {
        var exempt = new HashSet<int>();
        if (path == PitchFile)
        {
            bool candidate = false;
            bool rubric = false;
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    candidate = false;
                    rubric = line.StartsWith(RubricHeading, StringComparison.Ordinal);
                }
                else if (line.StartsWith("**", StringComparison.Ordinal))
                {
                    candidate = line.StartsWith("**B**", StringComparison.Ordinal) || line.StartsWith("**C**", StringComparison.Ordinal)
                        || (!present && line.StartsWith(CloudGpuTaglineRule.CandidateALabel, StringComparison.Ordinal));
                }

                if (candidate || rubric)
                {
                    _ = exempt.Add(index + 1);
                }
            }
        }
        else if (path == FeedbackFile && !present)
        {
            bool inFive = false;
            for (int index = 0; index < lines.Length; index++)
            {
                if (lines[index].StartsWith("## ", StringComparison.Ordinal))
                {
                    inFive = lines[index].StartsWith("## Q5.", StringComparison.Ordinal);
                }

                if (inFive)
                {
                    _ = exempt.Add(index + 1);
                }
            }
        }

        return exempt;
    }

    // The sentences and table rows of a document with the paragraph each belongs to. A heading, a table row and a list item start a unit of their own; a fenced block is skipped unless asked for.
    private static List<HonestyUnit> Units(string text, bool includeFenced)
    {
        string[] lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var units = new List<HonestyUnit>();
        var paragraph = new List<string>();
        int start = 0;
        bool fenced = false;

        void Flush()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            string joined = string.Join('\n', paragraph);
            var segments = new List<(int From, int End)>();
            int from = 0;
            foreach (Match gap in HonestySentenceBreak().Matches(joined))
            {
                segments.Add((from, gap.Index));
                from = gap.Index + gap.Length;
            }

            segments.Add((from, joined.Length));
            foreach ((int segmentStart, int segmentEnd) in segments)
            {
                int offset = joined[..segmentStart].Count(static c => c == '\n');
                units.Add(new HonestyUnit(start + offset, joined[segmentStart..segmentEnd], joined));
            }

            paragraph.Clear();
        }

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                fenced = !fenced;
            }
            else if (fenced)
            {
                if (includeFenced && trimmed.Length > 0)
                {
                    units.Add(new HonestyUnit(index + 1, line, line));
                }
            }
            else if (trimmed.Length == 0)
            {
                Flush();
            }
            else if (trimmed.StartsWith('|') || trimmed.StartsWith('#'))
            {
                Flush();
                if (!TableSeparator().IsMatch(trimmed))
                {
                    units.Add(new HonestyUnit(index + 1, line, line));
                }
            }
            else
            {
                if (ListItem().IsMatch(line))
                {
                    Flush();
                }

                if (paragraph.Count == 0)
                {
                    start = index + 1;
                }

                paragraph.Add(line);
            }
        }

        Flush();

        return units;
    }

    private sealed record HonestyUnit(int Line, string Text, string Paragraph);
}