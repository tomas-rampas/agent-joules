using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;
using AgentJoules.Reporting.Schema;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Runs;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-078 (REQ-015): the results model is complete and strictly typed, its label strings are the five of the vocabulary, a document round-trips, the version and the commit come from the informational
/// version, and every invariant of the model (run flags, cost maps, the pairing of the judge readings with the steps) holds on construction and on read.
/// AJ-079 (REQ-015): the JSON Schema is generated from the model, the committed <c>docs/results-schema.json</c> and the embedded copy equal the generated text, and the validator accepts the documents the tool writes
/// and refuses the mutations of criterion 5 and the rules of the metric definitions, the vocabularies and the ranges.
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
        5 => ("USD", JsonSerializer.Deserialize<Metric>("""{"value":1,"label":"model-judged","currency":"USD"}""")!),
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

    // ---- AJ-079: the generated JSON Schema, the guard against drift and the validator --------------------------------------------------------------------------------------------

    private const string UpdateVariable = CommittedFileDrift.SchemaUpdateVariable;
    private const string UpdateRewrote = "rewrote docs/results-schema.json; rebuild, then re-run without " + UpdateVariable;
    private const string UpdateRefusedInCi = UpdateVariable + " is set in a CI run (the CI variable is set and not empty); the schema is not rewritten there. Unset it and commit the regenerated file.";
    private const string SchemaFile = "docs/results-schema.json";
    private const string DefsPrefix = "#/$defs/";

    private static readonly string[] MetricDefs = ["metricMeasured", "metricEstimated", "metricJudged", "metricAny"];

    private static string CommittedSchemaPath() => Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "results-schema.json");

    private static JsonNode GeneratedSchema() => JsonNode.Parse(SchemaGenerator.GenerateText())!;

    private static IReadOnlyList<SchemaFailure> Failures(JsonNode tree, SchemaTarget target = SchemaTarget.RunFile)
    {
        using JsonDocument document = JsonDocument.Parse(tree.ToJsonString());
        return SchemaValidator.Validate(document.RootElement, target);
    }

    private static string Describe(IReadOnlyList<SchemaFailure> failures) =>
        failures.Count == 0 ? "no failure" : string.Join("; ", failures.Select(static failure => $"{failure.InstanceLocation} {failure.Message}"));

    private static void AssertValid(JsonNode tree, SchemaTarget target = SchemaTarget.RunFile)
    {
        IReadOnlyList<SchemaFailure> failures = Failures(tree, target);
        Assert.True(failures.Count == 0, Describe(failures));
    }

    // A failure lies at the member when it is reported at its location or inside it.
    private static bool FailsAt(IReadOnlyList<SchemaFailure> failures, string location) =>
        failures.Any(failure => string.Equals(failure.InstanceLocation, location, StringComparison.Ordinal) || failure.InstanceLocation.StartsWith(location + "/", StringComparison.Ordinal));

    private static void AssertInvalidAt(JsonNode tree, string location)
    {
        IReadOnlyList<SchemaFailure> failures = Failures(tree);
        Assert.True(FailsAt(failures, location), $"Expected a failure at {location}, but the failures are: {Describe(failures)}");
    }

    private static JsonObject MetricAt(JsonNode root, string path) => At(root, path).AsObject();

    // Puts a JSON value in place of the member the dotted path names.
    private static void Put(JsonNode root, string path, string json)
    {
        int cut = path.LastIndexOf('.');
        JsonNode parent = cut < 0 ? root : At(root, path[..cut]);
        string name = cut < 0 ? path : path[(cut + 1)..];
        if (parent is JsonArray array)
        {
            array[int.Parse(name, System.Globalization.CultureInfo.InvariantCulture)] = JsonNode.Parse(json);
        }
        else
        {
            parent.AsObject()[name] = JsonNode.Parse(json);
        }
    }

    private static string[] Strings(JsonNode? array) =>
        [.. array!.AsArray().Select(static item => item!.GetValue<string>())];

    // The names of an enumeration keyword, without a null member.
    private static string[] EnumOf(JsonNode node) =>
        [.. node["enum"]!.AsArray().Where(static item => item is not null).Select(static item => item!.GetValue<string>())];

    // The name of the metric definition a schema node refers to, directly or in an allOf; null when it refers to none.
    private static string? DefOf(JsonNode? node) =>
        node is not JsonObject obj ? null
        : obj["$ref"] is JsonValue reference && reference.GetValue<string>() is { } target && target.StartsWith(DefsPrefix, StringComparison.Ordinal) ? target[DefsPrefix.Length..]
        : obj["allOf"] is JsonArray parts ? parts.Select(DefOf).FirstOrDefault(static name => name is not null)
        : null;

    private static int CountMetricReferences(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                int own = obj["$ref"] is JsonValue reference && reference.GetValue<string>().StartsWith(DefsPrefix + "metric", StringComparison.Ordinal) ? 1 : 0;
                return own + obj.Sum(static child => CountMetricReferences(child.Value));
            case JsonArray array:
                return array.Sum(static item => CountMetricReferences(item));
            default:
                return 0;
        }
    }

    // What the converter of the enumeration writes for each value: a string, or for the flags an array of strings.
    private static string[] SerialisedNames<T>(Func<T, bool> include)
        where T : struct, Enum =>
        [.. Enum.GetValues<T>().Where(include).SelectMany(static value => JsonNode.Parse(JsonSerializer.Serialize(value)) switch
        {
            JsonArray names => names.Select(static name => name!.GetValue<string>()),
            JsonValue name => [name.GetValue<string>()],
            _ => throw new XunitException($"The converter of {typeof(T).Name} wrote neither a string nor an array."),
        })];

    [Fact]
    public void CommittedSchemaMatchesGenerated()
    {
        CheckOrUpdateSchema(
            CommittedSchemaPath(),
            SchemaGenerator.GenerateText(),
            Environment.GetEnvironmentVariable(UpdateVariable),
            Environment.GetEnvironmentVariable("CI"));
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void The_schema_defines_the_calibration_document_and_the_committed_file_holds_the_same_definition()
    {
        string[] members =
        [
            "task", "seconds", "windowSeconds", "sampleCount", "sampleRateHz", "repeats", "startUtc", "endUtc", "toolVersion", "gitCommit", "counterJoules", "samplingJoules", "relativeDifference", "probe", "environment",
        ];
        JsonNode generated = GeneratedSchema();
        JsonNode committed = JsonNode.Parse(File.ReadAllText(CommittedSchemaPath()))!;

        foreach (JsonNode schema in new[] { generated, committed })
        {
            JsonNode calibration = At(schema, "$defs.calibration");
            Assert.Equal("object", calibration["type"]!.GetValue<string>());
            Assert.False(calibration["additionalProperties"]!.GetValue<bool>());
            Assert.Equal(members, Strings(calibration["required"]));
            string[] declared = [.. calibration["properties"]!.AsObject().Select(static pair => pair.Key)];
            Assert.Equal(members, declared);
            foreach (string metric in new[] { "counterJoules", "samplingJoules", "relativeDifference" })
            {
                Assert.Equal("#/$defs/metricMeasured", At(calibration, "properties." + metric)["$ref"]!.GetValue<string>());
            }

            Assert.Equal(1, At(calibration, "properties.seconds")["minimum"]!.GetValue<int>());
            Assert.Equal(1, At(calibration, "properties.repeats")["minimum"]!.GetValue<int>());
            Assert.Equal("#/$defs/probe", At(calibration, "properties.probe.allOf.0")["$ref"]!.GetValue<string>());
            Assert.True(At(calibration, "properties.probe.allOf.1.properties.gpuPresent")["const"]!.GetValue<bool>());
            Assert.Equal(100, At(calibration, "properties.environment.properties.gpuOffloadPercent")["maximum"]!.GetValue<int>());
        }

        Assert.True(JsonNode.DeepEquals(At(generated, "$defs.calibration"), At(committed, "$defs.calibration")));

        // The run file's own definition does not change: the calibration definition is an addition to the defs, not to the root.
        Assert.Null(committed["properties"]!["counterJoules"]);
    }

    // The drift check and its update path. An update run (the variable is "1") never ends green, so a variable left set in a profile cannot turn the gate into a self-heal that passes on every run, and it
    // never writes in a CI run (the CI variable is set and not empty). The values are parameters so that a test can drive every combination without touching the environment of the process.
    private static void CheckOrUpdateSchema(string schemaPath, string generated, string? updateValue, string? ci)
    {
        if (string.Equals(updateValue, "1", StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(ci))
            {
                throw new XunitException(UpdateRefusedInCi);
            }

            File.WriteAllText(schemaPath, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            throw new XunitException(UpdateRewrote);
        }

        CommittedFileDrift.AssertNoDrift(File.ReadAllText(schemaPath), generated, SchemaFile, CommittedFileDrift.SchemaUpdateHint);
    }

    [Fact]
    public void The_update_run_never_writes_in_a_CI_run_and_never_ends_green()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = workspace.WriteFile("results-schema.json", "{}\n");
        string generated = SchemaGenerator.GenerateText();

        Assert.Equal(UpdateRefusedInCi, Assert.Throws<XunitException>(() => CheckOrUpdateSchema(path, generated, "1", "true")).Message);
        Assert.Equal("{}\n", File.ReadAllText(path));

        Assert.Equal(UpdateRewrote, Assert.Throws<XunitException>(() => CheckOrUpdateSchema(path, generated, "1", null)).Message);
        Assert.Equal(generated, File.ReadAllText(path));

        File.WriteAllText(path, "{}\n");
        Assert.Equal(UpdateRewrote, Assert.Throws<XunitException>(() => CheckOrUpdateSchema(path, generated, "1", string.Empty)).Message);
        Assert.Equal(generated, File.ReadAllText(path));

        // Without the variable (or with a value other than 1) it is the plain drift check: green on equal text, a drift failure on a stale file, and no write.
        CheckOrUpdateSchema(path, generated, null, "true");
        File.WriteAllText(path, "{}\n");
        Assert.Contains(CommittedFileDrift.SchemaUpdateHint, Assert.Throws<XunitException>(() => CheckOrUpdateSchema(path, generated, "0", null)).Message, StringComparison.Ordinal);
        Assert.Equal("{}\n", File.ReadAllText(path));
    }

    [Fact]
    public void A_stale_committed_schema_fails_the_drift_check_and_names_the_variable()
    {
        JsonNode stale = GeneratedSchema();
        Assert.True(At(stale, "properties.run.properties").AsObject().Remove("selfJudged"));

        XunitException failure = Assert.Throws<XunitException>(() =>
            CommittedFileDrift.AssertNoDrift(stale.ToJsonString(), SchemaGenerator.GenerateText(), SchemaFile, CommittedFileDrift.SchemaUpdateHint));

        Assert.Contains(UpdateVariable, failure.Message, StringComparison.Ordinal);
        Assert.EndsWith(CommittedFileDrift.SchemaUpdateHint, failure.Message, StringComparison.Ordinal);
        Assert.Contains(SchemaFile, failure.Message, StringComparison.Ordinal);
        Assert.Contains("/properties/run/properties/selfJudged", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_drift_check_ignores_white_space_and_line_endings_and_sees_a_changed_value()
    {
        string generated = SchemaGenerator.GenerateText();

        Assert.Null(CommittedFileDrift.Difference(generated.ReplaceLineEndings("\r\n"), generated));
        Assert.Null(CommittedFileDrift.Difference(JsonNode.Parse(generated)!.ToJsonString(), generated));

        JsonNode changed = GeneratedSchema();
        changed["additionalProperties"] = true;
        Assert.StartsWith("/additionalProperties", CommittedFileDrift.Difference(changed.ToJsonString(), generated), StringComparison.Ordinal);
        Assert.StartsWith("(root)", CommittedFileDrift.Difference("not json", generated), StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_text_is_indented_ascii_with_lf_and_one_final_line_feed_and_is_stable()
    {
        string text = SchemaGenerator.GenerateText();

        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", text, StringComparison.Ordinal);
        Assert.All(text, static c => Assert.True(c <= 0x7F, "The schema text is ASCII only."));
        Assert.Contains("\n  \"properties\": {\n", text, StringComparison.Ordinal);
        Assert.Equal(text, SchemaGenerator.GenerateText());
        Assert.True(JsonNode.DeepEquals(SchemaGenerator.Generate(), JsonNode.Parse(text)));
    }

    [Fact]
    public void The_schema_is_draft_2020_12_with_an_id_and_the_root_is_an_object_that_allows_nothing_else()
    {
        JsonNode schema = GeneratedSchema();

        Uri draft = new(schema["$schema"]!.GetValue<string>());
        Assert.Equal("json-schema.org", draft.Host);
        Assert.Equal("/draft/2020-12/schema", draft.AbsolutePath);
        Assert.Equal("urn:agentjoules:results-schema:1", schema["$id"]!.GetValue<string>());
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(RunDocument.CurrentSchemaVersion, At(schema, "properties.schemaVersion")["const"]!.GetValue<string>());
        Assert.Equal(EnvironmentInfo.GpuBoardOnly, At(schema, "properties.environment.properties.energyScope")["const"]!.GetValue<string>());
    }

    [Fact]
    public void Generated_document_validates()
    {
        AssertValid(Tree(Fixture()));
    }

    [Fact]
    public void Documents_of_other_shapes_validate()
    {
        RunDocument document = Fixture();

        AssertValid(Tree(RunDocumentFixtures.Document([], [])));
        AssertValid(Tree(document with { Probe = null, Baseline = null }));
        AssertValid(Tree(RunDocumentFixtures.EveryLabelKind(runFlags: [.. RunFlagNames.All])));
        AssertValid(Tree(document with { Run = document.Run with { Status = RunStatus.Partial, StopReason = "budget", SelfJudged = true } }));
    }

    [Fact]
    public void A_document_the_run_assembler_builds_and_the_checkpoint_documents_of_every_status_validate()
    {
        RunDocument fixture = Fixture();
        RunDocument assembled = RunAssembler.Assemble(fixture.Run, fixture.Environment, fixture.Probe, fixture.Baseline, fixture.Steps, fixture.Judge, wallClockMs: 4000.0);

        AssertValid(Tree(assembled));
        foreach (RunStatus status in Enum.GetValues<RunStatus>())
        {
            AssertValid(Tree(RunCheckpointFixtures.Document(status, stepCount: 2)));
        }
    }

    [Fact]
    public void Run_flags_enum_in_schema()
    {
        JsonNode flags = At(GeneratedSchema(), "properties.run.properties.flags");

        Assert.Equal(RunFlagNames.All, EnumOf(flags["items"]!));
        Assert.True(flags["uniqueItems"]!.GetValue<bool>());
        AssertValid(Tree(RunDocumentFixtures.EveryLabelKind(runFlags: [RunFlagNames.NonPublishable])));
        AssertValid(Tree(RunDocumentFixtures.EveryLabelKind(runFlags: [RunFlagNames.PartialOffload, RunFlagNames.NonPublishable])));
    }

    [Fact]
    public void An_unknown_or_repeated_or_non_string_run_flag_is_invalid()
    {
        string text = Text(Fixture());

        AssertInvalidAt(JsonNode.Parse(RunDocumentFixtures.WithRunFlagText(text, "made-up"))!, "/run/flags");
        AssertInvalidAt(JsonNode.Parse(RunDocumentFixtures.WithRunFlagText(text, RunFlagNames.NonPublishable, RunFlagNames.NonPublishable))!, "/run/flags");
        AssertInvalidAt(JsonNode.Parse(RunDocumentFixtures.WithRunFlagText(text, "Non-Publishable"))!, "/run/flags");

        JsonNode tree = JsonNode.Parse(text)!;
        Put(tree, "run.flags", "[1]");
        AssertInvalidAt(tree, "/run/flags");
    }

    [Fact]
    public void NA_metric_with_a_number_is_invalid()
    {
        JsonNode tree = Tree(Fixture());
        MetricAt(tree, "steps.1.cost")["value"] = 0.5;

        AssertInvalidAt(tree, "/steps/1/cost");
    }

    [Fact]
    public void A_not_reported_metric_with_a_number_is_invalid()
    {
        JsonNode tree = Tree(Fixture());
        MetricAt(tree, "steps.1.inputTokens")["value"] = 12;

        AssertInvalidAt(tree, "/steps/1/inputTokens");
    }

    [Fact]
    public void Quality_labelled_measured_is_invalid()
    {
        JsonNode tree = Tree(Fixture());
        MetricAt(tree, "steps.0.quality")["label"] = "measured";

        AssertInvalidAt(tree, "/steps/0/quality");
    }

    [Fact]
    public void NA_metric_without_a_reason_is_invalid()
    {
        JsonNode tree = Tree(Fixture());
        Assert.True(MetricAt(tree, "steps.1.cost").Remove("reason"));

        AssertInvalidAt(tree, "/steps/1/cost");
    }

    [Fact]
    public void An_empty_reason_and_a_reason_on_a_valued_metric_are_invalid()
    {
        JsonNode empty = Tree(Fixture());
        MetricAt(empty, "steps.1.cost")["reason"] = string.Empty;
        AssertInvalidAt(empty, "/steps/1/cost");

        JsonNode valued = Tree(Fixture());
        MetricAt(valued, "steps.0.latencyMs")["reason"] = "why";
        AssertInvalidAt(valued, "/steps/0/latencyMs");
    }

    [Fact]
    public void A_metric_needs_a_value_and_a_label_and_a_valued_label_needs_a_number()
    {
        JsonNode noValue = Tree(Fixture());
        Assert.True(MetricAt(noValue, "steps.0.latencyMs").Remove("value"));
        AssertInvalidAt(noValue, "/steps/0/latencyMs");

        JsonNode noLabel = Tree(Fixture());
        Assert.True(MetricAt(noLabel, "steps.0.latencyMs").Remove("label"));
        AssertInvalidAt(noLabel, "/steps/0/latencyMs");

        JsonNode nullValue = Tree(Fixture());
        MetricAt(nullValue, "steps.0.latencyMs")["value"] = null;
        AssertInvalidAt(nullValue, "/steps/0/latencyMs");

        JsonNode text = Tree(Fixture());
        MetricAt(text, "steps.0.latencyMs")["value"] = "2000";
        AssertInvalidAt(text, "/steps/0/latencyMs");
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
    [InlineData("totals.costByCurrency.USD")]
    [InlineData("steps.0")]
    [InlineData("steps.0.cost")]
    [InlineData("judge.0")]
    [InlineData("environment.models.0")]
    public void Extra_property_is_invalid_in_every_object(string path)
    {
        JsonNode tree = Tree(Fixture());
        At(tree, path).AsObject()["surprise"] = 1;

        IReadOnlyList<SchemaFailure> failures = Failures(tree);
        string expected = "/" + path.Replace('.', '/') + "/surprise";
        if (path.Length == 0)
        {
            expected = "/surprise";
        }

        Assert.True(failures.Any(failure => string.Equals(failure.InstanceLocation, expected, StringComparison.Ordinal)), $"Expected a failure at {expected}, but the failures are: {Describe(failures)}");
    }

    [Fact]
    public void A_member_missing_from_an_object_is_invalid_and_named()
    {
        JsonNode tree = Tree(Fixture());
        Assert.True(At(tree, "steps.0").AsObject().Remove("cost"));

        IReadOnlyList<SchemaFailure> failures = Failures(tree);

        Assert.Contains(failures, static failure => failure.InstanceLocation == "/steps/0" && failure.Message.Contains("cost", StringComparison.Ordinal));
    }

    [Fact]
    public void Cost_per_quality_point_as_an_estimate_in_a_currency_is_valid()
    {
        JsonNode tree = Tree(Fixture());
        Put(tree, "steps.0.costPerQualityPoint", """{"value":1.5,"label":"estimated","currency":"EUR"}""");

        AssertValid(tree);
    }

    [Fact]
    public void Unit_and_currency_on_cost_per_quality_point_is_invalid()
    {
        JsonNode tree = Tree(Fixture());
        Put(tree, "steps.0.costPerQualityPoint", """{"value":1.5,"label":"estimated","unit":"point","currency":"EUR"}""");

        AssertInvalidAt(tree, "/steps/0/costPerQualityPoint");
    }

    [Theory]
    [InlineData("steps.0.quality", """{"value":4,"label":"model-judged","unit":"point"}""")]
    [InlineData("steps.0.quality", """{"value":4,"label":"model-judged","currency":"USD"}""")]
    [InlineData("steps.0.quality", """{"value":4,"label":"estimated","currency":"USD"}""")]
    [InlineData("steps.0.joulesPerQualityPoint", """{"value":15,"label":"model-judged","currency":"USD"}""")]
    [InlineData("steps.0.joulesPerQualityPoint", """{"value":15,"label":"model-judged","unit":"J"}""")]
    [InlineData("steps.0.joulesPerQualityPoint", """{"value":-1,"label":"model-judged"}""")]
    public void Model_judged_members_refuse_the_wrong_qualifiers_labels_and_ranges(string path, string json)
    {
        JsonNode tree = Tree(Fixture());
        Put(tree, path, json);

        AssertInvalidAt(tree, "/" + path.Replace('.', '/'));
    }

    // The cost per quality point is an estimate in a currency (user decision 9, 2026-10-24): the refinements of the step cost apply, and the model-judged label is refused.
    [Theory]
    [InlineData("""{"value":0.005,"label":"model-judged"}""")]
    [InlineData("""{"value":0.005,"label":"model-judged","currency":"USD"}""")]
    [InlineData("""{"value":0.005,"label":"estimated"}""")]
    [InlineData("""{"value":0.005,"label":"estimated","unit":"J"}""")]
    [InlineData("""{"value":-0.005,"label":"estimated","currency":"USD"}""")]
    [InlineData("""{"value":0.005,"label":"measured","currency":"USD"}""")]
    public void The_cost_per_quality_point_refuses_the_wrong_qualifiers_labels_and_ranges(string json)
    {
        JsonNode tree = Tree(Fixture());
        Put(tree, "steps.0.costPerQualityPoint", json);

        AssertInvalidAt(tree, "/steps/0/costPerQualityPoint");
    }

    [Theory]
    [InlineData("steps.0.latencyMs", """{"value":1,"label":"measured"}""")]
    [InlineData("steps.0.latencyMs", """{"value":1,"label":"measured","unit":"ms","currency":"USD"}""")]
    [InlineData("steps.0.latencyMs", """{"value":1,"label":"estimated","currency":"USD"}""")]
    [InlineData("steps.0.latencyMs", """{"value":1,"label":"model-judged"}""")]
    [InlineData("steps.0.latencyMs", """{"value":null,"label":"N/A","reason":"x","unit":"ms"}""")]
    [InlineData("steps.0.latencyMs", """{"value":null,"label":"not-reported","reason":"x","currency":"USD"}""")]
    [InlineData("steps.0.latencyMs", """{"value":1,"label":"Measured","unit":"ms"}""")]
    [InlineData("environment.assumptions.electricityPrice", """{"value":0.3,"label":"measured","unit":"kWh"}""")]
    [InlineData("environment.assumptions.electricityPrice", """{"value":0.3,"label":"estimated"}""")]
    [InlineData("totals.gco2e", """{"value":1,"label":"estimated"}""")]
    public void The_measured_and_estimated_definitions_refuse_the_wrong_labels_and_qualifiers(string path, string json)
    {
        JsonNode tree = Tree(Fixture());
        Put(tree, path, json);

        AssertInvalidAt(tree, "/" + path.Replace('.', '/'));
    }

    [Theory]
    [InlineData("steps.0.latencyMs", """{"value":0,"label":"measured","unit":"ms"}""")]
    [InlineData("steps.0.cost", """{"value":0,"label":"estimated","currency":"USD"}""")]
    [InlineData("steps.0.cost", """{"value":null,"label":"not-reported","reason":"price-table-missing"}""")]
    [InlineData("steps.0.quality", """{"value":null,"label":"not-reported","reason":"judge-failed"}""")]
    [InlineData("steps.0.quality", """{"value":1,"label":"model-judged"}""")]
    [InlineData("steps.0.quality", """{"value":5,"label":"model-judged"}""")]
    [InlineData("steps.0.joulesPerQualityPoint", """{"value":0,"label":"model-judged"}""")]
    [InlineData("totals.gco2e", """{"value":0,"label":"estimated","unit":"gCO2e"}""")]
    [InlineData("environment.cloudCostUsd", """{"value":1.25,"label":"measured","currency":"USD"}""")]
    [InlineData("baseline.meanW", """{"value":null,"label":"not-reported","reason":"insufficient-samples"}""")]
    public void Valid_shapes_of_the_other_members_are_accepted(string path, string json)
    {
        JsonNode tree = Tree(Fixture());
        Put(tree, path, json);

        AssertValid(tree);
    }

    [Fact]
    public void Non_integer_quality_is_invalid()
    {
        JsonNode tree = Tree(Fixture());
        MetricAt(tree, "steps.0.quality")["value"] = 4.5;

        AssertInvalidAt(tree, "/steps/0/quality");
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(6.0, false)]
    [InlineData(-1.0, false)]
    [InlineData(1.0, true)]
    [InlineData(3.0, true)]
    [InlineData(5.0, true)]
    public void Quality_is_a_whole_number_from_one_to_five(double value, bool valid)
    {
        JsonNode tree = Tree(Fixture());
        MetricAt(tree, "steps.0.quality")["value"] = value;

        Assert.Equal(valid, Failures(tree).Count == 0);
    }

    [Fact]
    public void A_negative_step_cost_is_invalid_and_a_step_cost_needs_a_currency_and_no_unit()
    {
        JsonNode negative = Tree(Fixture());
        Put(negative, "steps.0.cost", """{"value":-0.01,"label":"estimated","currency":"USD"}""");
        AssertInvalidAt(negative, "/steps/0/cost");

        JsonNode unit = Tree(Fixture());
        Put(unit, "steps.0.cost", """{"value":0.01,"label":"estimated","unit":"usd"}""");
        AssertInvalidAt(unit, "/steps/0/cost");

        JsonNode measured = Tree(Fixture());
        Put(measured, "steps.0.cost", """{"value":0.01,"label":"measured","currency":"USD"}""");
        AssertInvalidAt(measured, "/steps/0/cost");
    }

    [Theory]
    [InlineData("totals.costByCurrency.USD", """{"value":null,"label":"not-reported","reason":"x"}""")]
    [InlineData("totals.costByCurrency.USD", """{"value":0.5,"label":"estimated","unit":"usd"}""")]
    [InlineData("totals.costByCurrency.USD", """{"value":-0.5,"label":"estimated","currency":"USD"}""")]
    [InlineData("totals.costByCurrency.USD", """{"value":0.5,"label":"measured","currency":"USD"}""")]
    [InlineData("totals.judge.costByCurrency.USD", """{"value":null,"label":"not-reported","reason":"x"}""")]
    [InlineData("totals.judge.costByCurrency.USD", """{"value":0.5,"label":"model-judged","currency":"USD"}""")]
    public void A_cost_map_value_is_N_A_or_a_non_negative_estimate_in_a_currency(string path, string json)
    {
        JsonNode tree = Tree(Fixture());
        Put(tree, path, json);

        AssertInvalidAt(tree, "/" + path.Replace('.', '/'));
    }

    [Fact]
    public void An_empty_cost_map_and_a_map_of_several_currencies_are_valid()
    {
        JsonNode empty = Tree(Fixture());
        Put(empty, "totals.costByCurrency", "{}");
        AssertValid(empty);

        JsonNode several = Tree(Fixture());
        Put(several, "totals.costByCurrency", """{"USD":{"value":0.5,"label":"estimated","currency":"USD"},"EUR":{"value":null,"label":"N/A","reason":"no-price"}}""");
        AssertValid(several);
    }

    [Fact]
    public void Judge_item_without_judged_agent_is_invalid()
    {
        JsonNode tree = Tree(Fixture());
        Assert.True(At(tree, "judge.0").AsObject().Remove("judgedAgent"));

        AssertInvalidAt(tree, "/judge/0");
    }

    [Fact]
    public void A_step_that_names_a_judged_agent_is_invalid_and_a_blank_one_too()
    {
        JsonNode step = Tree(Fixture());
        At(step, "steps.0").AsObject()["judgedAgent"] = RunDocumentFixtures.PlannerAgent;
        AssertInvalidAt(step, "/steps/0");

        JsonNode blank = Tree(Fixture());
        At(blank, "judge.0").AsObject()["judgedAgent"] = string.Empty;
        AssertInvalidAt(blank, "/judge/0");

        JsonNode wrongType = Tree(Fixture());
        At(wrongType, "judge.0").AsObject()["judgedAgent"] = 3;
        AssertInvalidAt(wrongType, "/judge/0");
    }

    [Theory]
    [InlineData("schemaVersion", "\"2\"", "/schemaVersion")]
    [InlineData("schemaVersion", "1", "/schemaVersion")]
    [InlineData("run.gitCommit", "\"\"", "/run/gitCommit")]
    [InlineData("run.gitCommit", "null", "/run/gitCommit")]
    [InlineData("run.toolVersion", "\"\"", "/run/toolVersion")]
    [InlineData("run.tier", "\"Local\"", "/run/tier")]
    [InlineData("run.tier", "1", "/run/tier")]
    [InlineData("run.mode", "\"parallel\"", "/run/mode")]
    [InlineData("run.status", "\"done\"", "/run/status")]
    [InlineData("run.tasks", "[null]", "/run/tasks")]
    [InlineData("run.repeat", "\"1\"", "/run/repeat")]
    [InlineData("run.repeat", "1.5", "/run/repeat")]
    [InlineData("run.selfJudged", "\"yes\"", "/run/selfJudged")]
    [InlineData("environment.energyScope", "\"GPU only\"", "/environment/energyScope")]
    [InlineData("environment.models", "[null]", "/environment/models")]
    [InlineData("steps", "[null]", "/steps")]
    [InlineData("judge", "[null]", "/judge")]
    [InlineData("steps.0.outcome", "\"success\"", "/steps/0/outcome")]
    [InlineData("steps.0.tier", "\"hosted\"", "/steps/0/tier")]
    [InlineData("steps.0.method", "\"unmetered\"", "/steps/0/method")]
    [InlineData("steps.0.method", "\"Counter\"", "/steps/0/method")]
    [InlineData("steps.0.flags", "[\"made-up\"]", "/steps/0/flags")]
    [InlineData("steps.0.flags", "[\"throttled\",\"throttled\"]", "/steps/0/flags")]
    [InlineData("steps.0.flags", "\"throttled\"", "/steps/0/flags")]
    [InlineData("steps.0.throttleReasons", "[\"Overheated\"]", "/steps/0/throttleReasons")]
    [InlineData("steps.0.throttleReasons", "[null]", "/steps/0/throttleReasons")]
    [InlineData("steps.0.repeat", "0", "/steps/0/repeat")]
    [InlineData("steps.0.retries", "-1", "/steps/0/retries")]
    [InlineData("steps.0.waitMs", "-1", "/steps/0/waitMs")]
    [InlineData("steps.0.sampleCount", "-1", "/steps/0/sampleCount")]
    [InlineData("steps.0.sampleRateHz", "0", "/steps/0/sampleRateHz")]
    [InlineData("steps.0.outputChars", "-1", "/steps/0/outputChars")]
    [InlineData("steps.0.outputSha256", "\"ABCDEF\"", "/steps/0/outputSha256")]
    [InlineData("steps.0.outputSha256", "\"0000000000000000000000000000000000000000000000000000000000000000\\n\"", "/steps/0/outputSha256")]
    [InlineData("run.startUtc", "\"yesterday\"", "/run/startUtc")]
    [InlineData("steps.0.endUtc", "\"2026-13-45T99:00:00Z\"", "/steps/0/endUtc")]
    [InlineData("baseline.seconds", "0", "/baseline/seconds")]
    [InlineData("baseline.seconds", "-1", "/baseline/seconds")]
    [InlineData("baseline.sampleCount", "-1", "/baseline/sampleCount")]
    [InlineData("baseline.maxCoefficientOfVariation", "0", "/baseline/maxCoefficientOfVariation")]
    [InlineData("baseline.coefficientOfVariation", "-0.5", "/baseline/coefficientOfVariation")]
    [InlineData("steps.0.task", "\"\"", "/steps/0/task")]
    [InlineData("steps.0.agent", "\"\"", "/steps/0/agent")]
    [InlineData("steps.0.modelId", "\"\"", "/steps/0/modelId")]
    [InlineData("baseline.flags", "[\"made-up\"]", "/baseline/flags")]
    [InlineData("probe.gpuPresent", "\"yes\"", "/probe/gpuPresent")]
    public void The_vocabularies_ranges_and_types_of_the_plain_members_are_enforced(string path, string json, string location)
    {
        JsonNode tree = Tree(Fixture());
        Put(tree, path, json);

        AssertInvalidAt(tree, location);
    }

    [Fact]
    public void A_hash_of_sixty_four_lowercase_hex_digits_and_a_null_hash_are_valid()
    {
        JsonNode tree = Tree(Fixture());
        Put(tree, "steps.1.outputSha256", "\"" + RunDocumentFixtures.Sha256Hex("anything") + "\"");
        Put(tree, "steps.1.outputChars", "8");

        AssertValid(tree);
    }

    [Fact]
    public void Every_step_flag_and_every_throttle_reason_the_code_knows_is_valid()
    {
        JsonNode tree = Tree(Fixture());
        Put(tree, "steps.0.flags", new JsonArray([.. SerialisedNames<ReadingFlags>(static flag => flag != ReadingFlags.None).Select(static name => (JsonNode?)JsonValue.Create(name))]).ToJsonString());
        Put(tree, "steps.0.throttleReasons", new JsonArray([.. ThrottleReasons.AllNames.Select(static name => (JsonNode?)JsonValue.Create(name))]).ToJsonString());

        AssertValid(tree);
    }

    [Fact]
    public void The_enumerations_of_the_schema_are_the_vocabularies_of_the_code()
    {
        JsonNode schema = GeneratedSchema();
        string[] flagNames = SerialisedNames<ReadingFlags>(static flag => flag != ReadingFlags.None);

        Assert.Equal(SerialisedNames<Tier>(static _ => true), EnumOf(At(schema, "properties.run.properties.tier")));
        Assert.Equal(SerialisedNames<RunMode>(static _ => true), EnumOf(At(schema, "properties.run.properties.mode")));
        Assert.Equal(SerialisedNames<RunStatus>(static _ => true), EnumOf(At(schema, "properties.run.properties.status")));
        Assert.Equal(SerialisedNames<Tier>(static _ => true), EnumOf(At(schema, "$defs.step.properties.tier")));
        Assert.Equal(SerialisedNames<EnergyMethod>(static method => method != EnergyMethod.Unmetered), EnumOf(At(schema, "$defs.step.properties.method")));
        Assert.Equal(flagNames, EnumOf(At(schema, "$defs.step.properties.flags.items")));
        Assert.Equal(flagNames, EnumOf(At(schema, "properties.baseline.properties.flags.items")));
        Assert.Equal(ThrottleReasons.AllNames, EnumOf(At(schema, "$defs.step.properties.throttleReasons.items")));

        string[] outcomes = EnumOf(At(schema, "$defs.step.properties.outcome"));
        Assert.All(outcomes, static outcome => Assert.True(StepOutcomes.IsKnown(outcome)));
        Assert.Equal(typeof(StepOutcomes).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Count(static field => field.IsLiteral), outcomes.Length);
        Assert.True(At(schema, "$defs.step.properties.flags")["uniqueItems"]!.GetValue<bool>());
        Assert.True(At(schema, "$defs.step.properties.throttleReasons")["uniqueItems"]!.GetValue<bool>());
        Assert.True(At(schema, "properties.baseline.properties.flags")["uniqueItems"]!.GetValue<bool>());
    }

    [Fact]
    public void The_four_metric_definitions_carry_the_label_sets()
    {
        JsonNode defs = At(GeneratedSchema(), "$defs");

        Assert.Equal(["measured", "N/A", "not-reported"], EnumOf(At(defs, "metricMeasured.properties.label")));
        Assert.Equal(["estimated", "N/A", "not-reported"], EnumOf(At(defs, "metricEstimated.properties.label")));
        Assert.Equal(["model-judged", "N/A", "not-reported"], EnumOf(At(defs, "metricJudged.properties.label")));
        Assert.Equal(FiveLabels, EnumOf(At(defs, "metricAny.properties.label")));
        foreach (string name in MetricDefs)
        {
            JsonNode def = defs[name]!;
            Assert.Equal("object", def["type"]!.GetValue<string>());
            Assert.False(def["additionalProperties"]!.GetValue<bool>());
            Assert.Equal(["value", "label"], Strings(def["required"]));
        }
    }

    [Fact]
    public void Every_metric_member_refers_to_a_def()
    {
        JsonNode schema = GeneratedSchema();
        (Type Type, string Path)[] objects =
        [
            (typeof(EnvironmentInfo), "properties.environment"),
            (typeof(EnvironmentAssumptions), "properties.environment.properties.assumptions"),
            (typeof(BaselineResult), "properties.baseline"),
            (typeof(StepReading), "$defs.step"),
            (typeof(Totals), "properties.totals"),
            (typeof(JudgeTotals), "properties.totals.properties.judge"),
        ];

        int expected = 0;
        foreach ((Type type, string path) in objects)
        {
            JsonNode properties = At(schema, path)["properties"]!;
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                bool isMetric = property.PropertyType == typeof(Metric);
                bool isMap = property.PropertyType == typeof(IReadOnlyDictionary<string, Metric>);
                if (!isMetric && !isMap)
                {
                    continue;
                }

                string name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                JsonNode member = properties[name] ?? throw new XunitException($"{type.Name}.{name} has no schema.");
                string? def = DefOf(isMap ? member["additionalProperties"] : member);

                Assert.True(def is not null && MetricDefs.Contains(def), $"{type.Name}.{name} does not refer to a metric definition.");
                expected++;
            }
        }

        Assert.Equal(27, expected);

        // The three totals of the calibration definition (AJ-109) are references as well; the objects above are the ones the run file reaches.
        Assert.Equal(expected + 3, CountMetricReferences(schema));
        Assert.DoesNotContain(DefsPrefix + "metricAny", schema.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_step_schema_is_defined_once_and_steps_and_judge_pick_their_side_of_judged_agent()
    {
        JsonNode schema = GeneratedSchema();
        JsonNode step = At(schema, "$defs.step");

        Assert.Equal("object", step["type"]!.GetValue<string>());

        // The calibration definition (AJ-109) has a task member of its own; the step reading's is defined once beside it.
        JsonNode withoutCalibration = schema.DeepClone();
        Assert.True(withoutCalibration["$defs"]!.AsObject().Remove("calibration"));
        Assert.Equal(1, withoutCalibration.ToJsonString().Split("\"task\":").Length - 1);
        foreach (string list in new[] { "steps", "judge" })
        {
            JsonNode items = At(schema, "properties." + list + ".items");
            Assert.Equal("array", At(schema, "properties." + list)["type"]!.GetValue<string>());
            Assert.Contains(items["allOf"]!.AsArray(), static part => part!["$ref"]?.GetValue<string>() == "#/$defs/step");
        }

        Assert.Equal("string", At(step, "properties.judgedAgent")["type"]!.GetValue<string>());
        Assert.Equal(1, At(step, "properties.judgedAgent")["minLength"]!.GetValue<int>());
    }

    [Fact]
    public void Property_order_follows_what_the_writer_writes_and_required_follows_the_constructors()
    {
        JsonNode schema = GeneratedSchema();
        JsonNode document = Tree(Fixture());
        (string SchemaPath, string DocumentPath)[] pairs =
        [
            (string.Empty, string.Empty),
            ("properties.run", "run"),
            ("properties.environment", "environment"),
            ("properties.environment.properties.assumptions", "environment.assumptions"),
            ("properties.environment.properties.models.items", "environment.models.0"),
            ("properties.baseline", "baseline"),
            ("properties.totals", "totals"),
            ("properties.totals.properties.judge", "totals.judge"),
            ("$defs.step", "judge.0"),
            ("$defs.probe", "probe"),
        ];

        foreach ((string schemaPath, string documentPath) in pairs)
        {
            JsonNode node = At(schema, schemaPath);
            string[] written = [.. At(document, documentPath).AsObject().Select(static member => member.Key)];
            string[] described = [.. node["properties"]!.AsObject().Select(static member => member.Key)];

            Assert.True(written.SequenceEqual(described), $"{schemaPath} lists {string.Join(",", described)} but the writer writes {string.Join(",", written)}.");
        }

        Assert.Equal(["schemaVersion", "run", "environment", "probe", "baseline", "steps", "judge", "totals"], Strings(schema["required"]));
        Assert.Equal(["id", "tier", "tasks", "mode", "repeat", "startUtc", "toolVersion", "gitCommit", "status"], Strings(At(schema, "properties.run")["required"]));
    }

    [Fact]
    public void Unmapped_metric_member_fails_generation()
    {
        var withoutCost = new Dictionary<(Type, string), MetricTarget>(MetricSchemaDefs.Mapping);
        Assert.True(withoutCost.Remove((typeof(StepReading), "cost")));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => SchemaGenerator.Generate(withoutCost));
        Assert.Contains("StepReading", failure.Message, StringComparison.Ordinal);
        Assert.Contains("cost", failure.Message, StringComparison.Ordinal);
        Assert.Contains("steps", failure.Message, StringComparison.Ordinal);

        var withoutMap = new Dictionary<(Type, string), MetricTarget>(MetricSchemaDefs.Mapping);
        Assert.True(withoutMap.Remove((typeof(Totals), "costByCurrency")));
        failure = Assert.Throws<InvalidOperationException>(() => SchemaGenerator.Generate(withoutMap));
        Assert.Contains("costByCurrency", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mapping_names_a_known_definition_for_every_entry()
    {
        // 27 members of the run file and the 3 totals of the calibration document (AJ-109).
        Assert.Equal(30, MetricSchemaDefs.Mapping.Count);
        Assert.All(MetricSchemaDefs.Mapping.Values, static target => Assert.Contains(target.Def, MetricDefs));
        Assert.DoesNotContain(MetricSchemaDefs.Mapping.Values, static target => target.Def == "metricAny");
    }

    [Fact]
    public void Committed_probe_file_validates_against_probe_def()
    {
        string path = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "results", "probe-20261019T205344Z.json");
        JsonNode probe = JsonNode.Parse(File.ReadAllText(path))!;

        AssertValid(probe, SchemaTarget.Probe);
        Assert.NotEmpty(Failures(probe, SchemaTarget.RunFile));
    }

    [Fact]
    public void A_probe_that_does_not_fit_the_probe_def_is_invalid_and_a_run_file_is_not_a_probe()
    {
        string path = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "results", "probe-20261019T205344Z.json");

        JsonNode extra = JsonNode.Parse(File.ReadAllText(path))!;
        extra["surprise"] = 1;
        Assert.True(FailsAt(Failures(extra, SchemaTarget.Probe), "/surprise"));

        JsonNode missing = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(missing.AsObject().Remove("gpuPresent"));
        Assert.NotEmpty(Failures(missing, SchemaTarget.Probe));

        JsonNode wrongType = JsonNode.Parse(File.ReadAllText(path))!;
        wrongType["powerLimitW"] = "96";
        Assert.True(FailsAt(Failures(wrongType, SchemaTarget.Probe), "/powerLimitW"));

        Assert.NotEmpty(Failures(Tree(Fixture()), SchemaTarget.Probe));
        Assert.Throws<ArgumentOutOfRangeException>(() => Failures(extra, (SchemaTarget)0));
    }

    [Fact]
    public void A_document_that_is_not_an_object_is_invalid()
    {
        foreach (string json in new[] { "null", "[]", "1", "\"text\"", "{}" })
        {
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.NotEmpty(SchemaValidator.Validate(document.RootElement));
        }
    }

    [Fact]
    public void Failure_messages_never_echo_the_value_they_refuse()
    {
        const string sentinel = "SENTINEL-6f1c2b9e";
        JsonNode tree = Tree(Fixture());
        Put(tree, "run.tier", $"\"{sentinel}\"");
        Put(tree, "run.status", $"\"{sentinel}\"");
        Put(tree, "run.flags", $"[\"{sentinel}\"]");
        Put(tree, "environment.energyScope", $"\"{sentinel}\"");
        Put(tree, "steps.0.outcome", $"\"{sentinel}\"");
        Put(tree, "steps.0.outputSha256", $"\"{sentinel}\"");
        Put(tree, "steps.0.throttleReasons", $"[\"{sentinel}\"]");
        Put(tree, "steps.0.cost", $"{{\"value\":\"{sentinel}\",\"label\":\"{sentinel}\"}}");
        Put(tree, "schemaVersion", $"\"{sentinel}\"");

        IReadOnlyList<SchemaFailure> failures = Failures(tree);

        Assert.NotEmpty(failures);
        Assert.DoesNotContain(failures, static failure => failure.Message.Contains(sentinel, StringComparison.Ordinal) || failure.InstanceLocation.Contains(sentinel, StringComparison.Ordinal));
        Assert.All(failures, static failure => Assert.True(failure.InstanceLocation.Length == 0 || failure.InstanceLocation[0] == '/'));
    }

    private const string EnumMessage = "Value should match one of the values specified by the enum";
    private const string ExcludedMessage = "The schema excludes this combination of members.";

    // The eleven mutations of the measured failure table: the fixture with one change each, and the exact ordered failures the validator reports for it.
    private static readonly (Action<JsonNode> Mutate, (string Location, string Message)[] Expected)[] ExactCases =
    [
        (static tree => MetricAt(tree, "steps.1.cost")["value"] = 0.5, [("/steps/1/cost/value", "Value is \"number\" but should be \"null\"")]),
        (static tree => MetricAt(tree, "steps.0.quality")["label"] = "measured", [("/steps/0/quality/label", EnumMessage)]),
        (static tree => MetricAt(tree, "steps.1.cost").Remove("reason"), [("/steps/1/cost", "Required properties [\"reason\"] are not present")]),
        (static tree => At(tree, "steps.0").AsObject()["surprise"] = 1, [("/steps/0/surprise", "All values fail against the false schema")]),
        (static tree => Put(tree, "run.flags", "[\"made-up\"]"), [("/run/flags/0", EnumMessage)]),
        (static tree => MetricAt(tree, "steps.0.quality")["value"] = 4.5, [("/steps/0/quality/value", "Value is \"number\" but should be \"integer\"")]),
        (static tree => Put(tree, "totals.costByCurrency.USD", """{"value":null,"label":"not-reported","reason":"x"}"""), [("/totals/costByCurrency/USD/label", EnumMessage)]),
        (static tree => MetricAt(tree, "steps.0.latencyMs")["currency"] = "USD", [("/steps/0/latencyMs", "Exactly one alternative may hold, but several do.")]),
        (static tree => MetricAt(tree, "steps.0.joulesPerQualityPoint")["unit"] = "point", [("/steps/0/joulesPerQualityPoint", ExcludedMessage)]),
        (static tree => At(tree, "steps.0").AsObject()["judgedAgent"] = RunDocumentFixtures.PlannerAgent, [("/steps/0", ExcludedMessage)]),
        (static tree => MetricAt(tree, "steps.0.latencyMs").Remove("unit"), [("/steps/0/latencyMs", "Required properties [\"unit\"] are not present"), ("/steps/0/latencyMs", "Required properties [\"currency\"] are not present")]),
    ];

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
    [InlineData(9)]
    [InlineData(10)]
    public void The_failures_of_a_mutated_document_are_exactly_these_in_this_order(int row)
    {
        JsonNode tree = Tree(Fixture());
        ExactCases[row].Mutate(tree);

        Assert.Equal(ExactCases[row].Expected, Failures(tree).Select(static failure => (failure.InstanceLocation, failure.Message)));
    }

    [Theory]
    [InlineData("run.startUtc", "\"yesterday\"", "/run/startUtc")]
    [InlineData("steps.0.endUtc", "\"2026-13-45T99:00:00Z\"", "/steps/0/endUtc")]
    public void The_date_time_format_is_asserted(string path, string json, string location)
    {
        JsonNode tree = Tree(Fixture());
        Put(tree, path, json);

        Assert.Equal([(location, "Value does not match format \"date-time\"")], Failures(tree).Select(static failure => (failure.InstanceLocation, failure.Message)));
    }

    [Fact]
    public void An_invalid_document_over_the_value_cap_is_one_root_failure_and_its_failures_are_not_listed()
    {
        JsonNode over = Tree(Fixture());
        over["steps"] = new JsonArray([.. Enumerable.Range(0, SchemaValidator.MaxValues).Select(static _ => (JsonNode?)new JsonObject())]);

        // The document is invalid in many places (every empty step lacks its members), so a single line at the root proves that no list was built.
        SchemaFailure only = Assert.Single(Failures(over));
        Assert.Equal(string.Empty, only.InstanceLocation);
        Assert.Equal("The document is invalid and holds more than 100000 values; the failures are not listed.", only.Message);
        Assert.Equal(SchemaValidator.TooLarge, only.Message);

        // Control: the same shape below the cap is evaluated and lists the members each step lacks.
        JsonNode under = Tree(Fixture());
        under["steps"] = new JsonArray([.. Enumerable.Range(0, 10).Select(static _ => (JsonNode?)new JsonObject())]);
        Assert.True(Failures(under).Count > 1);
        Assert.DoesNotContain(Failures(under), static failure => failure.Message == SchemaValidator.TooLarge);
    }

    [Fact]
    public void The_value_cap_counts_the_members_of_nested_objects()
    {
        // The bulk sits in the members of an object below the root (run), not in an array and not in the members of the root.
        JsonNode nested = Tree(Fixture());
        var run = new JsonObject();
        for (int index = 0; index < SchemaValidator.MaxValues; index++)
        {
            run["m" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = 1;
        }

        nested["run"] = run;

        SchemaFailure only = Assert.Single(Failures(nested));
        Assert.Equal(string.Empty, only.InstanceLocation);
        Assert.Equal("The document is invalid and holds more than 100000 values; the failures are not listed.", only.Message);
    }

    [Fact]
    public void A_valid_document_over_the_value_cap_validates()
    {
        // A run with many repeats legitimately writes a file this large: 100,000 distinct task names are valid against the schema and put the document over the cap.
        JsonNode large = Tree(Fixture());
        large["run"]!["tasks"] = new JsonArray([.. Enumerable.Range(0, SchemaValidator.MaxValues).Select(static index => (JsonNode?)JsonValue.Create("task-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)))]);

        Assert.Empty(Failures(large));
    }

    [Fact]
    public void The_value_cap_counts_the_root_and_every_element_and_member_and_the_boundary_is_inclusive()
    {
        // The root plus 99,999 numbers is exactly the cap: its failures are listed (it is invalid because it is not an object); one more number is over it, and the invalid document gets the one root failure.
        string atCap = "[" + string.Join(',', Enumerable.Repeat("1", SchemaValidator.MaxValues - 1)) + "]";
        string overCap = "[" + string.Join(',', Enumerable.Repeat("1", SchemaValidator.MaxValues)) + "]";

        using JsonDocument at = JsonDocument.Parse(atCap);
        using JsonDocument over = JsonDocument.Parse(overCap);

        SchemaFailure evaluated = Assert.Single(SchemaValidator.Validate(at.RootElement));
        Assert.NotEqual(SchemaValidator.TooLarge, evaluated.Message);
        Assert.Equal(SchemaValidator.TooLarge, Assert.Single(SchemaValidator.Validate(over.RootElement)).Message);

        // Members count as well as elements: an object with 100,000 members is over the cap.
        string members = "{" + string.Join(',', Enumerable.Range(0, SchemaValidator.MaxValues).Select(static index => $"\"m{index}\":1")) + "}";
        using JsonDocument wide = JsonDocument.Parse(members);
        Assert.Equal(SchemaValidator.TooLarge, Assert.Single(SchemaValidator.Validate(wide.RootElement)).Message);
    }

    [Fact]
    public void A_valid_document_returns_no_failure_at_all()
    {
        using JsonDocument document = JsonDocument.Parse(Text(Fixture()));

        Assert.Empty(SchemaValidator.Validate(document.RootElement));
    }

    [Fact]
    public void A_value_nested_too_deeply_for_the_enum_check_is_one_root_failure_and_not_an_exception()
    {
        const int levels = 70;
        string nested = new string('[', levels) + new string(']', levels);
        string deep = Text(Fixture()).Replace("\"flags\": []", $"\"flags\": [{nested}]", StringComparison.Ordinal);
        Assert.Contains(nested, deep, StringComparison.Ordinal);

        using JsonDocument document = JsonDocument.Parse(deep, new JsonDocumentOptions { MaxDepth = 256 });
        SchemaFailure only = Assert.Single(SchemaValidator.Validate(document.RootElement));

        Assert.Equal(string.Empty, only.InstanceLocation);
        Assert.Equal(SchemaValidator.NotEvaluable, only.Message);
    }

    [Fact]
    public void Embedded_schema_equals_committed_file()
    {
        Assembly assembly = typeof(SchemaValidator).Assembly;

        Assert.Contains(SchemaValidator.ResourceName, assembly.GetManifestResourceNames());
        using Stream stream = assembly.GetManifestResourceStream(SchemaValidator.ResourceName) ?? throw new XunitException("The schema is not embedded in the Reporting assembly.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.Equal(File.ReadAllText(CommittedSchemaPath()).ReplaceLineEndings("\n"), reader.ReadToEnd().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Only_reporting_references_the_schema_package()
    {
        const string package = "JsonSchema.Net";
        Assembly core = typeof(RunDocument).Assembly;
        Assembly cli = Assembly.Load(new AssemblyName("AgentJoules.Cli"));

        Assert.DoesNotContain(core.GetReferencedAssemblies(), static name => string.Equals(name.Name, package, StringComparison.Ordinal));
        Assert.DoesNotContain(cli.GetReferencedAssemblies(), static name => string.Equals(name.Name, package, StringComparison.Ordinal));
        Assert.Contains(typeof(SchemaValidator).Assembly.GetReferencedAssemblies(), static name => string.Equals(name.Name, package, StringComparison.Ordinal));
    }

    // ---- AJ-080: AJ validate, the run file validator and the committed fixture run files ---------------------------------------------------------------------------------------

    private const string FixtureUpdateVariable = "AGENTJOULES_UPDATE_FIXTURES";
    private const string FixtureUpdateHint = "Regenerate the files with " + FixtureUpdateVariable + "=1 dotnet test --filter \"FullyQualifiedName~CommittedFixtureRunFilesMatchGenerated\" and commit them.";
    private const string FixtureUpdateRewrote = "rewrote the committed fixture run files; re-run without " + FixtureUpdateVariable;
    private const string FixtureUpdateRefusedInCi = FixtureUpdateVariable + " is set in a CI run (the CI variable is set and not empty); the fixture files are not rewritten there. Unset it and commit the regenerated files.";
    private const string ValidateFolder = "runs";
    private const string ProbeFileName = "probe-20261019T205344Z.json";
    private const string CalibrationFileName = "calibration-20261019T205344Z.json";

    private static readonly Tier[] CommittedTiers = [Tier.Local, Tier.Nvidia, Tier.Nebius];

    private static readonly string[] CorruptedMembers = ["cost", "quality", "latencyMs"];

    private static readonly string[][] ConfigArguments = [["--config"], [ValidateFolder, "--config"], ["--config", "appsettings.json"], [ValidateFolder, "--config", "appsettings.json"]];

    private static string CommittedFixtureFolder() => Path.Combine(ConfigurationFixtures.RepositoryRoot(), "tests", "AgentJoules.TestSupport", "Fixtures", "results");

    private static string[] LinesOf(string text) => [.. text.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0)];

    private static Task<CliRunResult> ValidateAsync(TempWorkspace workspace, params string[] arguments) =>
        CliHarness.RunTokensAsync(["validate", .. arguments], new CliHostOverrides(WorkingDirectory: workspace.Root));

    private static string WriteCommitted(TempWorkspace workspace, string folder, Tier tier) =>
        workspace.WriteFile(Path.Combine(folder, RunDocumentFixtures.CommittedFileName(tier)), RunDocumentFixtures.CommittedText(tier));

    // The N/A metric with a number of the scenario: the cost of the first step.
    private static string WithCorruptCost(string json) =>
        RunDocumentFixtures.Edit(json, static root =>
        {
            JsonObject cost = root["steps"]![0]!["cost"]!.AsObject();
            cost["label"] = "N/A";
            cost["value"] = 0.5;
        });

    // Every step gets an N/A cost and an N/A quality that hold a number: more failures than a file may list.
    private static string WithManyCorruptSteps(string json) =>
        RunDocumentFixtures.Edit(json, static root =>
        {
            foreach (JsonNode? step in root["steps"]!.AsArray())
            {
                foreach (string member in CorruptedMembers)
                {
                    JsonObject metric = step![member]!.AsObject();
                    metric["label"] = "N/A";
                    metric["value"] = 0.5;
                }
            }
        });

    private static void AssertSafeLine(string line)
    {
        foreach (char character in line)
        {
            Assert.False(
                char.IsControl(character) || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format,
                "A failure line holds a control or format character.");
        }
    }

    [Fact]
    public async Task Validate_counts_files()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        foreach (Tier tier in CommittedTiers)
        {
            _ = WriteCommitted(workspace, ValidateFolder, tier);
        }

        _ = workspace.WriteFile(Path.Combine(ValidateFolder, ProbeFileName), "{}\n");

        CliRunResult result = await ValidateAsync(workspace, ValidateFolder);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Validated 3 files"], LinesOf(result.Stdout));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Validates_without_config_or_docs()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = WriteCommitted(workspace, ValidateFolder, Tier.Local);

        CliRunResult result = await ValidateAsync(workspace, ValidateFolder);

        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "config")));
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "docs")));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Validated 1 files"], LinesOf(result.Stdout));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Corrupted_file_prints_its_location_then_the_count()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        _ = workspace.WriteFile(Path.Combine(ValidateFolder, name), WithCorruptCost(RunDocumentFixtures.CommittedText(Tier.Local)));

        CliRunResult result = await ValidateAsync(workspace, ValidateFolder);

        Assert.Equal(2, result.ExitCode);
        string[] lines = LinesOf(result.Stdout);
        Assert.True(lines.Length >= 2, "The output holds a failure line and the count line.");
        Assert.Equal("Validated 1 files", lines[^1]);
        string[] failures = lines[..^1];
        Assert.All(failures, line => Assert.StartsWith(name + ": ", line, StringComparison.Ordinal));
        Assert.Contains(failures, line => line[(name.Length + 2)..].StartsWith("/steps/0/cost", StringComparison.Ordinal));
        Assert.Equal(["error: 1 of 1 run files are invalid (ConfigurationException)"], LinesOf(result.Stderr));
        Assert.DoesNotContain(workspace.Root, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_lines_are_ordered_by_file_name_and_the_error_counts_the_files()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string local = RunDocumentFixtures.CommittedFileName(Tier.Local);
        string nebius = RunDocumentFixtures.CommittedFileName(Tier.Nebius);
        _ = workspace.WriteFile(Path.Combine(ValidateFolder, "20260102T000000Z-nvidia.json"), WithCorruptCost(RunDocumentFixtures.CommittedText(Tier.Nvidia)));
        _ = workspace.WriteFile(Path.Combine(ValidateFolder, local), WithCorruptCost(RunDocumentFixtures.CommittedText(Tier.Local)));
        _ = WriteCommitted(workspace, ValidateFolder, Tier.Nebius);

        CliRunResult result = await ValidateAsync(workspace, ValidateFolder);

        Assert.Equal(2, result.ExitCode);
        string[] lines = LinesOf(result.Stdout);
        Assert.Equal("Validated 3 files", lines[^1]);
        int firstFile = Array.FindIndex(lines, line => line.StartsWith(local + ": ", StringComparison.Ordinal));
        int secondFile = Array.FindIndex(lines, line => line.StartsWith("20260102T000000Z-nvidia.json: ", StringComparison.Ordinal));
        Assert.True(firstFile >= 0 && secondFile > firstFile, "The failures of the earlier file name come first.");
        Assert.DoesNotContain(lines, line => line.StartsWith(nebius + ": ", StringComparison.Ordinal));
        Assert.Equal(["error: 2 of 3 run files are invalid (ConfigurationException)"], LinesOf(result.Stderr));
    }

    [Fact]
    public async Task A_file_lists_its_first_20_failures_then_the_number_of_the_rest()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        string text = WithManyCorruptSteps(RunDocumentFixtures.CommittedText(Tier.Local));
        _ = workspace.WriteFile(Path.Combine(ValidateFolder, name), text);
        using JsonDocument document = JsonDocument.Parse(text);
        int total = SchemaValidator.Validate(document.RootElement).Count;
        Assert.True(total > RunFileValidator.MaxFailureLines, $"The file must hold more than {RunFileValidator.MaxFailureLines} failures, but holds {total}.");

        CliRunResult result = await ValidateAsync(workspace, ValidateFolder);

        Assert.Equal(2, result.ExitCode);
        string[] lines = LinesOf(result.Stdout);
        Assert.Equal(RunFileValidator.MaxFailureLines + 2, lines.Length);
        Assert.Equal($"{name}: (root) and {total - RunFileValidator.MaxFailureLines} more failures", lines[RunFileValidator.MaxFailureLines]);
        Assert.Equal("Validated 1 files", lines[^1]);
        Assert.Equal(["error: 1 of 1 run files are invalid (ConfigurationException)"], LinesOf(result.Stderr));
    }

    [Fact]
    public async Task Non_json_run_file_exits_2_naming_it()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        _ = workspace.WriteFile(Path.Combine(ValidateFolder, name), "not json");

        CliRunResult result = await ValidateAsync(workspace, ValidateFolder);

        Assert.Equal(2, result.ExitCode);
        string[] lines = LinesOf(result.Stdout);
        Assert.Equal(2, lines.Length);
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(name) + @": \(root\) not valid JSON at line 1, byte \d+$", lines[0]);
        Assert.Equal("Validated 1 files", lines[1]);
        Assert.Equal(["error: 1 of 1 run files are invalid (ConfigurationException)"], LinesOf(result.Stderr));
    }

    [Fact]
    public async Task Missing_path_exits_2_naming_it()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        CliRunResult result = await ValidateAsync(workspace, "no-such-folder");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        string line = Assert.Single(LinesOf(result.Stderr));
        Assert.StartsWith("error: ", line, StringComparison.Ordinal);
        Assert.Contains("no-such-folder", line, StringComparison.Ordinal);
        Assert.EndsWith("(ConfigurationException)", line, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_path_is_echoed_bounded()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = new('a', 1000);

        CliRunResult result = await ValidateAsync(workspace, name);

        Assert.Equal(2, result.ExitCode);
        string line = Assert.Single(LinesOf(result.Stderr));
        Assert.Contains(new string('a', 256) + "...", line, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 257), line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Named_non_run_file_exits_2()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine(ValidateFolder, ProbeFileName), "{}\n");

        CliRunResult result = await ValidateAsync(workspace, Path.Combine(ValidateFolder, ProbeFileName));

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        string line = Assert.Single(LinesOf(result.Stderr));
        Assert.Contains(ProbeFileName, line, StringComparison.Ordinal);
        Assert.EndsWith("(ConfigurationException)", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_run_file_is_validated_alone()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        foreach (Tier tier in CommittedTiers)
        {
            _ = WriteCommitted(workspace, ValidateFolder, tier);
        }

        CliRunResult result = await ValidateAsync(workspace, Path.Combine(ValidateFolder, RunDocumentFixtures.CommittedFileName(Tier.Nebius)));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Validated 1 files"], LinesOf(result.Stdout));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Default_path_is_results()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = WriteCommitted(workspace, "results", Tier.Nvidia);
        _ = WriteCommitted(workspace, "other", Tier.Local);
        _ = workspace.WriteFile(Path.Combine("results", ProbeFileName), "{}\n");

        CliRunResult result = await ValidateAsync(workspace);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Validated 1 files"], LinesOf(result.Stdout));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Default_path_that_is_missing_exits_2_naming_results()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        CliRunResult result = await ValidateAsync(workspace);

        Assert.Equal(2, result.ExitCode);
        string line = Assert.Single(LinesOf(result.Stderr));
        Assert.Contains("results", line, StringComparison.Ordinal);
        Assert.EndsWith("(ConfigurationException)", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_folder_validates_0_files()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine(ValidateFolder, ProbeFileName), "{}\n");
        _ = workspace.WriteFile(Path.Combine(ValidateFolder, CalibrationFileName), "{}\n");
        _ = workspace.WriteFile(Path.Combine(ValidateFolder, "notes.json"), "not json");

        CliRunResult result = await ValidateAsync(workspace, ValidateFolder);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Validated 0 files"], LinesOf(result.Stdout));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Only_files_that_match_the_run_file_pattern_are_counted()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = WriteCommitted(workspace, ValidateFolder, Tier.Local);
        foreach (string ignored in new[] { ProbeFileName, CalibrationFileName, "20260101T000000Z-cloudgpu-baseline.json.bak", "20260101T000000Z-bogus.json", "20260101T000000Z-local.jsonl", "readme.txt" })
        {
            _ = workspace.WriteFile(Path.Combine(ValidateFolder, ignored), "not json");
        }

        CliRunResult result = await ValidateAsync(workspace, ValidateFolder);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Validated 1 files"], LinesOf(result.Stdout));
    }

    [Fact]
    public async Task Config_flag_parses_with_zero_or_one_value()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = WriteCommitted(workspace, ValidateFolder, Tier.Local);

        foreach (string[] arguments in ConfigArguments)
        {
            CliRunResult result = await ValidateAsync(workspace, arguments);

            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Stdout);
            string line = Assert.Single(LinesOf(result.Stderr));
            Assert.EndsWith("(ConfigurationException)", line, StringComparison.Ordinal);
            Assert.DoesNotContain("arrives in M4", line, StringComparison.Ordinal);
            Assert.DoesNotContain("ParseError", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unknown_option_exits_2_through_the_parse_check()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        CliRunResult result = await ValidateAsync(workspace, "--bogus");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        string line = Assert.Single(LinesOf(result.Stderr));
        Assert.EndsWith("(ParseError)", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_over_16_MiB_is_one_line_and_exit_2()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        string path = workspace.WriteFile(Path.Combine(ValidateFolder, name), string.Empty);
        byte[] big = new byte[RunFileValidator.MaxBytes + 1];
        Array.Fill(big, (byte)' ');
        File.WriteAllBytes(path, big);

        CliRunResult result = await ValidateAsync(workspace, ValidateFolder);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal([$"{name}: (root) is larger than 16777216 bytes", "Validated 1 files"], LinesOf(result.Stdout));
        Assert.Equal(["error: 1 of 1 run files are invalid (ConfigurationException)"], LinesOf(result.Stderr));
    }

    [Fact]
    public async Task Committed_fixture_folder_validates_3_files()
    {
        CliRunResult result = await CliHarness.RunTokensAsync(
            ["validate", RunDocumentFixtures.CommittedFolder],
            new CliHostOverrides(WorkingDirectory: ConfigurationFixtures.RepositoryRoot()));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Validated 3 files"], LinesOf(result.Stdout));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public void Check_returns_no_line_for_a_valid_document()
    {
        foreach (Tier tier in CommittedTiers)
        {
            Assert.Empty(RunFileValidator.Check(RunDocumentFixtures.CommittedFileName(tier), RunDocumentFixtures.CommittedText(tier)));
        }
    }

    [Fact]
    public void Check_names_the_position_of_a_syntax_error_and_echoes_nothing()
    {
        const string name = "20260101T000000Z-local.json";

        string line = Assert.Single(RunFileValidator.Check(name, "{\n  \"secret-word\": nope\n}"));

        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(name) + @": \(root\) not valid JSON at line 2, byte \d+$", line);
        Assert.DoesNotContain("secret-word", line, StringComparison.Ordinal);
        Assert.DoesNotContain("nope", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_prints_the_empty_location_as_root()
    {
        string line = Assert.Single(RunFileValidator.Check("20260101T000000Z-local.json", "[]"));

        Assert.StartsWith("20260101T000000Z-local.json: (root) ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_lists_one_line_per_failure_with_the_location_then_the_message()
    {
        const string name = "20260101T000000Z-local.json";
        string text = WithCorruptCost(RunDocumentFixtures.CommittedText(Tier.Local));
        using JsonDocument document = JsonDocument.Parse(text);
        IReadOnlyList<SchemaFailure> failures = SchemaValidator.Validate(document.RootElement);
        Assert.NotEmpty(failures);

        IReadOnlyList<string> lines = RunFileValidator.Check(name, text);

        Assert.Equal(failures.Count, lines.Count);
        for (int index = 0; index < failures.Count; index++)
        {
            string location = failures[index].InstanceLocation.Length == 0 ? "(root)" : failures[index].InstanceLocation;
            Assert.Equal($"{name}: {location} {failures[index].Message}", lines[index]);
        }
    }

    [Fact]
    public void Check_bounds_a_hostile_location_and_message()
    {
        const string name = "20260101T000000Z-local.json";
        string hostile = new string('x', 100_000) + new string((char)0x202E, 1) + new string((char)1, 1) + "\n";
        string text = RunDocumentFixtures.Edit(RunDocumentFixtures.CommittedText(Tier.Local), root => root["steps"]![0]![hostile] = 1);

        IReadOnlyList<string> lines = RunFileValidator.Check(name, text);

        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            AssertSafeLine(line);
            Assert.True(line.Length <= name.Length + 2 + 259 + 1 + 163, $"A failure line is {line.Length} characters long.");
            Assert.DoesNotContain(new string('x', 300), line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Check_caps_the_lines_of_a_file()
    {
        const string name = "20260101T000000Z-local.json";
        string text = WithManyCorruptSteps(RunDocumentFixtures.CommittedText(Tier.Local));
        using JsonDocument document = JsonDocument.Parse(text);
        int total = SchemaValidator.Validate(document.RootElement).Count;
        Assert.True(total > RunFileValidator.MaxFailureLines);

        IReadOnlyList<string> lines = RunFileValidator.Check(name, text);

        Assert.Equal(RunFileValidator.MaxFailureLines + 1, lines.Count);
        Assert.Equal($"{name}: (root) and {total - RunFileValidator.MaxFailureLines} more failures", lines[^1]);
    }

    [Fact]
    public void Check_reads_the_document_through_the_model_after_the_schema_passed()
    {
        const string name = "20260101T000000Z-local.json";

        // A second judge reading of the same step: every element is a valid reading, so the schema passes, and the model refuses the pairing.
        string text = RunDocumentFixtures.Edit(
            RunDocumentFixtures.CommittedText(Tier.Local),
            static root =>
            {
                JsonArray judge = root["judge"]!.AsArray();
                judge.Add(JsonNode.Parse(judge[0]!.ToJsonString()));
            });
        using JsonDocument document = JsonDocument.Parse(text);
        Assert.Empty(SchemaValidator.Validate(document.RootElement));
        _ = AssertReadFails(text);

        string line = Assert.Single(RunFileValidator.Check(name, text));

        Assert.StartsWith(name + ": (model) ", line, StringComparison.Ordinal);
        Assert.Contains("judge", line, StringComparison.Ordinal);
        AssertSafeLine(line);
    }

    [Fact]
    public async Task CheckFileAsync_reads_a_file_of_exactly_16_MiB()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        byte[] content = new byte[RunFileValidator.MaxBytes];
        Array.Fill(content, (byte)' ');
        byte[] document = Encoding.ASCII.GetBytes(RunDocumentFixtures.CommittedText(Tier.Local));
        document.CopyTo(content, 0);
        string path = workspace.WriteFile(name, string.Empty);
        File.WriteAllBytes(path, content);

        IReadOnlyList<string> lines = await RunFileValidator.CheckFileAsync(path, CancellationToken.None);

        Assert.Empty(lines);
    }

    [Fact]
    public async Task CheckFileAsync_reports_a_file_that_is_not_UTF_8_or_cannot_be_read_as_one_line()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        string path = workspace.WriteFile(name, string.Empty);
        File.WriteAllBytes(path, [0x7B, 0xFF, 0xFE, 0x7D]);

        Assert.Equal([$"{name}: (root) is not valid UTF-8"], await RunFileValidator.CheckFileAsync(path, CancellationToken.None));

        string missing = Path.Combine(workspace.Root, "20260101T000000Z-nvidia.json");
        Assert.Equal(["20260101T000000Z-nvidia.json: (root) could not be read"], await RunFileValidator.CheckFileAsync(missing, CancellationToken.None));
    }

    [Theory]
    [InlineData(Tier.Local)]
    [InlineData(Tier.Nvidia)]
    [InlineData(Tier.Nebius)]
    public void Committed_fixture_holds_what_the_pins_say(Tier tier)
    {
        RunDocument document = RunDocumentFixtures.Committed(tier);
        string tierName = tier.ToString().ToLowerInvariant();

        Assert.Equal($"fixture-20260101T000000Z-{tierName}", document.Run.Id);
        Assert.Equal("fixture", document.Run.GitCommit);
        Assert.Equal("0.0.0-fixture", document.Run.ToolVersion);
        Assert.Equal(RunStatus.Completed, document.Run.Status);
        Assert.Equal(RunDocumentFixtures.CommittedTaskNames, document.Run.Tasks);
        Assert.Equal(1, document.Run.Repeat);
        Assert.Equal(12, document.Steps.Count);
        Assert.Equal(12, document.Judge.Count);
        Assert.Equal(AgentCatalog.Names, document.Steps.Take(4).Select(static step => step.Agent));
        for (int index = 0; index < document.Steps.Count; index++)
        {
            Assert.Equal(RunDocumentFixtures.CommittedStartUtc.AddSeconds(2 * index), document.Steps[index].StartUtc);
            Assert.Equal(tier, document.Steps[index].Tier);
            Assert.Contains("nemotron", document.Steps[index].ModelId, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-baseline", document.Steps[index].ModelId, StringComparison.Ordinal);
        }

        Assert.All(document.Judge, reading =>
        {
            Assert.Equal(Tier.Nvidia, reading.Tier);
            Assert.NotNull(reading.JudgedAgent);
            Assert.Equal(MetricLabel.NotApplicable, reading.GrossJoules.Label);
            Assert.Equal("hosted-tier", reading.GrossJoules.Reason);
        });

        // The totals are the ones the assembler computes from the steps, never hand-built numbers.
        Totals expected = RunAssembler.ComputeTotals(tier, RunMode.Serialized, document.Steps, document.Judge, 24000.0);
        Assert.Null(ResultsEquality.FirstDifference(expected, document.Totals));
        Assert.Equal(tier == Tier.Nvidia, document.Run.SelfJudged);

        switch (tier)
        {
            case Tier.Local:
                Assert.NotNull(document.Probe);
                Assert.NotNull(document.Baseline);
                Assert.All(document.Steps, static step =>
                {
                    Assert.Equal(MetricLabel.Measured, step.GrossJoules.Label);
                    Assert.Equal(EnergyMethod.Counter, step.Method);
                    Assert.Equal(MetricLabel.Estimated, step.Cost.Label);
                    Assert.Equal("EUR", step.Cost.Currency);
                    Assert.Equal(MetricLabel.ModelJudged, step.JoulesPerQualityPoint.Label);
                    Assert.Equal(MetricLabel.Estimated, step.CostPerQualityPoint.Label);
                });
                break;
            case Tier.Nvidia:
                Assert.Null(document.Probe);
                Assert.Null(document.Baseline);
                Assert.All(document.Steps, static step =>
                {
                    Assert.Equal("hosted-tier", step.GrossJoules.Reason);
                    Assert.Equal(MetricLabel.NotApplicable, step.Cost.Label);
                    Assert.Equal("free-tier-no-price", step.Cost.Reason);
                });
                StepReading failed = Assert.Single(document.Steps, static step => step.Quality.Label == MetricLabel.NotApplicable);
                Assert.Equal("judge-failed: HttpRequestException", failed.Quality.Reason);
                break;
            default:
                Assert.Null(document.Probe);
                Assert.Null(document.Baseline);
                Assert.All(document.Steps, static step =>
                {
                    Assert.Equal(MetricLabel.Estimated, step.Cost.Label);
                    Assert.Equal("USD", step.Cost.Currency);
                });
                break;
        }
    }

    [Fact]
    public void CommittedFixtureRunFilesMatchGenerated()
    {
        CheckOrUpdateFixtures(
            CommittedFixtureFolder(),
            Environment.GetEnvironmentVariable(FixtureUpdateVariable),
            Environment.GetEnvironmentVariable("CI"));
    }

    // The drift check of the three committed fixture run files and its update path: an update run (the variable is "1") rewrites the files with the bytes the results writer would produce and never ends green,
    // and never writes in a CI run, like the schema drift test.
    private static void CheckOrUpdateFixtures(string folder, string? updateValue, string? ci)
    {
        if (string.Equals(updateValue, "1", StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(ci))
            {
                throw new XunitException(FixtureUpdateRefusedInCi);
            }

            _ = Directory.CreateDirectory(folder);
            foreach (Tier tier in CommittedTiers)
            {
                File.WriteAllBytes(Path.Combine(folder, RunDocumentFixtures.CommittedFileName(tier)), Encoding.ASCII.GetBytes(RunDocumentFixtures.CommittedText(tier)));
            }

            throw new XunitException(FixtureUpdateRewrote);
        }

        foreach (Tier tier in CommittedTiers)
        {
            string name = RunDocumentFixtures.CommittedFileName(tier);
            string path = Path.Combine(folder, name);
            if (!File.Exists(path))
            {
                throw new XunitException($"{RunDocumentFixtures.CommittedFolder}/{name} is missing. {FixtureUpdateHint}");
            }

            string committed = File.ReadAllText(path);
            CommittedFileDrift.AssertNoDrift(committed, RunDocumentFixtures.CommittedText(tier), $"{RunDocumentFixtures.CommittedFolder}/{name}", FixtureUpdateHint);

            // The trees are equal; the bytes must be too, so a change of formatting is drift as well.
            if (!string.Equals(committed, RunDocumentFixtures.CommittedText(tier), StringComparison.Ordinal))
            {
                throw new XunitException($"{RunDocumentFixtures.CommittedFolder}/{name} holds the same document in other bytes (formatting or line endings). {FixtureUpdateHint}");
            }
        }
    }

    [Fact]
    public void The_fixture_update_run_never_writes_in_a_CI_run_and_never_ends_green()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string folder = Path.Combine(workspace.Root, "fixtures");

        Assert.Equal(FixtureUpdateRefusedInCi, Assert.Throws<XunitException>(() => CheckOrUpdateFixtures(folder, "1", "true")).Message);
        Assert.False(Directory.Exists(folder));

        Assert.Equal(FixtureUpdateRewrote, Assert.Throws<XunitException>(() => CheckOrUpdateFixtures(folder, "1", null)).Message);
        Assert.Equal(3, Directory.GetFiles(folder).Length);

        // The plain run is green on the rewritten files and a drift failure that names the variable on a stale one.
        CheckOrUpdateFixtures(folder, null, "true");
        string stale = Path.Combine(folder, RunDocumentFixtures.CommittedFileName(Tier.Local));
        File.WriteAllText(stale, "{}\n");
        XunitException drift = Assert.Throws<XunitException>(() => CheckOrUpdateFixtures(folder, null, null));
        Assert.Contains(FixtureUpdateVariable, drift.Message, StringComparison.Ordinal);

        // The same document in other bytes (line endings) is drift too.
        File.WriteAllText(stale, RunDocumentFixtures.CommittedText(Tier.Local).Replace("\n", "\r\n", StringComparison.Ordinal));
        XunitException formatting = Assert.Throws<XunitException>(() => CheckOrUpdateFixtures(folder, null, null));
        Assert.Contains("other bytes", formatting.Message, StringComparison.Ordinal);
        Assert.Contains(FixtureUpdateVariable, formatting.Message, StringComparison.Ordinal);
        File.Delete(stale);
        Assert.Contains(FixtureUpdateVariable, Assert.Throws<XunitException>(() => CheckOrUpdateFixtures(folder, null, null)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Committed_fixtures_validate_against_the_schema_and_read_through_the_model()
    {
        foreach (Tier tier in CommittedTiers)
        {
            string name = RunDocumentFixtures.CommittedFileName(tier);
            byte[] bytes = File.ReadAllBytes(Path.Combine(CommittedFixtureFolder(), name));
            string text = Encoding.UTF8.GetString(bytes);

            Assert.True(bytes.Length > 0 && bytes.All(static value => value < 0x80), $"{name} must be ASCII.");
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.Equal((byte)'\n', bytes[^1]);
            Assert.NotEqual((byte)'\n', bytes[^2]);
            Assert.DoesNotContain("://", text, StringComparison.Ordinal);

            using JsonDocument document = JsonDocument.Parse(text);
            Assert.True(SchemaValidator.Validate(document.RootElement).Count == 0, $"{name}: {Describe(SchemaValidator.Validate(document.RootElement))}");
            ResultsEquality.AssertEqual(RunDocumentFixtures.Committed(tier), RunDocumentFixtures.Read(text));
            Assert.Empty(RunFileValidator.Check(name, text));
        }
    }

    // ---- AJ-080 fix round -----------------------------------------------------------------------------------------------------------------------------------------------

    private const string LocalFileName = "20260101T000000Z-local.json";

    // The number is written into the text as it is (a JSON tree cannot hold 1e400): the first member of that name gets the raw value.
    private static string WithRawNumber(string json, string member, string raw)
    {
        var pattern = new System.Text.RegularExpressions.Regex("(\"" + member + "\":\\s*)[^,\\r\\n]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.Matches(pattern, json);
        return pattern.Replace(json, "${1}" + raw, 1);
    }

    // k unknown members spread over the steps: the schema reports each at its own pointer, so the file holds exactly k failures.
    private static string WithUnknownStepMembers(string json, int count) =>
        RunDocumentFixtures.Edit(json, root =>
        {
            JsonArray steps = root["steps"]!.AsArray();
            for (int index = 0; index < count; index++)
            {
                steps[index % steps.Count]!.AsObject()["unknown" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = 1;
            }
        });

    private static string FormatFailure(string name, SchemaFailure failure) =>
        $"{name}: {(failure.InstanceLocation.Length == 0 ? "(root)" : failure.InstanceLocation)} {failure.Message}";

    private static IReadOnlyList<SchemaFailure> SchemaFailuresOf(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return SchemaValidator.Validate(document.RootElement);
    }

    [DeniedFolderFact]
    public async Task A_folder_that_cannot_be_listed_exits_2_naming_the_path_as_given()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = WriteCommitted(workspace, ValidateFolder, Tier.Local);
        string folder = Path.Combine(workspace.Root, ValidateFolder);

        CliRunResult result;
        using (FolderAccess.DenyListing(folder))
        {
            result = await ValidateAsync(workspace, ValidateFolder);
        }

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        string line = Assert.Single(LinesOf(result.Stderr));
        Assert.Equal($"error: the folder \"{ValidateFolder}\" could not be read (ConfigurationException)", line);
        Assert.DoesNotContain(workspace.Root, line, StringComparison.Ordinal);
        Assert.DoesNotContain("denied", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Validated", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_lists_exactly_the_first_20_failures_in_order_and_21_adds_the_count_of_the_rest()
    {
        string baseText = RunDocumentFixtures.CommittedText(Tier.Local);

        string twenty = WithUnknownStepMembers(baseText, 20);
        IReadOnlyList<SchemaFailure> twentyFailures = SchemaFailuresOf(twenty);
        Assert.Equal(20, twentyFailures.Count);
        Assert.Equal([.. twentyFailures.Select(failure => FormatFailure(LocalFileName, failure))], RunFileValidator.Check(LocalFileName, twenty));

        string twentyOne = WithUnknownStepMembers(baseText, 21);
        IReadOnlyList<SchemaFailure> twentyOneFailures = SchemaFailuresOf(twentyOne);
        Assert.Equal(21, twentyOneFailures.Count);
        IReadOnlyList<string> lines = RunFileValidator.Check(LocalFileName, twentyOne);
        Assert.Equal(21, lines.Count);
        Assert.Equal([.. twentyOneFailures.Take(20).Select(failure => FormatFailure(LocalFileName, failure))], lines.Take(20));
        Assert.Equal($"{LocalFileName}: (root) and 1 more failures", lines[20]);
    }

    [Fact]
    public void A_schema_message_is_cut_to_160_characters()
    {
        string text = RunDocumentFixtures.Edit(
            RunDocumentFixtures.CommittedText(Tier.Local),
            static root => root["run"]!["flags"] = new JsonArray([.. Enumerable.Range(0, 50).Select(static _ => (JsonNode?)JsonValue.Create(RunFlagNames.All[0]))]));
        SchemaFailure raw = Assert.Single(SchemaFailuresOf(text), static failure => failure.InstanceLocation == "/run/flags");
        Assert.True(raw.Message.Length > 160, $"The raw message must be longer than the cut, but is {raw.Message.Length} characters.");

        string line = Assert.Single(RunFileValidator.Check(LocalFileName, text), static candidate => candidate.StartsWith(LocalFileName + ": /run/flags ", StringComparison.Ordinal));

        Assert.Equal(LocalFileName.Length + 2 + "/run/flags".Length + 1 + 163, line.Length);
        Assert.EndsWith("...", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lone_surrogate_escape_is_one_failure_line_and_the_scan_goes_on()
    {
        string escape = string.Concat(@"\", "uD800");
        string baseText = RunDocumentFixtures.CommittedText(Tier.Local);
        string inMemberName = "{\"" + escape + "\":1," + baseText[1..];
        string inEnumValue = baseText.Replace("\"tier\": \"local\"", "\"tier\": \"" + string.Concat(@"\", "udc00") + "\"", StringComparison.Ordinal);
        Assert.NotEqual(baseText, inEnumValue);

        foreach (string text in new[] { inMemberName, inEnumValue })
        {
            IReadOnlyList<string> lines = RunFileValidator.Check(LocalFileName, text);
            Assert.Equal([$"{LocalFileName}: (root) The document holds a string the schema checks could not read (for example a lone surrogate escape)."], lines);
        }

        // In a folder the other files are still validated and the count line is still printed.
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine(ValidateFolder, LocalFileName), inMemberName);
        _ = WriteCommitted(workspace, ValidateFolder, Tier.Nvidia);
        _ = WriteCommitted(workspace, ValidateFolder, Tier.Nebius);

        CliRunResult result = await ValidateAsync(workspace, ValidateFolder);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal([$"{LocalFileName}: (root) The document holds a string the schema checks could not read (for example a lone surrogate escape).", "Validated 3 files"], LinesOf(result.Stdout));
        Assert.Equal(["error: 1 of 3 run files are invalid (ConfigurationException)"], LinesOf(result.Stderr));
    }

    [Fact]
    public void A_failure_of_the_checks_themselves_is_one_could_not_be_checked_line()
    {
        // The guard is exercised directly: the real checks no longer throw for the inputs known to provoke an exception, so a stand-in check throws.
        IReadOnlyList<string> lines = RunFileValidator.Guarded(LocalFileName, static () => throw new InvalidOperationException("secret detail"));

        Assert.Equal([$"{LocalFileName}: (root) could not be checked (InvalidOperationException)"], lines);
        Assert.Throws<OperationCanceledException>(() => RunFileValidator.Guarded(LocalFileName, static () => throw new OperationCanceledException()));
        Assert.Equal(["a"], RunFileValidator.Guarded(LocalFileName, static () => ["a"]));
    }

    [Theory]
    [InlineData("temperature")]
    [InlineData("modelLoadSeconds")]
    [InlineData("powerLimitW")]
    [InlineData("sampleRateHz")]
    public async Task A_number_that_overflows_a_double_is_a_model_failure_and_exit_2(string member)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string text = WithRawNumber(RunDocumentFixtures.CommittedText(Tier.Local), member, "1e400");
        _ = workspace.WriteFile(Path.Combine(ValidateFolder, LocalFileName), text);

        CliRunResult result = await ValidateAsync(workspace, ValidateFolder);

        Assert.Equal(2, result.ExitCode);
        string[] lines = LinesOf(result.Stdout);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith(LocalFileName + ": (model) ", lines[0], StringComparison.Ordinal);
        Assert.Contains(member, lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1e400", lines[0], StringComparison.Ordinal);
        Assert.Equal("Validated 1 files", lines[1]);
        Assert.Equal(["error: 1 of 1 run files are invalid (ConfigurationException)"], LinesOf(result.Stderr));
    }

    [Theory]
    [InlineData("repeat", "0", "/run/repeat")]
    [InlineData("gpuOffloadPercent", "150.123", "/environment/gpuOffloadPercent")]
    [InlineData("gpuOffloadPercent", "-1", "/environment/gpuOffloadPercent")]
    public void A_range_the_model_enforces_is_a_schema_failure_too(string member, string raw, string location)
    {
        string text = WithRawNumber(RunDocumentFixtures.CommittedText(Tier.Local), member, raw);

        IReadOnlyList<string> lines = RunFileValidator.Check(LocalFileName, text);

        Assert.Contains(lines, line => line.StartsWith($"{LocalFileName}: {location} ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, static line => line.Contains("(model)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_model_refuses_a_number_it_cannot_write_back_or_a_range_it_does_not_allow()
    {
        RunDocument document = RunDocumentFixtures.Committed(Tier.Local);

        foreach (double bad in new[] { double.PositiveInfinity, double.NaN })
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => document.Environment with { Temperature = bad });
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => document.Environment with { ModelLoadSeconds = bad });
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => document.Environment with { GpuOffloadPercent = bad });
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => document.Probe! with { PowerLimitW = bad });
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => document.Probe! with { SampleRateHz = bad });
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => document.Environment with { GpuOffloadPercent = 100.5 });
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => document.Environment with { GpuOffloadPercent = -0.5 });
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => document.Run with { Repeat = 0 });
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => document.Run with { Repeat = -3 });

        // The edges and the absent value stay allowed, and the message names the member and never the value.
        Assert.Equal(0.0, (document.Environment with { GpuOffloadPercent = 0.0 }).GpuOffloadPercent);
        Assert.Equal(100.0, (document.Environment with { GpuOffloadPercent = 100.0 }).GpuOffloadPercent);
        Assert.Null((document.Environment with { Temperature = null }).Temperature);
        Assert.Equal(1, (document.Run with { Repeat = 1 }).Repeat);
        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => document.Environment with { GpuOffloadPercent = 150.123 });
        Assert.Contains("gpuOffloadPercent", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("150", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_help_names_the_config_value_path()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        CliRunResult result = await ValidateAsync(workspace, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--config <path>", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--config <config>", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_option_message_ends_with_a_period()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        CliRunResult result = await ValidateAsync(workspace, "--bogus");

        Assert.Equal(["error: Unrecognized command or argument '--bogus'. (ParseError)"], LinesOf(result.Stderr));
    }

    [Fact]
    public void Committed_fixtures_hold_what_production_writes_for_an_ok_step()
    {
        foreach (Tier tier in CommittedTiers)
        {
            RunDocument document = RunDocumentFixtures.Committed(tier);
            foreach (StepReading step in document.Steps)
            {
                Assert.NotNull(step.OutputSha256);
                Assert.NotNull(step.OutputChars);
            }

            for (int index = 0; index < document.Judge.Count; index++)
            {
                StepReading judge = document.Judge[index];
                StepReading step = document.Steps[index];
                DateTimeOffset blockEnd = RunDocumentFixtures.CommittedStartUtc.AddSeconds(8 * ((index / 4) + 1));

                // The judge reading follows its step and ends inside the repeat: the wall time of a repeat is the sum of its steps and holds the judging.
                Assert.True(judge.StartUtc >= step.EndUtc, $"judge[{index}] starts before its step ends.");
                Assert.True(judge.EndUtc <= blockEnd, $"judge[{index}] ends after its repeat's wall time.");
                if (judge.Outcome == StepOutcomes.Ok)
                {
                    Assert.Equal(1, judge.OutputChars);
                    Assert.NotNull(judge.OutputSha256);
                }
            }
        }
    }

    // ---- AJ-080 last items: the guards that had no test ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CheckFileAsync_does_not_turn_a_cancellation_into_a_failure_line()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = WriteCommitted(workspace, ValidateFolder, Tier.Local);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // The token is cancelled before the read starts and CheckFileAsync has no check of its own, so the cancellation reaches the read of the file.
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunFileValidator.CheckFileAsync(path, cancelled.Token));
    }

    [WindowsFact]
    public async Task A_folder_that_is_a_dangling_directory_link_exits_2_naming_the_path_as_given()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string target = Path.Combine(workspace.Root, "target");
        _ = Directory.CreateDirectory(target);
        _ = workspace.WriteFile(Path.Combine("target", RunDocumentFixtures.CommittedFileName(Tier.Local)), RunDocumentFixtures.CommittedText(Tier.Local));

        CliRunResult result;
        using (FolderAccess.CreateDirectoryLink(Path.Combine(workspace.Root, "dangling"), target))
        {
            // The link stays, its target goes: listing the link fails with an IOException, not an UnauthorizedAccessException.
            Directory.Delete(target, recursive: true);

            result = await ValidateAsync(workspace, "dangling");
        }

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        string line = Assert.Single(LinesOf(result.Stderr));
        Assert.Equal("error: the folder \"dangling\" could not be read (ConfigurationException)", line);
        Assert.DoesNotContain(workspace.Root, line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("repeat", "0")]
    [InlineData("repeat", "-7")]
    [InlineData("gpuOffloadPercent", "150.123")]
    public void The_model_refuses_a_range_when_a_document_is_read(string member, string raw)
    {
        string text = WithRawNumber(RunDocumentFixtures.CommittedText(Tier.Local), member, raw);

        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => RunDocumentFixtures.Read(text));

        AssertNamesTheMemberNotTheValue(failure, member == "repeat" ? "run.repeat" : "environment.gpuOffloadPercent", raw);
    }

    [Fact]
    public void The_model_refuses_a_range_when_it_is_constructed()
    {
        RunInfo run = RunDocumentFixtures.Committed(Tier.Local).Run;
        ArgumentOutOfRangeException repeat = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RunInfo(run.Id, run.Tier, run.Tasks, run.Mode, Repeat: -7, run.StartUtc, run.ToolVersion, run.GitCommit, run.Status));
        AssertNamesTheMemberNotTheValue(repeat, "run.repeat", "-7");
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RunInfo(run.Id, run.Tier, run.Tasks, run.Mode, Repeat: 0, run.StartUtc, run.ToolVersion, run.GitCommit, run.Status));

        EnvironmentInfo e = RunDocumentFixtures.Committed(Tier.Local).Environment;
        ArgumentOutOfRangeException percent = Assert.Throws<ArgumentOutOfRangeException>(
            () => new EnvironmentInfo(
                e.GpuName, e.DriverVersion, e.Os, e.DotnetVersion, e.LocalRuntime, e.RuntimeVersion, 150.123, e.ModelLoadSeconds, e.PowerSource, e.ReasoningMode, e.Temperature, e.Provider, e.InstancePreset, e.Region,
                e.GpuUuid, e.ContainerImage, e.ContainerDigest, e.Precision, e.CloudCostUsd, e.EnergyScope, e.Assumptions, e.Models, e.CloudCostObservedUtc));
        AssertNamesTheMemberNotTheValue(percent, "environment.gpuOffloadPercent", "150.123");
    }

    private static void AssertNamesTheMemberNotTheValue(ArgumentOutOfRangeException failure, string member, string raw)
    {
        Assert.Contains(member, failure.Message, StringComparison.Ordinal);

        // A value of 0 would match the digits of a range ("from 0 to 100"), so only the other values are checked for an echo.
        if (raw != "0")
        {
            Assert.DoesNotContain(raw, failure.Message, StringComparison.Ordinal);
        }
    }

    private sealed class Opaque;
}