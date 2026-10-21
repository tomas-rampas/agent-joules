using System.Net.Sockets;

namespace AgentJoules.TestSupport;

/// <summary>
/// An endpoint that refuses the connection and, as a careless server or proxy would, echoes the credential it saw in the failure text: it records the request, then throws an
/// <see cref="HttpRequestException"/> (with a <see cref="SocketException"/> inside) whose message holds the bearer token of the request. A test that points a tier at it proves two
/// things at once: the client really sent the key, and no key text survives the redacting writers when the failure is reported.
/// </summary>
public sealed class CredentialEchoingRefusalHandler : HttpMessageHandler
{
    private int _attempts;

    /// <summary>The number of requests the endpoint refused.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>The bearer token of the last request, or null when none carried one; kept so a test can prove the key was sent.</summary>
    public string? LastBearerToken { get; private set; }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = Interlocked.Increment(ref _attempts);
        string? token = request.Headers.Authorization?.Parameter;
        LastBearerToken = token;

        return Task.FromException<HttpResponseMessage>(new HttpRequestException(
            "Connection refused; the endpoint saw Authorization: Bearer " + token,
            new SocketException((int)SocketError.ConnectionRefused)));
    }
}