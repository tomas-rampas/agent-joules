using System.Diagnostics;
using System.Globalization;

using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>The outcome of one git call.</summary>
/// <param name="ExitCode">The exit code of git.</param>
/// <param name="Stdout">What git wrote to standard output.</param>
/// <param name="Stderr">What git wrote to standard error.</param>
public sealed record GitResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// The one way the tests start git: bounded to 30 s (the process tree is killed on a timeout), no shell (<see cref="ProcessStartInfo.ArgumentList"/>), standard input closed, and every inherited <c>GIT_*</c> variable removed
/// so an inherited <c>GIT_DIR</c>, <c>GIT_WORK_TREE</c>, <c>GIT_INDEX_FILE</c> or <c>GIT_OBJECT_DIRECTORY</c> cannot point a call at another repository. Nothing here reads or prints a key.
/// </summary>
public static class GitRunner
{
    /// <summary>The time limit of one call.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>Runs git.</summary>
    /// <param name="workingDirectory">The working directory of the process.</param>
    /// <param name="args">The arguments, one per element.</param>
    /// <returns>The exit code and both streams.</returns>
    /// <exception cref="InvalidOperationException">git is not installed (the inner exception is the start failure), or it did not exit within <see cref="Limit"/>.</exception>
    public static GitResult Run(string workingDirectory, params string[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(args);

        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string name in info.Environment.Keys.Where(static key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _ = info.Environment.Remove(name);
        }

        foreach (string argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("git did not start");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("git is not installed or not on PATH", exception);
        }

        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)Limit.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("git " + (args.Length > 0 ? args[0] : string.Empty) + " did not exit within " + Limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
        }

        return new GitResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    /// <summary>True when git starts and <c>git --version</c> exits 0.</summary>
    /// <returns>False when git is not on PATH.</returns>
    public static bool Available()
    {
        try
        {
            return Run(Directory.GetCurrentDirectory(), "--version").ExitCode == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>A fact that is skipped, with its reason, when git is not on PATH.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GitFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute; <see cref="FactAttribute.Skip"/> is set when <c>git --version</c> cannot run.</summary>
    public GitFactAttribute()
    {
        if (!GitRunner.Available())
        {
            Skip = "git is not on PATH, so a scratch repository cannot be created";
        }
    }
}