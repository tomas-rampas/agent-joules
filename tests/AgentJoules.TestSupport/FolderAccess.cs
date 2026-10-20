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

    /// <summary>Why a folder cannot be made unlistable for the current user on this host, or null when it can.</summary>
    public static string? DenyListingSkipReason() =>
        !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess ? "a privileged process lists a folder whatever its mode" : null;

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

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start.");
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("mklink /J failed with exit code " + process.ExitCode);
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

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}