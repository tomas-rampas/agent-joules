using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The hardening rules of AJ-155: the captured output of the build, the test run and the format check, the CI workflow file, the key-shaped-string scan and the record of the secret-scan fallback.
/// A rule takes the captured text or the exit code (the orchestrator runs the commands and records their output) and returns its violations as text, an empty list being a pass.
/// No rule prints a key value; the scans name files only.
/// </summary>
public static partial class EvidenceChecks
{
    /// <summary>The names of the four commands of the scan fallback, in the order of the record.</summary>
    public static IReadOnlyList<string> ScanCommandNames { get; } = ["pattern", "NVIDIA_API_KEY", "NEBIUS_API_KEY", "NGC_API_KEY"];

    /// <summary>The words a record of the fallback scan carries.</summary>
    public const string ScanFallbackName = "git grep fallback";

    /// <summary>The note after a <c>git grep</c> exit code 1.</summary>
    public const string NoHitsNote = "1 = no hits";

    /// <summary>The message of the key-shaped-string rule.</summary>
    public const string KeyShapedInTrackedFile = "key-shaped string in tracked file";

    private static readonly string[] CiTokens =
    [
        "windows-latest",
        "ubuntu-latest",
        "dotnet build -warnaserror -tl:off",
        "dotnet format --verify-no-changes",
        "dotnet test",
    ];

    /// <summary>The build rule of box 4: exit code 0, and the output reports <c>0 Warning(s)</c> and <c>0 Error(s)</c>.</summary>
    /// <param name="output">The captured output.</param>
    /// <param name="exitCode">The exit code of the command.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> BuildOutputViolations(string output, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(output);

        List<string> violations = [];
        if (exitCode != 0)
        {
            violations.Add("build failed: exit " + exitCode.ToString(CultureInfo.InvariantCulture));
        }

