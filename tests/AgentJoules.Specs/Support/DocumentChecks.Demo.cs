using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>One row of a shot table of the demo script.</summary>
/// <param name="Shot">The shot number.</param>
/// <param name="Start">The start second.</param>
/// <param name="Duration">The duration in seconds.</param>
/// <param name="OnScreen">The on-screen content cell.</param>
/// <param name="Narration">The narration cell.</param>
/// <param name="LineIndex">The zero-based line of the row in the script.</param>
public sealed record ShotRow(int Shot, int Start, int Duration, string OnScreen, string Narration, int LineIndex);

/// <summary>A shot table of the script: the main one or a fallback variant.</summary>
/// <param name="Table">The parsed table.</param>
/// <param name="Rows">The rows whose first three cells are numbers.</param>
/// <param name="Fallback">True for a table after the first whose heading or lead text names a fallback or the absent cloudgpu tier.</param>
public sealed record ShotTable(MarkdownTable Table, IReadOnlyList<ShotRow> Rows, bool Fallback);

/// <summary>One step of the fenced block under <c>## Demo program</c>.</summary>
/// <param name="Step">The step number, from 1; the three VM commands of step 5 are one step.</param>
/// <param name="Verb">The verb: probe, run, report, validate, calibrate, dashboard, or <c>cloudgpu</c> for the VM step.</param>
/// <param name="Text">The command text; for the VM step the three commands joined with <c> ; </c>.</param>
public sealed record DemoCommand(int Step, string Verb, string Text);

/// <summary>The commands of the demo block and the failures found while reading it.</summary>
/// <param name="Commands">The steps in order.</param>
/// <param name="Failures">The failure texts.</param>
public sealed record DemoProgramResult(IReadOnlyList<DemoCommand> Commands, IReadOnlyList<string> Failures);

/// <summary>The state of the rehearsal screenshot.</summary>
public enum ScreenshotState
{
    /// <summary>The file exists and starts with the PNG signature.</summary>
    Present,

    /// <summary>The file does not exist yet: an open user action, not a failure.</summary>
    OpenUserAction,

    /// <summary>The file exists and is not a PNG.</summary>
    Invalid,
}

/// <summary>The outcome of the screenshot check.</summary>
/// <param name="State">The state.</param>
/// <param name="Message">The text to report.</param>
public sealed record ScreenshotStatus(ScreenshotState State, string Message);

