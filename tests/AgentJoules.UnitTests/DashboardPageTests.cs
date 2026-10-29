using System.Globalization;
using System.Reflection;
using System.Text;

using AgentJoules.Core.Runs;
using AgentJoules.Dashboard;
using AgentJoules.Reporting.Render;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-090 (REQ-032 R4): the embedded live page. The tests read <see cref="DashboardPage.Render"/>, the exact text the server serves at <c>GET /</c>, and apply the render-model rules of
/// <see cref="DashboardPageRules"/> to its source: one stream, one repaint loop, no markup built from event strings, no timers or <c>fetch</c> for data, elements created only when the agent list
/// arrives, a repaint loop that does nothing while the tab is hidden, and a reconnect that keeps the last state until the snapshot arrives. There is no browser engine in the suite: the rules are
/// source-level, and the visual check belongs to the demo rehearsal.
/// </summary>
[Trait("Spec", "REQ-032")]
public sealed class DashboardPageTests
{
    private const string Placeholder = "{{repo.url}}";
    private const string NotConfigured = "Repository link not configured (Report:RepositoryUrl).";

    // ---- criterion 4: no external resource ----

    [Fact]
    public void Page_has_no_external_resource()
    {
        string page = DashboardPage.Render(RepositoryUri());

        Assert.DoesNotContain("<script src", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", page, StringComparison.OrdinalIgnoreCase);

        // The one absolute URL is the repository README link; take it out and none is left.
        string link = string.Concat(Uri.UriSchemeHttps, "://example.test/owner/repo#readme");
        Assert.Equal(1, DashboardPageRules.CountOf(page, $"href=\"{link}\""));
        string rest = page.Replace(link, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.UriSchemeHttp + "://", rest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Uri.UriSchemeHttps + "://", rest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Uri.UriSchemeHttp, rest, StringComparison.OrdinalIgnoreCase);

        // Without a repository URL the page holds no URL at all.
        Assert.DoesNotContain(Uri.UriSchemeHttp, DashboardPage.Render(null), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Page_has_no_innerHTML_or_createElementNS()
    {
        string page = DashboardPage.Render(null);

        Assert.DoesNotContain("innerHTML", page, StringComparison.Ordinal);
        Assert.DoesNotContain("outerHTML", page, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", page, StringComparison.Ordinal);
        Assert.DoesNotContain("createElementNS", page, StringComparison.Ordinal);
        Assert.DoesNotContain("w3.org/2000/svg", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("textContent", page, StringComparison.Ordinal);
    }

    // ---- criterion 5: the stream client, the formatter and the sentence ----

    [Fact]
    public void Page_contains_the_stream_client_the_formatter_the_sentence_and_the_canvas_chart()
    {
        string page = DashboardPage.Render(null);

        Assert.Contains("EventSource(\"/events\")", page, StringComparison.Ordinal);
        Assert.Contains("addEventListener(\"aj\"", page, StringComparison.Ordinal);
        Assert.Contains("Energy is GPU-board energy only.", page, StringComparison.Ordinal);
        Assert.Contains("<canvas", page, StringComparison.Ordinal);
        Assert.Contains("getContext(\"2d\")", page, StringComparison.Ordinal);
        Assert.Contains("setLineDash(", page, StringComparison.Ordinal);

        // One formatter prints the label text when the value is null; nothing in the script turns a null into a zero.
        string formatter = DashboardPageRules.FunctionBody(page, "fmt");
        Assert.Contains("value === null", formatter, StringComparison.Ordinal);
        Assert.Contains("metric.label", formatter, StringComparison.Ordinal);

        // The null branch itself returns the label text: a branch that returned a number, "0" included, fails the rule and names it.
        Assert.Empty(DashboardPageRules.FormatterViolations(page));
        string zeroed = page.Replace("return { text: metric.label, label: metric.label, note: typeof", "return { text: \"0\", label: metric.label, note: typeof", StringComparison.Ordinal);
        Assert.NotEqual(page, zeroed);
        Assert.Contains(DashboardPageRules.FormatterViolations(zeroed), static violation => violation.Contains("null branch of fmt", StringComparison.Ordinal));
        Assert.Contains(DashboardPageRules.RenderViolations(zeroed), static violation => violation.Contains("null branch of fmt", StringComparison.Ordinal));
        string script = DashboardPageRules.Script(page);
        Assert.DoesNotContain("|| 0", script, StringComparison.Ordinal);
        Assert.DoesNotContain("?? 0", script, StringComparison.Ordinal);
        Assert.Equal(1, DashboardPageRules.CountOf(script, "function fmt("));
    }

    // ---- criterion 9: the render model ----

    [Fact]
    public void Page_source_obeys_render_rules()
    {
        string page = DashboardPage.Render(RepositoryUri());

        Assert.Empty(DashboardPageRules.RenderViolations(page));
        Assert.Equal(1, DashboardPageRules.CountOf(page, "EventSource"));
        Assert.Equal(1, DashboardPageRules.CountOf(page, "requestAnimationFrame("));
        Assert.DoesNotContain("setTimeout", page, StringComparison.Ordinal);
        Assert.DoesNotContain("setInterval", page, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", page, StringComparison.Ordinal);
        Assert.DoesNotContain("transition", page, StringComparison.Ordinal);
        Assert.DoesNotContain("animation", page, StringComparison.Ordinal);

        // createElement is called only inside the function that builds the agent cards.
        string builder = DashboardPageRules.FunctionBody(page, DashboardPageRules.CardBuilder);
        Assert.NotEmpty(builder);
        Assert.Equal(DashboardPageRules.CountOf(page, "createElement"), DashboardPageRules.CountOf(builder, "createElement"));

        // The cells that change are fixed-width tabular numerals, so a changing digit never moves the layout.
        Assert.Matches(@"\.val\s*\{[^}]*font-variant-numeric: tabular-nums", DashboardPageRules.Style(page));
        Assert.Matches(@"\.val\s*\{[^}]*\bwidth: \d+ch", DashboardPageRules.Style(page));
    }

    [Fact]
    public void A_source_with_a_forbidden_call_fails_the_render_rules_and_names_it()
    {
        string page = DashboardPage.Render(null);
        (string Edit, string Name)[] cases =
        [
            (page.Replace("</script>", "document.body.innerHTML = 1;\n</script>", StringComparison.Ordinal), "innerHTML"),
            (page.Replace("</script>", "setInterval(frame, 1000);\n</script>", StringComparison.Ordinal), "setInterval"),
            (page.Replace("</script>", "setTimeout(frame, 1000);\n</script>", StringComparison.Ordinal), "setTimeout"),
            (page.Replace("</script>", "fetch(\"/x\");\n</script>", StringComparison.Ordinal), "fetch("),
            (page.Replace("</script>", "document.createElementNS(a, b);\n</script>", StringComparison.Ordinal), "createElementNS"),
            (page.Replace("</script>", "const second = new EventSource(\"/events\");\n</script>", StringComparison.Ordinal), "EventSource"),
            (page.Replace("</script>", "requestAnimationFrame(frame);\n</script>", StringComparison.Ordinal), "requestAnimationFrame("),
            (page.Replace("</style>", ".x { transition: width 1s; }\n</style>", StringComparison.Ordinal), "transition"),
            (page.Replace("</script>", "function stray() { return document.createElement(\"div\"); }\n</script>", StringComparison.Ordinal), "createElement"),
        ];

        foreach ((string edit, string name) in cases)
        {
            Assert.Contains(DashboardPageRules.RenderViolations(edit), violation => violation.Contains(name, StringComparison.Ordinal));
        }
    }

    // ---- criterion 10: hidden tab and reconnect ----

    [Fact]
    public void Hidden_tab_stops_repainting()
    {
        string page = DashboardPage.Render(null);
        string loop = DashboardPageRules.FunctionBody(page, DashboardPageRules.Loop);

        // The loop starts with the check and leaves before it reaches the paint call.
        Assert.StartsWith("if (document.hidden)", loop.TrimStart(), StringComparison.Ordinal);
        string hiddenBranch = DashboardPageRules.BlockAfter(loop, "if (document.hidden)");
        Assert.Contains("return", hiddenBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("paint(", hiddenBranch, StringComparison.Ordinal);
        Assert.True(loop.IndexOf("paint(", StringComparison.Ordinal) > loop.IndexOf("document.hidden", StringComparison.Ordinal));

        // Becoming visible again repaints once.
        string visibility = DashboardPageRules.BlockAfter(page, "addEventListener(\"visibilitychange\"");
        Assert.Contains("dirty = true", visibility, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconnect_keeps_state_until_snapshot()
    {
        string page = DashboardPage.Render(null);
        string script = DashboardPageRules.Script(page);

        // The error handler marks the connection as lost and nothing else: no reset, no new state object, no cleared list.
        string errorHandler = DashboardPageRules.BlockAfter(script, ".onerror = function");
        Assert.NotEmpty(errorHandler);
        Assert.DoesNotContain(DashboardPageRules.ResetFunction, errorHandler, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?<![.\w])state\s*=(?!=)", errorHandler);
        Assert.DoesNotMatch(@"=\s*\[\s*\]|=\s*\{\s*\}|\.length\s*=\s*0", errorHandler);
        Assert.Contains("state.connected = false", errorHandler, StringComparison.Ordinal);

        // The reset function is called from the snapshot handler and from nowhere else.
        Assert.Equal(1, DashboardPageRules.ResetCalls(script));
        Assert.Contains($"{DashboardPageRules.ResetFunction}(", DashboardPageRules.FunctionBody(script, DashboardPageRules.SnapshotHandler), StringComparison.Ordinal);

        // A snapshot that arrives twice, or at any time, leaves the same state: it replaces the state wholesale.
        Assert.Contains("state.connected = true", DashboardPageRules.FunctionBody(script, DashboardPageRules.SnapshotHandler), StringComparison.Ordinal);
    }

    [Fact]
    public void Handlers_only_mutate_state_and_set_the_dirty_flag()
    {
        string script = DashboardPageRules.Script(DashboardPage.Render(null));
        IReadOnlyList<string> handlers = DashboardPageRules.HandlerNames(script);

        Assert.Contains("onEvent", handlers);
        Assert.Contains(DashboardPageRules.SnapshotHandler, handlers);
        Assert.True(handlers.Count >= 9, $"{handlers.Count} handlers");
        foreach (string handler in handlers)
        {
            string body = DashboardPageRules.FunctionBody(script, handler);
            foreach (string forbidden in new[] { "document.", "textContent", "getContext", "paint(", DashboardPageRules.CardBuilder + "(", "classList" })
            {
                Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
            }
        }

        Assert.Contains("dirty = true", DashboardPageRules.FunctionBody(script, "onEvent"), StringComparison.Ordinal);
    }

    // ---- the fix round: the loop, the axis, the labels, the notice ----

    [Fact]
    public void The_frame_loop_schedules_the_next_frame_in_a_finally_block()
    {
        string page = DashboardPage.Render(null);
        string loop = DashboardPageRules.FunctionBody(page, DashboardPageRules.Loop);

        // A repaint that throws must not stop the loop: the next frame is requested in a finally block, after the hidden check.
        Assert.Contains("try {", loop, StringComparison.Ordinal);
        int finallyAt = loop.IndexOf("finally {", StringComparison.Ordinal);
        Assert.True(finallyAt > loop.IndexOf("paint(", StringComparison.Ordinal));
        Assert.Contains("schedule();", DashboardPageRules.BlockAfter(loop, "finally {"), StringComparison.Ordinal);
        Assert.Equal(1, DashboardPageRules.CountOf(page, "requestAnimationFrame("));
        Assert.DoesNotContain("finally", loop[..loop.IndexOf("try {", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void The_time_axis_draws_at_most_thirteen_ticks_by_index()
    {
        string script = DashboardPageRules.Script(DashboardPage.Render(null));

        Assert.Contains("for (let i = 0; i <= 12; i++) {\n    const t = x0 + 10 * i;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("t += 10", script, StringComparison.Ordinal);

        // The shared-window band is clipped to the plot where it starts before the window or ends after it.
        Assert.Contains("const a = Math.max(left, px(from));", script, StringComparison.Ordinal);
        Assert.Contains("const b = Math.min(right, px(to));", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_baseline_line_shows_the_label_of_its_mean_and_the_dead_step_list_is_gone()
    {
        string script = DashboardPageRules.Script(DashboardPage.Render(null));

        Assert.Contains("\"idle baseline \" + meanView.text + \" \" + meanView.label", script, StringComparison.Ordinal);
        Assert.DoesNotContain("state.steps", script, StringComparison.Ordinal);
        Assert.DoesNotContain("steps: []", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_badge_of_an_agent_that_is_not_configured_and_the_degraded_sentence_are_fixed_texts()
    {
        string page = DashboardPage.Render(null);
        string script = DashboardPageRules.Script(page);
        string builder = DashboardPageRules.FunctionBody(script, DashboardPageRules.CardBuilder);

        // The badge text comes from a fixed map and is chosen by the flag the server sends, never taken from a payload string.
        Assert.Contains("const FIXED_TEXT = { extraAgent: \"not a configured agent\" };", script, StringComparison.Ordinal);
        Assert.Contains("extra.textContent = FIXED_TEXT.extraAgent;", builder, StringComparison.Ordinal);
        Assert.Contains("extra.hidden = configured[index];", builder, StringComparison.Ordinal);
        Assert.Equal(1, DashboardPageRules.CountOf(builder, "extra.textContent"));
        Assert.Contains("agent.configured === true", script, StringComparison.Ordinal);
        Assert.Contains(".flag-extra {", DashboardPageRules.Style(page), StringComparison.Ordinal);

        // The reduced snapshot is announced with a fixed sentence.
        Assert.Contains("<p id=\"snapshot-degraded\" class=\"muted\" hidden>This view is incomplete: the server could not serialize the whole state and sent a reduced snapshot.</p>", page, StringComparison.Ordinal);
        Assert.Contains("state.snapshotDegraded = data.snapshotDegraded === true;", DashboardPageRules.FunctionBody(script, DashboardPageRules.SnapshotHandler), StringComparison.Ordinal);
        Assert.Contains("snapshotDegradedEl.hidden = !state.snapshotDegraded;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_says_that_agents_were_left_out_with_a_fixed_sentence()
    {
        string page = DashboardPage.Render(null);

        Assert.Contains("<p id=\"agents-omitted\" class=\"muted\" hidden>More agents ran than the page tracks (12): the further agents are not shown.</p>", page, StringComparison.Ordinal);
        Assert.Contains("agentsOmittedEl.hidden = !(state.agentsOmitted > 0);", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_snapshot_handler_restores_the_last_power_sample()
    {
        string handler = DashboardPageRules.FunctionBody(DashboardPageRules.Script(DashboardPage.Render(null)), DashboardPageRules.SnapshotHandler);

        Assert.Contains("state.watts = labelled(data.watts);", handler, StringComparison.Ordinal);
        Assert.Contains("state.sampleSeconds = numberOrNull(data.sampleSeconds);", handler, StringComparison.Ordinal);
        Assert.Contains("addPoint(state.sampleSeconds, state.watts.value);", handler, StringComparison.Ordinal);
    }

    // ---- REQ-017: the cost surfaces of the live page ----

    [Fact]
    public void Page_shows_the_cumulative_cost_row_and_the_run_cost_item()
    {
        string page = DashboardPage.Render(null);
        string script = DashboardPageRules.Script(page);

        Assert.Contains("Cost (cumulative)", page, StringComparison.Ordinal);
        Assert.Contains("Cost (run)", page, StringComparison.Ordinal);
        Assert.Contains("[\"cumulative\", \"Net energy (cumulative)\"],\n    [\"cost\", \"Cost (cumulative)\"],", script, StringComparison.Ordinal);
        string agentCostView = DashboardPageRules.FunctionBody(script, "agentCostView");
        Assert.Contains("cumulativeCost", agentCostView, StringComparison.Ordinal);
        Assert.Contains("energy.reason === \"no-steps\"", agentCostView, StringComparison.Ordinal);
        Assert.Contains("noSteps ? { text: \"-\", label: \"\", note: \"\" } : { text: \"N/A\", label: \"N/A\", note: \"no-priced-step\" }", agentCostView, StringComparison.Ordinal);
        Assert.Contains("isObject(totals) ? { text: \"N/A\", label: \"N/A\", note: \"no-priced-step\" } : { text: \"-\", label: \"\", note: \"\" }", DashboardPageRules.FunctionBody(script, "runCostView"), StringComparison.Ordinal);
        Assert.Contains("if (view.label === \"\") {", DashboardPageRules.FunctionBody(script, "costMapView"), StringComparison.Ordinal);
        Assert.Contains("value.toPrecision(4)", DashboardPageRules.FunctionBody(script, "costValueText"), StringComparison.Ordinal);
        Assert.Contains("return currency + \" \" + value.toPrecision(4);", DashboardPageRules.FunctionBody(script, "costValueText"), StringComparison.Ordinal);
        string costMapView = DashboardPageRules.FunctionBody(script, "costMapView");
        Assert.Contains("if (Object.keys(map).length === 0) {\n    return emptyView;\n  }", costMapView, StringComparison.Ordinal);
        Assert.Equal(1, costMapView.Split("return emptyView;").Length - 1);
        Assert.Equal(2, costMapView.Split("return { text: \"-\", label: \"\", note: \"\" };").Length - 1);
        Assert.Contains("The cost cells are the exception: they show the estimated step costs and run totals that the results file carries, from the configured prices, and are never replaced.", page, StringComparison.Ordinal);
        Assert.Contains("showCostView(entry.rows.cost.val, entry.rows.cost.badge, agentCostView(agent), true);", DashboardPageRules.FunctionBody(script, "paintCards"), StringComparison.Ordinal);
        Assert.Contains("showCostView(hdr.cost, hdr.costBadge, runCostView(), false);", DashboardPageRules.FunctionBody(script, "paintHeader"), StringComparison.Ordinal);
        Assert.Contains("if (!isObject(map)) {\n    return { text: \"-\", label: \"\", note: \"\" };\n  }", costMapView, StringComparison.Ordinal);
        Assert.Contains("texts.push(numeric ? costValueText(typeof metric.currency === \"string\" ? metric.currency : key, metric.value) : view.text);", costMapView, StringComparison.Ordinal);
        Assert.Contains("if (labels.indexOf(view.label) < 0) {", costMapView, StringComparison.Ordinal);
        Assert.Contains("return { text: texts.join(\", \"), label: labels.length === 1 ? labels[0] : \"N/A\", note: notes.join(\", \") };", costMapView, StringComparison.Ordinal);
        Assert.Contains("setClass(valNode, view.text.length > 13 ? \"val long\" : \"val\");", DashboardPageRules.FunctionBody(script, "showCostView"), StringComparison.Ordinal);
        Assert.Equal("\n  return currency + \" \" + value.toPrecision(4);\n", DashboardPageRules.FunctionBody(script, "costValueText"));
        Assert.Equal("\n  if (!isObject(map)) {\n    return { text: \"-\", label: \"\", note: \"\" };\n  }\n  if (Object.keys(map).length === 0) {\n    return emptyView;\n  }\n  const texts = [];\n  const labels = [];\n  const notes = [];\n  Object.keys(map).forEach(function (key) {\n    const metric = map[key];\n    const view = fmt(metric);\n    if (view.label === \"\") {\n      return;\n    }\n    const numeric = metric.value !== null;\n    texts.push(numeric ? costValueText(typeof metric.currency === \"string\" ? metric.currency : key, metric.value) : view.text);\n    if (labels.indexOf(view.label) < 0) {\n      labels.push(view.label);\n      notes.push(view.note);\n    }\n  });\n  if (texts.length === 0) {\n    // Entries fmt could not read: the malformed-input dash, never a reason the payload does not support.\n    return { text: \"-\", label: \"\", note: \"\" };\n  }\n  return { text: texts.join(\", \"), label: labels.length === 1 ? labels[0] : \"N/A\", note: notes.join(\", \") };\n", DashboardPageRules.FunctionBody(script, "costMapView"));
        Assert.Equal("\n  const energy = labelled(agent.cumulativeNetJoules);\n  const noSteps = energy === null || (energy.value === null && energy.reason === \"no-steps\");\n  return costMapView(agent.cumulativeCost, noSteps ? { text: \"-\", label: \"\", note: \"\" } : { text: \"N/A\", label: \"N/A\", note: \"no-priced-step\" });\n", DashboardPageRules.FunctionBody(script, "agentCostView"));
        Assert.Equal("\n  const totals = state.completed && isObject(state.completed.totals) ? state.completed.totals.costByCurrency : null;\n  return costMapView(totals, isObject(totals) ? { text: \"N/A\", label: \"N/A\", note: \"no-priced-step\" } : { text: \"-\", label: \"\", note: \"\" });\n", DashboardPageRules.FunctionBody(script, "runCostView"));
        Assert.Equal("\n  setText(valNode, view.text);\n  if (rowValue) {\n    setClass(valNode, view.text.length > 13 ? \"val long\" : \"val\");\n  }\n  setText(badgeNode, view.note);\n  setClass(badgeNode, view.label ? \"badge \" + labelClass(view.label) + (view.note.length > 14 ? \" long\" : \"\") : \"badge empty\");\n", DashboardPageRules.FunctionBody(script, "showCostView"));

        // The browser sums nothing: the step-completed handler never touches a cost.
        Assert.DoesNotContain("addStepCost", script, StringComparison.Ordinal);
        Assert.DoesNotContain("cost", DashboardPageRules.FunctionBody(script, "handleStepCompleted"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("|| 0", page, StringComparison.Ordinal);
        Assert.DoesNotContain("?? 0", page, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", page, StringComparison.Ordinal);
    }

    // ---- criterion 12 and the footer ----

    [Fact]
    public void Page_states_one_second_average()
    {
        string text = DashboardPageRules.VisibleText(DashboardPage.Render(null));

        Assert.Contains("one-second driver average", text, StringComparison.Ordinal);
        Assert.Contains("Energy is GPU-board energy only.", text, StringComparison.Ordinal);
    }

    // ---- criterion 7: the report page is another page ----

    [Fact]
    public void The_report_page_stays_free_of_script_and_the_live_page_is_not_shared_with_it()
    {
        // The page of the report command, rendered here from a fixture document, has no script at all.
        ReportModel model = ReportAggregator.Build([ReportRunFixtures.Wrap(RunDocumentFixtures.Committed(Tier.Local))]);
        string report = ReportHtmlRenderer.Render(model, repositoryUrl: null);
        Assert.NotEmpty(report);
        Assert.DoesNotContain("<script", report, StringComparison.OrdinalIgnoreCase);

        // The page is an embedded resource of the dashboard project only.
        Assert.Contains(DashboardPage.ResourceName, typeof(DashboardPage).Assembly.GetManifestResourceNames());
        Assert.DoesNotContain(
            typeof(AgentJoules.Reporting.Json.AgentJoulesJsonContext).Assembly.GetManifestResourceNames(),
            static name => name.Contains("index.html", StringComparison.OrdinalIgnoreCase));
    }

    // ---- P-46: the placeholder ----

    [Fact]
    public void The_source_holds_the_repository_anchor_exactly_once()
    {
        string source = ReadResource();

        Assert.Equal(1, DashboardPageRules.CountOf(source, Placeholder));
        Assert.Equal(1, DashboardPageRules.CountOf(source, $"<a id=\"repo-link\" href=\"{Placeholder}\">Repository README</a>"));
    }

    [Fact]
    public void A_repository_url_becomes_the_encoded_readme_link()
    {
        string page = DashboardPage.Render(RepositoryUri("owner/repo&x"));

        Assert.DoesNotContain(Placeholder, page, StringComparison.Ordinal);
        Assert.Contains("<a id=\"repo-link\" href=\"" + string.Concat(Uri.UriSchemeHttps, "://example.test/owner/repo&amp;x#readme") + "\">Repository README</a>", page, StringComparison.Ordinal);
        Assert.DoesNotContain(NotConfigured, page, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_repository_url_the_anchor_becomes_the_not_configured_sentence()
    {
        string page = DashboardPage.Render(null);

        Assert.Contains(NotConfigured, page, StringComparison.Ordinal);
        Assert.DoesNotContain("repo-link", page, StringComparison.Ordinal);
        Assert.DoesNotContain(Placeholder, page, StringComparison.Ordinal);
        Assert.DoesNotContain("<a ", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_url_that_is_not_absolute_https_without_secrets_is_refused()
    {
        string withUser = string.Concat(Uri.UriSchemeHttps, "://user:pass@example.test/owner/repo");
        string withQuery = string.Concat(Uri.UriSchemeHttps, "://example.test/owner/repo?token=abc");
        string withKey = string.Concat(Uri.UriSchemeHttps, "://example.test/", DashboardEventSerializationTests.RuntimeKey());
        Uri[] refused =
        [
            new(string.Concat(Uri.UriSchemeHttp, "://example.test/owner/repo")),
            new("/owner/repo", UriKind.Relative),
            new(withUser),
            new(withQuery),
            new(withKey),
            new(string.Concat(Uri.UriSchemeFtp, "://example.test/owner/repo")),
        ];

        foreach (Uri url in refused)
        {
            ArgumentException error = Assert.Throws<ArgumentException>(() => DashboardPage.Render(url));
            Assert.DoesNotContain("example.test", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(DashboardEventSerializationTests.KeySuffix(DashboardEventSerializationTests.RuntimeKey()), error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_host_with_non_ascii_letters_is_written_in_punycode_and_the_page_stays_ascii()
    {
        // With Host in place of IdnHost the link would keep the letter u with a diaeresis and the page would stop being ASCII.
        string host = string.Concat("b", (char)0xFC, "cher.example");
        Uri url = new(string.Concat(Uri.UriSchemeHttps, "://", host, "/owner/repo"));

        string page = DashboardPage.Render(url);

        Assert.Contains("xn--bcher-kva.example", page, StringComparison.Ordinal);
        Assert.DoesNotContain(host, page, StringComparison.Ordinal);
        Assert.All(page, static character => Assert.True(character is '\n' or (>= ' ' and <= '~'), $"U+{(int)character:X4}"));
    }

    [Fact]
    public void A_fragment_in_the_url_is_replaced_by_the_readme_fragment_and_a_port_is_kept()
    {
        string withFragment = DashboardPage.Render(RepositoryUri("owner/repo#top"));
        string withPort = DashboardPage.Render(new Uri(string.Concat(Uri.UriSchemeHttps, "://example.test:8443/owner/repo")));

        Assert.Contains(string.Concat(Uri.UriSchemeHttps, "://example.test/owner/repo#readme\""), withFragment, StringComparison.Ordinal);
        Assert.DoesNotContain("#top", withFragment, StringComparison.Ordinal);
        Assert.Contains(string.Concat(Uri.UriSchemeHttps, "://example.test:8443/owner/repo#readme\""), withPort, StringComparison.Ordinal);
    }

    // ---- hygiene of the served text ----

    [Fact]
    public void The_rendered_page_is_ascii_with_lf_newlines_one_final_line_feed_and_is_the_same_in_every_culture()
    {
        string expected = DashboardPage.Render(RepositoryUri());
        Assert.All(expected, static character => Assert.True(character is '\n' or (>= ' ' and <= '~'), $"U+{(int)character:X4}"));
        Assert.EndsWith("\n", expected, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", expected, StringComparison.Ordinal);
        Assert.False(expected.EndsWith("\n\n", StringComparison.Ordinal));
        Assert.DoesNotContain('\r', expected);

        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "tr-TR", "de-DE", "fr-FR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Assert.Equal(expected, DashboardPage.Render(RepositoryUri()));
                Assert.Equal(DashboardPage.Render(null), DashboardPage.Render(null));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_embedded_resource_is_the_file_of_the_dashboard_project()
    {
        string onDisk = File.ReadAllText(Path.Combine(ConfigurationFixtures.RepositoryRoot(), "src", "AgentJoules.Dashboard", "wwwroot", "index.html"), Encoding.UTF8);

        Assert.Equal(onDisk, ReadResource());
        Assert.DoesNotContain('\r', onDisk);
        Assert.EndsWith("\n", onDisk, StringComparison.Ordinal);
        Assert.Contains("<!DOCTYPE html>", onDisk, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static Uri RepositoryUri(string path = "owner/repo") => new(string.Concat(Uri.UriSchemeHttps, "://", "example.test/", path));

    private static string ReadResource()
    {
        Assembly assembly = typeof(DashboardPage).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(DashboardPage.ResourceName) ?? throw new InvalidOperationException("The page is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}