using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-054 scenario (<c>run --dry-run</c> validates everything and makes no completion call), handed to the step class by Reqnroll context injection. It holds the
/// <see cref="RunCommandSetup"/> of the run (fake keys and fake base URLs in the configuration override, a recording fake endpoint per tier, the orchestrator double, and a scripted GPU
/// when the scenario gives one) and the result. The scenarios run the real <c>run</c> command through <see cref="RunCommandHarness"/>, the plumbing the AJ-052 and AJ-053 scenarios share.
/// </summary>
public sealed class DryRunContext
{
    private readonly Dictionary<string, FakeHttpMessageHandler> _handlers = new(StringComparer.Ordinal);

    /// <summary>The setup of the run: both hosted tiers point at fake base URLs; the keys, the endpoints and the GPU are added by the Given steps.</summary>
    public RunCommandSetup Setup { get; } = CreateSetup();

    /// <summary>The recording fake endpoints by lowercase tier name.</summary>
    public IReadOnlyDictionary<string, FakeHttpMessageHandler> Handlers => _handlers;

    /// <summary>The result of the harness run, or null before it ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>Puts a fake key under <c>NVIDIA_API_KEY</c> and <c>NEBIUS_API_KEY</c>; nvidia is also the judge tier, so a dry run of any tier passes the key check.</summary>
    public void SetKeys()
    {
        Setup.Settings[ProviderClientFixtures.NvidiaKeyVariable] = ProviderClientFixtures.FakeKey();
        Setup.Settings[ProviderClientFixtures.NebiusKeyVariable] = ProviderClientFixtures.FakeKey();
    }

    /// <summary>Routes the named client of <paramref name="tierName"/> to a recording fake endpoint whose models route lists every model ID of the shipped configuration.</summary>
    /// <param name="tierName">The lowercase tier name.</param>
    public void RouteToFake(string tierName)
    {
        var handler = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };
        _handlers[tierName] = handler;
        Setup.Endpoints[tierName] = handler;
    }

    /// <summary>Puts a scripted GPU (energy counter supported, MIG disabled) behind the real NVML binding.</summary>
    public void UseScriptedGpu() => Setup.Gpu = new ScriptedNvmlApi(new FakeTimeProvider());

    /// <summary>The base URL the run uses for <paramref name="tierName"/>: the fake one of the override for a hosted tier, the committed one otherwise.</summary>
    /// <param name="tierName">The lowercase tier name.</param>
    /// <returns>The base URL.</returns>
    public string BaseUrlOf(string tierName) =>
        Setup.Settings.TryGetValue("AgentJoules:Tiers:" + tierName + ":BaseUrl", out string? configured) && configured is not null
            ? configured
            : ConfigurationFixtures.ShippedOptions().Tiers[tierName].BaseUrl ?? throw new InvalidOperationException("The shipped configuration holds no base URL for " + tierName);

    /// <summary>Runs <paramref name="arguments"/> through the real <c>run</c> command with the setup of the scenario.</summary>
    /// <param name="arguments">The command line, for example <c>run --tier all --dry-run</c>.</param>
    /// <returns>A task that completes when the command ended and <see cref="Result"/> is set.</returns>
    public async Task RunAsync(string arguments)
    {
        Arguments = arguments;
        Result = await RunCommandHarness.RunAsync(arguments, Setup);
    }

    /// <summary>The command line of the run, or null before it ran.</summary>
    public string? Arguments { get; private set; }

    private static RunCommandSetup CreateSetup()
    {
        var setup = new RunCommandSetup();
        setup.Settings["AgentJoules:Tiers:nvidia:BaseUrl"] = ProviderClientFixtures.BaseUrl(Tier.Nvidia);
        setup.Settings["AgentJoules:Tiers:nebius:BaseUrl"] = ProviderClientFixtures.BaseUrl(Tier.Nebius);
        return setup;
    }

    /// <summary>The configured model ID of <paramref name="tierName"/> in the shipped configuration.</summary>
    /// <param name="tierName">The lowercase tier name.</param>
    /// <returns>The model ID.</returns>
    public static string ShippedModelId(string tierName) =>
        ConfigurationFixtures.ShippedOptions().Tiers[tierName].ModelId ?? throw new InvalidOperationException("The shipped configuration holds no model ID for " + tierName);

    /// <summary>The lowercase names of the tiers <c>--tier all</c> selects in the shipped configuration, in the order of a run.</summary>
    /// <returns>The tier names.</returns>
    public static string[] ShippedEnabledTiers()
    {
        AgentJoulesOptions shipped = ConfigurationFixtures.ShippedOptions();
        return [.. TierOptions.KnownNames.Where(name => shipped.Tiers.TryGetValue(name, out TierOptions? tier) && tier.Enabled)];
    }
}