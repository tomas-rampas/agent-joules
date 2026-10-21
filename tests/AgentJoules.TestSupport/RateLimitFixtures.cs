using System.ClientModel;

using AgentJoules.Core.Runs;

using Microsoft.Extensions.AI;

namespace AgentJoules.TestSupport;

/// <summary>Builders for the rate-limit tests.</summary>
public static class RateLimitFixtures
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The exception the real OpenAI SDK raises for an HTTP 429: a real call against a throwaway <see cref="FakeHttpMessageHandler"/> that answers 429, so
    /// the status, the raw response and its headers are the SDK's own and nothing is hand-built.
    /// </summary>
    /// <param name="retryAfter">The raw <c>Retry-After</c> value of the answer, or null for none.</param>
    /// <param name="customize">A hook that edits the 429 response before the SDK reads it (a body that echoes a credential, for example), or null.</param>
    /// <returns>The exception, caught from the SDK.</returns>
    /// <exception cref="InvalidOperationException">The call did not fail with a <see cref="ClientResultException"/>.</exception>
    public static async Task<ClientResultException> CaptureRateLimitAsync(string? retryAfter = null, Action<FakeRoute, HttpResponseMessage>? customize = null)
    {
        var handler = new FakeHttpMessageHandler { CustomizeResponse = customize };
        handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests(retryAfter));
        using ProviderClientHost host = ProviderClientFixtures.Build(
            ProviderClientFixtures.FourTiers(),
            handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = handler });
        using IChatClient client = host.Factory.Create(Tier.Nvidia, "model-nvidia");

        try
        {
            _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]).WaitAsync(HangGuard).ConfigureAwait(false);
        }
        catch (ClientResultException captured)
        {
            return captured;
        }

        throw new InvalidOperationException("The call did not fail with a ClientResultException.");
    }
}