using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.IntegrationTests;

/// <summary>
/// The live check behind AJ-120 (REQ-031, design 6.3): the repository tracks no private-key file name and <c>git check-ignore</c> prints the three ignore probes. It reads names only, needs no GPU, network or key,
/// and is skipped unless AGENTJOULES_INTEGRATION is 1.
/// </summary>
[Trait("Spec", "REQ-031")]
public sealed class CloudGpuPrerequisitesTests
{
    [IntegrationFact]
    public void The_key_pair_stays_outside_the_repository()
    {
        string root = ConfigurationFixtures.RepositoryRoot();

        KeyHygieneResult result = KeyHygieneCheck.RunTracked(root);
        Assert.True(result.Passed, result.Message);

        IReadOnlyList<string> ignored = KeyHygieneCheck.IgnoredProbeNames(root);
        Assert.All(KeyHygieneCheck.IgnoreProbeNames, name => Assert.Contains(name, ignored));
    }
}