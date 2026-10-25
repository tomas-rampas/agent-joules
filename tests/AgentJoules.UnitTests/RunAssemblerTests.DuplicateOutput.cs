using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Runs;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// User decision 3 follow-up of the M3 exit (build-log, confirmation run 5; AJ-104): the <c>duplicate-output</c> step flag. <see cref="RunAssembler.Assemble"/> sets it on every workflow step that finished
/// <c>ok</c> with output whose SHA-256 equals that of an earlier step of the same task and repeat (one workflow run), so two agents that recorded the same output are visible in the file and in the report. Identical outputs of
/// other repeats or other tasks are expected and never compared. Judge readings are neither flagged nor compared.
/// </summary>
public sealed partial class RunAssemblerTests
{
    private const string TaskName = "summarise";

    private static readonly DateTimeOffset Start = StepReadingFixtures.StartUtc;

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // A local-tier step at `second` seconds after the fixture start, with the output `text` (its hash and length are recorded; null records none).
    private static StepReading Step(string agent, string? text, int second = 0, string outcome = StepOutcomes.Ok, string? judgedAgent = null, int repeat = 1, string task = TaskName, ReadingFlags flags = ReadingFlags.None) =>
        new(
            task: task,
            repeat: repeat,
            agent: agent,
            modelId: "model-a",
            tier: Tier.Local,
            startUtc: Start.AddSeconds(second),
            endUtc: Start.AddSeconds(second + 1),
            outcome: outcome,
            latencyMs: Metric.Measured(1000, "ms"),
            ttftMs: Metric.Measured(250, "ms"),
            inputTokens: Metric.Measured(120, "tokens"),
            outputTokens: Metric.Measured(80, "tokens"),
            grossJoules: Metric.Measured(180, "J"),
            netJoules: Metric.Measured(120, "J"),
            avgW: Metric.Measured(90, "W"),
            peakW: Metric.Measured(110, "W"),
            cost: Metric.NotReported("not-priced"),
            retries: 0,
            waitMs: 0,
            flags: flags,
            method: EnergyMethod.Sampling,
            sampleCount: 11,
            sampleRateHz: 10.0,
            outputSha256: text is null ? null : Sha(text),
            outputChars: text?.Length,
            outputText: text,
            judgedAgent: judgedAgent);

    private static RunDocument Assemble(IReadOnlyList<StepReading> steps, IReadOnlyList<StepReading>? judge = null)
    {
        RunInfo run = RunAssembler.BuildRunInfo("20261022T091530Z-local", Tier.Local, [TaskName], RunMode.Serialized, 1, Start, RunStatus.Completed);
        EnvironmentInfo environment = RunAssembler.BuildEnvironment(Tier.Local, new TierOptions { ModelId = "model-a", ReasoningMode = "on" }, gpu: null, runtime: null, modelLoad: null);

        return RunAssembler.Assemble(run, environment, probe: null, baseline: null, steps, judge ?? [], wallClockMs: 4000.0);
    }

    private static ReadingFlags FlagsOf(RunDocument document, string agent) => document.Steps.Single(step => step.Agent == agent).Flags;

    [Fact]
    public void The_name_of_the_flag_is_duplicate_output()
    {
        Assert.Equal(["duplicate-output"], ReadingFlagNames.ToStrings(ReadingFlags.DuplicateOutput));
        Assert.True(ReadingFlagNames.TryParse("duplicate-output", out ReadingFlags flag));
        Assert.Equal(ReadingFlags.DuplicateOutput, flag);
    }

    [Fact]
    public void Two_steps_with_equal_hashes_flag_the_second_and_not_the_first()
    {
        RunDocument document = Assemble([Step("Planner", "same text", 0), Step("Researcher", "other text", 2), Step("Writer", "same text", 4)]);

        Assert.Equal(ReadingFlags.None, FlagsOf(document, "Planner"));
        Assert.Equal(ReadingFlags.None, FlagsOf(document, "Researcher"));
        Assert.Equal(ReadingFlags.DuplicateOutput, FlagsOf(document, "Writer"));
    }

    [Fact]
    public void Identical_outputs_of_two_repeats_of_one_task_flag_nothing()
    {
        RunDocument document = Assemble([Step("Planner", "same", 0, repeat: 1), Step("Planner", "same", 10, repeat: 2), Step("Writer", "same", 12, repeat: 2)]);

        Assert.Equal(ReadingFlags.None, document.Steps[0].Flags);
        Assert.Equal(ReadingFlags.None, document.Steps[1].Flags);
        Assert.Equal(ReadingFlags.DuplicateOutput, document.Steps[2].Flags);
    }

    [Fact]
    public void Identical_outputs_of_two_tasks_in_the_same_repeat_flag_nothing()
    {
        RunDocument document = Assemble([Step("Planner", "same", 0, task: "summarise"), Step("Planner", "same", 2, task: "translate")]);

        Assert.All(document.Steps, static step => Assert.Equal(ReadingFlags.None, step.Flags));
    }

