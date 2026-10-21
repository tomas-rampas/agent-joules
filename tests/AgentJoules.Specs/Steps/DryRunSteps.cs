using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-054: <c>run --dry-run</c> validates everything and makes no completion call. Scoped to the feature because sentences such as "the exit code is 2" are reused by other stories with
/// other meanings. The state lives in <see cref="DryRunContext"/>. Every scenario runs the real <c>run</c> command through the real command line harness: the parser, the preflight (keys,
/// models), the GPU check of a measured tier through the real NVML binding, the redacting writers and the deny-all handler are the real ones; the models routes are recording fakes.
/// </summary>
[Binding]
[Scope(Feature = "AJ-054 Add run --dry-run that validates everything and makes no completion call")]
public sealed class DryRunSteps(DryRunContext context)
{
    // A command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly string[] Separators = [", ", " and "];

    private readonly DryRunContext _context = context;

    private CliRunResult Result => _context.Result ?? throw new XunitException("The command has not run yet.");

    // The rule text a scenario's invalid value must produce: the allowed values of --tier, or the validator of --repeat.
    private static string RuleOf(string? arguments) =>
        arguments?.Contains("--tier bogus", StringComparison.Ordinal) == true ? "Must be one of: 'local' 'cloudgpu' 'nvidia' 'nebius' 'all'"
        : arguments?.Contains("--repeat 0", StringComparison.Ordinal) == true ? "--repeat must be 1 or more"
        : throw new XunitException("No rule is known for the command line " + arguments);

    private string[] StdoutLines => [.. Result.Stdout.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0)];

    [Given(@"^fake keys, fake models responses and a scripted GPU for tiers ""([^""]*)"", ""([^""]*)"" and ""([^""]*)""$")]
    public void GivenFakesForThreeTiers(string first, string second, string third)
    {
        // The three tiers are the ones --tier all selects in the committed configuration (cloudgpu is disabled there).
        string[] given = [first, second, third];
        Assert.Equal(DryRunContext.ShippedEnabledTiers(), given);

        _context.SetKeys();
        foreach (string tierName in new[] { first, second, third })
        {
            _context.RouteToFake(tierName);
        }

        _context.UseScriptedGpu();
    }

    [Given(@"^the run command$")]
    public void GivenTheRunCommand()
    {
        // Everything a run of nvidia needs, so an exit code 2 is the parser's verdict and nothing else.
        _context.SetKeys();
        _context.RouteToFake("nvidia");
    }

    [Given(@"^the GPU port reports no device$")]
    public void GivenTheGpuPortReportsNoDevice()
    {
        // The harness default GPU factory reports no GPU; the keys and the models routes of local and of the judge tier pass, so the GPU check decides the exit code.
        Assert.Null(_context.Setup.Gpu);
        _context.SetKeys();
        _context.RouteToFake("local");
        _context.RouteToFake("nvidia");
    }

    [Given(@"^fake keys and models responses for tier ""([^""]*)""$")]
    public void GivenFakeKeysAndModelsResponsesForTier(string tierName)
    {
        _context.SetKeys();
        _context.RouteToFake(tierName);
    }

    [When(@"^""([^""]*)"" is executed$")]
    public async Task WhenCommandIsExecuted(string arguments) => await _context.RunAsync(arguments).WaitAsync(HangGuard);

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.Empty(Result.DeniedRequests);
        Assert.Equal(0, _context.Setup.Orchestrator.Runs);

        switch (expected)
        {
            case 2:
                // A parse error of the run command for the rule the scenario breaks, not an unrecognized command: no host was built and nothing was sent.
                Assert.Equal(0, _context.Setup.HostBuilds);
                Assert.All(_context.Handlers.Values, static handler => Assert.Equal(0, handler.RequestCount));
                Assert.Contains(RuleOf(_context.Arguments), Result.Stderr, StringComparison.Ordinal);
                Assert.DoesNotContain("Unrecognized command", Result.Stderr, StringComparison.Ordinal);
                break;
            case 6:
                // The GPU check refused the measured tier, naming what the GPU port reported, after the models check passed and before any completion call.
                string error = Assert.Single(Result.ErrorLines);
                Assert.EndsWith("(MeasurementPreconditionException)", error, StringComparison.Ordinal);
                Assert.Contains("no GPU is reachable under test overrides", error, StringComparison.Ordinal);
                Assert.All(_context.Handlers.Values, static handler => Assert.Equal(0, handler.Count(FakeRoute.ChatCompletions)));
                Assert.Empty(Result.Stdout);
                break;
        }
    }

    [Then(@"^each tier line shows the endpoint, the matched model IDs and ""OK""$")]
    public void ThenEachTierLineShowsTheEndpointTheIdsAndOk()
    {
        string[] expected = [.. DryRunContext.ShippedEnabledTiers().Select(tierName => $"{tierName} {_context.BaseUrlOf(tierName)} {DryRunContext.ShippedModelId(tierName)} OK")];

        Assert.Equal(expected, StdoutLines);
        Assert.Empty(Result.ErrorLines);
    }

    [Then(@"^the output lists (.+)$")]
    public void ThenTheOutputListsTheOptions(string list)
    {
        string[] options = list.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        string[] tokens = Result.Stdout.Split([' ', '\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, Result.ExitCode);
        Assert.Equal(7, options.Length);
        Assert.All(options, option => Assert.Contains(option, tokens));
    }

    [Then(@"^no recorded request path ends with ""([^""]*)""$")]
    public void ThenNoRecordedRequestPathEndsWith(string suffix)
    {
        RecordedRequest[] requests = [.. _context.Handlers.Values.SelectMany(static handler => handler.Requests)];

        // The dry run passed and asked the models route, so "no completion request" is the dry run's doing and not a command that never started.
        Assert.Equal(0, Result.ExitCode);
        Assert.Contains(requests, static request => request.Route == FakeRoute.Models);
        Assert.DoesNotContain(requests, request => request.Uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal));
        Assert.Empty(Result.DeniedRequests);
    }
}