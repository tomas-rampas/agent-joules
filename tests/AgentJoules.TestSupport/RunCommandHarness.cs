using AgentJoules.Cli;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Nvml;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentJoules.TestSupport;

/// <summary>
/// What one run of the real <c>run</c> command under the harness gets: the in-memory configuration (fake keys and fake base URLs, never a process variable), a fake endpoint per
/// named tier client (a client without one falls through to the deny-all handler of the harness), the <see cref="FakeRunOrchestrator"/> in place of the measured run, an optional
/// scripted GPU behind the real NVML binding, and the working directory. <see cref="RunCommandHarness"/> turns it into <see cref="CliHostOverrides"/>.
/// </summary>
public sealed class RunCommandSetup
{
    private int _hostBuilds;

    /// <summary>The in-memory configuration; keys are root keys (<c>NVIDIA_API_KEY</c>) or full keys (<c>AgentJoules:Tiers:nvidia:BaseUrl</c>).</summary>
    public Dictionary<string, string?> Settings { get; } = new(StringComparer.Ordinal);

    /// <summary>The primary handler of each named tier client, by lowercase tier name.</summary>
    public Dictionary<string, HttpMessageHandler> Endpoints { get; } = new(StringComparer.Ordinal);

    /// <summary>The double that replaces the production orchestrator unless <see cref="UseProductionOrchestrator"/> is true.</summary>
    public FakeRunOrchestrator Orchestrator { get; } = new();

    /// <summary>True to keep the production orchestrator (the measured run of AJ-056) instead of <see cref="Orchestrator"/>.</summary>
    public bool UseProductionOrchestrator { get; set; }

    /// <summary>True to keep the production judge (the hosted judge of AJ-076, which reads the rubric and calls <c>Judge:Tier</c>) instead of the no-op judge the harness registers by default.</summary>
    public bool UseProductionJudge { get; set; }

    /// <summary>
    /// The <see cref="IPowerSourceDetector"/> every host of these overrides gets (AJ-106, EDGE-011): it answers <see cref="PowerSources.Unknown"/> unless a test sets <see cref="FakePowerSourceDetector.Value"/>, and never reads the host's
    /// power state, so a run under the harness does not depend on whether the machine is on its battery.
    /// </summary>
    public FakePowerSourceDetector PowerSource { get; } = new();

    /// <summary>The scripted GPU the real <see cref="NvmlGpuTelemetrySourceFactory"/> opens, or null for the harness default (no GPU is reachable).</summary>
    public ScriptedNvmlApi? Gpu { get; set; }

    /// <summary>More registrations, applied after the composition root and the command hook and before the endpoints, the GPU and the orchestrator double.</summary>
    public Action<IServiceCollection>? Services { get; set; }

    /// <summary>The working directory (content root) of the run; when null, <see cref="RunCommandHarness.RunAsync"/> provisions a fresh one with the configuration and the tasks.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// How many hosts were built with these overrides: the count goes up inside the build, when the services of the overrides are applied, so 0 proves that a
    /// parse error ended the command line before any host existed.
    /// </summary>
    public int HostBuilds => Volatile.Read(ref _hostBuilds);

    /// <summary>Creates a setup from the configuration pairs, the endpoints by lowercase tier name and an optional scripted GPU; a later pair with the same key wins.</summary>
    /// <param name="settings">The configuration override.</param>
    /// <param name="endpoints">The primary handler of each named tier client, or null for none.</param>
    /// <param name="gpu">The scripted GPU, or null for the harness default.</param>
    /// <returns>The setup.</returns>
    public static RunCommandSetup Create(
        IEnumerable<KeyValuePair<string, string?>> settings,
        IEnumerable<KeyValuePair<string, HttpMessageHandler>>? endpoints = null,
        ScriptedNvmlApi? gpu = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var setup = new RunCommandSetup { Gpu = gpu };
        foreach ((string key, string? value) in settings)
        {
            setup.Settings[key] = value;
        }

        foreach ((string tierName, HttpMessageHandler endpoint) in endpoints ?? [])
        {
            setup.Endpoints[tierName] = endpoint;
        }

        return setup;
    }

