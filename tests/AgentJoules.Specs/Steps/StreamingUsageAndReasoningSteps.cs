using System.Text.RegularExpressions;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Metering;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-108: the streaming usage behaviour and the reasoning toggle of every tier are recorded in <c>docs/endpoints.md</c> and agree with <c>config/appsettings.json</c> (REQ-018, EDGE-007). The scenarios about "the builder"
/// follow the AJ-101 pattern: the Given step evaluates <see cref="IntegrationGate.SkipReason"/> for AGENTJOULES_INTEGRATION (and reads, never sets and never prints, the named key variable); when the gate is closed or the
/// key is absent the remaining steps record the reason and pass. With them open they assert the content of the committed <c>docs/endpoints.md</c> against the shipped configuration and never perform the builder's
/// action: no call to a hosted endpoint is made here (the live calls are the gated <c>StreamingUsageTests</c>), and nothing is written. A scenario that names a literal value (<c>Streaming usage: reported</c>, <c>Reasoning result: off</c>) fails when the committed record says another value; the nebius scenario checks the record
/// against the shipped configuration (the toggle and the result), as every open-gate scenario does. The Ollama scenario asserts the record only when the committed decision of <c>docs/nim-local-attempt.md</c> is <c>ollama</c>; its metered-call step runs on every run because it needs no endpoint. The
/// scenario about a missing key needs neither the gate nor a key: it runs the real command line through the harness, whose configuration is in memory and whose HTTP is deny-all, so no process variable is read and no
/// request can leave. The record formats the open-gate steps read are listed on <see cref="StreamingUsageAndReasoningContext"/>. Scoped to the feature because "the exit code is 3" and "I run ..." are sentences of every CLI story.
/// </summary>
[Binding]
[Scope(Feature = "Verify streaming usage and the reasoning toggle per tier")]
public sealed partial class StreamingUsageAndReasoningSteps(StreamingUsageAndReasoningContext context, UsageReportingContext metering, IReqnrollOutputHelper output)
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    private static readonly string[] HostedTiers = ["nvidia", "nebius"];
    private static readonly IReadOnlyList<ChatMessage> Messages = [new ChatMessage(ChatRole.User, "What is 2+2?")];

    private readonly StreamingUsageAndReasoningContext _context = context;
    private readonly UsageReportingContext _metering = metering;
    private readonly IReqnrollOutputHelper _output = output;

    private string _tier = string.Empty;
    private string[] _lines = [];
    private CliRunResult? _run;
    private RunCommandSetup? _setup;

    // True when the step must assert; otherwise the reason is written to the scenario output.
    private bool Asserting()
    {
        if (!_context.IsActive)
        {
            _output.WriteLine("skipped: " + _context.Note);
            return false;
        }

        return true;
    }

    private void Load(string tier)
    {
        _tier = tier;
        _lines = HostedEndpointRecordContext.LinesOf(tier);
    }

    private TierOptions Configured() => ConfigurationFixtures.ShippedOptions().Tiers[_tier];

    [Given(@"^(NVIDIA_API_KEY|NEBIUS_API_KEY) is set$")]
    public void GivenTheKeyIsSet(string variable) => _context.Open(variable);

    [Given(@"^the local Ollama endpoint returns no usage in the final stream chunk$")]
    public void GivenTheOllamaEndpointReturnsNoUsage()
    {
        _context.OpenOllamaBranch();

        // The shape of an Ollama stream: the text chunks, then a closing chunk that holds the finish reason and no usage. The metered-call step needs no gate, so the stream is always prepared.
        _metering.Inner = new FakeChatClient(_metering.Time)
        {
            Updates = [new FakeUpdate("4", TimeSpan.FromMilliseconds(100)), new FakeUpdate(string.Empty, TimeSpan.FromMilliseconds(10))],
        };
    }

    [Given(@"^the response with the toggle still contains reasoning content$")]
    public void GivenTheResponseStillContainsReasoning() => _context.Open(keyVariable: null);

    [Given(@"^the endpoint answers HTTP 400 to the request containing the toggle JSON$")]
    public void GivenTheEndpointRejectsTheField() => _context.Open(keyVariable: null);

    [Given(@"^NVIDIA_API_KEY is not set$")]
    public void GivenNvidiaKeyIsNotSet()
    {
        // The harness configuration is in memory and the process environment is not a source of it, so the key is absent whatever the machine holds; the nebius key is present so that the one missing variable is the named one.
        _setup = RunCommandSetup.Create([new KeyValuePair<string, string?>("NEBIUS_API_KEY", ProviderClientFixtures.FakeKey())]);
        Assert.False(_setup.Settings.ContainsKey("NVIDIA_API_KEY"));
    }

    [When(@"^one streaming call is made with include_usage and the reasoning toggle JSON$")]
    public void WhenOneStreamingCallIsMadeAgainstNvidia()
    {
        if (Asserting())
        {
            Load("nvidia");
        }
    }

    [When(@"^the same two calls are made against the nebius tier$")]
    public void WhenTheSameTwoCallsAreMadeAgainstNebius()
    {
        if (Asserting())
        {
            Load("nebius");
        }
    }

    [When(@"^the builder records the local section$")]
    public void WhenTheBuilderRecordsTheLocalSection()
    {
        if (Asserting())
        {
            Load("local");
        }
    }

    [When(@"^the builder records the tier section$")]
    public void WhenTheBuilderRecordsTheTierSection()
    {
        // The tier is not named by the scenario: the Then step looks at every tier whose record has the branch the Given step describes.
        _ = Asserting();
    }

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments)
    {
        RunCommandSetup setup = _setup ?? throw new XunitException("The scenario named no configuration.");
        _run = await RunCommandHarness.RunAsync(arguments, setup).WaitAsync(HangGuard);
    }

    [Then(@"^""Streaming usage: (reported|not-reported)"" is recorded with the token counts$")]
    public void ThenTheUsageIsRecordedWithTheTokenCounts(string expected)
    {
        if (!Asserting())
        {
            return;
        }

        string recorded = StreamingUsageAndReasoningContext.AssertUsageRecord(_tier, _lines);
        Assert.True(recorded == expected, $"The committed record of ## {_tier} says {recorded}, not {expected}.");
    }

    [Then(@"^""Reasoning result: (off|on|unsupported)"" is recorded with the evidence$")]
    public void ThenTheResultIsRecordedWithTheEvidence(string expected)
    {
        if (!Asserting())
        {
            return;
        }

        string recorded = StreamingUsageAndReasoningContext.AssertReasoningRecord(_tier, _lines, Configured());
        Assert.True(recorded == expected, $"The committed record of ## {_tier} says {recorded}, not {expected}.");
    }

    [Then(@"^its section records the usage behaviour, the toggle JSON and the result$")]
    public void ThenTheSectionRecordsUsageToggleAndResult()
    {
        if (!Asserting())
        {
            return;
        }

        _ = StreamingUsageAndReasoningContext.AssertUsageRecord(_tier, _lines);
        _ = StreamingUsageAndReasoningContext.AssertReasoningRecord(_tier, _lines, Configured());
    }

    [Then(@"^""Streaming usage: not-reported"" is recorded$")]
    public void ThenTheLocalUsageIsNotReported()
    {
        if (Asserting())
        {
            Assert.Equal("not-reported", StreamingUsageAndReasoningContext.AssertUsageRecord(_tier, _lines));
        }
    }

    [Then(@"^docs/build-log\.md records the ADR-09 decision$")]
    public void ThenTheBuildLogRecordsTheDecision()
    {
        if (Asserting())
        {
            string log = StreamingUsageAndReasoningContext.ReadBuildLog() ?? throw new XunitException("docs/build-log.md does not exist.");
            Assert.Matches(Adr09Decision(), log);
        }
    }

    [Then(@"^a metered local call shows tokens labelled ""not-reported"" and never 0$")]
    public async Task ThenAMeteredLocalCallShowsNotReported()
    {
        // Not gated: the stream is the fake of the Given step, so this asserts the code path that the Ollama decision (tokens not-reported for local streaming) relies on, on every run.
        MeteredChatClient client = await _metering.CreateClientAsync();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Messages).WithCancellation(new CancellationTokenSource(HangGuard).Token))
        {
            _ = update;
        }

        StepReading reading = _metering.SingleReading();
        foreach (Metric metric in new[] { reading.InputTokens, reading.OutputTokens })
        {
            Assert.Equal(MetricLabel.NotReported, metric.Label);
            Assert.Null(metric.Value);
            Assert.Equal("provider-omitted-usage", metric.Reason);
        }
    }

    [Then(@"^""Reasoning result: unsupported"" is recorded and Tiers:<tier>:ReasoningMode is ""unsupported""$")]
    public void ThenAnIgnoredToggleIsRecordedAsUnsupported()
    {
        if (!Asserting())
        {
            return;
        }

        ShippedTiers().ToList().ForEach(AssertUnsupportedWhenRecorded);
    }

    [Then(@"^the section records ""Reasoning toggle: unsupported"" with the error excerpt and no key value$")]
    public void ThenARejectedToggleIsRecordedWithTheExcerpt()
    {
        if (!Asserting())
        {
            return;
        }

        foreach (string tier in ShippedTiers())
        {
            string[] lines = HostedEndpointRecordContext.LinesOf(tier);
            string? evidence = StreamingUsageAndReasoningContext.ValueOf(lines, StreamingUsageAndReasoningContext.EvidenceLabel);
            if (StreamingUsageAndReasoningContext.ValueOf(lines, StreamingUsageAndReasoningContext.ToggleLabel) != "unsupported" || evidence is null || !evidence.Contains("400", StringComparison.Ordinal))
            {
                _output.WriteLine($"## {tier}: the record does not show an endpoint that rejected the field with HTTP 400");
                continue;
            }

            Assert.Equal("unsupported", StreamingUsageAndReasoningContext.AssertReasoningRecord(tier, lines, ConfigurationFixtures.ShippedOptions().Tiers[tier]));

            // The assertions name the finding, never the text: a failure message must not repeat a key.
            string text = string.Join('\n', lines);
            string? key = HostedTiers.Contains(tier, StringComparer.Ordinal) ? KeyOf(tier) : null;
            Assert.False(key is not null && text.Contains(key, StringComparison.Ordinal), $"The section ## {tier} contains the value of a key variable.");
            Assert.False(KeyShapedValue().IsMatch(text), $"The section ## {tier} contains a value shaped like an nvapi key.");
        }
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        CliRunResult run = _run ?? throw new XunitException("The command has not run yet.");

        Assert.True(expected == run.ExitCode, $"Expected exit code {expected} but got {run.ExitCode}; stderr: {run.Stderr}");
        Assert.Empty(run.DeniedRequests);
    }

    [Then(@"^the output names ""([^""]*)""$")]
    public void ThenTheOutputNamesTheVariable(string variable)
    {
        CliRunResult run = _run ?? throw new XunitException("The command has not run yet.");

        string message = Assert.Single(run.ErrorLines);
        Assert.Contains(variable, message, StringComparison.Ordinal);
        Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), run.Stderr, StringComparison.Ordinal);
    }

    // The tiers of the shipped configuration that the committed record has a reasoning result for, in the fixed order of a run.
    private static IEnumerable<string> ShippedTiers() =>
        ConfigurationFixtures.ShippedOptions().Tiers.Keys.Where(static name => HasReasoningRecord(name)).OrderBy(static name => TierOptions.KnownNames.ToList().IndexOf(name));

    private static bool HasReasoningRecord(string tier) =>
        StreamingUsageAndReasoningContext.TryLinesOf(tier) is { } lines && StreamingUsageAndReasoningContext.ValueOf(lines, StreamingUsageAndReasoningContext.ResultLabel) is not null;

    private void AssertUnsupportedWhenRecorded(string tier)
    {
        string[] lines = HostedEndpointRecordContext.LinesOf(tier);
        if (StreamingUsageAndReasoningContext.ValueOf(lines, StreamingUsageAndReasoningContext.ResultLabel) != "unsupported")
        {
            _output.WriteLine($"## {tier}: the toggle works, so the unsupported branch has nothing to assert");
            return;
        }

        TierOptions configured = ConfigurationFixtures.ShippedOptions().Tiers[tier];
        Assert.Equal("unsupported", StreamingUsageAndReasoningContext.AssertReasoningRecord(tier, lines, configured));
        Assert.Equal("unsupported", configured.ReasoningMode);
    }

    private static string? KeyOf(string tier)
    {
        string? variable = ConfigurationFixtures.ShippedOptions().Tiers[tier].ApiKeyVariable;
        return variable is null ? null : Environment.GetEnvironmentVariable(variable);
    }

    // A line of the build log that states the decision: ADR-09 together with the outcome (tokens not-reported for local streaming, or buffered local calls).
    [GeneratedRegex(@"^.*ADR-09.*(not-reported|buffered).*$|^.*(not-reported|buffered).*ADR-09.*$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex Adr09Decision();

    [GeneratedRegex(@"nvapi-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyShapedValue();
}