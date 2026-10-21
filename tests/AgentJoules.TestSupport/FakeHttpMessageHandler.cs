using System.Globalization;
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
/// One scripted answer of a <see cref="FakeHttpMessageHandler"/> route (<see cref="FakeHttpMessageHandler.SetAnswers"/>): a status, and optionally a
/// <c>Retry-After</c> header, how long the call takes on the fake clock, and a stream that is cut after some chunks.
/// </summary>
/// <param name="Status">The HTTP status, from 100 to 599.</param>
/// <param name="RetryAfter">The raw <c>Retry-After</c> header value, stored verbatim and without validation so that a negative number or garbage can be scripted; null sends no header.</param>
/// <param name="Duration">How far the endpoint's fake clock moves before the answer; null takes <see cref="FakeHttpMessageHandler.DefaultDuration"/>, and an explicit zero is zero.</param>
/// <param name="StreamFailsAfterDeltas">A streaming request only: the number of text chunks that arrive (after the role chunk) before the body read fails with an <see cref="IOException"/>, as a connection that is cut mid-stream does; null streams to the end.</param>
public sealed record FakeAnswer(int Status, string? RetryAfter = null, TimeSpan? Duration = null, int? StreamFailsAfterDeltas = null)
{
    /// <summary>A 200 answer.</summary>
    /// <param name="duration">The call time on the fake clock, or null for the default of the endpoint.</param>
    /// <returns>The answer.</returns>
    public static FakeAnswer Ok(TimeSpan? duration = null) => new(200, Duration: duration);

    /// <summary>A 429 answer that takes no fake time, as a rate limiter that rejects at once does.</summary>
    /// <param name="retryAfter">The raw <c>Retry-After</c> value, or null for none.</param>
    /// <returns>The answer.</returns>
    public static FakeAnswer TooManyRequests(string? retryAfter = null) => new(429, retryAfter, TimeSpan.Zero);

    /// <summary>A 200 streaming answer whose body fails after the role chunk and <paramref name="deltas"/> text chunks.</summary>
    /// <param name="deltas">The text chunks that arrive before the failure.</param>
    /// <param name="duration">The call time on the fake clock, or null for the default of the endpoint.</param>
    /// <returns>The answer.</returns>
    public static FakeAnswer StreamFailure(int deltas, TimeSpan? duration = null) => new(200, Duration: duration, StreamFailsAfterDeltas: deltas);

    /// <summary>The HTTP-date form of a time, as a server writes it in <c>Retry-After</c> (RFC 9110, the IMF-fixdate: <c>Tue, 20 Oct 2026 12:00:03 GMT</c>).</summary>
    /// <param name="when">The time; converted to UTC.</param>
    /// <returns>The text.</returns>
    public static string HttpDate(DateTimeOffset when) => when.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
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
/// A status per route can be scripted (<see cref="SetStatuses"/>: the statuses are used in order and the last one repeats, which is how a test scripts
/// a 429 followed by a 200; <see cref="AlwaysStatus"/> answers one status on every route). <see cref="SetAnswers"/> scripts the same sequence with more per
/// answer (<see cref="FakeAnswer"/>): a <c>Retry-After</c> header as raw text, how long the call takes on the fake <see cref="Clock"/>, and a stream whose
/// body read fails after some chunks. A status of 400 or more answers a small JSON error body. <see cref="ConnectionRefused"/> throws an
/// <see cref="HttpRequestException"/> with an inner <see cref="SocketException"/> as the transport does. <see cref="CustomizeResponse"/> is the hook for
/// anything else a story needs on a response. Every request is recorded before it is answered, including a refused one. All state, the two settings
/// included, is guarded by one lock, so a test may call the handler from several tasks and change a setting between calls.
/// </remarks>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private const string ModelsSuffix = "/models";
    private const string ChatSuffix = "/chat/completions";
    private const string DefaultModel = "fake-model";

    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Dictionary<FakeRoute, Queue<FakeAnswer>> _scripted = [];
    private readonly Dictionary<FakeRoute, FakeAnswer> _lastAnswer = [];

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

    /// <summary>
    /// The fake clock that the duration of an answer moves (<see cref="FakeAnswer.Duration"/> and <see cref="DefaultDuration"/>): before it answers or refuses a
    /// request the handler advances this clock, so a test that measures the call on the same clock sees exactly that long a call and nothing waits on the wall
    /// clock, as <see cref="FakeChatClient"/> does. A positive duration with no clock is an <see cref="InvalidOperationException"/>.
    /// </summary>
    public Microsoft.Extensions.Time.Testing.FakeTimeProvider? Clock { get; init; }

    /// <summary>The fake time a request takes when its answer has no duration of its own, a refused connection and an unscripted request included; zero by default.</summary>
    public TimeSpan DefaultDuration { get; init; }

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

