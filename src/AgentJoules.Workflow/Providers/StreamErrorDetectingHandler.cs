using System.Net;
using System.Text.Json;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Security;

namespace AgentJoules.Workflow.Providers;

/// <summary>
/// The HTTP handler that turns an <c>error</c> chunk inside a successful event stream into a failure (user decision 3 of the M3 exit, AJ-104, EDGE-006). The saturated free-tier worker of the live
/// runs answered a streamed completion with HTTP 200 and then sent <c>data: {"error":{"message":"ResourceExhausted: ...","type":"internal_server_error","code":500}}</c> and <c>data: [DONE]</c>;
/// the SDK reads only <c>choices</c>, so the step ended <c>ok</c> with no output. It acts only on a <c>POST</c> whose path ends in <c>/chat/completions</c> and whose successful response is
/// <c>text/event-stream</c>; every other response passes through untouched.
/// </summary>
/// <remarks>
/// <para>
/// The detection is in the transport, not on <c>ChatResponseUpdate.RawRepresentation</c>. Measured: the SDK does keep the unknown <c>error</c> member and an update re-serialized with
/// <c>ModelReaderWriter.Write</c> shows it, but the error then arrives as a normal first update (no choices, no text), so the retry layer, which retries only a failure that comes out of the
/// first read, would have taken the call as started and could not retry it, and every update would pay for a re-serialization to find the one that is an error. Here the failure is thrown before
/// the SDK builds an update, from the first read. The response content is wrapped, not buffered: the stream is read as the consumer reads it, each complete line is
/// looked at, and the bytes pass through unchanged. A line is an error when it is a <c>data:</c> line whose payload is a JSON object with a top-level <c>error</c> member (an object or a string)
/// and no <c>choices</c>; a <c>[DONE]</c>, a comment, a payload that is not JSON and a chunk that has <c>choices</c> are never errors. A chunk that holds an error ends the stream with a
/// <see cref="ProviderErrorException"/> at the next read; the bytes of the lines before it have been handed to the reader first, so the consumer sees the updates that came before the error.
/// </para>
/// <para>
/// The message of the provider can echo the credential: it is masked by the <see cref="SecretRedactor"/>, cleaned and cut by the exception itself, and the line buffer is bounded
/// (<see cref="MaxLineBytes"/>), so a hostile stream cannot make the handler hold an unbounded line. Nothing is logged here. Both <c>Send</c> and <c>SendAsync</c> are overridden, so a
/// synchronous call cannot bypass the detection.
/// </para>
/// </remarks>
/// <param name="redactor">The redactor that masks key values in the text of a provider error.</param>
public sealed class StreamErrorDetectingHandler(SecretRedactor redactor) : DelegatingHandler
{
    /// <summary>The longest line the handler looks into, in bytes; a longer line is passed through without being inspected.</summary>
    public const int MaxLineBytes = 256 * 1024;

    private const string ChatCompletionsSuffix = "/chat/completions";
    private const string EventStreamMediaType = "text/event-stream";
    private const string UnreadableMessage = "(message not readable)";

