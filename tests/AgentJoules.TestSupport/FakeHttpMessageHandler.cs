using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentJoules.TestSupport;

/// <summary>The routes a <see cref="FakeHttpMessageHandler"/> tells apart by method and path suffix.</summary>
public enum FakeRoute
{
    /// <summary><c>GET .../models</c>.</summary>
    Models = 1,

    /// <summary><c>POST .../chat/completions</c>.</summary>
    ChatCompletions = 2,

    /// <summary>Any other request; answered 404.</summary>
    Other = 3,
}

/// <summary>
/// One request a <see cref="FakeHttpMessageHandler"/> received, as the primary handler sees it (after every delegating handler of the pipeline ran).
/// The key is never recorded: the request has a flag and a scheme word, not the header value.
/// </summary>
/// <param name="Route">The route the request matched.</param>
/// <param name="Method">The HTTP method, for example <c>POST</c>.</param>
/// <param name="Uri">The absolute request URI.</param>
/// <param name="Body">The body text, or null when the request has no content; a chat body is JSON.</param>
/// <param name="MediaType">The media type of the content, or null.</param>
/// <param name="ContentLength">The <c>Content-Length</c> of the content, or null.</param>
/// <param name="HasBearerToken">True when an <c>Authorization</c> header is present with a non-empty parameter.</param>
/// <param name="AuthorizationScheme">The scheme word of the <c>Authorization</c> header (<c>Bearer</c>), or null; never the parameter.</param>
public sealed record RecordedRequest(
    FakeRoute Route,
    string Method,
    Uri Uri,
    string? Body,
    string? MediaType,
    long? ContentLength,
    bool HasBearerToken,
    string? AuthorizationScheme)
{
    /// <summary>The body parsed as a JSON object, a fresh tree on every read, or null when there is no body or it is not an object.</summary>
    public JsonObject? Json => Body is null ? null : JsonNode.Parse(Body) as JsonObject;
}

