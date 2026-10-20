using AgentJoules.Core.Cli;
using AgentJoules.Core.Runs;

namespace AgentJoules.Core.Configuration;

/// <summary>One entry of <c>Tiers</c>: how a tier (<c>local</c>, <c>cloudgpu</c>, <c>nvidia</c> or <c>nebius</c>) is reached and described.</summary>
/// <remarks>
/// A mutable class with plain get and set accessors so that the configuration binding generator binds it; a key that is absent keeps the default
/// of its property. The rules (an enabled tier has a base URL and a model ID, the allowed words of <see cref="ReasoningMode"/> and
/// <see cref="MaxTokensField"/>) are checked by <see cref="CrossFieldValidator"/>, because the tiers live in a dictionary that the
/// generated validator does not enumerate.
/// </remarks>
public sealed class TierOptions
{
    /// <summary>The tier names the configuration may hold, in the fixed order of a run: <c>local</c>, <c>cloudgpu</c>, <c>nvidia</c>, <c>nebius</c>.</summary>
    public static IReadOnlyList<string> KnownNames { get; } = ["local", "cloudgpu", "nvidia", "nebius"];

    /// <summary>The words <see cref="ReasoningMode"/> may hold.</summary>
    public static IReadOnlyList<string> ReasoningModes { get; } = ["off", "on", "unsupported"];

    /// <summary>The words <see cref="MaxTokensField"/> may hold.</summary>
    public static IReadOnlyList<string> MaxTokensFields { get; } = ["max_completion_tokens", "max_tokens"];

    /// <summary>The agent names <see cref="AgentModelIds"/> may hold keys for.</summary>
    public static IReadOnlyList<string> KnownAgentNames { get; } = ["Planner", "Researcher", "Writer", "Reviewer"];

    /// <summary>Key <c>Tiers:&lt;tier&gt;:BaseUrl</c>, default none: the base URL of the OpenAI-compatible endpoint (by convention it ends in <c>/v1</c>, which is not checked); must be set when <see cref="Enabled"/> is true, absolute, without user info, query or fragment, and https unless the host is loopback.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Key <c>Tiers:&lt;tier&gt;:ModelId</c>, default none: the model ID the tier serves; must be set when <see cref="Enabled"/> is true.</summary>
    public string? ModelId { get; set; }

    /// <summary>Key <c>Tiers:&lt;tier&gt;:AgentModelIds</c>, default empty: a model ID per agent (<c>Planner</c>, <c>Researcher</c>, <c>Writer</c>, <c>Reviewer</c>) that replaces <see cref="ModelId"/> for that agent; names match without regard to case.</summary>
    public Dictionary<string, string> AgentModelIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Key <c>Tiers:&lt;tier&gt;:ApiKeyVariable</c>, default none: the NAME of the variable that holds the API key, never the key itself; the local and cloudgpu tiers have none.</summary>
    public string? ApiKeyVariable { get; set; }

    /// <summary>Key <c>Tiers:&lt;tier&gt;:Enabled</c>, default true: whether <c>--tier all</c> runs the tier; an explicit <c>--tier</c> runs it either way.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Key <c>Tiers:&lt;tier&gt;:Precision</c>, default none: the weight precision the tier serves (for example <c>FP8</c>), reported in the results.</summary>
    public string? Precision { get; set; }

    /// <summary>Key <c>Tiers:&lt;tier&gt;:ReasoningMode</c>, default <c>off</c>: <c>off</c>, <c>on</c> or <c>unsupported</c>.</summary>
    public string ReasoningMode { get; set; } = "off";

    /// <summary>Key <c>Tiers:&lt;tier&gt;:ExtraBodyJson</c>, default none: a JSON object merged into every chat request body.</summary>
    public string? ExtraBodyJson { get; set; }

    /// <summary>Key <c>Tiers:&lt;tier&gt;:SystemPromptPrefix</c>, default none: text put in front of every agent instruction.</summary>
    public string? SystemPromptPrefix { get; set; }

    /// <summary>Key <c>Tiers:&lt;tier&gt;:SupportsSeed</c>, default false: whether the endpoint honours a request seed.</summary>
    public bool SupportsSeed { get; set; }