    /// <summary>Builds the overrides of one run in <paramref name="workingDirectory"/>.</summary>
    /// <param name="workingDirectory">The content root of the run.</param>
    /// <returns>The overrides; the endpoints are copied, so a later change to <see cref="Endpoints"/> does not reach a run already built.</returns>
    public CliHostOverrides ToOverrides(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        KeyValuePair<string, HttpMessageHandler>[] endpoints = [.. Endpoints];
        ScriptedNvmlApi? gpu = Gpu;
        Action<IServiceCollection>? extra = Services;
        bool production = UseProductionOrchestrator;
        bool productionJudge = UseProductionJudge;
        FakePowerSourceDetector powerSource = PowerSource;

        return new CliHostOverrides(
            Services: services =>
            {
                _ = Interlocked.Increment(ref _hostBuilds);

                // Before the test's own registrations, so a detector a test registers wins: the harness never reads the host's power state.
                _ = services.RemoveAll<IPowerSourceDetector>();
                _ = services.AddSingleton<IPowerSourceDetector>(powerSource);

                // Before the test's own registrations, so a judge a test registers wins; the production judge only when the setup asks for it.
                if (!productionJudge)
                {
                    _ = services.RemoveAll<IJudgeService>();
                    _ = services.AddSingleton<IJudgeService>(new NoOpJudgeService());
                }

                extra?.Invoke(services);
                foreach ((string tierName, HttpMessageHandler endpoint) in endpoints)
                {
                    _ = services.AddHttpClient(ProviderClientFactory.HttpClientName(tierName)).ConfigurePrimaryHttpMessageHandler(() => endpoint);
                }

                if (gpu is not null)
                {
                    _ = services.RemoveAll<IGpuTelemetrySourceFactory>();
                    _ = services.AddSingleton<IGpuTelemetrySourceFactory>(new NvmlGpuTelemetrySourceFactory(gpu));
                }

                if (!production)
                {
                    Orchestrator.Register(services);
                }
            },
            Configuration: new Dictionary<string, string?>(Settings, StringComparer.Ordinal),
            WorkingDirectory: workingDirectory);
    }
}

/// <summary>
/// The one place the tests and scenarios that run the real <c>run</c> command are wired (AJ-054): the configuration override, the fake endpoints, the orchestrator double, the scripted
/// GPU, and a working directory that holds a copy of the shipped <c>config/appsettings.json</c> and of the committed task files (<c>tasks/*.json</c>, copied, never linked), because
/// the command loads the task set from <c>Paths:Tasks</c> (default <c>tasks</c>) under the content root before any check.
/// </summary>
public static class RunCommandHarness
{
    /// <summary>The folder of the task files under the content root, the default of <c>Paths:Tasks</c>.</summary>
    public const string TasksFolder = "tasks";

    /// <summary>The full paths of the committed task files of the repository, in ordinal file-name order.</summary>
    /// <returns>The paths; never empty.</returns>
    public static string[] CommittedTaskFiles()
    {
        string[] files = [.. Directory.EnumerateFiles(Path.Combine(ConfigurationFixtures.RepositoryRoot(), TasksFolder), "*.json").Order(StringComparer.Ordinal)];
        return files.Length > 0 ? files : throw new InvalidOperationException("The repository holds no task files.");
    }

    /// <summary>Copies the committed task files into <c>&lt;root&gt;/&lt;folder&gt;</c>, creating the folder.</summary>
    /// <param name="root">The working directory of the run.</param>
    /// <param name="folder">The folder relative to <paramref name="root"/>; <see cref="TasksFolder"/> by default.</param>
    /// <returns>The full path of the folder.</returns>
    public static string ProvisionTasks(string root, string folder = TasksFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        string target = Path.Combine(root, folder);
        _ = Directory.CreateDirectory(target);
        foreach (string file in CommittedTaskFiles())
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        return target;
    }

    /// <summary>Creates a workspace with the shipped configuration and the committed tasks; the caller disposes it.</summary>
    /// <returns>The workspace.</returns>
    public static TempWorkspace CreateWorkspace()
    {
        TempWorkspace workspace = TempWorkspace.Create();
        _ = ProvisionTasks(workspace.Root);
        return workspace;
    }

    /// <summary>
    /// Runs <paramref name="arguments"/> (split like <see cref="CliHarness.RunAsync"/>) with the overrides of <paramref name="setup"/>. Without a
    /// <see cref="RunCommandSetup.WorkingDirectory"/> the run gets a fresh workspace from <see cref="CreateWorkspace"/>, deleted after the run.
    /// </summary>
    /// <param name="arguments">The command line, for example <c>run --tier all --dry-run</c>.</param>
    /// <param name="setup">The setup of the run.</param>
    /// <returns>The result of the run.</returns>
    public static async Task<CliRunResult> RunAsync(string arguments, RunCommandSetup setup)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(setup);

        using TempWorkspace? workspace = setup.WorkingDirectory is null ? CreateWorkspace() : null;
        return await CliHarness.RunAsync(arguments, setup.ToOverrides(setup.WorkingDirectory ?? workspace!.Root));
    }
}