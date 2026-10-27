using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport.Docs;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-141: the pitch rule (REQ-028). Scoped to the feature because sentences such as "it fails with" and "the pitch check runs" belong to several documentation stories. The cloudgpu state is modelled with a fixture run
/// file through <see cref="CloudGpuConsequenceFixtures"/> or an empty list of files; the checklist and the pitch file of a scenario are texts built in memory. Candidate A is the 197-character text the story quotes
/// (with <c>L40S</c>), a fixture string, not the committed pitch file (which carries the 199-character text since AJ-129); the committed files are checked by the scenario that reads them.
/// </summary>
[Binding]
[Scope(Feature = "AJ-141 Devpost pitch rule")]
public sealed class DevpostTaglineSteps(DevpostContext context)
{
    // Candidate A as the story quotes it: 197 characters, the GPU token is the literal L40S.
    private const string CandidateA = "Which agent step burns the most energy? A .NET middleware meters joules per Microsoft Agent Framework step on your GPU and a Nebius AI Cloud L40S, then compares cost with Nemotron on Token Factory.";

    private const string Tagline = "Agent Joules: energy per agent step, measured on your GPUs";

    private readonly DevpostContext _context = context;

    private static string PitchFile(string candidateA, string candidateALocal) =>
        "# Devpost elevator pitch\n\n"
        + "**A (recommended)** (" + DocumentChecks.Digits(candidateA.Length) + " characters)\n\n> " + candidateA + "\n\n"
        + "**A-local (fallback)** (" + DocumentChecks.Digits(candidateALocal.Length) + " characters)\n\n> " + candidateALocal + "\n\n"
        + "## Tagline (60 characters)\n\n> " + Tagline + "\n";

    private static string ChecklistFile(string block, bool cloudGpuRan) =>
        "## Elevator pitch\n\n```\n" + block + "\n```\n\nCharacters: " + DocumentChecks.Digits(block.Length) + "\n\n"
        + (cloudGpuRan ? "Short tagline row: " + Tagline + "\n" : "Short tagline row: " + DocumentChecks.SingularTaglineOf(Tagline) + "\n\nReason: " + DocumentChecks.NoCloudGpuReason + "\n");

    private static string CommittedALocal() => CloudGpuTaglineRule.CandidateOf(DevpostCommitted.Pitch(), CloudGpuTaglineRule.CandidateALocalLabel);

    private void Model(string block, string candidateA)
    {
        _context.Pitch = PitchFile(candidateA, CommittedALocal());
        _context.Text = ChecklistFile(block, _context.CloudGpuRan);
    }

    [Given(@"^a workspace with a completed cloudgpu run file whose gpuName is ""([^""]*)""$")]
    public void GivenACompletedFile(string gpuName)
    {
        _context.State = CloudGpuConsequenceFixtures.StateOf(RunStatus.Completed, gpuName);
        _context.CloudGpuRan = true;
        Assert.Equal(CloudGpuTaglineRule.Complete, _context.State.State);
        Assert.Equal(gpuName, _context.State.GpuName);
    }

    [Given(@"^a workspace with no completed or partial cloudgpu run file$")]
    public void GivenNoFile()
    {
        _context.State = CloudGpuTaglineRule.Evaluate([], static _ => string.Empty);
        _context.CloudGpuRan = false;
        Assert.Equal(CloudGpuTaglineRule.Absent, _context.State.State);
    }

    [Given(@"^a workspace with no cloudgpu run file and a checklist block equal to candidate A$")]
    public void GivenNoFileAndTheBlockIsA()
    {
        GivenNoFile();
        Model(CandidateA, CandidateA);
    }

    [Given(@"^a workspace with a completed cloudgpu run file and a checklist block equal to A-local$")]
    public void GivenAFileAndTheBlockIsALocal()
    {
        GivenACompletedFile("NVIDIA L40S");
        Model(CommittedALocal(), CandidateA);
    }

    [Given(@"^a pitch candidate of 201 characters$")]
    public void GivenAPitchOf201Characters()
    {
        GivenACompletedFile("NVIDIA L40S");
        string longer = CandidateA + " xxx";
        Assert.Equal(201, longer.Length);
        Model(longer, longer);
    }

    [Given(@"^a checklist pitch block that differs from candidate A by one character$")]
    public void GivenABlockThatDiffers()
    {
        GivenACompletedFile("NVIDIA L40S");
        string changed = "w" + CandidateA[1..];
        Assert.NotEqual(CandidateA, changed);
        Assert.Equal(CandidateA.Length, changed.Length);
        Model(changed, CandidateA);
    }

