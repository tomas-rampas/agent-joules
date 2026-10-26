using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-135: the rehearsal of the demo on the laptop with the keys (REQ-027, REQ-028, REQ-032). The scenarios follow the AJ-110 pattern (<see cref="CampaignPreflightContext"/>): with AGENTJOULES_INTEGRATION closed, or a
/// key variable absent, the steps record the reason and pass; with the gate open the first scenario runs the real <c>run --tier all --dry-run</c> with the keys of the environment and a live HTTP handler per tier
/// (the one place a test opts in to the network, behind the gate). The scenario about a missing key is hermetic and always asserts: the configuration is in memory, every HTTP request is refused, and the output is
/// checked for key material.
/// </summary>
[Binding]
[Scope(Feature = "AJ-135 Demo rehearsal with the laptop and keys")]
public sealed class DemoRehearsalSteps(CampaignPreflightContext context, IReqnrollOutputHelper output)
{
    private static readonly string[] Tiers = ["local", "nvidia", "nebius"];

    private readonly CampaignPreflightContext _context = context;
    private readonly IReqnrollOutputHelper _output = output;

    private bool _hermetic;

    [Given(@"^NVIDIA_API_KEY and NEBIUS_API_KEY are set and the local runtime is up$")]
    public void GivenTheKeysAreSet() => _context.Open("NVIDIA_API_KEY", "NEBIUS_API_KEY");

    [Given(@"^NEBIUS_API_KEY is unset$")]
    public void GivenNebiusKeyIsUnset() => _hermetic = true;

    [When(@"^""run --tier all --dry-run"" runs$")]
    public async Task WhenTheDryRunRuns()
    {
        if (_hermetic)
        {
            await _context.RunWithoutNebiusKeyAsync("run --tier all --dry-run");
        }
        else if (_context.IsActive)
        {
            await _context.RunLiveAsync("run --tier all --dry-run");
        }
    }

    [Then(@"^the exit code is 0 and each enabled tier prints its endpoint, matched model IDs and ""OK""$")]
    public void ThenEachTierPrintsOk()
    {
        if (!_context.IsActive)
        {
            _output.WriteLine("passed without asserting: " + _context.Note);
            return;
        }

        Assert.True(_context.Result!.ExitCode == 0, $"exit {_context.Result.ExitCode}; stderr: {_context.Result.Stderr}");
        string[] lines = [.. _context.Output.Split('\n').Select(static line => line.TrimEnd('\r'))];
        foreach (string tier in Tiers)
        {
            Assert.True(
                lines.Any(line => line.StartsWith(tier + " ", StringComparison.Ordinal) && line.EndsWith(" OK", StringComparison.Ordinal)),
                $"no output line starts with \"{tier}\" and ends with \"OK\"");
        }
    }

    [Then(@"^the exit code is 3 and the output names NEBIUS_API_KEY and prints no key value$")]
    public void ThenTheMissingKeyIsNamed()
    {
        Assert.True(_context.Result!.ExitCode == 3, $"exit {_context.Result.ExitCode}; stderr: {_context.Result.Stderr}");
        Assert.Contains("NEBIUS_API_KEY", _context.Output, StringComparison.Ordinal);
        Assert.Null(DocumentChecks.KeyMaterial(_context.Output, "the output"));
        Assert.Empty(_context.Result.DeniedRequests);
    }
}