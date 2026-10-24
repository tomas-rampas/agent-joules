using System.Text;
using System.Text.Json.Nodes;

using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Judge;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class JudgeParsingTests
{
    // ---- user decision 4 of the M3 exit (AJ-104): the judge failure carries the HTTP status and the reason ----

    private const string StatusLine = "Service request failed.";

    [Fact]
    public async Task A_ClientResultException_that_is_not_retried_gives_the_kind_with_the_status_the_reason_and_one_detail_line()
    {
        await using JudgeRig rig = await JudgeRig.CreateAsync(client: (time, _) => Throwing(time, ClientFailures.Status(502, StatusLine + "\nStatus: 502 (Bad Gateway)")));

        JudgeResult result = await rig.ScoreAsync([JudgeStep()]);

        StepReading step = Assert.Single(result.Steps);
        AssertNotScoredWith(step, "judge-failed: ClientResultException 502");
        Assert.Equal(["ClientResultException 502"], result.FailureKinds);
        Assert.Equal(["ClientResultException 502: " + StatusLine], result.FailureDetails);
        Assert.Equal((1, 1), (result.JudgedSteps, result.FailedSteps));
    }

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    [InlineData(429)]
    [InlineData(401)]
    public void The_kind_of_a_ClientResultException_is_its_type_name_and_its_status(int status)
    {
        // A 503 or a 429 is retried before the judge sees it, so the kind is checked here and not through a call.
        Assert.Equal($"ClientResultException {status}", JudgeService.KindOf(ClientFailures.Status(status)));
    }

    [Fact]
    public void The_kind_of_a_provider_error_prefers_its_error_code_to_the_status_of_the_response()
    {
        var withCode = new ProviderErrorException(200, 503, "t", "m", new SecretRedactor());
        var withoutCode = new ProviderErrorException(200, null, "t", "m", new SecretRedactor());
        var withNothing = new ProviderErrorException(null, null, null, "m", new SecretRedactor());

        Assert.Equal("ProviderErrorException 503", JudgeService.KindOf(withCode));
        Assert.Equal("ProviderErrorException 200", JudgeService.KindOf(withoutCode));
        Assert.Equal("ProviderErrorException", JudgeService.KindOf(withNothing));
        Assert.Equal("RetriesExhaustedException", JudgeService.KindOf(new RetriesExhaustedException(5, 503)));
        Assert.Equal("InvalidOperationException", JudgeService.KindOf(new InvalidOperationException("x")));
    }

    [Fact]
    public async Task A_provider_error_chunk_gives_the_kind_with_the_error_code()
    {
        // A 500 without ResourceExhausted is not retried, so the error reaches the judge on the first call (a retried one would wait on a clock this test does not drive).
        var error = new ProviderErrorException(200, 500, "internal_server_error", "worker failed to start the model", new SecretRedactor());
        await using JudgeRig rig = await JudgeRig.CreateAsync(client: (time, _) => Throwing(time, error));

        JudgeResult result = await rig.ScoreAsync([JudgeStep()]);

        AssertNotScoredWith(Assert.Single(result.Steps), "judge-failed: ProviderErrorException 500");
        Assert.Equal(["ProviderErrorException 500"], result.FailureKinds);
        Assert.Equal(["ProviderErrorException 500: worker failed to start the model"], result.FailureDetails);
    }

    [Fact]
    public async Task A_failure_without_a_status_keeps_the_type_name_as_its_kind_and_gets_a_detail_all_the_same()
    {
        await using JudgeRig rig = await JudgeRig.CreateAsync(client: (time, _) => Throwing(time, new InvalidOperationException("first line\nsecond line")));

        JudgeResult result = await rig.ScoreAsync([JudgeStep()]);

        AssertNotScoredWith(Assert.Single(result.Steps), "judge-failed: InvalidOperationException");
        Assert.Equal(["InvalidOperationException"], result.FailureKinds);
        Assert.Equal(["InvalidOperationException: first line"], result.FailureDetails);
    }

    [Fact]
    public async Task The_detail_of_a_kind_is_the_first_line_of_the_message_bounded_to_120_characters_and_cleaned()
    {
        string message = "reason\u001b[31m " + new string('x', 300) + "\nsecond line";
        await using JudgeRig rig = await JudgeRig.CreateAsync(client: (time, _) => Throwing(time, ClientFailures.Status(502, message)));

        JudgeResult result = await rig.ScoreAsync([JudgeStep()]);

        string detail = Assert.Single(result.FailureDetails);
        string reason = detail["ClientResultException 502: ".Length..];
        Assert.True(reason.Length <= 120, $"The reason has {reason.Length} characters.");
        Assert.StartsWith("reason?[31m xxx", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(detail, static character => char.IsControl(character));
        Assert.DoesNotContain("second line", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_steps_with_the_same_kind_give_one_detail_and_two_kinds_give_two_in_the_order_of_the_kinds()
    {
        await using JudgeRig same = await JudgeRig.CreateAsync(client: (time, _) => Throwing(time, ClientFailures.Status(502, StatusLine)));
        JudgeResult one = await same.ScoreAsync([JudgeStep("Planner"), JudgeStep("Writer")]);

        Assert.Equal((2, 2), (one.JudgedSteps, one.FailedSteps));
        Assert.Equal(["ClientResultException 502"], one.FailureKinds);
        Assert.Single(one.FailureDetails);

        await using JudgeRig mixed = await JudgeRig.CreateAsync(client: (time, call) => Throwing(time, call == 0 ? ClientFailures.Status(502, StatusLine) : new IOException("disk gone")));
        JudgeResult two = await mixed.ScoreAsync([JudgeStep("Planner"), JudgeStep("Writer")]);

        Assert.Equal(["ClientResultException 502", "IOException"], two.FailureKinds);
        Assert.Equal(["ClientResultException 502: " + StatusLine, "IOException: disk gone"], two.FailureDetails);
    }

    [Fact]
    public async Task The_details_are_ordered_by_kind_whatever_the_order_of_the_failures()
    {
        // The first failure is the IOException and the second the ClientResultException, but the kinds, and so the details, are in ordinal order.
        await using JudgeRig rig = await JudgeRig.CreateAsync(client: (time, call) => Throwing(time, call == 0 ? new IOException("disk gone") : ClientFailures.Status(502, StatusLine)));

        JudgeResult result = await rig.ScoreAsync([JudgeStep("Planner"), JudgeStep("Writer")]);

        Assert.Equal(["ClientResultException 502", "IOException"], result.FailureKinds);
        Assert.Equal(["ClientResultException 502: " + StatusLine, "IOException: disk gone"], result.FailureDetails);
    }

    [Fact]
    public async Task The_first_reason_of_a_kind_is_the_one_told()
    {
        await using JudgeRig rig = await JudgeRig.CreateAsync(client: (time, call) => Throwing(time, ClientFailures.Status(502, call == 0 ? "first reason" : "second reason")));

        JudgeResult result = await rig.ScoreAsync([JudgeStep("Planner"), JudgeStep("Writer")]);

        Assert.Equal((2, 2), (result.JudgedSteps, result.FailedSteps));
        Assert.Equal(["ClientResultException 502: first reason"], result.FailureDetails);
    }

    [Theory]
    [InlineData("{\"detail\":\"ResourceExhausted: Worker local total request limit reached (16/16)\"}", "ResourceExhausted: Worker local total request limit reached (16/16)")]
    [InlineData("{\"error\":{\"message\":\"overloaded now\",\"type\":\"t\",\"code\":502}}", "overloaded now")]
    [InlineData("{\"error\":{\"message\":\"first line\\nsecond line\"}}", "first line")]
    public async Task The_reason_of_a_rejected_call_is_the_text_its_body_states(string body, string expected)
    {
        await using JudgeRig rig = await JudgeRig.CreateAsync(client: (time, _) => Throwing(time, ClientFailures.Status(502, StatusLine, body)));

        JudgeResult result = await rig.ScoreAsync([JudgeStep()]);

        Assert.Equal(["ClientResultException 502: " + expected], result.FailureDetails);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"error\":\"a bare string\"}")]
    [InlineData("{\"error\":{\"code\":502}}")]
    [InlineData("{\"detail\":[\"not\",\"a string\"]}")]
    [InlineData("{\"detail\":\"   \"}")]
    [InlineData("[1,2]")]
    public async Task A_body_without_a_reason_leaves_the_generic_line_of_the_message(string body)
    {
        await using JudgeRig rig = await JudgeRig.CreateAsync(client: (time, _) => Throwing(time, ClientFailures.Status(502, StatusLine, body)));

        JudgeResult result = await rig.ScoreAsync([JudgeStep()]);

        Assert.Equal(["ClientResultException 502: " + StatusLine], result.FailureDetails);
    }

    [Fact]
    public async Task A_body_larger_than_64_KiB_is_not_read_for_a_reason()
    {
        string body = new JsonObject { ["detail"] = "a reason", ["padding"] = new string('x', 64 * 1024) }.ToJsonString();
        string small = new JsonObject { ["detail"] = "a reason", ["padding"] = new string('x', 1024) }.ToJsonString();
        Assert.True(Encoding.UTF8.GetByteCount(body) > 64 * 1024);
        await using JudgeRig large = await JudgeRig.CreateAsync(client: (time, _) => Throwing(time, ClientFailures.Status(502, StatusLine, body)));
        await using JudgeRig fits = await JudgeRig.CreateAsync(client: (time, _) => Throwing(time, ClientFailures.Status(502, StatusLine, small)));

        JudgeResult tooLarge = await large.ScoreAsync([JudgeStep()]);
        JudgeResult ok = await fits.ScoreAsync([JudgeStep()]);

        Assert.Equal(["ClientResultException 502: " + StatusLine], tooLarge.FailureDetails);
        Assert.Equal(["ClientResultException 502: a reason"], ok.FailureDetails);
    }

    [Fact]
    public async Task The_reason_of_a_body_is_masked_cleaned_and_bounded_like_any_other()
    {
        string key = ProviderClientFixtures.FakeKey();
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(key));
        string body = new JsonObject { ["detail"] = "denied " + key + "\u001b[0m " + new string('x', 300) }.ToJsonString();
        await using JudgeRig rig = await JudgeRig.CreateAsync(client: (time, _) => Throwing(time, ClientFailures.Status(502, StatusLine, body)), redactor: redactor);

        JudgeResult result = await rig.ScoreAsync([JudgeStep()]);

        string detail = Assert.Single(result.FailureDetails);
        string reason = detail["ClientResultException 502: ".Length..];
        Assert.DoesNotContain(key, detail, StringComparison.Ordinal);
        Assert.StartsWith("denied ****?[0m xxx", reason, StringComparison.Ordinal);
        Assert.True(reason.Length <= 120, $"The reason has {reason.Length} characters.");
        Assert.DoesNotContain(detail, static character => char.IsControl(character));
    }

    [Fact]
    public async Task A_kind_without_an_exception_has_no_detail()
    {
        await using JudgeRig rig = await JudgeRig.CreateAsync("excellent work");

        JudgeResult result = await rig.ScoreAsync([JudgeStep()]);

        Assert.Equal(["parse-error"], result.FailureKinds);
        Assert.Empty(result.FailureDetails);
    }

    [Fact]
    public async Task A_registered_key_in_the_message_is_masked_in_the_detail()
    {
        string key = ProviderClientFixtures.FakeKey();
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(key));
        await using JudgeRig rig = await JudgeRig.CreateAsync(client: (time, _) => Throwing(time, ClientFailures.Status(502, "rejected " + key + " now")), redactor: redactor);

        JudgeResult result = await rig.ScoreAsync([JudgeStep()]);

        string detail = Assert.Single(result.FailureDetails);
        Assert.DoesNotContain(key, detail, StringComparison.Ordinal);
        Assert.Equal("ClientResultException 502: rejected **** now", detail);
        Assert.DoesNotContain(key, Assert.Single(result.Steps).Quality.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_retry_that_ran_out_is_told_with_the_status_in_its_detail()
    {
        FakeHttpMessageHandler? endpoint = null;
        ProviderClientHost? host = null;
        try
        {
            await using JudgeRig rig = await JudgeRig.CreateAsync(
                judgeModel: "model-nvidia",
                providers: time =>
                {
                    endpoint = new FakeHttpMessageHandler { Clock = time, DefaultDuration = TimeSpan.FromMilliseconds(10) };
                    endpoint.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ServiceUnavailable());
                    host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers(), handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = endpoint });
                    return host.Factory;
                });

            Task<JudgeResult> run = rig.ScoreAsync([JudgeStep()]);
            await FakeTimeDriver.RunWaitsToCompletionAsync(run, rig.Time);
            JudgeResult result = await run;

            Assert.Equal(["RetriesExhaustedException"], result.FailureKinds);
            Assert.Equal(["RetriesExhaustedException: HTTP 503 after 5 attempts"], result.FailureDetails);
            Assert.Equal(5, endpoint!.Count(FakeRoute.ChatCompletions));
        }
        finally
        {
            host?.Dispose();
        }
    }

    [Fact]
    public void The_warning_line_prints_the_kinds_as_before_and_the_reasons_after_them()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        new ConsoleReporter(output, error).ReportJudgeFailed(
            Tier.Nvidia,
            1,
            4,
            ["ClientResultException 503"],
            ["ClientResultException 503: Service Unavailable", "IOException: disk gone"]);

        string line = Assert.Single(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r');
        Assert.Equal(
            "Warning: the judge failed for 1 of 4 steps of tier nvidia (ClientResultException 503; reasons: ClientResultException 503: Service Unavailable; IOException: disk gone); their quality is N/A and the exit code is unchanged",
            line);
        Assert.Contains("503", line, StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void The_warning_line_without_details_is_the_line_of_before()
    {
        var error = new StringWriter();

        new ConsoleReporter(new StringWriter(), error).ReportJudgeFailed(Tier.Nebius, 3, 12, ["InvalidOperationException"], []);

        Assert.Equal("Warning: the judge failed for 3 of 12 steps of tier nebius (InvalidOperationException); their quality is N/A and the exit code is unchanged", error.ToString().TrimEnd('\r', '\n'));
    }

    [Fact]
    public void A_detail_with_control_characters_is_scrubbed_in_the_warning_line()
    {
        var error = new StringWriter();
        string hostile = "IOException: bad" + (char)0x0A + "line" + (char)0x1B;

        new ConsoleReporter(new StringWriter(), error).ReportJudgeFailed(Tier.Nebius, 1, 1, ["IOException"], [hostile]);

        string line = Assert.Single(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r');
        Assert.False(line.Any(static c => char.IsControl(c) && c != '\r'), "The line holds a control character.");
    }
}