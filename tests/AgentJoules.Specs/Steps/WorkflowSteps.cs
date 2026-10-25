using System.Text.Json;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
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


/// <summary>
/// AJ-105: disclose the local runtime, tag, offload and precision in every local run (REQ-020). Scoped to the feature because sentences such as "I run" and "the exit code is 6" are bound by the AJ-104 class above
/// for its own feature. The state lives in <see cref="LocalRuntimeDisclosureContext"/>. Every scenario runs the real <c>run --tier local</c> command through the harness on a fake clock against a loopback fake
/// that stands where Ollama or a NIM container stands, then reads the run file the command wrote and the header the report command renders from it.
/// </summary>
[Binding]
[Scope(Feature = "Disclose the local runtime, tag, offload and precision")]
public sealed class LocalRuntimeDisclosureSteps(LocalRuntimeDisclosureContext context)
{
    private readonly LocalRuntimeDisclosureContext _context = context;

    private CliRunResult Result => _context.Rig.Result ?? throw new XunitException("The command has not run yet.");

    [Given(@"^the fake /api/ps reports size_vram equal to size for the configured tag$")]
    public void GivenTheFakePsReportsFullOffload() => _context.VramPercent = 100;

    [Given(@"^Local:Runtime is ""([^""]*)"" and Tiers:local:Precision is ""([^""]*)""$")]
    public void GivenTheRuntimeAndThePrecision(string runtime, string precision)
    {
        Assert.Equal("nim", runtime);
        _context.Nim = true;
        _context.NimPrecision = precision;
    }

    [Given(@"^the fake /api/show reports quantization level ""([^""]*)""$")]
    public void GivenTheFakeShowReportsTheQuantizationLevel(string level) => _context.Quantization = level;

    [Given(@"^the fake /api/ps reports size_vram at (\d+) percent of size$")]
    public void GivenTheFakePsReportsAShare(int percent) => _context.VramPercent = percent;

    [Given(@"^the fake /api/ps lists no model after warm-up$")]
    public void GivenTheFakePsListsNoModel() => _context.NoModelListed = true;

