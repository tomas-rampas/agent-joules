using AgentJoules.TestSupport.Docs;

namespace AgentJoules.Specs.Support;

/// <summary>The lines of a Mermaid block, or the reason there is none.</summary>
/// <param name="Failure">The failure text, or null when the block is valid.</param>
/// <param name="Lines">The lines between the fences, empty when <paramref name="Failure"/> is set.</param>
public sealed record MermaidBlockResult(string? Failure, IReadOnlyList<string> Lines);

/// <summary>
/// Pure checks over the text of a committed document (AJ-130 and the later documentation stories). Each check takes strings and returns a failure text or a list of them; none starts a process,
/// touches a file or reads the network, except <c>CheckLinksAsync</c>, which requests the addresses through the <see cref="HttpClient"/> its caller supplies (a fake handler in the tests, the real one only under the integration gate), so a test feeds it a mutated copy of the committed text. A later story adds its check as another static method here.
/// </summary>
public static partial class DocumentChecks
{
    /// <summary>The fence line that opens the architecture diagram.</summary>
    public const string MermaidFence = "```mermaid";

    /// <summary>The line that closes a fenced block.</summary>
    public const string CloseFence = "```";

    /// <summary>The first content line of the diagram.</summary>
    public const string FlowchartLine = "flowchart LR";

    /// <summary>The placeholder the README sections carried before the build.</summary>
    public const string Placeholder = "Filled during the build";

    /// <summary>The singular tagline of the README when the cloudgpu tier did not run (AJ-130 criterion 3).</summary>
    public const string SingularTagline = "Agent Joules: energy per agent step, measured on your GPU";

    private static readonly string[] PlaceholderSections = ["What it measures", "Architecture"];

    private static readonly MermaidBlockResult MissingBlock = new("missing mermaid block", []);

    /// <summary>The words the diagram must contain, case-insensitively, in the spelling of the failure text.</summary>
    public static IReadOnlyList<string> DiagramWords { get; } = ["Agent", "Metered", "Provider", "NVML", "Sink", "Results", "Report", "OpenTelemetry"];

    /// <summary>The headings the README must hold, each exactly once.</summary>
    public static IReadOnlyList<string> ReadmeHeadings { get; } = ["What it measures", "Architecture", "Quick start", "Results", "License"];

