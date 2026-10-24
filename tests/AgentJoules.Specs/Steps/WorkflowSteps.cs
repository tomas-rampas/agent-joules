using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-104: report Ollama failures with the exact fix command (EDGE-009). Scoped to the feature because sentences such as "the exit code is 5" and "the output contains ..." are reused by other stories with other
/// meanings. The state lives in <see cref="OllamaFailuresContext"/>. Every scenario runs the real <c>run</c> command through the real command line harness against a loopback fake that stands where Ollama stands,
/// so the Ollama check, the model check, the provider pipeline and the redacting writers are the real ones.
/// </summary>
[Binding]
[Scope(Feature = "Report Ollama failures with the exact fix command")]
public sealed class WorkflowSteps(OllamaFailuresContext context)
{
    // A command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly OllamaFailuresContext _context = context;

    private CliRunResult Result => _context.Result ?? throw new XunitException("The command has not run yet.");

    private string Output => Result.Stdout + "\n" + Result.Stderr;

    [Given(@"^the fake Ollama endpoint answers /api/version and lists ""([^""]*)""$")]
    public void GivenTheFakeOllamaEndpointAnswersAndLists(string tag)
    {
        Assert.Equal(OllamaFixtures.StoryTag, tag);
        _context.Tags.Add(tag);
    }

    [Given(@"^the fake endpoint lists (\d+) tags including ""([^""]*)""$")]
    public void GivenTheFakeEndpointListsManyTags(int total, string tag)
    {
        Assert.Equal(OllamaFixtures.StoryTag, tag);
        _context.Tags.AddRange(Enumerable.Range(1, total - 1).Select(static index => $"acme-model-{index:00}:7b"));
        _context.Tags.Insert(total / 2, tag);
        Assert.Equal(total, _context.Tags.Count);
    }

    [Given(@"^the fake endpoint refuses connections$")]
    public void GivenTheFakeEndpointRefusesConnections()
    {
        _context.Tags.Add(OllamaFixtures.StoryTag);
        _context.Refuse = true;
    }

    [Given(@"^the fake endpoint never answers within the configured timeout$")]
    public void GivenTheFakeEndpointNeverAnswers()
    {
        _context.Tags.Add(OllamaFixtures.StoryTag);
        _context.Stall = true;
    }

    [Given(@"^the fake endpoint lists tags that do not include ""([^""]*)""$")]
    public void GivenTheFakeEndpointListsOtherTags(string tag)
    {
        Assert.Equal(OllamaFixtures.StoryTag, tag);
        _context.Tags.AddRange(["acme-other:1b", "acme-third:7b"]);
        Assert.DoesNotContain(tag, _context.Tags);
    }

    [Given(@"^Tiers:local:ModelId is ""([^""]*)"" and the endpoint does not list it$")]
    public void GivenTheConfiguredTagIsNotListed(string tag)
    {
        _context.ConfiguredTag = tag;
        _context.Tags.Add(OllamaFixtures.StoryTag);
        Assert.DoesNotContain(tag, _context.Tags);
    }

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments) => await _context.RunAsync(arguments).WaitAsync(HangGuard);

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.Empty(Result.DeniedRequests);
    }

    [Then(@"^the output contains ""([^""]*)""$")]
    public void ThenTheOutputContains(string text) => Assert.Contains(text, Output, StringComparison.Ordinal);

    [Then(@"^the output contains the configured endpoint and ""([^""]*)""$")]
    public void ThenTheOutputContainsTheConfiguredEndpointAnd(string text)
    {
        // The message names the native base of the configured endpoint (the base URL without its /v1 segment), which is what the user starts and what the check asked.
        string native = OllamaFixtures.NativeBaseUrl();
        Assert.StartsWith(native, OllamaFixtures.LocalBaseUrl(), StringComparison.Ordinal);
        Assert.Contains(native, Output, StringComparison.Ordinal);
        Assert.Contains(text, Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), Output, StringComparison.Ordinal);
    }

    [Then(@"^no request ended in ""([^""]*)""$")]
    public void ThenNoRequestEndedIn(string suffix)
    {
        FakeHttpMessageHandler endpoint = _context.Endpoint ?? throw new XunitException("The command has not run yet.");

        // The fake was asked (the check ran), and none of what it recorded ends in the completions route.
        Assert.True(endpoint.RequestCount >= 1, "The fake endpoint was never asked.");
        Assert.DoesNotContain(endpoint.Requests, request => request.Uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal));
        Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), Output, StringComparison.Ordinal);
    }
}