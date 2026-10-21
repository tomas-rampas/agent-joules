using System.Text.Json;

using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentJoules.TestSupport;

/// <summary>
/// The plumbing of the AJ-056 tests and scenarios that run the real <c>run</c> command with the production orchestrator: a setup over the hosted tiers (both keys, the fake base URLs and a
/// fake endpoint per tier), the registration of a fake clock as the host's clock (the measured tier waits on it for its idle baseline and samples on it), a run driven on that clock, and the
/// run files the command wrote, read back as JSON.
/// </summary>
public static class RunOrchestrationFixtures
{
    /// <summary>The folder of the run files under the content root, the default of <c>--out</c>.</summary>
    public const string ResultsFolder = "results";

    /// <summary>The instant the fake clock of a test starts at, so a run file name and <c>run.startUtc</c> are known in advance.</summary>
    public static readonly DateTimeOffset CommandStartUtc = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The sampling period of the shipped configuration (<c>Meter:SampleRateHz</c> 10): the step a driven run advances the clock by.</summary>
    public static readonly TimeSpan SamplingPeriod = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A setup that runs the production orchestrator in <paramref name="workingDirectory"/> with a fake key under <c>NVIDIA_API_KEY</c> and <c>NEBIUS_API_KEY</c>, the fake base URLs of the two
    /// hosted tiers, and <paramref name="endpoints"/> as the primary handlers of the named tier clients; the extra pairs change the configuration.
    /// </summary>
    /// <param name="workingDirectory">The content root of the run (a workspace from <see cref="RunCommandHarness.CreateWorkspace"/>).</param>
    /// <param name="endpoints">The fake endpoint of each tier the run reaches, by lowercase tier name.</param>
    /// <param name="extra">More configuration pairs; a later pair with the same key wins.</param>
    /// <returns>The setup.</returns>
    public static RunCommandSetup Production(string workingDirectory, IReadOnlyDictionary<string, HttpMessageHandler> endpoints, params (string Key, string? Value)[] extra)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(endpoints);

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ProviderClientFixtures.NvidiaKeyVariable] = ProviderClientFixtures.FakeKey(),
            [ProviderClientFixtures.NebiusKeyVariable] = ProviderClientFixtures.FakeKey(),
            ["AgentJoules:Tiers:nvidia:BaseUrl"] = ProviderClientFixtures.BaseUrl(Tier.Nvidia),
            ["AgentJoules:Tiers:nebius:BaseUrl"] = ProviderClientFixtures.BaseUrl(Tier.Nebius),
        };
        foreach ((string key, string? value) in extra)
        {
            settings[key] = value;
        }

        RunCommandSetup setup = RunCommandSetup.Create(settings, endpoints);
        setup.UseProductionOrchestrator = true;
        setup.WorkingDirectory = workingDirectory;
        return setup;
    }

    /// <summary>A fake endpoint whose models route lists every model ID of the shipped configuration; the clock and the duration of a call are optional.</summary>
    /// <param name="clock">The fake clock a call moves, or null for none.</param>
    /// <param name="callDuration">How long every call takes on <paramref name="clock"/>; zero by default.</param>
    /// <returns>The endpoint.</returns>
    public static FakeHttpMessageHandler Endpoint(Microsoft.Extensions.Time.Testing.FakeTimeProvider? clock = null, TimeSpan callDuration = default) =>
        new() { ModelIds = ConfigurationFixtures.ShippedModelIds(), Clock = clock, DefaultDuration = callDuration };

    /// <summary>Adds <paramref name="registrations"/> to the services of <paramref name="setup"/>, after the ones already there.</summary>
    /// <param name="setup">The setup.</param>
    /// <param name="registrations">The registrations to add.</param>
    public static void AddServices(RunCommandSetup setup, Action<IServiceCollection> registrations)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(registrations);

        Action<IServiceCollection>? previous = setup.Services;
        setup.Services = services =>
        {
            previous?.Invoke(services);
            registrations(services);
        };
    }

    /// <summary>Makes <paramref name="time"/> the clock of the host: the run file name, the meter, the windows, the latencies and the waits all read it.</summary>
    /// <param name="setup">The setup.</param>
    /// <param name="time">The clock.</param>
    public static void UseClock(RunCommandSetup setup, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        AddServices(setup, services =>
        {
            _ = services.RemoveAll<TimeProvider>();
            _ = services.AddSingleton(time);
        });
    }

    /// <summary>
    /// A fake clock at <see cref="CommandStartUtc"/> that reports every timer created on it, for a run driven by <see cref="RunDrivenAsync(string, RunCommandSetup, TimerTrackingTimeProvider)"/>; create the scripted GPU after it, so the
    /// GPU's script starts at the same instant.
    /// </summary>
    /// <returns>The clock.</returns>
    public static TimerTrackingTimeProvider Clock()
    {
        var time = new TimerTrackingTimeProvider();
        time.SetUtcNow(CommandStartUtc);
        return time;
    }

    /// <summary>
    /// Runs <paramref name="arguments"/> with <paramref name="setup"/> while the clock moves only when the command waits on it, one sampling period at a time
    /// (<see cref="FakeTimeDriver.RunWaitsToCompletionAsync"/>), so the idle baseline of a measured tier takes no wall-clock time and its samples carry their own instants.
    /// </summary>
    /// <param name="arguments">The command line.</param>
    /// <param name="setup">The setup; its clock must be <paramref name="time"/> (<see cref="UseClock"/>).</param>
    /// <param name="time">The clock.</param>
    /// <returns>The result of the run.</returns>
    public static async Task<CliRunResult> RunDrivenAsync(string arguments, RunCommandSetup setup, TimerTrackingTimeProvider time)
    {
        Task<CliRunResult> run = RunCommandHarness.RunAsync(arguments, setup);
        await FakeTimeDriver.RunWaitsToCompletionAsync(run, time, maxStep: SamplingPeriod);
        return await run;
    }

    /// <summary>
    /// <see cref="RunDrivenAsync(string, RunCommandSetup, TimerTrackingTimeProvider)"/> with a command token the test cancels (what Ctrl+C and SIGTERM do); the setup must name its
    /// working directory.
    /// </summary>
    /// <param name="arguments">The command line.</param>
    /// <param name="setup">The setup; its clock must be <paramref name="time"/> and its working directory set.</param>
    /// <param name="time">The clock.</param>
    /// <param name="cancellationToken">The command token.</param>
    /// <param name="beforeStep">Awaited before every step of the clock (<see cref="FakeTimeDriver.RunWaitsToCompletionAsync"/>), or null for none.</param>
    /// <returns>The result of the run.</returns>
    public static async Task<CliRunResult> RunDrivenAsync(
        string arguments,
        RunCommandSetup setup,
        TimerTrackingTimeProvider time,
        CancellationToken cancellationToken,
        Func<Task>? beforeStep = null)
    {
        ArgumentNullException.ThrowIfNull(setup);

        string root = setup.WorkingDirectory ?? throw new ArgumentException("The setup names no working directory.", nameof(setup));
        Task<CliRunResult> run = CliHarness.Start(arguments, setup.ToOverrides(root), cancellationToken: cancellationToken).Result;
        await FakeTimeDriver.RunWaitsToCompletionAsync(run, time, maxStep: SamplingPeriod, beforeStep: beforeStep);
        return await run;
    }

    /// <summary>The run files in <c>&lt;root&gt;/&lt;folder&gt;</c> (names that match <see cref="RunFileName.Pattern"/>), in ordinal order; empty when the folder does not exist.</summary>
    /// <param name="root">The content root of the run.</param>
    /// <param name="folder">The folder relative to <paramref name="root"/>.</param>
    /// <returns>The full paths.</returns>
    public static string[] RunFilesIn(string root, string folder = ResultsFolder)
    {
        string directory = Path.Combine(root, folder);
        return Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory).Where(static path => RunFileName.IsValid(Path.GetFileName(path))).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>Every entry of <c>&lt;root&gt;/&lt;folder&gt;</c> (files of any name), for the assertion that nothing else, no partial file included, was left behind.</summary>
    /// <param name="root">The content root of the run.</param>
    /// <param name="folder">The folder relative to <paramref name="root"/>.</param>
    /// <returns>The file names in ordinal order; empty when the folder does not exist.</returns>
    public static string[] EntriesIn(string root, string folder = ResultsFolder)
    {
        string directory = Path.Combine(root, folder);
        return Directory.Exists(directory)
            ? [.. Directory.EnumerateFileSystemEntries(directory).Select(static path => Path.GetFileName(path)).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>Parses a run file; the caller disposes the document.</summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The document.</returns>
    public static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));

    /// <summary>The <c>steps</c> of a run document, in file order.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The step elements.</returns>
    public static JsonElement[] Steps(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [.. document.RootElement.GetProperty("steps").EnumerateArray()];
    }

    /// <summary>The lines of standard output without terminators and without empty lines.</summary>
    /// <param name="result">The result of the run.</param>
    /// <returns>The lines.</returns>
    public static string[] StdoutLines(CliRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return [.. result.Stdout.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0)];
    }
}