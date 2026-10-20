using System.Reflection;
using System.Runtime.InteropServices;

namespace AgentJoules.Nvml.Interop;

/// <summary>
/// Resolves the logical library name <c>nvml</c> to the driver's library for the current OS (design 3.1).
/// On Windows a bare candidate is always loaded from the system directory by its full path, never by name, so a copy
/// of <c>nvml.dll</c> next to the executable is never bound; on Linux the soname goes straight to dlopen. When no candidate
/// loads, the resolver throws <see cref="DllNotFoundException"/> by design instead of returning zero, so the runtime's
/// default search (which probes the application folder) never runs. The first handle is cached for every later import.
/// </summary>
/// <remarks>
/// Tests must use <c>ScriptedNvmlApi.MissingLibrary</c> and never register their own resolver on this assembly: a second
/// <see cref="NativeLibrary.SetDllImportResolver"/> call throws <see cref="InvalidOperationException"/>, which the registration <see cref="Lazy{T}"/> would cache.
/// </remarks>
public static class NvmlLibraryResolver
{
    private const string LogicalName = "nvml";

    private static readonly Lazy<bool> Registration = new(Register);

    // The runtime does not cache resolver results, so without this every import would run LoadLibrary again.
    private static nint s_handle;

    /// <summary>Registers the resolver for this assembly; later calls are no-ops.</summary>
    public static void EnsureRegistered()
    {
        _ = Registration.Value;
    }

    /// <summary>Loads the first candidate the loader accepts.</summary>
    /// <param name="candidates">The names to try, in order.</param>
    /// <param name="loader">Returns the library handle, or zero when the candidate cannot be loaded; a missing file is zero, not an exception.</param>
    /// <param name="handle">The first non-zero handle, or zero when every candidate failed.</param>
    /// <returns>True when a candidate loaded.</returns>
    public static bool TryLoadFirst(IReadOnlyList<string> candidates, Func<string, nint> loader, out nint handle)
    {
        foreach (string candidate in candidates)
        {
            nint loaded = loader(candidate);
            if (loaded != 0)
            {
                handle = loaded;
                return true;
            }
        }

        handle = 0;
        return false;
    }

    /// <summary>Maps a Windows candidate to the path that is loaded: a bare name is qualified with the system directory, a path with a directory part is returned unchanged.</summary>
    /// <param name="candidate">A candidate from <see cref="NvmlLibraryCandidates.For"/>.</param>
    /// <param name="systemDirectory">The Windows system directory, for example <c>C:\Windows\system32</c>.</param>
    /// <returns>The path to pass to the loader.</returns>
    internal static string ToLoadPath(string candidate, string systemDirectory) =>
        candidate.AsSpan().IndexOfAny('\\', '/', ':') >= 0
            ? candidate
            : $@"{systemDirectory.TrimEnd('\\')}\{candidate}";

    /// <summary>The exact strings the production resolver asks the loader for, in order.</summary>
    internal static IReadOnlyList<string> LoadPaths(OSPlatform platform, string? programW6432, string systemDirectory)
    {
        IReadOnlyList<string> candidates = NvmlLibraryCandidates.For(platform, programW6432);
        return platform == OSPlatform.Windows
            ? [.. candidates.Select(candidate => ToLoadPath(candidate, systemDirectory))]
            : candidates;
    }

    // The runtime calls this for every import of this assembly; any library other than nvml keeps the default resolution.
    internal static nint Resolve(string libraryName, Assembly _, DllImportSearchPath? __)
    {
        if (libraryName != LogicalName)
        {
            return IntPtr.Zero;
        }

        nint cached = Volatile.Read(ref s_handle);
        if (cached != 0)
        {
            return cached;
        }

        OSPlatform platform = OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsLinux() ? OSPlatform.Linux : OSPlatform.Create("UNSUPPORTED");
        IReadOnlyList<string> loadPaths = LoadPaths(platform, Environment.GetEnvironmentVariable("ProgramW6432"), Environment.SystemDirectory);

        if (!TryLoadFirst(loadPaths, static path => NativeLibrary.TryLoad(path, out nint loaded) ? loaded : IntPtr.Zero, out nint handle))
        {
            throw new DllNotFoundException($"nvml: none of the candidates could be loaded: {string.Join(", ", loadPaths)}");
        }

        // Two threads may both load; the module is reference counted, so keep the first handle and release the extra one.
        nint winner = Interlocked.CompareExchange(ref s_handle, handle, 0);
        if (winner == 0)
        {
            return handle;
        }

        NativeLibrary.Free(handle);
        return winner;
    }

    private static bool Register()
    {
        NativeLibrary.SetDllImportResolver(typeof(NvmlNative).Assembly, Resolve);
        return true;
    }
}