using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace AgentJoules.IntegrationTests;

/// <summary>
/// The gated check behind AJ-127 (REQ-031, design 6.3): the newest committed <c>results/&lt;yyyyMMddTHHmmssZ&gt;-cloudgpu.json</c> has 3 tasks, <c>run.repeat</c> 3, 36 steps, <c>run.status</c> completed (or partial with a stop reason), the
/// provider nebius-ai-cloud, precision FP8, the instance preset, region eu-north1, GPU UUID, container image and a sha256 digest and a baseline of at least 30 s, and every committed cloudgpu run file passes the check of <c>AJ validate</c>
/// (<see cref="RunFileValidator"/>). The facts are skipped, with their reason, unless AGENTJOULES_INTEGRATION is 1 and the HEAD tree holds the file. They read committed files only: no GPU, network or key.
/// </summary>
[Trait("Spec", "REQ-031")]
public sealed class CloudGpuRunTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [CloudGpuEvidenceFact(CloudGpuEvidenceKind.Run)]
    public void The_newest_committed_cloudgpu_run_file_has_the_shape_and_the_fp8_metadata()
    {
        string path = CloudGpuEvidence.NewestRun() ?? throw new Xunit.Sdk.XunitException(CloudGpuEvidence.NoRunMessage);

        IReadOnlyList<string> failures = CloudGpuEvidence.RunFailures(Path.GetFileName(path), File.ReadAllText(path));

        _output.WriteLine($"{Path.GetFileName(path)}: {failures.Count} failures");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [CloudGpuEvidenceFact(CloudGpuEvidenceKind.Run)]
    public void Every_committed_cloudgpu_run_file_is_valid_for_the_validate_command()
    {
        IReadOnlyList<string> files = CloudGpuEvidence.CommittedRunFiles();

        string[] failures = [.. files.SelectMany(static file => RunFileValidator.Check(Path.GetFileName(file), File.ReadAllText(file)))];

        _output.WriteLine($"{files.Count} cloudgpu run files, {failures.Length} validator lines");
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
    }
}