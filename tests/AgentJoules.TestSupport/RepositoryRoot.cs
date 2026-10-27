namespace AgentJoules.TestSupport;

/// <summary>
/// The repository root for tests that read committed files. It wraps <see cref="ConfigurationFixtures.RepositoryRoot"/>, which walks up from the test assembly to the folder that holds <c>AgentJoules.slnx</c>, so
/// there is one mechanism on Windows and on the Linux runner.
/// </summary>
public static class RepositoryRoot
{
    /// <summary>The absolute path of the folder that holds <c>AgentJoules.slnx</c>.</summary>
    /// <exception cref="InvalidOperationException">No parent folder of the test assembly holds the solution file.</exception>
    public static string Path => System.IO.Path.GetFullPath(ConfigurationFixtures.RepositoryRoot());

    /// <summary>Resolves a repository-relative path (either separator) to an absolute path.</summary>
    /// <param name="relativePath">A path such as <c>docs/devpost-pitch.md</c>.</param>
    /// <returns>The absolute path under <see cref="Path"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is blank or rooted.</exception>
    public static string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string normalised = relativePath.Replace('\\', '/');
        // A colon is a drive prefix (C:/x) or an alternate data stream: refused on every platform, because Unix does not read C:/x as rooted.
        if (normalised[0] == '/' || normalised.Contains(':', StringComparison.Ordinal) || System.IO.Path.IsPathRooted(normalised))
        {
            throw new ArgumentException("The path must be relative to the repository root.", nameof(relativePath));
        }

        return System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, normalised.Replace('/', System.IO.Path.DirectorySeparatorChar)));
    }

    /// <summary>Reads a committed text file by its repository-relative path.</summary>
    /// <param name="relativePath">A path such as <c>README.md</c>.</param>
    /// <returns>The text of the file.</returns>
    public static string ReadAllText(string relativePath) => File.ReadAllText(Resolve(relativePath));
}