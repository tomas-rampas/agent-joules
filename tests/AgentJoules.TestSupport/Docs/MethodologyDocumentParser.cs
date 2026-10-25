using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// The contract of <c>docs/methodology.md</c> (AJ-112, REQ-022, criteria 2 to 7): <see cref="Check"/> reads the markdown and the calibration file it names and returns one message per broken rule, an empty list when the
/// document passes. The rules: each of the five headings exactly once (the pattern of the story's <c>grep -cE</c>); a <c>Verified: yyyy-MM-dd</c> line; each of the terms millijoule, milliwatt, sample rate and
/// HTTP call at least once, and four or more lines that hold any of them, in the Measurement section (the story's <c>grep -ciE</c>); the seven strings in the Limitations section; each of the three REQ-032 sentences
/// exactly once in the whole document and inside Limitations; exactly one named <c>results/calibration-yyyyMMddTHHmmssZ.json</c> (the name is not followed by a word character or a hyphen); and the
/// <c>relativeDifference</c> quoted beside that name equal, character for character, to the raw JSON text of the stored value (N/A with the stored reason when the file stores null). A section runs from its heading to the next heading of the same or a higher level, with
/// white space collapsed so a sentence wrapped over two lines still matches. The reading is line based and never judges prose; it takes no key and no other input than its two arguments.
/// </summary>
public static partial class MethodologyDocumentParser
{
    /// <summary>The first REQ-032 sentence (criterion 7).</summary>
    public const string NoGpuWorkSentence = "The dashboard server adds no GPU work.";

    /// <summary>The second REQ-032 sentence (criterion 7).</summary>
    public const string CompositingSentence = "A browser tab adds compositing load, so published runs are recorded without one.";

    /// <summary>The third REQ-032 sentence (criterion 7).</summary>
    public const string EstimatesSentence = "Estimates shown on the live dashboard never enter results files.";

    /// <summary>The least number of lines of the Measurement section that hold a term (criterion 3).</summary>
    public const int MinimumMeasurementTerms = 4;

    private const string LimitationsHeading = "Limitations";
    private const string MeasurementHeading = "Measurement";
    private const string DifferenceMember = "relativeDifference";
    private const string NotApplicable = "N/A";

    private static readonly string[] MeasurementTerms = ["millijoule", "milliwatt", "sample rate", "HTTP call"];
    private static readonly string[] RequiredHeadings = ["Measurement", "Attribution", "Baseline", "Labels", "Limitations"];
    private static readonly string[] LimitationStrings = ["GPU-only", "display", "power limit", "sampling", "offload", "hosted", "cloud"];
    private static readonly string[] DashboardSentences = [NoGpuWorkSentence, CompositingSentence, EstimatesSentence];

