using Microsoft.Extensions.Http;

namespace AgentJoules.TestSupport;

/// <summary>
/// Makes the harness deny-by-default for HTTP: after every other registration has configured a named client, a primary handler that would reach the network
/// (<see cref="SocketsHttpHandler"/> or <see cref="HttpClientHandler"/>, which is what the production registration and an unconfigured client use) is replaced by the
/// <see cref="DenyAllHttpMessageHandler"/> of the run, after disposing the replaced handler (nothing was sent through it). A test fake is another handler type and stays. Running after the configured actions is what lets a fake win without
/// the harness knowing the client names or the order of the registrations.
/// </summary>
/// <param name="deny">The handler of the run.</param>
internal sealed class DenyRealHandlersFilter(DenyAllHttpMessageHandler deny) : IHttpMessageHandlerBuilderFilter
{
    /// <inheritdoc />
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return builder =>
        {
            next(builder);
            if (builder.PrimaryHandler is HttpClientHandler or SocketsHttpHandler)
            {
                builder.PrimaryHandler.Dispose();
                builder.PrimaryHandler = deny;
            }
        };
    }
}