        AddCountViolations(violations, WarningCount().Matches(output), "0 Warning(s)", "build has warnings");
        AddCountViolations(violations, ErrorCount().Matches(output), "0 Error(s)", "build has errors");
        return violations;
    }

    /// <summary>The counts the summary lines of a captured <c>dotnet test</c> output add up to, or null when the output has no summary line.</summary>
    /// <param name="output">The captured output.</param>
    /// <returns>Passed, failed and skipped.</returns>
    public static (int Passed, int Failed, int Skipped)? TestRunCounts(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        MatchCollection summaries = TestSummary().Matches(output);
        if (summaries.Count == 0)
        {
            return null;
        }

        int Sum(string group) => summaries.Sum(match => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture));
        return (Sum("passed"), Sum("failed"), Sum("skipped"));
    }

    /// <summary>The test rule of box 6: the output has a summary, no test failed and the exit code is 0.</summary>
    /// <param name="output">The captured output.</param>
    /// <param name="exitCode">The exit code of the command.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> TestRunViolations(string output, int exitCode)
    {
        List<string> violations = [];
        (int Passed, int Failed, int Skipped)? counts = TestRunCounts(output);
        if (counts is null)
        {
            violations.Add("no test summary in the output");
        }
        else if (counts.Value.Failed > 0)
        {
            violations.Add("tests failed: " + counts.Value.Failed.ToString(CultureInfo.InvariantCulture));
        }

        if (exitCode != 0)
        {
            violations.Add("dotnet test exit code is " + exitCode.ToString(CultureInfo.InvariantCulture));
        }

        return violations;
    }

    /// <summary>The line the build log records for the test run: <c>dotnet test: passed=412 failed=0 skipped=9 exit=0</c>.</summary>
    /// <param name="passed">The passed count.</param>
    /// <param name="failed">The failed count.</param>
    /// <param name="skipped">The skipped count.</param>
    /// <param name="exitCode">The exit code.</param>
    /// <returns>The line.</returns>
    public static string TestSummaryLine(int passed, int failed, int skipped, int exitCode) =>
        string.Create(CultureInfo.InvariantCulture, $"dotnet test: passed={passed} failed={failed} skipped={skipped} exit={exitCode}");

    /// <summary>The line for a captured output, or null when it has no summary.</summary>
    /// <param name="output">The captured output.</param>
    /// <param name="exitCode">The exit code.</param>
    /// <returns>The line or null.</returns>
    public static string? TestSummaryLineOf(string output, int exitCode) =>
        TestRunCounts(output) is { } counts ? TestSummaryLine(counts.Passed, counts.Failed, counts.Skipped, exitCode) : null;

    /// <summary>The build log holds the summary line with failed 0 and exit 0.</summary>
    /// <param name="buildLog">The text of the build log, or null when absent.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> TestSummaryRecordViolations(string? buildLog)
    {
        if (buildLog is null)
        {
            return ["docs/build-log.md is missing"];
        }

        MatchCollection lines = TestSummaryRecord().Matches(buildLog);
        if (lines.Count == 0)
        {
            return ["the build log has no dotnet test summary line"];
        }

        Match last = lines[^1];
        List<string> violations = [];
        if (last.Groups["failed"].Value != "0")
        {
            violations.Add("tests failed: " + last.Groups["failed"].Value);
        }

        if (last.Groups["exit"].Value != "0")
        {
            violations.Add("dotnet test exit code is " + last.Groups["exit"].Value);
        }

        return violations;
    }

    /// <summary>The format rule of box 5: <c>dotnet format --verify-no-changes</c> exits 0.</summary>
    /// <param name="exitCode">The exit code.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> FormatViolations(int exitCode) => exitCode == 0 ? [] : ["format check failed"];

    /// <summary>
    /// The workflow rule: the text names both runners, the build, format and test commands and a probe step, and holds no <c>secrets.</c> (any line, comments included, as the criterion's grep).
    /// A probe step is a line outside comments that mentions <c>probe</c>.
    /// </summary>
    /// <param name="workflow">The text of <c>.github/workflows/ci.yml</c>, or null when absent.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> CiFileViolations(string? workflow)
    {
        if (workflow is null)
        {
            return ["ci.yml is missing"];
        }

        List<string> violations = [.. CiTokens.Where(token => !workflow.Contains(token, StringComparison.Ordinal)).Select(static token => "ci.yml lacks " + token)];
        if (!DocumentChecks.Lines(workflow).Any(static line => !line.TrimStart().StartsWith('#') && line.Contains("probe", StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add("ci.yml lacks a probe step");
        }

        if (workflow.Contains("secrets.", StringComparison.Ordinal))
        {
            violations.Add("secrets reference in ci.yml");
        }

        return violations;
    }

    /// <summary>The committed workflow file under a root.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations of <see cref="CiFileViolations(string?)"/>.</returns>
    public static IReadOnlyList<string> CiFileViolationsAt(string root) => CiFileViolations(ReadText(root, ".github/workflows/ci.yml"));

    /// <summary>
    /// The pattern scan: no tracked file holds <c>nvapi-</c> followed by 8 or more of <c>[A-Za-z0-9_-]</c>. In a git work tree the scan is <c>git grep -lE</c> (names only, a matching line is never read into a message);
    /// in a folder without git metadata every file below the root is scanned. The first violation is the bare message, the following ones name the files.
    /// </summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> KeyShapedFileViolations(string root)
    {
        string[] files;
        if (HasGitMetadata(root))
        {
            GitResult scan = GitRunner.Run(root, "grep", "-lE", KeyShapePattern);
            if (scan.ExitCode > 1)
            {
                return ["scan failed: git grep exit " + scan.ExitCode.ToString(CultureInfo.InvariantCulture)];
            }

            files = [.. scan.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        else
        {
            files = [.. FilesBelow(root).Where(path => KeyShape().IsMatch(File.ReadAllText(Path.Combine(root, path))))];
        }

        return files.Length == 0 ? [] : [KeyShapedInTrackedFile, .. files.Select(static file => "  in " + file)];
    }

    /// <summary>The names of the tracked files that contain a value as a fixed string (<c>git grep --name-only -F</c>, the value on standard input so it never sits in an argument list).</summary>
    /// <param name="root">A git work tree.</param>
    /// <param name="value">The value; a value that is empty or spans lines is refused.</param>
    /// <returns>The git result: exit 0 with the file names, exit 1 with none.</returns>
    /// <exception cref="ArgumentException">The value is empty or has a line break inside.</exception>
    public static GitResult TrackedFilesContaining(string root, string value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Contains('\n', StringComparison.Ordinal) || trimmed.Contains('\r', StringComparison.Ordinal))
        {
            throw new ArgumentException("the value cannot be scanned (empty or multi-line); it is not echoed", nameof(value));
        }

        return GitRunner.RunWithInput(root, trimmed + "\n", "grep", "--name-only", "-F", "-f", "-");
    }

    /// <summary>The record of the fallback scan: its name, then each command with its exit code, a 1 followed by <c>(1 = no hits)</c>.</summary>
    /// <param name="exitCodes">The four exit codes, in the order of <see cref="ScanCommandNames"/>.</param>
    /// <returns>The line.</returns>
    /// <exception cref="ArgumentException">The list does not hold four codes.</exception>
    public static string ScanRecordLine(IReadOnlyList<int> exitCodes)
    {
        ArgumentNullException.ThrowIfNull(exitCodes);
        if (exitCodes.Count != ScanCommandNames.Count)
        {
            throw new ArgumentException("four exit codes are needed", nameof(exitCodes));
        }

        StringBuilder line = new("Secret scan: " + ScanFallbackName + "; exit codes: ");
        for (int index = 0; index < exitCodes.Count; index++)
        {
            line.Append(index == 0 ? string.Empty : ", ").Append(ScanCommandNames[index]).Append('=').Append(exitCodes[index].ToString(CultureInfo.InvariantCulture));
            if (exitCodes[index] == 1)
            {
                line.Append(" (").Append(NoHitsNote).Append(')');
            }
        }

        return line.ToString();
    }

    /// <summary>The form of the record line: older prose may mention the fallback, only a line in this form is the record.</summary>
    public const string ScanRecordMarker = "Secret scan: " + ScanFallbackName + ";";

    /// <summary>The record rule: one line names the fallback and carries the four exit codes, each 1 and annotated <c>(1 = no hits)</c>.</summary>
    /// <param name="buildLog">The text holding the record, or null when absent.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> ScanRecordViolations(string? buildLog)
    {
        if (buildLog is null)
        {
            return ["the scan record is missing"];
        }

        string? line = DocumentChecks.Lines(buildLog).LastOrDefault(static candidate => candidate.Contains(ScanRecordMarker, StringComparison.Ordinal));
        if (line is null)
        {
            return ["the scan record lacks \"" + ScanFallbackName + "\""];
        }

        List<string> violations = [];
        MatchCollection codes = ScanExitCode().Matches(line);
        foreach (string name in ScanCommandNames)
        {
            Match? found = codes.FirstOrDefault(match => string.Equals(match.Groups["name"].Value, name, StringComparison.Ordinal));
            if (found is null)
            {
                violations.Add("the scan record lacks the exit code of " + name);
            }
            else if (found.Groups["code"].Value != "1")
            {
                violations.Add(name + " exit code is " + found.Groups["code"].Value + ", expected 1 (" + NoHitsNote + ")");
            }
            else if (!found.Groups["note"].Success)
            {
                violations.Add(name + " exit code 1 is not annotated (" + NoHitsNote + ")");
            }
        }

        return violations;
    }

    private static void AddCountViolations(List<string> violations, MatchCollection counts, string expectedText, string failure)
    {
        if (counts.Count == 0)
        {
            violations.Add("build output lacks \"" + expectedText + "\"");
        }
        else if (counts.Any(static match => match.Groups["n"].Value.Trim('0').Length > 0))
        {
            violations.Add(failure);
        }
    }

    private const string KeyShapePattern = "nvapi-[A-Za-z0-9_-]{8,}";

    [GeneratedRegex(@"(?<n>[0-9]+) Warning\(s\)", RegexOptions.CultureInvariant)]
    private static partial Regex WarningCount();

    [GeneratedRegex(@"(?<n>[0-9]+) Error\(s\)", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCount();

    [GeneratedRegex(@"Failed:\s+(?<failed>[0-9]+),\s+Passed:\s+(?<passed>[0-9]+),\s+Skipped:\s+(?<skipped>[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TestSummary();

    [GeneratedRegex(@"dotnet test: passed=(?<passed>[0-9]+) failed=(?<failed>[0-9]+) skipped=(?<skipped>[0-9]+) exit=(?<exit>[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TestSummaryRecord();

    [GeneratedRegex("nvapi-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyShape();

    [GeneratedRegex(@"(?<name>pattern|NVIDIA_API_KEY|NEBIUS_API_KEY|NGC_API_KEY)=(?<code>[0-9]+)(?<note> \(1 = no hits\))?", RegexOptions.CultureInvariant)]
    private static partial Regex ScanExitCode();
}