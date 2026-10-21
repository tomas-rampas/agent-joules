using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-053 scenario (exit 4 before any completion call when a model ID is not listed), handed to the step class by Reqnroll context injection. It holds the
/// configuration override of the run (fake keys for both hosted tiers and fake base URLs, never a process variable), the recording fake endpoint of each hosted tier with the model IDs
/// its models route lists, and the result. The commands run through <see cref="StandInRunner"/>, so the real preflight (key check, then the real model check from the started host),
/// the real provider pipeline and the real redacting writers are measured; only the <c>run</c> command around them is the stand-in until AJ-054.
/// </summary>
public sealed class ModelCheckContext
{
    private const string Rewire = "AJ-053 scenarios 1 to 5";

    private readonly Dictionary<string, FakeHttpMessageHandler> _endpoints = new(StringComparer.Ordinal);

    /// <summary>The configuration override of the run: both fake keys (so a key check never decides a scenario) and the fake base URLs of the hosted tiers.</summary>
    public Dictionary<string, string?> Settings { get; } = new(StringComparer.Ordinal)
    {
        ["NVIDIA_API_KEY"] = ProviderClientFixtures.FakeKey(),
        ["NEBIUS_API_KEY"] = ProviderClientFixtures.FakeKey(),
        ["AgentJoules:Tiers:nvidia:BaseUrl"] = ProviderClientFixtures.BaseUrl(Tier.Nvidia),
        ["AgentJoules:Tiers:nebius:BaseUrl"] = ProviderClientFixtures.BaseUrl(Tier.Nebius),
    };

    /// <summary>The model IDs the models route of the tier under test lists; empty before a Given step set them.</summary>
    public IReadOnlyList<string> ListedIds { get; private set; } = [];

    /// <summary>The lowercase name of the tier whose endpoint the scenario scripted last, or null.</summary>
    public string? TierName { get; private set; }

    /// <summary>The result of the harness run, or null before it ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The model ID the shipped configuration gives <paramref name="tierName"/>.</summary>
    /// <param name="tierName">The lowercase tier name.</param>
    /// <returns>The configured model ID.</returns>
    public static string ShippedModelId(string tierName) =>
        ConfigurationFixtures.ShippedOptions().Tiers[tierName].ModelId ?? throw new InvalidOperationException("The shipped configuration holds no model ID for " + tierName);

    /// <summary>The <c>Judge:ModelId</c> of the shipped configuration.</summary>
    /// <returns>The judge model ID.</returns>
    public static string ShippedJudgeModelId() => ConfigurationFixtures.ShippedOptions().Judge.ModelId ?? throw new InvalidOperationException("The shipped configuration holds no judge model ID");

    /// <summary>
    /// Scripts the endpoint of <paramref name="tierName"/>: its models route lists <paramref name="ids"/>, and it refuses the connection when <paramref name="refuse"/> is true. The judge
    /// tier of the shipped configuration is <c>nvidia</c>, so when that is the tier under test one handler serves the tier check and the judge check.
    /// </summary>
    /// <param name="tierName">The lowercase tier name.</param>
    /// <param name="ids">The model IDs the models route lists.</param>
    /// <param name="refuse">True to refuse every connection.</param>
    public void Script(string tierName, IReadOnlyList<string> ids, bool refuse = false)
    {
        _endpoints[tierName] = new FakeHttpMessageHandler { ModelIds = ids, ConnectionRefused = refuse };
        TierName = tierName;
        ListedIds = ids;
    }

    /// <summary>The recording endpoint scripted last.</summary>
    /// <returns>The fake handler.</returns>
    /// <exception cref="InvalidOperationException">No endpoint was scripted.</exception>
    public FakeHttpMessageHandler Endpoint() =>
        TierName is { } tierName && _endpoints.TryGetValue(tierName, out FakeHttpMessageHandler? handler)
            ? handler
            : throw new InvalidOperationException("No endpoint was scripted for the scenario.");

    /// <summary>Runs <paramref name="arguments"/> through the stand-in <c>run</c> command with the scripted endpoints.</summary>
    /// <param name="arguments">The command line, for example <c>run --tier nvidia --dry-run</c>.</param>
    /// <returns>A task that completes when the command ended and <see cref="Result"/> is set.</returns>
    public async Task RunAsync(string arguments) =>
        Result = await StandInRunner.RunAsync(
            arguments,
            Rewire,
            Endpoint(),
            Settings,
            _endpoints.Select(static pair => KeyValuePair.Create(pair.Key, (HttpMessageHandler)pair.Value)));
}