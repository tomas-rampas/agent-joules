using System.Text.Json.Nodes;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Energy;

using Microsoft.Extensions.Options;

namespace AgentJoules.Core.Configuration;

/// <summary>
/// Checks what annotations cannot: that <c>Meter:Method</c> is a known name, the 10 Hz rule (a rate below 10 Hz is valid only when
/// <c>Meter:AllowBelowMinRate</c> is true), and the <c>Tiers</c> and <c>Judge</c> sections.
/// </summary>
/// <remarks>
/// Every failure is reported, one line each, and the checks do not stop at the first one. A rate of 0 or less, NaN or infinity, which
/// no override allows, is left to the annotation on <see cref="MeterOptions.SampleRateHz"/> so that a caller that runs both validators
/// sees that failure once. The tiers are a dictionary, which the generated <see cref="FlatSectionsValidator"/> does not enumerate, so this
/// validator loops over them: a known tier name, a <c>BaseUrl</c> (absolute, no user info, query or fragment, https unless loopback) and a <c>ModelId</c> when the tier is enabled,
/// <c>ReasoningMode</c>, <c>MaxTokensField</c> and <c>ExtraBodyJson</c>, a positive <c>TimeoutSeconds</c>, the names and values of
/// <c>AgentModelIds</c>, and an <c>ApiKeyVariable</c> that is a variable name ending in <c>_API_KEY</c> (AJ-052). A line names the key path and never shows a configured value.
/// </remarks>
public sealed class CrossFieldValidator : IValidateOptions<AgentJoulesOptions>
{
    /// <summary>The lowest sample rate in hertz that is valid without <c>Meter:AllowBelowMinRate</c>.</summary>
    public const double MinimumRateHz = 10.0;

    private static readonly MinimumValueAttribute Positive = new(0.0) { IsExclusive = true };

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AgentJoulesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        MeterOptions meter = options.Meter;

        // Parse trims the value and matches it without regard to case; its message already names Meter:Method.
        try
        {
            MeterMethodSelector.Parse(meter.Method);
        }
        catch (ConfigurationException failure)
        {
            failures.Add(failure.Message);
        }

        // A rate of 0 or less, NaN and infinity belong to the annotation on Meter:SampleRateHz; only a positive rate is judged here.
        if (meter.SampleRateHz > 0.0 && meter.SampleRateHz < MinimumRateHz && !meter.AllowBelowMinRate)
        {
            failures.Add("Meter:SampleRateHz is below 10 Hz; set Meter:AllowBelowMinRate to true to allow it");
        }

        foreach ((string tierName, TierOptions? tier) in options.Tiers)
        {
            ValidateTier(tierName, tier, failures);
        }

