using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Runs;
using AgentJoules.Workflow.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class JudgeParsingTests
{
    // ---- the orchestrator and the command (P-2, P-9, P-10, P-11): the real run command, the production orchestrator and the production judge over fake endpoints ----

    private static string FirstTaskName => TaskSetLoader.Load(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunCommandHarness.TasksFolder))[0].Name;

    private static string NebiusRunLine => $"run --tier nebius --task {FirstTaskName} --repeat 1";

    // A fake endpoint that lists the shipped models and answers every non-streaming chat completion (the judge's) with the reply; the streaming ones (the workflow's) keep the default deltas.
    private static FakeHttpMessageHandler JudgeEndpoint(string reply) => new() { ModelIds = ConfigurationFixtures.ShippedModelIds(), ChatText = reply };

    // The nebius tier is both the workflow tier and the judge tier, so one fake answers the workflow and the judge; later pairs win.
    private static RunCommandSetup JudgedRun(TempWorkspace workspace, HttpMessageHandler nebius, params (string Key, string? Value)[] extra)
    {
        (string Key, string? Value)[] settings = [("AgentJoules:Judge:Tier", "nebius"), ("AgentJoules:Judge:ModelId", NebiusModel), .. extra];
        RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nebius"] = nebius }, settings);
        setup.UseProductionJudge = true;
        return setup;
    }

    private static Task<CliRunResult> RunJudgedAsync(string arguments, RunCommandSetup setup) => RunCommandHarness.RunAsync(arguments, setup).WaitAsync(JudgeHangGuard);

    private static void AssertJudgedExit(int expected, CliRunResult result) =>
        Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stderr: {result.Stderr}");

    private static JsonDocument ReadTheRunFile(TempWorkspace workspace) => RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));

    private static JsonElement[] JudgeOf(JsonDocument document) => [.. document.RootElement.GetProperty("judge").EnumerateArray()];

    private static string TextOf(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private static async Task EndJudgedRunAsync(CliHarnessRun run, CancellationTokenSource cancellation)
    {
        await cancellation.CancelAsync();
        try
        {
            _ = await run.Result.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            // The assertion that failed first is the one the test reports.
        }
    }

    [Fact]
    public async Task Judge_reading_never_in_workflow_sink()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nebius = JudgeEndpoint("4");

        CliRunResult result = await RunJudgedAsync(NebiusRunLine, JudgedRun(workspace, nebius));

        AssertJudgedExit(0, result);
        Assert.Equal(8, nebius.Count(FakeRoute.ChatCompletions));
        using JsonDocument document = ReadTheRunFile(workspace);
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
        JsonElement[] judge = JudgeOf(document);

        // The steps of the file are the snapshot of the workflow sink; no step line is printed for a judge call either.
        Assert.Equal(4, steps.Length);
        Assert.DoesNotContain(steps, static step => TextOf(step, "agent") == "Judge");
        string[] stepLines = [.. RunOrchestrationFixtures.StdoutLines(result).Where(static line => line.StartsWith(FirstTaskName + " r", StringComparison.Ordinal))];
        Assert.Equal(4, stepLines.Length);
        Assert.DoesNotContain(stepLines, static line => line.Contains("Judge", StringComparison.Ordinal));
        Assert.Equal(4, judge.Length);
        Assert.All(judge, static reading => Assert.Equal(("Judge", "nebius", NebiusModel), (TextOf(reading, "agent"), TextOf(reading, "tier"), TextOf(reading, "modelId"))));
        Assert.Equal(steps.Select(static step => TextOf(step, "agent")).Order(StringComparer.Ordinal), judge.Select(static reading => TextOf(reading, "judgedAgent")).Order(StringComparer.Ordinal));
        Assert.All(steps, static step => Assert.Equal("model-judged", TextOf(step.GetProperty("quality"), "label")));
        Assert.All(steps, static step => Assert.False(step.TryGetProperty("judgedAgent", out _)));
    }

    [Fact]
    public async Task Totals_unchanged_by_judge()
    {
        using TempWorkspace plain = RunCommandHarness.CreateWorkspace();
        using TempWorkspace judged = RunCommandHarness.CreateWorkspace();
        RunCommandSetup unjudged = JudgedRun(plain, JudgeEndpoint("4"));
        unjudged.UseProductionJudge = false;

        AssertJudgedExit(0, await RunJudgedAsync(NebiusRunLine, unjudged));
        AssertJudgedExit(0, await RunJudgedAsync(NebiusRunLine, JudgedRun(judged, JudgeEndpoint("4"))));

        using JsonDocument without = ReadTheRunFile(plain);
        using JsonDocument with = ReadTheRunFile(judged);
        JsonElement before = without.RootElement.GetProperty("totals");
        JsonElement after = with.RootElement.GetProperty("totals");
        foreach (string member in new[] { "grossJoules", "netJoules", "inputTokens", "outputTokens", "costByCurrency", "gco2e" })
        {
            Assert.Equal(before.GetProperty(member).GetRawText(), after.GetProperty(member).GetRawText());
        }

        // 4 workflow calls of 7 prompt tokens each, and 4 judge calls of 7 prompt tokens each, in separate sums.
        Assert.Equal(28.0, after.GetProperty("inputTokens").GetProperty("value").GetDouble());
        Assert.Equal(28.0, after.GetProperty("judge").GetProperty("inputTokens").GetProperty("value").GetDouble());
        Assert.Equal("N/A", TextOf(before.GetProperty("judge").GetProperty("inputTokens"), "label"));

        // The cost: 4 calls of 7 prompt and 3 completion tokens at the table price of the model (USD 0.06 and 0.24 per million), in the workflow sum and in the judge sum alike, and the workflow sum is
        // the same with and without the judge.
        double expected = 4 * ((7 * 0.06) + (3 * 0.24)) / 1e6;
        foreach (JsonElement totals in new[] { after.GetProperty("costByCurrency"), after.GetProperty("judge").GetProperty("costByCurrency") })
        {
            JsonElement usd = totals.GetProperty("USD");
            Assert.Equal("estimated", TextOf(usd, "label"));
            Assert.Equal("USD", TextOf(usd, "currency"));
            Assert.Equal(expected, usd.GetProperty("value").GetDouble(), 1e-12);
        }
    }

    [Theory]
    [InlineData("cut")]
    [InlineData("split")]
    public async Task A_judge_that_echoes_the_key_across_the_cut_or_a_think_tag_leaves_a_fixed_reason_and_no_fragment(string shape)
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        string secret = FakeSecret(11, 64);
        string reply = shape == "cut" ? new string(' ', JudgeService.MaxReplyChars - 30) + secret : secret[..32] + "<think>" + secret[32..];

        CliRunResult result = await RunJudgedAsync(NebiusRunLine, JudgedRun(workspace, JudgeEndpoint(reply), (ProviderClientFixtures.NebiusKeyVariable, secret)));

        AssertJudgedExit(0, result);
        string file = File.ReadAllText(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        using JsonDocument document = JsonDocument.Parse(file);
        string excerpt = shape == "cut" ? JudgeService.ReplyTooLongExcerpt : JudgeService.ThinkTagLeftExcerpt;
        Assert.All(RunOrchestrationFixtures.Steps(document), step => Assert.Equal(ReasonPrefix + excerpt, TextOf(step.GetProperty("quality"), "reason")));
        AssertNoFragment(secret, file, result.Stdout, result.Stderr);
    }

    [Fact]
    public async Task Incomplete_write_keeps_judge_readings_published_so_far()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nebius = JudgeEndpoint("4");

        // Chat completions 1 to 4 are the workflow, 5 and 6 are judge calls that finish, 7 is held until the shutdown.
        var held = new HoldingHttpMessageHandler(nebius, FakeRoute.ChatCompletions, number: 7);
        RunCommandSetup setup = JudgedRun(workspace, held);
        using var cancellation = new CancellationTokenSource();

        CliHarnessRun run = CliHarness.Start(NebiusRunLine, setup.ToOverrides(workspace.Root), cancellationToken: cancellation.Token);
        try
        {
            await held.Reached.WaitAsync(JudgeHangGuard);
            await cancellation.CancelAsync();
            CliRunResult result = await run.Result.WaitAsync(JudgeHangGuard);

            AssertJudgedExit(5, result);
            using JsonDocument document = ReadTheRunFile(workspace);
            Assert.Equal("incomplete", TextOf(document.RootElement.GetProperty("run"), "status"));
            JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
            Assert.Equal(4, steps.Length);
            Assert.All(steps, static step => Assert.Equal("ok", TextOf(step, "outcome")));
            JsonElement[] judge = JudgeOf(document);
            Assert.InRange(judge.Length, 2, 3);
            Assert.All(judge, static reading => Assert.Equal("Judge", TextOf(reading, "agent")));
            Assert.Equal(["ok", "ok"], judge.Take(2).Select(static reading => TextOf(reading, "outcome")));
            Assert.True(!document.RootElement.GetProperty("run").TryGetProperty("selfJudged", out JsonElement self) || self.ValueKind == JsonValueKind.Null);
        }
        finally
        {
            await EndJudgedRunAsync(run, cancellation);
        }
    }

    [Fact]
    public async Task A_completed_judged_run_records_self_judged_true_for_the_step_model_and_false_for_another_judge_model()
    {
        using TempWorkspace same = RunCommandHarness.CreateWorkspace();
        using TempWorkspace other = RunCommandHarness.CreateWorkspace();

        AssertJudgedExit(0, await RunJudgedAsync(NebiusRunLine, JudgedRun(same, JudgeEndpoint("4"))));
        AssertJudgedExit(0, await RunJudgedAsync(NebiusRunLine, JudgedRun(other, JudgeEndpoint("4"), ("AgentJoules:Judge:ModelId", OtherListedModel))));

        using JsonDocument selfJudged = ReadTheRunFile(same);
        using JsonDocument notSelfJudged = ReadTheRunFile(other);
        Assert.True(selfJudged.RootElement.GetProperty("run").GetProperty("selfJudged").GetBoolean());
        Assert.False(notSelfJudged.RootElement.GetProperty("run").GetProperty("selfJudged").GetBoolean());
        Assert.All(JudgeOf(notSelfJudged), static reading => Assert.Equal(OtherListedModel, TextOf(reading, "modelId")));
    }

    [Fact]
    public async Task Self_judged_null_when_judge_skipped()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nebius = JudgeEndpoint("4");

        // The judge tier is nvidia (the default) and its key is missing: the judge is skipped with the warning of the command.
        CliRunResult result = await RunJudgedAsync(NebiusRunLine, JudgedRun(workspace, nebius, ("AgentJoules:Judge:Tier", "nvidia"), (ProviderClientFixtures.NvidiaKeyVariable, null)));

        AssertJudgedExit(0, result);
        Assert.Contains(result.StderrLines, static line => line.StartsWith("Warning: judge key NVIDIA_API_KEY is not set", StringComparison.Ordinal));
        Assert.Equal(4, nebius.Count(FakeRoute.ChatCompletions));
        using JsonDocument document = ReadTheRunFile(workspace);
        JsonElement run = document.RootElement.GetProperty("run");
        Assert.True(!run.TryGetProperty("selfJudged", out JsonElement self) || self.ValueKind == JsonValueKind.Null);
        Assert.Empty(JudgeOf(document));

        // The row "judge skipped": the quality is the default of a fresh reading and both derived metrics are missing-input.
        Assert.All(
            RunOrchestrationFixtures.Steps(document),
            static step =>
            {
                Assert.Equal(("N/A", "not-scored"), (TextOf(step.GetProperty("quality"), "label"), TextOf(step.GetProperty("quality"), "reason")));
                Assert.Equal("not-scored", TextOf(step, "qualityReason"));
                Assert.Equal("missing-input", TextOf(step.GetProperty("joulesPerQualityPoint"), "reason"));
                Assert.Equal("missing-input", TextOf(step.GetProperty("costPerQualityPoint"), "reason"));
            });
        Assert.Equal("N/A", TextOf(document.RootElement.GetProperty("totals").GetProperty("judge").GetProperty("inputTokens"), "label"));
    }

    [Fact]
    public async Task A_failed_tier_is_not_judged_and_keeps_the_default_quality_of_a_fresh_reading()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nebius = JudgeEndpoint("4");
        nebius.SetStatuses(FakeRoute.ChatCompletions, 200, 500);

        CliRunResult result = await RunJudgedAsync(NebiusRunLine, JudgedRun(workspace, nebius));

        AssertJudgedExit(5, result);
        using JsonDocument document = ReadTheRunFile(workspace);
        Assert.Equal("failed", TextOf(document.RootElement.GetProperty("run"), "status"));
        Assert.Empty(JudgeOf(document));
        Assert.All(RunOrchestrationFixtures.Steps(document), static step => Assert.Equal(("N/A", "not-scored"), (TextOf(step.GetProperty("quality"), "label"), TextOf(step, "qualityReason"))));
    }

    [Fact]
    public async Task Blank_judge_model_fails_steps_without_a_call()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nebius = JudgeEndpoint("4");

        CliRunResult result = await RunJudgedAsync(NebiusRunLine, JudgedRun(workspace, nebius, ("AgentJoules:Judge:ModelId", null)));

        AssertJudgedExit(0, result);
        Assert.Equal(4, nebius.Count(FakeRoute.ChatCompletions));
        using JsonDocument document = ReadTheRunFile(workspace);
        Assert.Empty(JudgeOf(document));
        Assert.All(
            RunOrchestrationFixtures.Steps(document),
            static step =>
            {
                Assert.Equal("judge-failed: judge-model-not-configured", TextOf(step.GetProperty("quality"), "reason"));
                Assert.Equal("not-scored", TextOf(step, "qualityReason"));
            });
    }

    [Fact]
    public async Task A_judge_that_echoes_the_key_leaves_no_fragment_of_it_in_the_run_file_or_the_console()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        string secret = FakeSecret(9, 64);

        CliRunResult result = await RunJudgedAsync(NebiusRunLine, JudgedRun(workspace, JudgeEndpoint("score " + secret), (ProviderClientFixtures.NebiusKeyVariable, secret)));

        AssertJudgedExit(0, result);
        string file = File.ReadAllText(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        using JsonDocument document = JsonDocument.Parse(file);
        Assert.All(RunOrchestrationFixtures.Steps(document), static step => Assert.StartsWith(ReasonPrefix, TextOf(step.GetProperty("quality"), "reason"), StringComparison.Ordinal));
        AssertNoFragment(secret, file, result.Stdout, result.Stderr);
    }

    [Fact]
    public async Task Missing_rubric_exits_2_naming_paths_rubric()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        File.Delete(Path.Combine(workspace.Root, "config", RubricFileName));
        FakeHttpMessageHandler nebius = JudgeEndpoint("4");

        // A dry run reads the rubric too, before any check and any request.
        CliRunResult dry = await RunJudgedAsync("run --tier nebius --dry-run", JudgedRun(workspace, nebius));
        CliRunResult wet = await RunJudgedAsync(NebiusRunLine, JudgedRun(workspace, nebius));

        AssertJudgedExit(2, dry);
        AssertJudgedExit(2, wet);
        Assert.Equal("error: judge-rubric.md does not exist (Paths:Rubric) (ConfigurationException)", Assert.Single(dry.ErrorLines));
        Assert.Equal(dry.ErrorLines, wet.ErrorLines);
        Assert.DoesNotContain(workspace.Root, dry.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, nebius.RequestCount);
        Assert.Empty(RunOrchestrationFixtures.EntriesIn(workspace.Root));
    }

    [Fact]
    public async Task An_invalid_rubric_exits_2_with_the_key_in_front_of_the_message()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        _ = workspace.WriteFile(Path.Combine("config", RubricFileName), "Score one step.\n1. a\n2. b\n3. c\n4. d\n");

        CliRunResult result = await RunJudgedAsync("run --tier nebius --dry-run", JudgedRun(workspace, JudgeEndpoint("4")));

        AssertJudgedExit(2, result);
        Assert.Equal("error: Paths:Rubric: judge-rubric.md: level 5 is missing (ConfigurationException)", Assert.Single(result.ErrorLines));
    }

    [Fact]
    public async Task The_harness_registers_the_no_op_judge_unless_the_production_judge_is_asked_for()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        File.Delete(Path.Combine(workspace.Root, "config", RubricFileName));
        RunCommandSetup setup = JudgedRun(workspace, JudgeEndpoint("4"));
        setup.UseProductionJudge = false;

        CliRunResult result = await RunJudgedAsync("run --tier nebius --dry-run", setup);

        AssertJudgedExit(0, result);
    }

    // ---- AJ-077 (EDGE-012): a judge failure never changes the exit code ----

    /// <summary>
    /// The tests of EDGE-012. They live in a nested class with a trait of their own, because the class-level <c>Spec</c> trait of the outer class (REQ-010) would tag them too and
    /// <c>--filter "Spec=EDGE-012"</c> must list exactly these. The workflow tier is nvidia, on an endpoint that answers well; the judge is on the nebius tier, on an endpoint of its own that
    /// fails the way the test says, so only the judge fails. Every wait of the retry layer runs on a fake clock that the test advances.
    /// </summary>
    [Trait("Spec", "EDGE-012")]
    public sealed class JudgeFailureNeverChangesTheExitCode
    {
        private const string WorkflowLine = "run --tier nvidia --repeat 1";
        private const string JudgeWarningStart = "Warning: the judge failed for ";

        // The type name of the failure of a judge call that the endpoint answers with a 500: the client library's own result exception, which carries the response and so its text; only its name is kept.
        // The kind of a judge call that a 500 rejected: the type name and the status (user decision 4 of the M3 exit).
        private const string FailedCallTypeName = "ClientResultException 500";

        // The reason the warning gives for that kind: the first line of the SDK message of a rejected call. The SDK writes the status, the type and the code of a standard error body there
        // (the default body of the fake endpoint, whose error.message is the reason), and a generic line for a body it does not understand; the body text itself is never part of the first line.
        private const string FailedCallReason = "ClientResultException 500: Service request failed.";
        private const string StandardBodyReason = "ClientResultException 500: fake error";

        private static readonly TimeSpan CallTime = TimeSpan.FromMilliseconds(10);

        private static readonly string[] QualityMembers = ["quality", "qualityReason", "joulesPerQualityPoint", "costPerQualityPoint"];

        // The judge endpoint: the models route lists the shipped models (so the preflight passes), the completion route answers the reply, or the status when one is given.
        private static FakeHttpMessageHandler Judge(TimerTrackingTimeProvider time, string reply = "4", int? status = null)
        {
            var endpoint = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), ChatText = reply, Clock = time, DefaultDuration = CallTime };
            if (status is { } code)
            {
                endpoint.SetStatuses(FakeRoute.ChatCompletions, code);
            }

            return endpoint;
        }

        private static RunCommandSetup SetupWith(TempWorkspace workspace, TimerTrackingTimeProvider time, HttpMessageHandler judge, params (string Key, string? Value)[] extra)
        {
            (string Key, string? Value)[] settings = [("AgentJoules:Judge:Tier", "nebius"), ("AgentJoules:Judge:ModelId", NebiusModel), .. extra];
            var endpoints = new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
            {
                ["nvidia"] = RunOrchestrationFixtures.Endpoint(time, CallTime),
                ["nebius"] = judge,
            };
            RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, endpoints, settings);
            setup.UseProductionJudge = true;
            RunOrchestrationFixtures.UseClock(setup, time);
            return setup;
        }

        private static Task<CliRunResult> RunAsync(string arguments, RunCommandSetup setup, TimerTrackingTimeProvider time) =>
            RunOrchestrationFixtures.RunDrivenAsync(arguments, setup, time).WaitAsync(JudgeHangGuard);

        private static string[] JudgeWarnings(CliRunResult result) => [.. result.StderrLines.Where(static line => line.StartsWith(JudgeWarningStart, StringComparison.Ordinal))];

        private static string StatusOf(JsonDocument document) => TextOf(document.RootElement.GetProperty("run"), "status");

        private static void AssertNotScored(JsonElement step, string qualityReason)
        {
            JsonElement quality = step.GetProperty("quality");
            Assert.Equal("N/A", TextOf(quality, "label"));
            Assert.Equal(qualityReason, TextOf(quality, "reason"));
            Assert.Equal(JsonValueKind.Null, quality.GetProperty("value").ValueKind);
            Assert.Equal("not-scored", TextOf(step, "qualityReason"));
            foreach (string ratio in new[] { "joulesPerQualityPoint", "costPerQualityPoint" })
            {
                JsonElement derived = step.GetProperty(ratio);
                Assert.Equal(("N/A", "missing-input"), (TextOf(derived, "label"), TextOf(derived, "reason")));
                Assert.Equal(JsonValueKind.Null, derived.GetProperty("value").ValueKind);
            }
        }

        [Fact]
        public async Task Failing_judge_keeps_exit_0()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
            FakeHttpMessageHandler judge = Judge(time, status: 500);
            RunCommandSetup setup = SetupWith(workspace, time, judge);

            CliRunResult result = await RunAsync(WorkflowLine, setup, time);

            AssertJudgedExit(0, result);
            using JsonDocument document = ReadTheRunFile(workspace);
            Assert.Equal("completed", StatusOf(document));
            JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
            Assert.NotEmpty(steps);

            // The workflow made one call per step (the endpoint of the tier counts them), the judge was asked once per step (a 500 is not retried), and every step is N/A with the exception type,
            // the reason not-scored and both derived metrics missing-input; the judge readings are in the judge array.
            Assert.Equal(steps.Length, ((FakeHttpMessageHandler)setup.Endpoints["nvidia"]).Count(FakeRoute.ChatCompletions));
            Assert.Equal(steps.Length, judge.Count(FakeRoute.ChatCompletions));
            Assert.All(steps, static step => AssertNotScored(step, "judge-failed: " + FailedCallTypeName));
            Assert.Equal(steps.Length, JudgeOf(document).Length);
        }

        [Fact]
        public async Task One_judge_warning_no_key()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
            string secret = FakeSecret(5, 40);

            // The endpoint lists the models, then refuses every completion call with a failure text that holds the bearer token it saw, as a careless server would.
            var judge = new CredentialEchoingRefusalHandler(ConfigurationFixtures.ShippedModelIds());

            CliRunResult result = await RunAsync(WorkflowLine, SetupWith(workspace, time, judge, (ProviderClientFixtures.NebiusKeyVariable, secret)), time);

            AssertJudgedExit(0, result);
            using JsonDocument document = ReadTheRunFile(workspace);
            int steps = RunOrchestrationFixtures.Steps(document).Length;
            Assert.True(steps > 1, "The run has several steps, so a warning per step would be several lines.");

            // The key was sent, so the failure text had it to leak; one warning line for the tier, not one per step, and nothing of the key anywhere.
            Assert.Equal(secret, judge.LastBearerToken);
            Assert.Equal(steps, judge.Attempts);
            string[] warnings = [.. result.StderrLines.Where(static line => line.StartsWith("Warning:", StringComparison.Ordinal))];
            string warning = Assert.Single(warnings);
            Assert.StartsWith(JudgeWarningStart + $"{steps} of {steps} steps of tier nvidia (", warning, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, warning, StringComparison.Ordinal);
            AssertNoFragment(secret, result.Stdout, result.Stderr, File.ReadAllText(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root))));
        }

        [Fact]
        public async Task Partial_judge_failure_keeps_scores()
        {
            await using JudgeRig rig = await JudgeRig.CreateAsync(client: (time, index) => index == 2 ? Throwing(time, new InvalidOperationException("the judge is down")) : Replying(time, "4"));
            StepReading[] steps = [.. Enumerable.Range(1, 12).Select(static number => JudgeStep(WorkflowAgents[(number - 1) % WorkflowAgents.Length], repeat: number))];

            JudgeResult result = await rig.ScoreAsync(steps);

            Assert.Equal((12, 1), (result.JudgedSteps, result.FailedSteps));
            Assert.Equal(["InvalidOperationException"], result.FailureKinds);
            for (int i = 0; i < steps.Length; i++)
            {
                StepReading derived = RunAssembler.WithDerivedMetrics(result.Steps[i]);
                if (i == 2)
                {
                    AssertNotScoredWith(result.Steps[i], "judge-failed: InvalidOperationException");
                    AssertNotApplicable(derived.JoulesPerQualityPoint, "missing-input");
                    AssertNotApplicable(derived.CostPerQualityPoint, "missing-input");
                    continue;
                }

                // The 11 that succeeded keep the score, its label and the ratio derived from it (120 J net over 4 points).
                AssertJudged(result.Steps[i].Quality, 4.0);
                Assert.Null(result.Steps[i].QualityReason);
                AssertJudged(derived.JoulesPerQualityPoint, 30.0);
            }

            Assert.Equal(12, rig.Sink.Snapshot().Count);
        }

        [Fact]
        public async Task Judge_429_exhaustion_keeps_exit_0()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
            FakeHttpMessageHandler judge = Judge(time, status: 429);

            // The retry layer waits on the fake clock, which the driver advances: the five attempts of every judge call take no wall-clock time.
            CliRunResult result = await RunAsync(WorkflowLine, SetupWith(workspace, time, judge), time);

            AssertJudgedExit(0, result);
            using JsonDocument document = ReadTheRunFile(workspace);
            Assert.Equal("completed", StatusOf(document));
            JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
            Assert.NotEmpty(steps);
            Assert.Equal(5 * steps.Length, judge.Count(FakeRoute.ChatCompletions));
            Assert.All(steps, static step => AssertNotScored(step, "judge-failed: " + nameof(RetriesExhaustedException)));
            string warning = Assert.Single(JudgeWarnings(result));
            Assert.Contains($"({nameof(RetriesExhaustedException)}; reasons: {nameof(RetriesExhaustedException)}: HTTP 429 after 5 attempts)", warning, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Unparseable_reply_records_parse_error()
        {
            await using JudgeRig rig = await JudgeRig.CreateAsync("excellent work");

            JudgeResult result = await rig.ScoreAsync([JudgeStep()]);

            StepReading step = Assert.Single(result.Steps);
            AssertNotScoredWith(step, ReasonPrefix + "excellent work");
            Assert.Equal(["parse-error"], result.FailureKinds);
            Assert.Equal((1, 1), (result.JudgedSteps, result.FailedSteps));
            StepReading derived = RunAssembler.WithDerivedMetrics(step);
            AssertNotApplicable(derived.JoulesPerQualityPoint, "missing-input");
            AssertNotApplicable(derived.CostPerQualityPoint, "missing-input");
        }

        [Fact]
        public async Task Unparseable_reply_in_a_run_keeps_exit_0_and_warns_with_the_kind()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();

            CliRunResult result = await RunAsync(WorkflowLine, SetupWith(workspace, time, Judge(time, "excellent work")), time);

            AssertJudgedExit(0, result);
            using JsonDocument document = ReadTheRunFile(workspace);
            Assert.Equal("completed", StatusOf(document));
            JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
            Assert.All(steps, static step => AssertNotScored(step, ReasonPrefix + "excellent work"));
            string warning = Assert.Single(JudgeWarnings(result));
            Assert.Contains("(parse-error)", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("excellent", warning, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_reply_with_a_lone_surrogate_is_judge_failed_with_the_exception_type_of_the_json_layer()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();

            // The endpoint answers a buffered reply whose JSON text holds the escape of a lone high surrogate (a backslash, then u and d800), as a server that does not check its text does. The JSON layer
            // of the client refuses to read it as a string, which is a judge failure and not an escape. The escape is built here, so no escape sequence is typed in the source.
            const string marker = "LONE-SURROGATE-MARKER";
            string escape = new string((char)0x5C, 1) + "ud800";
            FakeHttpMessageHandler judge = Judge(time, "4" + marker);
            judge.CustomizeResponse = (route, response) =>
            {
                if (route == FakeRoute.ChatCompletions && response.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    response.Content = new StringContent(body.Replace(marker, escape, StringComparison.Ordinal), System.Text.Encoding.UTF8, "application/json");
                }
            };

            CliRunResult result = await RunAsync(WorkflowLine, SetupWith(workspace, time, judge), time);

            AssertJudgedExit(0, result);
            using JsonDocument document = ReadTheRunFile(workspace);
            Assert.Equal("completed", StatusOf(document));
            Assert.All(RunOrchestrationFixtures.Steps(document), static step => AssertNotScored(step, "judge-failed: InvalidOperationException"));
        }

        [Fact]
        public async Task Judge_failure_changes_only_quality_and_derived()
        {
            using TempWorkspace succeeded = RunCommandHarness.CreateWorkspace();
            using TempWorkspace failed = RunCommandHarness.CreateWorkspace();
            TimerTrackingTimeProvider goodTime = RunOrchestrationFixtures.Clock();
            TimerTrackingTimeProvider badTime = RunOrchestrationFixtures.Clock();

            CliRunResult good = await RunAsync(WorkflowLine, SetupWith(succeeded, goodTime, Judge(goodTime, "4")), goodTime);
            CliRunResult bad = await RunAsync(WorkflowLine, SetupWith(failed, badTime, Judge(badTime, status: 500)), badTime);

            AssertJudgedExit(0, good);
            AssertJudgedExit(0, bad);
            Assert.Empty(JudgeWarnings(good));
            _ = Assert.Single(JudgeWarnings(bad));
            using JsonDocument withScores = ReadTheRunFile(succeeded);
            using JsonDocument withoutScores = ReadTheRunFile(failed);

            // The status is the same, and so are the steps and the totals apart from the four quality members of a step and the totals of the judge itself.
            Assert.Equal(StatusOf(withScores), StatusOf(withoutScores));
            Assert.Equal("completed", StatusOf(withoutScores));
            Assert.Equal(WithoutQuality(withScores), WithoutQuality(withoutScores));
            Assert.Equal(TotalsWithoutJudge(withScores), TotalsWithoutJudge(withoutScores));

            // The control: the four members did change, so the comparison above is not vacuous.
            JsonElement[] before = RunOrchestrationFixtures.Steps(withScores);
            JsonElement[] after = RunOrchestrationFixtures.Steps(withoutScores);
            Assert.All(before, static step => Assert.Equal("model-judged", TextOf(step.GetProperty("quality"), "label")));
            Assert.All(after, static step => Assert.Equal("N/A", TextOf(step.GetProperty("quality"), "label")));
        }

        [Fact]
        public async Task Two_failing_tiers_print_two_judge_lines()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
            var gpu = new ScriptedNvmlApi(time) { ConstantPowerWatts = 30.0 };

            // The judge is on the measured tier local: it is metered by the meter of the local run and refused on the two hosted runs, which would otherwise be measured with a GPU that is not theirs.
            // So the local tier is judged, and the nvidia and the nebius tier each fail for every judged step.
            var endpoints = new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
            {
                ["local"] = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), ChatText = "4", Clock = time, DefaultDuration = TimeSpan.FromSeconds(1) },
                ["nvidia"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)),
                ["nebius"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)),
            };
            RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, endpoints, ("AgentJoules:Judge:Tier", "local"), ("AgentJoules:Judge:ModelId", ConfigurationFixtures.ShippedModelId("local")));
            setup.Gpu = gpu;
            setup.UseProductionJudge = true;
            RunOrchestrationFixtures.UseClock(setup, time);

            CliRunResult result = await RunAsync("run --tier all --repeat 1", setup, time);

            AssertJudgedExit(0, result);
            string[] warnings = JudgeWarnings(result);
            Assert.Equal(2, warnings.Length);
            Assert.Contains("steps of tier nvidia (judge-tier-not-metered);", warnings[0], StringComparison.Ordinal);
            Assert.Contains("steps of tier nebius (judge-tier-not-metered);", warnings[1], StringComparison.Ordinal);
            foreach (string tierName in new[] { "local", "nvidia", "nebius" })
            {
                using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root), path => Path.GetFileName(path).EndsWith("-" + tierName + ".json", StringComparison.Ordinal)));
                Assert.Equal("completed", StatusOf(document));
                string expected = tierName == "local" ? "model-judged" : "N/A";
                Assert.All(RunOrchestrationFixtures.Steps(document), step => Assert.Equal(expected, TextOf(step.GetProperty("quality"), "label")));
            }
        }

        [Fact]
        public async Task Judge_warning_names_kinds_not_messages()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();

            // The first call is rejected with a body that holds a sentinel, then every call gets a reply that is not a score: two kinds, one of them an exception type, in ordinal order.
            FakeHttpMessageHandler judge = Judge(time, "excellent work");
            judge.SetAnswers(FakeRoute.ChatCompletions, new FakeAnswer(500), FakeAnswer.Ok());
            judge.CustomizeResponse = (route, response) =>
            {
                if (route == FakeRoute.ChatCompletions && response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                {
                    response.Content = new StringContent("{\"error\":\"" + MessageSentinel + "\"}", System.Text.Encoding.UTF8, "application/json");
                }
            };

            CliRunResult result = await RunAsync(WorkflowLine, SetupWith(workspace, time, judge), time);

            AssertJudgedExit(0, result);
            using JsonDocument document = ReadTheRunFile(workspace);
            int steps = RunOrchestrationFixtures.Steps(document).Length;
            string warning = Assert.Single(JudgeWarnings(result));
            string[] reasons = [.. RunOrchestrationFixtures.Steps(document).Select(static step => TextOf(step.GetProperty("quality"), "reason"))];
            string firstKind = Assert.Single(reasons, static reason => reason.StartsWith("judge-failed: ", StringComparison.Ordinal))["judge-failed: ".Length..];
            Assert.Equal(JudgeWarningStart + $"{steps} of {steps} steps of tier nvidia ({firstKind}, parse-error; reasons: {FailedCallReason}); their quality is N/A and the exit code is unchanged", warning);
            Assert.DoesNotContain(MessageSentinel, result.Stdout + result.Stderr + File.ReadAllText(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root))), StringComparison.Ordinal);
            Assert.DoesNotContain("excellent", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("http", warning, StringComparison.OrdinalIgnoreCase);
        }

        private const string MessageSentinel = "detail-that-must-not-be-echoed-4f1c";

        [Fact]
        public async Task A_listener_that_fails_on_the_judge_warning_changes_neither_the_file_nor_the_exit_code()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
            string detail = "console-detail-" + Guid.NewGuid().ToString("N")[..12];
            RunCommandSetup setup = SetupWith(workspace, time, Judge(time, status: 500));

            // The console listener of the command is wrapped by one that throws, with a sentinel text, when it is told that the judge failed; the orchestrator must isolate that failure, like any other.
            RunListenerFixtures.Decorate(setup, inner => new JudgeFailedThrows(inner, detail));

            CliRunResult result = await RunAsync(WorkflowLine, setup, time);

            AssertJudgedExit(0, result);
            using JsonDocument document = ReadTheRunFile(workspace);
            Assert.Equal("completed", StatusOf(document));
            Assert.All(RunOrchestrationFixtures.Steps(document), static step => AssertNotScored(step, "judge-failed: " + FailedCallTypeName));

            // The tier was still told as completed (its closing lines are on the output), the failure of the listener reached neither stream, and the warning it would have printed is not there.
            string[] lines = RunOrchestrationFixtures.StdoutLines(result);
            Assert.Contains(lines, static line => line.StartsWith("results: ", StringComparison.Ordinal));
            Assert.Contains(lines, static line => line.StartsWith("totals: ", StringComparison.Ordinal));
            Assert.DoesNotContain(detail, result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(detail, result.Stderr, StringComparison.Ordinal);
            Assert.Empty(JudgeWarnings(result));
        }

        [Fact]
        public async Task The_judge_warning_counts_the_failed_and_the_judged_steps_and_is_told_after_the_file_is_written()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();

            // The first judge call is rejected and every other one scores 4, so exactly one of the judged steps fails.
            FakeHttpMessageHandler judge = Judge(time, "4");
            judge.SetAnswers(FakeRoute.ChatCompletions, new FakeAnswer(500), FakeAnswer.Ok());
            RunCommandSetup setup = SetupWith(workspace, time, judge);
            OrderRecordingListener? recorder = null;
            RunListenerFixtures.Decorate(setup, inner => recorder = new OrderRecordingListener(inner, () => StatusOfTheRunFile(workspace)));

            CliRunResult result = await RunAsync(WorkflowLine, setup, time);

            AssertJudgedExit(0, result);
            using JsonDocument document = ReadTheRunFile(workspace);
            JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
            Assert.Equal(1, steps.Count(static step => TextOf(step.GetProperty("quality"), "label") == "N/A"));
            Assert.Equal(steps.Length - 1, steps.Count(static step => TextOf(step.GetProperty("quality"), "label") == "model-judged"));
            Assert.Equal(
                JudgeWarningStart + $"1 of {steps.Length} steps of tier nvidia ({FailedCallTypeName}; reasons: {StandardBodyReason}); their quality is N/A and the exit code is unchanged",
                Assert.Single(JudgeWarnings(result)));

            // The warning is told once, after the final document was written (the file said completed at that moment) and just before the closing lines of the tier.
            OrderRecordingListener told = recorder ?? throw new Xunit.Sdk.XunitException("The decorator was not built.");
            Assert.Equal("completed", told.StatusAtJudgeFailed);
            Assert.Equal(1, told.Calls.Count(static call => call == nameof(IRunListener.JudgeFailed)));
            Assert.Equal([nameof(IRunListener.JudgeFailed), nameof(IRunListener.TierCompleted)], told.Calls.TakeLast(2));
        }

        // The status of the only run file of the workspace as it is on disk right now, or null while there is no file; a file that cannot be read says so with its exception type.
        private static string? StatusOfTheRunFile(TempWorkspace workspace)
        {
            string[] files = RunOrchestrationFixtures.RunFilesIn(workspace.Root);
            if (files.Length != 1)
            {
                return null;
            }

            try
            {
                using JsonDocument document = RunOrchestrationFixtures.Read(files[0]);
                return StatusOf(document);
            }
            catch (Exception failure) when (failure is IOException or JsonException)
            {
                return "unreadable: " + failure.GetType().Name;
            }
        }

        [Fact]
        public void The_judge_warning_line_has_one_shape_and_scrubs_the_kinds()
        {
            var output = new StringWriter();
            var error = new StringWriter();
            string hostile = "Bad" + (char)0x0A + "Kind" + (char)0x1B;

            new ConsoleReporter(output, error).ReportJudgeFailed(Tier.Nebius, 3, 12, ["InvalidOperationException", hostile]);

            string line = Assert.Single(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r');
            Assert.StartsWith("Warning: the judge failed for 3 of 12 steps of tier nebius (InvalidOperationException, ", line, StringComparison.Ordinal);
            Assert.EndsWith("); their quality is N/A and the exit code is unchanged", line, StringComparison.Ordinal);
            Assert.False(line.Any(static c => char.IsControl(c) && c != '\r'), "The line holds a control character.");
            Assert.Empty(output.ToString());
            _ = Assert.Throws<ArgumentNullException>(() => new ConsoleReporter(output, error).ReportJudgeFailed(Tier.Nebius, 1, 1, null!));
        }

        [Fact]
        public async Task Missing_judge_key_warns_once_and_exits_0()
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
            FakeHttpMessageHandler nebius = RunOrchestrationFixtures.Endpoint(time, CallTime);

            // The workflow tier nebius has its key and its endpoint; the judge tier is the default, nvidia, whose key is absent and whose endpoint does not exist (a request to it is denied and recorded).
            RunCommandSetup setup = RunOrchestrationFixtures.Production(
                workspace.Root,
                new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nebius"] = nebius },
                (ProviderClientFixtures.NvidiaKeyVariable, null));
            setup.UseProductionJudge = true;
            RunOrchestrationFixtures.UseClock(setup, time);

            CliRunResult result = await RunAsync("run --tier nebius --repeat 1", setup, time);

            AssertJudgedExit(0, result);
            string[] warnings = [.. result.StderrLines.Where(static line => line.StartsWith("Warning:", StringComparison.Ordinal))];
            string warning = Assert.Single(warnings);
            Assert.StartsWith("Warning: judge key NVIDIA_API_KEY is not set", warning, StringComparison.Ordinal);
            Assert.Empty(JudgeWarnings(result));
            using JsonDocument document = ReadTheRunFile(workspace);
            Assert.Equal("completed", StatusOf(document));
            JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
            Assert.NotEmpty(steps);
            Assert.All(steps, static step => Assert.Equal("N/A", TextOf(step.GetProperty("quality"), "label")));

            // No judge request went anywhere: every completion call the nebius endpoint saw is a workflow step, nothing was denied, and the file holds no judge reading and no self-judged statement.
            Assert.Equal(steps.Length, nebius.Count(FakeRoute.ChatCompletions));
            Assert.Empty(result.DeniedRequests);
            Assert.Empty(JudgeOf(document));
            Assert.Equal("N/A", TextOf(document.RootElement.GetProperty("totals").GetProperty("judge").GetProperty("inputTokens"), "label"));
        }

        // The steps of a run file keyed by task, repeat and agent, without the four members that the judge writes.
        private static string[] WithoutQuality(JsonDocument document) =>
            [.. RunOrchestrationFixtures.Steps(document)
                .Select(static step =>
                {
                    var copy = (JsonObject)JsonNode.Parse(step.GetRawText())!;
                    foreach (string member in QualityMembers)
                    {
                        _ = copy.Remove(member);
                    }

                    return $"{TextOf(step, "task")}/{step.GetProperty("repeat").GetInt32()}/{TextOf(step, "agent")} {copy.ToJsonString()}";
                })
                .Order(StringComparer.Ordinal)];

        // The totals of the workflow: the members of the totals except the totals of the judge, which sum the judge readings and so differ when the judge fails.
        private static string TotalsWithoutJudge(JsonDocument document)
        {
            var totals = (JsonObject)JsonNode.Parse(document.RootElement.GetProperty("totals").GetRawText())!;
            _ = totals.Remove("judge");
            return totals.ToJsonString();
        }
    }
}