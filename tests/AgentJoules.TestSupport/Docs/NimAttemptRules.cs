using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// The rules of the NIM attempt document (AJ-102, REQ-019, EDGE-010), applied to a <see cref="NimAttemptDocument"/>. <see cref="Check"/> returns the violations in the order of the rules; a pass is an empty list.
/// A message names the rule and the heading or the line and never shows a key, a suffix of a key or a password argument: a line that holds key material is named by its number only.
/// </summary>
public static partial class NimAttemptRules
{
    /// <summary>The headings the document must have, compared ordinally and exactly.</summary>
    public static readonly IReadOnlyList<string> RequiredHeadings = ["Commands", "Image", "Outcome", "Decision"];

    /// <summary>The longest time between Started and Ended that needs no statement.</summary>
    public static readonly TimeSpan Box = TimeSpan.FromMinutes(90);

    /// <summary>The shortest <c>NGC_API_KEY</c> value that the key rule searches for; a shorter value is treated as absent (a 1 or a word would match ordinary text and is no key).</summary>
    public const int MinimumKeyLength = 8;

    private const string LogoutText = "docker logout nvcr.io";
    private const int ShownTimeLength = 32;

    /// <summary>Applies the rules.</summary>
    /// <param name="document">The reading of the document.</param>
    /// <param name="ngcKey">The value of <c>NGC_API_KEY</c> in the environment of the test, or null or empty when there is none; the document must not contain it. The value is trimmed first, and a trimmed value shorter than <see cref="MinimumKeyLength"/> characters counts as absent.</param>
    /// <returns>The violations in the order of the rules (headings, times, the 90-minute box, the decision, the docker login lines, key material, the docker logout line); empty for a pass.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static IReadOnlyList<string> Check(NimAttemptDocument document, string? ngcKey)
    {
        ArgumentNullException.ThrowIfNull(document);

        string? key = ngcKey?.Trim() is { Length: >= MinimumKeyLength } trimmed ? trimmed : null;
        List<string> violations = [];
        CheckHeadings(document, violations);
        CheckTimes(document, key, violations);
        CheckBox(document, violations);
        CheckDecision(document, violations);
        CheckLogins(document, violations);
        CheckKeyMaterial(document, key, violations);
        CheckLogout(document, violations);
        return violations;
    }

    private static void CheckHeadings(NimAttemptDocument document, List<string> violations)
    {
        foreach (string heading in RequiredHeadings)
        {
            if (!document.Headings.Contains(heading, StringComparer.Ordinal))
            {
                violations.Add($"headings: the document has no \"## {heading}\" heading (the name must be exactly \"{heading}\")");
            }
        }
    }

    private static void CheckTimes(NimAttemptDocument document, string? key, List<string> violations)
    {
        CheckTime("Started:", document.StartedText, document.Started, key, violations);
        CheckTime("Ended:", document.EndedText, document.Ended, key, violations);
    }

    private static void CheckTime(string label, string? text, DateTimeOffset? time, string? key, List<string> violations)
    {
        if (text is null)
        {
            violations.Add($"times: the document has no \"{label}\" line");
        }
        else if (time is null)
        {
            violations.Add($"times: the \"{label}\" value is not an ISO 8601 UTC time (yyyy-MM-ddTHH:mm:ssZ or yyyy-MM-ddTHH:mm:ss.fffZ): {Shown(text, key)}");
        }
    }

    private static void CheckBox(NimAttemptDocument document, List<string> violations)
    {
        if (document.Started is not { } started || document.Ended is not { } ended)
        {
            return;
        }

        TimeSpan elapsed = ended - started;
        if (elapsed < TimeSpan.Zero)
        {
            violations.Add("times: \"Ended:\" is before \"Started:\"");
        }
        else if (elapsed > Box)
        {
            // Rounded up to whole seconds, so that a time just over the box never reads as exactly 90 minutes.
            long totalSeconds = (long)Math.Ceiling(elapsed.TotalSeconds);
            string minutes = string.Create(CultureInfo.InvariantCulture, $"{totalSeconds / 60} minutes {totalSeconds % 60} seconds");
            if (document.BoxExceededReason is null)
            {
                violations.Add($"90-minute box: \"Ended:\" is {minutes} after \"Started:\" and the document has no \"Box exceeded:\" line with a reason");
            }
            else if (string.IsNullOrWhiteSpace(document.BoxExceededReason))
            {
                violations.Add($"90-minute box: \"Ended:\" is {minutes} after \"Started:\" and the \"Box exceeded:\" line states no reason");
            }
        }
    }

