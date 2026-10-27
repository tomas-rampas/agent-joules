using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The checks of AJ-132 (REQ-025): the four Devpost headings, the model IDs against <c>docs/endpoints.md</c>, the local-runtime sentence, the results files the README names, the source of every cost figure, the cloud
/// section with or without a cloudgpu run, the methodology summary and the NIM-local outcome. Pure functions over strings, like the rest of <see cref="DocumentChecks"/>.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The heading of the cloud section.</summary>
    public const string CloudSectionHeading = "Other Nebius tools or services used";

    /// <summary>The sentence of the cloud section when no completed or partial cloudgpu run exists.</summary>
    public const string NoCloudSentence = "None besides Token Factory.";

    /// <summary>The label of the cloudgpu row of the results block when the tier did not run.</summary>
    public const string CloudGpuNotRunLabel = "cloudgpu | not run";

    /// <summary>The source file of the cloud facts.</summary>
    public const string CloudGpuRunDocument = "docs/cloudgpu-run.md";

    /// <summary>The headings the Devpost form requires, each once.</summary>
    public static IReadOnlyList<string> DevpostHeadings { get; } =
    [
        "How this project uses NVIDIA Nemotron",
        "Where Nebius Token Factory accelerated the workflow",
        CloudSectionHeading,
        JudgesHeading,
    ];

    [GeneratedRegex(@"(?<![\w/.-])(?:nvidia|Qwen)/[A-Za-z0-9._-]*[A-Za-z0-9](?::[A-Za-z0-9._-]*[A-Za-z0-9])?", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedModelId();

    // A name:tag in a code span or in plain prose; a name that follows a path separator, a dot, a colon or a word character is part of something longer (a container image path, a file name).
    [GeneratedRegex(@"(?<![\w/.:-])([a-z][a-z0-9.-]*):([a-z0-9](?:[a-z0-9._-]*[a-z0-9])?)", RegexOptions.CultureInvariant)]
    private static partial Regex TaggedModelId();

    [GeneratedRegex(@"(?<![A-Za-z0-9.-])[0-9]+(?:\.[0-9]+)*(?![A-Za-z0-9-])", RegexOptions.CultureInvariant)]
    private static partial Regex FigureToken();

    [GeneratedRegex(@"[0-9]+(?:\.[0-9]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex AnyNumber();

    [GeneratedRegex(@"about USD ([0-9]+(?:\.[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex RoundedCost();

    [GeneratedRegex(@"Final cost: USD ([0-9]+(?:\.[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex FinalCost();

    [GeneratedRegex(@"\bmodel ID\s*:?\s*`([^`\s]+)`", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StatedModelId();

    [GeneratedRegex(@"^- model: (.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelLine();

    [GeneratedRegex(@"Local numbers were produced with (?<runtime>\S+) running (?<tag>\S+?)\.(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeSentence();

    [GeneratedRegex(@"(?<![\w/.-])(?:results/)?(?<name>\d{8}T\d{6}Z-(?:local|cloudgpu|nvidia|nebius)(?:-baseline)?\.json)", RegexOptions.CultureInvariant)]
    private static partial Regex ResultsFileName();

    [GeneratedRegex(@"(?:USD|EUR|\$)\s?\d|\d\s?(?:USD|EUR)", RegexOptions.CultureInvariant)]
    private static partial Regex CostFigure();

    [GeneratedRegex(@"preset:\s*([A-Za-z0-9._-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex PresetFact();

    [GeneratedRegex(@"region:\s*([a-z0-9-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex RegionFact();

    [GeneratedRegex(@"served by (NIM [0-9][0-9.]*[0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex EngineFact();

    [GeneratedRegex(@"GPU[- ]hours?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GpuHours();

    [GeneratedRegex(@"^<!-- results:begin -->\r?$.*?^<!-- results:end -->\r?$\r?\n?", RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex GeneratedBlock();

    [GeneratedRegex(@"^\s*(?:[-*]|\d+\.)\s", RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();

    /// <summary>The README text with the generated results block removed: the lines from <c>&lt;!-- results:begin --&gt;</c> through <c>&lt;!-- results:end --&gt;</c>, inclusive.</summary>
    /// <remarks>
    /// The block is tool output that <c>report --verify-readme</c> checks, so the prose checks read the README outside it. The block generator writes a word-joiner entity
    /// (<c>&amp;#8288;</c>) before every <c>.</c> of a model ID, so a model-ID pattern that stops at <c>&amp;</c> would cut the ID short inside the block. The text is not decoded here.
    /// </remarks>
    /// <param name="readme">The README text.</param>
    /// <returns>The text without the block; the text unchanged when no block exists.</returns>
    public static string WithoutGeneratedBlock(string readme)
    {
        ArgumentNullException.ThrowIfNull(readme);

        return GeneratedBlock().Replace(readme, string.Empty, 1);
    }

    /// <summary>The Devpost structure check: the four headings once each at any level, and no leftover placeholder text anywhere in the README.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>Every failure text, empty when the README passes.</returns>
    public static IReadOnlyList<string> DevpostStructure(string readme)
    {
        var failures = new List<string>();
        failures.AddRange(DevpostHeadings.Select(heading => HasHeadingAtAnyLevel(readme, heading)).OfType<string>());
        if (WithoutGeneratedBlock(readme).Contains(Placeholder, StringComparison.Ordinal))
        {
            failures.Add("leftover placeholder text");
        }

        return failures;
    }

    /// <summary>The model IDs the README names: an <c>nvidia/</c> or <c>Qwen/</c> ID anywhere (not inside a longer path), a <c>name:tag</c> in a code span or in plain prose (a tag that is only digits is a port), and a code span that follows the words <c>model ID</c>.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>The distinct IDs in order of appearance.</returns>
    public static IReadOnlyList<string> ModelIdsNamed(string readme)
    {
        ArgumentNullException.ThrowIfNull(readme);
        string outside = WithoutGeneratedBlock(readme);
        string[] notModels = ["results", "http", "https", "file"];
        IEnumerable<(int At, string Id)> prefixed = PrefixedModelId().Matches(outside).Select(static match => (match.Index, match.Value));
        IEnumerable<(int At, string Id)> tagged = TaggedModelId().Matches(outside)
            .Where(match => !notModels.Contains(match.Groups[1].Value, StringComparer.Ordinal) && !match.Groups[2].Value.All(char.IsAsciiDigit))
            .Select(static match => (match.Index, match.Groups[1].Value + ":" + match.Groups[2].Value));
        IEnumerable<(int At, string Id)> stated = StatedModelId().Matches(outside).Select(static match => (match.Index, match.Groups[1].Value));

        return [.. prefixed.Concat(tagged).Concat(stated).OrderBy(static pair => pair.At).Select(static pair => pair.Id).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The IDs of the <c>- model: &lt;id&gt;</c> lines of <c>docs/endpoints.md</c>.</summary>
    /// <param name="endpoints">The text of the document.</param>
    /// <returns>The IDs.</returns>
    public static IReadOnlyList<string> EndpointModelIds(string endpoints) =>
        [.. Lines(endpoints).Select(static line => ModelLine().Match(line)).Where(static match => match.Success).Select(static match => match.Groups[1].Value)];

    /// <summary>The model-ID check: every ID the README names is a <c>- model:</c> line of the endpoints document.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="endpoints">The text of <c>docs/endpoints.md</c>.</param>
    /// <returns>One <c>model id not in docs/endpoints.md: &lt;id&gt;</c> per unlisted ID.</returns>
    public static IReadOnlyList<string> ModelIds(string readme, string endpoints)
    {
        IReadOnlyList<string> listed = EndpointModelIds(endpoints);

        return [.. ModelIdsNamed(readme).Where(id => !listed.Contains(id, StringComparer.Ordinal)).Select(static id => "model id not in docs/endpoints.md: " + id)];
    }

    /// <summary>The local-runtime sentence check: <c>Local numbers were produced with &lt;runtime&gt; running &lt;tag&gt;.</c> with the runtime of the latest local results file and one of its model IDs.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="runtime">The <c>environment.localRuntime</c> of the latest local results file.</param>
    /// <param name="modelIds">The model IDs of <c>environment.models</c> of that file.</param>
    /// <returns>The failures; empty when the sentence is right.</returns>
    public static IReadOnlyList<string> LocalRuntimeSentence(string readme, string runtime, IReadOnlyCollection<string> modelIds)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        Match sentence = RuntimeSentence().Match(WithoutGeneratedBlock(readme));
        if (!sentence.Success)
        {
            return ["local runtime sentence missing"];
        }

        var failures = new List<string>();
        if (sentence.Groups["runtime"].Value != runtime)
        {
            failures.Add($"local runtime sentence names {sentence.Groups["runtime"].Value}, the results file says {runtime}");
        }

        if (!modelIds.Contains(sentence.Groups["tag"].Value, StringComparer.Ordinal))
        {
            failures.Add("local tag not in the results file: " + sentence.Groups["tag"].Value);
        }

        return failures;
    }

    /// <summary>The names of the results files the README names, bare or with the <c>results/</c> prefix.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>The distinct file names.</returns>
    public static IReadOnlyList<string> ResultsFilesNamed(string readme) =>
        [.. ResultsFileName().Matches(WithoutGeneratedBlock(readme)).Select(static match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>The results-file check: every file the README names exists.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="exists">Whether a file of that name exists under <c>results/</c>.</param>
    /// <returns>One <c>results file not found: &lt;name&gt;</c> per missing file.</returns>
    public static IReadOnlyList<string> ResultsFilesExist(string readme, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        return [.. ResultsFilesNamed(readme).Where(name => !exists(name)).Select(static name => "results file not found: " + name)];
    }

    /// <summary>The source-line check: in the cloud section, a line with <c>observed cost</c> or a cost figure must name <c>docs/cloudgpu-run.md</c>.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>One <c>number without source: ...</c> per offending line.</returns>
    public static IReadOnlyList<string> SourceLines(string readme)
    {
        var failures = new List<string>();
        foreach (string line in SectionAtAnyLevel(readme, CloudSectionHeading))
        {
            if (line.Contains(CloudGpuRunDocument, StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("observed cost", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("number without source: observed cost");
            }
            else if (CostFigure().IsMatch(line))
            {
                failures.Add("number without source: cost figure");
            }
        }

        return failures;
    }

    /// <summary>The cloud section check by the state of the tier: with no completed or partial cloudgpu run the section is exactly <see cref="NoCloudSentence"/> and the README labels the tier <c>cloudgpu | not run</c>; with a run the section names the source file. The word <c>cloudgpu</c> occurs in both cases.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="cloudGpuRan">Whether a completed or partial cloudgpu run file exists.</param>
    /// <returns>The failures; empty when the section is right.</returns>
    public static IReadOnlyList<string> CloudSection(string readme, bool cloudGpuRan)
    {
        var failures = new List<string>();
        string[] body = [.. SectionAtAnyLevel(readme, CloudSectionHeading).Where(static line => line.Trim().Length > 0).Select(static line => line.Trim())];
        if (!readme.Contains("cloudgpu", StringComparison.Ordinal))
        {
            failures.Add("README does not mention cloudgpu");
        }

        if (cloudGpuRan)
        {
            if (!body.Any(static line => line.Contains(CloudGpuRunDocument, StringComparison.Ordinal)))
            {
                failures.Add("cloud section does not name " + CloudGpuRunDocument);
            }

            if (body.Any(static line => line.Contains(NoCloudSentence, StringComparison.Ordinal)))
            {
                failures.Add("cloud section says none although a cloudgpu run exists");
            }
        }
        else
        {
            if (!body.SequenceEqual([NoCloudSentence]))
            {
                failures.Add("cloud section is not the fixed sentence: " + NoCloudSentence);
            }

            if (!readme.Contains(CloudGpuNotRunLabel, StringComparison.Ordinal))
            {
                failures.Add("README does not label the tier: " + CloudGpuNotRunLabel);
            }
        }

        return failures;
    }

    /// <summary>The cloud facts check against the run record: the section names the preset, the region and the engine that <c>docs/cloudgpu-run.md</c> gives for the VM of the run, GPU-hours and a cost figure (the figure itself is the whole-run cost line of the user decision of 2026-10-26, not a literal phrase).</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="cloudRunDocument">The text of <c>docs/cloudgpu-run.md</c>.</param>
    /// <returns>One <c>cloud section lacks: ...</c> per missing fact.</returns>
    public static IReadOnlyList<string> CloudFacts(string readme, string cloudRunDocument)
    {
        string[] lines = Lines(cloudRunDocument);
        int run = Array.FindIndex(lines, static line => line.StartsWith("VM2", StringComparison.Ordinal));
        string text = string.Join('\n', run < 0 ? lines : lines[run..]);
        string section = string.Join('\n', SectionAtAnyLevel(readme, CloudSectionHeading));
        var failures = new List<string>();
        foreach ((string label, Match fact) in new[] { ("preset", PresetFact().Match(text)), ("region", RegionFact().Match(text)), ("engine", EngineFact().Match(text)) })
        {
            if (!fact.Success)
            {
                failures.Add("cloudgpu-run.md has no " + label);
            }
            else if (!section.Contains(fact.Groups[1].Value, StringComparison.Ordinal))
            {
                failures.Add($"cloud section lacks: {label} {fact.Groups[1].Value}");
            }
        }

        if (!GpuHours().IsMatch(section))
        {
            failures.Add("cloud section lacks: GPU-hours");
        }

        if (!CostFigure().IsMatch(section))
        {
            failures.Add("cloud section lacks: the cost figure");
        }

        return failures;
    }

    /// <summary>The cloud figures check: every number of the cloud section is a number of <c>docs/cloudgpu-run.md</c>, except the rounded whole-run cost (<c>about USD 7</c>), which must equal the final cost line of the file rounded to a whole number. A token written against letters or hyphens (<c>H100</c>, <c>eu-north1</c>, a file stamp) is not a figure.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="cloudRunDocument">The text of <c>docs/cloudgpu-run.md</c>.</param>
    /// <returns>One <c>cloud figure not in docs/cloudgpu-run.md: &lt;token&gt;</c> per number the file does not hold.</returns>
    public static IReadOnlyList<string> CloudFigures(string readme, string cloudRunDocument)
    {
        ArgumentNullException.ThrowIfNull(cloudRunDocument);
        string section = string.Join('\n', SectionAtAnyLevel(readme, CloudSectionHeading));
        HashSet<string> known = [.. AnyNumber().Matches(cloudRunDocument).Select(static match => match.Value)];
        Match finalCost = FinalCost().Match(cloudRunDocument);
        string? rounded = finalCost.Success
            ? Math.Round(double.Parse(finalCost.Groups[1].Value, CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
            : null;
        var failures = new List<string>();
        if (rounded is null)
        {
            failures.Add("cloudgpu-run.md has no final cost");
        }

        foreach (Match match in RoundedCost().Matches(section))
        {
            if (match.Groups[1].Value != rounded)
            {
                failures.Add("cloud figure not in docs/cloudgpu-run.md: about USD " + match.Groups[1].Value);
            }
        }

        string withoutRounded = RoundedCost().Replace(section, string.Empty);
        failures.AddRange(FigureToken().Matches(withoutRounded).Select(static match => match.Value).Where(token => !known.Contains(token)).Distinct(StringComparer.Ordinal).Select(static token => "cloud figure not in docs/cloudgpu-run.md: " + token));

        return failures;
    }

    /// <summary>The methodology summary check, over the README outside the generated results block (the block is tool output and would satisfy the prose checks by itself): it links <c>docs/methodology.md</c>, gives seven honesty rules as list lines, names the judge model, says judge calls are excluded from the totals, and for each self-judged results file has a line that says self-judging and names the file.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="judgeModelId">The judge model ID of the configuration.</param>
    /// <param name="selfJudgedFiles">The names of the latest results files, of every tier, whose <c>run.selfJudged</c> is true.</param>
    /// <returns>One <c>methodology summary lacks: ...</c> per missing element.</returns>
    public static IReadOnlyList<string> MethodologySummary(string readme, string judgeModelId, IReadOnlyCollection<string> selfJudgedFiles)
    {
        ArgumentException.ThrowIfNullOrEmpty(judgeModelId);
        ArgumentNullException.ThrowIfNull(selfJudgedFiles);
        readme = WithoutGeneratedBlock(readme);
        var failures = new List<string>();
        if (!readme.Contains("docs/methodology.md", StringComparison.Ordinal))
        {
            failures.Add("methodology summary lacks: docs/methodology.md");
        }

        if (!readme.Contains(judgeModelId, StringComparison.Ordinal))
        {
            failures.Add("methodology summary lacks: judge model " + judgeModelId);
        }

        if (!Lines(readme).Any(static line => line.Contains("judge", StringComparison.OrdinalIgnoreCase) && line.Contains("excluded", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("methodology summary lacks: judge calls excluded from workflow totals");
        }

        failures.AddRange(
            selfJudgedFiles
                .Where(file => !Lines(readme).Any(line => line.Contains("self-judg", StringComparison.OrdinalIgnoreCase) && line.Contains(file, StringComparison.Ordinal)))
                .Select(static file => "methodology summary lacks: self-judging disclosure naming " + file));

        string? heading = HeadingsOf(Lines(readme)).Where(static entry => entry.Text.Contains("honesty", StringComparison.OrdinalIgnoreCase)).Select(static entry => entry.Text).FirstOrDefault();
        int rules = heading is null ? 0 : SectionAtAnyLevel(readme, heading).Count(static line => ListItem().IsMatch(line));
        if (rules < 7)
        {
            failures.Add($"methodology summary lacks: seven honesty rules, one line each (found {rules})");
        }

        return failures;
    }

    /// <summary>The NIM-local outcome check: the heading <c>NIM-local outcome</c> once and a link to <c>docs/nim-local-attempt.md</c>.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>The failures; empty when the outcome section is right.</returns>
    public static IReadOnlyList<string> NimLocalOutcome(string readme)
    {
        var failures = new List<string>();
        if (HasHeadingAtAnyLevel(readme, "NIM-local outcome") is { } heading)
        {
            failures.Add(heading);
        }

        if (!SectionAtAnyLevel(readme, "NIM-local outcome").Any(static line => line.Contains("docs/nim-local-attempt.md", StringComparison.Ordinal)))
        {
            failures.Add("NIM-local outcome does not link docs/nim-local-attempt.md");
        }

        return failures;
    }
}