    [Given(@"^the committed checklist, the committed pitch file and the committed run files$")]
    public void GivenTheCommittedFiles()
    {
        _context.Text = DevpostCommitted.Checklist();
        _context.Pitch = DevpostCommitted.Pitch();
        _context.CloudGpuRan = DevpostCommitted.CloudGpuRan();
    }

    [When(@"^the pitch rule is applied$")]
    public void WhenThePitchRuleIsApplied() => _context.Chosen = DocumentChecks.PitchFor(_context.State, CandidateA, CommittedALocal());

    [When(@"^the pitch check runs$")]
    public void WhenThePitchCheckRuns() => _context.Failures = DocumentChecks.PitchChecks(_context.Text, _context.Pitch, _context.CloudGpuRan);

    [Then(@"^the chosen pitch is candidate A and is 197 characters$")]
    public void ThenTheChosenPitchIsA()
    {
        Assert.Equal(CandidateA, _context.Chosen);
        Assert.Equal(197, _context.Chosen.Length);
    }

    [Then(@"^the checklist block equals candidate A byte for byte$")]
    public void ThenTheBlockEqualsA()
    {
        Model(_context.Chosen, CandidateA);

        Assert.Equal(CandidateA, DocumentChecks.PitchBlock(_context.Text));
        Assert.Empty(DocumentChecks.PitchChecks(_context.Text, _context.Pitch, _context.CloudGpuRan));
    }

    [Then(@"^the chosen pitch is A-local and is 192 characters$")]
    public void ThenTheChosenPitchIsALocal()
    {
        Assert.Equal(CommittedALocal(), _context.Chosen);
        Assert.Equal(192, _context.Chosen.Length);
        Model(_context.Chosen, CandidateA);
        Assert.Empty(DocumentChecks.PitchChecks(_context.Text, _context.Pitch, _context.CloudGpuRan));
    }

    [Then(@"^it contains neither ""([^""]*)"" nor ""([^""]*)""$")]
    public void ThenItContainsNeither(string first, string second)
    {
        Assert.DoesNotContain(first, _context.Chosen, StringComparison.Ordinal);
        Assert.DoesNotContain(second, _context.Chosen, StringComparison.Ordinal);
    }

    [Then(@"^candidate A has ""([^""]*)"" replaced by ""([^""]*)"" and is 200 characters or fewer$")]
    public void ThenTheTokenIsSwapped(string token, string replacement)
    {
        Assert.Equal("H100", DocumentChecks.GpuClassToken(_context.State.GpuName));
        Assert.Equal(CandidateA.Replace(token, replacement, StringComparison.Ordinal), _context.Chosen);
        Assert.DoesNotContain(token, _context.Chosen, StringComparison.Ordinal);
        Assert.Contains(replacement, _context.Chosen, StringComparison.Ordinal);
        Assert.True(_context.Chosen.Length <= DocumentChecks.PitchLimit, $"{_context.Chosen.Length} characters");
    }

    [Then(@"^docs/build-log\.md has a ""Pitch change:"" entry$")]
    public static void ThenTheLogHasAnEntry() =>
        Assert.Contains(DocumentChecks.Lines(DevpostCommitted.BuildLog()), static line => line.StartsWith("- Pitch change: ", StringComparison.Ordinal) && line.Contains(" -> ", StringComparison.Ordinal));

    [Then(@"^the block equals the chosen candidate, the Characters line equals the count and the tagline row follows the rule$")]
    public void ThenTheCommittedBlockFollowsTheRule()
    {
        Assert.Empty(_context.Failures);
        string chosen = CloudGpuTaglineRule.CandidateOf(_context.Pitch, _context.CloudGpuRan ? CloudGpuTaglineRule.CandidateALabel : CloudGpuTaglineRule.CandidateALocalLabel);

        Assert.Equal(chosen, DocumentChecks.PitchBlock(_context.Text));
        Assert.Contains("Characters: " + DocumentChecks.Digits(chosen.Length), DocumentChecks.Lines(_context.Text));
        Assert.True(chosen.Length <= DocumentChecks.PitchLimit);
    }

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) =>
        Assert.True(_context.Failures.Any(entry => entry.Contains(failure, StringComparison.Ordinal)), "failures: " + string.Join("; ", _context.Failures));
}