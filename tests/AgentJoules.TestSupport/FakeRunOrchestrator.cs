using System.ClientModel;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Providers;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AgentJoules.TestSupport;

/// <summary>
/// A test-only <see cref="IRunOrchestrator"/> that stands where the measured run of AJ-056 stands (the default double of the AJ-052 to AJ-055 tests; AJ-056 tests opt in to the production
/// orchestrator with <see cref="RunCommandSetup.UseProductionOrchestrator"/>): it records the <see cref="RunPlan"/> the real <c>run</c> command built, then makes
/// exactly one chat request to the first selected tier through the real provider pipeline (<see cref="IProviderClientFactory"/> and the named <see cref="HttpClient"/> of the tier), so a
/// test measures that the command reached the orchestrator, what it handed over, and what a provider failure looks like at the command line. A provider failure is wrapped in a
/// <see cref="StepFailedException"/> of the <c>Planner</c> agent with the failure type, its status and the failure attached, as the real orchestrator does (exit code 5).
/// </summary>
public sealed class FakeRunOrchestrator
{
    /// <summary>The agent whose step the one request stands for.</summary>
    public const string Agent = "Planner";

    /// <summary>The user prompt of the one request.</summary>
    public const string Prompt = "ping";

    private int _runs;
    private RunPlan? _lastPlan;

    /// <summary>How many times the command called the orchestrator.</summary>
    public int Runs => Volatile.Read(ref _runs);

    /// <summary>The plan of the last call, or null before any call.</summary>
    public RunPlan? LastPlan => Volatile.Read(ref _lastPlan);

    /// <summary>
    /// Replaces the <see cref="IRunOrchestrator"/> registration of the host with this double: pass it as (part of) <c>CliHostOverrides.Services</c>, which runs after the
    /// composition root, so the production registration is removed first.
    /// </summary>
    /// <param name="services">The service collection of the host being built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.RemoveAll<IRunOrchestrator>();
        _ = services.AddSingleton<IRunOrchestrator>(provider => new Bound(
            this,
            provider.GetRequiredService<IProviderClientFactory>(),
            provider.GetRequiredService<IOptions<AgentJoulesOptions>>()));
    }

    private async Task<int> RunAsync(RunPlan plan, IProviderClientFactory clients, AgentJoulesOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _ = Interlocked.Increment(ref _runs);
        Volatile.Write(ref _lastPlan, plan);

        Tier tier = plan.Tiers[0];
        string modelId = options.Tiers[TierOptions.NameOf(tier)].ResolveModelId(Agent);
        try
        {
            using IChatClient client = clients.Create(tier, modelId);
            _ = await client.GetResponseAsync(Prompt, cancellationToken: cancellationToken);
        }
        catch (Exception failure) when (failure is not (OperationCanceledException or ConfigurationException or MissingKeyException))
        {
            throw new StepFailedException(Agent, failure.GetType().Name, StatusOf(failure), failure);
        }

        return 0;
    }

    // The status the real orchestrator reads from a failure: a provider status, an HTTP status, or the last status of exhausted retries.
    private static int? StatusOf(Exception failure) => failure switch
    {
        ClientResultException { Status: >= 100 and <= 599 } client => client.Status,
        HttpRequestException { StatusCode: { } status } when (int)status is >= 100 and <= 599 => (int)status,
        RetriesExhaustedException retries => retries.StatusCode,
        _ => null,
    };

    private sealed class Bound(FakeRunOrchestrator owner, IProviderClientFactory clients, IOptions<AgentJoulesOptions> options) : IRunOrchestrator
    {
        public Task<int> RunAsync(RunPlan plan, CancellationToken cancellationToken) => owner.RunAsync(plan, clients, options.Value, cancellationToken);
    }
}