/// <summary>
/// The fake HTTP endpoint every provider test runs against (design 6.1): it is the primary handler of a named <see cref="HttpClient"/>, so the real
/// OpenAI SDK, the real Microsoft.Extensions.AI adapter and the real delegating handlers run and nothing leaves the process. It routes
/// <c>GET .../models</c> to a model list and <c>POST .../chat/completions</c> to a buffered completion or, when the request body has
/// <c>"stream": true</c>, a server-sent-events body built by <see cref="SseBuilder"/>; any other request is answered 404.
/// </summary>
/// <remarks>
/// A status per route can be scripted (<see cref="SetStatuses"/>: the statuses are used in order and the last one repeats, which is how a later story
/// scripts a 429 followed by a 200; <see cref="AlwaysStatus"/> answers one status on every route). A status of 400 or more answers a small JSON error
/// body. <see cref="ConnectionRefused"/> throws an <see cref="HttpRequestException"/> with an inner <see cref="SocketException"/> as the transport
/// does. <see cref="CustomizeResponse"/> is the hook for headers the story needs (a <c>Retry-After</c>); nothing here reads it yet. Every request is
/// recorded before it is answered, including a refused one. All state, the two settings included, is guarded by one lock, so a test may call the handler from several tasks and change a setting between calls.
/// </remarks>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private const string ModelsSuffix = "/models";
    private const string ChatSuffix = "/chat/completions";
    private const string DefaultModel = "fake-model";

    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Dictionary<FakeRoute, Queue<int>> _scripted = [];
    private readonly Dictionary<FakeRoute, int> _lastStatus = [];

    private bool _connectionRefused;
    private Action<FakeRoute, HttpResponseMessage>? _customizeResponse;

    /// <summary>The text a buffered completion answers.</summary>
    public string ChatText { get; init; } = "answer";

    /// <summary>The text deltas a streaming completion sends, in order.</summary>
    public IReadOnlyList<string> StreamDeltas { get; init; } = ["ans", "wer"];

    /// <summary>The <c>prompt_tokens</c> a completion reports, or null to send no usage at all.</summary>
    public int? PromptTokens { get; init; } = 7;

    /// <summary>The <c>completion_tokens</c> a completion reports, or null to send no usage at all.</summary>
    public int? CompletionTokens { get; init; } = 3;

    /// <summary>The model IDs the models route lists.</summary>
    public IReadOnlyList<string> ModelIds { get; init; } = [DefaultModel];

    /// <summary>How long every answer is held back (a real wait, bounded by the request token); zero answers at once.</summary>
    public TimeSpan ResponseDelay { get; init; } = TimeSpan.Zero;

    /// <summary>True to refuse every connection: the request is recorded and an <see cref="HttpRequestException"/> with an inner <see cref="SocketException"/> is thrown.</summary>
    public bool ConnectionRefused
    {
        get => Settings().Refused;
        set
        {
            lock (_gate)
            {
                _connectionRefused = value;
            }
        }
    }

    /// <summary>A hook that runs on every answered response before it is returned; null for none.</summary>
    public Action<FakeRoute, HttpResponseMessage>? CustomizeResponse
    {
        get => Settings().Customize;
        set
        {
            lock (_gate)
            {
                _customizeResponse = value;
            }
        }
    }

    /// <summary>Every request received so far, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The number of requests received so far.</summary>
    public int RequestCount => Requests.Count;

    /// <summary>The requests of one route, in order.</summary>
    /// <param name="route">The route.</param>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<RecordedRequest> RequestsFor(FakeRoute route) => [.. Requests.Where(request => request.Route == route)];

    /// <summary>The number of requests of one route received so far.</summary>
    /// <param name="route">The route.</param>
    /// <returns>The count.</returns>
    public int Count(FakeRoute route) => RequestsFor(route).Count;

    /// <summary>Scripts the statuses of a route: they are used one per request in order and the last one repeats; the default status is 200.</summary>
    /// <param name="route">The route.</param>
    /// <param name="statuses">At least one HTTP status code.</param>
    /// <exception cref="ArgumentException"><paramref name="statuses"/> is empty.</exception>
    public void SetStatuses(FakeRoute route, params int[] statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (statuses.Length == 0)
        {
            throw new ArgumentException("At least one status is needed.", nameof(statuses));
        }

        lock (_gate)
        {
            _scripted[route] = new Queue<int>(statuses);
            _ = _lastStatus.Remove(route);
        }
    }

    /// <summary>Answers <paramref name="status"/> on every route, the always-500 mode of the retry test.</summary>
    /// <param name="status">The HTTP status code.</param>
    public void AlwaysStatus(int status)
    {
        foreach (FakeRoute route in Enum.GetValues<FakeRoute>())
        {
            SetStatuses(route, status);
        }
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        FakeRoute route = Classify(request);
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Record(request, route, body);

        (bool refused, Action<FakeRoute, HttpResponseMessage>? customize) = Settings();
        if (refused)
        {
            throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
        }

        if (ResponseDelay > TimeSpan.Zero)
        {
            await Task.Delay(ResponseDelay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        int status = NextStatus(route);
        HttpResponseMessage response = status >= 400 ? Error(status) : route switch
        {
            FakeRoute.Models => Json(ModelsBody()),
            FakeRoute.ChatCompletions => Chat(body),
            _ => Error((int)HttpStatusCode.NotFound),
        };

        // A scripted 2xx or 3xx status is answered as scripted, with the body of the route (the route-less 404 stays 404).
        if (status < 400 && route != FakeRoute.Other)
        {
            response.StatusCode = (HttpStatusCode)status;
        }

        response.RequestMessage = request;
        customize?.Invoke(route, response);
        return response;
    }

    private static FakeRoute Classify(HttpRequestMessage request)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        return request.Method == HttpMethod.Get && path.EndsWith(ModelsSuffix, StringComparison.Ordinal) ? FakeRoute.Models
            : request.Method == HttpMethod.Post && path.EndsWith(ChatSuffix, StringComparison.Ordinal) ? FakeRoute.ChatCompletions
            : FakeRoute.Other;
    }

    private void Record(HttpRequestMessage request, FakeRoute route, string? body)
    {
        AuthenticationHeaderValue? authorization = request.Headers.Authorization;
        var recorded = new RecordedRequest(
            route,
            request.Method.Method,
            request.RequestUri ?? throw new InvalidOperationException("The request has no absolute URI."),
            body,
            request.Content?.Headers.ContentType?.MediaType,
            request.Content?.Headers.ContentLength,
            HasBearerToken: !string.IsNullOrEmpty(authorization?.Parameter),
            AuthorizationScheme: authorization?.Scheme);

        lock (_gate)
        {
            _requests.Add(recorded);
        }
    }

    private (bool Refused, Action<FakeRoute, HttpResponseMessage>? Customize) Settings()
    {
        lock (_gate)
        {
            return (_connectionRefused, _customizeResponse);
        }
    }

    private int NextStatus(FakeRoute route)
    {
        lock (_gate)
        {
            if (_scripted.TryGetValue(route, out Queue<int>? queue) && queue.Count > 0)
            {
                int next = queue.Dequeue();
                _lastStatus[route] = next;
                return next;
            }

            return _lastStatus.TryGetValue(route, out int last) ? last : (int)HttpStatusCode.OK;
        }
    }

    private string ModelsBody()
    {
        var data = new JsonArray();
        foreach (string id in ModelIds)
        {
            data.Add(new JsonObject { ["id"] = id, ["object"] = "model", ["created"] = 1_700_000_000, ["owned_by"] = "fake" });
        }

        return new JsonObject { ["object"] = "list", ["data"] = data }.ToJsonString();
    }

    private HttpResponseMessage Chat(string? requestBody)
    {
        JsonObject? request = null;
        if (requestBody is not null)
        {
            try
            {
                request = JsonNode.Parse(requestBody) as JsonObject;
            }
            catch (JsonException)
            {
                request = null;
            }
        }

        string model = request?["model"] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text) ? text : DefaultModel;
        bool streaming = request?["stream"] is JsonValue flag && flag.TryGetValue(out bool stream) && stream;

        return streaming
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SseBuilder.Standard(model, StreamDeltas, PromptTokens, CompletionTokens), Encoding.UTF8, "text/event-stream"),
            }
            : Json(BufferedCompletion(model));
    }

    private string BufferedCompletion(string model)
    {
        var completion = new JsonObject
        {
            ["id"] = "chatcmpl-fake",
            ["object"] = "chat.completion",
            ["created"] = 1_700_000_000,
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = ChatText },
                ["finish_reason"] = "stop",
            }),
        };

        if (PromptTokens is { } prompt && CompletionTokens is { } completionTokens)
        {
            completion["usage"] = new JsonObject
            {
                ["prompt_tokens"] = prompt,
                ["completion_tokens"] = completionTokens,
                ["total_tokens"] = prompt + completionTokens,
            };
        }

        return completion.ToJsonString();
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(int status)
    {
        string body = new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = "fake error", ["type"] = "fake_error", ["code"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        }.ToJsonString();

        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}