/// <summary>
/// The checks of AJ-135 (the demo program) and AJ-136 (the demo script): the demo block, the shot tables, the budget, contiguity, words per second, the commands shown, the sources of numbers, the statements, the artifact
/// table, the run-file shape, the task set, the screenshot and the width check. Pure functions over strings, byte arrays and injected existence functions; nothing here reads or writes the repository.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The heading of the demo block.</summary>
    public const string DemoProgramHeading = "Demo program";

    /// <summary>The heading of the cloudgpu fallback section.</summary>
    public const string CloudGpuAbsentHeading = "If the cloudgpu tier is absent";

    /// <summary>The Try it out URL (the report page).</summary>
    public const string TryItOutUrl = "https://tomas-rampas.github.io/agent-joules/report/";

    /// <summary>The longest video the prompt allows, in seconds.</summary>
    public const int BudgetSeconds = 170;

    /// <summary>The planned total of the shot list, in seconds.</summary>
    public const int PlannedSeconds = 165;

    /// <summary>The most words per second a narration may carry.</summary>
    public const double MaxWordsPerSecond = 2.5;

    /// <summary>The text that the screenshot check reports while the user has not delivered the file.</summary>
    public const string ScreenshotOpenAction = "open user action: " + DashboardScreenshot;

    private const string VmPrefix = "./AgentJoules.Cli";
    private const string StoryShotsMarker = "What the video shows";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly string[] ShotColumns = ["Shot", "Start (s)", "Duration (s)", "On-screen content", "Narration"];

    private static readonly string[] RequiredShownVerbs = ["probe", "run", "dashboard"];

    // The decided form of the probe line under Commands shown: the bare probe, with no option.
    private const string ProbeCommandLine = "dotnet run --project src/AgentJoules.Cli -- probe";

    private static readonly string[] NarrationPhrases = ["illustrative", "not the source of any published figure"];

    private static readonly int[] BrowserShots = [5, 8];

    private static readonly string[] Step9Options = ["--out artifacts/demo-run", "--dashboard"];

    private static readonly int[] CheckedWidths = [1280, 1920];

    private static readonly string[] RankingWords = ["greener", "more efficient than", "beats", "outperforms"];

    /// <summary>The verbs of the ten demo steps in order; the VM step reads as <c>cloudgpu</c>.</summary>
    public static IReadOnlyList<string> DemoVerbs { get; } = ["probe", "run", "run", "calibrate", "cloudgpu", "validate", "report", "report", "run", "dashboard"];

    [GeneratedRegex(@"\(results/([^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex SourceCitation();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"^[0-9]{8}T[0-9]{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex RunFileName();

    [GeneratedRegex(@"^Validated (\d+) files$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ValidatedLine();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"--task (\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex TaskOption();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.-])[0-9]+(\.[0-9]+)?(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex BareNumber();

    [GeneratedRegex(@"recorded without a browser", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BrowserSentence();

    /// <summary>Whether a file name is a run file in the sense of AJ-080: a tier file, with or without the baseline suffix; probe and calibration files do not match.</summary>
    /// <param name="fileName">The file name without folder.</param>
    /// <returns>True for a run file.</returns>
    public static bool IsRunFileName(string fileName) => RunFileName().IsMatch(fileName);

    /// <summary>The number of <c>Validated &lt;n&gt; files</c> that a <c>validate results/</c> run must print for these file names.</summary>
    /// <param name="fileNames">The names of the files in <c>results/</c>.</param>
    /// <returns>The count of run files.</returns>
    public static int ExpectedValidatedCount(IEnumerable<string> fileNames) => fileNames.Count(IsRunFileName);

    /// <summary>The <c>n</c> of the <c>Validated &lt;n&gt; files</c> line of a standard output.</summary>
    /// <param name="stdout">The text.</param>
    /// <returns>The number, or null when the line is missing.</returns>
    public static int? ValidatedFiles(string stdout)
    {
        Match match = ValidatedLine().Match(stdout.Replace("\r", string.Empty, StringComparison.Ordinal));

        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The shot tables of a script: every table whose first header cell is <c>Shot</c>; the first is the main table. A fallback table may label its shots <c>F1</c> to <c>F9</c>; the main table needs numbers.</summary>
    /// <param name="script">The script text.</param>
    /// <returns>The tables in order.</returns>
    public static IReadOnlyList<ShotTable> ShotTables(string script)
    {
        MarkdownTable[] shots = [.. Tables(script).Where(static table => table.Header.Count > 0 && table.Header[0] == "Shot")];

        return
        [
            .. shots.Select((table, index) =>
            {
                bool fallback = index > 0 && IsFallbackContext(table);

                return new ShotTable(table, [.. table.Rows.Select(row => ParseShotRow(row, fallback)).OfType<ShotRow>()], fallback);
            }),
        ];
    }

    /// <summary>The header check: the lines <c>Upload target: public YouTube video</c>, <c>Budget: at most 170 seconds</c> and <c>Planned total: 165 seconds</c> occur above the first <c>## </c> heading.</summary>
    /// <param name="script">The script text.</param>
    /// <returns>One <c>missing header: &lt;label&gt;</c> per absent line.</returns>
    public static IReadOnlyList<string> ScriptHeader(string script)
    {
        string[] lines = Lines(script);
        int end = Array.FindIndex(lines, static line => line.StartsWith("## ", StringComparison.Ordinal));
        string[] header = end < 0 ? lines : lines[..end];
        (string Label, string Line)[] required =
        [
            ("Upload target", "Upload target: public YouTube video"),
            ("Budget", "Budget: at most 170 seconds"),
            ("Planned total", "Planned total: 165 seconds"),
        ];

        return [.. required.Where(pair => !header.Any(line => line.Contains(pair.Line, StringComparison.Ordinal))).Select(static pair => "missing header: " + pair.Label)];
    }

    /// <summary>The shot-list structure: the table, its five columns, shot numbers 1 to n, the budget and the planned total of the main table, and the budget of each fallback table.</summary>
    /// <param name="script">The script text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ShotBudget(string script)
    {
        IReadOnlyList<ShotTable> tables = ShotTables(script);
        ShotTable? main = tables.FirstOrDefault(static table => !table.Fallback);
        if (main is null)
        {
            return ["missing shot list table"];
        }

        var failures = new List<string>();
        failures.AddRange(ColumnFailures(main));
        int total = main.Rows.Sum(static row => row.Duration);
        if (total > BudgetSeconds)
        {
            failures.Add($"total {total} s exceeds {BudgetSeconds} s");
        }
        else if (total != PlannedSeconds)
        {
            failures.Add($"total {total} s differs from the planned total {PlannedSeconds} s");
        }

        ShotTable[] fallbacks = [.. tables.Where(static table => table.Fallback)];
        if (fallbacks.Length == 0)
        {
            failures.Add("missing fallback shot table");
        }

        foreach (ShotTable fallback in fallbacks)
        {
            int sum = fallback.Rows.Sum(static row => row.Duration);
            if (sum > BudgetSeconds)
            {
                failures.Add($"fallback total {sum} s exceeds {BudgetSeconds} s");
            }

            failures.AddRange(ColumnFailures(fallback));
        }

        return failures;
    }

    /// <summary>The contiguity check: shot 1 starts at 0, each start is the previous start plus the previous duration, shot numbers run 1 to n, and the main table ends at the planned total.</summary>
    /// <param name="script">The script text.</param>
    /// <returns>The failures; empty when the tables are contiguous.</returns>
    public static IReadOnlyList<string> ShotContiguity(string script)
    {
        var failures = new List<string>();
        foreach (ShotTable table in ShotTables(script))
        {
            string prefix = table.Fallback ? "fallback " : string.Empty;
            for (int index = 0; index < table.Rows.Count; index++)
            {
                ShotRow row = table.Rows[index];
                if (row.Shot != index + 1)
                {
                    failures.Add($"{prefix}shot {row.Shot}: expected shot number {index + 1}");
                }

                int expected = index == 0 ? 0 : table.Rows[index - 1].Start + table.Rows[index - 1].Duration;
                if (row.Start != expected)
                {
                    failures.Add($"{prefix}shot {row.Shot}: start {row.Start} s is not {expected} s (contiguous)");
                }
            }

            if (!table.Fallback && table.Rows.Count > 0)
            {
                ShotRow last = table.Rows[^1];
                if (last.Start + last.Duration != PlannedSeconds)
                {
                    failures.Add($"the last shot ends at {last.Start + last.Duration} s, not {PlannedSeconds} s");
                }
            }
        }

        return failures;
    }

    /// <summary>The speakability check: the words of each narration cell (without the <c>(results/...)</c> source marks, which are not spoken) per second of its shot.</summary>
    /// <param name="script">The script text.</param>
    /// <returns>One <c>shot &lt;n&gt;: &lt;ratio&gt; words per second exceeds 2.5</c> per shot over the limit.</returns>
    public static IReadOnlyList<string> WordsPerSecond(string script)
    {
        var failures = new List<string>();
        foreach (ShotTable table in ShotTables(script))
        {
            string prefix = table.Fallback ? "fallback " : string.Empty;
            foreach (ShotRow row in table.Rows)
            {
                double ratio = row.Duration > 0 ? (double)SpokenWords(row.Narration) / row.Duration : double.PositiveInfinity;
                if (ratio > MaxWordsPerSecond)
                {
                    failures.Add($"{prefix}shot {row.Shot}: {Ratio(ratio)} words per second exceeds {Ratio(MaxWordsPerSecond)}");
                }
            }
        }

        return failures;
    }

    /// <summary>The number of spoken words in a narration cell.</summary>
    /// <param name="narration">The cell text.</param>
    /// <returns>The count of words once line-break tags and source marks are removed.</returns>
    public static int SpokenWords(string narration) =>
        WordCount(SourceCitation().Replace(LineBreakTag().Replace(narration, " "), " "));

    /// <summary>The commands-shown check: the fenced block under <c>Commands shown</c> holds exactly three lines, all AJ commands with known verbs, the probe line being the bare probe, and covers probe, run and dashboard, its run and replay lines equal steps 9 and 10 of the Demo definition, and the script mentions <c>--dashboard</c> and <c>dashboard --replay</c>.</summary>
    /// <param name="script">The script text.</param>
    /// <param name="storyText">The text of <c>docs/plan/stories/M6-docs-content-demo.md</c>.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> CommandsShown(string script, string storyText)
    {
        var failures = new List<string>();
        IReadOnlyList<string> block = FencedBlock(SectionAtAnyLevel(script, "Commands shown"));
        if (block.Count == 0)
        {
            failures.Add("missing fenced block under Commands shown");
        }

        var verbs = new List<string>();
        string[] shown = [.. block.Select(static line => line.Trim()).Where(static line => line.Length > 0 && !line.StartsWith('#'))];
        if (shown.Length != 3)
        {
            failures.Add($"commands shown: expected 3 lines, found {shown.Length}");
        }

        foreach (string line in shown)
        {
            string? verb = VerbOf(line);
            if (verb is null)
            {
                failures.Add("not an AJ command: " + Cut(line));
            }
            else if (!CliCommands.Contains(verb))
            {
                failures.Add("unknown command: " + verb);
            }
            else if (verb == "probe")
            {
                verbs.Add(verb);
                if (line != ProbeCommandLine)
                {
                    failures.Add("commands shown: the probe line differs");
                }
            }
            else if (verb is "run" or "dashboard")
            {
                verbs.Add(verb);
                IReadOnlyDictionary<int, IReadOnlyList<string>> definition = DemoDefinitionCommands(storyText);
                int step = verb == "run" ? 9 : 10;
                if (!definition.TryGetValue(step, out IReadOnlyList<string>? expected) || !CommandsEqual([line], expected))
                {
                    failures.Add($"commands shown: the {verb} line differs from step {step} of the demo definition");
                }
            }
            else
            {
                verbs.Add(verb);
            }
        }

        failures.AddRange(RequiredShownVerbs.Where(required => block.Count > 0 && !verbs.Contains(required)).Select(static required => "commands shown lack: " + required));
        if (!script.Contains("--dashboard", StringComparison.Ordinal) || !script.Contains("dashboard --replay", StringComparison.Ordinal))
        {
            failures.Add("missing command: dashboard");
        }

        return failures;
    }

    /// <summary>The distinct, known verbs of the commands shown, for the help runs.</summary>
    /// <param name="script">The script text.</param>
    /// <returns>The verbs in order of first appearance.</returns>
    public static IReadOnlyList<string> CommandVerbsShown(string script) =>
        [.. FencedBlock(SectionAtAnyLevel(script, "Commands shown")).Select(static line => VerbOf(line.Trim())).OfType<string>().Where(CliCommands.Contains).Distinct(StringComparer.Ordinal)];

    /// <summary>The source rule: every quantity in the on-screen and narration cells is followed in its cell by <c>(results/&lt;file&gt;)</c> that names an existing file.</summary>
    /// <param name="script">The script text.</param>
    /// <param name="exists">Whether a repository-relative file exists, for example <c>results/x.json</c>.</param>
    /// <returns>One <c>number without source: &lt;match&gt;</c> (or <c>source file missing: ...</c>) per quantity.</returns>
    public static IReadOnlyList<string> ShotSources(string script, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var failures = new List<string>();
        foreach (string cell in ShotTables(script).SelectMany(static table => table.Rows).SelectMany(static row => (string[])[row.OnScreen, row.Narration]))
        {
            foreach (Match quantity in QuantityPattern().Matches(cell))
            {
                Match source = SourceCitation().Match(cell, quantity.Index + quantity.Length);
                if (!source.Success)
                {
                    failures.Add("number without source: " + quantity.Value);
                }
                else if (!exists("results/" + source.Groups[1].Value))
                {
                    failures.Add("source file missing: results/" + source.Groups[1].Value);
                }
            }

            // Criterion 6 says every number: a bare number (no unit) outside the citations and outside the quantities above needs a citation after it as well.
            string masked = SourceCitation().Replace(cell, static match => new string(' ', match.Length));
            MatchCollection quantities = QuantityPattern().Matches(cell);
            foreach (Match number in BareNumber().Matches(masked))
            {
                if (!quantities.Any(quantity => number.Index >= quantity.Index && number.Index < quantity.Index + quantity.Length) && !SourceCitation().IsMatch(cell, number.Index + number.Length))
                {
                    failures.Add("number without source: " + number.Value);
                }
            }
        }

        return failures;
    }

    /// <summary>The statements of the shots: shots 4 and 5 narration say illustrative and not the source of any published figure; shot 5 or 8 says the published results were recorded without a browser; shot 8 holds the energy sentence and the five labels; no narration ranks a tier; the fallback tables do not name Nebius AI Cloud; the fallback texts for shot 5 and shot 7 exist.</summary>
    /// <param name="script">The script text.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ShotStatements(string script)
    {
        var failures = new List<string>();
        IReadOnlyList<ShotTable> tables = ShotTables(script);
        ShotTable? main = tables.FirstOrDefault(static table => !table.Fallback);
        ShotRow? RowOf(int shot) => main?.Rows.FirstOrDefault(row => row.Shot == shot);
        foreach (int shot in new[] { 4, 5 })
        {
            string narration = RowOf(shot)?.Narration ?? string.Empty;
            failures.AddRange(NarrationPhrases.Where(phrase => !narration.Contains(phrase, StringComparison.OrdinalIgnoreCase)).Select(phrase => $"shot {shot}: narration lacks {phrase}"));
        }

        string cells58 = string.Join(' ', BrowserShots.Select(RowOf).OfType<ShotRow>().SelectMany(static row => (string[])[row.OnScreen, row.Narration]));
        if (!BrowserSentence().IsMatch(cells58))
        {
            failures.Add("missing statement: recorded without a browser");
        }

        string cells8 = RowOf(8) is { } eight ? eight.OnScreen + " " + eight.Narration : string.Empty;
        if (!cells8.Contains(EnergyScopeSentence, StringComparison.Ordinal))
        {
            failures.Add("shot 8 lacks: " + EnergyScopeSentence);
        }

        failures.AddRange(EvidenceLabels.Where(label => !cells8.Contains(label, StringComparison.Ordinal)).Select(static label => "shot 8 lacks: " + label));
        foreach (string narration in tables.SelectMany(static table => table.Rows).Select(static row => row.Narration))
        {
            failures.AddRange(RankingWords.Where(word => ContainsWord(narration, word)).Select(static word => "ranking claim: " + word));
        }

        foreach (ShotRow row in tables.Where(static table => table.Fallback).SelectMany(static table => table.Rows).Where(static row => row.Narration.Contains("Nebius AI Cloud", StringComparison.Ordinal)))
        {
            failures.Add($"fallback narration mentions Nebius AI Cloud: shot {row.Shot}");
        }

        if (!script.Contains("not run", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("missing fallback text: shot 7 not run");
        }

        if (!Lines(script).Any(static line => line.Contains("fallback", StringComparison.OrdinalIgnoreCase) && line.Contains("replay", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("missing fallback text: shot 5 replay");
        }

        return failures;
    }

    /// <summary>Every check of AJ-136 over a script.</summary>
    /// <param name="script">The script text.</param>
    /// <param name="storyText">The text of <c>docs/plan/stories/M6-docs-content-demo.md</c>.</param>
    /// <param name="exists">Whether a repository-relative file exists.</param>
    /// <returns>Every failure text.</returns>
    public static IReadOnlyList<string> DemoScript(string script, Func<string, bool> exists, string storyText) =>
    [
        .. ScriptHeader(script),
        .. ShotBudget(script),
        .. ShotContiguity(script),
        .. WordsPerSecond(script),
        .. CommandsShown(script, storyText),
        .. ShotSources(script, exists),
        .. ShotStatements(script),
    ];

    /// <summary>The script with the duration cell of one shot of the main table replaced; the starts of later shots are left as they are.</summary>
    /// <param name="script">The script text.</param>
    /// <param name="shot">The shot number.</param>
    /// <param name="duration">The new duration.</param>
    /// <returns>The new text.</returns>
    public static string WithShotDuration(string script, int shot, int duration) =>
        WithShotCell(script, shot, 2, duration.ToString(CultureInfo.InvariantCulture));

    /// <summary>The script with the start cell of one shot of the main table replaced.</summary>
    /// <param name="script">The script text.</param>
    /// <param name="shot">The shot number.</param>
    /// <param name="start">The new start second.</param>
    /// <returns>The new text.</returns>
    public static string WithShotStart(string script, int shot, int start) =>
        WithShotCell(script, shot, 1, start.ToString(CultureInfo.InvariantCulture));

    /// <summary>The script with the narration cell of one shot of the main table replaced.</summary>
    /// <param name="script">The script text.</param>
    /// <param name="shot">The shot number.</param>
    /// <param name="narration">The new narration text.</param>
    /// <returns>The new text.</returns>
    public static string WithShotNarration(string script, int shot, string narration) => WithShotCell(script, shot, 4, narration);

    /// <summary>The script with a line added to the fenced block under <c>Commands shown</c>.</summary>
    /// <param name="script">The script text.</param>
    /// <param name="command">The line.</param>
    /// <returns>The new text.</returns>
    public static string WithCommandShown(string script, string command) => WithBlockLine(script, "Commands shown", command);

    /// <summary>The script with a line added at the top of the first fenced block under a heading of any level.</summary>
    /// <param name="script">The script text.</param>
    /// <param name="headingText">The heading text without the <c>#</c> prefix.</param>
    /// <param name="command">The line.</param>
    /// <returns>The new text; unchanged when the heading or the block is missing.</returns>
    public static string WithBlockLine(string script, string headingText, string command)
    {
        string[] lines = Lines(script);
        int heading = Array.FindIndex(lines, line => line.StartsWith('#') && line.TrimStart('#').Trim() == headingText);
        if (heading < 0)
        {
            return script;
        }

        int open = Array.FindIndex(lines, heading, static line => line.TrimStart().StartsWith("```", StringComparison.Ordinal));

        return open < 0 ? script : string.Join('\n', [.. lines[..(open + 1)], command, .. lines[(open + 1)..]]);
    }

    /// <summary>The text with every case-insensitive occurrence of a phrase replaced.</summary>
    /// <param name="text">The text.</param>
    /// <param name="phrase">The phrase.</param>
    /// <param name="replacement">The replacement.</param>
    /// <returns>The new text.</returns>
    public static string WithoutPhrase(string text, string phrase, string replacement) =>
        Regex.Replace(text, Regex.Escape(phrase), replacement.Replace("$", "$$", StringComparison.Ordinal), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The text without the lines that contain a phrase.</summary>
    /// <param name="text">The text.</param>
    /// <param name="phrase">The phrase.</param>
    /// <returns>The new text.</returns>
    public static string WithoutLinesContaining(string text, string phrase) =>
        string.Join('\n', Lines(text).Where(line => !line.Contains(phrase, StringComparison.Ordinal)));

    /// <summary>The demo block: the fenced block under <c>## Demo program</c> read as ten steps and compared with the Demo definition of the story: each step, and each <c>&amp;&amp;</c> part of the VM step, equals the definition text (<c>&lt;ts&gt;</c> stands for a timestamp).</summary>
    /// <param name="script">The script text.</param>
    /// <param name="storyText">The text of <c>docs/plan/stories/M6-docs-content-demo.md</c>.</param>
    /// <returns>The steps and the failures; <c>demo step &lt;n&gt; differs from the demo definition</c> per differing step.</returns>
    public static DemoProgramResult DemoProgram(string script, string storyText)
    {
        DemoProgramResult block = ReadDemoBlock(script);
        var failures = new List<string>(block.Failures);
        IReadOnlyDictionary<int, IReadOnlyList<string>> definition = DemoDefinitionCommands(storyText);
        if (definition.Count == 0)
        {
            failures.Add("demo definition table not found");
        }
        else
        {
            foreach (DemoCommand step in block.Commands)
            {
                string[] parts = step.Text.Split([" && ", " ; "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (!definition.TryGetValue(step.Step, out IReadOnlyList<string>? expected) || !CommandsEqual(parts, expected))
                {
                    failures.Add($"demo step {step.Step} differs from the demo definition");
                }
            }
        }

        return new DemoProgramResult(block.Commands, failures);
    }

    // The commands equal one by one; the token <ts> of the definition stands for a run-file timestamp.
    private static bool CommandsEqual(string[] actual, IReadOnlyList<string> expected) =>
        actual.Length == expected.Count
        && actual.Zip(expected).All(static pair => Regex.IsMatch(pair.First, "^" + Regex.Escape(pair.Second).Replace("<ts>", "[0-9]{8}T[0-9]{6}Z", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant));

    // The demo block read as steps, without the comparison with the definition.
    private static DemoProgramResult ReadDemoBlock(string script)
    {
        var failures = new List<string>();
        string? heading = HasHeadingAtAnyLevel(script, DemoProgramHeading);
        if (heading is not null)
        {
            failures.Add(heading);
        }

        IReadOnlyList<string> block = FencedBlock(SectionAtAnyLevel(script, DemoProgramHeading));
        if (block.Count == 0)
        {
            failures.Add("missing fenced block under Demo program");
        }

        var steps = new List<DemoCommand>();
        bool vmOpen = false;
        foreach (string line in block.Select(static line => line.Trim()).Where(static line => line.Length > 0 && !line.StartsWith('#')))
        {
            if (line.StartsWith(VmPrefix, StringComparison.Ordinal))
            {
                string inner = FirstToken(line[VmPrefix.Length..]);
                if (inner is not ("probe" or "run" or "calibrate"))
                {
                    failures.Add("unknown command: " + inner);
                }

                if (vmOpen)
                {
                    DemoCommand open = steps[^1];
                    steps[^1] = open with { Text = open.Text + " ; " + line };
                }
                else
                {
                    steps.Add(new DemoCommand(steps.Count + 1, "cloudgpu", line));
                    vmOpen = true;
                }

                continue;
            }

            vmOpen = false;
            if (!line.StartsWith(DotnetRun, StringComparison.Ordinal))
            {
                failures.Add($"step {steps.Count + 1} does not start with \"{DotnetRun.Trim()}\"");
                steps.Add(new DemoCommand(steps.Count + 1, string.Empty, line));
                continue;
            }

            string verb = FirstToken(line[DotnetRun.Length..]);
            if (!CliCommands.Contains(verb))
            {
                failures.Add("unknown command: " + verb);
            }

            steps.Add(new DemoCommand(steps.Count + 1, verb, line));
        }

        if (steps.Count != DemoVerbs.Count)
        {
            failures.Add($"expected {DemoVerbs.Count} commands, found {steps.Count}");
        }
        else if (!steps.Select(static step => step.Verb).SequenceEqual(DemoVerbs))
        {
            failures.Add($"command order differs: expected {string.Join(", ", DemoVerbs)}; found {string.Join(", ", steps.Select(static step => step.Verb))}");
        }

        if (steps.Count >= 9)
        {
            failures.AddRange(Step9Options.Where(option => !steps[8].Text.Contains(option, StringComparison.Ordinal)).Select(static option => "step 9 lacks: " + option));
        }

        if (steps.Count >= 10 && !steps[9].Text.Contains("--replay", StringComparison.Ordinal))
        {
            failures.Add("step 10 lacks: --replay");
        }

        return new DemoProgramResult(steps, failures);
    }

    /// <summary>The script check of AJ-135 that needs only the text: the demo block, the fallback section heading, the Try it out URL and the browser statement.</summary>
    /// <param name="script">The script text.</param>
    /// <param name="storyText">The text of <c>docs/plan/stories/M6-docs-content-demo.md</c>.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> DemoScriptStructure(string script, string storyText)
    {
        var failures = new List<string>(DemoProgram(script, storyText).Failures);
        string? fallback = HasHeadingAtAnyLevel(script, CloudGpuAbsentHeading);
        if (fallback is not null)
        {
            failures.Add(fallback);
        }

        if (!script.Contains(TryItOutUrl, StringComparison.Ordinal))
        {
            failures.Add("missing Try it out URL: " + TryItOutUrl);
        }

        string? browser = BrowserStatement(script);
        if (browser is not null)
        {
            failures.Add(browser);
        }

        return failures;
    }

    /// <summary>The browser-load statement: the text says the published results were recorded without a browser open.</summary>
    /// <param name="text">The script or README text.</param>
    /// <returns>Null when stated, otherwise <c>missing statement: published results recorded without a browser</c>.</returns>
    public static string? BrowserStatement(string text) =>
        BrowserSentence().IsMatch(text) ? null : "missing statement: published results recorded without a browser";

    /// <summary>The shot-table comparison: shot number, start and duration of the script's main table against the table under <c>What the video shows</c> of the story file.</summary>
    /// <param name="script">The script text.</param>
    /// <param name="storyText">The text of <c>docs/plan/stories/M6-docs-content-demo.md</c>.</param>
    /// <returns>The differences, empty when all rows are equal.</returns>
    public static IReadOnlyList<string> ShotTableMatchesStory(string script, string storyText)
    {
        string[] story = Lines(storyText);
        int at = Array.FindIndex(story, static line => line.Contains(StoryShotsMarker, StringComparison.Ordinal));
        MarkdownTable? storyTable = at < 0 ? null : Tables(string.Join('\n', story[at..])).FirstOrDefault(static table => table.Header.Count > 0 && table.Header[0] == "Shot");
        if (storyTable is null)
        {
            return ["shot table not found in the story file"];
        }

        (int Shot, int Start, int Duration)[] expected =
        [
            .. storyTable.Rows
                .Where(static row => row.Cells.Count >= 3 && row.Cells.Take(3).All(static cell => int.TryParse(cell, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                .Select(static row => (int.Parse(row.Cells[0], CultureInfo.InvariantCulture), int.Parse(row.Cells[1], CultureInfo.InvariantCulture), int.Parse(row.Cells[2], CultureInfo.InvariantCulture))),
        ];
        ShotTable? main = ShotTables(script).FirstOrDefault(static table => !table.Fallback);
        if (main is null)
        {
            return ["missing shot list table"];
        }

        var failures = new List<string>();
        if (main.Rows.Count != expected.Length)
        {
            failures.Add($"shot rows: story {expected.Length}, script {main.Rows.Count}");
        }

        for (int index = 0; index < Math.Min(expected.Length, main.Rows.Count); index++)
        {
            (int shot, int start, int duration) = expected[index];
            ShotRow row = main.Rows[index];
            if (row.Shot != shot || row.Start != start || row.Duration != duration)
            {
                failures.Add($"shot {shot} differs: story {start}+{duration}, script {row.Start}+{row.Duration}");
            }
        }

        return failures;
    }

    /// <summary>The repository-relative paths named in the <c>Produces</c> (or <c>Artifact</c>) column of the script's tables, as code spans that look like paths.</summary>
    /// <param name="script">The script text.</param>
    /// <returns>The paths, distinct, in order; <c>artifacts/</c> paths (git-ignored) are left out.</returns>
    public static IReadOnlyList<string> DemoArtifactPaths(string script)
    {
        var paths = new List<string>();
        foreach (MarkdownTable table in Tables(script))
        {
            int column = table.Header.ToList().FindIndex(static cell => cell.Contains("produces", StringComparison.OrdinalIgnoreCase) || cell.Contains("artifact", StringComparison.OrdinalIgnoreCase));
            if (column < 0)
            {
                continue;
            }

            foreach (TableRow row in table.Rows.Where(row => row.Cells.Count > column))
            {
                string[] spans = [.. CodeSpan().Matches(row.Cells[column]).Select(static match => match.Groups[1].Value)];
                paths.AddRange((spans.Length > 0 ? spans : row.Cells[column].Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(IsPathLike));
            }
        }

        return [.. paths.Where(static path => !path.StartsWith("artifacts/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The artifact check (criterion 5): each path of the artifact table exists among <paramref name="files"/>, or is a cloudgpu artifact covered by the fallback section while no cloudgpu run file (completed or partial) exists. The screenshot is an open user action and is not checked here.</summary>
    /// <param name="script">The script text.</param>
    /// <param name="files">The repository-relative paths (forward slashes) that exist.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> MissingArtifacts(string script, IReadOnlyCollection<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        IReadOnlyList<string> paths = DemoArtifactPaths(script);
        if (paths.Count == 0)
        {
            return ["missing artifact table"];
        }

        bool cloudGpuRan = files.Any(static file => file.StartsWith("results/", StringComparison.Ordinal) && file.EndsWith("Z-cloudgpu.json", StringComparison.Ordinal) && IsRunFileName(file["results/".Length..]));
        bool fallback = HasHeadingAtAnyLevel(script, CloudGpuAbsentHeading) is null && !cloudGpuRan;

        return
        [
            .. paths
                .Where(path => path != DashboardScreenshot)
                .Where(path => !(fallback && path.Contains("cloudgpu", StringComparison.Ordinal)))
                .Where(path => !PathExists(path, files))
                .Select(static path => "missing artifact: " + path),
        ];
    }

    /// <summary>The screenshot check: absent is an open user action; present must start with the PNG signature.</summary>
    /// <param name="bytes">The bytes of <c>docs/report/dashboard.png</c>, or null when the file does not exist.</param>
    /// <returns>The status.</returns>
    public static ScreenshotStatus ScreenshotCheck(byte[]? bytes)
    {
        if (bytes is null)
        {
            return new ScreenshotStatus(ScreenshotState.OpenUserAction, ScreenshotOpenAction);
        }

        return bytes.Length >= PngSignature.Length && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)
            ? new ScreenshotStatus(ScreenshotState.Present, "screenshot ok: " + DashboardScreenshot)
            : new ScreenshotStatus(ScreenshotState.Invalid, "not a PNG: " + DashboardScreenshot);
    }

    /// <summary>The PNG signature, for fixtures.</summary>
    /// <returns>A copy of the eight bytes.</returns>
    public static byte[] PngHeader() => [.. PngSignature];

    /// <summary>The width check of the build log: the line <c>Dashboard width check: 1280 ok, 1920 ok</c>.</summary>
    /// <param name="buildLog">The build log text.</param>
    /// <returns>The failures: <c>width check not recorded</c>, or <c>width check incomplete: &lt;width&gt;</c> per width that is not recorded as ok.</returns>
    public static IReadOnlyList<string> WidthCheck(string buildLog)
    {
        string? line = Lines(buildLog).FirstOrDefault(static candidate => candidate.Contains("Dashboard width check:", StringComparison.Ordinal));
        if (line is null)
        {
            return ["width check not recorded"];
        }

        return [.. CheckedWidths.Where(width => !line.Contains($"{width} ok", StringComparison.Ordinal)).Select(static width => $"width check incomplete: {width}")];
    }

    /// <summary>The ignore check: <c>.gitignore</c> has a line that ignores the <c>artifacts/</c> folder (read from the text; no <c>git</c> process is started).</summary>
    /// <param name="gitignore">The text of <c>.gitignore</c>.</param>
    /// <returns>Null when ignored, otherwise a failure text.</returns>
    public static string? ArtifactsIgnored(string gitignore) =>
        Lines(gitignore).Select(static line => line.Trim()).Any(static line => line is "artifacts/" or "/artifacts/" or "artifacts" or "/artifacts" or "artifacts/*" or "artifacts/**")
            ? null
            : "artifacts/ is not git-ignored";

    /// <summary>The task-set check: exactly the three task files, each with a non-empty <c>name</c> field, names distinct.</summary>
    /// <param name="taskFiles">The task files by file name with their text.</param>
    /// <returns>The names read from the files (in file-name order) and the failures.</returns>
    public static (IReadOnlyList<string> Names, IReadOnlyList<string> Failures) TaskSet(IReadOnlyDictionary<string, string> taskFiles)
    {
        ArgumentNullException.ThrowIfNull(taskFiles);
        string[] expected = ["01-incident-brief.json", "02-api-migration.json", "03-energy-explainer.json"];
        var failures = new List<string>();
        if (!taskFiles.Keys.Order(StringComparer.Ordinal).SequenceEqual(expected))
        {
            failures.Add($"task set differs: expected {string.Join(", ", expected)}; found {string.Join(", ", taskFiles.Keys.Order(StringComparer.Ordinal))}");
        }

        var names = new List<string>();
        foreach (string file in taskFiles.Keys.Order(StringComparer.Ordinal))
        {
            string? name = JsonNode.Parse(taskFiles[file])?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                failures.Add("task without a name: " + file);
            }
            else
            {
                names.Add(name);
            }
        }

        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            failures.Add("task names are not distinct");
        }

        return (names, failures);
    }

    /// <summary>The task named by step 9 of the demo block.</summary>
    /// <param name="script">The script text.</param>
    /// <returns>The task name after <c>--task</c>, or null.</returns>
    public static string? DemoVideoTask(string script)
    {
        IReadOnlyList<DemoCommand> steps = ReadDemoBlock(script).Commands;
        Match match = steps.Count >= 9 ? TaskOption().Match(steps[8].Text) : Match.Empty;

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The run-shape check of the demo: for each tier the newest run file (by name) has <c>run.repeat</c> of at least 3 and 36 steps, and no step writes net energy as 0 under the label N/A.</summary>
    /// <param name="runFiles">The run files by file name with their text; names that are not tier run files are ignored.</param>
    /// <param name="tiers">The tiers to check, for example local, nvidia and nebius.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> DemoRunShape(IReadOnlyDictionary<string, string> runFiles, IReadOnlyList<string> tiers)
    {
        ArgumentNullException.ThrowIfNull(runFiles);
        ArgumentNullException.ThrowIfNull(tiers);
        var failures = new List<string>();
        foreach (string tier in tiers)
        {
            string? latest = runFiles.Keys.Where(name => RunFileName().IsMatch(name) && name.EndsWith($"Z-{tier}.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal).LastOrDefault();
            if (latest is null)
            {
                failures.Add("no run file for tier: " + tier);
                continue;
            }

            JsonNode root = JsonNode.Parse(runFiles[latest]) ?? throw new InvalidOperationException("The run file is empty: " + latest);
            int repeat = root["run"]?["repeat"]?.GetValue<int>() ?? 0;
            if (repeat < 3)
            {
                failures.Add($"run.repeat {repeat} is below 3: {tier}");
            }

            JsonArray steps = root["steps"]?.AsArray() ?? [];
            if (steps.Count != 36)
            {
                failures.Add($"{tier} has {steps.Count} steps, expected 36");
            }

            if (steps.Any(static step => step?["netJoules"] is JsonObject net && net["label"]?.GetValue<string>() == "N/A" && net["value"] is not null && net["value"]!.GetValue<double>() == 0))
            {
                failures.Add("step energy written as 0 under the label N/A: " + tier);
            }
        }

        return failures;
    }

    private static IReadOnlyList<string> ColumnFailures(ShotTable table) =>
        table.Table.Header.Take(ShotColumns.Length).SequenceEqual(ShotColumns) && table.Table.Header.Count >= ShotColumns.Length
            ? []
            : ["shot list columns differ: expected " + string.Join(" | ", ShotColumns)];

    // The shot cell of the main table is a number; the fallback table may label its shots F1, F2 and so on, read as shots 1, 2 and so on.
    private static ShotRow? ParseShotRow(TableRow row, bool fallback)
    {
        IReadOnlyList<string> cells = row.Cells;
        int shot = 0;
        if (cells.Count < 5
            || !int.TryParse(fallback && cells[0].StartsWith('F') ? cells[0][1..] : cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out shot)
            || !int.TryParse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture, out int start)
            || !int.TryParse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture, out int duration))
        {
            return null;
        }

        return new ShotRow(shot, start, duration, cells[3], cells[4], row.LineIndex);
    }

    private static bool IsFallbackContext(MarkdownTable table)
    {
        string context = table.Heading + "\n" + string.Join('\n', table.Preceding);

        return context.Contains("fallback", StringComparison.OrdinalIgnoreCase) || context.Contains("cloudgpu tier is absent", StringComparison.OrdinalIgnoreCase);
    }

    // The lines between the first fence line and the next fence line of a section.
    private static IReadOnlyList<string> FencedBlock(IReadOnlyList<string> section)
    {
        int open = section.ToList().FindIndex(static line => line.TrimStart().StartsWith("```", StringComparison.Ordinal));
        if (open < 0)
        {
            return [];
        }

        int close = section.ToList().FindIndex(open + 1, static line => line.TrimStart().StartsWith("```", StringComparison.Ordinal));

        return close < 0 ? [] : [.. section.Skip(open + 1).Take(close - open - 1)];
    }

    // The verb of a command line: the first token after the AJ, dotnet run or VM prefix; null when the line has none of them.
    private static string? VerbOf(string line)
    {
        string text = line.StartsWith("$ ", StringComparison.Ordinal) ? line[2..] : line;
        foreach (string prefix in new[] { DotnetRun, "AJ ", VmPrefix + " " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return FirstToken(text[prefix.Length..]);
            }
        }

        return null;
    }

    private static string FirstToken(string text) => text.Trim().Split(' ', 2)[0];

    private static string Cut(string text) => text.Length <= 60 ? text : text[..60];

    private static bool IsPathLike(string token) =>
        token.Contains('/', StringComparison.Ordinal)
        && !token.Contains(' ', StringComparison.Ordinal)
        && !token.Contains("://", StringComparison.Ordinal)
        && !token.Contains('*', StringComparison.Ordinal)
        && !token.StartsWith("--", StringComparison.Ordinal)
        && !token.StartsWith("./", StringComparison.Ordinal)
        && !token.StartsWith("AJ", StringComparison.Ordinal);

    private static bool PathExists(string path, IReadOnlyCollection<string> files)
    {
        if (path.EndsWith('/'))
        {
            return files.Any(file => file.StartsWith(path, StringComparison.Ordinal));
        }

        if (!path.Contains('<', StringComparison.Ordinal))
        {
            return files.Contains(path);
        }

        string pattern = "^" + Regex.Replace(Regex.Escape(path).Replace("<ts>", "[0-9]{8}T[0-9]{6}Z", StringComparison.Ordinal), "<[a-z]+>", "[^/]+") + "$";

        return files.Any(file => Regex.IsMatch(file, pattern, RegexOptions.CultureInvariant));
    }

    private static string WithShotCell(string script, int shot, int column, string value)
    {
        string[] lines = Lines(script);
        ShotTable? main = ShotTables(script).FirstOrDefault(static table => !table.Fallback);
        ShotRow? row = main?.Rows.FirstOrDefault(candidate => candidate.Shot == shot);
        if (row is null)
        {
            return script;
        }

        string[] cells = SplitRow(lines[row.LineIndex]);
        cells[column] = value;
        lines[row.LineIndex] = "| " + string.Join(" | ", cells.Select(static cell => cell.Replace("|", "\\|", StringComparison.Ordinal))) + " |";

        return string.Join('\n', lines);
    }
}