using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// Read-only access to the committed files the content checks look at (AJ-134 to AJ-137): existence, text, the run files of <c>results/</c> and the set of repository-relative paths the artifact check searches. Nothing is
/// created, changed or deleted.
/// </summary>
public static class RepositoryFiles
{
    private static readonly string[] Folders = ["results", "docs", "content", "tasks"];

    private static readonly string[] RootFiles = ["README.md", "LICENSE", ".gitignore"];

    /// <summary>Whether a committed file exists.</summary>
    /// <param name="relativePath">The repository-relative path, for example <c>results/x.json</c>.</param>
    /// <returns>True when the file exists.</returns>
    public static bool Exists(string relativePath) => File.Exists(RepositoryRoot.Resolve(relativePath));

    /// <summary>The text of a committed file, or null when it does not exist.</summary>
    /// <param name="relativePath">The repository-relative path.</param>
    /// <returns>The text or null.</returns>
    public static string? ReadOrNull(string relativePath) => Exists(relativePath) ? RepositoryRoot.ReadAllText(relativePath) : null;

    /// <summary>The file names in <c>results/</c> in ordinal order.</summary>
    /// <returns>The names without folder.</returns>
    public static IReadOnlyList<string> ResultsFileNames() =>
        [.. Directory.EnumerateFiles(RepositoryRoot.Resolve("results")).Select(static path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    /// <summary>The tier run files of <c>results/</c> (probe and calibration files are not run files) by file name with their text.</summary>
    /// <returns>The run files.</returns>
    public static IReadOnlyDictionary<string, string> RunFiles() =>
        ResultsFileNames().Where(DocumentChecks.IsRunFileName).ToDictionary(static name => name, static name => RepositoryRoot.ReadAllText("results/" + name), StringComparer.Ordinal);

    /// <summary>The newest run file of a tier by name (the timestamp sorts), without a baseline suffix.</summary>
    /// <param name="tier">The tier name.</param>
    /// <returns>The repository-relative path, or null.</returns>
    public static string? LatestRunFile(string tier)
    {
        string? name = ResultsFileNames().LastOrDefault(file => DocumentChecks.IsRunFileName(file) && file.EndsWith($"Z-{tier}.json", StringComparison.Ordinal));

        return name is null ? null : "results/" + name;
    }

    /// <summary>The text of the newest run file of a tier.</summary>
    /// <param name="tier">The tier name.</param>
    /// <returns>The text, or null when the tier has no run file.</returns>
    public static string? LatestRunText(string tier) => LatestRunFile(tier) is { } path ? RepositoryRoot.ReadAllText(path) : null;

    /// <summary>The task files by name with their text.</summary>
    /// <returns>The files of <c>tasks/*.json</c>.</returns>
    public static IReadOnlyDictionary<string, string> TaskFiles() =>
        Directory.EnumerateFiles(RepositoryRoot.Resolve("tasks"), "*.json").ToDictionary(static path => Path.GetFileName(path), static path => File.ReadAllText(path), StringComparer.Ordinal);

    /// <summary>The repository-relative paths (forward slashes) of the files under the folders the demo artifacts live in, and of the files at the root the demo names.</summary>
    /// <returns>The set of paths.</returns>
    public static HashSet<string> Files()
    {
        string root = RepositoryRoot.Path;
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (string folder in Folders.Where(static folder => Directory.Exists(RepositoryRoot.Resolve(folder))))
        {
            foreach (string path in Directory.EnumerateFiles(RepositoryRoot.Resolve(folder), "*", SearchOption.AllDirectories))
            {
                _ = files.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
            }
        }

        foreach (string name in RootFiles.Where(Exists))
        {
            _ = files.Add(name);
        }

        return files;
    }
}