        SetAnswers(route, [.. statuses.Select(static status => new FakeAnswer(status))]);
    }

    /// <summary>Scripts the answers of a route: they are used one per request in order and the last one repeats; the default answer is a 200.</summary>
    /// <param name="route">The route.</param>
    /// <param name="answers">At least one answer, each with a status from 100 to 599.</param>
    /// <exception cref="ArgumentException"><paramref name="answers"/> is empty or holds a null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An answer has a status outside 100 to 599, a negative duration or a negative chunk count.</exception>
    public void SetAnswers(FakeRoute route, params FakeAnswer[] answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Length == 0 || answers.Contains(null!))
        {
            throw new ArgumentException("At least one answer is needed and none may be null.", nameof(answers));
        }

        foreach (FakeAnswer answer in answers)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(answer.Status, 100);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(answer.Status, 599);
            ArgumentOutOfRangeException.ThrowIfLessThan(answer.Duration ?? TimeSpan.Zero, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThan(answer.StreamFailsAfterDeltas ?? 0, 0);
        }

        lock (_gate)
        {
            _scripted[route] = new Queue<FakeAnswer>(answers);
            _ = _lastAnswer.Remove(route);
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
            AdvanceClock(DefaultDuration);
            throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
        }

        FakeAnswer answer = NextAnswer(route);
        AdvanceClock(answer.Duration ?? DefaultDuration);

        if (ResponseDelay > TimeSpan.Zero)
        {
            await Task.Delay(ResponseDelay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        int status = answer.Status;
        HttpResponseMessage response = status >= 400 ? Error(status) : route switch
        {
            FakeRoute.Models => Json(ModelsBody()),
            FakeRoute.ChatCompletions => Chat(body, answer),
            _ => Error((int)HttpStatusCode.NotFound),
        };

        // A scripted 2xx or 3xx status is answered as scripted, with the body of the route (the route-less 404 stays 404).
        if (status < 400 && route != FakeRoute.Other)
        {
            response.StatusCode = (HttpStatusCode)status;
        }

        // The raw text, unvalidated: a negative number or garbage is what a test of the reader of this header needs to send.
        if (answer.RetryAfter is not null)
        {
            _ = response.Headers.TryAddWithoutValidation("Retry-After", answer.RetryAfter);
        }

        response.RequestMessage = request;
        customize?.Invoke(route, response);
        return response;
    }

    private void AdvanceClock(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        (Clock ?? throw new InvalidOperationException("A duration is scripted but the endpoint has no fake clock to move.")).Advance(duration);
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

    private FakeAnswer NextAnswer(FakeRoute route)
    {
        lock (_gate)
        {
            if (_scripted.TryGetValue(route, out Queue<FakeAnswer>? queue) && queue.Count > 0)
            {
                FakeAnswer next = queue.Dequeue();
                _lastAnswer[route] = next;
                return next;
            }

            return _lastAnswer.TryGetValue(route, out FakeAnswer? last) ? last : new FakeAnswer((int)HttpStatusCode.OK);
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

    private HttpResponseMessage Chat(string? requestBody, FakeAnswer answer)
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

        if (!streaming)
        {
            return Json(BufferedCompletion(model));
        }

        // A stream that is cut: the role chunk and the first deltas arrive, then the body read fails, so the consumer has seen real updates by then.
        if (answer.StreamFailsAfterDeltas is { } delivered)
        {
            SseBuilder prefix = new SseBuilder(model).Role();
            foreach (string delta in StreamDeltas.Take(delivered))
            {
                _ = prefix.Text(delta);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new CutStreamContent(prefix.Build()) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseBuilder.Standard(model, StreamDeltas, PromptTokens, CompletionTokens), Encoding.UTF8, "text/event-stream"),
        };
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

    // The body of a stream that is cut: the prefix is readable, then every further read fails like a connection that dropped. HttpContent buffers its
    // body when it is read as a stream unless the stream is created by the content itself, so both creation paths are overridden; buffering would fail
    // before the first chunk was seen.
    private sealed class CutStreamContent : HttpContent
    {
        private readonly byte[] _prefix;

        public CutStreamContent(string prefix)
        {
            _prefix = Encoding.UTF8.GetBytes(prefix);
            Headers.ContentType = new MediaTypeHeaderValue("text/event-stream") { CharSet = "utf-8" };
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => new CutStream(_prefix);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new CutStream(_prefix));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new CutStream(_prefix));

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(_prefix).ConfigureAwait(false);
            throw CutStream.Dropped();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CutStream(byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public static IOException Dropped() => new("The connection was closed in the middle of the stream.");

        public override int Read(Span<byte> buffer)
        {
            if (_position >= prefix.Length)
            {
                throw Dropped();
            }

            int count = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ValueTask.FromResult(Read(buffer.Span));
            }
            catch (IOException dropped)
            {
                return ValueTask.FromException<int>(dropped);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}