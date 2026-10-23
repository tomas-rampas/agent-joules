using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Runs;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-078 (REQ-015): the results model is complete and strictly typed, its label strings are the five of the vocabulary, a document round-trips, the version and the commit come from the informational
/// version, and every invariant of the model (run flags, cost maps, the pairing of the judge readings with the steps) holds on construction and on read.
/// </summary>
[Trait("Spec", "REQ-015")]
public sealed class SchemaValidationTests
{
    private static readonly string[] FiveLabels = ["measured", "estimated", "model-judged", "N/A", "not-reported"];

    private static RunDocument Fixture() => RunDocumentFixtures.EveryLabelKind();

    private static string Text(RunDocument document) => RunDocumentFixtures.Serialise(document);

    private static JsonNode Tree(RunDocument document) => JsonNode.Parse(Text(document))!;

    // Walks the whole tree and hands every object that has a "label" member to the visitor.
    private static void ForEachMetric(JsonNode? node, Action<JsonObject> visit)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.ContainsKey("label"))
                {
                    visit(obj);
                }

                foreach (KeyValuePair<string, JsonNode?> child in obj)
                {
                    ForEachMetric(child.Value, visit);
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    ForEachMetric(item, visit);
                }

                break;
            default:
                break;
        }
    }

    private static T AssertRejected<T>(Action read)
        where T : Exception
    {
        Exception failure = Assert.ThrowsAny<Exception>(read);
        return Assert.IsType<T>(failure);
    }

    // A read failure is a JsonException (a member the type does not have, a missing member, a null or a wrong token) or an ArgumentException (a model invariant), and nothing else.
    private static Exception AssertReadFails(string text)
    {
        Exception failure = Assert.ThrowsAny<Exception>(() => RunDocumentFixtures.Read(text));
        Assert.True(failure is JsonException or ArgumentException, $"Reading failed with {failure.GetType().Name}.");
        return failure;
    }

    private static JsonNode At(JsonNode root, string path)
    {
        JsonNode node = root;
        foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            node = int.TryParse(part, out int index) ? node[index]! : node[part]!;
        }

        return node;
    }

    // ---- The labels, the strict reader and the persisted fields ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void No_NA_metric_has_a_number()
    {
        int absent = 0;
        int present = 0;
        ForEachMetric(Tree(Fixture()), metric =>
        {
            Assert.True(metric.ContainsKey("value"), "A metric always writes its value, null included.");
            string label = metric["label"]!.GetValue<string>();
            if (label is "N/A" or "not-reported")
            {
                absent++;
                Assert.Null(metric["value"]);
            }
            else
            {
                present++;
                Assert.NotNull(metric["value"]);
            }
        });

        Assert.True(absent >= 2, "The fixture must hold N/A and not-reported metrics.");
        Assert.True(present >= 3, "The fixture must hold valued metrics.");
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("not-reported")]
    public void A_metric_without_a_value_but_with_a_number_is_rejected_on_read(string label)
    {
        string text = Text(Fixture());
        string edited = label == "N/A" ? RunDocumentFixtures.WithNaValue(text, 0) : RunDocumentFixtures.WithNotReportedValue(text, 0);

        _ = AssertReadFails(edited);
    }

    [Fact]
    public void Document_round_trips()
    {
        RunDocument original = Fixture();
        Assert.Equal(RunDocumentFixtures.SecretText, original.Steps[0].OutputText);
        Assert.NotNull(original.Steps[0].Window);

        string first = Text(original);
        RunDocument back = RunDocumentFixtures.Read(first);

        ResultsEquality.AssertEqual(original, back);
        Assert.Null(back.Steps[0].OutputText);
        Assert.Null(back.Steps[0].Window);
        Assert.Equal(first, Text(back));
    }

    [Fact]
    public void Output_text_is_never_written()
    {
        string text = Text(Fixture());

        Assert.DoesNotContain(RunDocumentFixtures.SecretText, text, StringComparison.Ordinal);
        JsonObject step = Tree(Fixture())["steps"]![0]!.AsObject();
        Assert.False(step.ContainsKey("outputText"));
        Assert.False(step.ContainsKey("window"));
        Assert.Equal(RunDocumentFixtures.Sha256Hex(RunDocumentFixtures.SecretText), step["outputSha256"]!.GetValue<string>());
        Assert.Equal(RunDocumentFixtures.SecretText.Length, step["outputChars"]!.GetValue<int>());
        Assert.Matches("^[0-9a-f]{64}$", step["outputSha256"]!.GetValue<string>());
    }

    [Fact]
    public void Label_strings_are_the_five()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        ForEachMetric(Tree(Fixture()), metric => used.Add(metric["label"]!.GetValue<string>()));

        Assert.Equal(FiveLabels.Order(StringComparer.Ordinal), used.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void String_where_a_number_is_expected_is_rejected()
    {
        string text = Text(Fixture());

        string repeat = RunDocumentFixtures.Edit(text, root => root["run"]!["repeat"] = "3");
        _ = Assert.IsType<JsonException>(AssertReadFails(repeat));

        string metric = RunDocumentFixtures.Edit(text, root => root["steps"]![0]!["latencyMs"]!["value"] = "0.5");
        _ = Assert.IsType<JsonException>(AssertReadFails(metric));
    }

    [Fact]
    public void Unknown_property_in_a_step_is_rejected_and_named()
    {
        Exception failure = AssertReadFails(RunDocumentFixtures.WithExtraStepProperty(Text(Fixture()), "surprise"));

        _ = Assert.IsType<JsonException>(failure);
        Assert.Contains("surprise", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("run")]
    [InlineData("environment")]
    [InlineData("environment.assumptions")]
    [InlineData("probe")]
    [InlineData("baseline")]
    [InlineData("totals")]
    [InlineData("totals.judge")]
    [InlineData("totals.grossJoules")]
    [InlineData("steps.0")]
    [InlineData("judge.0")]
    [InlineData("environment.models.0")]
    public void Unknown_property_is_rejected_in_every_object(string path)
    {
        string edited = RunDocumentFixtures.Edit(Text(Fixture()), root => At(root, path).AsObject()["surprise"] = 1);

        _ = AssertReadFails(edited);
    }

    [Theory]
    [InlineData("run", "gitCommit")]
    [InlineData("run", "flags")]
    [InlineData("", "judge")]
    [InlineData("", "probe")]
    [InlineData("totals", "gco2e")]
    public void Missing_member_is_rejected_unless_it_has_a_default(string path, string member)
    {
        string edited = RunDocumentFixtures.Edit(Text(Fixture()), root => _ = At(root, path).AsObject().Remove(member));

        if (member == "flags")
        {
            // run.flags defaults to none, so a file without it still reads.
            Assert.Empty(RunDocumentFixtures.Read(edited).Run.Flags);
        }
        else
        {
            _ = AssertReadFails(edited);
        }
    }

    // ---- The version, the commit and the cloud cost -----------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.1.0+abc1234", "0.1.0", "abc1234")]
    [InlineData("1.2.3-beta.1+deadbeef", "1.2.3-beta.1", "deadbeef")]
    [InlineData("0.1.0", "0.1.0", "unknown")]
    [InlineData("0.1.0+", "0.1.0", "unknown")]
    [InlineData("0.1.0+   ", "0.1.0", "unknown")]
    [InlineData(null, "4.5.6.7", "unknown")]
    [InlineData("", "4.5.6.7", "unknown")]
    public void Version_and_commit_from_informational_version(string? informational, string version, string commit)
    {
        (string toolVersion, string gitCommit) = RunAssembler.ParseVersion(informational, new Version(4, 5, 6, 7));

        Assert.Equal(version, toolVersion);
        Assert.Equal(commit, gitCommit);
    }

    [Fact]
    public void The_run_info_of_this_build_never_holds_a_null_or_blank_commit()
    {
        RunInfo run = RunAssembler.BuildRunInfo("run-1", Tier.Nvidia, ["summarise"], RunMode.Serialized, 1, RunCheckpointFixtures.RunStartUtc, RunStatus.Running);

        Assert.False(string.IsNullOrWhiteSpace(run.GitCommit));
    }

    [Fact]
    public void A_null_git_commit_is_rejected_on_read()
    {
        string text = Text(Fixture());

        string nullCommit = RunDocumentFixtures.Edit(text, root => root["run"]!["gitCommit"] = null);
        _ = Assert.IsType<JsonException>(AssertReadFails(nullCommit));
        Assert.Equal("unknown", RunDocumentFixtures.Read(RunDocumentFixtures.Edit(text, root => root["run"]!["gitCommit"] = "unknown")).Run.GitCommit);
    }

    [Fact]
    public void Cloud_cost_observed_utc_is_nullable()
    {
        foreach (Tier tier in new[] { Tier.Local, Tier.CloudGpu, Tier.Nvidia, Tier.Nebius })
        {
            EnvironmentInfo environment = RunAssembler.BuildEnvironment(tier, new TierOptions { ModelId = "model-a", ReasoningMode = "on" }, gpu: null, runtime: null, modelLoad: null);
            Assert.Null(environment.CloudCostObservedUtc);
        }

        RunDocument document = Fixture();
        Assert.Null(document.Environment.CloudCostObservedUtc);

        string text = Text(document);
        JsonObject environmentNode = JsonNode.Parse(text)!["environment"]!.AsObject();
        Assert.True(environmentNode.ContainsKey("cloudCostObservedUtc"), "The member is always written.");
        Assert.Null(environmentNode["cloudCostObservedUtc"]);
        Assert.Null(RunDocumentFixtures.Read(text).Environment.CloudCostObservedUtc);

        // A file that lacks the member reads as null too; a set value round-trips.
        Assert.Null(RunDocumentFixtures.Read(RunDocumentFixtures.Edit(text, root => _ = root["environment"]!.AsObject().Remove("cloudCostObservedUtc"))).Environment.CloudCostObservedUtc);

        var observed = new DateTimeOffset(2026, 10, 22, 10, 30, 0, TimeSpan.Zero);
        RunDocument cloud = document with { Environment = document.Environment with { CloudCostObservedUtc = observed } };
        Assert.Equal(observed, RunDocumentFixtures.Read(Text(cloud)).Environment.CloudCostObservedUtc);
    }

    // ---- Run flags ---------------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_run_flag_vocabulary_is_the_two_names_in_order()
    {
        Assert.Equal(["partial-offload", "non-publishable"], RunFlagNames.All);
        Assert.True(RunFlagNames.IsKnown("partial-offload"));
        Assert.True(RunFlagNames.IsKnown("non-publishable"));
        Assert.False(RunFlagNames.IsKnown("Non-Publishable"));
        Assert.False(RunFlagNames.IsKnown("made-up"));
        Assert.False(RunFlagNames.IsKnown(string.Empty));
        Assert.False(RunFlagNames.IsKnown(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("non-publishable")]
    [InlineData("partial-offload")]
    [InlineData("non-publishable,partial-offload")]
    public void Run_flags_round_trip(string flagList)
    {
        string[] flags = flagList.Length == 0 ? [] : flagList.Split(',');
        RunDocument original = RunDocumentFixtures.EveryLabelKind(runFlags: flags);

        RunDocument back = RunDocumentFixtures.Read(Text(original));

        Assert.Equal(flags, back.Run.Flags);
        ResultsEquality.AssertEqual(original, back);
    }

    [Fact]
    public void Unlisted_run_flag_is_rejected_on_read_and_named()
    {
        string text = RunDocumentFixtures.WithRunFlagText(Text(Fixture()), "made-up");

        Exception failure = AssertReadFails(text);

        ArgumentException argument = Assert.IsType<ArgumentException>(failure);
        Assert.Equal("Flags", argument.ParamName);
        Assert.Contains("made-up", argument.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_offending_run_flag_is_echoed_bounded_and_without_control_characters()
    {
        string flag = new string('x', 200) + (char)0x07;

        ArgumentException failure = AssertRejected<ArgumentException>(() => RunDocumentFixtures.EveryLabelKind(runFlags: [flag]));

        Assert.DoesNotContain(new string('x', 100), failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain((char)0x07, failure.Message);
    }

    [Fact]
    public void Duplicate_run_flag_is_rejected()
    {
        ArgumentException failure = AssertRejected<ArgumentException>(() => RunDocumentFixtures.EveryLabelKind(runFlags: ["non-publishable", "non-publishable"]));

        Assert.Equal("Flags", failure.ParamName);
        Assert.Contains("non-publishable", failure.Message, StringComparison.Ordinal);
        _ = AssertReadFails(RunDocumentFixtures.WithRunFlagText(Text(Fixture()), "partial-offload", "partial-offload"));
    }

    [Fact]
    public void A_null_run_flag_element_is_rejected()
    {
        ArgumentException failure = AssertRejected<ArgumentException>(() => RunDocumentFixtures.EveryLabelKind(runFlags: [null!]));

        Assert.Equal("Flags", failure.ParamName);
    }

    [Fact]
    public void Run_flag_set_by_with_is_checked()
    {
        RunInfo run = Fixture().Run;

        ArgumentException unknown = AssertRejected<ArgumentException>(() => _ = run with { Flags = ["made-up"] });
        Assert.Equal("Flags", unknown.ParamName);
        _ = AssertRejected<ArgumentException>(() => _ = run with { Flags = ["partial-offload", "partial-offload"] });
        Assert.Equal(["non-publishable"], (run with { Flags = ["non-publishable"] }).Flags);
    }

    [Fact]
    public void Run_flags_are_kept_in_order_and_stored_as_a_copy()
    {
        var flags = new List<string> { "non-publishable", "partial-offload" };
        RunInfo run = RunDocumentFixtures.EveryLabelKind(runFlags: flags).Run;

        flags.Clear();

        Assert.Equal(["non-publishable", "partial-offload"], run.Flags);
    }

    // ---- The comparer that closes the reference comparison of collection members ----------------------------------------------------------------------------------

    [Fact]
    public void Comparer_sees_equal_documents_as_equal()
    {
        ResultsEquality.AssertEqual(Fixture(), Fixture());
    }

    [Fact]
    public void Comparer_sees_a_changed_throttle_reason()
    {
        RunDocument other = RunDocumentFixtures.EveryLabelKind(throttleReason: "HwSlowdown");

        Assert.Equal("steps[0].throttleReasons[0]", ResultsEquality.FirstDifference(Fixture(), other));
        XunitException failure = Assert.Throws<XunitException>(() => ResultsEquality.AssertEqual(Fixture(), other));
        Assert.Contains("steps[0].throttleReasons[0]", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparer_sees_a_changed_cost_map_value()
    {
        RunDocument other = RunDocumentFixtures.EveryLabelKind(judgeTotalCostUsd: 0.002);

        Assert.Equal("totals.judge.costByCurrency[USD].value", ResultsEquality.FirstDifference(Fixture(), other));
    }

    [Fact]
    public void Comparer_sees_a_member_by_reflection()
    {
        RunDocument document = Fixture();
        RunDocument other = document with { Run = document.Run with { SelfJudged = true } };

        Assert.Equal("run.selfJudged", ResultsEquality.FirstDifference(document, other));
    }

    [Fact]
    public void Comparer_sees_a_different_list_length_and_a_different_key_set()
    {
        RunDocument document = Fixture();
        RunDocument fewerSteps = RunDocumentFixtures.Document([RunDocumentFixtures.PlannerStep()], [RunDocumentFixtures.JudgeReading(RunDocumentFixtures.PlannerAgent)]);
        Assert.Equal("steps", ResultsEquality.FirstDifference(document, fewerSteps));

        var costs = new Dictionary<string, Metric>(StringComparer.Ordinal) { ["EUR"] = Metric.NotApplicable("missing-step-costs") };
        Totals other = new(document.Totals.GrossJoules, document.Totals.NetJoules, document.Totals.InputTokens, document.Totals.OutputTokens, costs, document.Totals.WallClockMs, document.Totals.Judge, document.Totals.Gco2e);
        Assert.Equal("costByCurrency", ResultsEquality.FirstDifference(document.Totals, other));
    }

    [Fact]
    public void Comparer_skips_the_members_the_file_never_holds_and_compares_the_ones_it_writes_when_set()
    {
        StepReading withText = RunDocumentFixtures.PlannerStep();
        StepReading read = RunDocumentFixtures.Read(Text(Fixture())).Steps[0];

        Assert.NotNull(withText.OutputText);
        Assert.Null(read.OutputText);
        Assert.Null(ResultsEquality.FirstDifference(withText, read));

        StepReading judge = RunDocumentFixtures.JudgeReading("Planner");
        StepReading other = RunDocumentFixtures.JudgeReading("Writer");
        Assert.Equal("judgedAgent", ResultsEquality.FirstDifference(judge, other));
    }

    // ---- Cost maps -----------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cost_map_with_a_foreign_currency_is_rejected_on_read()
    {
        string text = Text(Fixture());

        string judgeMap = RunDocumentFixtures.Edit(text, root => root["totals"]!["judge"]!["costByCurrency"]!["USD"]!["currency"] = "EUR");
        ArgumentException failure = Assert.IsType<ArgumentException>(AssertReadFails(judgeMap));
        Assert.Contains("costByCurrency", failure.Message, StringComparison.Ordinal);
        Assert.Contains("USD", failure.Message, StringComparison.Ordinal);

        string workflowMap = RunDocumentFixtures.Edit(text, root => root["totals"]!["costByCurrency"]!["USD"] = JsonNode.Parse("""{"value":0.1,"label":"estimated","currency":"EUR"}"""));
        _ = Assert.IsType<ArgumentException>(AssertReadFails(workflowMap));
    }

    // One row per rule, by index, because a TheoryData of a Metric is not serializable (xUnit1044).
    private static (string Key, Metric Entry) RejectedCostEntry(int index) => index switch
    {
        0 => ("USD", Metric.Measured(1.0, "USD")),
        1 => ("USD", Metric.MeasuredCost(1.0, "USD")),
        2 => ("USD", Metric.Estimated(1.0, "EUR")),
        3 => ("USD", Metric.EstimatedQuantity(1.0, "gCO2e")),
        4 => ("USD", Metric.Estimated(-0.01, "USD")),
        5 => ("USD", Metric.ModelJudgedCost(1.0, "USD")),
        6 => ("USD", Metric.NotReported("missing-step-costs")),
        7 => (" ", Metric.NotApplicable("missing-step-costs")),
        8 => ("usd", Metric.Estimated(1.0, "USD")),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void A_cost_map_entry_that_is_not_an_estimate_in_its_own_currency_or_N_A_is_rejected(int row)
    {
        (string key, Metric entry) = RejectedCostEntry(row);
        var costs = new Dictionary<string, Metric>(StringComparer.Ordinal) { [key] = entry };
        Totals sample = Fixture().Totals;
        Metric na = Metric.NotApplicable("not-scored");

        ArgumentException workflow = AssertRejected<ArgumentException>(() => _ = new Totals(sample.GrossJoules, sample.NetJoules, sample.InputTokens, sample.OutputTokens, costs, sample.WallClockMs, sample.Judge, sample.Gco2e));
        Assert.Equal("CostByCurrency", workflow.ParamName);
        ArgumentException judge = AssertRejected<ArgumentException>(() => _ = new JudgeTotals(na, na, costs));
        Assert.Equal("CostByCurrency", judge.ParamName);
    }

    [Fact]
    public void A_cost_map_of_estimates_and_N_A_in_the_currency_of_the_key_is_accepted()
    {
        var costs = new Dictionary<string, Metric>(StringComparer.Ordinal)
        {
            ["EUR"] = Metric.Estimated(0.0, "EUR"),
            ["USD"] = Metric.NotApplicable("missing-step-costs"),
        };
        Metric na = Metric.NotApplicable("not-scored");

        JudgeTotals totals = new(na, na, costs);

        Assert.Equal(2, totals.CostByCurrency.Count);
    }

    [Fact]
    public void Empty_cost_map_round_trips()
    {
        RunDocument fixture = Fixture();
        Totals t = fixture.Totals;
        var none = new Dictionary<string, Metric>(StringComparer.Ordinal);
        RunDocument document = fixture with
        {
            Totals = new Totals(t.GrossJoules, t.NetJoules, t.InputTokens, t.OutputTokens, none, t.WallClockMs, new JudgeTotals(t.Judge.InputTokens, t.Judge.OutputTokens, none), t.Gco2e),
        };

        string text = Text(document);
        RunDocument back = RunDocumentFixtures.Read(text);

        Assert.Empty(Tree(document)["totals"]!["costByCurrency"]!.AsObject());
        Assert.Empty(Tree(document)["totals"]!["judge"]!["costByCurrency"]!.AsObject());
        Assert.Empty(back.Totals.CostByCurrency);
        ResultsEquality.AssertEqual(document, back);
    }

    // ---- The pairing of the judge readings with the steps and the other document invariants -------------------------------------------------------------------------

    [Fact]
    public void Judge_readings_pair_with_steps()
    {
        RunDocument document = RunDocumentFixtures.Document(
            [RunDocumentFixtures.PlannerStep(), RunDocumentFixtures.WriterStep()],
            [RunDocumentFixtures.JudgeReading("Writer"), RunDocumentFixtures.JudgeReading(RunDocumentFixtures.PlannerAgent)]);

        RunDocument back = RunDocumentFixtures.Read(Text(document));

        Assert.Equal(["Writer", "Planner"], back.Judge.Select(static reading => reading.JudgedAgent!));
        Assert.All(back.Steps, static step => Assert.Null(step.JudgedAgent));
        ResultsEquality.AssertEqual(document, back);
    }

    [Fact]
    public void Judge_reading_naming_no_step_is_rejected()
    {
        ArgumentException unknownAgent = AssertRejected<ArgumentException>(() => RunDocumentFixtures.Document([RunDocumentFixtures.PlannerStep()], [RunDocumentFixtures.JudgeReading("Nobody")]));
        Assert.Contains("judge[0]", unknownAgent.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nobody", unknownAgent.Message, StringComparison.Ordinal);

        ArgumentException unknownRepeat = AssertRejected<ArgumentException>(() => RunDocumentFixtures.Document(
            [RunDocumentFixtures.PlannerStep()],
            [RunDocumentFixtures.JudgeReading(RunDocumentFixtures.PlannerAgent), RunDocumentFixtures.JudgeReading(RunDocumentFixtures.PlannerAgent, repeat: 2)]));
        Assert.Contains("judge[1]", unknownRepeat.Message, StringComparison.Ordinal);

        string text = Text(Fixture());
        string edited = RunDocumentFixtures.Edit(text, root => root["judge"]![0]!["judgedAgent"] = "Nobody");
        ArgumentException onRead = Assert.IsType<ArgumentException>(AssertReadFails(edited));
        Assert.Contains("judge[0]", onRead.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Judge_reading_without_a_judged_agent_is_rejected()
    {
        ArgumentException failure = AssertRejected<ArgumentException>(() => RunDocumentFixtures.Document([RunDocumentFixtures.PlannerStep()], [RunDocumentFixtures.JudgeReading(judgedAgent: null)]));

        Assert.Contains("judge[0]", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Judge_reading_matching_two_steps_is_rejected()
    {
        ArgumentException failure = AssertRejected<ArgumentException>(() => RunDocumentFixtures.Document(
            [RunDocumentFixtures.PlannerStep(), RunDocumentFixtures.PlannerStep()],
            [RunDocumentFixtures.JudgeReading(RunDocumentFixtures.PlannerAgent)]));

        Assert.Contains("judge[0]", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Step_with_a_judged_agent_is_rejected()
    {
        ArgumentException failure = AssertRejected<ArgumentException>(() => RunDocumentFixtures.Document([RunDocumentFixtures.PlannerStep(), RunDocumentFixtures.JudgeReading("Planner")], []));
        Assert.Contains("steps[1]", failure.Message, StringComparison.Ordinal);

        string edited = RunDocumentFixtures.Edit(Text(Fixture()), root => root["steps"]![0]!["judgedAgent"] = "Planner");
        ArgumentException onRead = Assert.IsType<ArgumentException>(AssertReadFails(edited));
        Assert.Contains("steps[0]", onRead.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_version_other_than_1_is_rejected()
    {
        RunDocument fixture = Fixture();

        foreach (string version in new[] { "2", "0", string.Empty, "1.0", " 1" })
        {
            ArgumentException failure = AssertRejected<ArgumentException>(() => _ = new RunDocument(version, fixture.Run, fixture.Environment, fixture.Probe, fixture.Baseline, fixture.Steps, fixture.Judge, fixture.Totals));
            Assert.Contains("schemaVersion", failure.Message, StringComparison.Ordinal);
        }

        string edited = RunDocumentFixtures.Edit(Text(fixture), root => root["schemaVersion"] = "2");
        _ = Assert.IsType<ArgumentException>(AssertReadFails(edited));
        Assert.Equal("1", RunDocument.CurrentSchemaVersion);
    }

    [Fact]
    public void Empty_judge_is_valid()
    {
        RunDocument document = RunDocumentFixtures.Document([RunDocumentFixtures.PlannerStep(), RunDocumentFixtures.WriterStep()], []);

        RunDocument back = RunDocumentFixtures.Read(Text(document));

        Assert.Empty(back.Judge);
        ResultsEquality.AssertEqual(document, back);
    }

    [Fact]
    public void A_null_list_or_element_is_rejected_by_index()
    {
        ArgumentNullException step = AssertRejected<ArgumentNullException>(() => RunDocumentFixtures.Document([RunDocumentFixtures.PlannerStep(), null!], []));
        Assert.Contains("steps[1]", step.Message, StringComparison.Ordinal);

        ArgumentNullException judge = AssertRejected<ArgumentNullException>(() => RunDocumentFixtures.Document([RunDocumentFixtures.PlannerStep()], [null!]));
        Assert.Contains("judge[0]", judge.Message, StringComparison.Ordinal);

        _ = AssertRejected<ArgumentNullException>(() => RunDocumentFixtures.Document(null!, []));
        _ = AssertRejected<ArgumentNullException>(() => RunDocumentFixtures.Document([], null!));
    }

    [Fact]
    public void The_lists_are_copies_and_cannot_be_replaced_by_with()
    {
        var steps = new List<StepReading> { RunDocumentFixtures.PlannerStep() };
        var judge = new List<StepReading> { RunDocumentFixtures.JudgeReading(RunDocumentFixtures.PlannerAgent) };
        RunDocument document = RunDocumentFixtures.Document(steps, judge);

        steps.Clear();
        judge.Add(RunDocumentFixtures.JudgeReading("Nobody"));

        Assert.Single(document.Steps);
        Assert.Single(document.Judge);
        Assert.Null(typeof(RunDocument).GetProperty(nameof(RunDocument.Steps))!.SetMethod);
        Assert.Null(typeof(RunDocument).GetProperty(nameof(RunDocument.Judge))!.SetMethod);
        Assert.Null(typeof(RunDocument).GetProperty(nameof(RunDocument.SchemaVersion))!.SetMethod);
    }

    // ---- Fix round: lists, copies, blank text, null members, the comparer's exactness ---------------------------------------------------------------------------------

    private static RunInfo Info(IReadOnlyList<string>? tasks = null, string toolVersion = "1.0", string gitCommit = "abc") =>
        new("run-1", Tier.Local, tasks ?? ["summarise"], RunMode.Serialized, 1, RunCheckpointFixtures.RunStartUtc, toolVersion, gitCommit, RunStatus.Running);

    private static JsonArray NullArray() => new([null]);

    [Fact]
    public void A_null_task_is_rejected_on_construction_and_with()
    {
        ArgumentNullException constructed = AssertRejected<ArgumentNullException>(() => Info(tasks: ["first", null!]));
        Assert.Equal("Tasks", constructed.ParamName);
        Assert.Contains("run.tasks[1]", constructed.Message, StringComparison.Ordinal);

        ArgumentNullException changed = AssertRejected<ArgumentNullException>(() => _ = Fixture().Run with { Tasks = [null!] });
        Assert.Contains("run.tasks[0]", changed.Message, StringComparison.Ordinal);

        var source = new List<string> { "a", "b" };
        RunInfo copy = Info(tasks: source);
        source.Clear();
        Assert.Equal(["a", "b"], copy.Tasks);
    }

    [Fact]
    public void A_null_task_is_rejected_on_read()
    {
        string edited = RunDocumentFixtures.Edit(Text(Fixture()), root => root["run"]!["tasks"] = NullArray());

        ArgumentNullException failure = Assert.IsType<ArgumentNullException>(AssertReadFails(edited));
        Assert.Contains("run.tasks[0]", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_model_is_rejected_on_construction_and_with()
    {
        ArgumentNullException constructed = AssertRejected<ArgumentNullException>(() => RunDocumentFixtures.Environment([new EnvironmentModel("Planner", "model-a", false), null!]));
        Assert.Equal("Models", constructed.ParamName);
        Assert.Contains("environment.models[1]", constructed.Message, StringComparison.Ordinal);

        ArgumentNullException changed = AssertRejected<ArgumentNullException>(() => _ = Fixture().Environment with { Models = [null!] });
        Assert.Contains("environment.models[0]", changed.Message, StringComparison.Ordinal);

        var source = new List<EnvironmentModel> { new("Planner", "model-a", false) };
        EnvironmentInfo copy = RunDocumentFixtures.Environment(source);
        source.Clear();
        Assert.Single(copy.Models);
    }

    [Fact]
    public void A_null_model_is_rejected_on_read()
    {
        string edited = RunDocumentFixtures.Edit(Text(Fixture()), root => root["environment"]!["models"] = NullArray());

        ArgumentNullException failure = Assert.IsType<ArgumentNullException>(AssertReadFails(edited));
        Assert.Contains("environment.models[0]", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stored_cost_map_is_a_copy_in_the_order_of_its_source()
    {
        var source = new Dictionary<string, Metric>(StringComparer.Ordinal)
        {
            ["USD"] = Metric.Estimated(1.0, "USD"),
            ["EUR"] = Metric.Estimated(2.0, "EUR"),
        };
        Metric na = Metric.NotApplicable("not-scored");
        Totals sample = Fixture().Totals;
        Totals totals = new(sample.GrossJoules, sample.NetJoules, sample.InputTokens, sample.OutputTokens, source, sample.WallClockMs, sample.Judge, sample.Gco2e);
        JudgeTotals judge = new(na, na, source);

        source["GBP"] = Metric.Estimated(3.0, "EUR");
        _ = source.Remove("USD");

        foreach (IReadOnlyDictionary<string, Metric> stored in new[] { totals.CostByCurrency, judge.CostByCurrency })
        {
            Assert.NotSame(source, stored);
            Assert.Equal(["USD", "EUR"], stored.Keys);
            Assert.False(stored.ContainsKey("GBP"));
        }
    }

    [Fact]
    public void Two_judge_readings_for_one_step_are_rejected_on_construction_and_on_read()
    {
        ArgumentException constructed = AssertRejected<ArgumentException>(() => RunDocumentFixtures.Document(
            [RunDocumentFixtures.PlannerStep(), RunDocumentFixtures.WriterStep()],
            [RunDocumentFixtures.JudgeReading(RunDocumentFixtures.PlannerAgent), RunDocumentFixtures.JudgeReading("Writer"), RunDocumentFixtures.JudgeReading(RunDocumentFixtures.PlannerAgent)]));
        Assert.Contains("judge[2] pairs with the same step as an earlier judge reading.", constructed.Message, StringComparison.Ordinal);

        string edited = RunDocumentFixtures.Edit(Text(Fixture()), root => root["judge"]!.AsArray().Add(root["judge"]![0]!.DeepClone()));
        ArgumentException onRead = Assert.IsType<ArgumentException>(AssertReadFails(edited));
        Assert.Contains("judge[1] pairs with the same step as an earlier judge reading.", onRead.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("toolVersion", "")]
    [InlineData("toolVersion", "   ")]
    [InlineData("gitCommit", "")]
    [InlineData("gitCommit", "   ")]
    public void A_blank_tool_version_or_git_commit_is_rejected_on_construction_with_and_read(string member, string value)
    {
        string parameter = char.ToUpperInvariant(member[0]) + member[1..];

        ArgumentException constructed = AssertRejected<ArgumentException>(() => _ = member == "toolVersion" ? Info(toolVersion: value) : Info(gitCommit: value));
        Assert.Equal(parameter, constructed.ParamName);
        Assert.Contains(parameter, constructed.Message, StringComparison.Ordinal);

        RunInfo run = Info();
        ArgumentException changed = AssertRejected<ArgumentException>(() => _ = member == "toolVersion" ? run with { ToolVersion = value } : run with { GitCommit = value });
        Assert.Equal(parameter, changed.ParamName);

        string edited = RunDocumentFixtures.Edit(Text(Fixture()), root => root["run"]![member] = value);
        ArgumentException onRead = Assert.IsType<ArgumentException>(AssertReadFails(edited));
        Assert.Equal(parameter, onRead.ParamName);
    }

    [Fact]
    public void A_null_cost_entry_is_rejected_on_read_and_on_construction()
    {
        string edited = RunDocumentFixtures.Edit(Text(Fixture()), root => root["totals"]!["costByCurrency"]!["USD"] = null);
        ArgumentException onRead = Assert.IsType<ArgumentException>(AssertReadFails(edited));
        Assert.Equal("CostByCurrency", onRead.ParamName);
        Assert.Contains("totals.costByCurrency[USD]", onRead.Message, StringComparison.Ordinal);

        var costs = new Dictionary<string, Metric>(StringComparer.Ordinal) { ["USD"] = null! };
        Metric na = Metric.NotApplicable("not-scored");
        Totals sample = Fixture().Totals;
        ArgumentException workflow = AssertRejected<ArgumentException>(() => _ = new Totals(sample.GrossJoules, sample.NetJoules, sample.InputTokens, sample.OutputTokens, costs, sample.WallClockMs, sample.Judge, sample.Gco2e));
        Assert.Equal("CostByCurrency", workflow.ParamName);
        ArgumentException judge = AssertRejected<ArgumentException>(() => _ = new JudgeTotals(na, na, costs));
        Assert.Equal("CostByCurrency", judge.ParamName);
    }

    [Theory]
    [InlineData("judge")]
    [InlineData("steps")]
    public void A_null_list_element_is_rejected_on_read_by_index(string list)
    {
        string edited = RunDocumentFixtures.Edit(Text(Fixture()), root => root[list] = NullArray());

        ArgumentNullException failure = Assert.IsType<ArgumentNullException>(AssertReadFails(edited));
        Assert.Contains(list + "[0]", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_run_environment_or_totals_set_by_with_is_rejected()
    {
        RunDocument document = Fixture();

        _ = AssertRejected<ArgumentNullException>(() => _ = document with { Run = null! });
        _ = AssertRejected<ArgumentNullException>(() => _ = document with { Environment = null! });
        _ = AssertRejected<ArgumentNullException>(() => _ = document with { Totals = null! });
    }

    [Fact]
    public void Comparer_sees_the_same_instant_in_another_offset()
    {
        RunDocument document = Fixture();
        RunDocument shifted = document with { Run = document.Run with { StartUtc = document.Run.StartUtc.ToOffset(TimeSpan.FromHours(2)) } };

        Assert.Equal(document.Run.StartUtc, shifted.Run.StartUtc);
        Assert.Equal("run.startUtc", ResultsEquality.FirstDifference(document, shifted));
    }

    [Fact]
    public void Comparer_sees_negative_zero_as_different_from_zero()
    {
        RunDocument document = Fixture();
        RunDocument negative = document with { Environment = document.Environment with { Temperature = -0.0 } };

        Assert.Equal(0.0, document.Environment.Temperature);
        Assert.Equal("environment.temperature", ResultsEquality.FirstDifference(document, negative));
    }

    [Fact]
    public void Comparer_refuses_a_type_it_cannot_look_into()
    {
        _ = Assert.Throws<InvalidOperationException>(() => ResultsEquality.FirstDifference(new Opaque(), new Opaque()));
    }

    private sealed class Opaque;
}