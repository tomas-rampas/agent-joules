using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>AJ-044 (REQ-012): the fake HTTP endpoint and the SSE builder that every provider test stands on behave as design 6.1 says.</summary>
[Trait("Spec", "REQ-012")]
public sealed class FakeHttpMessageHandlerTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly Uri ModelsUri = new("https://fake.test/v1/models");
    private static readonly Uri ChatUri = new("https://fake.test/v1/chat/completions");

    private static HttpMessageInvoker Invoker(FakeHttpMessageHandler handler) => new(handler, disposeHandler: false);

    private static HttpRequestMessage Chat(string body, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ChatUri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }

    [Fact]
    public async Task Routes_models_and_buffered_chat_and_answers_404_for_anything_else()
    {
        var handler = new FakeHttpMessageHandler { ModelIds = ["m-1", "m-2"], ChatText = "hi", PromptTokens = 5, CompletionTokens = 1 };
        using HttpMessageInvoker invoker = Invoker(handler);

        using HttpResponseMessage models = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, ModelsUri), CancellationToken.None).WaitAsync(HangGuard);
        using HttpResponseMessage chat = await invoker.SendAsync(Chat("""{"model":"m-1"}"""), CancellationToken.None).WaitAsync(HangGuard);
        using HttpResponseMessage other = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("https://fake.test/v1/other")), CancellationToken.None).WaitAsync(HangGuard);

        JsonObject list = JsonNode.Parse(await models.Content.ReadAsStringAsync())!.AsObject();
        JsonObject completion = JsonNode.Parse(await chat.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("list", list["object"]!.GetValue<string>());
        string[] ids = [.. list["data"]!.AsArray().Select(model => model!["id"]!.GetValue<string>())];
        Assert.Equal(["m-1", "m-2"], ids);
        Assert.Equal("chat.completion", completion["object"]!.GetValue<string>());
        Assert.Equal("m-1", completion["model"]!.GetValue<string>());
        Assert.Equal("hi", completion["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal(6, completion["usage"]!["total_tokens"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(1, handler.Count(FakeRoute.Models));
        Assert.Equal(1, handler.Count(FakeRoute.ChatCompletions));
        Assert.Equal(1, handler.Count(FakeRoute.Other));
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Answers_server_sent_events_when_the_request_asks_to_stream()
    {
        var handler = new FakeHttpMessageHandler { StreamDeltas = ["a", "b"], PromptTokens = 11, CompletionTokens = 2 };
        using HttpMessageInvoker invoker = Invoker(handler);

        using HttpResponseMessage response = await invoker.SendAsync(Chat("""{"model":"m","stream":true}"""), CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        string[] events = (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("data: [DONE]", events[^1]);
        Assert.All(events, entry => Assert.StartsWith("data: ", entry, StringComparison.Ordinal));
    }

    [Fact]
    public void Sse_stream_has_a_role_chunk_text_deltas_a_finish_chunk_a_usage_chunk_and_the_terminator()
    {
        string body = SseBuilder.Standard("m", ["Hel", "lo"], 11, 2);

        string[] events = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        JsonObject[] chunks = [.. events[..^1].Select(entry => JsonNode.Parse(entry["data: ".Length..])!.AsObject())];

        Assert.Equal(6, events.Length);
        Assert.All(chunks, chunk => Assert.Equal("chat.completion.chunk", chunk["object"]!.GetValue<string>()));
        Assert.Equal("assistant", chunks[0]["choices"]![0]!["delta"]!["role"]!.GetValue<string>());
        Assert.Equal("Hel", chunks[1]["choices"]![0]!["delta"]!["content"]!.GetValue<string>());
        Assert.Equal("lo", chunks[2]["choices"]![0]!["delta"]!["content"]!.GetValue<string>());
        Assert.Equal("stop", chunks[3]["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.Empty(chunks[4]["choices"]!.AsArray());
        Assert.Equal(11, chunks[4]["usage"]!["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(2, chunks[4]["usage"]!["completion_tokens"]!.GetValue<int>());
        Assert.Equal("data: [DONE]", events[^1]);
        Assert.DoesNotContain("usage", SseBuilder.Standard("m", ["x"], null, null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripted_statuses_are_used_in_order_and_the_last_one_repeats()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetStatuses(FakeRoute.ChatCompletions, 429, 200, 503);
        using HttpMessageInvoker invoker = Invoker(handler);
        var seen = new List<HttpStatusCode>();

        for (int i = 0; i < 5; i++)
        {
            using HttpResponseMessage response = await invoker.SendAsync(Chat("{}"), CancellationToken.None).WaitAsync(HangGuard);
            seen.Add(response.StatusCode);
        }

        Assert.Equal([(HttpStatusCode)429, HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable], seen);
        Assert.Throws<ArgumentException>(() => handler.SetStatuses(FakeRoute.Models));
    }

    [Fact]
    public async Task A_scripted_success_status_other_than_200_is_answered_as_scripted_with_the_body_of_the_route()
    {
        var handler = new FakeHttpMessageHandler { ChatText = "hi" };
        handler.SetStatuses(FakeRoute.ChatCompletions, 202);
        handler.SetStatuses(FakeRoute.Models, 203);
        using HttpMessageInvoker invoker = Invoker(handler);

        using HttpResponseMessage chat = await invoker.SendAsync(Chat("{}"), CancellationToken.None).WaitAsync(HangGuard);
        using HttpResponseMessage models = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, ModelsUri), CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal(HttpStatusCode.Accepted, chat.StatusCode);
        Assert.Equal(HttpStatusCode.NonAuthoritativeInformation, models.StatusCode);
        Assert.Equal("chat.completion", JsonNode.Parse(await chat.Content.ReadAsStringAsync())!["object"]!.GetValue<string>());
    }

    [Fact]
    public async Task Always_status_answers_every_route_with_a_json_error_body()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AlwaysStatus(500);
        using HttpMessageInvoker invoker = Invoker(handler);

        using HttpResponseMessage chat = await invoker.SendAsync(Chat("{}"), CancellationToken.None).WaitAsync(HangGuard);
        using HttpResponseMessage models = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, ModelsUri), CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal(HttpStatusCode.InternalServerError, chat.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, models.StatusCode);
        Assert.Equal("fake error", JsonNode.Parse(await chat.Content.ReadAsStringAsync())!["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Connection_refused_mode_records_the_request_and_throws_with_a_socket_exception_inside()
    {
        var handler = new FakeHttpMessageHandler { ConnectionRefused = true };
        using HttpMessageInvoker invoker = Invoker(handler);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(Chat("{}"), CancellationToken.None));

        SocketException socket = Assert.IsType<SocketException>(failure.InnerException);
        Assert.Equal(SocketError.ConnectionRefused, socket.SocketErrorCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Customize_response_hook_sees_every_answered_response()
    {
        var routes = new List<FakeRoute>();
        var handler = new FakeHttpMessageHandler
        {
            CustomizeResponse = (route, response) =>
            {
                routes.Add(route);
                response.Headers.TryAddWithoutValidation("Retry-After", "1");
            },
        };
        using HttpMessageInvoker invoker = Invoker(handler);

        using HttpResponseMessage response = await invoker.SendAsync(Chat("{}"), CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal([FakeRoute.ChatCompletions], routes);
        Assert.True(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Recording_keeps_the_scheme_and_a_flag_but_never_a_character_of_the_key()
    {
        string key = ProviderClientFixtures.FakeKey();
        var handler = new FakeHttpMessageHandler();
        using HttpMessageInvoker invoker = Invoker(handler);

        using HttpResponseMessage withKey = await invoker.SendAsync(Chat("""{"model":"m"}""", key), CancellationToken.None).WaitAsync(HangGuard);
        using HttpResponseMessage withoutKey = await invoker.SendAsync(Chat("""{"model":"m"}"""), CancellationToken.None).WaitAsync(HangGuard);

        RecordedRequest recorded = handler.Requests[0];
        Assert.Equal("POST", recorded.Method);
        Assert.Equal(ChatUri, recorded.Uri);
        Assert.True(recorded.HasBearerToken);
        Assert.Equal("Bearer", recorded.AuthorizationScheme);
        Assert.Equal("m", recorded.Json!["model"]!.GetValue<string>());
        Assert.False(handler.Requests[1].HasBearerToken);
        Assert.Null(handler.Requests[1].AuthorizationScheme);

        string everything = recorded.ToString();
        Assert.DoesNotContain(key, everything, StringComparison.Ordinal);
        for (int start = 0; start + 4 <= key.Length; start++)
        {
            Assert.DoesNotContain(key.Substring(start, 4), everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Recording_and_counting_are_thread_safe()
    {
        var handler = new FakeHttpMessageHandler();
        using HttpMessageInvoker invoker = Invoker(handler);

        await Task.WhenAll(Enumerable.Range(0, 64).Select(async index =>
        {
            using var request = index % 2 == 0 ? Chat("{}") : new HttpRequestMessage(HttpMethod.Get, ModelsUri);
            using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);
        })).WaitAsync(HangGuard);

        Assert.Equal(64, handler.RequestCount);
        Assert.Equal(32, handler.Count(FakeRoute.ChatCompletions));
        Assert.Equal(32, handler.Count(FakeRoute.Models));
    }
}