    /// <summary>The full path of the committed <c>docs/methodology.md</c>, which may not exist yet.</summary>
    /// <returns>The path.</returns>
    public static string CommittedPath() => Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "methodology.md");

    /// <summary>Checks the document against the contract.</summary>
    /// <param name="markdown">The text of the document; carriage returns are ignored.</param>
    /// <param name="readResultsFile">Reads a repository-relative path such as <c>results/calibration-20261025T101500Z.json</c>; returns null when the file does not exist.</param>
    /// <returns>One message per broken rule; empty when the document passes.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> Check(string markdown, Func<string, string?> readResultsFile)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(readResultsFile);

        string text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = text.Split('\n');
        List<string> failures = [];

        Section[] sections = CheckHeadings(lines, failures);
        if (!VerifiedLine().IsMatch(text))
        {
            failures.Add("the document has no line \"Verified: <UTC date>\" (Verified: yyyy-MM-dd)");
        }

        if (sections.FirstOrDefault(static section => string.Equals(section.Name, MeasurementHeading, StringComparison.Ordinal)) is { } measurement)
        {
            // The story's greps: each term at least once (grep -ciE per term), and four or more lines that match any term (grep -ciE of the alternation).
            failures.AddRange(MeasurementTerms.Where(term => !measurement.Text.Contains(term, StringComparison.OrdinalIgnoreCase)).Select(static term => $"the Measurement section lacks the term \"{term}\""));
            int matchingLines = measurement.Lines.Count(static line => MeasurementTerm().IsMatch(line));
            if (matchingLines < MinimumMeasurementTerms)
            {
                failures.Add($"the Measurement section has {matchingLines.ToString(CultureInfo.InvariantCulture)} lines with the terms millijoule, milliwatt, sample rate or HTTP call; at least {MinimumMeasurementTerms} are required");
            }
        }

        if (SectionText(sections, LimitationsHeading) is { } limitations)
        {
            failures.AddRange(LimitationStrings.Where(required => !limitations.Contains(required, StringComparison.Ordinal)).Select(static required => $"the Limitations section lacks the string \"{required}\""));
            CheckDashboardSentences(Collapse(text), limitations, failures);
        }

        CheckCalibration(lines, text, readResultsFile, failures);
        return failures;
    }

    /// <summary>The sentences of <paramref name="before"/> that <paramref name="after"/> no longer holds verbatim (AJ-112 criterion 1, the merge rule).</summary>
    /// <param name="before">The document before the merge.</param>
    /// <param name="after">The document after it.</param>
    /// <returns>The lost sentences, one per non-blank, non-heading line of <paramref name="before"/> whose collapsed text is not in <paramref name="after"/>; empty when none was lost.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> LostSentences(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        string collapsed = Collapse(after);
        return
        [
            .. before.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                .Where(line => !collapsed.Contains(Collapse(line), StringComparison.Ordinal)),
        ];
    }

    private static string Collapse(string text) => WhiteSpace().Replace(text, " ").Trim();

    // Each REQ-032 sentence occurs exactly once in the whole document (grep -cF prints 1) and that one occurrence lies inside the Limitations section; both texts have their white space collapsed.
    private static void CheckDashboardSentences(string document, string limitations, List<string> failures)
    {
        foreach (string sentence in DashboardSentences)
        {
            int count = Occurrences(document, sentence);
            if (count != 1)
            {
                failures.Add($"the document holds the sentence \"{sentence}\" {count.ToString(CultureInfo.InvariantCulture)} times; exactly once is required, inside the Limitations section");
            }
            else if (!limitations.Contains(sentence, StringComparison.Ordinal))
            {
                failures.Add($"the Limitations section lacks the sentence \"{sentence}\"");
            }
        }
    }

    private static int Occurrences(string text, string sentence)
    {
        int count = 0;
        for (int index = text.IndexOf(sentence, StringComparison.Ordinal); index >= 0; index = text.IndexOf(sentence, index + sentence.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string? SectionText(Section[] sections, string heading) =>
        sections.Where(section => string.Equals(section.Name, heading, StringComparison.Ordinal)).Select(static section => section.Text).FirstOrDefault();

    // Counts each required heading, names a missing or repeated one, and returns the sections of those that occur (the first occurrence of a repeated one).
    private static Section[] CheckHeadings(string[] lines, List<string> failures)
    {
        List<(int Line, int Level, string? Name)> headings = [];
        for (int index = 0; index < lines.Length; index++)
        {
            Match match = AnyHeading().Match(lines[index]);
            if (match.Success)
            {
                Match required = RequiredHeading().Match(lines[index]);
                headings.Add((index, match.Groups["hashes"].Length, required.Success ? required.Groups["name"].Value : null));
            }
        }

        List<Section> sections = [];
        foreach (string name in RequiredHeadings)
        {
            int count = headings.Count(heading => string.Equals(heading.Name, name, StringComparison.Ordinal));
            if (count == 0)
            {
                failures.Add($"missing heading: {name}");
                continue;
            }

            if (count > 1)
            {
                failures.Add($"heading {name} appears {count.ToString(CultureInfo.InvariantCulture)} times; exactly once is required");
            }

            (int line, int level, _) = headings.First(heading => string.Equals(heading.Name, name, StringComparison.Ordinal));
            int end = headings.Where(heading => heading.Line > line && heading.Level <= level).Select(static heading => heading.Line).DefaultIfEmpty(lines.Length).Min();
            string[] body = lines[(line + 1)..end];
            sections.Add(new Section(name, Collapse(string.Join('\n', body)), body));
        }

        return [.. sections];
    }

    private static void CheckCalibration(string[] lines, string text, Func<string, string?> readResultsFile, List<string> failures)
    {
        string[] names = [.. CalibrationFileName().Matches(text).Select(static match => match.Value).Distinct(StringComparer.Ordinal)];
        if (names.Length == 0)
        {
            failures.Add("the document names no results/calibration-<yyyyMMddTHHmmssZ>.json file: the calibration file must be named");
            return;
        }

        if (names.Length > 1)
        {
            failures.Add($"the document names {names.Length.ToString(CultureInfo.InvariantCulture)} calibration files ({string.Join(", ", names)}); exactly one must be named");
            return;
        }

        string name = names[0];
        string? json = readResultsFile(name);
        if (json is null)
        {
            failures.Add($"the named calibration file {name} does not exist");
            return;
        }

        JsonElement difference;
        try
        {
            using JsonDocument file = JsonDocument.Parse(json);
            if (file.RootElement.ValueKind != JsonValueKind.Object || !file.RootElement.TryGetProperty(DifferenceMember, out JsonElement member) || member.ValueKind != JsonValueKind.Object)
            {
                failures.Add($"{name} has no {DifferenceMember} label object");
                return;
            }

            difference = member.Clone();
        }
        catch (JsonException)
        {
            failures.Add($"{name} is not valid JSON");
            return;
        }

        // The quote sits on the line that names the file or on the next one; the file name itself is cut out so its digits are not read as the number.
        int named = Array.FindIndex(lines, line => CalibrationFileName().Matches(line).Any(match => string.Equals(match.Value, name, StringComparison.Ordinal)));
        string context = Collapse(string.Join(' ', lines.Skip(named).Take(2))).Replace(name, " ", StringComparison.Ordinal);
        int word = context.IndexOf(DifferenceMember, StringComparison.Ordinal);
        if (word < 0)
        {
            failures.Add($"the document quotes no {DifferenceMember} on the line that names {name} or on the next line");
            return;
        }

        string quotedPart = context[(word + DifferenceMember.Length)..];
        difference.TryGetProperty("value", out JsonElement value);
        if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
        {
            string reason = difference.TryGetProperty("reason", out JsonElement stored) && stored.ValueKind == JsonValueKind.String ? stored.GetString() ?? string.Empty : string.Empty;
            if (!quotedPart.Contains(NotApplicable, StringComparison.Ordinal) || (reason.Length > 0 && !quotedPart.Contains(reason, StringComparison.Ordinal)))
            {
                failures.Add($"{name} stores {DifferenceMember} {NotApplicable} with the reason \"{reason}\"; the document must quote {NotApplicable} with that reason");
            }

            return;
        }

        string storedText = value.GetRawText();
        Match number = Number().Match(quotedPart);
        if (!number.Success)
        {
            failures.Add($"the document quotes no number for {DifferenceMember}; {name} stores {storedText}");
            return;
        }

        // The quote is the raw JSON text of the stored number, character for character (0.0202 and 0.02020 differ, as does an exponent form against its expansion).
        if (!string.Equals(number.Value, storedText, StringComparison.Ordinal))
        {
            failures.Add($"the document quotes {DifferenceMember} {number.Value} but {name} stores {storedText}");
        }
    }

    // The lines of a section are kept as written (the story's grep counts lines); the text is the same lines with white space collapsed.
    private sealed record Section(string Name, string Text, string[] Lines);

    [GeneratedRegex(@"^#+ +(?<name>Measurement|Attribution|Baseline|Labels|Limitations)", RegexOptions.CultureInvariant)]
    private static partial Regex RequiredHeading();

    [GeneratedRegex(@"^(?<hashes>#+) ", RegexOptions.CultureInvariant)]
    private static partial Regex AnyHeading();

    [GeneratedRegex(@"^Verified: \d{4}-\d{2}-\d{2}", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex VerifiedLine();

    [GeneratedRegex(@"millijoule|milliwatt|sample rate|HTTP call", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeasurementTerm();

    // Anchored on the right: a name followed by a word character or a hyphen (results/calibration-...Z.jsonx) is not a calibration file name; a full stop, a comma, a quote or a bracket after it is prose.
    [GeneratedRegex(@"results/calibration-\d{8}T\d{6}Z\.json(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex CalibrationFileName();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpace();
}