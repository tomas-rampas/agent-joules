using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;

namespace AgentJoules.Workflow.Judge;

/// <summary>Turns the value of <c>Judge:Tier</c> into a <see cref="Tier"/>, for the preflight and for the judge registration alike, so both read the key the same way.</summary>
internal static class JudgeTierName
{
    /// <summary>Parses a tier name exactly, like the configuration validator: <c>Judge:Tier</c> is a tier name as written in the configuration.</summary>
    /// <param name="name">The configured value.</param>
    /// <returns>The tier.</returns>
    /// <exception cref="ConfigurationException">The name is not one of <see cref="TierOptions.KnownNames"/>; the value is not shown (exit code 2).</exception>
    internal static Tier Parse(string name)
    {
        foreach (Tier tier in Enum.GetValues<Tier>())
        {
            if (string.Equals(name, TierOptions.NameOf(tier), StringComparison.Ordinal))
            {
                return tier;
            }
        }

        throw new ConfigurationException("Judge:Tier must be one of " + string.Join(", ", TierOptions.KnownNames) + "; the value is not shown");
    }
}