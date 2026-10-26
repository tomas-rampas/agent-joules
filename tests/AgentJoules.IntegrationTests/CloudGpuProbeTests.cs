using AgentJoules.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace AgentJoules.IntegrationTests;

/// <summary>
/// The gated check behind AJ-125 (REQ-031, design 6.3): the newest committed <c>results/probe-&lt;yyyyMMddTHHmmssZ&gt;-cloudgpu.json</c> validates against <c>$defs/probe</c> and names an accepted GPU (L40S or the GPU of the user's 2026-10-26 decision), a <c>gpuUuid</c>, a <c>migMode</c> of
/// disabled or not-supported and a boolean <c>energyCounterSupported</c>. The fact is skipped, with its reason, unless AGENTJOULES_INTEGRATION is 1 and the HEAD tree holds the file. It reads the committed file only: no GPU, network or key.
/// </summary>
[Trait("Spec", "REQ-031")]
public sealed class CloudGpuProbeTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [CloudGpuEvidenceFact(CloudGpuEvidenceKind.Probe)]
    public void The_newest_committed_cloudgpu_probe_validates_and_names_an_accepted_GPU()
    {
        string path = CloudGpuEvidence.NewestProbe() ?? throw new Xunit.Sdk.XunitException(CloudGpuEvidence.NoProbeMessage);

        IReadOnlyList<string> failures = CloudGpuEvidence.ProbeFailures(Path.GetFileName(path), File.ReadAllText(path));

        _output.WriteLine($"{Path.GetFileName(path)}: {failures.Count} failures");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}