    [Fact]
    public void Three_equal_hashes_flag_the_second_and_the_third()
    {
        RunDocument document = Assemble([Step("Planner", "same", 0), Step("Researcher", "same", 2), Step("Writer", "same", 4), Step("Reviewer", "different", 6)]);

        Assert.Equal(ReadingFlags.None, FlagsOf(document, "Planner"));
        Assert.Equal(ReadingFlags.DuplicateOutput, FlagsOf(document, "Researcher"));
        Assert.Equal(ReadingFlags.DuplicateOutput, FlagsOf(document, "Writer"));
        Assert.Equal(ReadingFlags.None, FlagsOf(document, "Reviewer"));
    }

    [Fact]
    public void Equal_hashes_with_a_second_step_that_is_not_ok_flag_nothing()
    {
        RunDocument document = Assemble([Step("Planner", "same", 0), Step("Writer", "same", 2, outcome: StepOutcomes.Error), Step("Reviewer", "same", 4, outcome: StepOutcomes.Incomplete)]);

        Assert.All(document.Steps, static step => Assert.Equal(ReadingFlags.None, step.Flags));
    }

    [Fact]
    public void Steps_without_output_flag_nothing()
    {
        RunDocument document = Assemble([Step("Planner", null, 0), Step("Writer", null, 2), Step("Reviewer", string.Empty, 4), Step("Researcher", string.Empty, 6)]);

        Assert.All(document.Steps, static step => Assert.Equal(ReadingFlags.None, step.Flags));
    }

    [Fact]
    public void A_judge_reading_with_an_equal_hash_is_neither_flagged_nor_an_earlier_step()
    {
        StepReading[] judge = [Step("Judge", "same", 1, judgedAgent: "Planner")];

        RunDocument document = Assemble([Step("Planner", "other", 0), Step("Writer", "same", 2)], judge);

        Assert.All(document.Steps, static step => Assert.Equal(ReadingFlags.None, step.Flags));
        Assert.Equal(ReadingFlags.None, Assert.Single(document.Judge).Flags);
    }

    [Fact]
    public void The_earlier_step_is_the_one_that_started_first_whatever_the_order_of_the_list()
    {
        RunDocument document = Assemble([Step("Writer", "same", 5), Step("Planner", "same", 0)]);

        Assert.Equal(ReadingFlags.None, FlagsOf(document, "Planner"));
        Assert.Equal(ReadingFlags.DuplicateOutput, FlagsOf(document, "Writer"));
    }

    [Fact]
    public void Equal_start_times_fall_back_to_the_order_of_the_list()
    {
        RunDocument document = Assemble([Step("Planner", "same", 3), Step("Writer", "same", 3)]);

        Assert.Equal(ReadingFlags.None, FlagsOf(document, "Planner"));
        Assert.Equal(ReadingFlags.DuplicateOutput, FlagsOf(document, "Writer"));
    }

    [Fact]
    public void Assembling_again_gives_the_same_flags_and_leaves_the_input_unchanged()
    {
        StepReading[] steps = [Step("Planner", "same", 0), Step("Writer", "same", 2)];

        RunDocument first = Assemble(steps);
        RunDocument second = Assemble(first.Steps);

        Assert.Equal(ReadingFlags.None, steps[1].Flags);
        Assert.Equal(first.Steps.Select(static step => step.Flags), second.Steps.Select(static step => step.Flags));
        Assert.Equal(ReadingFlags.DuplicateOutput, FlagsOf(second, "Writer"));
    }

    [Fact]
    public void The_flag_keeps_the_other_flags_of_the_step()
    {
        StepReading shared = Step("Writer", "same", 2, flags: ReadingFlags.MaxTokensExceeded);
        RunDocument document = Assemble([Step("Planner", "same", 0), shared]);

        Assert.Equal(shared.NetJoules.Value, document.Steps[1].NetJoules.Value);
        Assert.Equal(shared.OutputSha256, document.Steps[1].OutputSha256);
        Assert.Equal(ReadingFlags.MaxTokensExceeded | ReadingFlags.DuplicateOutput, document.Steps[1].Flags);
    }

    [Fact]
    public void The_flag_is_written_to_the_file_and_shown_by_the_summary_of_the_report()
    {
        RunDocument document = Assemble([Step("Planner", "same", 0), Step("Writer", "same", 2)]);

        string json = JsonSerializer.Serialize(document, Reporting.Json.AgentJoulesJsonContext.Default.RunDocument);
        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement writer = parsed.RootElement.GetProperty("steps").EnumerateArray().Single(static step => step.GetProperty("agent").GetString() == "Writer");
        Assert.Equal(["duplicate-output"], writer.GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()));

        ReportModel model = ReportAggregator.Build([ReportRunFixtures.Wrap(document)]);
        string summary = SummaryMarkdownRenderer.Render(model);
        Assert.Contains("duplicate-output", summary, StringComparison.Ordinal);
        Assert.Equal(ReadingFlags.DuplicateOutput, model.Rows.Single(static row => row.Agent == "Writer").Flags);
        Assert.Equal(ReadingFlags.None, model.Rows.Single(static row => row.Agent == "Planner").Flags);
    }
}