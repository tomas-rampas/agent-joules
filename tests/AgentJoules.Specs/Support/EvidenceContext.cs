using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using AgentJoules.Cli;
using AgentJoules.Core.Gpu;
using AgentJoules.Nvml;
using AgentJoules.TestSupport;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one M7 evidence scenario (AJ-150 to AJ-153), handed to the step class by Reqnroll context injection. A scenario about the committed tree reads the repository through <see cref="RepositoryRoot"/>
/// and changes nothing; a negative scenario lays a fixture out in a <see cref="TempWorkspace"/> (a copy of the committed files with one change) and applies the same <see cref="EvidenceChecks"/> rule to it, so no
/// scenario writes into the repository. The commands (<c>validate</c>, <c>report</c>) run in process through <see cref="CliHarness"/> with the folder as the working directory. The @integration scenarios call
/// <see cref="OpenGate"/> first: with <c>AGENTJOULES_INTEGRATION</c> closed they record the reason in <see cref="SkipNote"/> and the remaining steps pass without asserting; no key value is ever printed.
/// </summary>
public sealed class EvidenceContext : IDisposable
{
    private TempWorkspace? _workspace;

    /// <summary>The folder the rules read: the repository or the fixture.</summary>
    public string Root { get; set; } = RepositoryRoot.Path;

    /// <summary>The rule the When step applies; set by the Given step that builds the subject.</summary>
    public Func<IReadOnlyList<string>>? Check { get; set; }

    /// <summary>The violations the last rule reported, or null before it ran.</summary>
    public IReadOnlyList<string>? Result { get; set; }

    /// <summary>The boxes the last documentation rule left unchecked with their reason.</summary>
    public IReadOnlyList<string> UncheckedBoxes { get; set; } = [];

    /// <summary>The preset the cloudgpu run-file rule recorded, or null.</summary>
    public string? Preset { get; set; }

    /// <summary>The generated files that differ from the committed ones after the first run, by relative path.</summary>
    public IReadOnlyList<string> CurrentnessViolations { get; set; } = [];

    /// <summary>The result of the last command, or null.</summary>
    public CliRunResult? Command { get; private set; }

    /// <summary>The hash of every file of the workspace after the first run, by relative path.</summary>
    public IReadOnlyDictionary<string, string> FirstHashes { get; set; } = new Dictionary<string, string>();

    /// <summary>The hash of every file of the workspace after the second run, by relative path.</summary>
    public IReadOnlyDictionary<string, string> SecondHashes { get; set; } = new Dictionary<string, string>();

    /// <summary>The reason a gated scenario passes without asserting, or null when it asserts.</summary>
    public string? SkipNote { get; private set; }

    /// <summary>The value of the variable a key scan compares with; held only to compare, never printed.</summary>
    public string? KeyValue { get; set; }

    /// <summary>The fake values seeded for the three key variables of an in-process run (built at run time, never the real ones), by variable name; a scenario that checks the output for a key looks for these.</summary>
    public IReadOnlyDictionary<string, string> FakeKeys { get; } = CliChildProcess.KeyVariables.ToDictionary(
        static variable => variable,
        static variable => ProviderClientFixtures.FakeKey() + "-" + variable.ToLowerInvariant(),
        StringComparer.Ordinal);

    /// <summary>The text a scan or a parse works on.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The violations of the last rule.</summary>
    /// <exception cref="XunitException">No rule has run.</exception>
    public IReadOnlyList<string> Violations => Result ?? throw new XunitException("The rule has not run yet.");

    /// <summary>Reads the committed repository.</summary>
    public void UseRepository() => Root = RepositoryRoot.Path;

    /// <summary>Starts a fresh fixture folder (the previous one is deleted) and makes it the folder the rules read.</summary>
    /// <returns>The workspace.</returns>
    public TempWorkspace NewFixture()
    {
        _workspace?.Dispose();
        _workspace = TempWorkspace.CreateEmpty();
        Root = _workspace.Root;
        return _workspace;
    }

