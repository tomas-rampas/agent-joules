using System.ClientModel;
using System.ClientModel.Primitives;

namespace AgentJoules.TestSupport;

/// <summary>
/// Builds the <see cref="ClientResultException"/> the OpenAI SDK throws for a rejected call, with a status and no network: the judge and retry tests need an exception of that type carrying a given HTTP status
/// at a place where the real client would have retried it away (a 503 behind the retry layer).
/// </summary>
public static class ClientFailures
{
    /// <summary>The exception of a call that the endpoint answered with <paramref name="status"/>.</summary>
    /// <param name="status">The HTTP status, from 100 to 599.</param>
    /// <param name="message">The message of the exception; the SDK writes a status line there (and not the body, measured), so a test can put text in it.</param>
    /// <param name="body">The body of the raw response, as the SDK keeps it when it buffers an error answer, or null for none.</param>
    /// <returns>The exception; <see cref="ClientResultException.Status"/> is <paramref name="status"/>.</returns>
    public static ClientResultException Status(int status, string message = "Service request failed.", string? body = null) => new(message, new FixedResponse(status, body));

    private sealed class FixedResponse(int status, string? body) : PipelineResponse
    {
        public override int Status => status;

        public override string ReasonPhrase => "fake";

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => body is null ? BinaryData.Empty : BinaryData.FromString(body);

        protected override PipelineResponseHeaders HeadersCore { get; } = new EmptyHeaders();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(BinaryData.Empty);

        public override void Dispose()
        {
        }
    }

    private sealed class EmptyHeaders : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string? value)
        {
            value = null;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, string>>)[]).GetEnumerator();
    }
}