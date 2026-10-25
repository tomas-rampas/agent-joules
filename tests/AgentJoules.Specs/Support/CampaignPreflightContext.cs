using AgentJoules.Core.Gpu;
using AgentJoules.Nvml;
using AgentJoules.TestSupport;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-110 scenario (run the preflight for the laptop measurement campaign, REQ-021, REQ-017), handed to the step class by Reqnroll context injection. The scenarios follow the AJ-101 pattern:
/// <see cref="Open"/> evaluates <see cref="IntegrationGate.SkipReason"/> for AGENTJOULES_INTEGRATION and reads (never sets, never prints) the key variables the scenario names; with the gate closed, or a key absent, the
/// remaining steps record the reason in <see cref="Note"/> and pass. With the gate open the scenarios about the saved probe assert the newest committed <c>results/probe-*.json</c> and never perform the builder's
/// action; the scenario about the dry run runs the real <c>run --tier all --dry-run</c> command through the harness with the committed configuration, the real key values passed in as in-memory configuration and a
/// live HTTP handler per tier (the one place a test opts in to the network, behind the gate); the two scenarios about the machine state (power source, other GPU applications) and the one about the sampling disclosure
/// are rules of the campaign, asserted against the <c>AJ-110</c> entry of <c>docs/build-log.md</c>. The scenario about a missing key needs neither the gate nor a key: it runs the real command through the harness with
/// an in-memory configuration and deny-all HTTP, so nothing leaves the process and no process variable is read.
/// </summary>
public sealed class CampaignPreflightContext
{
    /// <summary>The story whose entry of <c>docs/build-log.md</c> holds the campaign rules.</summary>
    public const string Story = "AJ-110";

    private static readonly string[] LiveTiers = ["local", "nvidia", "nebius"];

    private string? ClosedReason { get; set; }

    private string? RecordedNote { get; set; }

    private string[] _keyVariables = [];

    /// <summary>True when the gate is open and every named key variable has a value: the scenario asserts the committed record.</summary>
    public bool IsActive { get; private set; }

    /// <summary>True when <see cref="Open"/> found the gate open and every named key present, even after <see cref="Skip"/> switched the record-dependent steps off: the rules of the campaign are still asserted.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The note of a scenario that passed without asserting (gate closed, key absent, or the precondition of the scenario does not hold), or null.</summary>
    public string? Note => ClosedReason ?? RecordedNote;

    /// <summary>The result of the last command, or null when no command ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The text of standard output and standard error of the last command.</summary>
    /// <exception cref="XunitException">No command ran.</exception>
    public string Output => (Result ?? throw new XunitException("The command has not run yet.")).Stdout + "\n" + Result.Stderr;

    /// <summary>Evaluates the gate and reads the named key variables (a value is never copied into a message).</summary>
    /// <param name="keyVariables">The variables the scenario names; none for a scenario that needs no key.</param>
    public void Open(params string[] keyVariables)
    {
        _keyVariables = keyVariables;
        string? gate = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        string? missing = keyVariables.FirstOrDefault(static variable => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)));
        ClosedReason = gate ?? (missing is null ? null : missing + " is not set");
        IsActive = ClosedReason is null;
        IsOpen = IsActive;
    }

    /// <summary>Records why an open-gate scenario passed without asserting.</summary>
    /// <param name="note">The reason.</param>
    public void Skip(string note)
    {
        RecordedNote = note;
        IsActive = false;
    }

    /// <summary>
    /// Runs <c>run --tier all --dry-run</c> (or any <c>run</c> command line) through the harness with the committed configuration, the real key values of the named variables as in-memory configuration, the real GPU
    /// and a live HTTP handler per tier.
    /// </summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunLiveAsync(string arguments)
    {
        RunCommandSetup setup = RunCommandSetup.Create(
            _keyVariables.Select(static variable => new KeyValuePair<string, string?>(variable, Environment.GetEnvironmentVariable(variable))),
            LiveTiers.Select(static tier => KeyValuePair.Create<string, HttpMessageHandler>(tier, new LiveHttpHandler())));
        setup.Services = static services =>
        {
            _ = services.RemoveAll<IGpuTelemetrySourceFactory>();
            _ = services.AddSingleton<IGpuTelemetrySourceFactory>(new NvmlGpuTelemetrySourceFactory(new NvmlApi()));
        };
        Result = await RunCommandHarness.RunAsync(arguments, setup).WaitAsync(TimeSpan.FromMinutes(2));
        foreach (string variable in _keyVariables)
        {
            string key = Environment.GetEnvironmentVariable(variable) ?? string.Empty;
            Xunit.Assert.False(Output.Contains(key, StringComparison.Ordinal), variable + " appears in the output of the dry run; the text is not shown");
        }
    }

    /// <summary>Runs <paramref name="arguments"/> hermetically: the fake judge key is the only key, the configuration is in memory, and every HTTP request is refused.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunWithoutNebiusKeyAsync(string arguments)
    {
        RunCommandSetup setup = RunCommandSetup.Create([new KeyValuePair<string, string?>("NVIDIA_API_KEY", ProviderClientFixtures.FakeKey())]);
        Xunit.Assert.False(setup.Settings.ContainsKey("NEBIUS_API_KEY"));
        Result = await RunCommandHarness.RunAsync(arguments, setup).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>The <c>AJ-110</c> entry of the committed <c>docs/build-log.md</c>.</summary>
    /// <returns>The entry text.</returns>
    /// <exception cref="XunitException">The log has no such entry.</exception>
    public static string BuildLogEntry() =>
        LaptopResults.BuildLogEntry(Story) ?? throw new XunitException("docs/build-log.md has no \"### " + Story + "\" entry; the campaign rules are recorded there");

    /// <summary>
    /// A handler that reaches the network: the gated scenario's explicit opt-in. It wraps a socket handler built like the production primary handler of a tier (<c>new SocketsHttpHandler { AllowAutoRedirect = false }</c>,
    /// <c>AddAgentJoulesProviderClients</c>, the only setting production makes), so a redirect is never followed and a bearer key never travels to a redirect target. Being a wrapper, it is not a bare socket handler,
    /// which the harness's deny-all filter replaces, so the filter leaves it alone.
    /// </summary>
    private sealed class LiveHttpHandler : DelegatingHandler
    {
        public LiveHttpHandler()
            : base(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
        }
    }
}