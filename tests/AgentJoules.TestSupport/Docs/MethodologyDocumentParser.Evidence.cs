using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// The evidence half of the contract of <c>docs/methodology.md</c> (the final M4 review, M-1, m-1 and m-4; REQ-022 and prompt section 6, honesty rule 1): <see cref="CheckEvidence"/> reads the markdown against the
/// committed files under <c>results/</c> and <c>tasks/</c> so that the document cannot go stale silently. The rules: every <c>results/*.json</c> name the document cites is a committed file (the list of
/// <c>git ls-tree -r --name-only HEAD -- results/</c>, never a working-tree file or the index); the baseline-unstable example quotes the mean, standard deviation, sample count, window seconds and coefficient of variation of the latest committed local run
/// rounded to three decimals; the throttled statement quotes that run's throttled step count, reason, peak range (rounded to one decimal) and embedded <c>powerLimitW</c>; the power limit item quotes the
/// <c>powerLimitW</c> of that run's embedded probe and of every committed probe it names, and names the latest committed probe; the offload item quotes <c>gpuOffloadPercent</c> of the run; the output caps item quotes the
/// caps of <c>tasks/*.json</c> and, per tier, the count of steps whose output tokens equal their task's cap in the latest committed run file of the tier; the REQ-032 compositing sentence is followed by the open
/// applications sentence; and the Baseline section holds the settle sentence. A quote is the first match of a fixed phrase on the line of its item; the reading never judges other prose.
/// </summary>
public static partial class MethodologyDocumentParser
{
    /// <summary>The sentence that follows the compositing sentence: the campaign ran with other GPU applications open (M-1).</summary>
    public const string OpenApplicationsSentence = "The 2026-10-25 campaign that produced the published local file ran with the user's other GPU applications open (the user's decision of 12:32Z recorded in the AJ-114 entry of docs/build-log.md), so that load sits in the baseline and in every local window.";

    /// <summary>The Baseline sentence that discloses the missing settle interval (m-4).</summary>
    public const string BaselineSettleSentence = "The baseline window starts after the warm-up call returns and the runtime version call that follows it, plus the configured settle interval Baseline:SettleSeconds (default 0, as in the published runs).";

    private const string BaselineHeading = "Baseline";
    private static readonly string[] Tiers = ["local", "nebius", "nvidia"];

    /// <summary>The committed <c>maxOutputTokens</c> of each task, read from <c>tasks/*.json</c>.</summary>
    /// <returns>The cap per task name.</returns>
    public static IReadOnlyDictionary<string, int> TaskCaps()
    {
        Dictionary<string, int> caps = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(Path.Combine(ConfigurationFixtures.RepositoryRoot(), "tasks"), "*.json"))
        {
            using JsonDocument task = JsonDocument.Parse(File.ReadAllText(path));
            caps[task.RootElement.GetProperty("name").GetString()!] = task.RootElement.GetProperty("maxOutputTokens").GetInt32();
        }

