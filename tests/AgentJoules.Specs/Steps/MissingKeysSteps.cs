using System.Text.RegularExpressions;

using AgentJoules.Core.Cli;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Runs;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-052: exit 3 before any call when an API key is missing. Scoped to the feature because sentences such as "the exit code is 3" and "the fake handler recorded 0 requests" are
/// reused by other stories with other meanings. The state lives in <see cref="MissingKeysContext"/>. Every scenario runs the stand-in <c>run</c> command of
/// <see cref="RunCommandStandIn"/> through the real command line harness (AJ-054 and AJ-056 build the real command later in the milestone and rewire the scenarios); the key
/// check, the provider pipeline, the redacting writers and the deny-all handler are the real ones.
/// </summary>
[Binding]
[Scope(Feature = "AJ-052 Exit 3 before any call when an API key is missing")]
public sealed class MissingKeysSteps(MissingKeysContext context)
{
    // A command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly MissingKeysContext _context = context;

    private CliRunResult Result => _context.Result ?? throw new XunitException("The command has not run yet.");

    private string ErrorMessage => Assert.Single(Result.ErrorLines);

    [Given(@"^fake keys for NVIDIA_API_KEY and NEBIUS_API_KEY in the configuration override$")]
    public void GivenFakeKeysForBothVariables()
    {
        _context.SetKey("NVIDIA_API_KEY");
        _context.SetKey("NEBIUS_API_KEY");

        // `--tier all` checks the models route of local, nvidia and nebius (AJ-053); the recording fake answers each of them. Only this scenario routes every tier, so the scenarios that
        // route one tier on purpose can still see a request that goes somewhere else.
        _context.RouteUnroutedToFake();
    }

    [Given(@"^NEBIUS_API_KEY is present and the judge key is absent$")]
    public void GivenNebiusKeyPresentAndJudgeKeyAbsent()
    {
        _context.SetKey("NEBIUS_API_KEY");
        Assert.False(_context.Settings.ContainsKey("NVIDIA_API_KEY"));
    }

    [Given(@"^an empty environment$")]
    public void GivenAnEmptyEnvironment() => Assert.DoesNotContain(_context.Settings.Keys, key => key.EndsWith("_API_KEY", StringComparison.Ordinal));

    [Given(@"^a runtime-built key starting with ""nvapi-"" and an endpoint that refuses the connection$")]
    public void GivenARuntimeBuiltKeyAndARefusingEndpoint() => _context.UseLeakingNvidiaKey();

    [Given(@"^tier ""([^""]*)"" selected and NEBIUS_API_KEY absent$")]
    public void GivenTierSelectedAndNebiusKeyAbsent(string tier)
    {
        Assert.Equal("nebius", tier);
        Assert.False(_context.Settings.ContainsKey("NEBIUS_API_KEY"));

        // The judge key is present so the only missing variable is the one the scenario names; the recording endpoint proves that nothing was sent.
        _context.SetKey("NVIDIA_API_KEY");
        _context.RouteToFake("nebius");
    }

    [Given(@"^tier ""([^""]*)"" enabled with RequireSshVariables (true|false) and (CLOUDGPU_SSH_HOST|CLOUDGPU_SSH_USER) absent$")]
    public void GivenTierEnabledWithRequireSshVariablesAndOneAbsent(string tier, string require, string absent)
    {
        Assert.Equal("cloudgpu", tier);
        _context.Settings["AgentJoules:Tiers:cloudgpu:Enabled"] = "true";
        _context.Settings["AgentJoules:CloudGpu:RequireSshVariables"] = require;
        _context.SetKey("NVIDIA_API_KEY");

        // The other SSH variable is present, so the message must name exactly the absent one.
        string present = absent == "CLOUDGPU_SSH_HOST" ? "CLOUDGPU_SSH_USER" : "CLOUDGPU_SSH_HOST";
        _context.Settings[present] = MissingKeysContext.FakeKey();
    }

    [Given(@"^a harness run with fake keys and a fake models handler$")]
    public void GivenAHarnessRunWithFakeKeysAndAFakeModelsHandler()
    {
        _context.SetKey("NVIDIA_API_KEY");
        _context.SetKey("NEBIUS_API_KEY");
        _context.RouteToFake("nvidia");
    }

    [Given(@"^a harness run where a tier base URL has no fake route$")]
    public void GivenAHarnessRunWhereABaseUrlHasNoFakeRoute()
    {
        _context.SetKey("NVIDIA_API_KEY");
        _context.SetKey("NEBIUS_API_KEY");
        Assert.False(_context.HasRoute("nebius"));
    }

    [When(@"^""([^""]*)"" is executed$")]
    public async Task WhenCommandIsExecuted(string arguments) => await _context.RunAsync(arguments).WaitAsync(HangGuard);

    [When(@"^""([^""]*)"" is executed with fake endpoints$")]
    public async Task WhenCommandIsExecutedWithFakeEndpoints(string arguments)
    {
        _context.RouteToFake("nvidia");
        _context.RouteToFake("nebius");
        await _context.RunAsync(arguments).WaitAsync(HangGuard);
    }

    [When(@"^a request to that URL is attempted$")]
    public async Task WhenARequestToThatUrlIsAttempted() => await _context.RunAsync("run --tier nebius").WaitAsync(HangGuard);