    [Given(@"^the fake /api/ps reports size 0 for the configured tag$")]
    public void GivenTheFakePsReportsSizeZero() => _context.ZeroSize = true;

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments) => await _context.RunAsync(arguments);

    [Then(@"^environment\.localRuntime is ""([^""]*)"" and environment\.gpuOffloadPercent is (\d+)$")]
    public void ThenTheRuntimeAndTheOffload(string runtime, int offload)
    {
        AssertExit(0);
        using JsonDocument document = _context.ReadRunFile();
        JsonElement environment = document.RootElement.GetProperty("environment");
        Assert.Equal(runtime, environment.GetProperty("localRuntime").GetString());
        Assert.Equal((double)offload, environment.GetProperty("gpuOffloadPercent").GetDouble());
    }

    [Then(@"^environment\.gpuOffloadPercent is (\d+)$")]
    public void ThenTheOffloadIs(int offload)
    {
        AssertExit(0);
        using JsonDocument document = _context.ReadRunFile();
        Assert.Equal((double)offload, document.RootElement.GetProperty("environment").GetProperty("gpuOffloadPercent").GetDouble());
    }

    [Then(@"^environment\.precision is ""([^""]*)""$")]
    public void ThenThePrecisionIs(string precision)
    {
        AssertExit(0);
        using JsonDocument document = _context.ReadRunFile();
        Assert.Equal(precision, document.RootElement.GetProperty("environment").GetProperty("precision").GetString());
    }

    [Then(@"^run\.flags does not contain ""([^""]*)""$")]
    public void ThenTheFlagsDoNotContain(string flag)
    {
        using JsonDocument document = _context.ReadRunFile();
        Assert.DoesNotContain(flag, RunFlags(document));
    }

    [Then(@"^run\.flags contains ""([^""]*)""$")]
    public void ThenTheFlagsContain(string flag)
    {
        using JsonDocument document = _context.ReadRunFile();
        Assert.Contains(flag, RunFlags(document));
    }

    [Then(@"^the rendered report header contains ""([^""]*)""$")]
    public async Task ThenTheRenderedHeaderContains(string text)
    {
        // The header is the text above the table of summary.md; the HTML page renders the same lines.
        string summary = await _context.Rig.SummaryAsync();
        string header = summary[..summary.IndexOf("\n| ", StringComparison.Ordinal)];
        Assert.Contains("Flags: " + text, header, StringComparison.Ordinal);

        string html = await _context.Rig.HtmlAsync();
        Assert.Contains("Flags: " + text, html, StringComparison.Ordinal);
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected) => AssertExit(expected);

    [Then(@"^the output says the offload could not be determined$")]
    public void ThenTheOutputSaysTheOffloadCouldNotBeDetermined()
    {
        string output = Result.Stdout + "\n" + Result.Stderr;
        Assert.Contains("the GPU offload could not be determined", output, StringComparison.Ordinal);
        Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), output, StringComparison.Ordinal);
    }

    [Then(@"^the run file contains no gpuOffloadPercent value of 0$")]
    public void ThenTheRunFileHoldsNoZeroOffload()
    {
        // The run stopped before it measured anything, so there may be no file at all; a file, if one exists, must not state a 0 for a size that was never read.
        foreach (string path in _context.Rig.RunFiles())
        {
            using JsonDocument document = RunOrchestrationFixtures.Read(path);
            JsonElement offload = document.RootElement.GetProperty("environment").GetProperty("gpuOffloadPercent");
            Assert.True(offload.ValueKind == JsonValueKind.Null || offload.GetDouble() != 0.0, "A zero size must never be written as a 0 percent offload.");
        }
    }

    private static string[] RunFlags(JsonDocument document) => [.. document.RootElement.GetProperty("run").GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()!)];

    private void AssertExit(int expected)
    {
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.Empty(Result.DeniedRequests);
    }
}

/// <summary>
/// AJ-106: order warm-up, idle baseline and repeats in local runs (REQ-021). Scoped to the feature because sentences such as "I run" and "the exit code is 5" are bound by the classes above for their own features. The
/// state lives in <see cref="WarmupBaselineRepeatContext"/>; the file-selection scenario uses the <see cref="RunFileSelectionContext"/> of AJ-081. Every run scenario runs the real <c>run</c> command through the harness on
/// a fake clock against loopback fakes that stand where Ollama and the hosted tiers stand, and reads what the rig recorded (the order of the operations) and the run file the command wrote.
/// </summary>
[Binding]
[Scope(Feature = "Order warm-up, idle baseline and repeats in local runs")]
public sealed class WarmupBaselineRepeatSteps(WarmupBaselineRepeatContext context, RunFileSelectionContext selection)
{
    private const string OlderStamp = "20261022T093000Z";
    private const string NewerStamp = "20261023T093000Z";
    private const int AgentsPerRepeat = 4;
    private const int PromptTokensPerCall = 7;

    private readonly WarmupBaselineRepeatContext _context = context;
    private readonly RunFileSelectionContext _selection = selection;

    private CliRunResult Result => _context.Rig.Result ?? throw new XunitException("The command has not run yet.");

    private string Output => Result.Stdout + "\n" + Result.Stderr;

    [Given(@"^a local run with fakes recording every operation$")]
    public static void GivenALocalRunWithFakesRecordingEveryOperation()
    {
        // The rig records every operation of every run (the probe, the meter start, the warm-up, the baseline wait and each completion); nothing is prepared here.
    }

    [Given(@"^the Ollama runtime is used$")]
    public static void GivenTheOllamaRuntimeIsUsed()
    {
        // The rig of these scenarios is the Ollama rig (Local:Runtime ollama); nothing is prepared here.
    }

