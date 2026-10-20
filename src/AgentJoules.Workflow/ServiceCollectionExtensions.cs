using AgentJoules.Core.Runs;
using AgentJoules.Metering;

using Microsoft.Extensions.DependencyInjection;

namespace AgentJoules.Workflow;

/// <summary>Registrations of the services that the workflow of a run shares process-wide.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the one <see cref="IMeterGate"/> of the process, chosen by the run mode (REQ-013, design 3.3): a <see cref="SerializedMeterGate"/> for
    /// <see cref="RunMode.Serialized"/>, <see cref="NoOpMeterGate.Instance"/> for <see cref="RunMode.Concurrent"/>. Every <see cref="MeteredChatClient"/> of
    /// the run, including the one that wraps the judge, takes the gate from this registration, so the choice of the mode is the only difference between the two
    /// modes: the workflow topology is built the same way in both.
    /// </summary>
    /// <remarks>
    /// The serialized gate is created by a factory, so the container owns it and disposes it when the provider is disposed. Dispose the provider only
    /// after every metered call has finished: a waiter still queued at that point never completes (see <see cref="SerializedMeterGate"/>). The no-op gate is
    /// shared static state and is never disposed. The per-run <see cref="MeteringServices"/> record (its sink differs between the workflow and the judge) is
    /// not a registration: the orchestrator builds it around the gate resolved from here. Keyed registrations of <see cref="IMeterGate"/> do not count as
    /// a second gate. A test that needs another gate replaces this one through <c>CliHostOverrides.Services</c> with <c>RemoveAll&lt;IMeterGate&gt;()</c>
    /// followed by <c>AddSingleton</c>.
    /// </remarks>
    /// <param name="services">The collection to add the gate to.</param>
    /// <param name="mode">The run mode, <c>--mode</c> on the command line.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined <see cref="RunMode"/>.</exception>
    /// <exception cref="InvalidOperationException">An <see cref="IMeterGate"/> is already registered; there is one gate per process and a second registration would hide which mode the run uses.</exception>
    public static IServiceCollection AddAgentJoulesMeterGate(this IServiceCollection services, RunMode mode)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.Any(static descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IMeterGate))
            ? throw new InvalidOperationException("An IMeterGate is already registered. A run has one meter gate, chosen by --mode, and a second registration would leave it unclear which mode the run uses.")
            : mode switch
            {
                RunMode.Serialized => services.AddSingleton<IMeterGate>(static _ => new SerializedMeterGate()),
                RunMode.Concurrent => services.AddSingleton<IMeterGate>(NoOpMeterGate.Instance),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The run mode is not a defined value; use Serialized or Concurrent."),
            };
    }
}