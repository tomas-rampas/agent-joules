using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-131 link scenario (the prior-art citations, REQ-025 and REQ-032), handed to the step class by Reqnroll context injection. The scenario about live links follows the AJ-111 pattern:
/// <see cref="Open"/> evaluates <see cref="IntegrationGate.SkipReason"/> for AGENTJOULES_INTEGRATION; with the gate closed the remaining steps record <see cref="Note"/> and pass, and with it open the links of the
/// committed README are requested with real HTTP GET requests and the build log is read (the line is written by the builder after the check, this test never writes it). The scenario about a dead link is hermetic:
/// the handler is a fake that answers 404, so no request leaves the process.
/// </summary>
public sealed class PriorArtLinksContext : IDisposable
{
    private readonly List<HttpClient> _clients = [];

    /// <summary>The addresses to request.</summary>
    public IReadOnlyList<string> Urls { get; set; } = [];

    /// <summary>True when the gate is open and the scenario asserts the live links.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Why a gated scenario passed without asserting, or null.</summary>
    public string? Note { get; private set; }

    /// <summary>The result of the link check, or null before it ran.</summary>
    public LinkCheckResult? Result { get; set; }

    /// <summary>Evaluates the gate.</summary>
    public void Open()
    {
        Note = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        IsActive = Note is null;
    }

    /// <summary>A client over the real handler, for the open gate only.</summary>
    /// <returns>The client, disposed with the context.</returns>
    public HttpClient RealClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("agent-joules-link-check");
        _clients.Add(client);

        return client;
    }

    /// <summary>A client whose handler answers every request with the given status and never touches the network.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <returns>The client, disposed with the context.</returns>
    public HttpClient FakeClient(System.Net.HttpStatusCode status)
    {
        var client = new HttpClient(new StatusHandler(status));
        _clients.Add(client);

        return client;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
    }
}