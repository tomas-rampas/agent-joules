using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;

using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>
/// Starts the built <c>AgentJoules.Cli.dll</c> as a child process (<c>dotnet AgentJoules.Cli.dll ...</c>), for what an in-process harness cannot
/// show: an exception that escapes a worker thread reaches the <c>AppDomain.UnhandledException</c> handler of a real process, and the production
/// configuration path (environment variables as a source) can be proven with a witness variable instead of the laptop's own environment.
/// The wait is bounded and uses the wall clock, which is allowed for a child-process test.
/// </summary>
public static class CliChildProcess
{
    /// <summary>The hidden root option that makes a worker thread throw <c>InvalidOperationException(message)</c>.</summary>
    public const string WorkerThrowOption = "--debug-throw-on-worker";

    private static readonly string[] KeyVariables = ["NVIDIA_API_KEY", "NEBIUS_API_KEY", "NGC_API_KEY"];
    private static readonly string[] RuntimeVariables = ["PATH", "SystemRoot", "windir", "TEMP", "TMP", "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)"];

    /// <summary>The path of the Cli assembly beside the test assembly.</summary>
    /// <exception cref="Xunit.Sdk.TrueException">The assembly or its runtimeconfig file is missing; the message names the path.</exception>
    public static string CliAssemblyPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "AgentJoules.Cli.dll");
        string runtimeConfig = Path.ChangeExtension(path, ".runtimeconfig.json");
        Assert.True(File.Exists(path), "the Cli assembly was not found beside the test assembly: " + path);
        Assert.True(File.Exists(runtimeConfig), "the Cli runtimeconfig file was not found beside the test assembly: " + runtimeConfig);
        return path;
    }

    /// <summary>
    /// Runs the Cli with <paramref name="arguments"/> in a temporary working directory that holds a copy of the shipped
    /// <c>config/appsettings.json</c> and returns its exit code and both output streams. Every child loses the three key variables and gets
    /// <c>APPDATA</c>, <c>HOME</c> and <c>USERPROFILE</c> pointed at the workspace, so no real key or user secret can reach it.
    /// </summary>
    /// <param name="arguments">The command line without the program name.</param>
    /// <param name="timeout">The bound of the wait (wall clock).</param>
    /// <param name="scratchEnvironment">
    /// When not null, the environment block is built from scratch: only what the runtime needs to start (<c>PATH</c>, <c>DOTNET_*</c> and the
    /// Windows system variables) plus these pairs, so a witness variable proves a source without the laptop's variables in the way.
    /// </param>
    /// <exception cref="TimeoutException">The process did not exit within <paramref name="timeout"/>; it was killed.</exception>
    public static async Task<CliRunResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? scratchEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string cliPath = CliAssemblyPath();
        using TempWorkspace workspace = TempWorkspace.Create();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        ConfigureEnvironment(startInfo, workspace.Root, scratchEnvironment);
        startInfo.ArgumentList.Add(cliPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "the child process did not start: dotnet " + cliPath);

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("the Cli child process did not exit within " + timeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s: " + cliPath);
        }

        return new CliRunResult(process.ExitCode, await stdout, await stderr);
    }

    private static void ConfigureEnvironment(ProcessStartInfo startInfo, string workspaceRoot, IReadOnlyDictionary<string, string>? scratchEnvironment)
    {
        IDictionary<string, string?> environment = startInfo.Environment;
        if (scratchEnvironment is not null)
        {
            var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in RuntimeVariables)
            {
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                {
                    kept[name] = value;
                }
            }

            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) && entry.Value is string value)
                {
                    kept[key] = value;
                }
            }

            environment.Clear();
            foreach (KeyValuePair<string, string> pair in kept)
            {
                environment[pair.Key] = pair.Value;
            }

            foreach (KeyValuePair<string, string> pair in scratchEnvironment)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        foreach (string variable in KeyVariables)
        {
            environment.Remove(variable);
        }

        // User secrets live under APPDATA (Windows) or HOME (elsewhere); neither may reach the laptop's own store.
        environment["APPDATA"] = workspaceRoot;
        environment["HOME"] = workspaceRoot;
        environment["USERPROFILE"] = workspaceRoot;
    }
}