    /// <summary>Key <c>Tiers:&lt;tier&gt;:MissingPriceReason</c>, default <c>price-not-found</c>: the reason reported as the cost when the price table has no row for the model.</summary>
    public string MissingPriceReason { get; set; } = "price-not-found";

    /// <summary>Key <c>Tiers:&lt;tier&gt;:TimeoutSeconds</c>, default 300: the network timeout of one call in seconds; greater than 0.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Key <c>Tiers:&lt;tier&gt;:MaxTokensField</c>, default <c>max_completion_tokens</c>: the request field that carries the output cap, <c>max_completion_tokens</c> or <c>max_tokens</c>.</summary>
    public string MaxTokensField { get; set; } = "max_completion_tokens";

    /// <summary>The configuration name of <paramref name="tier"/>: the key under <c>Tiers</c> and the word of the results schema (<c>local</c>, <c>cloudgpu</c>, <c>nvidia</c>, <c>nebius</c>).</summary>
    /// <param name="tier">A defined tier.</param>
    /// <returns>The lowercase name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a defined <see cref="Tier"/>.</exception>
    public static string NameOf(Tier tier) => tier switch
    {
        Tier.Local => KnownNames[0],
        Tier.CloudGpu => KnownNames[1],
        Tier.Nvidia => KnownNames[2],
        Tier.Nebius => KnownNames[3],
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "The tier is not a defined value."),
    };

    /// <summary>
    /// The model ID a call of <paramref name="agent"/> goes to: the <see cref="AgentModelIds"/> entry of the agent (names match without regard to
    /// case, AJ-043) when it holds a model ID, otherwise <see cref="ModelId"/>. The agent factory of the workflow and the model check of the
    /// preflight both take the model from here, so the two cannot disagree.
    /// </summary>
    /// <param name="agent">The agent name, for example <c>Writer</c>.</param>
    /// <returns>The model ID; never blank.</returns>
    /// <exception cref="ArgumentException"><paramref name="agent"/> is null or blank.</exception>
    /// <exception cref="ConfigurationException">The agent has no entry and the tier has no <see cref="ModelId"/>.</exception>
    public string ResolveModelId(string agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);

        // A scan, not a lookup: a dictionary that replaced the default one may compare names with case.
        foreach ((string name, string? modelId) in AgentModelIds)
        {
            if (string.Equals(name, agent, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(modelId))
            {
                return modelId;
            }
        }

        return !string.IsNullOrWhiteSpace(ModelId)
            ? ModelId
            : throw new ConfigurationException("The tier has no ModelId and no AgentModelIds entry for the agent " + ConfigurationMessages.Segment(agent) + "; set Tiers:<tier>:ModelId");
    }
}

/// <summary>The <c>Judge</c> section of <c>AgentJoules</c>: which tier and model score the answers.</summary>
/// <remarks>A mutable class with plain get and set accessors so that the configuration binding generator binds it; <see cref="Tier"/> is checked by <see cref="CrossFieldValidator"/>.</remarks>
public sealed class JudgeOptions
{
    /// <summary>Key <c>Judge:Tier</c>, default <c>nvidia</c>: the name of the tier whose endpoint serves the judge; one of <see cref="TierOptions.KnownNames"/> and, once <c>Tiers</c> holds a tier with a known name, one of the tiers it holds.</summary>
    public string Tier { get; set; } = "nvidia";

    /// <summary>Key <c>Judge:ModelId</c>, default none: the model ID of the judge, looked up in the section of <see cref="Tier"/>.</summary>
    public string? ModelId { get; set; }
}

/// <summary>The <c>Workflow</c> section of <c>AgentJoules</c>: settings of the agent workflow that every tier shares.</summary>
/// <remarks>A mutable class with plain get and set accessors so that the configuration binding generator binds it.</remarks>
public sealed class WorkflowOptions
{
    /// <summary>Key <c>Workflow:Seed</c>, default 42: the request seed sent to a tier whose <see cref="TierOptions.SupportsSeed"/> is true.</summary>
    public int Seed { get; set; } = 42;
}