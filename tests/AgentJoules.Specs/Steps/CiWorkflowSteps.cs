using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-038: the CI workflow scenarios. Self-contained and scoped to the feature, because "the test fails" and "each exits 0" are generic sentences.
/// Scenarios 1 and 2 read the real <c>.github/workflows/ci.yml</c> (repository root found by walking up to <c>global.json</c>). The negative
/// scenarios 4 to 6 run the same <see cref="CiWorkflowRules"/> code as <c>CiWorkflowTests</c> against a modified in-memory copy of the text and
/// never write a file; "grep" in scenario 4 is emulated in C# (line number and line text) rather than started as a process.
/// Scenario 3 cannot run the restore, build and test commands from inside the running test host: that would restore and rebuild the tree under
/// the host that is executing it. Its step asserts that the workflow's three command lines are exactly the strings of the Definition of Done; the
/// local exit codes of those commands are criterion 6, measured by the orchestrator's battery and recorded in docs/build-log.md.
/// </summary>
[Binding]
[Scope(Feature = "Add the GitHub Actions matrix for build, tests and the no-GPU probe")]
public sealed class CiWorkflowSteps
{
    private const string SecretGrepCommand = "grep -n 'secrets\\.' ci.yml";
    private const string StrictlessBuild = "dotnet build -tl:off --no-incremental --no-restore";

    private string _text = string.Empty;
    private CiWorkflowStep? _probeStep;
    private List<string> _localCommandViolations = [];
    private List<string> _grepHits = [];
    private int _grepExitCode = -1;
    private List<string> _violations = [];

    [Given(@"^\.github/workflows/ci\.yml$")]
    [Given(@"^the workflow's restore, build and test commands$")]
    public void GivenTheWorkflow()
    {
        _text = CiWorkflowRules.ReadWorkflowText();
        Assert.False(string.IsNullOrWhiteSpace(_text), "the workflow file is empty");
    }

    [Given(@"^the workflow contains ""([^""]*)""$")]
    public void GivenTheWorkflowContains(string reference)
    {
        _text = CiWorkflowRules.WithSecretReference(CiWorkflowRules.ReadWorkflowText());
        Assert.Contains(reference, _text, StringComparison.Ordinal);
    }

    [Given(@"^the build step reads ""dotnet build"" without -warnaserror$")]
    public void GivenTheBuildStepLacksWarnAsError()
    {
        _text = CiWorkflowRules.WithoutWarningsAsErrors(CiWorkflowRules.ReadWorkflowText());
        Assert.DoesNotContain("-warnaserror", _text, StringComparison.Ordinal);
    }

    [Given(@"^the workflow triggers on pull_request_target$")]
    public void GivenThePullRequestTargetTrigger()
    {
        _text = CiWorkflowRules.WithPullRequestTarget(CiWorkflowRules.ReadWorkflowText());
        Assert.Contains("pull_request_target", _text, StringComparison.Ordinal);
    }

    [When(@"^the workflow text is read$")]
    public void WhenTheWorkflowTextIsRead()
    {
        Assert.False(string.IsNullOrWhiteSpace(_text), "the workflow text was not read");
    }

    [When(@"^the probe step is read$")]
    public void WhenTheProbeStepIsRead()
    {
        _probeStep = CiWorkflowRules.ProbeStep(_text);
    }

    [When(@"^they run locally$")]
    public void WhenTheyRunLocally()
    {
        // The commands are compared with the Definition of Done strings, not started: see the class summary. Criterion 6 is the local run.
        _localCommandViolations = CiWorkflowRules.LocalCommandViolations(_text);
    }

    [When(@"^""(grep -n [^""]*)"" runs$")]
    public void WhenGrepRuns(string command)
    {
        Assert.Equal(SecretGrepCommand, command);
        _grepHits = CiWorkflowRules.SecretReferences(_text);
        _grepExitCode = _grepHits.Count > 0 ? 0 : 1;

        // The story's verdict on the same text: the reference makes CiWorkflowTests fail.
        _violations = CiWorkflowRules.Violations(_text);
    }

