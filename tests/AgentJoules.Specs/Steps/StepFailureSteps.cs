using System.Text.Json;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Json;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Agents;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-057: stop on a failed step, keep the partial results and exit 5. Scoped to the feature because sentences such as "the exit code is 5" are reused by other stories with other meanings.
/// The state lives in <see cref="StepFailureContext"/>. Every scenario runs the real <c>run</c> command with the production orchestrator and the real retry layer through the real command
/// line harness, on a fake clock, so the five attempts of an exhausted 429 take no wall-clock time; the endpoints are recording fakes.
/// </summary>
[Binding]
[Scope(Feature = "AJ-057 Stop on a failed step, keep partial results and exit 5")]
public sealed class StepFailureSteps(StepFailureContext context)
{
    // A command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private static readonly string[] MetricFields = ["inputTokens", "outputTokens", "grossJoules", "netJoules"];

    // The attempts the retry layer makes at a 429 before it gives up (EDGE-006).
    private const int RetryAttempts = 5;

    private static readonly string[] AbsentLabels = ["not-reported", "N/A"];

    private readonly StepFailureContext _context = context;

    private CliRunResult Result => _context.Result ?? throw new XunitException("The command has not run yet.");

    [Given(@"^a nvidia run where the Reviewer of the second task fails after the first task completed$")]
    public void GivenTheReviewerOfTheSecondTaskFails()
    {
        // Task 1 is four ok calls; task 2 answers its Planner, Researcher and Writer and fails at its Reviewer, the eighth call.
        Assert.True(_context.Tasks.Count >= 3, "The scenario needs the three committed tasks.");
        _ = _context.Route("nvidia", 200, 200, 200, 200, 200, 200, 200, 500);
    }

    [Given(@"^tiers ""([^""]*)"" and ""([^""]*)"" where nebius answers 429 on every call$")]
    public void GivenTiersWhereNebiusAnswers429(string first, string second)
    {
        Assert.Equal(("nvidia", "nebius"), (first, second));

        // --tier all expands to the enabled tiers: with local disabled that is nvidia and nebius, in that order.
        _context.Setting("AgentJoules:Tiers:local:Enabled", "false");
        _ = _context.Route(first);
        _ = _context.Route(second, 429);
        _context.FailedTier = second;
        _context.ExpectedFailedRequests = RetryAttempts;
    }

    [Given(@"^an endpoint that answers 429 on every call$")]
    public void GivenAnEndpointThatAnswers429()
    {
        _ = _context.Route("nvidia", 429);
        _ = _context.Route("nebius", 429);
        _context.FailedTier = "nvidia";
        _context.ExpectedFailedRequests = RetryAttempts;
    }

    [Given(@"^an endpoint that answers 500$")]
    public void GivenAnEndpointThatAnswers500()
    {
        _ = _context.Route("nvidia", 500);
        _ = _context.Route("nebius", 500);
        _context.FailedTier = "nebius";
        _context.ExpectedFailedRequests = 1;
    }

    [Given(@"^a run that fails on its first step$")]
    public async Task GivenARunThatFailsOnItsFirstStep()
    {
        _ = _context.Route("nvidia", 500);
        await _context.RunAsync("run --tier nvidia --repeat 1").WaitAsync(HangGuard);
    }

    [When(@"^""([^""]*)"" is executed$")]
    public async Task WhenCommandIsExecuted(string arguments) => await _context.RunAsync(arguments).WaitAsync(HangGuard);

    [When(@"^the results file is read$")]
    public void WhenTheResultsFileIsRead()
    {
        Assert.Equal(5, Result.ExitCode);
        _context.Document = RunOrchestrationFixtures.Read(Assert.Single(_context.RunFiles()));
    }

