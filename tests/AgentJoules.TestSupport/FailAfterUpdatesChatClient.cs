using System.Runtime.CompilerServices;

using Microsoft.Extensions.AI;

namespace AgentJoules.TestSupport;

/// <summary>
/// A decorator for the retry tests: a streaming call yields the first <c>updates</c> updates of the inner stream and then throws <c>failure</c>, so a failure
/// that the real endpoint could not produce on the wire (an HTTP 429 after the stream started, which carries a status that only exists before the body) can
/// be put after a real first update. A buffered call is passed through unchanged.
/// </summary>
/// <param name="inner">The client whose stream is cut.</param>
/// <param name="updates">How many updates are yielded before the failure; 0 fails before the first one.</param>
/// <param name="failure">The exception thrown after the updates; the same instance every time.</param>
public sealed class FailAfterUpdatesChatClient(IChatClient inner, int updates, Exception failure) : DelegatingChatClient(inner)
{
    private readonly int _updates = updates;
    private readonly Exception _failure = failure ?? throw new ArgumentNullException(nameof(failure));

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int yielded = 0;
        await foreach (ChatResponseUpdate update in InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (yielded == _updates)
            {
                throw _failure;
            }

            yielded++;
            yield return update;
        }

        throw _failure;
    }
}