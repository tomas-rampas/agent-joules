using System.Text.Json;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Runs;
using AgentJoules.Dashboard;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-090: the serialization of the live events and the embedded page (REQ-032). Scoped to the feature because sentences such as "it is loaded" and "the event is serialized" belong to this story
/// alone, and a scoped binding wins inside this feature and never matches another one. The state lives in <see cref="LivePageContext"/>; the rules over the page source are the ones of
/// <see cref="DashboardPageRules"/>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-090 Serialize live events and embed the dashboard page")]
public sealed class LivePageSteps(LivePageContext context)
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private readonly LivePageContext _context = context;

    // ---- serialization ----

    [Given(@"^a fixture local step for agent ""([^""]*)"" with net joules measured$")]
    public void GivenAFixtureLocalStep(string agent) =>
        _context.Queue(new StepCompletedEvent(Origin, false, StepReadingFixtures.Metered(agent: agent, netJoules: Metric.Measured(60.0, "J"))));

    [When(@"^the event is serialized$")]
    public void WhenTheEventIsSerialized() => _context.ApplyQueued();

    [Then(@"^the payload contains ""([^""]*)"" and a netJoules object with label ""([^""]*)""$")]
    public void ThenThePayloadContains(string agent, string label)
    {
        string payload = _context.LastPayload;
        Assert.Contains($"\"{agent}\"", payload, StringComparison.Ordinal);
        JsonElement step = Data(payload).GetProperty("step");
        Assert.Equal(agent, step.GetProperty("agent").GetString());
        Assert.Equal(label, step.GetProperty("netJoules").GetProperty("label").GetString());
        Assert.Contains($"\"netJoules\":{{\"value\":60,\"label\":\"{label}\"", payload, StringComparison.Ordinal);
    }

    [Given(@"^a hosted step whose power metric has no value$")]
    public async Task GivenAHostedStepWhosePowerMetricHasNoValue()
    {
        _context.Queue(new PowerEvent(Origin, false, Metric.NotApplicable("hosted-tier"), 1.0));
        _context.Queue(new StepCompletedEvent(Origin, false, await StepReadingFixtures.HostedAsync(Tier.Nvidia, agent: "Writer")));
    }

    [Then(@"^the power metric has label ""([^""]*)"", reason ""([^""]*)"" and a null value$")]
    public void ThenThePowerMetricHasLabelReasonAndNull(string label, string reason)
    {
        Assert.Equal(2, _context.Payloads.Count);
        JsonElement watts = Data(_context.Payloads[0]).GetProperty("watts");
        AssertNullLabelled(watts, label, reason);
        JsonElement avgW = Data(_context.Payloads[1]).GetProperty("step").GetProperty("avgW");
        AssertNullLabelled(avgW, label, reason);
        Assert.Contains($"\"avgW\":{{\"value\":null,\"label\":\"{label}\",\"reason\":\"{reason}\"}}", _context.Payloads[1], StringComparison.Ordinal);
    }

    [Given(@"^a runtime-built key starting with ""nvapi-"" present in the configuration$")]
    public void GivenARuntimeBuiltKey() => _context.UseRedactedSnapshot();

    [When(@"^every event type is serialized$")]
    public void WhenEveryEventTypeIsSerialized() => _context.SerializeEveryEventType();

    [Then(@"^no payload contains any character of the key after the nvapi- prefix$")]
    public void ThenNoPayloadContainsTheKey()
    {
        string key = _context.Key ?? throw new InvalidOperationException("No key was built.");
        string material = key["nvapi-".Length..];
        Assert.Equal(9, _context.Payloads.Count);
        foreach (string payload in _context.Payloads)
        {
            Assert.DoesNotContain(key, payload, StringComparison.Ordinal);
            for (int start = 0; start + 6 <= material.Length; start++)
            {
                Assert.DoesNotContain(material.Substring(start, 6), payload, StringComparison.Ordinal);
            }
        }

        Assert.Contains(_context.Payloads, static payload => payload.Contains("nvapi-****", StringComparison.Ordinal));
    }

    [Given(@"^two completed steps of agent ""([^""]*)"" with net joules (\d+) and (\d+) \(fixture values\)$")]
    public void GivenTwoCompletedSteps(string agent, int first, int second)
    {
        _context.Queue(new StepCompletedEvent(Origin, false, StepReadingFixtures.Metered(agent: agent, netJoules: Metric.Measured(first, "J"))));
        _context.Queue(new StepCompletedEvent(Origin, false, StepReadingFixtures.Metered(agent: agent, netJoules: Metric.Measured(second, "J"))));
    }

    [When(@"^the second step event is serialized$")]
    public void WhenTheSecondStepEventIsSerialized() => _context.ApplyQueued();

    [Then(@"^cumulativeNetJoules is (\d+) with label ""([^""]*)""$")]
    public void ThenCumulativeNetJoulesIs(int expected, string label)
    {
        JsonElement cumulative = AgentOf(_context.LastPayload, "Writer").GetProperty("cumulativeNetJoules");
        Assert.Equal(label, cumulative.GetProperty("label").GetString());
        Assert.Equal(expected, cumulative.GetProperty("value").GetDouble(), 9);
        Assert.Equal("J", cumulative.GetProperty("unit").GetString());
    }

    [Given(@"^1500 events emitted with no reader so the earliest are dropped$")]
    public async Task GivenManyEventsEmittedWithNoReader() => await _context.EmitManyStepsAsync();

    [When(@"^the last step event is serialized$")]
    public void WhenTheLastStepEventIsSerialized()
    {
        // The connection holds the newest 1000 payloads; the 500 earliest were dropped, and the last one is the payload of the 1500th step.
        Assert.Equal(1000, _context.DrainConnection());
    }

    [Then(@"^its cumulative values equal the sum over all emitted steps$")]
    public void ThenItsCumulativeValuesEqualTheSum()
    {
        JsonElement writer = AgentOf(_context.LastPayload, "Writer");
        Assert.Equal(_context.EmittedNetJoules, writer.GetProperty("cumulativeNetJoules").GetProperty("value").GetDouble(), 9);
        Assert.Equal("measured", writer.GetProperty("cumulativeNetJoules").GetProperty("label").GetString());
        Assert.Equal(LivePageContext.ManySteps * 120.0, writer.GetProperty("cumulativeInputTokens").GetProperty("value").GetDouble(), 9);
        Assert.Equal(LivePageContext.ManySteps * 80.0, writer.GetProperty("cumulativeOutputTokens").GetProperty("value").GetDouble(), 9);

        // The oldest payload left is far into the run: the earlier ones are gone, the sum is not.
        Assert.True(AgentOf(_context.Payloads[0], "Writer").GetProperty("cumulativeNetJoules").GetProperty("value").GetDouble() > 500.0 * 501.0 / 2.0);
    }

    // ---- running estimates ----

    [Given(@"^power events of 60 W at 0\.0 s, 0\.1 s and 0\.2 s for the active step and a 20 W baseline mean$")]
    public void GivenPowerEventsOfSixtyWatts()
    {
        _context.Queue(new BaselineEvent(Origin, false, Baseline(20.0)));
        _context.Queue(Started("Writer", 0.0));
        foreach (double seconds in new[] { 0.0, 0.1, 0.2 })
        {
            _context.Queue(Power(60.0, seconds));
        }
    }

    [When(@"^the snapshot is serialized$")]
    public void WhenTheSnapshotIsSerialized()
    {
        _context.ApplyQueued();
        _context.Payloads.Add(_context.Snapshot.ToSnapshotPayload(Origin.AddSeconds(5)));
    }

    [Then(@"^the running energy is (\d+) with label ""([^""]*)""$")]
    public void ThenTheRunningEnergyIs(int expected, string label)
    {
        JsonElement running = AgentOf(_context.LastPayload, "Writer").GetProperty("runningNetJoules");
        Assert.Equal(label, running.GetProperty("label").GetString());
        Assert.Equal(expected, running.GetProperty("value").GetDouble(), 9);
    }

    [Given(@"^a step started at 1\.00 s and a first power sample at 1\.05 s of 60 W with a 20 W baseline mean$")]
    public void GivenAStepStartedAndAFirstSample()
    {
        _context.Queue(new BaselineEvent(Origin, false, Baseline(20.0)));
        _context.Queue(Started("Writer", 1.00));
        _context.Queue(Power(60.0, 1.05));
        _context.ApplyQueued();
    }

    [When(@"^the next sample at 1\.15 s of 60 W arrives$")]
    public void WhenTheNextSampleArrives()
    {
        _context.ApplyNow(Power(60.0, 1.15));
        _context.Payloads.Add(_context.Snapshot.ToSnapshotPayload(Origin.AddSeconds(5)));
    }

    [Then(@"^the running energy covers 0\.15 s of net 40 W, which is (\d+) with label ""([^""]*)""$")]
    public void ThenTheRunningEnergyCoversASpan(int expected, string label) => ThenTheRunningEnergyIs(expected, label);

    [Given(@"^two steps active at the same time in concurrent mode$")]
    public void GivenTwoStepsActive()
    {
        _context.Queue(new BaselineEvent(Origin, false, Baseline(20.0)));
        _context.Queue(Started("Planner", 0.0));
        _context.Queue(Started("Researcher", 0.5));
    }

    [Then(@"^both running estimates carry the flag ""([^""]*)""$")]
    public void ThenBothRunningEstimatesCarryTheFlag(string flag)
    {
        foreach (string agent in new[] { "Planner", "Researcher" })
        {
            JsonElement flags = AgentOf(_context.LastPayload, agent).GetProperty("flags");
            Assert.Contains(flag, flags.EnumerateArray().Select(static item => item.GetString()));
        }
    }

    [Given(@"^a step of tier ""([^""]*)"" with no power samples$")]
    public void GivenAStepOfTierWithNoPowerSamples(string tier) =>
        _context.Queue(new StepStartedEvent(Origin, false, new StepIdentity("Writer", "model-h", ParseTier(tier), "summarise", 1, IsJudge: false), 0.0));

    [Then(@"^the running energy has label ""([^""]*)"", reason ""([^""]*)"" and a null value$")]
    public void ThenTheRunningEnergyHasLabelReasonAndNull(string label, string reason) =>
        AssertNullLabelled(AgentOf(_context.LastPayload, "Writer").GetProperty("runningNetJoules"), label, reason);

    [Given(@"^a running step with an estimated energy of 118 and an estimated chunk count$")]
    public void GivenARunningStepWithAnEstimate()
    {
        _context.Queue(new BaselineEvent(Origin, false, Baseline(20.0)));
        _context.Queue(Started("Writer", 0.0));
        foreach (double seconds in new[] { 0.0, 1.0, 2.0 })
        {
            _context.Queue(Power(79.0, seconds));
        }

        _context.Queue(new StepProgressEvent(Origin, false, new StepIdentity("Writer", "model-a", Tier.Local, "summarise", 1, IsJudge: false), 9));
        _context.ApplyQueued();
        JsonElement agent = AgentOf(_context.LastPayload, "Writer");
        Assert.Equal(118.0, agent.GetProperty("runningNetJoules").GetProperty("value").GetDouble(), 9);
        Assert.Equal("estimated", agent.GetProperty("runningNetJoules").GetProperty("label").GetString());
        Assert.Equal("estimated", agent.GetProperty("runningOutputChunks").GetProperty("label").GetString());
    }

    [When(@"^the step-completed event with measured net joules arrives$")]
    public void WhenTheStepCompletedEventArrives() =>
        _context.ApplyNow(new StepCompletedEvent(Origin.AddSeconds(2), false, StepReadingFixtures.Metered(agent: "Writer", netJoules: Metric.Measured(120.0, "J"))));

    [Then(@"^the displayed value carries label ""([^""]*)"" and no estimated value remains for that step$")]
    public void ThenTheDisplayedValueCarriesLabel(string label)
    {
        JsonElement data = Data(_context.LastPayload);
        Assert.Equal(label, data.GetProperty("step").GetProperty("netJoules").GetProperty("label").GetString());
        JsonElement agent = AgentOf(_context.LastPayload, "Writer");
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("runningNetJoules").ValueKind);
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("runningOutputChunks").ValueKind);
        Assert.DoesNotContain("estimated", agent.GetRawText(), StringComparison.Ordinal);
    }

    // ---- the run and the results file ----

    [Given(@"^a run executed with the dashboard enabled$")]
    public async Task GivenARunExecutedWithTheDashboard() => await _context.RunWithDashboardAsync();

    [When(@"^the results file is read$")]
    public void WhenTheResultsFileIsRead() => _context.ReadResultsFile();

    [Then(@"^it contains no running-estimate field and no estimated energy label$")]
    public void ThenItContainsNoRunningEstimate()
    {
        // The dashboard computed running estimates during the run ...
        // (a number: a null or a bare label would not do)
        Assert.Contains(_context.RunPayloads, DashboardFixtures.HasNumericEstimate);

        // ... and none of them is in the file.
        JsonElement root = _context.ResultsFile!.RootElement;
        Assert.DoesNotContain("\"running", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        foreach (JsonElement step in root.GetProperty("steps").EnumerateArray())
        {
            foreach (string member in new[] { "grossJoules", "netJoules", "avgW", "peakW" })
            {
                Assert.NotEqual("estimated", step.GetProperty(member).GetProperty("label").GetString());
            }
        }

        foreach (string member in new[] { "grossJoules", "netJoules", "avgW", "peakW" })
        {
            if (root.GetProperty("totals").TryGetProperty(member, out JsonElement total))
            {
                Assert.NotEqual("estimated", total.GetProperty("label").GetString());
            }
        }
    }

    // ---- the page ----

    [Given(@"^the embedded dashboard page$")]
    public void GivenTheEmbeddedDashboardPage() => _context.Source = DashboardPage.Render(null);

    [Given(@"^the embedded dashboard page after placeholder replacement$")]
    public void GivenTheEmbeddedDashboardPageAfterReplacement() => _context.Source = DashboardPage.Render(LivePageContext.RepositoryUri);

    [When(@"^it is loaded$")]
    public void WhenItIsLoaded() => Assert.NotEmpty(Page);

    [When(@"^the page text is scanned$")]
    public void WhenThePageTextIsScanned() => Assert.NotEmpty(Page);

    [Then(@"^it contains EventSource ""/events"", the null-to-label formatter and ""([^""]*)""$")]
    public void ThenItContainsTheStreamTheFormatterAndTheSentence(string sentence)
    {
        Assert.Contains("EventSource(\"/events\")", Page, StringComparison.Ordinal);
        AssertFormatterPrintsTheLabel();
        Assert.Contains(sentence, DashboardPageRules.VisibleText(Page), StringComparison.Ordinal);
    }

    [Then(@"^the page formatter prints the label text for a null value$")]
    public void ThenThePageFormatterPrintsTheLabel() => AssertFormatterPrintsTheLabel();

    [Then(@"^there is no script src, no link href and no http or https URL other than the repository link$")]
    public void ThenThereIsNoExternalResource()
    {
        Assert.DoesNotContain("<script src", Page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", Page, StringComparison.OrdinalIgnoreCase);
        string link = string.Concat(Uri.UriSchemeHttps, "://example.test/owner/repo#readme");
        Assert.Contains($"href=\"{link}\"", Page, StringComparison.Ordinal);
        string rest = Page.Replace(link, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.UriSchemeHttp, rest, StringComparison.OrdinalIgnoreCase);
    }

    [Then(@"^it contains no innerHTML and no createElementNS$")]
    public void ThenItContainsNoInnerHtml()
    {
        Assert.DoesNotContain("innerHTML", Page, StringComparison.Ordinal);
        Assert.DoesNotContain("createElementNS", Page, StringComparison.Ordinal);
    }

    [Then(@"^it contains ""one-second driver average""$")]
    public void ThenItContainsTheOneSecondAverage() => Assert.Contains("one-second driver average", DashboardPageRules.VisibleText(Page), StringComparison.Ordinal);

    // ---- the render rules ----

    [Given(@"^the embedded dashboard page source$")]
    public void GivenTheEmbeddedDashboardPageSource() => _context.Source = DashboardPage.Render(null);

    [Given(@"^a page source that assigns innerHTML$")]
    public void GivenAPageSourceThatAssignsInnerHtml() => _context.Source = With("</script>", "document.body.innerHTML = 1;\n</script>");

    [Given(@"^a page source that calls setInterval and fetch$")]
    public void GivenAPageSourceThatCallsTimerAndFetch() => _context.Source = With("</script>", "setInterval(frame, 1000);\nfetch(\"/x\");\n</script>");

    [When(@"^the render rules are checked$")]
    public void WhenTheRenderRulesAreChecked() => _context.Violations = DashboardPageRules.RenderViolations(Page);

    [Then(@"^there is exactly one EventSource and one requestAnimationFrame loop$")]
    public void ThenThereIsOneStreamAndOneLoop()
    {
        Assert.Empty(_context.Violations);
        Assert.Equal(1, DashboardPageRules.CountOf(Page, "EventSource"));
        Assert.Equal(1, DashboardPageRules.CountOf(Page, "requestAnimationFrame("));
    }

    [Then(@"^createElement appears only inside the agent card builder$")]
    public void ThenCreateElementAppearsOnlyInTheBuilder()
    {
        string builder = DashboardPageRules.FunctionBody(Page, DashboardPageRules.CardBuilder);
        Assert.NotEmpty(builder);
        Assert.Equal(DashboardPageRules.CountOf(Page, "createElement"), DashboardPageRules.CountOf(builder, "createElement"));
    }

    [Then(@"^the check fails and names innerHTML$")]
    public void ThenTheCheckFailsAndNamesInnerHtml() => AssertNames("innerHTML");

    [Then(@"^the check fails and names setInterval and fetch$")]
    public void ThenTheCheckFailsAndNamesTimerAndFetch() => AssertNames("setInterval", "fetch(");

    [Given(@"^the repaint loop of the embedded page$")]
    public void GivenTheRepaintLoop() => _context.Source = DashboardPage.Render(null);

    [When(@"^the document is hidden$")]
    public void WhenTheDocumentIsHidden() => Assert.NotEmpty(DashboardPageRules.FunctionBody(Page, DashboardPageRules.Loop));

    [Then(@"^the loop returns without painting$")]
    public void ThenTheLoopReturnsWithoutPainting()
    {
        string loop = DashboardPageRules.FunctionBody(Page, DashboardPageRules.Loop);
        Assert.StartsWith("if (document.hidden)", loop.TrimStart(), StringComparison.Ordinal);
        string hiddenBranch = DashboardPageRules.BlockAfter(loop, "if (document.hidden)");
        Assert.Contains("return", hiddenBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("paint(", hiddenBranch, StringComparison.Ordinal);
        Assert.True(loop.IndexOf("paint(", StringComparison.Ordinal) > loop.IndexOf("document.hidden", StringComparison.Ordinal));
    }

    [Given(@"^the page source error handler$")]
    public void GivenTheErrorHandler() => _context.Source = DashboardPage.Render(null);

    [When(@"^the stream reconnects$")]
    public void WhenTheStreamReconnects() => Assert.NotEmpty(DashboardPageRules.BlockAfter(DashboardPageRules.Script(Page), ".onerror = function"));

    [Then(@"^no state is cleared before the snapshot item arrives$")]
    public void ThenNoStateIsClearedBeforeTheSnapshot()
    {
        string script = DashboardPageRules.Script(Page);
        string errorHandler = DashboardPageRules.BlockAfter(script, ".onerror = function");
        Assert.DoesNotContain(DashboardPageRules.ResetFunction, errorHandler, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?<![.\w])state\s*=(?!=)|=\s*\[\s*\]|=\s*\{\s*\}|\.length\s*=\s*0", errorHandler);
        Assert.Equal(1, DashboardPageRules.ResetCalls(script));
        Assert.Contains($"{DashboardPageRules.ResetFunction}(", DashboardPageRules.FunctionBody(script, DashboardPageRules.SnapshotHandler), StringComparison.Ordinal);
    }

    // ---- the design rules ----

    [When(@"^the design rules are checked$")]
    public void WhenTheDesignRulesAreChecked() => _context.Violations = DashboardPageRules.DesignViolations(Page);

    [Then(@"^it has at least 6 custom properties, a prefers-color-scheme media query and the regions header, agents, chart and footer$")]
    public void ThenItHasTokensMediaQueryAndRegions()
    {
        string style = DashboardPageRules.Style(Page);
        Assert.True(DashboardPageRules.ColourPropertyCount(style) >= 6);
        Assert.Contains("@media (prefers-color-scheme: light)", style, StringComparison.Ordinal);
        foreach (string region in DashboardPageRules.Regions)
        {
            Assert.Contains($"id=\"{region}\"", Page, StringComparison.Ordinal);
        }

        Assert.Empty(_context.Violations);
    }

    [Then(@"^it has no link element, no import and no external url$")]
    public void ThenItHasNoLinkNoImportNoUrl()
    {
        Assert.DoesNotContain("<link", Page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", Page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"url\(\s*['""]?(?:ht)tps?:", Page);
    }

    [Given(@"^a page source without a prefers-color-scheme media query$")]
    public void GivenAPageSourceWithoutTheMediaQuery() => _context.Source = With("prefers-color-scheme: light", "min-width: 1");

    [Given(@"^a page source that contains an @import rule$")]
    public void GivenAPageSourceWithAnImport() => _context.Source = With("</style>", "@import 'x';\n</style>");

    [Then(@"^the check fails and names the media query$")]
    public void ThenTheCheckNamesTheMediaQuery() => AssertNames("media query");

    [Then(@"^the check fails and names the import$")]
    public void ThenTheCheckNamesTheImport() => AssertNames("@import");

    // ---- the palette ----

    [Given(@"^the palette values of the dark and the light theme$")]
    public void GivenThePaletteValues()
    {
        (IReadOnlyDictionary<string, string> dark, IReadOnlyDictionary<string, string> light) = DashboardPageRules.Palettes(DashboardPage.Render(null));
        _context.Palettes["dark"] = dark;
        _context.Palettes["light"] = light;
    }

    [When(@"^the contrast ratios of every text-colour token on the panel are computed$")]
    public void WhenTheContrastRatiosAreComputed() => _context.Violations = ComputeViolations();

    [Then(@"^every ratio is at least 4\.5$")]
    public void ThenEveryRatioIsAtLeastFourPointFive()
    {
        Assert.Empty(_context.Violations);
        foreach ((string theme, IReadOnlyDictionary<string, string> palette) in _context.Palettes)
        {
            foreach (string token in DashboardPageRules.TextColourTokens)
            {
                Assert.True(DashboardPageRules.ContrastRatio(palette[token], palette["--panel"]) >= 4.5, $"{token} on the {theme} panel");
            }
        }
    }

    [Given(@"^a palette whose muted text on the panel has a ratio of 3\.0$")]
    public void GivenALowContrastPalette()
    {
        (IReadOnlyDictionary<string, string> dark, _) = DashboardPageRules.Palettes(DashboardPage.Render(null));
        var palette = new Dictionary<string, string>(dark, StringComparer.Ordinal) { ["--panel"] = "#ffffff", ["--muted"] = "#959595" };
        Assert.InRange(DashboardPageRules.ContrastRatio(palette["--muted"], palette["--panel"]), 2.95, 3.05);
        _context.Palettes["test"] = palette;
    }

    [When(@"^the contrast ratios are computed$")]
    public void WhenTheContrastRatiosOfThePaletteAreComputed() => _context.Violations = ComputeViolations();

    [Then(@"^the check fails and names the muted-on-panel pair$")]
    public void ThenTheCheckNamesTheMutedPair() => AssertNames("muted-on-panel");

    // ---- helpers ----

    // The page of the scenario; a scenario that names none reads the page as the server serves it without a repository link.
    private string Page => _context.Source ??= DashboardPage.Render(null);

    private static JsonElement Data(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("data").Clone();
    }

    private static JsonElement AgentOf(string payload, string agent) =>
        Data(payload).GetProperty("agents").EnumerateArray().Single(item => item.GetProperty("agent").GetString() == agent);

    private static void AssertNullLabelled(JsonElement metric, string label, string reason)
    {
        Assert.Equal(label, metric.GetProperty("label").GetString());
        Assert.Equal(reason, metric.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, metric.GetProperty("value").ValueKind);
    }

    private void AssertFormatterPrintsTheLabel()
    {
        string formatter = DashboardPageRules.FunctionBody(Page, "fmt");
        Assert.NotEmpty(formatter);
        Assert.Contains("value === null", formatter, StringComparison.Ordinal);
        Assert.Contains("metric.label", formatter, StringComparison.Ordinal);
        Assert.Empty(DashboardPageRules.FormatterViolations(Page));
    }

    private void AssertNames(params string[] names)
    {
        Assert.NotEmpty(_context.Violations);
        foreach (string name in names)
        {
            Assert.Contains(_context.Violations, violation => violation.Contains(name, StringComparison.Ordinal));
        }
    }

    private static string With(string find, string replacement)
    {
        string page = DashboardPage.Render(null);
        Assert.Contains(find, page, StringComparison.Ordinal);
        return page.Replace(find, replacement, StringComparison.Ordinal);
    }

    private List<string> ComputeViolations()
    {
        var violations = new List<string>();
        foreach ((string theme, IReadOnlyDictionary<string, string> palette) in _context.Palettes)
        {
            violations.AddRange(DashboardPageRules.ContrastViolations(theme, palette));
        }

        return violations;
    }

    private static Tier ParseTier(string name) => name switch
    {
        "local" => Tier.Local,
        "nvidia" => Tier.Nvidia,
        "nebius" => Tier.Nebius,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "The tier is not one the scenario knows."),
    };

    private static StepStartedEvent Started(string agent, double startSeconds) =>
        new(Origin, false, new StepIdentity(agent, "model-a", Tier.Local, "summarise", 1, IsJudge: false), startSeconds);

    private static PowerEvent Power(double watts, double seconds) => new(Origin.AddSeconds(seconds), false, Metric.Measured(watts, "W"), seconds);

    private static BaselineResult Baseline(double meanW) =>
        new(Metric.Measured(meanW, "W"), Metric.Measured(0.5, "W"), seconds: 10.0, sampleCount: 100, coefficientOfVariation: 0.025, maxCoefficientOfVariation: 0.1, ReadingFlags.None);
}