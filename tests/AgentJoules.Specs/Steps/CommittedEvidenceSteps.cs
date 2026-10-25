using System.Globalization;

using AgentJoules.Specs.Support;
using AgentJoules.TestSupport.Evidence;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-115: the committed local evidence and the README runtime statements are checked against each other (REQ-020, REQ-021, EDGE-010). Scoped to the feature because "the evidence tests run" and "the check fails"
/// belong to this story only. The state lives in <see cref="CommittedEvidenceContext"/>; each scenario lays a <see cref="CommittedEvidenceDraft"/> out in a temporary workspace and runs the same
/// <see cref="CommittedEvidenceRules"/> that judge the committed files.
/// </summary>
[Binding]
[Scope(Feature = "Gate the committed local evidence and the README runtime statements")]
public sealed class CommittedEvidenceSteps(CommittedEvidenceContext context)
{
    private const string ReadmeStart = "README.md";
    private const string Edge010Start = "EDGE-010";

    private readonly CommittedEvidenceContext _context = context;

    [Given(@"^a workspace with a local run file, endpoints, a calibration file and a README that agree$")]
    public static void GivenAWorkspaceThatAgrees()
    {
        // The draft starts as a consistent set; nothing to change.
    }

    [Given(@"^the run file has localRuntime ""(nim|ollama)"" and the tag ""([^""]+)""$")]
    public void GivenTheRunFileHasTheRuntimeAndTag(string runtime, string tag)
    {
        _context.Draft.Runtime = runtime;
        _context.Draft.Tag = tag;
    }

    [Given(@"^README\.md contains ""Local numbers were produced with (\S+) running ([^""]+)\.""$")]
    public void GivenTheReadmeContainsTheSentence(string runtime, string tag)
    {
        _context.Draft.ReadmeRuntime = runtime;
        _context.Draft.ReadmeTag = tag;
    }

    [Given(@"^gpuOffloadPercent is (\d+) and run\.flags is empty$")]
    public void GivenOffloadWithoutFlags(int percent)
    {
        _context.Draft.GpuOffloadPercent = percent;
        _context.Draft.Flags.Clear();
    }

    [Given(@"^baseline\.seconds is (\d+)$")]
    public void GivenBaselineSeconds(int seconds) => _context.Draft.BaselineSeconds = seconds;

    [Given(@"^run\.repeat is (\d+)$")]
    public void GivenRepeat(int repeat) => _context.Draft.Repeat = repeat;

    [Given(@"^the README sentence names a tag that differs from the run file$")]
    public void GivenTheReadmeNamesAnotherTag() => _context.Draft.ReadmeTag = "other/readme-tag";

    [Given(@"^the Decision says ""Local runtime: (nim|ollama)"" and README\.md has no link to the attempt document$")]
    public void GivenTheDecisionWithoutTheLink(string runtime)
    {
        _context.Draft.DecisionRuntime = runtime;
        _context.Draft.Runtime = runtime;
        _context.Draft.ReadmeHasLink = false;
    }

    [Given(@"^the Decision says ""Local runtime: (nim|ollama)"" and the latest local run file says ""(nim|ollama)""$")]
    public void GivenTheDecisionAndTheRunFile(string decision, string runtime)
    {
        _context.Draft.DecisionRuntime = decision;
        _context.Draft.Runtime = runtime;
    }

    [When(@"^the evidence tests run$")]
    public void WhenTheEvidenceTestsRun() => _context.Run();

    [Then(@"^all checks pass$")]
    public void ThenAllChecksPass() => Assert.True(_context.Violations.Count == 0, string.Join("\n", _context.Violations));

    [Then(@"^the README check passes$")]
    public void ThenTheReadmeCheckPasses()
    {
        string[] readme = [.. _context.Violations.Where(static line => line.StartsWith(ReadmeStart, StringComparison.Ordinal))];

        Assert.True(readme.Length == 0, string.Join("\n", readme));
    }

    [Then(@"^the check fails and names ""([^""]+)""$")]
    public void ThenTheCheckFailsAndNamesQuoted(string name) => AssertFailsNaming(_context.Violations, name);

    [Then(@"^the check fails and names ([A-Za-z][A-Za-z.]*)$")]
    public void ThenTheCheckFailsAndNames(string name) => AssertFailsNaming(_context.Violations, name);

    [Then(@"^the README check fails and names both tags$")]
    public void ThenTheReadmeCheckFailsAndNamesBothTags()
    {
        string[] readme = [.. _context.Violations.Where(static line => line.StartsWith(ReadmeStart, StringComparison.Ordinal))];

        AssertFailsNaming(readme, _context.Draft.Tag);
        AssertFailsNaming(readme, _context.Draft.ReadmeTag ?? string.Empty);
    }

    [Then(@"^the EDGE-010 check fails$")]
    public void ThenTheEdge010CheckFails()
    {
        string[] edge = [.. _context.Violations.Where(static line => line.StartsWith(Edge010Start, StringComparison.Ordinal))];

        Assert.True(edge.Length > 0, "No EDGE-010 violation was reported: " + string.Join("\n", _context.Violations));
    }

    private static void AssertFailsNaming(IReadOnlyList<string> violations, string name)
    {
        Assert.True(violations.Count > 0, string.Create(CultureInfo.InvariantCulture, $"The check passed; it should fail and name \"{name}\"."));
        Assert.Contains(violations, line => line.Contains(name, StringComparison.Ordinal));
    }
}