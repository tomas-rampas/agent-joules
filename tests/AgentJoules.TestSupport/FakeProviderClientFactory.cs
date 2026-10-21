using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.TestSupport;

/// <summary>One client a <see cref="FakeProviderClientFactory"/> handed out.</summary>
/// <param name="Tier">The tier it was asked for.</param>
/// <param name="ModelId">The model ID it was asked for.</param>
/// <param name="Client">The client that was returned.</param>
public sealed record CreatedClient(Tier Tier, string ModelId, IChatClient Client);

/// <summary>
/// An <see cref="IProviderClientFactory"/> for the agent factory tests (REQ-011): every <see cref="Create"/> returns a new client, by default a
/// <see cref="FakeChatClient"/> on the given fake clock, and records which tier and model it was asked for, so a test can tell which client sits at
/// the bottom of which agent. No request leaves the process.
/// </summary>
/// <param name="time">The clock the default fake clients move.</param>
/// <param name="make">Builds the client of a call, or null for a default <see cref="FakeChatClient"/>.</param>
public sealed class FakeProviderClientFactory(FakeTimeProvider time, Func<Tier, string, IChatClient>? make = null) : IProviderClientFactory
{
    private readonly Lock _gate = new();
    private readonly List<CreatedClient> _created = [];
    private readonly Func<Tier, string, IChatClient> _make = make ?? ((_, _) => new FakeChatClient(time));

    /// <summary>The clients handed out so far, in order of creation.</summary>
    public IReadOnlyList<CreatedClient> Created
    {
        get
        {
            lock (_gate)
            {
                return [.. _created];
            }
        }
    }

    /// <inheritdoc />
    public IChatClient Create(Tier tier, string modelId)
    {
        IChatClient client = _make(tier, modelId);
        lock (_gate)
        {
            _created.Add(new CreatedClient(tier, modelId, client));
        }

        return client;
    }
}