    /// <summary>Copies the newest committed run file of each tier into <c>results/</c> of the fixture.</summary>
    /// <param name="workspace">The fixture.</param>
    /// <param name="tiers">The tiers; the three published tiers when empty.</param>
    public static void CopyNewestRunFiles(TempWorkspace workspace, params string[] tiers)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        foreach (string tier in tiers.Length == 0 ? [.. EvidenceChecks.PublishedTiers] : tiers)
        {
            string name = EvidenceChecks.NewestRunFileName(RepositoryRoot.Path, tier) ?? throw new XunitException("results/ holds no run file for tier " + tier);
            _ = workspace.WriteFile(Path.Combine("results", name), RepositoryRoot.ReadAllText("results/" + name));
        }
    }

    /// <summary>Copies a committed file to the same relative path of the fixture.</summary>
    /// <param name="workspace">The fixture.</param>
    /// <param name="relativePath">The repository-relative path.</param>
    public static void CopyCommitted(TempWorkspace workspace, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _ = workspace.WriteFile(relativePath, RepositoryRoot.ReadAllText(relativePath));
    }

    /// <summary>Writes a copy of the newest committed run file of a tier into the fixture after an edit of its tree.</summary>
    /// <param name="workspace">The fixture.</param>
    /// <param name="tier">The tier.</param>
    /// <param name="edit">The edit of the root object.</param>
    public static void WriteEditedRunFile(TempWorkspace workspace, string tier, Action<JsonObject> edit)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        string name = EvidenceChecks.NewestRunFileName(RepositoryRoot.Path, tier) ?? throw new XunitException("results/ holds no run file for tier " + tier);
        _ = workspace.WriteFile(Path.Combine("results", name), RunDocumentFixtures.Edit(RepositoryRoot.ReadAllText("results/" + name), edit) + "\n");
    }

    /// <summary>Runs a command line in process with the folder as the working directory.</summary>
    /// <param name="tokens">The arguments without the program name.</param>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunCommandAsync(params string[] tokens) =>
        Command = await CliHarness.RunTokensAsync(tokens, new CliHostOverrides(WorkingDirectory: Root)).WaitAsync(TimeSpan.FromMinutes(2));

    /// <summary>The SHA-256 of every file below the fixture, by relative path with forward slashes.</summary>
    /// <returns>The hashes.</returns>
    public IReadOnlyDictionary<string, string> HashTree() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(Root, path).Replace('\\', '/'), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    /// <summary>Evaluates the integration gate; with it closed the reason is kept in <see cref="SkipNote"/>.</summary>
    /// <param name="keyVariable">A variable that must be set when the gate is open (its value is read, never copied into a message), or null.</param>
    public void OpenGate(string? keyVariable = null)
    {
        SkipNote = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        if (SkipNote is null && keyVariable is not null)
        {
            KeyValue = Environment.GetEnvironmentVariable(keyVariable);
            if (string.IsNullOrWhiteSpace(KeyValue))
            {
                throw new XunitException(keyVariable + " is not set");
            }
        }
    }

    /// <summary>
    /// Runs <c>run --tier local --dashboard --dry-run</c> through the harness with the real GPU and a live HTTP handler for the local tier (the gated scenario's explicit opt-in). A base URL replaces the endpoint of the
    /// local tier; fake keys and a fake judge endpoint are seeded and the dashboard port is a free one.
    /// </summary>
    /// <param name="baseUrl">The endpoint to use instead of the configured one, or null.</param>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunLocalDryRunAsync(string? baseUrl)
    {
        RunCommandSetup setup = RunCommandSetup.Create([]);
        foreach (string variable in CliChildProcess.KeyVariables)
        {
            setup.Settings[variable] = FakeKeys[variable];
        }

        // A dry run requires the judge key and reaches the judge endpoint; the in-process harness leaves out the process environment, so both are seeded (fake key, fake endpoint).
        setup.Endpoints["nvidia"] = RunOrchestrationFixtures.Endpoint();
        if (baseUrl is not null)
        {
            setup.Settings["AgentJoules:Tiers:local:BaseUrl"] = baseUrl;
        }

        setup.Endpoints["local"] = new LiveHttpHandler();
        setup.Services = static services =>
        {
            _ = services.RemoveAll<IGpuTelemetrySourceFactory>();
            _ = services.AddSingleton<IGpuTelemetrySourceFactory>(new NvmlGpuTelemetrySourceFactory(new NvmlApi()));
        };
        int port;
        using (LoopbackPortBlock free = LoopbackPortBlock.Reserve(1))
        {
            port = free.First;
        }

        Command = await RunCommandHarness.RunAsync(string.Create(CultureInfo.InvariantCulture, $"run --tier local --dashboard --dashboard-port {port} --dry-run"), setup).WaitAsync(TimeSpan.FromMinutes(2));
    }

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();

    // A wrapper around a socket handler, so the deny-all filter of the harness leaves it alone; redirects are never followed.
    private sealed class LiveHttpHandler : DelegatingHandler
    {
        public LiveHttpHandler()
            : base(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
        }
    }
}