        ValidateJudge(options, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateTier(string tierName, TierOptions? tier, List<string> failures)
    {
        // A tier name comes from the configuration sources, so it is bounded and cleaned before it is printed.
        string path = $"{nameof(AgentJoulesOptions.Tiers)}:{ConfigurationMessages.Segment(tierName)}";

        if (!IsKnownTierName(tierName))
        {
            failures.Add(ConfigurationMessages.UnknownTier(path));
            return;
        }

        if (tier is null)
        {
            failures.Add($"{path} must be a section that holds keys");
            return;
        }

        bool hasBaseUrl = !string.IsNullOrWhiteSpace(tier.BaseUrl);
        if (tier.Enabled && !hasBaseUrl)
        {
            failures.Add($"{path}:BaseUrl must be set when the tier is enabled");
        }
        else if (hasBaseUrl)
        {
            // Plain http is for a loopback host only: a hosted tier must not send its key or its prompts in the clear.
            // The rule is shared with the provider client factory (EndpointRules), so what is refused here is refused again when a key would be sent.
            Uri.TryCreate(tier.BaseUrl, UriKind.Absolute, out Uri? baseUrl);
            switch (EndpointRules.Check(baseUrl, requireHttpsOffLoopback: true))
            {
                case EndpointProblem.Shape:
                    failures.Add($"{path}:BaseUrl must be an absolute http or https URL without user info, query or fragment; the value is not shown");
                    break;
                case EndpointProblem.PlainHttpOffLoopback:
                    failures.Add($"{path}:BaseUrl must be https unless the host is loopback");
                    break;
                default:
                    break;
            }
        }

        if (tier.Enabled && string.IsNullOrWhiteSpace(tier.ModelId))
        {
            failures.Add($"{path}:ModelId must be set when the tier is enabled");
        }

        if (tier.ApiKeyVariable is not null && !VariableNames.IsKeyVariable(tier.ApiKeyVariable))
        {
            failures.Add($"{path}:ApiKeyVariable must be the name of an environment variable that ends in _API_KEY (capital letters, digits and underscores), never a key; the value is not shown");
        }

        if (!TierOptions.ReasoningModes.Contains(tier.ReasoningMode, StringComparer.Ordinal))
        {
            failures.Add($"{path}:ReasoningMode must be {ConfigurationMessages.OrList(TierOptions.ReasoningModes)}; the value is not shown");
        }

        if (tier.ExtraBodyJson is not null)
        {
            JsonObjectProblem problem = ConfigurationJson.Check(tier.ExtraBodyJson, out JsonObject? extraBody);
            if (problem == JsonObjectProblem.RepeatedMember)
            {
                failures.Add($"{path}:ExtraBodyJson must not repeat a member name; the value is not shown");
            }
            else if (problem != JsonObjectProblem.None)
            {
                failures.Add($"{path}:ExtraBodyJson must be a JSON object; the value is not shown");
            }
            else if (ConfigurationJson.FirstReservedMember(extraBody!) is { } reserved)
            {
                failures.Add($"{path}:ExtraBodyJson must not set the member {reserved}; the call itself is not configurable here ({ConfigurationMessages.OrList(ConfigurationJson.ReservedBodyMembers)})");
            }
        }

        if (!TierOptions.MaxTokensFields.Contains(tier.MaxTokensField, StringComparer.Ordinal))
        {
            failures.Add($"{path}:MaxTokensField must be {ConfigurationMessages.OrList(TierOptions.MaxTokensFields)}; the value is not shown");
        }

        if (!Positive.IsValid(tier.TimeoutSeconds))
        {
            failures.Add($"{path}:TimeoutSeconds must be greater than 0");
        }

        foreach ((string agent, string? modelId) in tier.AgentModelIds)
        {
            string agentPath = $"{path}:{nameof(TierOptions.AgentModelIds)}:{ConfigurationMessages.Segment(agent)}";
            if (!TierOptions.KnownAgentNames.Contains(agent, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add(ConfigurationMessages.UnknownAgent(agentPath));
            }
            else if (string.IsNullOrWhiteSpace(modelId))
            {
                failures.Add($"{agentPath} must be set");
            }
        }
    }

    // Judge:Tier is matched exactly: the name goes into the results as written, and the tier names there are lowercase. It must be a section of Tiers
    // once Tiers holds a section with a known name (an unknown name is reported on its own, so it does not count); with no tier configured at all
    // there is nothing to name yet, which keeps an empty configuration valid.
    private static void ValidateJudge(AgentJoulesOptions options, List<string> failures)
    {
        string? judgeTier = options.Judge.Tier;

        if (!TierOptions.KnownNames.Contains(judgeTier, StringComparer.Ordinal))
        {
            failures.Add($"Judge:Tier must be {ConfigurationMessages.OrList(TierOptions.KnownNames)}; the value is not shown");
        }
        else if (options.Tiers.Keys.Any(IsKnownTierName) && !options.Tiers.ContainsKey(judgeTier))
        {
            failures.Add("Judge:Tier names a tier that Tiers does not hold");
        }
    }

    private static bool IsKnownTierName(string name) => TierOptions.KnownNames.Contains(name, StringComparer.OrdinalIgnoreCase);
}