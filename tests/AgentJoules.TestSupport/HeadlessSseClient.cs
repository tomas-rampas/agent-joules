using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

namespace AgentJoules.TestSupport;

/// <summary>One response of the dashboard as <see cref="HeadlessSseClient.GetAsync"/> saw it.</summary>
/// <param name="Status">The status code.</param>
/// <param name="Headers">Every response and content header, the values of one name joined with <c>, </c>; names compare case-insensitively.</param>
/// <param name="BodyBytes">The bytes of the body, empty when it was not read (an event stream) or there was none.</param>
/// <param name="BodyRead">False for an event stream, whose body never ends: only its headers were read.</param>
public sealed record HeadlessResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] BodyBytes, bool BodyRead)
{
    /// <summary>The body decoded as UTF-8.</summary>
    public string Body => Encoding.UTF8.GetString(BodyBytes);

    /// <summary>The value of the header <paramref name="name"/>, or null when the response has none.</summary>
    /// <param name="name">The header name.</param>
    /// <returns>The value.</returns>
    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>One server-sent event the client received.</summary>
/// <param name="ReceivedUtc">The instant on the system clock at which the blank line that ends the event was read.</param>
/// <param name="EventType">The <c>event:</c> field, or null when the event had none.</param>
/// <param name="Type">The <c>type</c> member of the envelope.</param>
/// <param name="AtUtc">The <c>atUtc</c> member of the envelope.</param>
/// <param name="Json">The data of the event: the envelope as the server wrote it.</param>
/// <param name="Lines">The raw lines of the event as they arrived, without the blank line that ends it.</param>
public sealed record SseReceipt(DateTimeOffset ReceivedUtc, string? EventType, string Type, DateTimeOffset AtUtc, string Json, IReadOnlyList<string> Lines)
{
    /// <summary>The time from the emit instant to the receipt.</summary>
    public TimeSpan Latency => ReceivedUtc - AtUtc;
}

/// <summary>The outcome of the latency check of REQ-032 R1 over the events a client received.</summary>
/// <param name="Passed">True when every event arrived within the budget.</param>
/// <param name="Count">The number of events checked.</param>
/// <param name="MaximumMs">The largest latency in milliseconds.</param>
/// <param name="Message">The verdict, naming the count, the maximum and the budget.</param>
public sealed record LatencyResult(bool Passed, int Count, double MaximumMs, string Message);

/// <summary>
/// The fixture run of the latency check (REQ-032 R1, P-80): the step code emits synthetic live events of a committed local run into the real sink on the given clock, at least
/// 50 of them, among which <see cref="PowerEvents"/> <c>power</c> events at the default interval of 100 ms, and a headless client reads them from the real server. Every event's
/// <c>atUtc</c> is its emit instant; the receipts are stamped on the system clock.
/// </summary>
public static class DashboardLatencyRun
{
    /// <summary>The number of power events, one per <see cref="PowerInterval"/>.</summary>
    public const int PowerEvents = 30;

    /// <summary>The number of events the run emits: run-started, baseline, the power events, one progress, five per step for four steps (started, three progress, completed) and run-completed.</summary>
    public const int EventCount = 1 + 1 + PowerEvents + 1 + (4 * 5) + 1;

    /// <summary>The default <c>Dashboard:PowerIntervalMs</c>.</summary>
    public static readonly TimeSpan PowerInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The budget of REQ-032 R1.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Connects a client to <paramref name="uri"/>, emits the run into <paramref name="target"/> (the sink of the server, or a delaying sink in front of it) and returns every
    /// event the client received, the two snapshots included, once every emitted event arrived.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="uri">The URI the server printed.</param>
    /// <param name="target">The sink the run emits into.</param>
    /// <param name="time">The clock of the emit instants and of the power interval.</param>
    /// <param name="guard">The real time after which a run that did not arrive is a failure.</param>
    /// <returns>The receipts in the order they arrived.</returns>
    public static async Task<IReadOnlyList<SseReceipt>> RunAsync(HeadlessSseClient client, Uri uri, ILiveEventSink target, TimeProvider time, TimeSpan guard)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(time);

        using var timeout = new CancellationTokenSource(guard);
        HeadlessSseConnection stream = await client.OpenEventsAsync(uri, cancellationToken: timeout.Token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var receipts = new List<SseReceipt>();
            Task reading = ReadAsync(stream, receipts, timeout.Token);
            await EmitAsync(target, time, timeout.Token).ConfigureAwait(false);
            await reading.ConfigureAwait(false);
            return receipts;
        }
    }