    [When(@"^CiWorkflowTests runs$")]
    public void WhenCiWorkflowTestsRuns()
    {
        _violations = CiWorkflowRules.Violations(_text);
    }

    [Then(@"^it contains ""([^""]*)"", ""([^""]*)"" and ""([^""]*)""$")]
    public void ThenItContains(string first, string second, string third)
    {
        Assert.Empty(CiWorkflowRules.Missing(_text, first, second, third));
    }

    [Then(@"^it runs ""([^""]*)"" under bash and checks for ""([^""]*)"": false$")]
    public void ThenTheProbeStepExpectsNoGpu(string command, string property)
    {
        // The rule checks the shell, the exact two run lines and the absence of anything between them; the two arguments are the scenario's words.
        CiWorkflowStep probe = _probeStep ?? throw new InvalidOperationException("The probe step was not found.");
        Assert.Contains(command, probe.Run[0], StringComparison.Ordinal);
        Assert.Contains(probe.Run, line => line.Contains("\"" + property + "\": *false", StringComparison.Ordinal));
        Assert.Empty(CiWorkflowRules.ProbeStepViolations(_text));
    }

    [Then(@"^each exits 0$")]
    public void ThenEachExitsZero()
    {
        // Equality with the Definition of Done strings is what this step can prove inside the test host; the exit codes are criterion 6.
        Assert.Empty(_localCommandViolations);
        List<string> runLines = [.. CiWorkflowRules.Steps(_text).SelectMany(step => step.Run)];
        Assert.Contains(CiWorkflowRules.RestoreCommand, runLines);
        Assert.Contains(CiWorkflowRules.BuildCommand, runLines);
        Assert.Contains(CiWorkflowRules.TestCommand, runLines);
    }

    [Then(@"^it exits 0 with the offending line and the story fails$")]
    public void ThenGrepFindsTheSecretReference()
    {
        Assert.Equal(0, _grepExitCode);
        string hit = Assert.Single(_grepHits);
        int line = CiWorkflowRules.LineOf(_text, CiWorkflowRules.SecretReferenceLine);
        Assert.True(line > 0, "the injected line is not in the text");
        string offending = $"{line}:{CiWorkflowRules.SecretReferenceLine}";
        Assert.Equal(offending, hit);

        // The injected line is the only change, and every rule that reads it says so: the literal grep, the secrets word, the job key, the second
        // expression and the env key.
        AssertExactViolations(
            "secrets reference at " + offending,
            "secrets context at " + offending,
            "the job \"build\" has the key \"env\"",
            offending + ": an expression outside the runs-on line",
            "the workflow has 2 expressions, expected exactly 1 (the runs-on line)",
            "the forbidden key \"env:\" at " + offending);
    }

    [Then(@"^the test fails$")]
    public void ThenTheBuildTestFails()
    {
        // One changed line, three rules that read it: the criterion 1 string, the build command, and the step order.
        string[] order = [.. CiWorkflowRules.StepOrder];
        order[Array.IndexOf(order, CiWorkflowRules.BuildCommand)] = StrictlessBuild;
        AssertExactViolations(
            "the workflow does not contain \"dotnet build -warnaserror -tl:off\"",
            "a step runs \"" + StrictlessBuild + "\" instead of \"" + CiWorkflowRules.BuildCommand + "\"",
            "the steps are [" + string.Join(" | ", order) + "], expected [" + string.Join(" | ", CiWorkflowRules.StepOrder) + "]");
    }

    [Then(@"^the test fails because forks would run with repository privileges$")]
    public void ThenThePullRequestTargetTestFails()
    {
        int line = CiWorkflowRules.LineOf(_text, "  pull_request_target:");
        Assert.True(line > 0, "the injected trigger is not in the text");
        AssertExactViolations("pull_request_target at " + line + ":  pull_request_target:");
    }

    // The unmodified workflow must be clean, so the rules do not fail on everything, and the violations the When step collected on the modified
    // text must be exactly the expected list: a mutation that also trips an unrelated rule fails here.
    private void AssertExactViolations(params string[] expected)
    {
        Assert.Empty(CiWorkflowRules.Violations(CiWorkflowRules.ReadWorkflowText()));
        Assert.Equal(expected, _violations);
    }
}