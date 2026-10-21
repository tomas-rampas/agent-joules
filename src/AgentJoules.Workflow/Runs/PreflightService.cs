using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AgentJoules.Workflow.Runs;

/// <summary>
/// The checks that run before any call (design 3.7, REQ-017, EDGE-011). This story adds the key check; the model check (AJ-053) and the measured-tier checks (AJ-054) join it
/// later. The check reads the key variables from <see cref="IConfiguration"/> and never from the process environment, so a test that replaces the configuration controls every
/// key, and it reads only whether a value is present: no value is copied into a message, a field or a result.
/// </summary>
/// <param name="configuration">The configuration that holds the key variables (the process environment, user secrets or an in-memory override, as the host composed it).</param>
/// <param name="options">The bound and validated options.</param>
public sealed class PreflightService(IConfiguration configuration, IOptions<AgentJoulesOptions> options)
{
    /// <summary>The variable that names the SSH host of the <c>cloudgpu</c> VM; required on the laptop (<c>CloudGpu:RequireSshVariables</c> true).</summary>
    public const string SshHostVariable = "CLOUDGPU_SSH_HOST";

    /// <summary>The variable that names the SSH user of the <c>cloudgpu</c> VM; required on the laptop (<c>CloudGpu:RequireSshVariables</c> true).</summary>
    public const string SshUserVariable = "CLOUDGPU_SSH_USER";

    private const string AllTiers = "all";

    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly IOptions<AgentJoulesOptions> _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Resolves the <c>--tier</c> value: <c>all</c> is every enabled tier in the fixed order of a run (<c>local</c>, <c>cloudgpu</c>, <c>nvidia</c>, <c>nebius</c>); a tier name is that
    /// one tier, enabled or not (an explicit <c>--tier</c> runs a disabled tier). The value is matched without regard to case.
    /// </summary>
    /// <param name="selection">A tier name or <c>all</c>.</param>
    /// <returns>The selected tiers in the order of a run; never empty.</returns>
    /// <exception cref="ConfigurationException">The value is neither a tier name nor <c>all</c>, or <c>all</c> finds no enabled tier; the message never shows the value.</exception>
    public IReadOnlyList<Tier> SelectTiers(string selection)
    {
        if (string.Equals(selection, AllTiers, StringComparison.OrdinalIgnoreCase))
        {
            Dictionary<string, TierOptions> configured = _options.Value.Tiers;
            Tier[] enabled =
            [
                .. Enum.GetValues<Tier>()
                    .Where(tier => configured.TryGetValue(TierOptions.NameOf(tier), out TierOptions? entry) && entry is { Enabled: true }),
            ];

            return enabled.Length > 0 ? enabled : throw new ConfigurationException("--tier all finds no enabled tier; enable one under Tiers:<tier>:Enabled");
        }

        foreach (Tier tier in Enum.GetValues<Tier>())
        {
            if (string.Equals(selection, TierOptions.NameOf(tier), StringComparison.OrdinalIgnoreCase))
            {
                return [tier];
            }
        }

        throw new ConfigurationException($"--tier must be {string.Join(", ", TierOptions.KnownNames)} or {AllTiers}; the value is not shown");
    }

    /// <summary>
    /// Checks that every variable the selected tiers need is set, before any request (EDGE-011): the <c>ApiKeyVariable</c> of each selected tier that names one, for <c>cloudgpu</c> the
    /// two SSH variables when <c>CloudGpu:RequireSshVariables</c> is true, and the judge key (the <c>ApiKeyVariable</c> of the <c>Judge:Tier</c> tier). A variable that is absent, empty
    /// or white space is missing. A missing judge key is part of the failure in a dry run and only a result outside it, which the caller turns into a warning.
    /// </summary>
    /// <param name="tiers">The selected tiers, from <see cref="SelectTiers(string)"/>.</param>
    /// <param name="dryRun">True for <c>--dry-run</c>.</param>
    /// <returns>The result when nothing required is missing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> is null.</exception>
    /// <exception cref="MissingKeyException">Something required is missing: one exception, one message, every missing variable once in the order the check met them (exit code 3).</exception>
    public KeyCheckResult CheckKeys(IReadOnlyList<Tier> tiers, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        AgentJoulesOptions current = _options.Value;
        List<string> missing = [];

        foreach (Tier tier in tiers)
        {
            AddIfMissing(missing, KeyVariableOf(current, TierOptions.NameOf(tier)));

            if (tier == Tier.CloudGpu && current.CloudGpu.RequireSshVariables)
            {
                AddIfMissing(missing, SshHostVariable);
                AddIfMissing(missing, SshUserVariable);
            }
        }

        string? judgeVariable = KeyVariableOf(current, current.Judge.Tier);
        bool judgeMissing = judgeVariable is not null && IsMissing(judgeVariable);
        if (judgeMissing && dryRun)
        {
            AddIfMissing(missing, judgeVariable);
        }

        return missing.Count > 0
            ? throw new MissingKeyException(MissingKeyMessage.Format(missing), missing)
            : new KeyCheckResult(judgeMissing && !dryRun ? judgeVariable : null);
    }

    // The name of the variable the tier reads its key from, or null for a tier that is not configured or names none.
    private static string? KeyVariableOf(AgentJoulesOptions current, string? tierName) =>
        tierName is not null && current.Tiers.TryGetValue(tierName, out TierOptions? tier) && tier is { ApiKeyVariable: { } variable } && !string.IsNullOrWhiteSpace(variable)
            ? variable
            : null;

    private void AddIfMissing(List<string> missing, string? variable)
    {
        if (variable is not null && !missing.Contains(variable, StringComparer.Ordinal) && IsMissing(variable))
        {
            missing.Add(variable);
        }
    }

    private bool IsMissing(string variable) => string.IsNullOrWhiteSpace(_configuration[variable]);
}