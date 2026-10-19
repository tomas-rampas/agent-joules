using AgentJoules.Cli;

namespace AgentJoules.TestSupport;

/// <summary>
/// Runs the real CLI (real commands, real DI graph) in process with deny-by-default isolation (design 6.2): the harness never passes null
/// overrides, so no process environment variable, no user secret, no real GPU and no real HTTP handler is reachable unless a test opts in
/// through the overrides. Keys are runtime-built fakes set through <see cref="CliHostOverrides.Configuration"/>, never process variables.
/// </summary>
public static class CliHarness
{
    /// <summary>
    /// Runs <paramref name="arguments"/> (split on whitespace, double quotes group a token) and returns the exit code and the captured output.
    /// A run gets its own temporary working directory with a copy of the shipped <c>config/appsettings.json</c> unless
    /// <paramref name="overrides"/> names a <c>WorkingDirectory</c>; the five fake commands of <see cref="FakeCommands"/> are always registered.
    /// </summary>
    /// <param name="arguments">The command line without the program name, for example <c>probe --json</c>.</param>
    /// <param name="overrides">Test overrides; null means the default isolated overrides, never the production null.</param>
    /// <param name="stdout">The writer that receives standard output; a <see cref="StringWriter"/> when null.</param>
    /// <param name="stderr">The writer that receives standard error; a <see cref="StringWriter"/> when null.</param>
    /// <param name="cancellationToken">The token handed to <c>RunAsync</c> (the stand-in for <c>CancellationToken.None</c> of Program.cs).</param>
    public static Task<CliRunResult> RunAsync(
        string arguments,
        CliHostOverrides? overrides = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default) =>
        Start(arguments, overrides, stdout, stderr, cancellationToken).Result;

    /// <summary>Starts a run without waiting for it, for the tests that interact with it (the shutdown test raises <c>ApplicationStopping</c> while <c>fake-wait</c> waits).</summary>
    public static CliHarnessRun Start(
        string arguments,
        CliHostOverrides? overrides = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new CliHarnessRun(arguments, overrides, stdout, stderr, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="body"/> while no harness run is active and none can start, for a test that replaces <c>Console.Out</c> or
    /// <c>Console.Error</c> itself (they are process-wide); the test restores them before returning.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside another exclusive section (the gate is not re-entrant), which would otherwise deadlock.</exception>
    public static async Task<T> RunExclusiveAsync<T>(Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (CliHarnessRun.InExclusiveSection.Value)
        {
            throw new InvalidOperationException("RunExclusiveAsync is not re-entrant: it was called from inside another exclusive section");
        }

        await CliHarnessRun.Gate.WaitAsync();
        CliHarnessRun.InExclusiveSection.Value = true;
        try
        {
            return await body();
        }
        finally
        {
            CliHarnessRun.InExclusiveSection.Value = false;
            CliHarnessRun.Gate.Release();
        }
    }

    /// <summary>
    /// Builds overrides from in-memory configuration pairs (full keys such as <c>AgentJoules:Meter:SampleRateHz</c> or the root key
    /// <c>NVIDIA_API_KEY</c>) and, optionally, a GPU factory that throws <paramref name="gpuFactoryThrows"/> when opened.
    /// </summary>
    public static CliHostOverrides Overrides(
        IEnumerable<(string Key, string? Value)>? configuration = null,
        Exception? gpuFactoryThrows = null)
    {
        Dictionary<string, string?>? pairs = configuration?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        return new CliHostOverrides(
            Services: gpuFactoryThrows is null ? null : ThrowingGpuTelemetrySourceFactory.AsOverride(gpuFactoryThrows),
            Configuration: pairs);
    }
}