        return caps;
    }

    /// <summary>Checks the document against the committed evidence.</summary>
    /// <param name="markdown">The text of the document; carriage returns are ignored.</param>
    /// <param name="committedFiles">The repository-relative paths of the committed files under <c>results/</c> (<c>git ls-tree -r --name-only HEAD -- results/</c>).</param>
    /// <param name="readFile">Reads a repository-relative path; returns null when the file does not exist.</param>
    /// <param name="taskCaps">The <c>maxOutputTokens</c> per task name.</param>
    /// <returns>One message per broken rule; empty when the document passes.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> CheckEvidence(string markdown, IReadOnlyCollection<string> committedFiles, Func<string, string?> readFile, IReadOnlyDictionary<string, int> taskCaps)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(committedFiles);
        ArgumentNullException.ThrowIfNull(readFile);
        ArgumentNullException.ThrowIfNull(taskCaps);

        string text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = text.Split('\n');
        Section[] sections = CheckHeadings(lines, []);
        List<string> failures = [];

        // Rule 1: every cited results file is a committed file.
        // A run file, a probe or a calibration file named without its folder (the first draft cited 20261025T024349Z-local.json) is the same citation.
        foreach (string cited in ResultsFileName().Matches(text).Select(static match => match.Value)
            .Concat(RunFileName().Matches(text).Select(static match => "results/" + match.Value))
            .Concat(BareFileName().Matches(text).Select(static match => "results/" + match.Value))
            .Distinct(StringComparer.Ordinal))
        {
            if (!committedFiles.Contains(cited, StringComparer.Ordinal))
            {
                failures.Add($"the document cites {cited}, which is not a committed file under results/ (git ls-tree HEAD results/)");
            }
        }

        string? localName = Latest(committedFiles, "local", readFile);
        JsonElement? run = localName is null ? null : Load(localName, readFile, failures);
        string? limitations = SectionText(sections, LimitationsHeading);
        string[] limitationLines = sections.FirstOrDefault(static section => string.Equals(section.Name, LimitationsHeading, StringComparison.Ordinal))?.Lines ?? [];
        string[] baselineLines = sections.FirstOrDefault(static section => string.Equals(section.Name, BaselineHeading, StringComparison.Ordinal))?.Lines ?? [];
        if (localName is null || run is null)
        {
            failures.Add("no committed results/<yyyyMMddTHHmmssZ>-local.json file to read the quoted values from");
        }
        else
        {
            CheckBaselineExample(baselineLines, localName, run.Value, failures);
            CheckThrottled(Item(limitationLines, "laptop throttling"), localName, run.Value, failures);
            CheckPowerLimit(Item(limitationLines, "power limit"), localName, run.Value, committedFiles, readFile, failures);
            CheckOffload(Item(limitationLines, "offload"), localName, run.Value, failures);
        }

        CheckOutputCaps(Item(limitationLines, "output caps"), committedFiles, readFile, taskCaps, failures);

        // Rule 4 and rule 6: the added sentences, verbatim, in place.
        if (limitations is null || !limitations.Contains(CompositingSentence + " " + OpenApplicationsSentence, StringComparison.Ordinal))
        {
            failures.Add("the Limitations section lacks the open applications sentence directly after the compositing sentence: \"" + OpenApplicationsSentence + "\"");
        }

        if (!Collapse(string.Join('\n', baselineLines)).Contains(BaselineSettleSentence, StringComparison.Ordinal))
        {
            failures.Add("the Baseline section lacks the settle sentence: \"" + BaselineSettleSentence + "\"");
        }

        return failures;
    }

    // The newest committed primary file of a tier that RunFileSelector.Latest would accept (see IsSelectableRunFile).
    private static string? Latest(IReadOnlyCollection<string> committedFiles, string tier, Func<string, string?> readFile) =>
        committedFiles
            .Where(path => Regex.IsMatch(path, @"^results/\d{8}T\d{6}Z-" + tier + @"\.json\z", RegexOptions.CultureInvariant))
            .OrderDescending(StringComparer.Ordinal)
            .FirstOrDefault(path => readFile(path) is not { } json || IsSelectableRunFile(json));

    /// <summary>
    /// Whether <c>RunFileSelector.Latest</c> would accept a run file, judged from its text: <c>run.status</c> is <c>completed</c> or <c>partial</c> and <c>run.flags</c> lacks <c>non-publishable</c>; the statuses
    /// <c>running</c>, <c>failed</c> and <c>incomplete</c> are skipped. A file that cannot be read or has no <c>run</c> header is not skipped here: the selector reports it as a problem, and the caller's load of it fails visibly.
    /// </summary>
    /// <param name="json">The text of the run file.</param>
    /// <returns>False when the selector skips the file; true otherwise.</returns>
    public static bool IsSelectableRunFile(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("run", out JsonElement run) || run.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            string? status = run.TryGetProperty("status", out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() : null;
            return status is not ("running" or "failed" or "incomplete")
                && !(run.TryGetProperty("flags", out JsonElement flags) && flags.ValueKind == JsonValueKind.Array
                    && flags.EnumerateArray().Any(static flag => flag.ValueKind == JsonValueKind.String && string.Equals(flag.GetString(), "non-publishable", StringComparison.Ordinal)));
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static JsonElement? Load(string name, Func<string, string?> readFile, List<string> failures)
    {
        string? json = readFile(name);
        if (json is null)
        {
            failures.Add($"the committed file {name} cannot be read");
            return null;
        }

        using JsonDocument file = JsonDocument.Parse(json);
        return file.RootElement.Clone();
    }

    // The collapsed text of the bullet that starts "- <name>" (the bullet is one line in the document); empty when there is none.
    private static string Item(string[] lines, string name) =>
        Collapse(lines.FirstOrDefault(line => line.StartsWith("- " + name, StringComparison.Ordinal)) ?? string.Empty);

    private static string Rounded(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero).ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Number(JsonElement parent, params string[] path)
    {
        JsonElement current = parent;
        foreach (string member in path)
        {
            current = current.GetProperty(member);
        }

        return current.GetRawText();
    }

    private static double Value(JsonElement parent, params string[] path) => double.Parse(Number(parent, path), CultureInfo.InvariantCulture);

    // Compares one quote with the stored text and records a message that names both.
    private static void Compare(string what, string? quoted, string stored, string file, string rule, List<string> failures)
    {
        if (quoted is null)
        {
            failures.Add($"{what}: the document quotes no value; {file} stores {stored} ({rule})");
        }
        else if (!string.Equals(quoted, stored, StringComparison.Ordinal))
        {
            failures.Add($"{what}: the document quotes {quoted} but {file} stores {stored} ({rule})");
        }
    }

    private static string? Group(string line, string pattern)
    {
        Match match = Regex.Match(line, pattern, RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    // Rule 2, the baseline-unstable example: the bullet cites the latest local run and quotes its baseline rounded to three decimals.
    private static void CheckBaselineExample(string[] baselineLines, string file, JsonElement run, List<string> failures)
    {
        string line = Item(baselineLines, "Baseline-unstable");
        const string rule = "rounded to three decimals";
        const string what = "baseline-unstable example";
        if (!line.Contains(file, StringComparison.Ordinal))
        {
            failures.Add($"{what}: the document does not cite the latest committed local run {file}");
        }

        Compare(what + " mean", Group(line, @"mean (\d+\.\d+) W"), Rounded(Value(run, "baseline", "meanW", "value"), 3), file, rule, failures);
        Compare(what + " standard deviation", Group(line, @"standard deviation (\d+\.\d+) W"), Rounded(Value(run, "baseline", "stdDevW", "value"), 3), file, rule, failures);
        Compare(what + " sample count", Group(line, @"(\d+) samples over"), Number(run, "baseline", "sampleCount"), file, "as stored", failures);
        Compare(what + " window seconds", Group(line, @"samples over (\d+\.\d+) s"), Rounded(Value(run, "baseline", "seconds"), 3), file, rule, failures);
        Compare(what + " coefficient of variation", Group(line, @"coefficient of variation (\d+\.\d+)"), Rounded(Value(run, "baseline", "coefficientOfVariation"), 3), file, rule, failures);
        if (!run.GetProperty("baseline").GetProperty("flags").EnumerateArray().Any(static flag => string.Equals(flag.GetString(), "baseline-unstable", StringComparison.Ordinal)))
        {
            failures.Add($"{what}: {file} does not carry the baseline-unstable flag the document shows");
        }
    }

    // Rule 2, the EDGE-013 statement: throttled step count, reason, peak range (one decimal) and the embedded probe's power limit.
    private static void CheckThrottled(string line, string file, JsonElement run, List<string> failures)
    {
        const string what = "EDGE-013 statement";
        JsonElement[] steps = [.. run.GetProperty("steps").EnumerateArray()];
        if (!line.Contains(file, StringComparison.Ordinal))
        {
            failures.Add($"{what}: the document does not cite the latest committed local run {file}");
        }

        int throttled = steps.Count(static step => step.GetProperty("flags").EnumerateArray().Any(static flag => string.Equals(flag.GetString(), "throttled", StringComparison.Ordinal)));
        Compare(what + " throttled steps", Group(line, @"on (\d+) of its \d+ steps"), throttled.ToString(CultureInfo.InvariantCulture), file, "count of steps flagged throttled", failures);
        Compare(what + " step total", Group(line, @"on \d+ of its (\d+) steps"), steps.Length.ToString(CultureInfo.InvariantCulture), file, "count of steps", failures);
        string[] reasons = [.. steps.SelectMany(static step => step.GetProperty("throttleReasons").EnumerateArray()).Select(static reason => reason.GetString() ?? string.Empty).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Compare(what + " reason", Group(line, @"with the reason (\w+)"), string.Join(',', reasons), file, "the distinct throttleReasons over all steps", failures);
        double[] peaks = [.. steps.Select(static step => Value(step, "peakW", "value"))];
        Compare(what + " lowest peak", Group(line, @"peak power (\d+\.\d) to \d+\.\d W"), Rounded(peaks.Min(), 1), file, "rounded to one decimal", failures);
        Compare(what + " highest peak", Group(line, @"peak power \d+\.\d to (\d+\.\d) W"), Rounded(peaks.Max(), 1), file, "rounded to one decimal", failures);
        Compare(what + " power limit", Group(line, @"powerLimitW (\d+(?:\.\d+)?)"), Number(run, "probe", "powerLimitW"), file, "the embedded probe's powerLimitW as stored", failures);
    }

    // Rule 3: the run-time value (the probe embedded in the latest local run) and every committed probe the item names, each quoted with its file name; the latest committed probe must be named.
    private static void CheckPowerLimit(string line, string file, JsonElement run, IReadOnlyCollection<string> committedFiles, Func<string, string?> readFile, List<string> failures)
    {
        const string what = "power limit item";
        Compare(what + " run-time value", ClauseQuote(line, file), Number(run, "probe", "powerLimitW"), file, "the embedded probe's powerLimitW as stored", failures);
        string[] cited = [.. ProbeFileName().Matches(line).Select(static match => match.Value).Distinct(StringComparer.Ordinal)];
        string? latest = committedFiles.Where(static path => ProbeFileName().IsMatch(path)).Order(StringComparer.Ordinal).LastOrDefault();
        if (latest is null || !cited.Contains(latest, StringComparer.Ordinal))
        {
            failures.Add($"{what}: the document does not name the latest committed probe {latest}");
        }

        foreach (string probe in cited)
        {
            if (Load(probe, readFile, failures) is { } stored)
            {
                Compare(what + " " + probe, ClauseQuote(line, probe), Number(stored, "powerLimitW"), probe, "powerLimitW as stored", failures);
            }
        }
    }

    // The number after "powerLimitW" in the clause (up to the next semicolon) that follows the file name.
    private static string? ClauseQuote(string line, string file) => Group(line, Regex.Escape(file) + @"[^;]*?powerLimitW (-?\d+(?:\.\d+)?)");

    private static void CheckOffload(string line, string file, JsonElement run, List<string> failures) =>
        Compare("offload item", Group(line, @"gpuOffloadPercent (\d+) in " + Regex.Escape(file)), Number(run, "environment", "gpuOffloadPercent"), file, "environment.gpuOffloadPercent as stored", failures);

    // Rule 5: the caps of tasks/*.json and, per tier, the steps at the cap of the latest committed run file; the disclosure phrases.
    private static void CheckOutputCaps(string line, IReadOnlyCollection<string> committedFiles, Func<string, string?> readFile, IReadOnlyDictionary<string, int> taskCaps, List<string> failures)
    {
        const string what = "output caps item";
        if (line.Length == 0)
        {
            failures.Add($"{what}: the Limitations section has no \"- output caps\" item");
            return;
        }

        foreach ((string task, int cap) in taskCaps)
        {
            Compare(what + " cap of " + task, Group(line, Regex.Escape(task) + @" (\d+)"), cap.ToString(CultureInfo.InvariantCulture), "tasks/*.json", "maxOutputTokens as stored", failures);
        }

        foreach (string tier in Tiers)
        {
            string? name = Latest(committedFiles, tier, readFile);
            Match quote = Regex.Match(line, @"(\d+) of (\d+) " + tier + @" steps \((results/[^)]+?\.json)\)", RegexOptions.CultureInvariant);
            if (name is null || !quote.Success)
            {
                failures.Add($"{what}: no count for the {tier} tier or no committed {tier} run file");
                continue;
            }

            if (!string.Equals(quote.Groups[3].Value, name, StringComparison.Ordinal))
            {
                failures.Add($"{what}: the {tier} count cites {quote.Groups[3].Value}, not the latest committed {tier} run file {name}");
            }

            if (Load(name, readFile, failures) is not { } tierRun)
            {
                continue;
            }

            JsonElement[] steps = [.. tierRun.GetProperty("steps").EnumerateArray()];
            int atCap = steps.Count(step => taskCaps.TryGetValue(step.GetProperty("task").GetString() ?? string.Empty, out int cap)
                && step.GetProperty("outputTokens").TryGetProperty("value", out JsonElement tokens)
                && tokens.ValueKind == JsonValueKind.Number
                && tokens.GetInt64() == cap);
            Compare(what + " " + tier + " steps at the cap", quote.Groups[1].Value, atCap.ToString(CultureInfo.InvariantCulture), name, "steps whose outputTokens equal their task's maxOutputTokens", failures);
            Compare(what + " " + tier + " step total", quote.Groups[2].Value, steps.Length.ToString(CultureInfo.InvariantCulture), name, "count of steps", failures);
        }

        foreach (string phrase in new[] { "judged as returned", "carries no flag", "finish-reason flag", "M6 or M7" })
        {
            if (!line.Contains(phrase, StringComparison.Ordinal))
            {
                failures.Add($"{what}: the item lacks the phrase \"{phrase}\"");
            }
        }
    }

    [GeneratedRegex(@"results/[\w.-]*?\.json(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex ResultsFileName();

    [GeneratedRegex(@"\d{8}T\d{6}Z-(?:local|nvidia|nebius|cloudgpu)\.json(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex RunFileName();

    [GeneratedRegex(@"(?<![\w-])(?:probe|calibration)-\d{8}T\d{6}Z(?:-[\w.-]*?)?\.json(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex BareFileName();

    [GeneratedRegex(@"results/probe-\d{8}T\d{6}Z\.json(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex ProbeFileName();
}