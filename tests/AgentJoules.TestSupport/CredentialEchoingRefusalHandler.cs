using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentJoules.TestSupport;

/// <summary>
/// An endpoint that refuses the connection and, as a careless server or proxy would, echoes the credential it saw in the failure text: it records the request, then throws an
/// <see cref="HttpRequestException"/> (with a <see cref="SocketException"/> inside) whose message holds the bearer token of the request. A test that points a tier at it proves two
/// things at once: the client really sent the key, and no key text survives the redacting writers when the failure is reported.
/// </summary>
/// <remarks>
/// By default every request is refused, so a command that asks the models route first (the preflight, AJ-053) never reaches the completion request. With a model list
/// (the constructor with <c>modelIds</c>), <c>GET .../models</c> is answered with that list and only the other requests are refused, so the
/// completion path, and the echo in its failure, is exercised too.
/// </remarks>
/// <param name="modelIds">The model IDs the models route lists; null refuses the models route too.</param>
public sealed class CredentialEchoingRefusalHandler(IReadOnlyList<string>? modelIds) : HttpMessageHandler
{
    private int _attempts;
    private int _modelsRequests;

    /// <summary>Creates the handler that refuses every request.</summary>
    public CredentialEchoingRefusalHandler()
        : this(null)
    {
    }

    /// <summary>The number of requests the endpoint refused.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>The number of <c>GET .../models</c> requests the endpoint answered with its model list; always 0 without a model list.</summary>
    public int ModelsRequests => Volatile.Read(ref _modelsRequests);

    /// <summary>The bearer token of the last request, or null when none carried one; kept so a test can prove the key was sent.</summary>
    public string? LastBearerToken { get; private set; }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? token = request.Headers.Authorization?.Parameter;
        LastBearerToken = token;

        if (modelIds is not null && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
        {
            _ = Interlocked.Increment(ref _modelsRequests);
            var data = new JsonArray();
            foreach (string id in modelIds)
            {
                data.Add(new JsonObject { ["id"] = id, ["object"] = "model" });
            }

            string body = new JsonObject { ["object"] = "list", ["data"] = data }.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        _ = Interlocked.Increment(ref _attempts);
        return Task.FromException<HttpResponseMessage>(new HttpRequestException(
            "Connection refused; the endpoint saw Authorization: Bearer " + token,
            new SocketException((int)SocketError.ConnectionRefused)));
    }
}