    [Given(@"^the fake power source detector returns ""([^""]*)""$")]
    public void GivenTheFakePowerSourceDetectorReturns(string value) => _context.PowerSource = value;

    [Given(@"^the fake Ollama endpoint refuses the warm-up request$")]
    public void GivenTheFakeOllamaEndpointRefusesTheWarmUpRequest() => _context.RefuseWarmUp = true;

    [Given(@"^a valid local run file and a newer local run file whose run\.flags contain ""non-publishable""$")]
    public void GivenAValidAndANewerNonPublishableLocalRunFile()
    {
        string valid = _selection.WriteRunFile(OlderStamp, Tier.Local, "completed", []);
        string newer = _selection.WriteRunFile(NewerStamp, Tier.Local, "completed", [RunFlagNames.NonPublishable]);
        _selection.Remember(newer, valid);
    }

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments) => await _context.RunAsync(arguments);

    [When(@"^the warm-up call is made$")]
    public async Task WhenTheWarmUpCallIsMade() => await _context.RunAsync(LocalRunRig.StoryCommand);

    [When(@"^the latest file for tier ""([^""]*)"" is selected$")]
    public void WhenTheLatestFileIsSelected(string tier)
    {
        Assert.Equal("local", tier);
        _selection.SelectLatest(Tier.Local);
    }

    [Then(@"^the recorded order is probe, meter start, warm-up, baseline window, task calls$")]
    public void ThenTheRecordedOrderIs()
    {
        AssertExit(0);
        Assert.Equal("probe, meter-start, warm-up, baseline, task-call x4", _context.Rig.Recorder.Describe());
    }

    [Then(@"^no step reading exists for the warm-up call$")]
    public void ThenNoStepReadingExistsForTheWarmUpCall()
    {
        // One repeat of one task is four steps, one per completion of the workflow; the warm-up call is a fifth request to the endpoint that is in no list of the file.
        using JsonDocument document = _context.ReadRunFile();
        JsonElement root = document.RootElement;
        Assert.Equal(AgentsPerRepeat, root.GetProperty("steps").GetArrayLength());
        Assert.Equal(AgentsPerRepeat, _context.Rig.Recorder.Count(OperationRecorder.TaskCall));
        Assert.Equal(1, _context.Rig.Recorder.Count(OperationRecorder.WarmUp));
        Assert.Equal(0, root.GetProperty("judge").GetArrayLength());
        Assert.Equal((double)(AgentsPerRepeat * PromptTokensPerCall), root.GetProperty("totals").GetProperty("inputTokens").GetProperty("value").GetDouble());
    }

    [Then(@"^the request body contains keep_alive ""([^""]*)"" and an empty prompt$")]
    public void ThenTheRequestBodyContainsKeepAliveAndAnEmptyPrompt(string keepAlive)
    {
        AssertExit(0);
        RecordedRequest request = _context.Rig.Local.RequestsFor(FakeRoute.OllamaGenerate)[0];
        System.Text.Json.Nodes.JsonObject body = request.Json ?? throw new XunitException("The warm-up request has no JSON body.");
        Assert.Equal(keepAlive, (string?)body["keep_alive"]);
        Assert.Equal(string.Empty, (string?)body["prompt"]);
        Assert.Equal(OllamaFixtures.StoryTag, (string?)body["model"]);
    }

    [Then(@"^environment\.modelLoadSeconds equals the fake warm-up duration$")]
    public void ThenTheModelLoadSecondsEqualTheFakeWarmUpDuration()
    {
        // Every distinct tag is warmed once and the scenario configures one tag, so the sum is the one scripted duration.
        using JsonDocument document = _context.ReadRunFile();
        Assert.Equal(WarmupBaselineRepeatContext.FakeWarmUp.TotalSeconds, document.RootElement.GetProperty("environment").GetProperty("modelLoadSeconds").GetDouble());
    }

    [Then(@"^the run file has (\d+) steps with repeat values 1, 2 and 3 four times each$")]
    public void ThenTheRunFileHasStepsWithRepeatValues(int steps)
    {
        AssertExit(0);
        using JsonDocument document = _context.ReadRunFile();
        int[] repeats = [.. document.RootElement.GetProperty("steps").EnumerateArray().Select(static step => step.GetProperty("repeat").GetInt32())];
        Assert.Equal(steps, repeats.Length);
        Assert.Equal([(1, 4), (2, 4), (3, 4)], repeats.GroupBy(static repeat => repeat).OrderBy(static group => group.Key).Select(static group => (group.Key, group.Count())));
    }

    [Then(@"^run\.repeat is (\d+)$")]
    public void ThenRunRepeatIs(int repeat)
    {
        using JsonDocument document = _context.ReadRunFile();
        Assert.Equal(repeat, document.RootElement.GetProperty("run").GetProperty("repeat").GetInt32());
    }

    [Then(@"^environment\.powerSource is ""([^""]*)""$")]
    public void ThenThePowerSourceIs(string value)
    {
        AssertExit(0);
        using JsonDocument document = _context.ReadRunFile();
        Assert.Equal(value, document.RootElement.GetProperty("environment").GetProperty("powerSource").GetString());
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected) => AssertExit(expected);

    [Then(@"^the output contains ""([^""]*)""$")]
    public void ThenTheOutputContains(string text) => Assert.Contains(text, Output, StringComparison.Ordinal);

    [Then(@"^baseline\.seconds is (\d+)$")]
    public void ThenBaselineSecondsIs(int seconds)
    {
        using JsonDocument document = _context.ReadRunFile();
        Assert.Equal((double)seconds, document.RootElement.GetProperty("baseline").GetProperty("seconds").GetDouble());
    }

    [Then(@"^run\.flags contains ""([^""]*)""$")]
    public void ThenTheFlagsContain(string flag)
    {
        using JsonDocument document = _context.ReadRunFile();
        Assert.Contains(flag, RunFlags(document));
    }

    [Then(@"^run\.flags does not contain ""([^""]*)""$")]
    public void ThenTheFlagsDoNotContain(string flag)
    {
        using JsonDocument document = _context.ReadRunFile();
        Assert.DoesNotContain(flag, RunFlags(document));
    }

    [Then(@"^the valid local run file is returned$")]
    public void ThenTheValidLocalRunFileIsReturned() => Assert.Equal(_selection.OlderName, _selection.LatestName());

    [Then(@"^no baseline window was opened$")]
    public void ThenNoBaselineWindowWasOpened()
    {
        // The refused warm-up ended the command before the meter waited on its baseline, and the endpoint saw no completion.
        Assert.Equal(0, _context.Rig.Recorder.Count(OperationRecorder.Baseline));
        Assert.Equal(0, _context.Rig.Recorder.Count(OperationRecorder.TaskCall));
        Assert.Equal(1, _context.Rig.Recorder.Count(OperationRecorder.WarmUp));
        Assert.Empty(_context.Rig.RunFiles());
    }

    [Then(@"^no warm-up call and no baseline window were recorded$")]
    public void ThenNoWarmUpCallAndNoBaselineWindowWereRecorded()
    {
        AssertExit(0);
        OperationRecorder recorder = _context.Rig.Recorder;
        Assert.Equal(0, recorder.Count(OperationRecorder.WarmUp));
        Assert.Equal(0, recorder.Count(OperationRecorder.Baseline));
        Assert.Equal(0, recorder.Count(OperationRecorder.Probe));
        Assert.Equal(AgentsPerRepeat, recorder.Count(OperationRecorder.TaskCall));
        Assert.Empty(_context.Rig.Local.Requests);
    }

    private static string[] RunFlags(JsonDocument document) => [.. document.RootElement.GetProperty("run").GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()!)];

    private void AssertExit(int expected)
    {
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stdout: {Result.Stdout}; stderr: {Result.Stderr}");
        Assert.Empty(Result.DeniedRequests);
    }
}