    private readonly SecretRedactor _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return Watch(request, response);
    }

    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Watch(request, base.Send(request, cancellationToken));
    }

    private HttpResponseMessage Watch(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (request.Method == HttpMethod.Post
            && request.RequestUri is { } uri
            && uri.AbsolutePath.EndsWith(ChatCompletionsSuffix, StringComparison.Ordinal)
            && response.IsSuccessStatusCode
            && response.Content is { } content
            && string.Equals(content.Headers.ContentType?.MediaType, EventStreamMediaType, StringComparison.OrdinalIgnoreCase))
        {
            response.Content = new ScanningContent(content, (int)response.StatusCode, _redactor);
        }

        return response;
    }

    // The original content behind a stream that scans what it hands out. The headers are the original ones except the length, which the wrapper does not state.
    private sealed class ScanningContent : HttpContent
    {
        private readonly HttpContent _original;
        private readonly int _status;
        private readonly SecretRedactor _redactor;

        public ScanningContent(HttpContent original, int status, SecretRedactor redactor)
        {
            _original = original;
            _status = status;
            _redactor = redactor;
            foreach (KeyValuePair<string, IEnumerable<string>> header in original.Headers)
            {
                if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
            new ScanningStream(_original.ReadAsStream(cancellationToken), _status, _redactor);

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new ScanningStream(await _original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _status, _redactor);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Stream source = await CreateContentReadStreamAsync().ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                await source.CopyToAsync(stream).ConfigureAwait(false);
            }
        }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using Stream source = CreateContentReadStream(cancellationToken);
            source.CopyTo(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _original.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // A read-only stream over the response body that looks at each complete line as it passes. Bytes are never changed, delayed or dropped, except that the bytes of an error line and the rest
    // of the read that held it are not handed on: the next read throws the error instead.
    private sealed class ScanningStream(Stream inner, int status, SecretRedactor redactor) : Stream
    {
        private readonly LineScanner _scanner = new(status, redactor);
        private ProviderErrorException? _failure;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfFailed();
            return Scan(buffer, inner.Read(buffer));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfFailed();
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Scan(buffer.Span, read);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        // The error is thrown once, on the read after the one that held it; a read after that throws it again, so a consumer that retries the read does not see a clean end.
        private void ThrowIfFailed()
        {
            if (_failure is { } failure)
            {
                throw failure;
            }
        }

        private int Scan(Span<byte> buffer, int read)
        {
            // A read into an empty buffer returns 0 whatever the stream holds; it is not the end of the stream and must not be taken for it.
            if (buffer.IsEmpty)
            {
                return read;
            }

            if (read == 0)
            {
                // The end of the stream: a last line without a terminator is a line too.
                if (_scanner.Finish() is { } late)
                {
                    _failure = late;
                    ThrowIfFailed();
                }

                return 0;
            }

            ProviderErrorException? error = _scanner.Feed(buffer[..read], out int handedOn);
            if (error is null)
            {
                return read;
            }

            _failure = error;

            // Nothing precedes the error line in this read: throw now. Otherwise hand on the bytes that precede it and throw on the next read.
            if (handedOn == 0)
            {
                ThrowIfFailed();
            }

            return handedOn;
        }
    }

    // Splits the bytes of the stream into lines and decides, per line, whether it is the data line of an error. The only state is the line in progress.
    private sealed class LineScanner(int status, SecretRedactor redactor)
    {
        private static ReadOnlySpan<byte> DataPrefix => "data:"u8;

        private static ReadOnlySpan<byte> ErrorKey => "\"error\""u8;

        private readonly int _status = status;
        private readonly SecretRedactor _redactor = redactor;
        private byte[] _line = new byte[256];
        private int _length;
        private bool _overflow;

        // True while a line that began in an earlier read is not finished.
        private bool _carried;

        // Looks at the complete lines of `bytes`. Returns the error of the first error line and, in `handedOn`, how many leading bytes precede that line in this read (0 when the line began in
        // an earlier read); returns null when no line is an error, and then handedOn is the whole length.
        public ProviderErrorException? Feed(ReadOnlySpan<byte> bytes, out int handedOn)
        {
            int position = 0;
            while (position < bytes.Length)
            {
                int newline = bytes[position..].IndexOf((byte)'\n');
                if (newline < 0)
                {
                    Append(bytes[position..]);
                    _carried = true;
                    break;
                }

                int lineStart = _carried ? 0 : position;
                Append(bytes.Slice(position, newline));
                _carried = false;
                if (Inspect() is { } error)
                {
                    handedOn = lineStart;
                    return error;
                }

                position += newline + 1;
            }

            handedOn = bytes.Length;
            return null;
        }

        // The end of the stream: the line in progress, if any, is complete.
        public ProviderErrorException? Finish()
        {
            if (_length == 0 && !_overflow)
            {
                return null;
            }

            _carried = false;
            return Inspect();
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            if (_overflow || bytes.IsEmpty)
            {
                return;
            }

            if (_length + bytes.Length > MaxLineBytes)
            {
                _overflow = true;
                _length = 0;
                return;
            }

            if (_length + bytes.Length > _line.Length)
            {
                Array.Resize(ref _line, Math.Min(MaxLineBytes, Math.Max(_line.Length * 2, _length + bytes.Length)));
            }

            bytes.CopyTo(_line.AsSpan(_length));
            _length += bytes.Length;
        }

        // Decides the line that was just completed and resets the buffer. A line that is not a data line, is too long, is not JSON or is not an error object is not an error.
        private ProviderErrorException? Inspect()
        {
            bool overflow = _overflow;
            ReadOnlySpan<byte> line = _line.AsSpan(0, _length);
            _length = 0;
            _overflow = false;
            if (overflow)
            {
                return null;
            }

            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (!line.StartsWith(DataPrefix))
            {
                return null;
            }

            ReadOnlySpan<byte> payload = line[DataPrefix.Length..];
            if (!payload.IsEmpty && payload[0] == (byte)' ')
            {
                payload = payload[1..];
            }

            // The cheap tests first: nearly every payload of a normal stream is a chunk with choices and is never parsed here.
            return payload.IsEmpty || payload[0] != (byte)'{' || payload.IndexOf(ErrorKey) < 0 ? null : ParseError(payload);
        }

        private ProviderErrorException? ParseError(ReadOnlySpan<byte> payload)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(payload.ToArray());
                JsonElement root = document.RootElement;
                return root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out JsonElement error) || root.TryGetProperty("choices", out _)
                    ? null
                    : error.ValueKind switch
                    {
                        JsonValueKind.String => new ProviderErrorException(_status, null, null, error.GetString(), _redactor),
                        JsonValueKind.Object => new ProviderErrorException(_status, CodeOf(error), TextOf(error, "type"), TextOf(error, "message"), _redactor),
                        _ => null,
                    };
            }
            catch (JsonException)
            {
                // Not JSON (a payload that only looks like it): it is not an error object, and its text is not kept.
                return null;
            }
            catch (InvalidOperationException)
            {
                // An error object whose text cannot be read (invalid UTF-8, a lone surrogate): it is still an error, and the text is replaced by a fixed one.
                return new ProviderErrorException(_status, null, null, UnreadableMessage, _redactor);
            }
        }

        private static string? TextOf(JsonElement error, string name) =>
            error.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // error.code as a number, or as a string of digits (some servers send "500"); anything else is no code.
        private static int? CodeOf(JsonElement error) =>
            !error.TryGetProperty("code", out JsonElement code)
                ? null
                : code.ValueKind switch
                {
                    JsonValueKind.Number when code.TryGetInt32(out int number) => number,
                    JsonValueKind.String when int.TryParse(code.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed) => parsed,
                    _ => null,
                };
    }
}