    [Then(@"^the key check passes and the exit code is not 3$")]
    public void ThenTheKeyCheckPasses()
    {
        Assert.NotEqual(3, Result.ExitCode);
        Assert.True(Result.ExitCode == 0, $"Expected the dry run to end with exit code 0 but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.DoesNotContain("user-secrets", Result.Stderr, StringComparison.Ordinal);
    }

    [Then(@"^a warning names the judge key and the exit code is not 3$")]
    public void ThenAWarningNamesTheJudgeKey()
    {
        Assert.NotEqual(3, Result.ExitCode);
        Assert.True(Result.ExitCode == 0, $"Expected exit code 0 but got {Result.ExitCode}; stderr: {Result.Stderr}");

        string warning = Assert.Single(Result.StderrLines, line => line.Length > 0);
        Assert.StartsWith("Warning:", warning, StringComparison.Ordinal);
        Assert.Contains("NVIDIA_API_KEY", warning, StringComparison.Ordinal);

        // The one completion request went out; the models route was asked once for the tier, and not for the judge whose key is missing.
        Assert.Equal(1, _context.Handler.Count(FakeRoute.ChatCompletions));
        Assert.Equal(1, _context.Handler.Count(FakeRoute.Models));
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected) =>
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stderr: {Result.Stderr}");

    [Then(@"^one message names ""([^""]*)"" and ""([^""]*)""$")]
    public void ThenOneMessageNames(string first, string second)
    {
        // Exactly one stderr line, and it is the error line: a single message, written once.
        string message = ErrorMessage;
        _ = Assert.Single(Result.StderrLines, line => line.Length > 0);
        Assert.Contains(first, message, StringComparison.Ordinal);
        Assert.Contains(second, message, StringComparison.Ordinal);
        Assert.Contains(nameof(MissingKeyException), message, StringComparison.Ordinal);
    }

    [Then(@"^the message shows the user-secrets alternative with the placeholder ""([^""]*)""$")]
    public void ThenTheMessageShowsTheAlternative(string placeholder)
    {
        string message = ErrorMessage;
        Assert.Contains(MissingKeyMessage.UserSecretsAlternative, message, StringComparison.Ordinal);
        Assert.Contains(placeholder, message, StringComparison.Ordinal);
        Assert.DoesNotContain(MissingKeysContext.FakeKey(), Result.Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the message names ""([^""]*)""$")]
    public void ThenTheMessageNames(string variable)
    {
        string message = ErrorMessage;
        Assert.Contains(variable, message, StringComparison.Ordinal);

        // The other SSH variable is present, so it is not named.
        string other = variable == "CLOUDGPU_SSH_HOST" ? "CLOUDGPU_SSH_USER" : "CLOUDGPU_SSH_HOST";
        Assert.DoesNotContain(other, message, StringComparison.Ordinal);
        Assert.DoesNotContain(MissingKeysContext.FakeKey(), Result.Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the output does not contain any character of the key after the nvapi- prefix$")]
    public void ThenTheOutputHoldsNothingOfTheKey()
    {
        string key = _context.NvidiaKey ?? throw new XunitException("No key was built.");
        CredentialEchoingRefusalHandler refusal = _context.Refusal ?? throw new XunitException("No refusing endpoint was registered.");
        string suffix = key["nvapi-".Length..];
        string output = Result.Stdout + "\n" + Result.Stderr;

        // The key was sent and the endpoint echoed it, so the failure text had it to leak; the models route was answered and the run failed on the refused completion request.
        Assert.Equal(key, refusal.LastBearerToken);
        Assert.Equal(1, refusal.ModelsRequests);
        Assert.Equal(1, refusal.Attempts);
        Assert.NotEqual(0, Result.ExitCode);
        Assert.NotEmpty(Result.ErrorLines);

        Assert.DoesNotContain(key, output, StringComparison.Ordinal);
        Assert.DoesNotContain(suffix, output, StringComparison.Ordinal);
        for (int start = 0; start + 5 <= suffix.Length; start++)
        {
            Assert.DoesNotContain(suffix.Substring(start, 5), output, StringComparison.Ordinal);
        }

        // Every occurrence of the public prefix is followed by the mask, nothing else.
        Assert.DoesNotMatch(new Regex(@"nvapi-(?!\*{4})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)), output);
        Assert.Contains("nvapi-****", output, StringComparison.Ordinal);
    }

    [Then(@"^the fake handler recorded (\d+) requests$")]
    public void ThenTheFakeHandlerRecordedRequests(int expected)
    {
        Assert.True(_context.HasRoute("nebius"), "The scenario registered no recording endpoint.");
        Assert.Equal(expected, _context.Handler.RequestCount);
        Assert.Empty(Result.DeniedRequests);
    }

    [Then(@"^the deny-all handler recorded (\d+) real requests$")]
    public void ThenTheDenyAllHandlerRecordedRealRequests(int expected)
    {
        Assert.Equal(0, Result.ExitCode);
        Assert.Equal(expected, Result.DeniedRequests.Count);

        // A dry run asks the models route (AJ-053) once, for the tier and the judge (both nvidia), at the nvidia base URL, and makes no completion call; nothing else was sent anywhere.
        string baseUrl = _context.Settings["AgentJoules:Tiers:nvidia:BaseUrl"] ?? throw new XunitException("The scenario set no base URL for nvidia.");
        RecordedRequest request = Assert.Single(_context.Handler.Requests);
        Assert.Equal(FakeRoute.Models, request.Route);
        Assert.Equal(baseUrl + "/models", request.Uri.AbsoluteUri);
    }

    [Then(@"^the deny-all handler throws and the message names the URL$")]
    public void ThenTheDenyAllHandlerThrowsAndNamesTheUrl()
    {
        string baseUrl = _context.Settings[MissingKeysContext.NebiusBaseUrlKey] ?? throw new XunitException("The scenario set no base URL for nebius.");
        string url = baseUrl + "/models";

        Assert.Equal([url], Result.DeniedRequests);
        Assert.Equal(1, Result.ExitCode);
        Assert.Contains(url, ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", Result.Stderr, StringComparison.Ordinal);
    }
}