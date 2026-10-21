using AgentJoules.Cli;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-058 scenario (map every failure to the documented exit code), handed to the step class by Reqnroll context injection. A failure class is prepared by a Given step as a
/// pending command (the real application under the harness: <c>probe</c> with a throwing service override, or the real <c>run</c> command with fake keys, fake endpoints and, for a measured
/// tier, a scripted GPU); the When step runs it and the Then steps read the result. Scenario 1 uses the mapper alone: <see cref="Exceptions"/> and <see cref="MappedCodes"/>.
/// </summary>
public sealed class ExitCodesContext
{
    private Func<Task<CliRunResult>>? _pending;

    /// <summary>The result of the run, or null before it ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The recording fake endpoint of the nvidia tier, or null when the scenario routes none.</summary>
    public FakeHttpMessageHandler? Nvidia { get; private set; }

    /// <summary>The NVIDIA key the scenario put in the configuration (the runtime-built key of the redaction scenario), or null.</summary>
    public string? Key { get; private set; }

    /// <summary>The exceptions that scenario 1 builds from their type names, in the order given.</summary>
    public List<Exception> Exceptions { get; } = [];

    /// <summary>The codes the mapper returned for <see cref="Exceptions"/>, in the same order.</summary>
    public List<int> MappedCodes { get; } = [];

    /// <summary>The setup of the <c>run</c> command: both hosted tiers point at fake base URLs; keys, endpoints and the GPU are added by the Given steps.</summary>
    public RunCommandSetup Setup { get; } = CreateSetup();

    /// <summary>Prepares <c>probe</c> with a GPU factory that throws <paramref name="failure"/>.</summary>
    /// <param name="failure">The exception the service override throws.</param>
    public void PrepareProbeThrowing(Exception failure)
    {
        CliHostOverrides overrides = CliHarness.Overrides(gpuFactoryThrows: failure);
        _pending = () => CliHarness.RunAsync("probe", overrides);
    }

    /// <summary>Prepares the real <c>run</c> command with <paramref name="arguments"/> and the setup of the scenario.</summary>
    /// <param name="arguments">The command line, for example <c>run --tier nvidia --dry-run</c>.</param>
    public void PrepareRun(string arguments)
    {
        _pending = () => RunCommandHarness.RunAsync(arguments, Setup);
    }

    /// <summary>Puts a fake key under <c>NVIDIA_API_KEY</c>; nvidia is also the judge tier, so a dry run of any tier passes the key check of the judge.</summary>
    /// <param name="key">The key; a fake one when null.</param>
    public void SetNvidiaKey(string? key = null)
    {
        Key = key ?? ProviderClientFixtures.FakeKey();
        Setup.Settings[ProviderClientFixtures.NvidiaKeyVariable] = Key;
    }

    /// <summary>Routes the nvidia client to <paramref name="handler"/>.</summary>
    /// <param name="handler">The recording fake endpoint.</param>
    public void RouteNvidia(FakeHttpMessageHandler handler)
    {
        Nvidia = handler;
        Setup.Endpoints["nvidia"] = handler;
    }

    /// <summary>Routes the local client to a fake endpoint that lists every shipped model ID, and puts a scripted GPU without the NVML library behind the real binding.</summary>
    public void RouteLocalWithoutNvml()
    {
        Setup.Endpoints["local"] = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };
        Setup.Gpu = new ScriptedNvmlApi(new FakeTimeProvider()) { MissingLibrary = true };
    }

    /// <summary>Runs the prepared command to its end.</summary>
    /// <returns>A task that completes when <see cref="Result"/> is set.</returns>
    public async Task RunAsync()
    {
        Func<Task<CliRunResult>> pending = _pending ?? throw new InvalidOperationException("No command was prepared by a Given step.");
        Result = await pending();
    }

    private static RunCommandSetup CreateSetup()
    {
        var setup = new RunCommandSetup();
        setup.Settings["AgentJoules:Tiers:nvidia:BaseUrl"] = ProviderClientFixtures.BaseUrl(Tier.Nvidia);
        setup.Settings["AgentJoules:Tiers:nebius:BaseUrl"] = ProviderClientFixtures.BaseUrl(Tier.Nebius);
        return setup;
    }
}