    [Then(@"^the results file holds the (\d+) steps of the first task unchanged$")]
    public void ThenTheResultsFileHoldsTheStepsUnchanged(int count)
    {
        Assert.Equal(5, Result.ExitCode);

        // Task 2 ran all four agents of its workflow, the last of them failing: the file holds the kept steps and those four.
        // The first running checkpoint is what the writer was given after the first task; the file must hold those steps, to the character, and then the failure.
        RunDocument checkpoint = Assert.Single(_context.Recorder.Documents, static document => document.Run.Status == RunStatus.Running);
        Assert.Equal(count, checkpoint.Steps.Count);
        using JsonDocument expected = JsonDocument.Parse(JsonSerializer.Serialize(checkpoint, AgentJoulesJsonContext.Default.RunDocument));
        using JsonDocument file = _context.ReadFile("nvidia");

        JsonElement[] before = RunOrchestrationFixtures.Steps(expected);
        JsonElement[] kept = RunOrchestrationFixtures.Steps(file);
        Assert.Equal("failed", file.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal(count + AgentCatalog.Names.Count, kept.Length);
        Assert.Equal(before.Select(static step => step.GetRawText()), kept.Take(count).Select(static step => step.GetRawText()));
        Assert.All(kept.Take(count), step => Assert.Equal(_context.Tasks[0].Name, step.GetProperty("task").GetString()));
        Assert.Equal(("Reviewer", "error"), (kept[^1].GetProperty("agent").GetString(), kept[^1].GetProperty("outcome").GetString()));
    }

    [Then(@"^the (\w+) run file exists with status ""([^""]*)""$")]
    public void ThenTheRunFileExistsWithStatus(string tierName, string status)
    {
        using JsonDocument document = _context.ReadFile(tierName);
        Assert.Equal(status, document.RootElement.GetProperty("run").GetProperty("status").GetString());
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.Empty(Result.DeniedRequests);
        if (expected == 5)
        {
            Assert.EndsWith("(StepFailedException)", Assert.Single(Result.ErrorLines), StringComparison.Ordinal);
        }

        if (_context.FailedTier is { } failedTier)
        {
            // The tier that failed has its partial file next to the files of the tiers before it: one failed step and the attempts the scenario expects behind it.
            using JsonDocument document = _context.ReadFile(failedTier);
            Assert.Equal("failed", document.RootElement.GetProperty("run").GetProperty("status").GetString());
            Assert.Equal("error", Assert.Single(RunOrchestrationFixtures.Steps(document)).GetProperty("outcome").GetString());
            Assert.Equal(_context.ExpectedFailedRequests, _context.Handlers[failedTier].Count(FakeRoute.ChatCompletions));
        }
    }

    [Then(@"^exactly (\d+) HTTP requests? (?:were|was) recorded(?: for the first step)?$")]
    public void ThenExactlyRequestsWereRecorded(int expected)
    {
        FakeHttpMessageHandler handler = _context.HandlerOf(_context.Arguments ?? throw new XunitException("The command has not run yet."));

        Assert.Equal(expected, handler.Count(FakeRoute.ChatCompletions));
    }

    [Then(@"^the results file has a step with outcome ""([^""]*)"" and run.status ""([^""]*)""$")]
    public void ThenTheResultsFileHasAStepWithOutcome(string outcome, string status)
    {
        using JsonDocument document = _context.ReadFile("nvidia");
        JsonElement step = Assert.Single(RunOrchestrationFixtures.Steps(document));

        Assert.Equal(outcome, step.GetProperty("outcome").GetString());
        Assert.Equal(status, document.RootElement.GetProperty("run").GetProperty("status").GetString());
    }

    [Then(@"^it contains (\d+) step and no step with a numeric 0 for tokens or joules$")]
    public void ThenItContainsOneStepAndNoNumericZero(int count)
    {
        JsonDocument document = _context.Document ?? throw new XunitException("The results file has not been read.");
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);

        Assert.Equal(count, steps.Length);
        Assert.Equal("error", steps[0].GetProperty("outcome").GetString());

        // A step that never reported a count or an energy carries a label, never a number: the tokens are not reported and the hosted energy is not applicable.
        foreach (JsonElement step in steps)
        {
            foreach (string field in MetricFields)
            {
                JsonElement metric = step.GetProperty(field);
                Assert.True(metric.ValueKind != JsonValueKind.Number, $"{field} is the number {metric.GetRawText()}.");
                Assert.Equal(JsonValueKind.Object, metric.ValueKind);
                Assert.Contains(metric.GetProperty("label").GetString(), AbsentLabels);
            }
        }
    }
}