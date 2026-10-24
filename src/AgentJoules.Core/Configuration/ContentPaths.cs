using AgentJoules.Core.Cli;

namespace AgentJoules.Core.Configuration;

/// <summary>
/// Resolves a configured path (a <c>Paths:*</c> key) against the content root, the working directory of the command (design 3.6 point d), so a configuration cannot point a run at a
/// folder outside the repository. The value must be relative; a backslash is a separator on every host; a colon (a drive or an alternate data stream), a segment made only of dots
/// other than <c>.</c> (with or without spaces around it, which Windows trims) and a reserved device name are refused before the file system sees the path, so the refusals are the
/// same on Windows and on Linux; an accepted value can still differ, because Windows trims a trailing space and Linux keeps it. The result must lie strictly inside the content root, and no folder on the way from the root to it may be a link (a symbolic link or a junction). A
/// refusal is a <see cref="ConfigurationException"/> (exit code 2) that names the key and never the value or a path.
/// </summary>
public static class ContentPaths
{
    private static readonly char[] Separators = ['/', '\\'];

    // The device names Windows reserves in any folder, with or without an extension; refused on every host so the refusals are the same everywhere.
    // The superscript digits one, two and three, written as casts so the source stays ASCII.
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM" + (char)0x00B9, "COM" + (char)0x00B2, "COM" + (char)0x00B3,
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT" + (char)0x00B9, "LPT" + (char)0x00B2, "LPT" + (char)0x00B3,
    ];

    /// <summary>Resolves <paramref name="value"/> under <paramref name="contentRoot"/>.</summary>
    /// <param name="contentRoot">The absolute content root.</param>
    /// <param name="value">The configured relative path, with <c>/</c> or <c>\</c> separators.</param>
    /// <param name="key">The configuration key of the value, for example <c>Paths:Tasks</c>, used in the message.</param>
    /// <returns>The full path, strictly inside <paramref name="contentRoot"/>; it need not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="contentRoot"/> or <paramref name="key"/> is null, empty or white space.</exception>
    /// <exception cref="ConfigurationException">
    /// The value is blank or rooted, holds a colon, a parent segment or a reserved device name, does not resolve to a path strictly inside the content root, or passes through a link.
    /// </exception>
    public static string Resolve(string contentRoot, string? value, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (string.IsNullOrWhiteSpace(value) || value.Contains(':', StringComparison.Ordinal))
        {
            throw Refused(key);
        }

        string normalised = value.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalised) || value.Split(Separators).Any(IsForbiddenSegment))
        {
            throw Refused(key);
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, normalised)));
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not attached: the message of the failure can quote the path.
            throw Refused(key);
        }

        if (!IsStrictlyInside(root, full))
        {
            throw Refused(key);
        }

        RefuseLinks(root, full, key);
        return full;
    }

    /// <summary>True when <paramref name="full"/> lies strictly inside <paramref name="root"/>: below it, not equal to it, and not a sibling that only shares its name as a prefix.</summary>
    /// <param name="root">The absolute root, with or without a trailing separator.</param>
    /// <param name="full">The absolute candidate.</param>
    /// <returns>Whether the candidate is inside the root; letter case is ignored on Windows and macOS.</returns>
    internal static bool IsStrictlyInside(string root, string full)
    {
        // A drive or file-system root keeps its separator after the trim.
        string trimmed = Path.TrimEndingDirectorySeparator(root);
        string prefix = Path.EndsInDirectorySeparator(trimmed) ? trimmed : trimmed + Path.DirectorySeparatorChar;
        string candidate = Path.TrimEndingDirectorySeparator(full);
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Length > prefix.Length && candidate.StartsWith(prefix, comparison);
    }

    // A parent segment in any spelling Windows trims to one (".. ", " ..", "..."), "." with spaces around it, and a reserved device name with or without an extension.
    private static bool IsForbiddenSegment(string segment)
    {
        string trimmed = segment.Trim(' ');
        if (trimmed.Length > 0 && trimmed.All(static character => character == '.') && segment != ".")
        {
            return true;
        }

        string stem = trimmed.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    // Every component from the root to the resolved path that exists must not be a link: a link would let a folder inside the repository point anywhere.
    private static void RefuseLinks(string root, string full, string key)
    {
        string current = root;
        foreach (string segment in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (IsLink(current, key))
            {
                throw new ConfigurationException(key + " must not pass through a link (a symbolic link or a junction); the path is not shown");
            }
        }
    }

    private static bool IsLink(string path, string key)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A component that cannot be inspected is not trusted.
            throw new ConfigurationException(key + ": a folder on the way could not be inspected for a link, so the path is refused; the path is not shown");
        }
    }

    private static ConfigurationException Refused(string key) =>
        new($"{key} must be a relative path inside the working directory, without parent segments; the value is not shown");
}