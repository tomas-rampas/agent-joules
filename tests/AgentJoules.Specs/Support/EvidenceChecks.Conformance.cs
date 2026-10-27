using System.Globalization;
using System.Text.RegularExpressions;

using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The conformance rules of AJ-154 (the <c>Acceptance matrix</c> and the <c>Spec filter results</c> tables of <c>docs/build-log.md</c>, and the scope of the repository). Each rule returns its violations as text, an empty list
/// being a pass; the wording of a violation is the wording the negative scenarios assert. The table rules take the text of the build log, the scope rules take the root of a repository-shaped folder.
/// </summary>
public static partial class EvidenceChecks
{
    /// <summary>The title of the matrix table in the build log.</summary>
    public const string AcceptanceMatrixTitle = "Acceptance matrix";

    /// <summary>The title of the filter table in the build log.</summary>
    public const string SpecFilterTitle = "Spec filter results";

    /// <summary>The statuses a matrix row may carry.</summary>
    public static IReadOnlyList<string> MatrixStatuses { get; } = ["MET", "UNMET", "BLOCKED", "USER-ACTION"];

    /// <summary>The statuses that need a reason in the fifth column.</summary>
    public static IReadOnlyList<string> StatusesNeedingReason { get; } = ["UNMET", "BLOCKED", "USER-ACTION"];

    /// <summary>The 51 identifiers of the matrix: REQ-001 to REQ-032, then EDGE-001 to EDGE-019.</summary>
    public static IReadOnlyList<string> MatrixIds { get; } =
    [
        .. Enumerable.Range(1, 32).Select(static n => "REQ-" + n.ToString("000", CultureInfo.InvariantCulture)),
        .. Enumerable.Range(1, 19).Select(static n => "EDGE-" + n.ToString("000", CultureInfo.InvariantCulture)),
    ];

