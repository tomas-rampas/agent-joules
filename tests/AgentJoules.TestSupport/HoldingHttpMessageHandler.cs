namespace AgentJoules.TestSupport;

/// <summary>
/// A primary handler in front of a fake endpoint that holds one request of a route until the request's token is cancelled (AJ-056, the shutdown scenario): when the request with the
/// given number on the route arrives, <see cref="Reached"/> completes and the request waits on its token, so a test can raise a shutdown while that call is in flight; the held request
/// never reaches the fake, which records only the requests it answered. Every other request goes to the fake unchanged. The wait ends when the request token is cancelled, or after
/// <see cref="MaxHold"/> with a <see cref="TimeoutException"/> naming this handler.
/// </summary>
/// <param name="inner">The fake endpoint that answers every request that is not held.</param>
/// <param name="route">The route whose request is held.</param>
/// <param name="number">The number of the held request on that route, from 1.</param>
public sealed class HoldingHttpMessageHandler(FakeHttpMessageHandler inner, FakeRoute route, int number) : DelegatingHandler(inner)
{
    /// <summary>The longest a request is held (a real wait, ended early by the request token).</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(60);

    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _seen;

    /// <summary>The fake endpoint behind the hold.</summary>
    public FakeHttpMessageHandler Inner { get; } = inner;

    /// <summary>Completes when the held request arrived and started to wait.</summary>
    public Task Reached => _reached.Task;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool matches = route switch
        {
            FakeRoute.Models => request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/models", StringComparison.Ordinal) == true,
            FakeRoute.ChatCompletions => request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true,
            FakeRoute.OllamaGenerate => request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/api/generate", StringComparison.Ordinal) == true,
            _ => false,
        };

        if (matches && Interlocked.Increment(ref _seen) == number)
        {
            _ = _reached.TrySetResult();

            // Bounded, so a test that never cancels fails here instead of holding the harness gate for the rest of the assembly.
            await Task.Delay(MaxHold, cancellationToken).ConfigureAwait(false);
            throw new TimeoutException($"{nameof(HoldingHttpMessageHandler)} held request {number} of {route} for {MaxHold.TotalSeconds} s and no cancellation came.");
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}