using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-045 scenario (the 429 retry), handed to the step class by Reqnroll context injection. It owns the fake clock that moves only when
/// the code under test waits, the fake endpoint of the nvidia tier, and the pipeline of an agent built over them like the agent factory builds it: the
/// metered client outside, the retry layer inside it, one ledger shared by the two, and the real OpenAI SDK at the bottom. Nothing in it waits on the wall
/// clock and no request leaves the process.
/// </summary>
public sealed class RateLimitRetryContext : IDisposable
{
    private const int Seed = 12345;

    private ProviderClientHost? _host;
    private UnmeteredEnergyMeter? _meter;
    private MeteredChatClient? _client;

    /// <summary>The scenario clock; the endpoint advances it for the duration of a call and the driver advances it for every wait.</summary>
    public TimerTrackingTimeProvider Time { get; } = new();

    /// <summary>The sink that receives the readings of the scenario.</summary>
    public InMemoryMetricsSink Sink { get; } = new();

    /// <summary>The fake endpoint of the scenario, set by a Given step.</summary>
    public FakeHttpMessageHandler? Endpoint { get; set; }

    /// <summary>Wraps the provider client before the retry layer sees it, or null; the scenario that needs a failure the wire cannot produce uses it.</summary>
    public Func<IChatClient, IChatClient>? Decorate { get; set; }

    /// <summary>The exception the call or the stream ended with, or null.</summary>
    public Exception? Thrown { get; set; }

    /// <summary>The identity of the agent a scenario meters, on the nvidia tier.</summary>
    public static StepIdentity Identity { get; } = new("Planner", "model-nvidia", Tier.Nvidia, "summarise", 1, IsJudge: false);

    /// <summary>Creates the endpoint of the scenario on the scenario clock; every request that has no scripted duration takes 10 ms of fake time.</summary>
    /// <returns>The new endpoint, also stored in <see cref="Endpoint"/>.</returns>
    public FakeHttpMessageHandler NewEndpoint()
    {
        Endpoint = new FakeHttpMessageHandler { Clock = Time, DefaultDuration = TimeSpan.FromMilliseconds(10) };
        return Endpoint;
    }

    /// <summary>The requests the endpoint recorded for the chat route.</summary>
    public int ChatRequests => (Endpoint ?? throw new InvalidOperationException("No endpoint was scripted.")).Count(FakeRoute.ChatCompletions);

    /// <summary>The client under test, built on first use so that every Given step can still script the endpoint.</summary>
    /// <returns>The metered client.</returns>
    public async Task<MeteredChatClient> ClientAsync()
    {
        if (_client is not null)
        {
            return _client;
        }

        FakeHttpMessageHandler endpoint = Endpoint ?? throw new InvalidOperationException("No endpoint was scripted.");
        _host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers(), handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = endpoint });
        _meter = new UnmeteredEnergyMeter(Time);
        await _meter.StartAsync(CancellationToken.None);

        var ledger = new RetryLedger();
        var services = new MeteringServices(_meter, NoOpMeterGate.Instance, Sink, NotConfiguredCostCalculator.Instance, TestInstruments.Create(), Time);
        IChatClient provider = _host.Factory.Create(Tier.Nvidia, Identity.ModelId);
        if (Decorate is not null)
        {
            provider = Decorate(provider);
        }

        var retry = new RateLimitRetryChatClient(provider, ledger, RetryOptions.Default, Time, new Random(Seed));
        _client = new MeteredChatClient(retry, Identity, services, ledger);
        return _client;
    }

    /// <summary>The one reading the sink holds.</summary>
    /// <returns>The reading.</returns>
    /// <exception cref="InvalidOperationException">The sink does not hold exactly one reading.</exception>
    public StepReading SingleReading()
    {
        IReadOnlyList<StepReading> readings = Sink.Snapshot();
        return readings.Count == 1
            ? readings[0]
            : throw new InvalidOperationException($"The sink holds {readings.Count} readings, not exactly 1.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client?.Dispose();
        _meter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _host?.Dispose();
    }
}