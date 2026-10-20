using AgentJoules.Core.Runs;

using Microsoft.Extensions.AI;

namespace AgentJoules.Metering;

/// <summary>Adds the metering decorator to a <see cref="ChatClientBuilder"/> pipeline.</summary>
public static class ChatClientBuilderMeteringExtensions
{
    /// <summary>Adds a <see cref="MeteredChatClient"/> that meters every call of the agent named by <paramref name="identity"/>.</summary>
    /// <param name="builder">The builder; the first decorator added is the outermost one, so a pipeline adds this one first.</param>
    /// <param name="identity">Who the readings are attributed to; fixed for the client, never ambient.</param>
    /// <param name="services">The shared meter, gate, sink and clock.</param>
    /// <returns>The same builder, so calls chain.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ChatClientBuilder UseMetering(this ChatClientBuilder builder, StepIdentity identity, MeteringServices services)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(services);

        return builder.Use(inner => new MeteredChatClient(inner, identity, services));
    }
}