    private static void CheckDecision(NimAttemptDocument document, List<string> violations)
    {
        if (!document.Sections.TryGetValue(NimAttemptDocumentParser.DecisionHeading, out string? decision))
        {
            return;
        }

        string? last = decision.Split('\n').LastOrDefault(static line => !string.IsNullOrWhiteSpace(line));
        if (last is null || NimAttemptDocumentParser.RuntimeOfLine(last) is null)
        {
            violations.Add("decision: the last non-blank line of the \"Decision\" section must be \"Local runtime: nim\" or \"Local runtime: ollama\"");
        }
    }

    // A command that continues on the next line (a trailing backslash, backtick or caret) is joined with that line before the rules run, so that a flag on the continuation line is seen; the line number of a message is the first line of the command.
    private static void CheckLogins(NimAttemptDocument document, List<string> violations)
    {
        for (int index = 0; index < document.Lines.Count; index++)
        {
            int first = index;
            string command = document.Lines[index];
            while (EndsWithContinuation(command) && index + 1 < document.Lines.Count)
            {
                command = command.TrimEnd()[..^1].TrimEnd() + " " + document.Lines[++index];
            }

            if (LoginCommand().IsMatch(command)
                && (!command.Contains("--password-stdin", StringComparison.Ordinal) || PasswordArgument().IsMatch(command) || ShortPasswordFlag().IsMatch(command) || command.Contains("<<<", StringComparison.Ordinal)))
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture, $"docker login: line {first + 1}: the login must use --password-stdin and must not pass --password or -p, or a here-string"));
            }
        }
    }

    // A trailing backslash, backtick or caret continues the command only after white space (the shape of a shell continuation): a prose line that ends with inline code, like "use `--password-stdin`", does not.
    private static bool EndsWithContinuation(string line)
    {
        string trimmed = line.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] is '\\' or '`' or '^' && (trimmed.Length == 1 || char.IsWhiteSpace(trimmed[^2]));
    }

    private static void CheckKeyMaterial(NimAttemptDocument document, string? key, List<string> violations)
    {
        bool found = false;
        for (int index = 0; index < document.Lines.Count; index++)
        {
            string line = document.Lines[index];
            if (KeyShape().IsMatch(line) || (key is not null && line.Contains(key, StringComparison.Ordinal)))
            {
                found = true;
                violations.Add(string.Create(CultureInfo.InvariantCulture, $"secrets: key material found at line {index + 1}; the text is not shown"));
            }
        }

        // The key is matched line by line; only a key value that itself contains a line break cannot match inside one line, so the whole text is the last resort, and then no line can be named.
        if (!found && key is not null && document.Raw.Contains(key, StringComparison.Ordinal))
        {
            violations.Add("secrets: key material found in the document; the text is not shown");
        }
    }

    private static void CheckLogout(NimAttemptDocument document, List<string> violations)
    {
        if (!document.Lines.Any(static line => LogoutCommand().IsMatch(line)))
        {
            violations.Add($"docker logout: no line contains \"{LogoutText}\"");
        }
    }

    // A malformed time is shown to explain the failure, except when it looks like key material: then it is hidden (the secrets rule names the line).
    private static string Shown(string text, string? key)
    {
        bool looksLikeKey = KeyShape().IsMatch(text) || text.Contains("nvapi-", StringComparison.OrdinalIgnoreCase) || (key is not null && text.Contains(key, StringComparison.Ordinal));
        return looksLikeKey ? "(not shown)" : "\"" + (text.Length > ShownTimeLength ? text[..ShownTimeLength] + "..." : text) + "\"";
    }

    // "docker login" with any white space between the words.
    [GeneratedRegex(@"docker\s+login", RegexOptions.CultureInvariant)]
    private static partial Regex LoginCommand();

    // "docker logout nvcr.io" with any white space between the words; the registry must be named and must end there.
    [GeneratedRegex(@"docker\s+logout\s+nvcr\.io(?![A-Za-z0-9-]|\.[A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LogoutCommand();

    // --password followed by anything but -stdin: "--password value", "--password=value" and a bare "--password".
    [GeneratedRegex(@"--password(?!-stdin)", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordArgument();

    // -p as a word of its own or with a value attached: " -p value", " -p=value", " -pvalue". "--password-stdin" has no space before its -p.
    [GeneratedRegex(@"(?<!\S)-p", RegexOptions.CultureInvariant)]
    private static partial Regex ShortPasswordFlag();

    [GeneratedRegex(@"nvapi-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyShape();
}