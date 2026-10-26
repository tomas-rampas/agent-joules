using AgentJoules.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace AgentJoules.IntegrationTests;

/// <summary>
/// The gated check behind AJ-126 (REQ-031, design 6.3): the <c>## cloudgpu</c> section of <c>docs/endpoints.md</c> has a <c>Verified:</c> date of 2026-10-17 or later, a <c>- model:</c> line, the <c>Command:</c> of the models call and the
/// streaming-usage and reasoning-result lines. The fact is skipped, with its reason, unless AGENTJOULES_INTEGRATION is 1 and the section has a Verified line. It reads the committed document only: no network or key.
/// </summary>
[Trait("Spec", "REQ-031")]
public sealed class CloudGpuNimTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [CloudGpuEvidenceFact(CloudGpuEvidenceKind.Endpoints)]
    public void The_cloudgpu_endpoints_section_records_the_verified_model_call()
    {
        string markdown = CloudGpuEvidence.EndpointsText() ?? throw new Xunit.Sdk.XunitException(CloudGpuEvidence.NoEndpointsMessage);

        IReadOnlyList<string> failures = CloudGpuEvidence.EndpointsFailures(markdown, withMeasurements: true);

        _output.WriteLine($"## cloudgpu: {failures.Count} failures");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.False(CloudGpuEvidence.HasSecretShape(markdown), "docs/endpoints.md holds the shape of a key (a key-like token: nvapi- or 40 or more mixed characters).");
    }
}