    /// <summary>The 36 identifiers whose acceptance names a unit test (the list of AJ-154 criterion 1).</summary>
    public static IReadOnlyList<string> SpecFilterIds { get; } =
    [
        .. new[] { 3, 4, 5, 7, 8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 25, 26, 29, 30, 32 }.Select(static n => "REQ-" + n.ToString("000", CultureInfo.InvariantCulture)),
        .. new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13, 15, 16, 17, 18, 19 }.Select(static n => "EDGE-" + n.ToString("000", CultureInfo.InvariantCulture)),
    ];

    /// <summary>The seven project folders under <c>src/</c> of the technical design.</summary>
    public static IReadOnlyList<string> SourceProjects { get; } =
        ["AgentJoules.Nvml", "AgentJoules.Core", "AgentJoules.Metering", "AgentJoules.Workflow", "AgentJoules.Reporting", "AgentJoules.Dashboard", "AgentJoules.Cli"];

    /// <summary>The four project folders under <c>tests/</c> of the technical design.</summary>
    public static IReadOnlyList<string> TestProjects { get; } =
        ["AgentJoules.TestSupport", "AgentJoules.UnitTests", "AgentJoules.Specs", "AgentJoules.IntegrationTests"];

    /// <summary>The message of the stretch rule.</summary>
    public const string StretchTooEarly = "stretch started before the Definition of Done was complete";

    private static readonly string[] SkippedFolders = [".git", "bin", "obj", "node_modules", ".vs"];

    /// <summary>
    /// The matrix rule: the table under the <c>Acceptance matrix</c> caption of the build log has one row for each of the 51 identifiers, in the columns
    /// <c>| ID | Command or artifact | Observed | Status | Reason |</c>; the status is one of <see cref="MatrixStatuses"/> and a status that is not MET carries its reason in the fifth column.
    /// </summary>
    /// <param name="buildLog">The text of the build log, or null when the file is absent.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> AcceptanceMatrixViolations(string? buildLog)
    {
        if (buildLog is null)
        {
            return ["docs/build-log.md is missing"];
        }

        List<string[]> rows = TableRowsUnder(buildLog, AcceptanceMatrixTitle);
        if (rows.Count == 0)
        {
            return ["no " + AcceptanceMatrixTitle + " table"];
        }

        List<string> violations = [];
        string[][] matrixRows = [.. rows.Where(static row => MatrixIdCell().IsMatch(CleanCell(row[0])))];
        violations.AddRange(rows.Select(static row => CleanCell(row[0])).Where(static id => MatrixIdLike().IsMatch(id) && !MatrixIdCell().IsMatch(id)).Distinct(StringComparer.Ordinal).Select(static id => "malformed row: " + id));
        foreach (string id in MatrixIds)
        {
            string[][] found = [.. matrixRows.Where(row => string.Equals(CleanCell(row[0]), id, StringComparison.Ordinal))];
            if (found.Length == 0)
            {
                violations.Add("missing row: " + id);
                continue;
            }

            if (found.Length > 1)
            {
                violations.Add("duplicate row: " + id);
            }

            AddMatrixRowViolations(violations, id, found[0]);
        }

        violations.AddRange(matrixRows.Select(static row => CleanCell(row[0])).Where(id => !MatrixIds.Contains(id, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Select(static id => "unexpected row: " + id));
        return violations;
    }

    /// <summary>The identifiers of the matrix rows, in file order.</summary>
    /// <param name="buildLog">The text of the build log.</param>
    /// <returns>The identifiers.</returns>
    public static IReadOnlyList<string> AcceptanceMatrixRowIds(string buildLog) =>
        [.. TableRowsUnder(buildLog, AcceptanceMatrixTitle).Select(static row => CleanCell(row[0])).Where(static id => MatrixIdCell().IsMatch(id))];

    /// <summary>
    /// The filter rule: the table under the <c>Spec filter results</c> caption has one row for each of the 36 identifiers, in the columns <c>| ID | Passed | Failed | Skipped | Exit code |</c>
    /// (the ID cell is <c>Spec=REQ-003</c> or <c>REQ-003</c>); each row has passed 1 or more, failed 0 and exit code 0.
    /// </summary>
    /// <param name="buildLog">The text of the build log, or null when the file is absent.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> SpecFilterViolations(string? buildLog)
    {
        if (buildLog is null)
        {
            return ["docs/build-log.md is missing"];
        }

        List<string[]> rows = TableRowsUnder(buildLog, SpecFilterTitle);
        if (rows.Count == 0)
        {
            return ["no " + SpecFilterTitle + " table"];
        }

        List<string> violations = [];
        foreach (string id in SpecFilterIds)
        {
            string[][] found = [.. rows.Where(row => string.Equals(FilterId(row[0]), id, StringComparison.Ordinal))];
            if (found.Length == 0)
            {
                violations.Add("missing row: " + id);
                continue;
            }

            if (found.Length > 1)
            {
                violations.Add("duplicate row: " + id);
            }

            AddFilterRowViolations(violations, id, found[0]);
        }

        violations.AddRange(rows.Select(static row => FilterId(row[0])).Where(id => MatrixIdLike().IsMatch(id) && !SpecFilterIds.Contains(id, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Select(static id => "unexpected row: " + id));

        return violations;
    }

    /// <summary>The identifiers of the filter rows, in file order.</summary>
    /// <param name="buildLog">The text of the build log.</param>
    /// <returns>The identifiers.</returns>
    public static IReadOnlyList<string> SpecFilterRowIds(string buildLog) =>
        [.. TableRowsUnder(buildLog, SpecFilterTitle).Select(static row => FilterId(row[0])).Where(static id => MatrixIdCell().IsMatch(id))];

    /// <summary>The scope rule of criterion 3 as a whole: packages, Python files, the packable switch, the project folders and the stretch commits.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <param name="uncheckedBoxes">The number of Definition of Done boxes still unchecked; a <c>STRETCH:</c> commit is allowed only at 0.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> ScopeViolations(string root, int uncheckedBoxes) =>
        [.. PackageScopeViolations(root), .. PythonFileViolations(root), .. PackableViolations(root), .. ProjectFolderViolations(root), .. StretchViolations(root, uncheckedBoxes)];

    /// <summary>
    /// Every line of <c>Directory.Packages.props</c> that names an out-of-scope package (AgentHooks, Agents.AI.Mcp, DevUI, Agents.AI.A2A, SemanticKernel, Plotly, ScottPlot, OxyPlot; the same words as the criterion's grep)
    /// is a violation, and so is a package whose version carries an alpha or preview suffix.
    /// </summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> PackageScopeViolations(string root)
    {
        string? props = ReadText(root, "Directory.Packages.props");
        if (props is null)
        {
            return ["Directory.Packages.props is missing"];
        }

        List<string> violations = [];
        foreach (string line in DocumentChecks.Lines(props))
        {
            Match package = PackageLine().Match(line);
            string name = package.Success ? package.Groups["name"].Value : string.Empty;
            if (OutOfScopeWords().Match(line) is { Success: true } word)
            {
                violations.Add("out-of-scope package: " + (name.Length > 0 ? name : word.Value));
            }

            if (package.Success && PrereleaseVersion().IsMatch(package.Groups["version"].Value))
            {
                violations.Add("prerelease package: " + name + " " + package.Groups["version"].Value);
            }
        }

        return violations;
    }

    /// <summary>
    /// No Python file is tracked. In a git work tree the tracked names come from <c>git ls-files</c>; in a folder without <c>.git</c> every file below the root counts (bin, obj and node_modules left out).
    /// </summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> PythonFileViolations(string root)
    {
        IEnumerable<string> names;
        if (HasGitMetadata(root))
        {
            GitResult listing = GitRunner.Run(root, "ls-files", "*.py");
            if (listing.ExitCode != 0)
            {
                return ["git ls-files failed: exit " + listing.ExitCode.ToString(CultureInfo.InvariantCulture)];
            }

            names = listing.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else
        {
            names = FilesBelow(root).Where(static path => path.EndsWith(".py", StringComparison.Ordinal));
        }

        return [.. names.Select(static name => "python file: " + name)];
    }

    /// <summary><c>Directory.Build.props</c> holds the line <c>&lt;IsPackable&gt;false&lt;/IsPackable&gt;</c> exactly once (<c>grep -c</c> counts lines).</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> PackableViolations(string root)
    {
        string? props = ReadText(root, "Directory.Build.props");
        int count = props is null ? 0 : LinesContaining(props, "<IsPackable>false</IsPackable>");
        return count == 1 ? [] : ["IsPackable false occurs " + count.ToString(CultureInfo.InvariantCulture) + " times in Directory.Build.props, expected 1"];
    }

    /// <summary>The project folders (a folder that holds a <c>.csproj</c>) under <c>src/</c> and <c>tests/</c> equal the eleven of the technical design.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> ProjectFolderViolations(string root)
    {
        List<string> violations = [];
        AddFolderViolations(violations, root, "src", SourceProjects);
        AddFolderViolations(violations, root, "tests", TestProjects);
        return violations;
    }

    /// <summary>
    /// No commit subject of the git history starts with <c>STRETCH:</c> unless every box is ticked. A folder without git metadata, or a repository without a commit, has no history and passes.
    /// </summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <param name="uncheckedBoxes">The number of boxes still unchecked.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> StretchViolations(string root, int uncheckedBoxes)
    {
        if (uncheckedBoxes <= 0 || !HasGitMetadata(root))
        {
            return [];
        }

        GitResult log = GitRunner.Run(root, "log", "--format=%s");
        if (log.ExitCode != 0)
        {
            return [];
        }

        return log.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(static subject => subject.StartsWith("STRETCH:", StringComparison.Ordinal)) ? [StretchTooEarly] : [];
    }

    /// <summary>Commits in a temporary repository without a hook: the hooks folder is set to a folder that does not exist and the verify step is skipped, so a global or template hook cannot run or reject the subject.</summary>
    /// <param name="root">The repository folder.</param>
    /// <param name="subject">The commit subject.</param>
    /// <returns>The git result.</returns>
    public static GitResult CommitWithoutHooks(string root, string subject) =>
        GitRunner.Run(root, "-c", "core.hooksPath=" + Path.Combine(root, "no-hooks"), "commit", "--quiet", "--no-verify", "--allow-empty", "-m", subject);

    private static void AddMatrixRowViolations(List<string> violations, string id, string[] row)
    {
        if (row.Length < 2 || CleanCell(row[1]).Length == 0)
        {
            violations.Add("empty evidence: " + id);
        }

        string status = row.Length > 3 ? CleanCell(row[3]) : string.Empty;
        if (status.Length == 0)
        {
            violations.Add("empty status: " + id);
        }
        else if (!MatrixStatuses.Contains(status, StringComparer.Ordinal))
        {
            violations.Add("unknown status: " + id + " (" + status + ")");
        }
        else if (StatusesNeedingReason.Contains(status, StringComparer.Ordinal) && (row.Length < 5 || row[4].Trim().Length == 0))
        {
            violations.Add(status + " without reason: " + id);
        }
    }

    private static void AddFilterRowViolations(List<string> violations, string id, string[] row)
    {
        int?[] counts = [.. Enumerable.Range(1, 4).Select(index => row.Length > index && int.TryParse(CleanCell(row[index]), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : (int?)null)];
        if (counts.Any(static count => count is null))
        {
            violations.Add(id + " has a missing or non-numeric count");
            return;
        }

        if (counts[1] > 0)
        {
            violations.Add(id + " has failed tests");
        }

        if (counts[0] < 1)
        {
            violations.Add(id + " has no passed test");
        }

        if (counts[3] != 0)
        {
            violations.Add(id + " exit code is " + counts[3]!.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddFolderViolations(List<string> violations, string root, string parent, IReadOnlyList<string> expected)
    {
        string folder = Path.Combine(root, parent);
        string[] found = Directory.Exists(folder)
            ? [.. Directory.EnumerateDirectories(folder).Where(static dir => Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).Any()).Select(static dir => Path.GetFileName(dir)).Order(StringComparer.Ordinal)]
            : [];
        violations.AddRange(expected.Where(name => !found.Contains(name, StringComparer.Ordinal)).Select(name => "missing project: " + parent + "/" + name));
        violations.AddRange(found.Where(name => !expected.Contains(name, StringComparer.Ordinal)).Select(name => "unexpected project: " + parent + "/" + name));
    }

    private static bool HasGitMetadata(string root) => Path.Exists(Path.Combine(root, ".git"));

    private static IEnumerable<string> FilesBelow(string root)
    {
        Stack<string> pending = new([root]);
        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                yield return Path.GetRelativePath(root, file).Replace('\\', '/');
            }

            foreach (string child in Directory.EnumerateDirectories(folder).Where(static dir => !SkippedFolders.Contains(Path.GetFileName(dir), StringComparer.Ordinal)))
            {
                pending.Push(child);
            }
        }
    }

    // The data rows (cells split on the bars, outer bars removed) of the table that follows the last caption with this title; the header and the separator rows are included and do not match an identifier.
    private static List<string[]> TableRowsUnder(string text, string title)
    {
        string[] lines = DocumentChecks.Lines(text);

        // A heading caption wins; the bold and plain captions are read only when no heading names the table.
        bool headingExists = lines.Any(line => line.StartsWith('#') && IsCaption(line, title));
        for (int caption = lines.Length - 1; caption >= 0; caption--)
        {
            if (!IsCaption(lines[caption], title) || (headingExists && !lines[caption].StartsWith('#')))
            {
                continue;
            }

            int start = caption + 1;
            while (start < lines.Length && !lines[start].StartsWith('|') && !lines[start].StartsWith('#'))
            {
                start++;
            }

            List<string[]> rows = [];
            for (int index = start; index < lines.Length && lines[index].StartsWith('|'); index++)
            {
                string body = lines[index].Trim().Trim('|');
                rows.Add(body.Split('|'));
            }

            if (rows.Count > 0)
            {
                return rows;
            }
        }

        return [];
    }

    private static bool IsCaption(string line, string title)
    {
        if (line.StartsWith('|'))
        {
            return false;
        }

        if (line.StartsWith('#'))
        {
            return line.Contains(title, StringComparison.OrdinalIgnoreCase);
        }

        string stripped = line.TrimStart('*', '_', ' ');
        return stripped.StartsWith(title, StringComparison.OrdinalIgnoreCase) || stripped.StartsWith("Table: " + title, StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanCell(string cell) => cell.Replace("*", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal).Trim();

    private static string FilterId(string cell) => CleanCell(cell).Replace("Spec=", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"^(REQ|EDGE)-", RegexOptions.CultureInvariant)]
    private static partial Regex MatrixIdLike();

    [GeneratedRegex(@"^(REQ|EDGE)-[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex MatrixIdCell();

    [GeneratedRegex(@"AgentHooks|Agents\.AI\.Mcp|DevUI|Agents\.AI\.A2A|SemanticKernel|Plotly|ScottPlot|OxyPlot", RegexOptions.CultureInvariant)]
    private static partial Regex OutOfScopeWords();

    [GeneratedRegex(@"<PackageVersion\s+(?:Include|Update)=""(?<name>[^""]+)""\s+Version=""(?<version>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex PackageLine();

    [GeneratedRegex(@"-(alpha|preview)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PrereleaseVersion();
}