using System.Globalization;

namespace AgentJoules.TestSupport;

/// <summary>The outcome of a <see cref="KeyHygieneCheck"/>: whether the tree is free of private-key file names, which names offend, and a message that names them and never quotes a file body.</summary>
/// <param name="Passed">True when no file name matches.</param>
/// <param name="Offenders">The relative names (forward slashes) that match, sorted ordinally.</param>
/// <param name="Message">A one-line description; the file names only.</param>
public sealed record KeyHygieneResult(bool Passed, IReadOnlyList<string> Offenders, string Message);

/// <summary>
/// The key hygiene check of AJ-120 (REQ-031): no file name in a tree contains <c>id_ed25519</c>, <c>id_rsa</c> or <c>.pem</c>. Only names are looked at, a file is never opened, so no key value can reach a message or a log.
/// <see cref="Run"/> scans a directory; <see cref="RunTracked"/> checks the names git tracks in a repository.
/// </summary>
public static class KeyHygieneCheck
{
    /// <summary>The name fragments that mark private-key material, matched ignoring case anywhere in a path.</summary>
    public static IReadOnlyList<string> Patterns { get; } = ["id_ed25519", "id_rsa", ".pem"];

    /// <summary>The names <c>git check-ignore</c> must print for the three patterns.</summary>
    public static IReadOnlyList<string> IgnoreProbeNames { get; } = ["id_ed25519", "id_rsa", "key.pem"];

    /// <summary>Scans the file names under a directory, recursively; file contents are never read.</summary>
    /// <param name="directory">The directory to scan.</param>
    /// <returns>The result; <see cref="KeyHygieneResult.Offenders"/> holds paths relative to <paramref name="directory"/>.</returns>
    public static KeyHygieneResult Run(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string[] names = [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))];
        return Evaluate(names);
    }

    /// <summary>Checks the names git tracks in a repository (<c>git ls-files</c>).</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The result.</returns>
    /// <exception cref="InvalidOperationException">git is not on PATH, or the call failed.</exception>
    public static KeyHygieneResult RunTracked(string repositoryRoot)
    {
        string output = Git(repositoryRoot, ["ls-files"]) ?? throw new InvalidOperationException("git is not installed or not on PATH");
        return Evaluate(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>Runs <c>git check-ignore</c> over <see cref="IgnoreProbeNames"/>.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The names git printed, which are the ignored ones.</returns>
    /// <exception cref="InvalidOperationException">git is not on PATH, or the call failed.</exception>
    public static IReadOnlyList<string> IgnoredProbeNames(string repositoryRoot)
    {
        string output = Git(repositoryRoot, ["check-ignore", .. IgnoreProbeNames], allowedExit: 1) ?? throw new InvalidOperationException("git is not installed or not on PATH");
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static KeyHygieneResult Evaluate(IEnumerable<string> names)
    {
        string[] offenders = [.. names.Where(static name => Patterns.Any(pattern => name.Contains(pattern, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.Ordinal)];
        return offenders.Length == 0
            ? new KeyHygieneResult(true, offenders, "No file name looks like private-key material.")
            : new KeyHygieneResult(false, offenders, "Private-key file names found: " + string.Join(", ", offenders) + ". Keep the key outside the repository.");
    }

    // Null when git cannot start; throws when it exits with a code other than 0 or allowedExit.
    private static string? Git(string workingDirectory, IEnumerable<string> arguments, int allowedExit = 0)
    {
        GitResult result;
        try
        {
            result = GitRunner.Run(workingDirectory, [.. arguments]);
        }
        catch (InvalidOperationException exception) when (exception.InnerException is System.ComponentModel.Win32Exception)
        {
            return null;
        }

        return result.ExitCode == 0 || result.ExitCode == allowedExit
            ? result.Stdout
            : throw new InvalidOperationException("git exited with " + result.ExitCode.ToString(CultureInfo.InvariantCulture));
    }
}