    /// <summary>Splits a text into lines, accepting LF and CRLF.</summary>
    /// <param name="text">The document text.</param>
    /// <returns>The lines without line endings.</returns>
    public static string[] Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
    }

    /// <summary>Finds the one Mermaid block: exactly one fence line, <c>flowchart LR</c> as the next content line, a closing fence, and balanced brackets.</summary>
    /// <param name="text">The document text.</param>
    /// <returns>The block lines, or the failure <c>missing mermaid block</c> (or <c>unbalanced brackets in mermaid block</c>).</returns>
    public static MermaidBlockResult MermaidBlock(string text)
    {
        string[] lines = Lines(text);
        int[] fences = [.. lines.Select(static (line, index) => (line, index)).Where(static pair => pair.line == MermaidFence).Select(static pair => pair.index)];
        if (fences.Length != 1)
        {
            return MissingBlock;
        }

        int start = fences[0] + 1;
        while (start < lines.Length && lines[start].Trim().Length == 0)
        {
            start++;
        }

        if (start >= lines.Length || lines[start] != FlowchartLine)
        {
            return MissingBlock;
        }

        int end = Array.FindIndex(lines, start, static line => line == CloseFence);
        if (end < 0)
        {
            return MissingBlock;
        }

        string[] block = lines[start..end];

        return BracketsBalanced(block) ? new MermaidBlockResult(null, block) : new MermaidBlockResult("unbalanced brackets in mermaid block", []);
    }

    /// <summary>The diagram node check: one failure <c>diagram lacks: &lt;Word&gt;</c> per word of <see cref="DiagramWords"/> that no block line contains.</summary>
    /// <param name="blockLines">The lines of the block.</param>
    /// <returns>The failures, empty when every word is present.</returns>
    public static IReadOnlyList<string> DiagramNodes(IReadOnlyList<string> blockLines)
    {
        ArgumentNullException.ThrowIfNull(blockLines);

        return [.. DiagramWords.Where(word => !blockLines.Any(line => line.Contains(word, StringComparison.OrdinalIgnoreCase))).Select(static word => "diagram lacks: " + word)];
    }

    /// <summary>The heading check: <c>## &lt;heading&gt;</c> as a whole line exactly once.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="heading">The heading text without the <c>## </c> prefix.</param>
    /// <returns>Null when the heading occurs once, otherwise <c>missing heading: &lt;heading&gt;</c> (or <c>duplicate heading: ...</c>).</returns>
    public static string? HasHeading(string text, string heading)
    {
        ArgumentException.ThrowIfNullOrEmpty(heading);
        int count = Lines(text).Count(line => line == "## " + heading);

        return count switch
        {
            1 => null,
            0 => "missing heading: " + heading,
            _ => "duplicate heading: " + heading,
        };
    }

    /// <summary>The lines of a <c>## </c> section, from the line after its heading to the line before the next <c>## </c> heading; empty when the heading is missing.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="heading">The heading text without the <c>## </c> prefix.</param>
    /// <returns>The section lines.</returns>
    public static IReadOnlyList<string> Section(string text, string heading)
    {
        string[] lines = Lines(text);
        int at = Array.IndexOf(lines, "## " + heading);
        if (at < 0)
        {
            return [];
        }

        int end = Array.FindIndex(lines, at + 1, static line => line.StartsWith("## ", StringComparison.Ordinal));

        return end < 0 ? lines[(at + 1)..] : lines[(at + 1)..end];
    }

    /// <summary>The placeholder check: a section may not hold the leftover text.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="section">The heading of the section without the <c>## </c> prefix.</param>
    /// <param name="leftover">The text that must be gone, matched ignoring case.</param>
    /// <returns>Null when the section is clean, otherwise <c>leftover placeholder text: &lt;leftover&gt;</c>.</returns>
    public static string? NoLeftoverText(string text, string section, string leftover)
    {
        ArgumentException.ThrowIfNullOrEmpty(leftover);

        return Section(text, section).Any(line => line.Contains(leftover, StringComparison.OrdinalIgnoreCase)) ? "leftover placeholder text: " + leftover : null;
    }

    /// <summary>The text of the <c>## Tagline (60 characters)</c> section of the pitch file: its quote line without the <c>&gt; </c> prefix.</summary>
    /// <param name="pitch">The text of <c>docs/devpost-pitch.md</c>.</param>
    /// <returns>The tagline text, or null when the section or its quote line is missing.</returns>
    public static string? PitchTagline(string pitch) => Section(pitch, "Tagline (60 characters)").FirstOrDefault(static line => line.StartsWith("> ", StringComparison.Ordinal))?[2..].Trim();

    /// <summary>Whether a completed or partial cloudgpu run file exists, decided as in AJ-141 by <see cref="CloudGpuTaglineRule"/> over the <c>run.status</c> of the files.</summary>
    /// <param name="resultsFiles">The paths or names of the results files.</param>
    /// <param name="readFile">Returns the text of a file by the path as given.</param>
    /// <returns>True when a selectable cloudgpu file exists.</returns>
    public static bool CloudGpuRan(IEnumerable<string> resultsFiles, Func<string, string> readFile) => CloudGpuTaglineRule.Evaluate(resultsFiles, readFile).State != CloudGpuTaglineRule.Absent;

    /// <summary>The tagline check: line 3 of the README against the pitch tagline (plural) or, when the cloudgpu tier did not run, its singular form.</summary>
    /// <param name="readme">The README text.</param>
    /// <param name="pitch">The text of <c>docs/devpost-pitch.md</c>.</param>
    /// <param name="cloudGpuRan">Whether a completed or partial cloudgpu run file exists.</param>
    /// <returns>The failures, empty when the line matches.</returns>
    public static IReadOnlyList<string> Tagline(string readme, string pitch, bool cloudGpuRan)
    {
        string? pitchLine = PitchTagline(pitch);
        if (pitchLine is null)
        {
            return ["missing tagline section in the pitch file"];
        }

        string expected = cloudGpuRan || !pitchLine.EndsWith("GPUs", StringComparison.Ordinal) ? pitchLine : pitchLine[..^1];
        string[] lines = Lines(readme);
        string actual = lines.Length > 2 ? lines[2] : string.Empty;

        return actual == expected ? [] : ["README line 3 is not the tagline: expected " + expected];
    }

    /// <summary>The singular-tagline log check: the build log holds a line that names AJ-130 and the singular tagline.</summary>
    /// <param name="buildLog">The text of <c>docs/build-log.md</c>.</param>
    /// <returns>Null when the change is logged, otherwise a failure text.</returns>
    public static string? SingularTaglineLogged(string buildLog) =>
        Lines(buildLog).Any(static line => line.Contains("AJ-130", StringComparison.Ordinal) && line.Contains(SingularTagline, StringComparison.Ordinal)) ? null : "singular tagline change is not logged";

    /// <summary>The README structure check of AJ-130: the Mermaid block and its nodes, the five headings, and no placeholder under <c>What it measures</c> or <c>Architecture</c>.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>Every failure text, empty when the README passes.</returns>
    public static IReadOnlyList<string> ReadmeStructure(string readme)
    {
        var failures = new List<string>();
        MermaidBlockResult block = MermaidBlock(readme);
        if (block.Failure is not null)
        {
            failures.Add(block.Failure);
        }
        else
        {
            failures.AddRange(DiagramNodes(block.Lines));
        }

        failures.AddRange(ReadmeHeadings.Select(heading => HasHeading(readme, heading)).OfType<string>());
        failures.AddRange(PlaceholderSections.Select(section => NoLeftoverText(readme, section, Placeholder)).OfType<string>());

        return failures;
    }

    // Brackets inside double-quoted labels are text, not structure.
    private static bool BracketsBalanced(IEnumerable<string> lines)
    {
        var stack = new Stack<char>();
        foreach (string line in lines)
        {
            bool quoted = false;
            foreach (char c in line)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (!quoted)
                {
                    if (c is '(' or '[' or '{')
                    {
                        stack.Push(c);
                    }
                    else if (c is ')' or ']' or '}' && (stack.Count == 0 || !Matches(stack.Pop(), c)))
                    {
                        return false;
                    }
                }
            }
        }

        return stack.Count == 0;
    }

    private static bool Matches(char open, char close) => (open, close) is ('(', ')') or ('[', ']') or ('{', '}');
}