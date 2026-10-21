using System.Text.Json;

using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Energy.Testing;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-046: serialized and concurrent execution modes (REQ-013). The mode chooses the one process-wide meter gate; these tests prove that the serialized
/// gate keeps the windows of parallel branches apart, that it is shared with the judge, that its wait is outside the window and the latency, that it is
/// given back after a failure or a cancellation, and that concurrent mode registers the gate that admits everyone. Every wait on a task is bounded by
/// <see cref="HangGuard"/>, which is the system clock and only ends a run that already failed; no test uses the system clock for anything else.
/// </summary>
public sealed partial class ExecutionModeTests
{
    private static readonly IReadOnlyList<ChatMessage> Prompt = [new ChatMessage(ChatRole.User, "Summarise the report.")];

    // A test that waits on a held call or on the gate must fail, not hang the run, when the code under test leaks.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    // Wide margins: the branches take a second each and the assertions compare recorded timestamps, never exact durations.
    private static readonly TimeSpan BranchDelay = TimeSpan.FromSeconds(1);

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task Serialized_windows_do_not_overlap()
    {
        await using ServiceProvider provider = Provide(RunMode.Serialized);
        await using Rig rig = await Rig.CreateAsync(provider.GetRequiredService<IMeterGate>());
        TaskCompletionSource holdFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChatClient firstInner = new(rig.Time) { Hold = holdFirst.Task, ResponseDelay = BranchDelay };
        FakeChatClient secondInner = new(rig.Time) { ResponseDelay = BranchDelay };

        // The second branch is the judge: the gate of the run is shared with the judge client.
        Task<ChatResponse> first = rig.ClientOver(firstInner, "Planner").GetResponseAsync(Prompt);
        Task<ChatResponse> second = rig.ClientOver(secondInner, "Judge", isJudge: true).GetResponseAsync(Prompt);

        // Both branches are started; only the first is inside the gate, so the second has not reached its inner client and has no window.
        _ = Assert.Single(firstInner.Calls);
        Assert.False(second.IsCompleted);
        Assert.Empty(secondInner.Calls);
        holdFirst.SetResult();
        _ = await Task.WhenAll(first, second).WaitAsync(HangGuard);

        StepReading planner = rig.Reading("Planner");
        StepReading judge = rig.Reading("Judge");
        Assert.True(judge.StartUtc >= planner.EndUtc, "The second window must open after the first one closed.");
        Assert.False(Overlaps(planner, judge));
        Assert.False(planner.Flags.HasFlag(ReadingFlags.SharedWindow));
        Assert.False(judge.Flags.HasFlag(ReadingFlags.SharedWindow));
        Assert.Equal(StepOutcomes.Ok, planner.Outcome);
        Assert.Equal(StepOutcomes.Ok, judge.Outcome);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task Concurrent_windows_overlap_because_the_gate_admits_everyone()
    {
        // The control of the test above: the same two branches over the no-op gate do overlap, so that test cannot pass for lack of a way to see an overlap.
        await using ServiceProvider provider = Provide(RunMode.Concurrent);
        await using Rig rig = await Rig.CreateAsync(provider.GetRequiredService<IMeterGate>());
        TaskCompletionSource holdFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChatClient firstInner = new(rig.Time) { Hold = holdFirst.Task, ResponseDelay = BranchDelay };
        FakeChatClient secondInner = new(rig.Time) { ResponseDelay = BranchDelay };

        Task<ChatResponse> first = rig.ClientOver(firstInner, "Planner").GetResponseAsync(Prompt);
        Task<ChatResponse> second = rig.ClientOver(secondInner, "Writer").GetResponseAsync(Prompt);

        // The second branch ran while the first was in flight.
        _ = await second.WaitAsync(HangGuard);
        Assert.False(first.IsCompleted);
        holdFirst.SetResult();
        _ = await first.WaitAsync(HangGuard);

        Assert.True(Overlaps(rig.Reading("Planner"), rig.Reading("Writer")));
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task Serialized_streams_do_not_overlap_and_the_second_starts_when_the_first_is_disposed()
    {
        await using ServiceProvider provider = Provide(RunMode.Serialized);
        await using Rig rig = await Rig.CreateAsync(provider.GetRequiredService<IMeterGate>());
        FakeChatClient firstInner = new(rig.Time) { Updates = [new FakeUpdate("part 0 ", TimeSpan.FromMilliseconds(500)), new FakeUpdate("part 1 ", TimeSpan.FromMilliseconds(500))] };
        FakeChatClient secondInner = new(rig.Time) { Updates = [new FakeUpdate("part 0 ", TimeSpan.FromMilliseconds(500))] };
        IAsyncEnumerator<ChatResponseUpdate> first = rig.ClientOver(firstInner, "Planner").GetStreamingResponseAsync(Prompt).GetAsyncEnumerator();
        IAsyncEnumerator<ChatResponseUpdate> second = rig.ClientOver(secondInner, "Writer").GetStreamingResponseAsync(Prompt).GetAsyncEnumerator();

        Assert.True(await first.MoveNextAsync().AsTask().WaitAsync(HangGuard));
        Assert.True(await first.MoveNextAsync().AsTask().WaitAsync(HangGuard));
        Task<bool> secondStarted = second.MoveNextAsync().AsTask();

        // A stream holds the gate until its enumerator is disposed, not until its last update was returned: the first stream has returned both updates
        // and is not disposed, and the second stream still waits.
        Assert.False(secondStarted.IsCompleted);
        Assert.Empty(secondInner.Calls);
        await first.DisposeAsync().AsTask().WaitAsync(HangGuard);
        Assert.True(await secondStarted.WaitAsync(HangGuard));
        await second.DisposeAsync().AsTask().WaitAsync(HangGuard);

        StepReading planner = rig.Reading("Planner");
        StepReading writer = rig.Reading("Writer");
        Assert.True(writer.StartUtc >= planner.EndUtc);
        Assert.False(Overlaps(planner, writer));
        Assert.False(planner.Flags.HasFlag(ReadingFlags.SharedWindow));
        Assert.False(writer.Flags.HasFlag(ReadingFlags.SharedWindow));
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task The_gate_wait_is_outside_the_window_and_the_latency()
    {
        await using ServiceProvider provider = Provide(RunMode.Serialized);
        await using Rig rig = await Rig.CreateAsync(provider.GetRequiredService<IMeterGate>());
        TaskCompletionSource holdFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChatClient firstInner = new(rig.Time) { Hold = holdFirst.Task, ResponseDelay = TimeSpan.Zero };
        FakeChatClient secondInner = new(rig.Time) { ResponseDelay = TimeSpan.FromMilliseconds(200) };
        Task<ChatResponse> first = rig.ClientOver(firstInner, "Planner").GetResponseAsync(Prompt);
        Task<ChatResponse> second = rig.ClientOver(secondInner, "Writer").GetResponseAsync(Prompt);

        // The first call holds the gate for one second of the fake clock while the second waits for it.
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(second.IsCompleted);
        holdFirst.SetResult();
        _ = await Task.WhenAll(first, second).WaitAsync(HangGuard);

        StepReading waiter = rig.Reading("Writer");
        Assert.Equal(Metric.Measured(200, "ms"), waiter.LatencyMs);
        Assert.Equal(TimeSpan.FromMilliseconds(200), waiter.EndUtc - waiter.StartUtc);

        // The energy window spans the 200 ms of the call and not the second of waiting: 60 W for 0.2 s is 12 J, not 72 J.
        Assert.Equal(12.0, waiter.GrossJoules.Value ?? double.NaN, 6);
        Assert.True(waiter.StartUtc >= rig.Reading("Planner").EndUtc);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task Gate_is_released_on_failure()
    {
        await using ServiceProvider provider = Provide(RunMode.Serialized);
        SerializedMeterGate gate = Assert.IsType<SerializedMeterGate>(provider.GetRequiredService<IMeterGate>());
        await using Rig rig = await Rig.CreateAsync(gate);
        TaskCompletionSource holdFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChatClient failing = new(rig.Time) { Hold = holdFirst.Task, ResponseDelay = TimeSpan.Zero };
        FakeChatClient healthy = new(rig.Time);
        Task<ChatResponse> first = rig.ClientOver(failing, "Planner").GetResponseAsync(Prompt);
        Task<ChatResponse> second = rig.ClientOver(healthy, "Writer").GetResponseAsync(Prompt);
        Assert.Equal(0, gate.CurrentCount);
        Assert.Empty(healthy.Calls);

        // The first call fails while the second waits for the gate; the second must proceed.
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        holdFirst.SetException(new InvalidDataException("The provider broke."));
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => first.WaitAsync(HangGuard));
        _ = await second.WaitAsync(HangGuard);

        Assert.Equal(1, gate.CurrentCount);
        Assert.Equal(StepOutcomes.Error, rig.Reading("Planner").Outcome);
        Assert.Equal(StepOutcomes.Ok, rig.Reading("Writer").Outcome);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task Gate_is_released_on_cancellation()
    {
        await using ServiceProvider provider = Provide(RunMode.Serialized);
        SerializedMeterGate gate = Assert.IsType<SerializedMeterGate>(provider.GetRequiredService<IMeterGate>());
        await using Rig rig = await Rig.CreateAsync(gate);
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChatClient cancelled = new(rig.Time) { Hold = never.Task };
        FakeChatClient healthy = new(rig.Time);
        using CancellationTokenSource cancellation = new();
        Task<ChatResponse> first = rig.ClientOver(cancelled, "Planner").GetResponseAsync(Prompt, cancellationToken: cancellation.Token);
        Task<ChatResponse> second = rig.ClientOver(healthy, "Writer").GetResponseAsync(Prompt);
        Assert.Equal(0, gate.CurrentCount);

        // The first call is cancelled in flight, after its window has a length; the second must proceed.
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(HangGuard));
        _ = await second.WaitAsync(HangGuard);

        Assert.Equal(1, gate.CurrentCount);
        Assert.Equal(StepOutcomes.Cancelled, rig.Reading("Planner").Outcome);
        Assert.Equal(StepOutcomes.Ok, rig.Reading("Writer").Outcome);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task A_metered_call_nested_inside_a_call_that_holds_the_gate_waits_until_its_token_fires()
    {
        // The gate is not re-entrant: a metered call made from inside a call that holds the gate waits for the gate (here until its own token fires).
        // The positive half, the judge running once the flow has given the gate back, is Serialized_windows_do_not_overlap with isJudge set.
        await using ServiceProvider provider = Provide(RunMode.Serialized);
        SerializedMeterGate gate = Assert.IsType<SerializedMeterGate>(provider.GetRequiredService<IMeterGate>());
        await using Rig rig = await Rig.CreateAsync(gate);
        FakeChatClient judgeInner = new(rig.Time);
        MeteredChatClient judge = rig.ClientOver(judgeInner, "Judge", isJudge: true);
        using CancellationTokenSource nested = new();
        NestedCallChatClient flow = new(() => judge.GetResponseAsync(Prompt, cancellationToken: nested.Token));

        Task<ChatResponse> outer = rig.ClientOver(flow, "Planner").GetResponseAsync(Prompt);

        Assert.False(outer.IsCompleted);
        Assert.Empty(judgeInner.Calls);
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        await nested.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outer.WaitAsync(HangGuard));

        Assert.Empty(judgeInner.Calls);
        Assert.Equal("Planner", Assert.Single(rig.Sink.Snapshot()).Agent);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task Concurrent_mode_registers_the_no_op_gate()
    {
        await using ServiceProvider provider = Provide(RunMode.Concurrent);

        IMeterGate gate = provider.GetRequiredService<IMeterGate>();

        Assert.Same(NoOpMeterGate.Instance, gate);
        Assert.Same(gate, provider.GetRequiredService<IMeterGate>());
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task Serialized_mode_registers_one_serialized_gate_per_process()
    {
        await using ServiceProvider provider = Provide(RunMode.Serialized);

        IMeterGate first = provider.GetRequiredService<IMeterGate>();
        IMeterGate second = provider.GetRequiredService<IMeterGate>();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        _ = Assert.IsType<SerializedMeterGate>(first);
        Assert.Same(first, second);
        Assert.Same(first, scope.ServiceProvider.GetRequiredService<IMeterGate>());
    }

    [Theory]
    [Trait("Spec", "REQ-013")]
    [InlineData(RunMode.Serialized)]
    [InlineData(RunMode.Concurrent)]
    public void The_mode_changes_only_the_gate_and_adds_nothing_else(RunMode mode)
    {
        ServiceCollection services = [];

        _ = services.AddAgentJoulesMeterGate(mode);

        // One singleton, nothing else: the workflow topology is built from the same registrations in both modes.
        ServiceDescriptor descriptor = Assert.Single(services);
        Assert.Equal(typeof(IMeterGate), descriptor.ServiceType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task The_container_disposes_the_serialized_gate_with_the_provider_and_not_the_no_op_gate()
    {
        ServiceProvider serialized = Provide(RunMode.Serialized);
        IMeterGate gate = serialized.GetRequiredService<IMeterGate>();
        IAsyncDisposable held = await gate.AcquireAsync(CancellationToken.None);
        await held.DisposeAsync();

        await serialized.DisposeAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await gate.AcquireAsync(CancellationToken.None));
        ServiceProvider concurrent = Provide(RunMode.Concurrent);
        await concurrent.DisposeAsync();
        IAsyncDisposable noOp = await NoOpMeterGate.Instance.AcquireAsync(CancellationToken.None);
        await noOp.DisposeAsync();
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public void A_second_gate_registration_is_rejected_and_an_undefined_mode_is_not_a_mode()
    {
        ServiceCollection services = [];
        _ = services.AddAgentJoulesMeterGate(RunMode.Serialized);

        _ = Assert.Throws<InvalidOperationException>(() => services.AddAgentJoulesMeterGate(RunMode.Concurrent));
        Assert.Equal("mode", Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddAgentJoulesMeterGate(default)).ParamName);
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddAgentJoulesMeterGate(null!, RunMode.Serialized)).ParamName);
        Assert.False(Enum.IsDefined(default(RunMode)));
    }

    [Theory]
    [Trait("Spec", "REQ-013")]
    [InlineData(RunMode.Serialized, "\"serialized\"")]
    [InlineData(RunMode.Concurrent, "\"concurrent\"")]
    public void The_mode_serializes_as_its_lowercase_name_and_reads_back(RunMode mode, string json)
    {
        Assert.Equal(json, JsonSerializer.Serialize(mode));
        Assert.Equal(mode, JsonSerializer.Deserialize<RunMode>(json));
    }

    [Theory]
    [Trait("Spec", "REQ-013")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"Serialized\"")]
    [InlineData("\" concurrent\"")]
    [InlineData("\"serialized,concurrent\"")]
    [InlineData("\"parallel\"")]
    public void The_mode_reader_accepts_nothing_but_the_two_names(string json)
    {
        _ = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RunMode>(json));
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public void An_undefined_mode_is_not_written()
    {
        _ = Assert.Throws<JsonException>(() => JsonSerializer.Serialize((RunMode)99));
    }

    private static ServiceProvider Provide(RunMode mode) => new ServiceCollection().AddAgentJoulesMeterGate(mode).BuildServiceProvider();

    // The strict overlap rule of the run assembler (AJ-047): two windows overlap when each starts before the other stops.
    private static bool Overlaps(StepReading a, StepReading b) => a.StartUtc < b.EndUtc && b.StartUtc < a.EndUtc;

    // A client whose call makes a nested call and answers with its response: the shape of a flow that calls the judge while it holds the gate.
    private sealed class NestedCallChatClient(Func<Task<ChatResponse>> nested) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => nested();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The nested-call client answers buffered calls only.");

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    // The services of one test: a fake clock, a sink, a deterministic meter at a constant 60 W on that clock and the gate the test brings.
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(FakeTimeProvider time, DeterministicEnergyMeter meter, IMeterGate gate)
        {
            Time = time;
            Meter = meter;
            Gate = gate;
        }

        public FakeTimeProvider Time { get; }

        public InMemoryMetricsSink Sink { get; } = new();

        public DeterministicEnergyMeter Meter { get; }

        public IMeterGate Gate { get; }

        public static async Task<Rig> CreateAsync(IMeterGate gate)
        {
            FakeTimeProvider time = new();
            DeterministicEnergyMeter meter = new(time, static _ => 60.0, sampleRateHz: 10.0, counterSupported: true, MeterMethodSetting.Auto);
            await meter.StartAsync(CancellationToken.None);
            return new Rig(time, meter, gate);
        }

        public MeteredChatClient ClientOver(IChatClient inner, string agent, bool isJudge = false) =>
            new(inner, new StepIdentity(agent, "model-a", Tier.Local, "summarise", 1, isJudge), new MeteringServices(Meter, Gate, Sink, NotConfiguredCostCalculator.Instance, TestInstruments.Create(), Time));

        public StepReading Reading(string agent) => Sink.Snapshot().Single(reading => reading.Agent == agent);

        public ValueTask DisposeAsync() => Meter.DisposeAsync();
    }
}