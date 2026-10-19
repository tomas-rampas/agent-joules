using System.Runtime.InteropServices;

namespace AgentJoules.Nvml.Interop;

/// <summary>The ordered library names the resolver tries for the logical NVML library name (design 3.1).</summary>
public static class NvmlLibraryCandidates
{
    /// <summary>Builds the candidate list for a platform.</summary>
    /// <param name="platform">The operating system; only Windows and Linux have candidates.</param>
    /// <param name="programW6432">
    /// The ProgramW6432 directory. The NVSMI path is added only when this is a fully qualified Windows path (a drive letter
    /// with a separator, or a UNC path), so null, empty, whitespace, <c>C:foo</c>, <c>\foo</c> and <c>relative</c> never produce a
    /// path that depends on the current directory. A trailing backslash is trimmed before joining. The check is lexical, so
    /// the list does not depend on the OS the code runs on.
    /// </param>
    /// <returns>The names in the order they are tried; empty for any other platform.</returns>
    public static IReadOnlyList<string> For(OSPlatform platform, string? programW6432)
    {
        return platform == OSPlatform.Windows
            ? WindowsCandidates(programW6432)
            : platform == OSPlatform.Linux ? ["libnvidia-ml.so.1", "libnvidia-ml.so"] : [];
    }

    private static string[] WindowsCandidates(string? programW6432)
    {
        string? directory = IsFullyQualifiedWindowsPath(programW6432) ? programW6432!.TrimEnd('\\') : null;
        return string.IsNullOrWhiteSpace(directory)
            ? ["nvml.dll"]
            : ["nvml.dll", $@"{directory}\NVIDIA Corporation\NVSMI\nvml.dll"];
    }

    private static bool IsFullyQualifiedWindowsPath(string? path) =>
        path is { Length: >= 3 }
        && ((char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
            || path.StartsWith(@"\\", StringComparison.Ordinal));
}