    /// <summary>Emits the run into <paramref name="sink"/>: <see cref="EventCount"/> events over <see cref="PowerEvents"/> intervals of <see cref="PowerInterval"/>.</summary>
    /// <param name="sink">The sink.</param>
    /// <param name="time">The clock of the emit instants and of the interval.</param>
    /// <param name="cancellationToken">Ends the run.</param>
    /// <returns>A task that completes after the last event.</returns>
    public static async Task EmitAsync(ILiveEventSink sink, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(time);

        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);
        StepReading[] steps = [.. run.Steps.Take(4)];
        var others = new List<Func<DateTimeOffset, double, LiveEvent>>
        {
            (at, _) => new ProgressEvent(at, false, steps[0].Task, 1, 1, 1, 1),
        };
        foreach (StepReading step in steps)
        {
            var identity = new StepIdentity(step.Agent, step.ModelId, step.Tier, step.Task, step.Repeat, IsJudge: false);
            others.Add((at, seconds) => new StepStartedEvent(at, false, identity, seconds));
            others.Add((at, _) => new StepProgressEvent(at, false, identity, 1));
            others.Add((at, _) => new StepProgressEvent(at, false, identity, 2));
            others.Add((at, _) => new StepProgressEvent(at, false, identity, 3));
            others.Add((at, _) => new StepCompletedEvent(at, false, step));
        }

        sink.EmitSafely(new RunStartedEvent(time.GetUtcNow(), false, run.Run, run.Environment, 85.0));
        sink.EmitSafely(new BaselineEvent(time.GetUtcNow(), false, run.Baseline!));
        for (int tick = 0; tick < PowerEvents; tick++)
        {
            await Task.Delay(PowerInterval, time, cancellationToken).ConfigureAwait(false);
            double seconds = (tick + 1) * PowerInterval.TotalSeconds;
            sink.EmitSafely(new PowerEvent(time.GetUtcNow(), false, Metric.Measured(30.0 + (tick % 5), "W"), seconds));
            if (tick < others.Count)
            {
                sink.EmitSafely(others[tick](time.GetUtcNow(), seconds));
            }
        }

        sink.EmitSafely(new RunCompletedEvent(time.GetUtcNow(), false, run.Run, run.Totals));
    }

    // Reads until every emitted event arrived (the snapshots are extra), whatever order a delaying sink handed them on in.
    private static async Task ReadAsync(HeadlessSseConnection stream, List<SseReceipt> receipts, CancellationToken cancellationToken)
    {
        int events = 0;
        await foreach (SseReceipt receipt in stream.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
        {
            receipts.Add(receipt);
            if (!string.Equals(receipt.Type, "snapshot", StringComparison.Ordinal) && ++events == EventCount)
            {
                return;
            }
        }
    }
}

/// <summary>An open <c>/events</c> response: its status and headers, and the events as they arrive.</summary>
public sealed class HeadlessSseConnection : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _body;
    private readonly StreamReader _reader;
    private int _disposed;

    internal HeadlessSseConnection(HttpResponseMessage response, Stream body)
    {
        _response = response;
        _body = body;
        _reader = new StreamReader(body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Status = (int)response.StatusCode;
        Headers = HeadlessSseClient.HeadersOf(response);
    }

    /// <summary>The status code of the response.</summary>
    public int Status { get; }

    /// <summary>Every response and content header.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The media type of the response, or null.</summary>
    public string? ContentType => _response.Content.Headers.ContentType?.MediaType;

    /// <summary>Reads the events until the server ends the stream or <paramref name="cancellationToken"/> is cancelled (which ends the enumeration with an <see cref="OperationCanceledException"/>).</summary>
    /// <param name="cancellationToken">Ends the read.</param>
    /// <returns>The events in the order they arrived.</returns>
    public async IAsyncEnumerable<SseReceipt> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        string? eventType = null;
        var data = new StringBuilder();
        while (await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventType = line["event:".Length..].TrimStart(' ');
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        _ = data.Append('\n');
                    }

                    _ = data.Append(line.AsSpan()["data:".Length..].TrimStart(' '));
                }

                continue;
            }

            if (lines.Count == 0)
            {
                continue;
            }

            DateTimeOffset received = TimeProvider.System.GetUtcNow();
            string json = data.ToString();
            yield return Receipt(received, eventType, json, [.. lines]);
            lines.Clear();
            eventType = null;
            _ = data.Clear();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _reader.Dispose();
            await _body.DisposeAsync().ConfigureAwait(false);
            _response.Dispose();
        }
    }

    private static SseReceipt Receipt(DateTimeOffset received, string? eventType, string json, IReadOnlyList<string> lines)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        return new SseReceipt(received, eventType, root.GetProperty("type").GetString()!, root.GetProperty("atUtc").GetDateTimeOffset(), json, lines);
    }
}

