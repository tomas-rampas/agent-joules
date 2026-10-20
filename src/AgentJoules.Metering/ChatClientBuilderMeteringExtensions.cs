using AgentJoules.Core.Runs;

using Microsoft.Extensions.AI;

namespace AgentJoules.Metering;

/// <summary>Adds the metering decorator to a <see cref="ChatClientBuilder"/> pipeline.</summary>
public static class ChatClientBuilderMeteringExtensions
{
    /// <summary>Adds a <see cref="MeteredChatClient"/> that meters every call of the agent named by <paramref name="identity"/>; it adopts the ledger of a <see cref="RateLimitRetryChatClient"/> below it, or has one of its own (0 retries) when there is none.</summary>
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

    /// <summary>
    /// Adds a <see cref="MeteredChatClient"/> that reads the retries of its calls from <paramref name="ledger"/>: the pipeline of an agent with a
    /// <see cref="RateLimitRetryChatClient"/> adds this one first and the retry layer after it, over the same ledger.
    /// </summary>
    /// <param name="builder">The builder; the first decorator added is the outermost one.</param>
    /// <param name="identity">Who the readings are attributed to; fixed for the client, never ambient.</param>
    /// <param name="services">The shared meter, gate, sink, cost calculator and clock.</param>
    /// <param name="ledger">The ledger the retry layer of the same pipeline writes; it must be the instance the retry layer holds, or building the pipeline throws <see cref="ArgumentException"/>.</param>
    /// <returns>The same builder, so calls chain.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ChatClientBuilder UseMetering(this ChatClientBuilder builder, StepIdentity identity, MeteringServices services, RetryLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ledger);

        return builder.Use(inner => new MeteredChatClient(inner, identity, services, ledger));
    }
}