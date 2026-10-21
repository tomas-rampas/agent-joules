namespace AgentJoules.TestSupport;

/// <summary>
/// The primary handler of every HTTP client under the CLI harness unless a test registers a fake (deny-by-default, design 3.6 and AJ-052): it sends nothing. Every
/// request is recorded, then refused with an <see cref="InvalidOperationException"/> that names the request URL, so a tier base URL that has no fake route fails
/// loudly in the test instead of reaching the network. The URL of a tier is configuration, never user input: the validator rejects user info, a query and a fragment.
/// </summary>
public sealed class DenyAllHttpMessageHandler : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<string> _urls = [];

    /// <summary>The number of requests this handler refused.</summary>
    public int Attempts => RequestedUrls.Count;

    /// <summary>The URL of every refused request, in order.</summary>
    public IReadOnlyList<string> RequestedUrls
    {
        get
        {
            lock (_gate)
            {
                return [.. _urls];
            }
        }
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(Deny(request));

    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => throw Deny(request);

    private InvalidOperationException Deny(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string url = request.RequestUri?.ToString() ?? "(no request URI)";
        lock (_gate)
        {
            _urls.Add(url);
        }

        return new InvalidOperationException("The test harness denies every real HTTP request and no fake route handles " + url);
    }
}