using System.Text.Json;

using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Judge;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-077: a judge failure never changes the exit code (EDGE-012). Scoped to the feature because sentences such as "the exit code is 0" and "the run is judged" are reused by other
/// stories with other meanings. The state lives in <see cref="JudgeFailureContext"/>: the unit scenario runs the real <c>JudgeService</c> over fake provider clients, and the command
/// scenarios run the real <c>run</c> command with the production judge over a fake endpoint for the workflow tier and another for the judge tier.
/// </summary>
[Binding]
[Scope(Feature = "AJ-077 Never let a judge failure change the exit code")]
public sealed class JudgeFailureSteps(JudgeFailureContext context)
{
    private const string JudgeWarningStart = "Warning: the judge failed";

    private readonly JudgeFailureContext _context = context;

    private JudgeResult Result => _context.Result ?? throw new XunitException("The run was not judged.");

    private CliRunResult Command => _context.Command ?? throw new XunitException("The command has not run yet.");

    private static string TextOf(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private static MetricLabel LabelNamed(string name) => name switch
    {
        "model-judged" => MetricLabel.ModelJudged,
        "N/A" => MetricLabel.NotApplicable,
        _ => throw new XunitException($"Unknown label word {name}."),
    };

    [Given(@"^a judge that fails for the third of 12 steps and answers ""([^""]*)"" otherwise$")]
    public void GivenAJudgeThatFailsForTheThirdOfTwelveSteps(string reply)
    {
        _context.Reply = reply;
        _context.FailingCall = 3;
    }

    [Given(@"^a judge that fails for every step$")]
    public void GivenAJudgeThatFailsForEveryStep() => _context.JudgeStatus = 500;

    [Given(@"^a judge endpoint that answers (\d+) on every call$")]
    public void GivenAJudgeEndpointThatAnswersOnEveryCall(int status) => _context.JudgeStatus = status;

    [Given(@"^a judge that replies ""([^""]*)""$")]
    public void GivenAJudgeThatReplies(string reply) => _context.Reply = reply;

    [Given(@"^the judge key is absent and the workflow tier key is present$")]
    public void GivenTheJudgeKeyIsAbsent() => _context.JudgeKeyAbsent = true;

    [When(@"^the run is judged$")]
    public async Task WhenTheRunIsJudged()
    {
        // The reply scenario judges inside the real command, so that the exit code it asserts is the real one; the scenario that fails one call of twelve judges over fakes.
        if (_context.FailingCall > 0)
        {
            await _context.JudgeAsync();
        }
        else
        {
            await _context.RunCommandAsync("run --tier nvidia --repeat 1");
        }
    }

    [When(@"^the run completes$")]
    public Task WhenTheRunCompletes() => _context.RunCommandAsync("run --tier nvidia --repeat 1");

    [When(@"^""([^""]*)"" is executed$")]
    public Task WhenTheCommandIsExecuted(string arguments) => _context.RunCommandAsync(arguments);

    [Then(@"^(\d+) steps have quality (\d+) with label ""([^""]*)""$")]
    public void ThenStepsHaveQuality(int count, double quality, string label)
    {
        Assert.Equal(JudgeFailureContext.StepCount, Result.Steps.Count);
        MetricLabel expected = LabelNamed(label);
        StepReading[] scored = [.. Result.Steps.Where(step => step.Quality.Label == expected)];
        Assert.Equal(count, scored.Length);
        Assert.All(scored, step => Assert.Equal(quality, step.Quality.Value));
    }

    [Then(@"^the third step has quality label ""([^""]*)"" and qualityReason ""([^""]*)""$")]
    public void ThenTheThirdStepHasQualityLabelAndReason(string label, string reason)
    {
        StepReading third = Result.Steps[2];
        Assert.Equal(LabelNamed(label), third.Quality.Label);
        Assert.Null(third.Quality.Value);
        Assert.Equal(reason, third.QualityReason);

        // Only the third step failed, and the run did not stop at it.
        Assert.Equal(1, Result.FailedSteps);
        Assert.Equal(JudgeFailureContext.StepCount, Result.JudgedSteps);
    }

    [Then(@"^exactly 1 judge warning line is printed$")]
    public void ThenExactlyOneJudgeWarningLineIsPrinted()
    {
        Assert.Equal(0, Command.ExitCode);
        string line = Assert.Single(Command.StderrLines, static candidate => candidate.StartsWith(JudgeWarningStart, StringComparison.Ordinal));
        Assert.Contains("steps of tier nvidia", line, StringComparison.Ordinal);

        // One line for the judge, and no step line or other warning about it.
        Assert.Equal(1, Command.StderrLines.Count(static candidate => candidate.Contains("judge", StringComparison.OrdinalIgnoreCase)));
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        Assert.True(expected == Command.ExitCode, $"Expected exit code {expected} but got {Command.ExitCode}; stderr: {Command.Stderr}");

        // A judge that answers a status must really have been asked: once per step, and five times per step for a 429 (the attempts of the retry layer). Without this a scenario would pass
        // on a judge that never ran.
        if (_context.JudgeStatus is { } status)
        {
            FakeHttpMessageHandler endpoint = _context.JudgeEndpoint ?? throw new XunitException("The scenario routed no judge endpoint.");
            Assert.Equal(_context.FileSteps.Length * (status == 429 ? 5 : 1), endpoint.Count(FakeRoute.ChatCompletions));
        }
    }

    [Then(@"^every step has quality label ""([^""]*)"", qualityReason ""([^""]*)"" and joulesPerQualityPoint label ""([^""]*)""$")]
    public void ThenEveryStepHasQualityLabelReasonAndRatioLabel(string label, string reason, string ratioLabel)
    {
        JsonElement[] steps = _context.FileSteps;
        Assert.NotEmpty(steps);
        Assert.All(steps, step =>
        {
            Assert.Equal(label, TextOf(step.GetProperty("quality"), "label"));
            Assert.StartsWith("judge-failed: ", TextOf(step.GetProperty("quality"), "reason"), StringComparison.Ordinal);
            Assert.Equal(reason, TextOf(step, "qualityReason"));
            Assert.Equal(ratioLabel, TextOf(step.GetProperty("joulesPerQualityPoint"), "label"));
        });
    }

    [Then(@"^the results file has run\.status ""([^""]*)""$")]
    public void ThenTheResultsFileHasRunStatus(string status) =>
        Assert.Equal(status, TextOf((_context.Document ?? throw new XunitException("The command has not run yet.")).RootElement.GetProperty("run"), "status"));

    [Then(@"^the quality reason starts with ""([^""]*)"" and the exit code is (\d+)$")]
    public void ThenTheQualityReasonStartsWith(string prefix, int exitCode)
    {
        Assert.True(exitCode == Command.ExitCode, $"Expected exit code {exitCode} but got {Command.ExitCode}; stderr: {Command.Stderr}");
        JsonElement[] steps = _context.FileSteps;
        Assert.NotEmpty(steps);
        Assert.All(steps, step => Assert.StartsWith(prefix, TextOf(step.GetProperty("quality"), "reason"), StringComparison.Ordinal));
    }

    [Then(@"^every step quality is N/A$")]
    public void ThenEveryStepQualityIsNotApplicable()
    {
        JsonElement[] steps = _context.FileSteps;
        Assert.NotEmpty(steps);
        Assert.All(steps, step => Assert.Equal("N/A", TextOf(step.GetProperty("quality"), "label")));
    }
}