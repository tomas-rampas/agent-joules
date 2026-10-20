using AgentJoules.Core.Energy;
using AgentJoules.Core.Energy.Testing;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-041 scenario (metered buffered and streaming calls), handed to the step class by Reqnroll context injection. It owns the
/// fake clock, the deterministic meter, the sink and the client under test; nothing in it waits on the wall clock, because the fake chat client
/// moves the fake clock itself.
/// </summary>
public sealed class MeteredCallsContext : IDisposable
{
    private DeterministicEnergyMeter? _meter;

    /// <summary>The scenario clock; the fake chat client advances it.</summary>
    public FakeTimeProvider Time { get; } = new();

    /// <summary>The sink that receives the readings of the scenario.</summary>
    public InMemoryMetricsSink Sink { get; } = new();

    /// <summary>The fake inner client of the scenario.</summary>
    public FakeChatClient? Inner { get; set; }

    /// <summary>The client under test, built directly or through the builder.</summary>
    public IChatClient? Client { get; set; }

    /// <summary>The cancellation source of a scenario in which the caller cancels.</summary>
    public CancellationTokenSource? Cancellation { get; set; }

    /// <summary>The exception a consuming step caught, or null.</summary>
    public Exception? Thrown { get; set; }

    /// <summary>The call that is held in flight by a scenario of the overlap guard.</summary>
    public Task<ChatResponse>? FirstCall { get; set; }

    /// <summary>Completes the hold of the call in flight, or null when the scenario holds no call.</summary>
    public TaskCompletionSource? Release { get; set; }

    /// <summary>The identity of the agent a scenario meters, on the local tier.</summary>
    /// <param name="agent">The agent name.</param>
    /// <returns>The identity.</returns>
    public static StepIdentity IdentityOf(string agent) => new(agent, "model-a", Tier.Local, "summarise", 1, IsJudge: false);

    /// <summary>Creates the services the client under test uses: the deterministic meter, a gate that admits everyone, the scenario sink and the fake clock.</summary>
    /// <returns>The started services.</returns>
    /// <exception cref="InvalidOperationException">The services were already created; a scenario has one meter.</exception>
    public async Task<MeteringServices> CreateServicesAsync()
    {
        if (_meter is not null)
        {
            throw new InvalidOperationException("The services of this scenario were already created.");
        }

        _meter = new DeterministicEnergyMeter(Time, static _ => 100.0, sampleRateHz: 10.0, counterSupported: true, MeterMethodSetting.Auto);
        await _meter.StartAsync(CancellationToken.None);
        return new MeteringServices(_meter, NoOpMeterGate.Instance, Sink, Time);
    }

    /// <summary>The readings published so far.</summary>
    /// <returns>A copy of the list the sink holds.</returns>
    public IReadOnlyList<StepReading> Readings() => Sink.Snapshot();

    /// <inheritdoc />
    public void Dispose()
    {
        Release?.TrySetResult();
        Cancellation?.Dispose();
        _meter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}