/// <summary>
/// The only HTTP client of the dashboard tests (REQ-032, P-50, P-76): it owns one <see cref="HttpClient"/> over loopback, without a proxy and without redirects, so a test never
/// builds a client of its own. <see cref="GetAsync"/> reads a whole response (only the headers of an event stream, whose body never ends); <see cref="OpenEventsAsync"/> and
/// <see cref="ReadEventsAsync"/> read <c>/events</c> with <see cref="HttpCompletionOption.ResponseHeadersRead"/>, one event at a time as it arrives, each stamped with its receipt on
/// the system clock.
/// </summary>
public sealed class HeadlessSseClient : IDisposable
{
    /// <summary>The path of the event stream.</summary>
    public const string EventsPath = "/events";

    private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Sends one GET request and reads the response. The body of an event stream (<c>text/event-stream</c>) is not read: the response is closed after its headers.
    /// </summary>
    /// <param name="baseUri">The URI the dashboard printed.</param>
    /// <param name="path">The path, for example <c>/</c> or <c>/events</c>.</param>
    /// <param name="hostHeader">The <c>Host</c> header to send instead of the one of <paramref name="baseUri"/>, or null.</param>
    /// <param name="extraHeaders">More request headers (for example <c>Origin</c>), or null.</param>
    /// <param name="cancellationToken">Ends the request.</param>
    /// <returns>The response.</returns>
    public async Task<HeadlessResponse> GetAsync(Uri baseUri, string path, string? hostHeader, IEnumerable<KeyValuePair<string, string>>? extraHeaders = null, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Request(baseUri, path, hostHeader, extraHeaders);
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> headers = HeadersOf(response);
        bool stream = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
        byte[] body = stream ? [] : await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new HeadlessResponse((int)response.StatusCode, headers, body, !stream);
    }

    /// <summary>Opens <c>/events</c> and returns once the headers arrived; the caller disposes the stream.</summary>
    /// <param name="baseUri">The URI the dashboard printed.</param>
    /// <param name="extraHeaders">More request headers, or null.</param>
    /// <param name="cancellationToken">Ends the request.</param>
    /// <returns>The open stream.</returns>
    public async Task<HeadlessSseConnection> OpenEventsAsync(Uri baseUri, IEnumerable<KeyValuePair<string, string>>? extraHeaders = null, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Request(baseUri, EventsPath, hostHeader: null, extraHeaders);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new HeadlessSseConnection(response, body);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Reads the events of <c>/events</c> as they arrive, until the server ends the stream or <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <param name="baseUri">The URI the dashboard printed.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    /// <returns>The events in the order they arrived.</returns>
    public async IAsyncEnumerable<SseReceipt> ReadEventsAsync(Uri baseUri, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        HeadlessSseConnection stream = await OpenEventsAsync(baseUri, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await foreach (SseReceipt receipt in stream.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return receipt;
            }
        }
    }

    /// <summary>The latency check of REQ-032 R1: every event's receipt is at most <paramref name="budget"/> after its <c>atUtc</c>.</summary>
    /// <param name="receipts">The events received.</param>
    /// <param name="budget">The budget (250 ms).</param>
    /// <returns>The verdict with the maximum.</returns>
    /// <exception cref="ArgumentException"><paramref name="receipts"/> is empty.</exception>
    public static LatencyResult CheckLatency(IReadOnlyCollection<SseReceipt> receipts, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        if (receipts.Count == 0)
        {
            throw new ArgumentException("No event was received, so no latency can be checked.", nameof(receipts));
        }

        double maximum = receipts.Max(static receipt => receipt.Latency.TotalMilliseconds);
        bool passed = maximum <= budget.TotalMilliseconds;
        string verdict = passed ? "within" : "exceeds";
        string message = string.Create(CultureInfo.InvariantCulture, $"{receipts.Count} events, maximum latency {maximum:F1} ms {verdict} the budget of {budget.TotalMilliseconds:F0} ms");
        return new LatencyResult(passed, receipts.Count, maximum, message);
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    internal static IReadOnlyDictionary<string, string> HeadersOf(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        return headers;
    }

    private static HttpRequestMessage Request(Uri baseUri, string path, string? hostHeader, IEnumerable<KeyValuePair<string, string>>? extraHeaders)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        if (hostHeader is not null)
        {
            request.Headers.Host = hostHeader;
        }

        foreach ((string name, string value) in extraHeaders ?? [])
        {
            _ = request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }
}