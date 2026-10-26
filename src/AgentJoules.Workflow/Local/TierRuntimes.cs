using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.Options;

namespace AgentJoules.Workflow.Local;

/// <summary>
/// The production <see cref="ITierRuntimes"/>: the local tier is served by the runtime <c>Local:Runtime</c> selects (the one the preflight and the calibration use), the cloudgpu tier by a NIM
/// runtime bound to the cloudgpu tier (its base URL, its request factory so a configured key travels with the request, its <c>TimeoutSeconds</c> and its <c>Precision</c>), a hosted tier by none.
/// </summary>
/// <param name="local">The runtime of the local tier.</param>
/// <param name="factory">The provider client factory the cloudgpu runtime asks through.</param>
/// <param name="httpClients">The factory of the named <see cref="HttpClient"/> of each tier.</param>
/// <param name="options">The options, for the configured precision of the cloudgpu tier.</param>
/// <param name="time">The clock that measures the cloudgpu warm-up call; the system clock when null.</param>
public sealed class TierRuntimes(ILocalRuntime local, ProviderClientFactory factory, IHttpClientFactory httpClients, IOptions<AgentJoulesOptions> options, TimeProvider? time = null) : ITierRuntimes
{
    private readonly ILocalRuntime _local = local ?? throw new ArgumentNullException(nameof(local));
    private readonly Lazy<NimRuntime> _cloudGpu = new(() => new NimRuntime(factory, httpClients, options, time, Tier.CloudGpu));

    /// <inheritdoc />
    public ILocalRuntime? RuntimeOf(Tier tier) => tier switch
    {
        Tier.Local => _local,
        Tier.CloudGpu => _cloudGpu.Value,
        _ => null,
    };
}