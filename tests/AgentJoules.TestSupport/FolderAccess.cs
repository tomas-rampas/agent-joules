using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AgentJoules.TestSupport;

/// <summary>
/// File system set-ups that a test needs to prove a load refuses a link or survives a folder it may not list: a directory link, a folder that denies
/// listing to the current user, and the probes that say whether the host allows them. Everything happens inside a path the caller owns (a
/// <see cref="TempWorkspace"/>); nothing needs elevation except a file symbolic link on a Windows host without Developer Mode, which
/// <see cref="SymbolicLinkSkipReason"/> reports.
/// </summary>
public static class FolderAccess
{
    // Probed once per process: the account and its token do not change while the tests run.
    private static readonly Lazy<string?> ListingProbe = new(ProbeListing);
    private static readonly Lazy<string?> WritingProbe = new(ProbeWriting);

    /// <summary>Why a file symbolic link cannot be created on this host, or null when it can (probed in a scratch workspace).</summary>
    public static string? SymbolicLinkSkipReason()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string target = workspace.WriteFile("target.txt", "x");
        try
        {
            _ = File.CreateSymbolicLink(Path.Combine(workspace.Root, "link.txt"), target);
            return null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return "this host cannot create a file symbolic link without elevation (Developer Mode is off)";
        }
    }

    /// <summary>
    /// Why a folder cannot be made unlistable for the current user on this host, or null when it can. A privileged Unix process is ruled out at once; everywhere
    /// else a scratch folder is denied with <see cref="DenyListing"/> and listed the way the tasks loader lists a folder (<see cref="Directory.EnumerateFiles(string)"/>),
    /// and a listing that succeeds is the reason. The deny entry is not enough on every Windows account: the administrator account of the windows-latest CI
    /// runner listed such a folder (CI run 39660315559), while the same entry refuses the listing to a normal account.
    /// </summary>
    public static string? DenyListingSkipReason() => ListingProbe.Value;

    /// <summary>
    /// Why a folder cannot be made to refuse file creation to the current user on this host, or null when it can: the same rule as
    /// <see cref="DenyListingSkipReason"/>, probed with <see cref="DenyWriting"/> and a file created in the folder. It is a probe of its own because a host can
    /// honour one denial and pass over the other (the CI runner above refused the file creation).
    /// </summary>
    public static string? DenyWritingSkipReason() => WritingProbe.Value;

    private static string? ProbeListing() =>
        Probe(
            DenyListing,
            static folder => _ = Directory.EnumerateFiles(folder).Any(),
            "a privileged process lists a folder whatever its mode",
            "this account lists a folder whose listing is denied to it (the deny entry is not effective for its token)");

    private static string? ProbeWriting() =>
        Probe(
            DenyWriting,
            static folder => File.Create(Path.Combine(folder, "probe.tmp")).Dispose(),
            "a privileged process writes into a folder whatever its mode",
            "this account creates a file in a folder that denies file creation to it (the deny entry is not effective for its token)");

    // Applies the denial to a scratch folder and runs the access it should refuse: null when the access is refused, the reason when it is not or when the
    // denial cannot be applied at all.
    private static string? Probe(Func<string, IDisposable> deny, Action<string> access, string privilegedReason, string bypassedReason)
    {
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
        {
            return privilegedReason;
        }

        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string folder = Path.Combine(workspace.Root, "probe");
        _ = Directory.CreateDirectory(folder);
        try
        {
            using IDisposable denial = deny(folder);
            try
            {
                access(folder);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            return bypassedReason;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return "the access of a folder cannot be changed on this host (" + failure.GetType().Name + ")";
        }
    }

    /// <summary>Creates a directory link at <paramref name="link"/> that points at <paramref name="target"/>: a junction on Windows (no elevation needed), a symbolic link elsewhere.</summary>
    /// <param name="link">The path of the new link.</param>
    /// <param name="target">The existing directory it points at.</param>
    /// <returns>The handle that removes the link (not its target) again; dispose it before the workspace that holds the link, because a recursive delete does not always remove a junction.</returns>
    /// <exception cref="InvalidOperationException">The link could not be created.</exception>
    public static IDisposable CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateJunction(link, target);
        }
        else
        {
            _ = Directory.CreateSymbolicLink(link, target);
        }

        return new Restore(() => Directory.Delete(link));
    }

    /// <summary>Creates a hard link at <paramref name="link"/> to the existing file <paramref name="target"/> (<c>mklink /H</c> on Windows, <c>ln</c> elsewhere); both names then share one file.</summary>
    /// <param name="link">The path of the new name.</param>
    /// <param name="target">The existing file.</param>
    /// <exception cref="InvalidOperationException">The link could not be created.</exception>
    public static void CreateHardLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            RunTool("cmd.exe", "/c", "mklink", "/H", link, target);
        }
        else
        {
            RunTool("ln", target, link);
        }
    }

    private static void CreateJunction(string link, string target) => RunTool("cmd.exe", "/c", "mklink", "/J", link, target);

    private static void RunTool(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException(fileName + " did not start.");
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(fileName + " failed with exit code " + process.ExitCode);
        }
    }

    /// <summary>
    /// Makes listing <paramref name="folder"/> fail with <see cref="UnauthorizedAccessException"/> for the current user (a deny entry on Windows, mode 0
    /// elsewhere). Dispose the result before the folder is deleted: it restores the access.
    /// </summary>
    /// <param name="folder">An existing folder.</param>
    /// <returns>The handle that restores the access.</returns>
    public static IDisposable DenyListing(string folder)
    {
        return OperatingSystem.IsWindows() ? DenyOnWindows(folder) : DenyOnUnix(folder);
    }

    [UnsupportedOSPlatform("windows")]
    private static Restore DenyOnUnix(string folder)
    {
        UnixFileMode original = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.None);
        return new Restore(() => File.SetUnixFileMode(folder, original));
    }

    [SupportedOSPlatform("windows")]
    private static Restore DenyOnWindows(string folder)
    {
        var info = new DirectoryInfo(folder);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);

        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);

        return new Restore(() =>
        {
            DirectorySecurity current = info.GetAccessControl();
            _ = current.RemoveAccessRule(rule);
            info.SetAccessControl(current);
        });
    }

    /// <summary>
    /// Makes creating a file or a folder inside <paramref name="folder"/> fail with <see cref="UnauthorizedAccessException"/> for the current user (a deny
    /// entry for file and folder creation on Windows, mode r-x elsewhere); listing and reading stay allowed. A host where this does not hold (a privileged Unix
    /// process writes whatever the mode) is named by <see cref="DenyWritingSkipReason"/>. Dispose the result before the folder is deleted: it restores the access.
    /// </summary>
    /// <param name="folder">An existing folder.</param>
    /// <returns>The handle that restores the access.</returns>
    public static IDisposable DenyWriting(string folder)
    {
        return OperatingSystem.IsWindows() ? DenyWritingOnWindows(folder) : DenyWritingOnUnix(folder);
    }

    [UnsupportedOSPlatform("windows")]
    private static Restore DenyWritingOnUnix(string folder)
    {
        UnixFileMode original = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restore(() => File.SetUnixFileMode(folder, original));
    }

    [SupportedOSPlatform("windows")]
    private static Restore DenyWritingOnWindows(string folder)
    {
        var info = new DirectoryInfo(folder);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny);

        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);

        return new Restore(() =>
        {
            DirectorySecurity current = info.GetAccessControl();
            _ = current.RemoveAccessRule(rule);
            info.SetAccessControl(current);
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}