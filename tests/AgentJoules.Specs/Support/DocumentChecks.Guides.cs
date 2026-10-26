using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

using AgentJoules.Cli;
using AgentJoules.Core.Cli;

namespace AgentJoules.Specs.Support;

/// <summary>One row of the exit-code table of the README: <c>| &lt;code&gt; | &lt;meaning&gt; |</c>.</summary>
/// <param name="Code">The exit code, 0 to 7.</param>
/// <param name="Meaning">The text of the second cell.</param>
public sealed record ExitCodeRow(int Code, string Meaning);

/// <summary>The outcome of a link check: one failure text per link that did not answer 200, naming the URL.</summary>
/// <param name="Failures">The failure texts; empty when every link answered 200.</param>
public sealed record LinkCheckResult(IReadOnlyList<string> Failures)
{
    /// <summary>True when every link answered 200; only then may the README be marked as verified.</summary>
    public bool Verified => Failures.Count == 0;
}

/// <summary>
/// The checks of AJ-131 (REQ-025, REQ-032): the guide headings at any level, the key-free judges section, the option-versus-help check, the exit-code table, the key-material check, the dashboard screenshot link
/// rule, the Reproduce list and the Prior art section. Pure functions over strings; the one asynchronous member takes the <see cref="HttpClient"/> from the caller so that a test supplies a fake handler.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The heading of the key-free path for a judge.</summary>
    public const string JudgesHeading = "Judges without a GPU";

    /// <summary>The heading of the command reference.</summary>
    public const string CliReferenceHeading = "CLI reference";

    /// <summary>The repository-relative path of the dashboard screenshot.</summary>
    public const string DashboardScreenshot = "docs/report/dashboard.png";

    /// <summary>The text of the line the build log carries after the prior-art links were requested.</summary>
    public const string LinksCheckedMarker = "Prior-art links checked";

    private const string DotnetRun = "dotnet run --project src/AgentJoules.Cli -- ";

    private static readonly string[] HostOptions = ["--project", "--no-build", "--no-restore", "--configuration", "--filter", "--verbosity", "--framework", "--runtime", "--self-contained"];

    /// <summary>The five headings of the guide sections, each of which must occur once at any level.</summary>
    public static IReadOnlyList<string> GuideHeadings { get; } = ["Quick start", JudgesHeading, CliReferenceHeading, "Reproduce", "Prior art"];

    /// <summary>The four actions of the judges section.</summary>
    public static IReadOnlyList<string> JudgeActions { get; } = ["dotnet test", "validate results/", "docs/report/index.html", "dashboard --replay"];

    /// <summary>The number of actions of the judges section.</summary>
    public const int JudgeActionCount = 4;

    /// <summary>The six commands of the CLI reference.</summary>
    public static IReadOnlyList<string> CliCommands { get; } = ["probe", "run", "report", "validate", "calibrate", "dashboard"];

    /// <summary>The sources the Prior art section must cite, each as a text that must occur in it.</summary>
    public static IReadOnlyList<string> PriorArtSources { get; } = ["2407.16893", "TokenPowerBench", "Watt Counts", "2604.09048", "greentoken"];

    [GeneratedRegex(@"^(#{1,6}) (.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"(?<![\w-])--[A-Za-z][A-Za-z0-9-]*", RegexOptions.CultureInvariant)]
    private static partial Regex LongOption();

    [GeneratedRegex(@"^\|\s*([0-7])\s*\|\s*([^|]*?)\s*(?:\|.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ExitCodeLine();

    [GeneratedRegex(@"nvapi-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyMaterialPattern();

    // The CLI verbs that run a model or read the GPU: run, calibrate and probe after the "--" that ends the dotnet options or after the program name, or the words calibrate and probe anywhere.
    [GeneratedRegex(@"(?:--\s+|\./AgentJoules\.Cli\s+)(?:run|calibrate|probe)\b|\b(?:calibrate|probe)\b", RegexOptions.CultureInvariant)]
    private static partial Regex GpuCommand();

    [GeneratedRegex(@"^\|\s*(\d+)\b[^|]*\|([^|]*)\|", RegexOptions.CultureInvariant)]
    private static partial Regex DemoRow();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex BacktickedText();

    [GeneratedRegex(@"^(\d+)\.\s", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedItem();

    [GeneratedRegex(@"https?://[^\s)\]>""'`]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    /// <summary>The heading check at any level: a heading line (<c>#</c> to <c>######</c>) with exactly this text, once. Headings inside fenced blocks do not count.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="heading">The heading text without the <c>#</c> prefix.</param>
    /// <returns>Null when the heading occurs once, otherwise <c>missing heading: &lt;heading&gt;</c> (or <c>duplicate heading: ...</c>).</returns>
    public static string? HasHeadingAtAnyLevel(string text, string heading)
    {
        ArgumentException.ThrowIfNullOrEmpty(heading);
        int count = HeadingsOf(Lines(text)).Count(entry => entry.Text == heading);

        return count switch
        {
            1 => null,
            0 => "missing heading: " + heading,
            _ => "duplicate heading: " + heading,
        };
    }

    /// <summary>The lines of a section found by a heading of any level: from the line after it to the line before the next heading of the same or a higher level (a <c>###</c> section ends at the next <c>###</c> or <c>##</c>).</summary>
    /// <param name="text">The document text.</param>
    /// <param name="heading">The heading text without the <c>#</c> prefix.</param>
    /// <returns>The section lines; empty when the heading is missing.</returns>
    public static IReadOnlyList<string> SectionAtAnyLevel(string text, string heading)
    {
        string[] lines = Lines(text);
        (int start, int end) = SectionBounds(lines, heading);

        return start < 0 ? [] : lines[start..end];
    }

    /// <summary>The text with the body of a section replaced (the heading line is kept).</summary>
    /// <param name="text">The document text.</param>
    /// <param name="heading">The heading text without the <c>#</c> prefix.</param>
    /// <param name="body">The new body lines.</param>
    /// <returns>The new text; the input unchanged when the heading is missing.</returns>
    public static string WithSectionBody(string text, string heading, IReadOnlyList<string> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string[] lines = Lines(text);
        (int start, int end) = SectionBounds(lines, heading);
        if (start < 0)
        {
            return text;
        }

        return string.Join('\n', [.. lines[..start], string.Empty, .. body, string.Empty, .. lines[end..]]);
    }

    /// <summary>The text without the heading line of a section (any level); the body stays.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="heading">The heading text without the <c>#</c> prefix.</param>
    /// <returns>The text.</returns>
    public static string WithoutHeading(string text, string heading)
    {
        string[] lines = Lines(text);
        HeadingAt? found = HeadingsOf(lines).Where(entry => entry.Text == heading).Select(static entry => (HeadingAt?)entry).FirstOrDefault();

        return found is null ? text : string.Join('\n', lines.Where((_, index) => index != found.Value.Index));
    }

    /// <summary>The judges section check: the four actions are present and the section needs no key. The heading itself is checked by <see cref="GuideStructure"/>.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>The failures; empty when the section is missing (the heading check reports that) or passes.</returns>
    public static IReadOnlyList<string> JudgesSection(string readme)
    {
        IReadOnlyList<string> section = SectionAtAnyLevel(readme, JudgesHeading);
        if (section.Count == 0)
        {
            return [];
        }

        var failures = new List<string>();
        int actions = section.Count(static line => OrderedItem().IsMatch(line));
        if (actions != JudgeActionCount)
        {
            failures.Add($"judges section has {actions} actions, not {JudgeActionCount}");
        }

        if (section.Any(static line => line.Contains("_API_KEY", StringComparison.Ordinal) || line.Contains("--tier", StringComparison.Ordinal)))
        {
            failures.Add("judges section needs a key");
        }

        if (section.Any(static line => GpuCommand().IsMatch(line)))
        {
            failures.Add("judges section needs a GPU");
        }

        failures.AddRange(JudgeActions.Where(action => !section.Any(line => line.Contains(action, StringComparison.Ordinal))).Select(static action => "judges section lacks: " + action));

        return failures;
    }

    /// <summary>The README structure check of AJ-131 and AJ-132: the five guide headings once each at any level, and the judges section.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>Every failure text, empty when the README passes.</returns>
    public static IReadOnlyList<string> GuideStructure(string readme)
    {
        var failures = new List<string>();
        failures.AddRange(GuideHeadings.Select(heading => HasHeadingAtAnyLevel(readme, heading)).OfType<string>());
        failures.AddRange(JudgesSection(readme));

        return failures;
    }

    /// <summary>The long options the README names for a command in its CLI reference: every <c>--name</c> outside fenced blocks in the subsection of the command and in the table rows that start with the command. The options of <c>dotnet</c> itself are not counted.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="command">The command name, for example <c>run</c>.</param>
    /// <returns>The distinct options in order of appearance.</returns>
    public static IReadOnlyList<string> DocumentedOptions(string readme, string command)
    {
        string[] lines = Lines(readme);

        return
        [
            .. CommandLineIndexes(lines, command)
                .SelectMany(index => LongOption().Matches(lines[index]).Where(match => !IsCrossReference(lines[index], match.Index, command)).Select(static match => match.Value))
                .Where(static option => !HostOptions.Contains(option, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];
    }

    // An option written right after the name of another command ("run --dashboard" in the dashboard section) is a reference to that command, which its own section documents.
    private static bool IsCrossReference(string line, int optionAt, string command)
    {
        string[] words = line[..optionAt].Split([' ', '`', '(', '\t'], StringSplitOptions.RemoveEmptyEntries);

        return words.Length > 0 && words[^1] != command && CliCommands.Contains(words[^1], StringComparer.Ordinal);
    }

    /// <summary>The option check: every option must be printed by the help text of the command.</summary>
    /// <param name="options">The options the README names.</param>
    /// <param name="helpText">The standard output of <c>AJ &lt;command&gt; --help</c>.</param>
    /// <returns>One <c>option not in help: --x</c> per option that the help lacks.</returns>
    public static IReadOnlyList<string> OptionsNotInHelp(IEnumerable<string> options, string helpText)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(helpText);

        return [.. options.Where(option => !Regex.IsMatch(helpText, "(?<![\\w-])" + Regex.Escape(option) + "(?![\\w-])", RegexOptions.CultureInvariant)).Select(static option => "option not in help: " + option)];
    }

    /// <summary>The README with one more option in the subsection (or table row) of a command: a bullet after the heading of the subsection, or the option appended to the first table row of the command.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="command">The command name.</param>
    /// <param name="option">The option, for example <c>--fast</c>.</param>
    /// <returns>The mutated text; the input unchanged when the README has no place for the command.</returns>
    public static string WithOptionInCommand(string readme, string command, string option)
    {
        string[] lines = Lines(readme);
        string[] all = lines;
        HeadingAt? heading = CommandHeading(all, command);
        if (heading is not null)
        {
            return string.Join('\n', [.. lines[..(heading.Value.Index + 1)], string.Empty, "- `" + option + "`: an option that does not exist.", .. lines[(heading.Value.Index + 1)..]]);
        }

        int row = Array.FindIndex(lines, line => IsCommandRow(line, command));
        if (row < 0)
        {
            return readme;
        }

        lines[row] = lines[row].TrimEnd().TrimEnd('|') + " `" + option + "` |";

        return string.Join('\n', lines);
    }

    /// <summary>The rows of the exit-code table: every line that starts with <c>| 0 |</c> to <c>| 7 |</c>.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>The rows in order of appearance.</returns>
    public static IReadOnlyList<ExitCodeRow> ExitCodeRows(string readme) =>
        [.. Lines(readme).Select(static line => ExitCodeLine().Match(line)).Where(static match => match.Success).Select(static match => new ExitCodeRow(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value))];

    /// <summary>The exit-code table check: the codes 0 to 7 each once, and each meaning names the meaning of the <see cref="ExitCode"/> of the same number (<see cref="ExitCodeMapper.Describe"/>); code 5 names the interrupted run and the loopback port.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>The failures; empty when the table is complete and consistent.</returns>
    public static IReadOnlyList<string> ExitCodeTable(string readme)
    {
        IReadOnlyList<ExitCodeRow> rows = ExitCodeRows(readme);
        var failures = new List<string>();
        foreach (ExitCode code in Enum.GetValues<ExitCode>())
        {
            ExitCodeRow[] matching = [.. rows.Where(row => row.Code == (int)code)];
            if (matching.Length == 0)
            {
                failures.Add("exit code missing: " + (int)code);
            }
            else if (matching.Length > 1)
            {
                failures.Add("exit code repeated: " + (int)code);
            }
            else if (!MeaningMatches(matching[0].Meaning, code))
            {
                failures.Add($"exit code {(int)code} does not mean \"{ExitCodeMapper.Describe(code)}\": {matching[0].Meaning}");
            }
        }

        ExitCodeRow? interrupted = rows.FirstOrDefault(static row => row.Code == (int)ExitCode.Unreachable);
        if (interrupted is not null)
        {
            if (!interrupted.Meaning.Contains("interrupt", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("exit code 5 does not name the interrupted run");
            }

            if (!interrupted.Meaning.Contains("port", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("exit code 5 does not name the missing loopback port");
            }
        }

        return failures;
    }

    /// <summary>The key-material check: no <c>nvapi-</c> followed by 8 or more key characters.</summary>
    /// <param name="text">The text of the file.</param>
    /// <param name="fileName">The file name for the failure text.</param>
    /// <returns>Null when clean, otherwise <c>key material in &lt;fileName&gt;</c>.</returns>
    public static string? KeyMaterial(string text, string fileName)
    {
        ArgumentNullException.ThrowIfNull(text);

        return KeyMaterialPattern().IsMatch(text) ? "key material in " + fileName : null;
    }

    /// <summary>The dashboard screenshot link rule (AJ-131 criterion 8): the README links the file if and only if it exists.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="screenshotExists">Whether <c>docs/report/dashboard.png</c> exists in the workspace the README belongs to.</param>
    /// <returns>Null when the rule holds, otherwise <c>dangling link: ...</c> (link without file) or <c>missing link: ...</c> (file without link).</returns>
    public static string? DashboardLink(string readme, bool screenshotExists)
    {
        ArgumentNullException.ThrowIfNull(readme);
        bool linked = readme.Contains(DashboardScreenshot, StringComparison.Ordinal);

        return (linked, screenshotExists) switch
        {
            (true, false) => "dangling link: " + DashboardScreenshot,
            (false, true) => "missing link: " + DashboardScreenshot,
            _ => null,
        };
    }

    /// <summary>The numbers of the ordered list items (<c>1. text</c> at the start of a line) of a section, in order.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="heading">The section heading.</param>
    /// <returns>The item numbers.</returns>
    public static IReadOnlyList<int> OrderedItemNumbers(string readme, string heading) =>
        [.. SectionAtAnyLevel(readme, heading).Select(static line => OrderedItem().Match(line)).Where(static match => match.Success).Select(static match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))];

    /// <summary>The Reproduce check: an ordered list numbered 1 to <paramref name="steps"/> without a gap.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="steps">The number of steps, ten for the demo of AJ-135.</param>
    /// <returns>The failure text, or null.</returns>
    public static string? ReproduceSteps(string readme, int steps)
    {
        IReadOnlyList<int> numbers = OrderedItemNumbers(readme, "Reproduce");

        return numbers.SequenceEqual(Enumerable.Range(1, steps)) ? null : $"Reproduce is not an ordered list numbered 1 to {steps}";
    }

    /// <summary>The backticked commands of the ten rows of the Demo definition table of the M6 story file, by step number; <c>AJ</c> is expanded to the <c>dotnet run</c> form. Only a backticked text that starts with <c>AJ </c> or <c>./AgentJoules.Cli</c> is a command.</summary>
    /// <param name="storyText">The text of <c>docs/plan/stories/M6-docs-content-demo.md</c>.</param>
    /// <returns>The commands of each step, keyed by step number; empty when the table is missing.</returns>
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> DemoDefinitionCommands(string storyText)
    {
        ArgumentNullException.ThrowIfNull(storyText);
        string[] lines = Lines(storyText);
        int start = Array.FindIndex(lines, static line => line.StartsWith("Demo definition", StringComparison.Ordinal));
        var steps = new Dictionary<int, IReadOnlyList<string>>();
        if (start < 0)
        {
            return steps;
        }

        for (int index = start + 1; index < lines.Length; index++)
        {
            Match row = DemoRow().Match(lines[index]);
            if (row.Success)
            {
                steps[int.Parse(row.Groups[1].Value, CultureInfo.InvariantCulture)] =
                [
                    .. BacktickedText().Matches(row.Groups[2].Value)
                        .Select(static match => match.Groups[1].Value)
                        .Where(static text => text.StartsWith("AJ ", StringComparison.Ordinal) || text.StartsWith("./AgentJoules.Cli", StringComparison.Ordinal))
                        .Select(static text => text.StartsWith("AJ ", StringComparison.Ordinal) ? DotnetRun + text[3..] : text),
                ];
            }
            else if (steps.Count > 0)
            {
                break;
            }
        }

        return steps;
    }

    /// <summary>The Reproduce content check: the text of each ordered step of Reproduce holds every backticked command of the same step of the Demo definition table as its own backticked span (<c>`command`</c>), so a longer or altered command does not pass. A Demo definition row without a command is a failure.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="storyText">The text of <c>docs/plan/stories/M6-docs-content-demo.md</c>.</param>
    /// <param name="steps">The number of steps, ten.</param>
    /// <returns>One <c>reproduce step &lt;n&gt; differs from the demo definition</c> per differing step, and one <c>demo step &lt;n&gt; has no command</c> per step without a command.</returns>
    public static IReadOnlyList<string> ReproduceMatchesDemo(string readme, string storyText, int steps)
    {
        IReadOnlyDictionary<int, IReadOnlyList<string>> demo = DemoDefinitionCommands(storyText);
        Dictionary<int, string> items = ReproduceItems(readme);
        var failures = new List<string>();
        if (demo.Count == 0)
        {
            failures.Add("demo definition table not found");
        }

        for (int number = 1; number <= steps; number++)
        {
            if (!demo.TryGetValue(number, out IReadOnlyList<string>? commands) || commands.Count == 0)
            {
                failures.Add($"demo step {number} has no command");
                continue;
            }

            if (!items.TryGetValue(number, out string? text) || commands.Any(command => !text.Contains("`" + command + "`", StringComparison.Ordinal)))
            {
                failures.Add($"reproduce step {number} differs from the demo definition");
            }
        }

        return failures;
    }

    // The text of each ordered item of the Reproduce section by number; lines that follow an item and are not themselves items belong to it.
    private static Dictionary<int, string> ReproduceItems(string readme)
    {
        var items = new Dictionary<int, string>();
        int current = -1;
        foreach (string line in SectionAtAnyLevel(readme, "Reproduce"))
        {
            Match item = OrderedItem().Match(line);
            if (item.Success)
            {
                current = int.Parse(item.Groups[1].Value, CultureInfo.InvariantCulture);
                items[current] = line;
            }
            else if (current > 0 && line.Trim().Length > 0)
            {
                items[current] += "\n" + line;
            }
        }

        return items;
    }

    /// <summary>The Prior art check: each source is cited by name or number, the claim is scoped to the search of 2026-10-17, and the section links at least one address per source (four).</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>One <c>prior art lacks: ...</c> per missing element.</returns>
    public static IReadOnlyList<string> PriorArt(string readme)
    {
        IReadOnlyList<string> section = SectionAtAnyLevel(readme, "Prior art");
        string text = string.Join('\n', section);
        var failures = new List<string>();
        failures.AddRange(PriorArtSources.Where(source => !text.Contains(source, StringComparison.OrdinalIgnoreCase)).Select(static source => "prior art lacks: " + source));
        if (!text.Contains("2026-10-17", StringComparison.Ordinal))
        {
            failures.Add("prior art lacks: the search date 2026-10-17");
        }

        if (PriorArtLinks(readme).Count < 4)
        {
            failures.Add("prior art lacks: four links");
        }

        return failures;
    }

    /// <summary>The distinct addresses of the Prior art section, in order, without trailing punctuation.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>The URLs.</returns>
    public static IReadOnlyList<string> PriorArtLinks(string readme) =>
        [.. UrlPattern().Matches(string.Join('\n', SectionAtAnyLevel(readme, "Prior art"))).Select(static match => match.Value.TrimEnd('.', ',', ';', ':')).Distinct(StringComparer.Ordinal)];

    /// <summary>Requests each link with GET and expects HTTP 200. Nothing here chooses the handler: the caller passes the real one under the integration gate, or a fake.</summary>
    /// <param name="urls">The addresses.</param>
    /// <param name="client">The client.</param>
    /// <param name="cancellationToken">The token.</param>
    /// <returns>The result; each failure names the URL.</returns>
    public static async Task<LinkCheckResult> CheckLinksAsync(IEnumerable<string> urls, HttpClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(client);
        var failures = new List<string>();
        foreach (string url in urls)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    failures.Add($"link answered {(int)response.StatusCode}: {url}");
                }
            }
            catch (HttpRequestException)
            {
                failures.Add("link unreachable: " + url);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add("link timed out: " + url);
            }
        }

        return new LinkCheckResult(failures);
    }

    /// <summary>The build-log check: a line with <c>Prior-art links checked</c> and a date (<c>yyyy-MM-dd</c>).</summary>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <returns>Null when logged, otherwise a failure text.</returns>
    public static string? LinksCheckLogged(string buildLog) =>
        Lines(buildLog).Any(static line => line.Contains(LinksCheckedMarker, StringComparison.Ordinal) && DatePattern().IsMatch(line)) ? null : "prior-art link check is not logged with a date";

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    private readonly record struct HeadingAt(int Index, int Level, string Text);

    private static List<HeadingAt> HeadingsOf(string[] lines)
    {
        var result = new List<HeadingAt>();
        bool fenced = false;
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith(CloseFence, StringComparison.Ordinal))
            {
                fenced = !fenced;
            }
            else if (!fenced)
            {
                Match match = HeadingLine().Match(lines[index]);
                if (match.Success)
                {
                    result.Add(new HeadingAt(index, match.Groups[1].Length, match.Groups[2].Value));
                }
            }
        }

        return result;
    }

    // The body of a section: from the line after its heading to the line before the next heading of the same or a higher level; (-1, -1) when the heading is missing.
    private static (int Start, int End) SectionBounds(string[] lines, string heading)
    {
        List<HeadingAt> headings = HeadingsOf(lines);
        int at = headings.FindIndex(entry => entry.Text == heading);
        if (at < 0)
        {
            return (-1, -1);
        }

        HeadingAt own = headings[at];
        HeadingAt? next = headings.Skip(at + 1).Where(entry => entry.Level <= own.Level).Select(static entry => (HeadingAt?)entry).FirstOrDefault();

        return (own.Index + 1, next?.Index ?? lines.Length);
    }

    private static string CommandNameOf(string text)
    {
        string plain = text.Replace("`", string.Empty, StringComparison.Ordinal).Trim();
        if (plain.StartsWith("AJ ", StringComparison.Ordinal))
        {
            plain = plain[3..];
        }

        int space = plain.IndexOf(' ', StringComparison.Ordinal);

        return space < 0 ? plain : plain[..space];
    }

    private static HeadingAt? CommandHeading(string[] lines, string command)
    {
        (int start, int end) = SectionBounds(lines, CliReferenceHeading);
        if (start < 0)
        {
            return null;
        }

        return HeadingsOf(lines).Where(entry => entry.Index >= start && entry.Index < end && CommandNameOf(entry.Text) == command).Select(static entry => (HeadingAt?)entry).FirstOrDefault();
    }

    private static bool IsCommandRow(string line, string command)
    {
        if (!line.StartsWith('|'))
        {
            return false;
        }

        string[] cells = line.Split('|');

        return cells.Length > 2 && CommandNameOf(cells[1]) == command;
    }

    // The indexes of the lines, outside fenced blocks, that belong to the command inside the CLI reference: its subsection and its table rows.
    private static List<int> CommandLineIndexes(string[] lines, string command)
    {
        var indexes = new List<int>();
        (int start, int end) = SectionBounds(lines, CliReferenceHeading);
        if (start < 0)
        {
            return indexes;
        }

        var fenced = new bool[lines.Length];
        bool inside = false;
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith(CloseFence, StringComparison.Ordinal))
            {
                inside = !inside;
                fenced[index] = true;
            }
            else
            {
                fenced[index] = inside;
            }
        }

        List<HeadingAt> headings = HeadingsOf(lines);
        HeadingAt? own = CommandHeading(lines, command);
        int subStart = -1;
        int subEnd = -1;
        if (own is not null)
        {
            HeadingAt? next = headings.Where(entry => entry.Index > own.Value.Index && entry.Level <= own.Value.Level).Select(static entry => (HeadingAt?)entry).FirstOrDefault();
            subStart = own.Value.Index + 1;
            subEnd = Math.Min(next?.Index ?? end, end);
        }

        for (int index = start; index < end; index++)
        {
            if (!fenced[index] && ((index >= subStart && index < subEnd) || IsCommandRow(lines[index], command)))
            {
                indexes.Add(index);
            }
        }

        return indexes;
    }

    // Each meaning must contain the whole documented description of the exit code.
    private static bool MeaningMatches(string meaning, ExitCode code) => meaning.Contains(ExitCodeMapper.Describe(code), StringComparison.OrdinalIgnoreCase);
}