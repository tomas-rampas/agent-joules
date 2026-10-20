using System.Runtime.CompilerServices;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.TestSupport;

/// <summary>One scripted streaming update of a <see cref="FakeChatClient"/>.</summary>
/// <param name="Text">The text of the update; an empty text gives an update without a <see cref="TextContent"/>.</param>
/// <param name="Delay">How far the fake clock moves before the update is yielded, so a consumer that reads the clock when the update arrives sees exactly this delay.</param>
/// <param name="Usage">The usage the update carries as a <see cref="UsageContent"/>, or null.</param>
/// <param name="Reasoning">True when the text is reasoning text (a <see cref="TextReasoningContent"/>) and not answer text.</param>
public sealed record FakeUpdate(string Text, TimeSpan Delay = default, UsageDetails? Usage = null, bool Reasoning = false);

/// <summary>One call a <see cref="FakeChatClient"/> received.</summary>
/// <param name="Messages">The messages of the call, copied.</param>
/// <param name="Options">The options of the call.</param>
/// <param name="Streaming">True for a streaming call, false for a buffered one.</param>
public sealed record FakeChatCall(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options, bool Streaming);

/// <summary>
/// A scripted <see cref="IChatClient"/> for the metering tests (design 6.1). A buffered call answers <see cref="ResponseText"/> after
/// <see cref="ResponseDelay"/>; a streaming call yields <see cref="Updates"/>, each after its own delay. Every delay is applied by moving the
/// <see cref="FakeTimeProvider"/> forward before the answer or the update is handed back, so a time measured around the call is exact by
/// construction and no test waits on the wall clock. A scripted failure is thrown after a chosen number of updates. The cancellation token is
/// honoured between updates. Every call is recorded.
/// </summary>
public sealed class FakeChatClient(FakeTimeProvider time) : IChatClient
{
    private readonly Lock _gate = new();
    private readonly List<FakeChatCall> _calls = [];

    /// <summary>The text a buffered call answers.</summary>
    public string ResponseText { get; init; } = "answer";

    /// <summary>The usage a buffered call answers, or null.</summary>
    public UsageDetails? ResponseUsage { get; init; }

    /// <summary>How far the clock moves before a buffered call answers (after <see cref="Hold"/> is released, when one is set).</summary>
    public TimeSpan ResponseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A buffered call waits for this task, when it is set, before it moves the clock and answers; a test completes it to end a call that is in flight.</summary>
    public Task? Hold { get; init; }

    /// <summary>The updates a streaming call yields, in order.</summary>
    public IReadOnlyList<FakeUpdate> Updates { get; init; } = [];

    /// <summary>The failure thrown by a buffered call (instead of answering) and by a streaming call once <see cref="FailAfterUpdates"/> updates were yielded; null for no failure.</summary>
    public Exception? Failure { get; init; }

    /// <summary>How far the clock moves before a streaming call throws <see cref="Failure"/>, so the window of a call that fails at once still has a length.</summary>
    public TimeSpan FailureDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The number of updates a streaming call yields before it throws <see cref="Failure"/>; null means the failure is not thrown by a streaming call.</summary>
    public int? FailAfterUpdates { get; init; }

    /// <summary>Every call received so far, in order.</summary>
    public IReadOnlyList<FakeChatCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_calls.ToArray());
            }
        }
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(messages, options, streaming: false);
        cancellationToken.ThrowIfCancellationRequested();
        if (Hold is not null)
        {
            await Hold.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ResponseDelay > TimeSpan.Zero)
        {
            time.Advance(ResponseDelay);
        }

        return Failure is null
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText)) { Usage = ResponseUsage }
            : throw Failure;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Record(messages, options, streaming: true);
        for (int i = 0; i < Updates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null && FailAfterUpdates == i)
            {
                time.Advance(FailureDelay);
                throw Failure;
            }

            FakeUpdate scripted = Updates[i];
            if (scripted.Delay > TimeSpan.Zero)
            {
                time.Advance(scripted.Delay);
            }

            yield return ToUpdate(scripted);

            // Hands control back like a network read does, so a consumer sees the update before the next one is produced.
            await Task.Yield();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null && FailAfterUpdates is { } after && after >= Updates.Count)
        {
            time.Advance(FailureDelay);
            throw Failure;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static ChatResponseUpdate ToUpdate(FakeUpdate scripted)
    {
        ChatResponseUpdate update = new() { Role = ChatRole.Assistant };
        if (scripted.Text.Length > 0)
        {
            update.Contents.Add(scripted.Reasoning ? new TextReasoningContent(scripted.Text) : new TextContent(scripted.Text));
        }

        if (scripted.Usage is not null)
        {
            update.Contents.Add(new UsageContent(scripted.Usage));
        }

        return update;
    }

    private void Record(IEnumerable<ChatMessage> messages, ChatOptions? options, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(messages);

        lock (_gate)
        {
            _calls.Add(new FakeChatCall([.. messages], options, streaming));
        }
    }
}