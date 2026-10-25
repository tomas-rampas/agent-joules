using System.ClientModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.AI;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// User decision 3 of the M3 exit (AJ-104, EDGE-006): an <c>error</c> chunk inside an HTTP 200 event stream ends the step as a provider error, never as <c>ok</c> with no output, and HTTP 503 and
/// HTTP 500 with a <c>ResourceExhausted</c> message enter the retry policy like a 429. The detection sits in the transport (<see cref="StreamErrorDetectingHandler"/>), so these tests run the real SDK, the
/// real adapter, the real named pipeline and the real retry layer against the fake endpoint, and the handler itself against hand-made streams cut into pieces of a few bytes.
/// </summary>
public sealed partial class RetryPolicyTests
{
    private const string ChatPath = "/v1/chat/completions";

    // The body a saturated worker returns for a 500 on the buffered path.
    private static FakeAnswer ResourceExhausted500() =>
        new(500, ErrorBody: new System.Text.Json.Nodes.JsonObject { ["detail"] = FakeAnswer.ResourceExhaustedMessage }.ToJsonString());

    // ----- the error chunk through the whole pipeline

    [Fact]
    public async Task An_error_chunk_once_then_a_normal_stream_is_one_retry_and_the_step_is_ok()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ErrorChunk(), FakeAnswer.Ok()));

        string text = await rig.StreamAsync();

        StepReading reading = rig.Reading;
        Assert.Equal("answer", text);
        Assert.Equal(2, rig.Requests);
        Assert.Equal(1, reading.Retries);
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
        Assert.Equal(6, reading.OutputChars);
        Assert.Equal(SumMs(rig.Time.History), reading.WaitMs);
    }

    [Fact]
    public async Task An_error_chunk_every_time_exhausts_the_attempts_with_the_status_of_the_error_and_the_step_is_an_error()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ErrorChunk()));

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.StreamAsync());

        StepReading reading = rig.Reading;
        Assert.Equal(RetryOptions.Default.MaxAttempts, failure.Attempts);
        Assert.Equal(500, failure.StatusCode);

        // The code of an error in a 200 stream is no HTTP status, and the last error travels with the exhaustion.
        Assert.Equal("error code 500 in the event stream after 5 attempts", failure.Message);
        Assert.DoesNotContain("HTTP", failure.Message, StringComparison.Ordinal);
        Assert.Equal(FakeAnswer.ResourceExhaustedMessage, Assert.IsType<ProviderErrorException>(failure.LastProviderError).Message);
        Assert.Equal(RetryOptions.Default.MaxAttempts, rig.Requests);
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal(RetryOptions.Default.MaxAttempts - 1, reading.Retries);
        Assert.Null(reading.OutputChars);
        Assert.Equal(ExitCode.Unreachable, failure.ExitCode);
    }

    [Fact]
    public async Task An_error_chunk_with_code_503_is_retried()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ErrorChunk("worker overloaded", 503), FakeAnswer.Ok()));

        string text = await rig.StreamAsync();

        Assert.Equal("answer", text);
        Assert.Equal(1, rig.Reading.Retries);
        Assert.Equal(2, rig.Requests);
    }

    [Theory]
    [InlineData(500, "worker failed")]
    [InlineData(400, "bad request")]
    public async Task An_error_chunk_that_is_not_503_or_a_500_with_ResourceExhausted_is_not_retried(int code, string message)
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ErrorChunk(message, code), FakeAnswer.Ok()));

        ProviderErrorException failure = await Assert.ThrowsAsync<ProviderErrorException>(() => rig.StreamAsync());

        StepReading reading = rig.Reading;
        Assert.Equal(code, failure.ErrorCode);
        Assert.Equal(200, failure.HttpStatus);
        Assert.Equal("internal_server_error", failure.ErrorType);
        Assert.Equal(message, failure.Message);
        Assert.Equal($"provider-error: internal_server_error {code} {message}", failure.StepText);
        Assert.Equal(1, rig.Requests);
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal(0, reading.Retries);
    }

    // ----- 503 and 500 ResourceExhausted on the buffered path

    [Fact]
    public async Task A_buffered_503_is_retried()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ServiceUnavailable(), FakeAnswer.Ok()));

        ChatResponse response = await rig.CallAsync();

        Assert.Equal("answer", response.Text);
        Assert.Equal(2, rig.Requests);
        Assert.Equal(1, rig.Reading.Retries);
        Assert.Equal(StepOutcomes.Ok, rig.Reading.Outcome);
    }

    [Fact]
    public async Task A_buffered_503_every_time_exhausts_with_status_503()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ServiceUnavailable()));

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.CallAsync());

        Assert.Equal(503, failure.StatusCode);
        Assert.Equal(RetryOptions.Default.MaxAttempts, failure.Attempts);
        Assert.Equal(RetryOptions.Default.MaxAttempts, rig.Requests);
        Assert.Equal(StepOutcomes.Error, rig.Reading.Outcome);
    }

    [Fact]
    public async Task A_buffered_500_that_names_ResourceExhausted_is_retried()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, ResourceExhausted500(), FakeAnswer.Ok()));

        ChatResponse response = await rig.CallAsync();

        Assert.Equal("answer", response.Text);
        Assert.Equal(2, rig.Requests);
        Assert.Equal(1, rig.Reading.Retries);
    }

    [Fact]
    public async Task A_buffered_500_that_names_ResourceExhausted_every_time_exhausts_with_status_500()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, ResourceExhausted500()));

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.CallAsync());

        Assert.Equal(500, failure.StatusCode);
        Assert.Equal(RetryOptions.Default.MaxAttempts, rig.Requests);
    }

    [Fact]
    public async Task A_buffered_500_without_ResourceExhausted_is_not_retried_and_the_step_is_an_error()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 500, 200));

        ClientResultException failure = await Assert.ThrowsAsync<ClientResultException>(() => rig.CallAsync());

        Assert.Equal(500, failure.Status);
        Assert.Equal(1, rig.Requests);
        Assert.Equal(0, rig.Reading.Retries);
        Assert.Equal(StepOutcomes.Error, rig.Reading.Outcome);
    }

    // ----- the same statuses on the streaming path (the status comes out of the first read)

    [Fact]
    public async Task A_streaming_503_is_retried()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ServiceUnavailable(), FakeAnswer.Ok()));

        string text = await rig.StreamAsync();

        Assert.Equal("answer", text);
        Assert.Equal(1, rig.Reading.Retries);
        Assert.Equal(2, rig.Requests);
    }

    [Fact]
    public async Task A_streaming_500_that_names_ResourceExhausted_is_retried()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, ResourceExhausted500(), FakeAnswer.Ok()));

        string text = await rig.StreamAsync();

        Assert.Equal("answer", text);
        Assert.Equal(1, rig.Reading.Retries);
    }

    [Fact]
    public async Task A_streaming_500_without_ResourceExhausted_is_not_retried()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 500, 200));

        ClientResultException failure = await Assert.ThrowsAsync<ClientResultException>(() => rig.StreamAsync());

        Assert.Equal(500, failure.Status);
        Assert.Equal(1, rig.Requests);
        Assert.Equal(StepOutcomes.Error, rig.Reading.Outcome);
    }

    // ----- the classification of the outcome

    [Theory]
    [InlineData(Tier.Local, false)]
    [InlineData(Tier.Nvidia, false)]
    [InlineData(Tier.Nvidia, true)]
    [InlineData(Tier.CloudGpu, false)]
    [InlineData(Tier.CloudGpu, true)]
    public void A_provider_error_is_an_error_on_every_tier_before_and_after_the_first_update(Tier tier, bool firstUpdateSeen)
    {
        var failure = new ProviderErrorException(200, 500, "internal_server_error", "worker failed", new SecretRedactor());

        Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(failure, tier, firstUpdateSeen, CancellationToken.None));
    }

    // ----- the handler: pass-through, detection, the bound and the mask

    [Fact]
    public async Task A_stream_without_an_error_chunk_passes_through_byte_identical_whatever_the_read_size()
    {
        string body = new SseBuilder("model-x").Role().Text("hel").Text("lo").Finish().Usage(11, 22).Done().Build();
        body = ": keep-alive comment\n\nevent: ping\n" + body + "data: not json at all\n\ndata: {\"id\":\"x\",\"choices\":[],\"error_count\":1}\n\n";
        byte[] original = Encoding.UTF8.GetBytes(body);
        string expected = Convert.ToHexString(SHA256.HashData(original));

        foreach (int piece in new[] { 1, 2, 3, 7, 64, 4096 })
        {
            using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent(original, piece));

            (byte[] bytes, Exception? failure) = await ReadAllAsync(response.Content);

            Assert.Null(failure);
            Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(bytes)));
            Assert.Equal(original.Length, bytes.Length);
        }
    }

    [Fact]
    public async Task A_stream_with_crlf_line_ends_passes_through_byte_identical()
    {
        string body = new SseBuilder().Role().Text("a").Finish().Done().Build().Replace("\n", "\r\n", StringComparison.Ordinal);
        byte[] original = Encoding.UTF8.GetBytes(body);

        using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent(original, 5));
        (byte[] bytes, Exception? failure) = await ReadAllAsync(response.Content);

        Assert.Null(failure);
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(4096)]
    public async Task An_error_payload_makes_the_stream_throw_a_provider_error_with_its_fields(int piece)
    {
        string body = "data: {\"error\":{\"message\":\"" + FakeAnswer.ResourceExhaustedMessage + "\",\"type\":\"internal_server_error\",\"code\":500}}\n\ndata: [DONE]\n\n";

        using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent(Encoding.UTF8.GetBytes(body), piece));
        (byte[] bytes, Exception? failure) = await ReadAllAsync(response.Content);

        ProviderErrorException error = Assert.IsType<ProviderErrorException>(failure);

        // The bytes of the error line that arrived before the line was complete have been handed on (a stream cannot be held back without a look-ahead); its line feed never reaches the reader,
        // so the reader never sees the event complete.
        Assert.StartsWith(Encoding.UTF8.GetString(bytes), body, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', Encoding.UTF8.GetString(bytes));
        Assert.Equal(200, error.HttpStatus);
        Assert.Equal(500, error.ErrorCode);
        Assert.Equal("internal_server_error", error.ErrorType);
        Assert.Equal(FakeAnswer.ResourceExhaustedMessage, error.Message);
        Assert.Equal("provider-error: internal_server_error 500 " + FakeAnswer.ResourceExhaustedMessage, error.StepText);
        Assert.Equal(ExitCode.Unreachable, error.ExitCode);
    }

    [Fact]
    public async Task The_bytes_before_an_error_payload_reach_the_reader_and_the_error_comes_on_the_next_read()
    {
        string good = new SseBuilder().Role().Text("par").Build();
        string bad = "data: {\"error\":{\"message\":\"boom\",\"type\":\"t\",\"code\":\"503\"}}\n\n";

        using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent(Encoding.UTF8.GetBytes(good + bad), 4096));
        (byte[] bytes, Exception? failure) = await ReadAllAsync(response.Content, bufferSize: 4096);

        ProviderErrorException error = Assert.IsType<ProviderErrorException>(failure);
        Assert.Equal(good, Encoding.UTF8.GetString(bytes));
        Assert.Equal(503, error.ErrorCode);
        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task An_error_payload_without_a_type_or_a_numeric_code_is_a_provider_error_all_the_same()
    {
        using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent(Encoding.UTF8.GetBytes("data: {\"error\":{\"message\":\"no code here\"}}\n\n"), 9));
        (_, Exception? failure) = await ReadAllAsync(response.Content);

        ProviderErrorException error = Assert.IsType<ProviderErrorException>(failure);
        Assert.Null(error.ErrorCode);
        Assert.Null(error.ErrorType);
        Assert.Equal("provider-error: no code here", error.StepText);
    }

    [Fact]
    public async Task An_error_payload_that_is_a_bare_string_or_has_no_message_is_a_provider_error()
    {
        using HttpResponseMessage bare = await ThroughHandlerAsync(new PiecewiseContent(Encoding.UTF8.GetBytes("data: {\"error\":\"overloaded\"}\n\n"), 3));
        (_, Exception? bareFailure) = await ReadAllAsync(bare.Content);
        using HttpResponseMessage empty = await ThroughHandlerAsync(new PiecewiseContent(Encoding.UTF8.GetBytes("data: {\"error\":{\"code\":500}}\n\n"), 3));
        (_, Exception? emptyFailure) = await ReadAllAsync(empty.Content);

        Assert.Equal("overloaded", Assert.IsType<ProviderErrorException>(bareFailure).Message);
        ProviderErrorException error = Assert.IsType<ProviderErrorException>(emptyFailure);
        Assert.Equal(500, error.ErrorCode);
        Assert.Equal(string.Empty, error.Message);
    }

    [Fact]
    public async Task An_error_payload_on_the_last_line_without_a_terminator_is_found_at_the_end_of_the_stream()
    {
        using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent(Encoding.UTF8.GetBytes("data: {\"error\":{\"message\":\"late\",\"code\":500}}"), 6));
        (_, Exception? failure) = await ReadAllAsync(response.Content);

        Assert.Equal("late", Assert.IsType<ProviderErrorException>(failure).Message);
    }

    [Fact]
    public async Task A_payload_with_choices_passes_even_when_it_has_an_error_member()
    {
        byte[] original = Encoding.UTF8.GetBytes("data: {\"choices\":[],\"error\":{\"message\":\"not an error of the call\"},\"usage\":{\"prompt_tokens\":1}}\n\ndata: [DONE]\n\n");

        using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent(original, 8));
        (byte[] bytes, Exception? failure) = await ReadAllAsync(response.Content);

        Assert.Null(failure);
        Assert.Equal(original, bytes);
    }

    [Fact]
    public async Task The_message_is_cleaned_of_control_characters_and_bounded_to_200_characters()
    {
        string payload = new System.Text.Json.Nodes.JsonObject
        {
            ["error"] = new System.Text.Json.Nodes.JsonObject { ["message"] = "line one\u001b[31m\nline two " + new string('x', 400), ["type"] = new string('t', 100), ["code"] = 500 },
        }.ToJsonString();

        using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent(Encoding.UTF8.GetBytes("data: " + payload + "\n\n"), 4096));
        (_, Exception? failure) = await ReadAllAsync(response.Content);

        ProviderErrorException error = Assert.IsType<ProviderErrorException>(failure);
        Assert.Equal(ProviderErrorException.MaxMessageLength, error.Message.Length);
        Assert.StartsWith("line one?[31m?line two ", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(error.Message, static character => char.IsControl(character));
        Assert.Equal(ProviderErrorException.MaxTypeLength, error.ErrorType!.Length);
    }

    [Fact]
    public async Task A_registered_key_in_the_error_message_never_reaches_the_exception()
    {
        string key = ProviderClientFixtures.FakeKey();
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(key));
        string body = "data: " + new System.Text.Json.Nodes.JsonObject
        {
            ["error"] = new System.Text.Json.Nodes.JsonObject { ["message"] = "rejected key " + key + " for the worker", ["type"] = "auth " + key, ["code"] = 401 },
        }.ToJsonString() + "\n\n";

        using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent(Encoding.UTF8.GetBytes(body), 11), redactor);
        (_, Exception? failure) = await ReadAllAsync(response.Content);

        ProviderErrorException error = Assert.IsType<ProviderErrorException>(failure);
        Assert.DoesNotContain(key, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key, error.StepText, StringComparison.Ordinal);
        Assert.DoesNotContain(key, error.ErrorType!, StringComparison.Ordinal);
        Assert.Contains("****", error.Message, StringComparison.Ordinal);
        Assert.Equal("rejected key **** for the worker", error.Message);
    }

    [Fact]
    public async Task A_registered_key_in_the_error_chunk_never_reaches_the_failure_the_pipeline_throws()
    {
        string key = ProviderClientFixtures.FakeKey();
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(key));
        var handler = new FakeHttpMessageHandler();
        handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ErrorChunk("denied " + key, 401));
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        using ProviderClientHost host = ProviderClientFixtures.Build(options, handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = handler }, redactor: redactor);
        using IChatClient client = host.Factory.Create(Tier.Nvidia, "model-nvidia");

        ProviderErrorException failure = await Assert.ThrowsAsync<ProviderErrorException>(async () =>
        {
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Hello))
            {
                _ = update;
            }
        });

        Assert.DoesNotContain(key, failure.Message, StringComparison.Ordinal);
        Assert.Equal("denied ****", failure.Message);
        Assert.Equal(401, failure.ErrorCode);
    }

    [Fact]
    public async Task Content_that_is_not_an_event_stream_a_request_that_is_not_a_completion_and_a_failure_status_pass_untouched()
    {
        byte[] body = Encoding.UTF8.GetBytes("data: {\"error\":{\"message\":\"x\",\"code\":500}}\n\n");
        var json = new ByteArrayContent(body);
        json.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using HttpResponseMessage notStream = await ThroughHandlerAsync(json);
        using HttpResponseMessage otherRoute = await ThroughHandlerAsync(new PiecewiseContent(body, 3), path: "/v1/models");
        using HttpResponseMessage failed = await ThroughHandlerAsync(new PiecewiseContent(body, 3), status: HttpStatusCode.ServiceUnavailable);

        Assert.Same(json, notStream.Content);
        Assert.Equal(body, (await ReadAllAsync(otherRoute.Content)).Bytes);
        Assert.Equal(body, (await ReadAllAsync(failed.Content)).Bytes);
    }

    // ----- the step failure through the command

    [Fact]
    public async Task A_provider_error_that_is_not_retried_stops_the_tier_with_the_provider_error_in_the_failure_and_the_partial_file()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        string key = ProviderClientFixtures.FakeKey();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);
        nvidia.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ErrorChunk("worker refused the request " + key, 400));
        RunCommandSetup setup = Failing.Setup(workspace, time, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });

        CliRunResult result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, time);

        Failing.AssertExit(5, result);
        Assert.Equal(1, nvidia.Count(FakeRoute.ChatCompletions));
        string error = Assert.Single(result.ErrorLines);
        Assert.Contains("Step Planner failed with ProviderErrorException; provider-error: internal_server_error 400 worker refused the request", error, StringComparison.Ordinal);
        Assert.EndsWith("(StepFailedException)", error, StringComparison.Ordinal);
        string path = Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root));
        using JsonDocument document = RunOrchestrationFixtures.Read(path);
        Assert.Equal("failed", Failing.Text(document.RootElement.GetProperty("run"), "status"));
        JsonElement step = Assert.Single(RunOrchestrationFixtures.Steps(document));
        Assert.Equal(("Planner", "error"), (Failing.Text(step, "agent"), Failing.Text(step, "outcome")));
        Assert.False(step.TryGetProperty("outputChars", out JsonElement chars) && chars.ValueKind == JsonValueKind.Number, "A provider error must not leave an output length.");
        Assert.DoesNotContain(key, result.Stdout + result.Stderr + File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resource_exhausted_error_chunk_that_clears_on_the_second_attempt_lets_the_command_finish()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);
        nvidia.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ErrorChunk(), FakeAnswer.Ok(CallTime));
        RunCommandSetup setup = Failing.Setup(workspace, time, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });

        CliRunResult result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, time);

        Failing.AssertExit(0, result);
        string path = Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root));
        using JsonDocument document = RunOrchestrationFixtures.Read(path);
        JsonElement planner = RunOrchestrationFixtures.Steps(document).First(static step => step.GetProperty("agent").GetString() == "Planner");
        Assert.Equal("ok", Failing.Text(planner, "outcome"));
        Assert.Equal(1, planner.GetProperty("retries").GetInt32());
    }

    [Fact]
    public async Task A_mebibyte_line_keeps_the_scan_buffer_bounded_and_a_later_error_is_still_found()
    {
        byte[] huge = Encoding.UTF8.GetBytes("data: {\"filler\":\"" + new string('a', 1024 * 1024) + "\"}\n\n");
        byte[] error = Encoding.UTF8.GetBytes("data: {\"error\":{\"message\":\"after the long line\",\"code\":500}}\n\n");
        using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent([.. huge, .. error], 64 * 1024));
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        var received = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        ProviderErrorException? failure = null;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                received.Write(buffer, 0, read);
            }
        }
        catch (ProviderErrorException found)
        {
            failure = found;
        }

        // The long line passed through whole and unchanged, the line buffer never grew past its bound, and the error behind the long line was found.
        Assert.Equal(huge.Length, received.Length);
        Assert.Equal(huge, received.ToArray());
        object scanner = stream.GetType().GetField("_scanner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(stream)!;
        byte[] line = (byte[])scanner.GetType().GetField("_line", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(scanner)!;
        Assert.True(line.Length <= StreamErrorDetectingHandler.MaxLineBytes, $"The line buffer holds {line.Length} bytes.");
        Assert.Equal("after the long line", Assert.IsType<ProviderErrorException>(failure).Message);
    }

    [Fact]
    public async Task An_error_message_that_is_not_valid_utf8_is_still_a_provider_error_with_a_fixed_text()
    {
        byte[] head = Encoding.UTF8.GetBytes("data: {\"error\":{\"message\":\"bad ");
        byte[] tail = Encoding.UTF8.GetBytes(" byte\",\"type\":\"t\",\"code\":500}}\n\n");
        byte[] body = [.. head, 0xFF, .. tail];

        using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent(body, 4096));
        (_, Exception? failure) = await ReadAllAsync(response.Content, bufferSize: 4096);

        ProviderErrorException error = Assert.IsType<ProviderErrorException>(failure);
        Assert.Equal("(message not readable)", error.Message);
        Assert.Equal("provider-error: (message not readable)", error.StepText);
    }

    [Fact]
    public async Task A_read_into_an_empty_buffer_is_not_the_end_of_the_stream()
    {
        byte[] body = Encoding.UTF8.GetBytes("data: {\"error\":{\"message\":\"late\",\"code\":500}}");
        using HttpResponseMessage response = await ThroughHandlerAsync(new PiecewiseContent(body, 10));
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[10];

        // Ten bytes of the unfinished line are buffered; an empty read in between must neither throw nor drop what is buffered, in either form of the read.
        Assert.Equal(10, await stream.ReadAsync(buffer));
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.Equal(0, stream.Read([]));
        ProviderErrorException? failure = null;
        try
        {
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (ProviderErrorException found)
        {
            failure = found;
        }

        Assert.Equal("late", Assert.IsType<ProviderErrorException>(failure).Message);
    }

    [Fact]
    public async Task An_error_chunk_that_never_clears_ends_the_step_with_the_exhaustion_wording_and_the_last_error()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);
        nvidia.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.ErrorChunk());
        RunCommandSetup setup = Failing.Setup(workspace, time, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });

        CliRunResult result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, time);

        Failing.AssertExit(5, result);
        Assert.Equal(5, nvidia.Count(FakeRoute.ChatCompletions));
        string error = Assert.Single(result.ErrorLines);
        Assert.Contains("Step Planner failed with RetriesExhaustedException; error code 500 in the event stream after 5 attempts; provider-error: internal_server_error 500 " + FakeAnswer.ResourceExhaustedMessage, error, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP status", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detail_of_a_step_failure_is_cut_at_420_characters_and_survives_the_copy_with_the_partial_file()
    {
        string detail = new('d', 421);

        var failure = new StepFailedException("Planner", "X", null, new InvalidOperationException("inner"), detail);
        StepFailedException copy = failure.WithPartialFile("results/file.json");

        Assert.Equal(detail, failure.Detail);
        Assert.Contains("; " + new string('d', 420) + "...", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('d', 421), failure.Message, StringComparison.Ordinal);
        Assert.EndsWith("; partial results in results/file.json", copy.Message, StringComparison.Ordinal);
        Assert.Contains("; " + new string('d', 420) + "...", copy.Message, StringComparison.Ordinal);
        Assert.Equal(detail, copy.Detail);
    }

    // ----- helpers

    private static async Task<HttpResponseMessage> ThroughHandlerAsync(HttpContent content, SecretRedactor? redactor = null, string path = ChatPath, HttpStatusCode status = HttpStatusCode.OK)
    {
        content.Headers.ContentType ??= new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        var handler = new StreamErrorDetectingHandler(redactor ?? new SecretRedactor()) { InnerHandler = new FixedResponseHandler(content, status) };
        var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(path.EndsWith("/models", StringComparison.Ordinal) ? HttpMethod.Get : HttpMethod.Post, new Uri("https://fake.test" + path));
        return await invoker.SendAsync(request, CancellationToken.None);
    }

    // Reads the whole content in small buffers; a provider error ends the read and is returned with the bytes that came before it.
    private static async Task<(byte[] Bytes, Exception? Failure)> ReadAllAsync(HttpContent content, int bufferSize = 16)
    {
        var received = new MemoryStream();
        try
        {
            await using Stream stream = await content.ReadAsStreamAsync();
            byte[] buffer = new byte[bufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                received.Write(buffer, 0, read);
            }

            return (received.ToArray(), null);
        }
        catch (ProviderErrorException failure)
        {
            return (received.ToArray(), failure);
        }
    }

    private sealed class FixedResponseHandler(HttpContent content, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = content, RequestMessage = request });
    }

    // A response body that hands out at most `piece` bytes per read, like a network stream that delivers a stream of events in arbitrary fragments.
    private sealed class PiecewiseContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly int _piece;

        public PiecewiseContent(byte[] data, int piece)
        {
            _data = data;
            _piece = piece;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => new PiecewiseStream(_data, _piece);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new PiecewiseStream(_data, _piece));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new PiecewiseStream(_data, _piece));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;
            return true;
        }
    }

    private sealed class PiecewiseStream(byte[] data, int piece) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(Math.Min(buffer.Length, piece), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}