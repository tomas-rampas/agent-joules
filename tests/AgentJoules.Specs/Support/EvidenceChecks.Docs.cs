using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>The outcome of the documentation rules.</summary>
/// <param name="Violations">The failures.</param>
/// <param name="UncheckedBoxes">The boxes left unchecked with their reason: open items, not failures.</param>
public sealed record DocsEvidenceResult(IReadOnlyList<string> Violations, IReadOnlyList<string> UncheckedBoxes);

/// <summary>The rules of AJ-153 over the four reference documents and the three Devpost files.</summary>
public static partial class EvidenceChecks
{
    /// <summary>The four reference documents that carry a verification date.</summary>
    public static IReadOnlyList<string> VerifiedDocuments { get; } = ["docs/nim-local-attempt.md", "docs/endpoints.md", "docs/methodology.md", "docs/devpost-checklist.md"];

    /// <summary>The headings the checklist holds, each once.</summary>
    public static IReadOnlyList<string> ChecklistHeadings { get; } = ["Registration", "Tracks", "Judging criteria", "Credits", "Feedback form, user fills"];

    /// <summary>The reason box 9 stays unchecked when the rules page could not be fetched.</summary>
    public const string RulesPageNotVerified = "rules page not verified";

    private const string ChecklistPath = "docs/devpost-checklist.md";
    private const string StoryPath = "docs/devpost-story.md";
    private const string FeedbackPath = "docs/devpost-feedback.md";

    /// <summary>
    /// The verification rule: each of the four documents holds a line <c>Verified: YYYY-MM-DD</c> dated 2026-10-17 or later. A checklist whose line reads <c>Verified: not verified (checked &lt;date&gt;, HTTP &lt;status&gt;)</c>
    /// is not a failure: box 9 is reported unchecked with <see cref="RulesPageNotVerified"/>.
    /// </summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations and the unchecked boxes.</returns>
    public static DocsEvidenceResult VerificationDates(string root)
    {
        var violations = new List<string>();
        var uncheckedBoxes = new List<string>();
        foreach (string document in VerifiedDocuments)
        {
            string? text = ReadText(root, document);
            if (text is null)
            {
                violations.Add("missing file: " + document);
                continue;
            }

            string[] lines = DocumentChecks.Lines(text);
            DateOnly[] dates = [.. lines.Select(static line => VerifiedLine().Match(line)).Where(static match => match.Success).Select(static match => DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) ? date : DateOnly.MinValue).Where(static date => date != DateOnly.MinValue)];
            if (dates.Length > 0)
            {
                if (dates.Any(static date => date < EarliestVerification))
                {
                    violations.Add($"verification date before {EarliestVerification.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}: {document}");
                }

                continue;
            }

            Match notVerified = lines.Select(static line => NotVerifiedLine().Match(line)).FirstOrDefault(static match => match.Success) ?? Match.Empty;
            if (string.Equals(document, ChecklistPath, StringComparison.Ordinal) && notVerified.Success)
            {
                uncheckedBoxes.Add($"box 9: {RulesPageNotVerified} (checked {notVerified.Groups["date"].Value}, HTTP {notVerified.Groups["status"].Value})");
            }
            else
            {
                violations.Add("no verification date: " + document);
            }
        }

        return new DocsEvidenceResult(violations, uncheckedBoxes);
    }

    /// <summary>The checklist rules: one <c>Demo URL:</c> line, at least one <c>Verified</c> line, the headings Registration, Tracks, Judging criteria and Credits once each, and the feedback heading once with seven bullets.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> ChecklistViolations(string root)
    {
        string? text = ReadText(root, ChecklistPath);
        if (text is null)
        {
            return ["missing file: " + ChecklistPath];
        }

        var violations = new List<string>();
        string[] lines = DocumentChecks.Lines(text);
        int demoUrls = lines.Count(static line => line.StartsWith("Demo URL:", StringComparison.Ordinal));
        if (demoUrls != 1)
        {
            violations.Add($"Demo URL: line occurs {demoUrls.ToString(CultureInfo.InvariantCulture)} times, expected 1");
        }

        if (!lines.Any(static line => line.StartsWith("Verified", StringComparison.Ordinal)))
        {
            violations.Add("no Verified line in " + ChecklistPath);
        }

        IReadOnlyList<string> headings = HeadingsOf(text);
        foreach (string heading in ChecklistHeadings)
        {
            int count = headings.Count(candidate => string.Equals(candidate, heading, StringComparison.Ordinal));
            if (count == 0)
            {
                violations.Add("missing heading: " + heading);
            }
            else if (count > 1)
            {
                violations.Add($"heading occurs {count.ToString(CultureInfo.InvariantCulture)} times: {heading}");
            }
        }

        if (SectionOf(text, "Feedback form, user fills") is { } feedback)
        {
            int bullets = DocumentChecks.Lines(feedback).Count(static line => line.StartsWith("- ", StringComparison.Ordinal));
            if (bullets != UserPlaceholderCount)
            {
                violations.Add($"Feedback form, user fills lists {bullets.ToString(CultureInfo.InvariantCulture)} bullets, expected {UserPlaceholderCount.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        return violations;
    }

    /// <summary>The story rules: no <c>[[FILL:</c> and no line that contains <c>Fill-after-build</c>.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> StoryViolations(string root)
    {
        string? text = ReadText(root, StoryPath);
        if (text is null)
        {
            return ["missing file: " + StoryPath];
        }

        var violations = new List<string>();
        if (text.Contains("[[FILL:", StringComparison.Ordinal))
        {
            violations.Add("leftover placeholder in " + StoryPath);
        }

        if (DocumentChecks.Lines(text).Any(static line => line.Contains("Fill-after-build", StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add("Fill-after-build text in " + StoryPath);
        }

        return violations;
    }

    /// <summary>The feedback-file rules: no <c>owner: builder</c> and exactly seven <c>owner: user</c>.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> FeedbackViolations(string root)
    {
        string? text = ReadText(root, FeedbackPath);
        if (text is null)
        {
            return ["missing file: " + FeedbackPath];
        }

        var violations = new List<string>();
        if (text.Contains("owner: builder", StringComparison.Ordinal))
        {
            violations.Add("builder placeholder left in " + FeedbackPath);
        }

        int users = LinesContaining(text, "owner: user");
        if (users != UserPlaceholderCount)
        {
            violations.Add($"owner: user occurs {users.ToString(CultureInfo.InvariantCulture)} times in {FeedbackPath}, expected {UserPlaceholderCount.ToString(CultureInfo.InvariantCulture)}");
        }

        return violations;
    }

    /// <summary>Every AJ-153 rule over a folder.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations and the unchecked boxes.</returns>
    public static DocsEvidenceResult DocsEvidence(string root)
    {
        DocsEvidenceResult dates = VerificationDates(root);
        return new DocsEvidenceResult([.. dates.Violations, .. ChecklistViolations(root), .. StoryViolations(root), .. FeedbackViolations(root)], dates.UncheckedBoxes);
    }

    [GeneratedRegex(@"^Verified: (?<date>2026-1[0-9]-[0-9]{2})", RegexOptions.CultureInvariant)]
    private static partial Regex VerifiedLine();

    [GeneratedRegex(@"^Verified: not verified \(checked (?<date>[0-9-]+), HTTP (?<status>[0-9]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex NotVerifiedLine();
}