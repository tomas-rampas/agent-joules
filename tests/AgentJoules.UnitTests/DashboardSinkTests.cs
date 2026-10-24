using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Xml.Linq;

using AgentJoules.Cli;
using AgentJoules.Cli.Commands;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Dashboard;
using AgentJoules.Metering;
using AgentJoules.Nvml;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Runs;

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-089 (REQ-032, EDGE-019): readings fan out to a dashboard sink through bounded, drop-oldest buffers that can never block the run; the meter forwards the power samples it already
/// took and the metered client its streaming progress, both throttled. Every timing test runs on a fake clock; the only real-time values are hang guards that end a run that already failed.
/// </summary>
[Trait("Spec", "EDGE-019")]
[Trait("Spec", "REQ-032")]
public sealed class DashboardSinkTests
{
    private const int Capacity = 1000;
    private const int MaxYields = 100_000;
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Origin = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<ChatMessage> Prompt = [new ChatMessage(ChatRole.User, "Summarise the report.")];

    // ---- criterion 1: the fan-out ----

    [Fact]
    public void Throwing_target_leaves_in_memory_sink_complete()
    {
        var logger = new RecordingLogger();
        var memory = new InMemoryMetricsSink();
        var throwing = new ThrowingMetricsSink();
        var fanOut = new FanOutMetricsSink(logger, memory, throwing);

        for (int i = 0; i < 5; i++)
        {
            fanOut.Publish(StepReadingFixtures.Metered());
        }

        Assert.Equal(5, memory.Snapshot().Count);
        Assert.Equal(5, throwing.Calls);

        // Logged once for the target, at Warning, with the type names only: the exception's message never reaches a log.
        RecordedLogEntry warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(nameof(ThrowingMetricsSink), warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidDataException), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ThrowingMetricsSink.SecretMessage, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_throwing_first_target_does_not_stop_the_second()
    {
        var logger = new RecordingLogger();
        var memory = new InMemoryMetricsSink();
        var fanOut = new FanOutMetricsSink(logger, new ThrowingMetricsSink(), memory);

        fanOut.Publish(StepReadingFixtures.Metered());
        fanOut.Publish(StepReadingFixtures.Metered());

        Assert.Equal(2, memory.Snapshot().Count);
        _ = Assert.Single(logger.At(LogLevel.Warning));
    }

    [Fact]
    public void The_first_failure_of_each_target_is_logged_once()
    {
        var logger = new RecordingLogger();
        var fanOut = new FanOutMetricsSink(logger, new ThrowingMetricsSink(), new InMemoryMetricsSink(), new ThrowingMetricsSink());

        for (int i = 0; i < 4; i++)
        {
            fanOut.Publish(StepReadingFixtures.Metered());
        }

        Assert.Equal(2, logger.At(LogLevel.Warning).Count);
    }

    [Fact]
    public void The_fan_out_forwards_in_the_given_order()
    {
        var order = new ConcurrentQueue<string>();
        var fanOut = new FanOutMetricsSink(new RecordingLogger(), new NamedMetricsSink("first", order), new NamedMetricsSink("second", order), new NamedMetricsSink("third", order));

        fanOut.Publish(StepReadingFixtures.Metered());

        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public void A_null_reading_is_refused_before_any_target()
    {
        var memory = new InMemoryMetricsSink();
        var fanOut = new FanOutMetricsSink(new RecordingLogger(), memory);

        _ = Assert.Throws<ArgumentNullException>(() => fanOut.Publish(null!));
        Assert.Empty(memory.Snapshot());
    }

    [Fact]
    public void The_fan_out_refuses_a_null_logger_a_null_target_list_and_a_null_target()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new FanOutMetricsSink(null!, new InMemoryMetricsSink()));
        _ = Assert.Throws<ArgumentNullException>(() => new FanOutMetricsSink(new RecordingLogger(), null!));
        _ = Assert.Throws<ArgumentException>(() => new FanOutMetricsSink(new RecordingLogger(), new InMemoryMetricsSink(), null!));
    }

    // ---- criterion 2: Emit is one TryWrite; the connection keeps the newest 1000 ----

    [Fact]
    public async Task Connection_buffer_keeps_newest_1000()
    {
        var time = new FakeTimeProvider(Origin);
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), time);
        using DashboardConnection connection = sink.Connect();

        for (int batch = 0; batch < 3; batch++)
        {
            for (int i = 0; i < 500; i++)
            {
                sink.Emit(Power((batch * 500) + i));
            }

            await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        }

        Assert.Equal(Capacity, connection.Events.Count);
        DateTimeOffset[] received = [.. Drain(connection).Select(AtUtcOf)];
        Assert.Equal(Enumerable.Range(500, Capacity).Select(At), received);
    }

    [Fact]
    public async Task A_published_reading_reaches_a_connection_as_one_step_completed_event()
    {
        var time = new FakeTimeProvider(Origin);
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), time);
        using DashboardConnection connection = sink.Connect();

        sink.Publish(StepReadingFixtures.Metered());
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        string payload = Assert.Single(Drain(connection));
        Assert.Equal("step-completed", TypeOf(payload));
        Assert.Equal(Origin, AtUtcOf(payload));
    }

    [Fact]
    public async Task Publish_refuses_a_null_reading()
    {
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));

        _ = Assert.Throws<ArgumentNullException>(() => sink.Publish(null!));
    }

    [Fact]
    public async Task A_connection_receives_only_events_applied_after_its_snapshot_without_loss_or_doubling()
    {
        const int count = 500;
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        using var started = new ManualResetEventSlim();
        Task emitter = Task.Run(() =>
        {
            for (int i = 0; i < count; i++)
            {
                sink.Emit(Power(i));
                if (i == count / 4)
                {
                    started.Set();
                }
            }
        });

        Assert.True(started.Wait(HangGuard));
        using DashboardConnection connection = sink.Connect();
        await emitter.WaitAsync(HangGuard);
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        int before = checked((int)connection.AppliedAtConnect);
        DateTimeOffset[] received = [.. Drain(connection).Select(AtUtcOf)];
        Assert.Equal(Enumerable.Range(before, count - before).Select(At), received);
    }

    [Fact]
    public async Task The_snapshot_of_a_connection_is_a_snapshot_envelope()
    {
        var time = new FakeTimeProvider(Origin);
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), time);
        time.Advance(TimeSpan.FromSeconds(3));

        using DashboardConnection connection = sink.Connect();

        Assert.Equal("snapshot", TypeOf(connection.Snapshot));
        Assert.Equal(Origin.AddSeconds(3), AtUtcOf(connection.Snapshot));
        Assert.Equal(0, connection.Events.Count);
    }

    // ---- criterion 3: a faulted pump never reaches Emit ----

    [Fact]
    public async Task Emit_returns_while_pump_faulted()
    {
        var logger = new RecordingLogger();
        await using var sink = DashboardFixtures.NewSink(logger, new FakeTimeProvider(Origin));
        using DashboardConnection connection = sink.Connect();

        // The pump cannot apply an event type it does not know: that event is its fault, and the nine after it still arrive.
        sink.Emit(new UnknownEvent(Origin));
        for (int i = 1; i < 10; i++)
        {
            sink.Emit(Power(i));
        }

        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal(1, sink.PumpFaultCount);
        Assert.Equal(9, connection.Events.Count);
        RecordedLogEntry warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(nameof(NotSupportedException), warning.Message, StringComparison.Ordinal);

        // The exception type only: neither the exception's message nor the event type it names reaches the log.
        Assert.DoesNotContain("cannot apply", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(UnknownEvent), warning.Message, StringComparison.Ordinal);

        // A second fault is counted and not logged again; the loop still delivers.
        sink.Emit(new UnknownEvent(Origin));
        sink.Emit(Power(10));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal(2, sink.PumpFaultCount);
        Assert.Equal(10, connection.Events.Count);
        _ = Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task A_throwing_logger_neither_ends_the_pump_nor_reaches_the_disposer()
    {
        var sink = DashboardFixtures.NewSink(new ThrowingLogger(), new FakeTimeProvider(Origin));
        using DashboardConnection connection = sink.Connect();

        sink.Emit(new UnknownEvent(Origin));
        sink.Emit(Power(1));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        sink.Emit(Power(2));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal(1, sink.PumpFaultCount);
        Assert.Equal(["power", "power"], Drain(connection).Select(TypeOf));
        Assert.False(connection.Events.Completion.IsCompleted);
        await sink.DisposeAsync();
        await connection.Events.Completion.WaitAsync(HangGuard);
    }

    [Fact]
    public void A_throwing_logger_never_reaches_the_caller_of_the_fan_out()
    {
        var memory = new InMemoryMetricsSink();
        var fanOut = new FanOutMetricsSink(new ThrowingLogger(), new ThrowingMetricsSink(), memory);

        fanOut.Publish(StepReadingFixtures.Metered());
        fanOut.Publish(StepReadingFixtures.Metered());

        Assert.Equal(2, memory.Snapshot().Count);
    }

    [Fact]
    public async Task The_pump_runs_on_another_thread_than_the_emitter()
    {
        var logger = new ThreadRecordingLogger();
        await using var sink = DashboardFixtures.NewSink(logger, new FakeTimeProvider(Origin));
        int emitter = 0;
        var thread = new Thread(() =>
        {
            emitter = Environment.CurrentManagedThreadId;
            sink.Emit(new UnknownEvent(Origin));
        });

        thread.Start();
        Assert.True(thread.Join(HangGuard));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal(1, sink.PumpFaultCount);
        Assert.NotEqual(emitter, Assert.Single(logger.Threads));
    }

    [Fact]
    public async Task A_flush_covers_an_event_the_caller_emitted_while_a_second_writer_runs()
    {
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        using var stop = new CancellationTokenSource();

        // The second writer stays well below the ingress capacity (a flush every 100 emits), so nothing it writes can push the caller's event out.
        Task second = Task.Run(async () =>
        {
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                sink.Emit(Power(i));
                if (i % 100 == 99)
                {
                    await sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        });

        const int rounds = 5000;
        for (int round = 1; round <= rounds; round++)
        {
            sink.Emit(new UnknownEvent(Origin));
            await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            Assert.Equal(round, sink.PumpFaultCount);
        }

        await stop.CancelAsync();
        await second.WaitAsync(HangGuard);
    }

    // The gatekeeper's stress probe (M-1): after a writer's own Emit returned and its flush completed, its event is applied, with every core writing at once.
    [Fact]
    public async Task A_flush_covers_every_returned_emit_under_stress()
    {
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        Lock gate = (Lock)typeof(DashboardSink).GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sink)!;
        var snapshot = (DashboardSnapshot)typeof(DashboardSink).GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sink)!;
        long violations = 0;
        long checks = 0;
        var elapsed = Stopwatch.StartNew();

        // The state tracks a fixed number of agents (AJ-090), so each writer owns one tracked agent (at most eight writers, the four configured agents and eight more) and alternates a step start and a
        // step completion for it. After its own flush, the status of that agent must be the one its own last event set: an event that returned before the flush began is applied by the time it completes.
        int writerCount = Math.Min(Environment.ProcessorCount, 8);
        Task[] writers =
        [
            .. Enumerable.Range(0, writerCount).Select(writer => Task.Run(async () =>
            {
                string agent = $"w{writer}";
                for (int i = 0; elapsed.Elapsed < TimeSpan.FromSeconds(2); i++)
                {
                    bool start = i % 2 == 0;
                    if (start)
                    {
                        sink.Emit(new StepStartedEvent(Origin, false, new StepIdentity(agent, "m", Tier.Local, "t", 1, IsJudge: false), 0.0));
                    }
                    else
                    {
                        sink.Publish(StepReadingFixtures.Metered(agent: agent));
                    }

                    await sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    string status;
                    using (gate.EnterScope())
                    {
                        status = snapshot.StatusOf(agent);
                    }

                    _ = Interlocked.Increment(ref checks);
                    if (status != (start ? DashboardSnapshot.RunningStatus : StepOutcomes.Ok))
                    {
                        _ = Interlocked.Increment(ref violations);
                    }
                }
            })),
        ];

        await Task.WhenAll(writers).WaitAsync(HangGuard);

        Assert.True(Interlocked.Read(ref checks) > 0);
        Assert.True(Interlocked.Read(ref violations) == 0, $"{violations} of {checks} checks saw another event in place of the writer's own");
    }

    // ---- the two-phase first fault (the gatekeeper's round-2 probes) ----

    // The lock is free while the first fault is logged: the logger connects from another thread while it records.
    [Fact]
    public async Task The_lock_is_free_while_the_first_fault_is_logged()
    {
        DashboardSink? sink = null;
        bool connected = false;
        var logger = new CallbackLogger(() => connected = Task.Run(() => sink!.Connect().Dispose()).Wait(TimeSpan.FromSeconds(5)));
        sink = DashboardFixtures.NewSink(logger, new FakeTimeProvider(Origin));

        sink.Emit(new UnknownEvent(Origin));
        sink.Emit(Power(1));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal(1, logger.Calls);
        Assert.True(connected, "Connect blocked while the first fault was logged.");
        await sink.DisposeAsync();
    }

    // The lock is free while the degraded-snapshot warning (event 302) is logged: the logger connects from another thread while it records, and that connect must return at once.
    [Fact]
    public async Task The_lock_is_free_while_the_degraded_snapshot_is_logged()
    {
        DashboardSink? sink = null;
        bool connected = false;
        var logger = new EventCallbackLogger(302, () => connected = Task.Run(() => sink!.Connect().Dispose()).Wait(TimeSpan.FromSeconds(5)));
        sink = DashboardFixtures.NewSink(logger, new FakeTimeProvider(Origin));
        sink.Emit(RunStarted());

        // A sample time that is not a number cannot be written, so every snapshot after it is the smaller form, and the first one is logged.
        sink.Emit(new PowerEvent(Origin, false, Metric.Measured(40.0, "W"), double.NaN));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        sink.Connect().Dispose();

        using var guard = new CancellationTokenSource(HangGuard);
        while (logger.Calls == 0)
        {
            await Task.Yield();
            guard.Token.ThrowIfCancellationRequested();
        }

        Assert.Equal(1, logger.Calls);
        Assert.True(connected, "Connect blocked while the degraded snapshot was logged.");
        await sink.DisposeAsync();
    }

    // The first fault races Connect, FlushAsync and a second writer: the flush that covers the fault completes after its record, each event is counted once, the loop goes on,
    // and every connection saw exactly the events applied after its snapshot.
    [Fact]
    public async Task The_first_fault_races_connect_flush_and_a_writer()
    {
        const int rounds = 300;
        int earlyFlush = 0;
        int countMismatch = 0;
        int connectionMismatch = 0;
        for (int round = 0; round < rounds; round++)
        {
            var logger = new SlowLogger(spin: 2000 + ((round % 7) * 3000));
            var sink = DashboardFixtures.NewSink(logger, new FakeTimeProvider(Origin));
            var connections = new ConcurrentBag<DashboardConnection>();
            using var go = new ManualResetEventSlim();
            Task writer = Task.Run(async () =>
            {
                go.Wait();
                for (int i = 0; i < 400; i++)
                {
                    sink.Emit(Power(i));
                    if (i % 20 == 7)
                    {
                        connections.Add(sink.Connect());
                    }

                    if (i % 50 == 49)
                    {
                        await sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
            });
            go.Set();
            Thread.SpinWait((round % 30) * 300);
            sink.Emit(new UnknownEvent(Origin));
            await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            if (logger.Count != 1)
            {
                earlyFlush++;
            }

            await writer.WaitAsync(HangGuard);
            sink.Emit(Power(10_000));
            await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            (long written, long pumped, long dropped, long applied) = Counts(sink);
            if (written != pumped + dropped || dropped != 0 || sink.PumpFaultCount != 1 || applied != written - 1)
            {
                countMismatch++;
            }

            foreach (DashboardConnection connection in connections)
            {
                if (connection.AppliedAtConnect + Drain(connection).Count != applied)
                {
                    connectionMismatch++;
                }

                connection.Dispose();
            }

            await sink.DisposeAsync();
        }

        Assert.Equal((0, 0, 0), (earlyFlush, countMismatch, connectionMismatch));
    }

    // Writers racing the disposal: after DisposeAsync every count is exact (a refused write takes its count back), and a flush taken after the disposal completes.
    [Fact]
    public async Task The_counts_are_exact_after_writers_race_the_disposal()
    {
        const int rounds = 300;
        int bad = 0;
        for (int round = 0; round < rounds; round++)
        {
            var sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
            using var go = new ManualResetEventSlim();
            int started = 0;
            Task[] writers =
            [
                .. Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                {
                    _ = Interlocked.Increment(ref started);
                    go.Wait();
                    for (int i = 0; i < 1500; i++)
                    {
                        sink.Emit(i % 97 == 3 ? new UnknownEvent(Origin) : Power(i));
                    }
                })),
            ];
            while (Volatile.Read(ref started) < 4)
            {
                Thread.SpinWait(10);
            }

            go.Set();
            Thread.SpinWait((round % 50) * 200);
            sink.Dispose();
            await Task.WhenAll(writers).WaitAsync(HangGuard);
            await sink.DisposeAsync();
            await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            (long written, long pumped, long dropped, _) = Counts(sink);
            if (written != pumped + dropped)
            {
                bad++;
            }
        }

        Assert.Equal(0, bad);
    }

    // The fault path logs through a static, non-capturing helper: PumpOne runs for every event and must allocate no closure. The allocation is checked by reflection, not by
    // GC.GetAllocatedBytesForCurrentThread, because the pump runs on a thread-pool thread the test cannot read the counter of.
    [Fact]
    public void The_pump_allocates_no_closure()
    {
        Type[] closures = [.. typeof(DashboardSink).GetNestedTypes(BindingFlags.NonPublic).Where(static type => type.Name.Contains("DisplayClass", StringComparison.Ordinal))];

        Assert.Empty(closures);
        Assert.NotNull(typeof(DashboardSink).GetMethod("LogFaultSafely", BindingFlags.NonPublic | BindingFlags.Static, [typeof(ILogger), typeof(string)]));
    }

    // ---- the ingress semantics while the pump is held (gatekeeper m-2 a and b) ----

    [Fact]
    public async Task A_held_pump_never_blocks_emit_or_dispose_and_dispose_async_waits_for_it()
    {
        using var time = new PumpHoldingTime(Origin);
        var sink = DashboardFixtures.NewSink(new RecordingLogger(), time);
        using DashboardConnection connection = sink.Connect();
        time.HoldPump = true;
        sink.Emit(RunStarted());
        Assert.True(time.PumpHeld.Wait(HangGuard), "The pump never reached the clock.");

        var watch = Stopwatch.StartNew();
        for (int i = 1; i <= 1500; i++)
        {
            sink.Emit(Power(i));
        }

        TimeSpan emitting = watch.Elapsed;
        watch.Restart();
        sink.Dispose();
        TimeSpan disposing = watch.Elapsed;
        ValueTask waiting = sink.DisposeAsync();
        bool completedWhileHeld = waiting.IsCompleted;

        time.Release();
        await waiting.AsTask().WaitAsync(HangGuard);

        Assert.True(emitting < TimeSpan.FromSeconds(2), $"1500 emits took {emitting}");
        Assert.True(disposing < TimeSpan.FromSeconds(1), $"Dispose took {disposing}");
        Assert.False(completedWhileHeld);

        // The ingress kept the newest 1000 powers (501 to 1500); with the run-started and snapshot payloads ahead of them, the connection dropped its two oldest.
        Assert.Equal(Enumerable.Range(501, Capacity).Select(At), Drain(connection).Select(AtUtcOf));
        await connection.Events.Completion.WaitAsync(HangGuard);
    }

    [Fact]
    public async Task The_ingress_keeps_the_newest_1000_while_the_pump_is_held()
    {
        using var time = new PumpHoldingTime(Origin);
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), time);
        time.HoldPump = true;
        sink.Emit(RunStarted());
        Assert.True(time.PumpHeld.Wait(HangGuard), "The pump never reached the clock.");
        for (int i = 1; i <= 1500; i++)
        {
            sink.Emit(new StepCompletedEvent(At(i), false, StepReadingFixtures.Metered(agent: "agent" + i)));
        }

        time.Release();
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        var snapshot = (DashboardSnapshot)typeof(DashboardSink).GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sink)!;
        Lock gate = (Lock)typeof(DashboardSink).GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sink)!;
        using (gate.EnterScope())
        {
            Assert.Equal(Capacity, snapshot.CompletedStepCount);
            Assert.Equal(DashboardSnapshot.WaitingStatus, snapshot.StatusOf("agent500"));
            Assert.Equal(StepOutcomes.Ok, snapshot.StatusOf("agent501"));
        }
    }

    // ---- criterion 4: a client that never reads does not stop publishing ----

    [Fact]
    public async Task A_client_that_never_reads_does_not_stop_publishing()
    {
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        using DashboardConnection connection = sink.Connect();
        var samples = new List<int>();

        Task publishing = Task.Run(() =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                sink.Emit(Power(i));
                if ((i + 1) % 1000 == 0)
                {
                    samples.Add(connection.Events.Count);
                }
            }
        });

        await publishing.WaitAsync(TimeSpan.FromSeconds(10));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        samples.Add(connection.Events.Count);

        Assert.Equal(11, samples.Count);
        Assert.All(samples, count => Assert.InRange(count, 0, Capacity));
        Assert.InRange(connection.Events.Count, 1, Capacity);
    }

    // ---- the pump, the snapshot and the lifecycle ----

    [Fact]
    public async Task A_run_started_event_is_followed_by_a_snapshot_payload()
    {
        var time = new FakeTimeProvider(Origin);
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), time);
        using DashboardConnection connection = sink.Connect();

        sink.Emit(Power(0));
        sink.Emit(RunStarted());
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal(["power", "run-started", "snapshot"], Drain(connection).Select(TypeOf));
    }

    [Fact]
    public void A_run_started_event_resets_the_snapshot_state()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        _ = snapshot.Apply(RunStarted());
        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, StepReadingFixtures.Metered()));
        _ = snapshot.Apply(new BaselineEvent(Origin, false, Baseline()));
        Assert.Equal(1, snapshot.CompletedStepCount);
        Assert.NotNull(snapshot.Baseline);

        _ = snapshot.Apply(RunStarted());

        Assert.Equal(0, snapshot.CompletedStepCount);
        Assert.Null(snapshot.Baseline);
        Assert.NotNull(snapshot.Run);
    }

    [Fact]
    public void The_snapshot_tracks_the_agent_status_and_the_active_agents()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        StepIdentity writer = new("Writer", "model-a", Tier.Local, "summarise", 1, IsJudge: false);

        _ = snapshot.Apply(new StepStartedEvent(Origin, false, writer, 1.5));
        Assert.Equal(["Writer"], snapshot.ActiveAgents);
        Assert.Equal("running", snapshot.StatusOf("Writer"));

        _ = snapshot.Apply(new StepCompletedEvent(Origin, false, StepReadingFixtures.Metered(agent: "Writer")));
        Assert.Empty(snapshot.ActiveAgents);
        Assert.Equal(StepOutcomes.Ok, snapshot.StatusOf("Writer"));
        Assert.Equal("waiting", snapshot.StatusOf("Reviewer"));
    }

    [Fact]
    public void The_snapshot_carries_the_replay_flag_of_the_latest_run_started()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        RunStartedEvent live = RunStarted();

        _ = snapshot.Apply(live with { Replay = true });
        Assert.True(ReplayOf(snapshot.ToSnapshotPayload(At(1))));

        _ = snapshot.Apply(live);
        Assert.False(ReplayOf(snapshot.ToSnapshotPayload(At(2))));
    }

    [Fact]
    public void Apply_refuses_an_unknown_event_type()
    {
        var snapshot = DashboardFixtures.NewSnapshot();

        _ = Assert.Throws<NotSupportedException>(() => snapshot.Apply(new UnknownEvent(Origin)));
        _ = Assert.Throws<NotSupportedException>(() => DashboardEventSerializer.TypeName(new UnknownEvent(Origin)));
    }

    [Fact]
    public void Every_event_type_has_its_wire_name_and_the_envelope_carries_type_atUtc_replay_and_data()
    {
        var snapshot = DashboardFixtures.NewSnapshot();
        StepIdentity step = new("Planner", "model-a", Tier.Local, "summarise", 1, IsJudge: false);
        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);
        (LiveEvent Event, string Name)[] cases =
        [
            (RunStarted(), "run-started"),
            (new BaselineEvent(At(1), true, Baseline()), "baseline"),
            (new ProgressEvent(At(2), false, "summarise", 2, 3, 1, 3), "progress"),
            (new StepStartedEvent(At(3), false, step, 1.0), "step-started"),
            (new StepProgressEvent(At(4), false, step, 7), "step-progress"),
            (new StepCompletedEvent(At(5), false, StepReadingFixtures.Metered()), "step-completed"),
            (Power(6), "power"),
            (new RunCompletedEvent(At(7), false, run.Run, run.Totals), "run-completed"),
        ];

        foreach ((LiveEvent liveEvent, string name) in cases)
        {
            Assert.Equal(name, DashboardEventSerializer.TypeName(liveEvent));
            string payload = snapshot.Apply(liveEvent);
            Assert.DoesNotContain('\n', payload);
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            Assert.Equal(name, root.GetProperty("type").GetString());
            Assert.Equal(liveEvent.AtUtc, root.GetProperty("atUtc").GetDateTimeOffset());
            Assert.Equal(liveEvent.Replay, root.GetProperty("replay").GetBoolean());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("data").ValueKind);
        }

        Assert.Equal("snapshot", TypeOf(DashboardEventSerializer.SerializeSnapshot(snapshot, At(8))));
        Assert.Equal(At(8), AtUtcOf(snapshot.ToSnapshotPayload(At(8))));
    }

    [Fact]
    public async Task Emit_after_disposal_is_a_silent_no_op_and_every_connection_completes()
    {
        var sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        DashboardConnection connection = sink.Connect();
        sink.Emit(Power(0));

        await sink.DisposeAsync();
        await sink.DisposeAsync();
        sink.Emit(Power(1));
        sink.Publish(StepReadingFixtures.Metered());
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        // The event emitted before the disposal was applied; the channel completes once it is read.
        Assert.Equal(["power"], Drain(connection).Select(TypeOf));
        await connection.Events.Completion.WaitAsync(HangGuard);
        connection.Dispose();
    }

    [Fact]
    public async Task Dispose_does_not_wait_and_is_idempotent()
    {
        var sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        DashboardConnection connection = sink.Connect();

        sink.Dispose();
        sink.Dispose();
        sink.Emit(Power(0));

        await connection.Events.Completion.WaitAsync(HangGuard);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task A_disposed_connection_is_unsubscribed()
    {
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        DashboardConnection first = sink.Connect();
        using DashboardConnection second = sink.Connect();

        first.Dispose();
        first.Dispose();
        sink.Emit(Power(0));
        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        await first.Events.Completion.WaitAsync(HangGuard);
        Assert.Equal(1, second.Events.Count);
    }

    [Fact]
    public async Task Connect_after_disposal_returns_a_completed_connection()
    {
        var sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        await sink.DisposeAsync();

        using DashboardConnection connection = sink.Connect();

        await connection.Events.Completion.WaitAsync(HangGuard);
        Assert.Equal("snapshot", TypeOf(connection.Snapshot));
    }

    [Fact]
    public async Task Flush_on_an_idle_sink_completes_and_honours_its_token()
    {
        await using var sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));

        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.FlushAsync(cancelled.Token));
    }

    // ---- criterion 5: the live types live in Core; without --dashboard the null sink is used ----

    [Fact]
    public void The_live_types_live_in_core_which_has_no_asp_net_core_reference()
    {
        Type[] types =
        [
            typeof(LiveEvent), typeof(RunStartedEvent), typeof(BaselineEvent), typeof(ProgressEvent), typeof(StepStartedEvent), typeof(StepProgressEvent),
            typeof(StepCompletedEvent), typeof(PowerEvent), typeof(RunCompletedEvent), typeof(ILiveEventSink), typeof(NullLiveEventSink), typeof(PowerForwarding),
        ];

        Assert.All(types, type => Assert.Equal(typeof(AgentJoulesOptions).Assembly, type.Assembly));
        Assert.All(types.Skip(1).Take(8), type => Assert.True(type.IsSealed && type.IsSubclassOf(typeof(LiveEvent)), type.Name));
        Assert.DoesNotContain(typeof(LiveEvent).Assembly.GetReferencedAssemblies(), name => name.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_the_dashboard_the_host_resolves_the_null_live_sink()
    {
        ILiveEventSink? resolved = null;
        CliHostOverrides overrides = CliHarness.Overrides() with { OnHostStarted = host => resolved = host.Services.GetRequiredService<ILiveEventSink>() };

        CliRunResult result = await CliHarness.RunAsync("probe", overrides).WaitAsync(HangGuard);

        Assert.Equal(0, result.ExitCode);
        Assert.Same(NullLiveEventSink.Instance, resolved);
    }

    [Fact]
    public void The_null_sink_drops_every_event_and_EmitSafely_drops_a_throwing_sink()
    {
        NullLiveEventSink.Instance.Emit(Power(0));
        new ThrowingLiveSink().EmitSafely(Power(0));
        NullLiveEventSink.Instance.EmitSafely(null!);
        ((ILiveEventSink)null!).EmitSafely(Power(0));
    }

    // ---- criterion 6: the project graph ----

    [Fact]
    public void The_dashboard_references_core_and_metering_only_and_is_the_only_asp_net_core_project()
    {
        string root = ConfigurationFixtures.RepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(root, "src", "AgentJoules.Dashboard", "AgentJoules.Dashboard.csproj"));

        string[] references = [.. project.Descendants("ProjectReference").Select(static reference => Path.GetFileName(reference.Attribute("Include")!.Value.Replace('\\', '/')))];
        Assert.Equal(["AgentJoules.Core.csproj", "AgentJoules.Metering.csproj"], references.Order(StringComparer.Ordinal));
        Assert.Equal("Microsoft.AspNetCore.App", Assert.Single(project.Descendants("FrameworkReference")).Attribute("Include")!.Value);
        Assert.Empty(project.Descendants("PackageReference"));

        string[] aspNetProjects =
        [
            .. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
                .Where(static path => File.ReadAllText(path).Contains("Microsoft.AspNetCore", StringComparison.Ordinal))
                .Select(static path => Path.GetFileName(path)),
        ];
        Assert.Equal(["AgentJoules.Dashboard.csproj"], aspNetProjects);
    }

    // ---- criterion 8: power forwarding ----

    [Fact]
    public async Task Power_forwarding_follows_interval_without_extra_nvml_calls()
    {
        MeterRun forwarded = await MeterOneSecondAsync(rateHz: 10.0, intervalMs: 100);
        MeterRun plain = await MeterOneSecondAsync(rateHz: 10.0, intervalMs: null);

        Assert.InRange(forwarded.Events.Count, 1, 10);

        // The start sample, the open edge, ten ticks and the close edge read power; the two edges read the counter. Forwarding adds no read.
        Assert.Equal((13, 2), (plain.PowerCalls, plain.CounterCalls));
        Assert.Equal(plain.PowerCalls, forwarded.PowerCalls);
        Assert.Equal(plain.CounterCalls, forwarded.CounterCalls);
        Assert.Equal(plain.Reading, forwarded.Reading);
        Assert.All(forwarded.Events, static power => Assert.Equal(Metric.Measured(60.0, "W"), power.Watts));
        Assert.Equal(forwarded.Events.Select(static power => power.SampleSeconds).Order(), forwarded.Events.Select(static power => power.SampleSeconds));
        Assert.All(forwarded.Events, static power => Assert.False(power.Replay));
    }

    [Fact]
    public async Task A_sampling_period_shorter_than_the_interval_forwards_at_most_one_sample_per_interval()
    {
        MeterRun run = await MeterOneSecondAsync(rateHz: 20.0, intervalMs: 100);

        Assert.InRange(run.Events.Count, 1, 10);
        double[] seconds = [.. run.Events.Select(static power => power.SampleSeconds)];
        Assert.All(seconds.Zip(seconds.Skip(1)), static pair => Assert.True(pair.Second - pair.First >= 0.1 - 1e-9, $"{pair.First} -> {pair.Second}"));
    }

    [Fact]
    public async Task Short_interval_forwards_every_sample()
    {
        MeterRun run = await MeterOneSecondAsync(rateHz: 5.0, intervalMs: 100);

        Assert.Equal([0.2, 0.4, 0.6, 0.8, 1.0], run.Events.Select(static power => Math.Round(power.SampleSeconds, 9)));
        Assert.Equal("Dashboard:PowerIntervalMs 100 is shorter than the sampling period of 200 ms; every power sample is forwarded.", PowerForwarding.ShortIntervalNote(100, 5.0));
    }

    [Theory]
    [InlineData(150, 7, 0.2)]
    [InlineData(250, 4, 0.3)]
    [InlineData(100, 10, 0.1)]
    public async Task An_interval_between_two_periods_forwards_at_the_next_whole_period(int intervalMs, int atMost, double minimumGapSeconds)
    {
        MeterRun run = await MeterOneSecondAsync(rateHz: 10.0, intervalMs: intervalMs);

        double[] seconds = [.. run.Events.Select(static power => power.SampleSeconds)];
        Assert.InRange(seconds.Length, 1, atMost);
        Assert.All(seconds.Zip(seconds.Skip(1)), pair => Assert.True(pair.Second - pair.First >= minimumGapSeconds - 1e-9, $"{pair.First} -> {pair.Second}"));
        if (intervalMs == 100)
        {
            Assert.Equal(10, seconds.Length);
        }
    }

    // A period that is not a whole number of ticks (1000 / 6 ms is 1,666,666 ticks) must not cost an exact multiple a whole extra period.
    [Theory]
    [InlineData(6.0, 1000, 12, 6)]
    [InlineData(30.0, 100, 30, 3)]
    [InlineData(6.0, 500, 12, 3)]
    [InlineData(60.0, 100, 60, 6)]
    [InlineData(7.0, 300, 14, 3)]
    [InlineData(10.0, 201, 20, 3)]
    public async Task An_interval_that_is_an_exact_multiple_of_the_period_takes_that_many_periods(double rateHz, int intervalMs, int ticks, int everyNth)
    {
        var time = new FakeTimeProvider(Origin);
        var api = new ScriptedNvmlApi(time) { ConstantPowerWatts = 60.0, CounterSupported = true };
        GpuOpenResult open = new NvmlGpuTelemetrySourceFactory(api).Open(0);
        Assert.True(open.IsAvailable, open.Reason);
        using IGpuTelemetrySource source = open.Source;
        var live = new RecordingLiveSink();
        var meter = new SampledEnergyMeter(source, MeterMethodSelector.Select("auto", source, rateHz), time, new PowerForwarding(live, intervalMs));
        await meter.StartAsync(CancellationToken.None);

        await TickAsync(meter, api, time, TimeSpan.FromMilliseconds(1000.0 / rateHz), ticks);
        await meter.DisposeAsync();

        double[] seconds = [.. live.Of<PowerEvent>().Select(static power => power.SampleSeconds)];
        Assert.Equal((ticks + everyNth - 1) / everyNth, seconds.Length);
        double gap = everyNth / rateHz;
        Assert.All(seconds.Zip(seconds.Skip(1)), pair => Assert.True(Math.Abs(pair.Second - pair.First - gap) < 1e-6, $"{pair.First} -> {pair.Second}, expected a gap of {gap}"));
    }

    [Fact]
    public async Task A_tick_that_follows_a_late_one_within_half_a_period_is_still_forwarded()
    {
        var time = new FakeTimeProvider(Origin);
        var api = new ScriptedNvmlApi(time) { ConstantPowerWatts = 60.0 };
        GpuOpenResult open = new NvmlGpuTelemetrySourceFactory(api).Open(0);
        Assert.True(open.IsAvailable, open.Reason);
        using IGpuTelemetrySource source = open.Source;
        var live = new RecordingLiveSink();
        var meter = new SampledEnergyMeter(source, MeterMethodSelector.Select("sampling", source, 10.0), time, new PowerForwarding(live, 100));
        await meter.StartAsync(CancellationToken.None);

        // A tick 130 ms in and the next one 70 ms later: the gap is within half a period of the interval, so both go.
        await TickAsync(meter, api, time, TimeSpan.FromMilliseconds(130), ticks: 1);
        await TickAsync(meter, api, time, TimeSpan.FromMilliseconds(70), ticks: 1);
        await meter.DisposeAsync();

        Assert.Equal([0.13, 0.2], live.Of<PowerEvent>().Select(static power => Math.Round(power.SampleSeconds, 9)));
    }

    [Fact]
    public void The_note_rounds_the_period_to_the_nearest_whole_millisecond()
    {
        Assert.Equal("Dashboard:PowerIntervalMs 100 is shorter than the sampling period of 167 ms; every power sample is forwarded.", PowerForwarding.ShortIntervalNote(100, 6.0));
    }

    [Theory]
    [InlineData(100, 10.0)]
    [InlineData(1000, 10.0)]
    [InlineData(50, 20.0)]
    public void An_interval_that_is_not_shorter_than_the_period_has_no_note(int intervalMs, double rateHz)
    {
        Assert.Null(PowerForwarding.ShortIntervalNote(intervalMs, rateHz));
    }

    [Fact]
    public void Power_forwarding_refuses_a_null_sink_and_an_interval_below_1_ms()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new PowerForwarding(null!, 100));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new PowerForwarding(new RecordingLiveSink(), 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => PowerForwarding.ShortIntervalNote(100, 0.0));
    }

    [Fact]
    public async Task Window_edges_and_failed_reads_are_never_forwarded()
    {
        var time = new FakeTimeProvider(Origin);
        var api = new ScriptedNvmlApi(time) { ConstantPowerWatts = 60.0, CounterSupported = true };
        GpuOpenResult open = new NvmlGpuTelemetrySourceFactory(api).Open(0);
        Assert.True(open.IsAvailable, open.Reason);
        using IGpuTelemetrySource source = open.Source;
        var live = new RecordingLiveSink();
        var meter = new SampledEnergyMeter(source, MeterMethodSelector.Select("auto", source, 10.0), time, new PowerForwarding(live, 50));
        await meter.StartAsync(CancellationToken.None);

        // An edge sample on each side of a window that no tick crosses forwards nothing.
        IMeterWindow window = meter.OpenWindow();
        time.Advance(TimeSpan.FromMilliseconds(40));
        _ = window.Close();

        // The second tick's power read fails: that tick is skipped, never forwarded as 0 W.
        api.ThrowOnPowerReadNumber = api.PowerCallCount + 2;
        await TickAsync(meter, api, time, TimeSpan.FromMilliseconds(60), ticks: 1);
        await TickAsync(meter, api, time, TimeSpan.FromMilliseconds(100), ticks: 2);
        await meter.DisposeAsync();

        Assert.Equal(1, meter.SkippedReadCount);
        PowerEvent[] events = [.. live.Of<PowerEvent>()];
        Assert.Equal([0.1, 0.3], events.Select(static power => Math.Round(power.SampleSeconds, 9)));
        Assert.All(events, static power => Assert.Equal(60.0, power.Watts.Value));
    }

    [Fact]
    public async Task Power_events_are_emitted_outside_the_meter_lock_and_a_throwing_sink_never_stops_the_loop()
    {
        var time = new FakeTimeProvider(Origin);
        var api = new ScriptedNvmlApi(time) { ConstantPowerWatts = 60.0 };
        GpuOpenResult open = new NvmlGpuTelemetrySourceFactory(api).Open(0);
        Assert.True(open.IsAvailable, open.Reason);
        using IGpuTelemetrySource source = open.Source;
        SampledEnergyMeter? meter = null;
        var probe = new LockProbingLiveSink(() => meter!.SampleCount);
        meter = new SampledEnergyMeter(source, MeterMethodSelector.Select("sampling", source, 10.0), time, new PowerForwarding(probe, 100));
        await meter.StartAsync(CancellationToken.None);

        await TickAsync(meter, api, time, TimeSpan.FromMilliseconds(100), ticks: 3);
        await meter.DisposeAsync();

        // Another thread could take the meter lock while every emit ran, and the sink's own exception never reached the loop.
        Assert.Equal(3, probe.Calls);
        Assert.Equal(3, probe.LockFree);
        Assert.Null(meter.LoopFault);
        Assert.True(meter.SampleCount >= 4);
    }

    [Fact]
    public void The_meter_refuses_nothing_new_without_forwarding()
    {
        var time = new FakeTimeProvider(Origin);
        var api = new ScriptedNvmlApi(time) { ConstantPowerWatts = 60.0 };
        GpuOpenResult open = new NvmlGpuTelemetrySourceFactory(api).Open(0);
        Assert.True(open.IsAvailable, open.Reason);
        using IGpuTelemetrySource source = open.Source;

        // The existing three-argument call sites compile and behave as before; the fourth argument is optional.
        var meter = new SampledEnergyMeter(source, MeterMethodSelector.Select("sampling", source, 10.0), time);

        Assert.Equal(0, meter.SampleCount);
    }

    // ---- criterion 9: streaming progress ----

    [Fact]
    public async Task Step_progress_throttled_to_10_per_second()
    {
        var time = new FakeTimeProvider(Origin);
        var live = new RecordingLiveSink();
        FakeChatClient inner = new(time) { Updates = [.. Enumerable.Range(0, 100).Select(static i => new FakeUpdate($"chunk {i} ", TimeSpan.FromMilliseconds(10)))] };
        (MeteredChatClient client, InMemoryMetricsSink sink, IEnergyMeter meter) = await HostedClientAsync(inner, time, live);

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Prompt).WithCancellation(CancellationToken.None))
        {
            _ = update;
        }

        await meter.DisposeAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), time.GetUtcNow() - Origin);
        StepProgressEvent[] progress = [.. live.Of<StepProgressEvent>()];
        Assert.InRange(progress.Length, 2, 10);
        Assert.Equal(1, progress[0].OutputChunks);
        Assert.All(progress.Zip(progress.Skip(1)), static pair => Assert.True(pair.Second.OutputChunks > pair.First.OutputChunks));
        Assert.All(progress.Zip(progress.Skip(1)), static pair => Assert.True(pair.Second.AtUtc - pair.First.AtUtc >= TimeSpan.FromMilliseconds(100)));
        Assert.All(progress, static item => Assert.Equal("Writer", item.Step.Agent));
        Assert.Equal(StepOutcomes.Ok, Assert.Single(sink.Snapshot()).Outcome);
    }

    [Fact]
    public async Task Updates_exactly_100_ms_apart_are_each_reported()
    {
        var time = new FakeTimeProvider(Origin);
        var live = new RecordingLiveSink();
        FakeChatClient inner = new(time) { Updates = [.. Enumerable.Range(0, 5).Select(static i => new FakeUpdate($"c{i}", TimeSpan.FromMilliseconds(100)))] };
        (MeteredChatClient client, _, IEnergyMeter meter) = await HostedClientAsync(inner, time, live);

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Prompt).WithCancellation(CancellationToken.None))
        {
            _ = update;
        }

        await meter.DisposeAsync();
        Assert.Equal([1, 2, 3, 4, 5], live.Of<StepProgressEvent>().Select(static item => item.OutputChunks));
    }

    [Fact]
    public async Task Updates_without_text_are_not_counted_and_reasoning_text_is()
    {
        var time = new FakeTimeProvider(Origin);
        var live = new RecordingLiveSink();
        FakeChatClient inner = new(time)
        {
            Updates =
            [
                new FakeUpdate("first ", TimeSpan.FromMilliseconds(10)),
                new FakeUpdate(string.Empty, TimeSpan.FromMilliseconds(200)),
                new FakeUpdate("thinking", TimeSpan.FromMilliseconds(200), Reasoning: true),
                new FakeUpdate("second", TimeSpan.FromMilliseconds(200)),
            ],
        };
        (MeteredChatClient client, _, IEnergyMeter meter) = await HostedClientAsync(inner, time, live);

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Prompt).WithCancellation(CancellationToken.None))
        {
            _ = update;
        }

        await meter.DisposeAsync();
        Assert.Equal([1, 2, 3], live.Of<StepProgressEvent>().Select(static item => item.OutputChunks));
    }

    [Fact]
    public async Task A_buffered_call_emits_no_step_progress()
    {
        var time = new FakeTimeProvider(Origin);
        var live = new RecordingLiveSink();
        (MeteredChatClient client, InMemoryMetricsSink sink, IEnergyMeter meter) = await HostedClientAsync(new FakeChatClient(time), time, live);

        _ = await client.GetResponseAsync(Prompt);

        await meter.DisposeAsync();

        // No progress; the only live event of the call is the step-started of its window (AJ-091).
        Assert.Empty(live.Of<StepProgressEvent>());
        _ = Assert.IsType<StepStartedEvent>(Assert.Single(live.Events));
        _ = Assert.Single(sink.Snapshot());
    }

    [Fact]
    public async Task A_throwing_live_sink_leaves_the_streamed_reading_unchanged()
    {
        var time = new FakeTimeProvider(Origin);
        IReadOnlyList<FakeUpdate> updates = [.. Enumerable.Range(0, 5).Select(static i => new FakeUpdate($"part {i} ", TimeSpan.FromMilliseconds(150)))];
        (MeteredChatClient broken, InMemoryMetricsSink brokenSink, IEnergyMeter brokenMeter) = await HostedClientAsync(new FakeChatClient(time) { Updates = updates }, time, new ThrowingLiveSink());
        await foreach (ChatResponseUpdate update in broken.GetStreamingResponseAsync(Prompt).WithCancellation(CancellationToken.None))
        {
            _ = update;
        }

        var reference = new FakeTimeProvider(Origin);
        (MeteredChatClient plain, InMemoryMetricsSink plainSink, IEnergyMeter plainMeter) = await HostedClientAsync(new FakeChatClient(reference) { Updates = updates }, reference, NullLiveEventSink.Instance);
        await foreach (ChatResponseUpdate update in plain.GetStreamingResponseAsync(Prompt).WithCancellation(CancellationToken.None))
        {
            _ = update;
        }

        await brokenMeter.DisposeAsync();
        await plainMeter.DisposeAsync();
        Assert.Equal(Assert.Single(plainSink.Snapshot()), Assert.Single(brokenSink.Snapshot()));
    }

    [Fact]
    public async Task The_metered_client_refuses_services_without_a_live_sink()
    {
        var time = new FakeTimeProvider(Origin);
        var meter = new UnmeteredEnergyMeter(time);
        MeteringServices services = new(meter, NoOpMeterGate.Instance, new InMemoryMetricsSink(), NotConfiguredCostCalculator.Instance, TestInstruments.Create(), time) { Live = null! };

        _ = Assert.Throws<ArgumentNullException>(() => new MeteredChatClient(new FakeChatClient(time), Identity(), services));
        await meter.DisposeAsync();
    }

    // ---- the orchestrator wiring (P-57) ----

    [Fact]
    public async Task A_dashboard_live_sink_receives_the_workflow_readings_and_the_power_samples_of_a_measured_run()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        var gpu = new ScriptedNvmlApi(time) { ConstantPowerWatts = 30.0 };
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
            {
                ["local"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)),

                // The judge tier answers its buffered calls with a score, so the production judge publishes judge readings that must never reach the dashboard.
                ["nvidia"] = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), Clock = time, DefaultDuration = TimeSpan.FromSeconds(1), ChatText = "4" },
            },
            ("AgentJoules:Dashboard:PowerIntervalMs", "300"));
        setup.Gpu = gpu;
        setup.UseProductionJudge = true;
        RunOrchestrationFixtures.UseClock(setup, time);
        var live = new RecordingLiveMetricsSink();
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<ILiveEventSink>();
            _ = services.AddSingleton<ILiveEventSink>(live);
        });

        CliRunResult result = await RunOrchestrationFixtures.RunDrivenAsync("run --tier local --task incident-brief --repeat 1 --baseline-seconds 2", setup, time).WaitAsync(HangGuard);

        Assert.True(result.ExitCode == 0, result.Stderr);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);

        // The dashboard receives the same readings as the results file (the listener sink first, the dashboard second), never a judge reading, although the judge ran.
        Assert.NotEmpty(document.RootElement.GetProperty("judge").EnumerateArray());
        Assert.Equal(steps.Length, live.Readings.Count);
        Assert.All(live.Readings, static reading => Assert.Null(reading.JudgedAgent));
        Assert.Equal(steps.Select(static step => step.GetProperty("agent").GetString()), live.Readings.Select(static reading => reading.Agent));

        // The measured tier forwards the samples its meter took; a measured value, never 0 W from a skipped read.
        PowerEvent[] power = [.. live.Of<PowerEvent>()];
        Assert.NotEmpty(power);
        Assert.All(power, static item => Assert.Equal(Metric.Measured(30.0, "W"), item.Watts));

        // The configured 300 ms at 10 Hz: every third sample at most, so no two forwarded samples are closer than 0.3 s.
        Assert.True(power.Length >= 5, $"{power.Length} power events");
        Assert.All(power.Zip(power.Skip(1)), static pair => Assert.True(pair.Second.SampleSeconds - pair.First.SampleSeconds >= 0.3 - 1e-9, $"{pair.First.SampleSeconds} -> {pair.Second.SampleSeconds}"));

        // The streaming workflow calls report their progress through the services of the tier run.
        StepProgressEvent[] progress = [.. live.Of<StepProgressEvent>()];
        Assert.NotEmpty(progress);
        string?[] agents = [.. steps.Select(static step => step.GetProperty("agent").GetString())];
        Assert.All(progress, item => Assert.Contains(item.Step.Agent, agents));
    }

    // ---- AJ-091 (REQ-032, EDGE-018, REQ-017): the loopback server, its port fallback and the run wiring. Every test that binds or holds a port uses LoopbackPortBlock;
    // the only system-clock tests are the latency pair, which measure real delivery (P-80) ----

    [Fact]
    [Trait("Spec", "EDGE-018")]
    public async Task Port_in_use_falls_back()
    {
        using LoopbackPortBlock held = LoopbackPortBlock.Reserve(1);
        var logger = new RecordingLogger();
        await using DashboardSink sink = DashboardFixtures.NewSink(logger, new FakeTimeProvider(Origin));
        await using var server = new DashboardServer(sink, new SingleLoggerFactory(logger));

        Uri? uri = await server.TryStartAsync(held.First, null, CancellationToken.None).WaitAsync(HangGuard);

        Assert.NotNull(uri);
        Assert.NotEqual(held.First, uri.Port);
        Assert.InRange(uri.Port, held.First + 1, held.First + DashboardServer.PortAttempts - 1);
        Assert.Equal(uri, server.Uri);
        Assert.Null(server.FailureReason);
        using var client = new HeadlessSseClient();
        Assert.Equal(200, (await client.GetAsync(uri, "/", null).WaitAsync(HangGuard)).Status);

        // The refused port is never reported at Warning or above (Kestrel's bind failure goes to Debug).
        Assert.DoesNotContain(logger.Entries, static entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Spec", "EDGE-018")]
    public async Task All_twenty_ports_busy_return_no_uri_and_name_the_range()
    {
        using LoopbackPortBlock held = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        var logger = new RecordingLogger();
        await using DashboardSink sink = DashboardFixtures.NewSink(logger, new FakeTimeProvider(Origin));
        await using var server = new DashboardServer(sink, new SingleLoggerFactory(logger));

        Uri? uri = await server.TryStartAsync(held.First, null, CancellationToken.None).WaitAsync(HangGuard);

        Assert.Null(uri);
        Assert.Null(server.Uri);
        Assert.Equal($"no free dashboard port in {held.First}-{held.Last}", server.FailureReason);
        Assert.Equal(DashboardServer.NoFreePortMessage(held.First), server.FailureReason);
        Assert.DoesNotContain(logger.Entries, static entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Spec", "EDGE-018")]
    public void Port_range_stops_at_65535()
    {
        Assert.Equal((65530, 65535), DashboardServer.PortRange(65530));
        Assert.Equal("no free dashboard port in 65530-65535", DashboardServer.NoFreePortMessage(65530));
        Assert.Equal((65535, 65535), DashboardServer.PortRange(65535));
        Assert.Equal((5178, 5197), DashboardServer.PortRange(DashboardServer.DefaultPort));
        Assert.Equal("no free dashboard port in 5178-5197", DashboardServer.NoFreePortMessage(DashboardServer.DefaultPort));
        Assert.Equal((5178, 20), (DashboardServer.DefaultPort, DashboardServer.PortAttempts));
        Assert.Equal((TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)), (DashboardServer.StartTimeout, DashboardServer.GracePeriod, DashboardServer.StopTimeout));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => DashboardServer.PortRange(0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => DashboardServer.PortRange(65536));
    }

    [Fact]
    public async Task The_printed_uri_is_the_loopback_address_built_by_the_uri_builder()
    {
        await using ServerRig rig = await ServerRig.StartAsync();

        Assert.Equal(new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), rig.Uri.Port).Uri, rig.Uri);
        Assert.Equal((Uri.UriSchemeHttp, "127.0.0.1", "/"), (rig.Uri.Scheme, rig.Uri.Host, rig.Uri.AbsolutePath));

        // The only listener on the port is the loopback one: a wildcard or IPv6 bind would show here on every OS.
        Assert.Equal([new IPEndPoint(IPAddress.Loopback, rig.Uri.Port)], ListenersOn(rig.Uri.Port));
    }

    [Fact]
    public async Task Security_headers_present()
    {
        await using ServerRig rig = await ServerRig.StartAsync();
        string page = DashboardPage.Render(null);
        string policy = "default-src 'none'; script-src 'sha256-" + Sha256Base64(Between(page, "<script>", "</script>")) + "'; style-src 'sha256-" + Sha256Base64(Between(page, "<style>", "</style>"))
            + "'; connect-src 'self'; img-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

        Assert.Equal(policy, DashboardServer.ContentSecurityPolicy(page));
        int[] statuses = await Task.WhenAll(SecurityPaths.Select(async path =>
        {
            HeadlessResponse response = await rig.Client.GetAsync(rig.Uri, path, null).WaitAsync(HangGuard);
            Assert.Equal(policy, response.Header("Content-Security-Policy"));
            Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
            Assert.DoesNotContain(response.Headers.Keys, static name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
            return response.Status;
        }));

        Assert.Equal([200, 200, 404], statuses);
    }

    [Fact]
    public async Task Foreign_host_gets_421()
    {
        await using ServerRig rig = await ServerRig.StartAsync();
        rig.Sink.Publish(StepReadingFixtures.Metered());
        await rig.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        int port = rig.Uri.Port;
        string[] foreign = ["evil.example", $"evil.example:{port}", $"127.0.0.1:{port + 1}", "localhost", "127.0.0.1", $"localhost.evil.example:{port}"];

        foreach (string path in RootAndEvents)
        {
            foreach (string host in foreign)
            {
                HeadlessResponse response = await rig.Client.GetAsync(rig.Uri, path, host).WaitAsync(HangGuard);
                Assert.True(response.Status == 421, $"{path} with Host {host}: {response.Status}");
                Assert.Empty(response.BodyBytes);
                Assert.NotNull(response.Header("Content-Security-Policy"));
                Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
            }
        }

        // The two loopback names of the port pass, in any case.
        string[] loopback = [$"127.0.0.1:{port}", $"localhost:{port}", $"LOCALHOST:{port}"];
        foreach (string host in loopback)
        {
            Assert.Equal(200, (await rig.Client.GetAsync(rig.Uri, "/", host).WaitAsync(HangGuard)).Status);
        }
    }

    [Fact]
    public async Task A_foreign_origin_or_a_cross_site_fetch_gets_403_and_no_cors_header()
    {
        await using ServerRig rig = await ServerRig.StartAsync();
        string own = Uri.UriSchemeHttp + Uri.SchemeDelimiter + "127.0.0.1:" + rig.Uri.Port.ToString(CultureInfo.InvariantCulture);
        string ownByName = Uri.UriSchemeHttp + Uri.SchemeDelimiter + "localhost:" + rig.Uri.Port.ToString(CultureInfo.InvariantCulture);
        string foreign = new UriBuilder(Uri.UriSchemeHttps, "evil.example").Uri.GetLeftPart(UriPartial.Authority);

        // Absent headers: a typed URL or a tool.
        Assert.Equal(200, (await rig.Client.GetAsync(rig.Uri, "/", null).WaitAsync(HangGuard)).Status);

        // same-site is a page on another loopback port: refused, so it cannot exhaust the stream slots.
        (string Name, string Value)[] refused = [("Origin", foreign), ("Origin", "null"), ("Origin", own + "0"), ("Sec-Fetch-Site", "cross-site"), ("Sec-Fetch-Site", "same-site"), ("Sec-Fetch-Site", "bogus")];
        foreach (string path in RootAndEvents)
        {
            foreach ((string name, string value) in refused)
            {
                HeadlessResponse response = await rig.Client.GetAsync(rig.Uri, path, null, [new(name, value)]).WaitAsync(HangGuard);
                Assert.True(response.Status == 403, $"{path} with {name} {value}: {response.Status}");
                Assert.Empty(response.BodyBytes);
                Assert.NotNull(response.Header("Content-Security-Policy"));
                Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
                Assert.DoesNotContain(response.Headers.Keys, static header => header.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
            }
        }

        (string Name, string Value)[] accepted = [("Origin", own), ("Origin", ownByName), ("Sec-Fetch-Site", "same-origin"), ("Sec-Fetch-Site", "none")];
        foreach ((string name, string value) in accepted)
        {
            HeadlessResponse response = await rig.Client.GetAsync(rig.Uri, "/", null, [new(name, value)]).WaitAsync(HangGuard);
            Assert.True(response.Status == 200, $"{name} {value}: {response.Status}");
            Assert.DoesNotContain(response.Headers.Keys, static header => header.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Get_root_serves_the_rendered_page_byte_for_byte()
    {
        Uri repository = new UriBuilder(Uri.UriSchemeHttps, "repo.fake.test") { Path = "/owner/agent-joules" }.Uri;
        await using ServerRig linked = await ServerRig.StartAsync(repository);
        await using ServerRig unlinked = await ServerRig.StartAsync();

        HeadlessResponse withLink = await linked.Client.GetAsync(linked.Uri, "/", null).WaitAsync(HangGuard);
        HeadlessResponse withoutLink = await unlinked.Client.GetAsync(unlinked.Uri, "/", null).WaitAsync(HangGuard);

        Assert.Equal((200, "text/html; charset=utf-8"), (withLink.Status, withLink.Header("Content-Type")));
        Assert.Equal(Encoding.UTF8.GetBytes(DashboardPage.Render(repository)), withLink.BodyBytes);
        Assert.Contains("#readme", withLink.Body, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes(DashboardPage.Render(null)), withoutLink.BodyBytes);
        Assert.Contains(DashboardPage.RepositoryNotConfigured, withoutLink.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_start_with_the_snapshot_and_send_each_payload_as_a_raw_data_line()
    {
        await using ServerRig rig = await ServerRig.StartAsync();
        rig.Sink.Publish(StepReadingFixtures.Metered());
        await rig.Sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        using var guard = new CancellationTokenSource(HangGuard);

        await using HeadlessSseConnection stream = await rig.Client.OpenEventsAsync(rig.Uri, cancellationToken: guard.Token);
        await using IAsyncEnumerator<SseReceipt> events = stream.ReadEventsAsync(guard.Token).GetAsyncEnumerator(guard.Token);

        Assert.Equal((200, "text/event-stream"), (stream.Status, stream.ContentType));
        Assert.NotNull(stream.Headers.GetValueOrDefault("Content-Security-Policy"));
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(("aj", "snapshot"), (events.Current.EventType, events.Current.Type));
        Assert.Equal(["event: aj", "data: " + events.Current.Json], events.Current.Lines);
        Assert.StartsWith("data: {\"type\":\"snapshot\"", events.Current.Lines[1], StringComparison.Ordinal);

        // A payload written after the snapshot: one data line holding the envelope exactly as the sink wrote it, raw JSON and not a quoted string.
        using DashboardConnection reference = rig.Sink.Connect();
        rig.Sink.Publish(StepReadingFixtures.Metered(agent: "Writer"));
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(("aj", "step-completed"), (events.Current.EventType, events.Current.Type));
        Assert.True(await reference.Events.WaitToReadAsync(guard.Token));
        Assert.True(reference.Events.TryRead(out string? payload));
        Assert.Equal(["event: aj", "data: " + payload], events.Current.Lines);
        Assert.StartsWith("data: {", events.Current.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_arrive_within_250ms()
    {
        // The latency check of REQ-032 R1 on the system clock (P-80): real sink, real server, real loopback socket.
        await using ServerRig rig = await ServerRig.StartAsync(time: TimeProvider.System);

        IReadOnlyList<SseReceipt> receipts = await DashboardLatencyRun.RunAsync(rig.Client, rig.Uri, rig.Sink, TimeProvider.System, HangGuard).WaitAsync(HangGuard);

        LatencyResult result = HeadlessSseClient.CheckLatency(receipts, DashboardLatencyRun.Budget);
        Assert.True(result.Passed, result.Message);
        Assert.True(receipts.Count(static receipt => receipt.Type != "snapshot") >= 50, result.Message);
        Assert.True(receipts.Count(static receipt => receipt.Type == "power") >= 10, result.Message);
    }

    [Fact]
    public async Task A_delayed_delivery_fails_the_latency_check()
    {
        await using ServerRig rig = await ServerRig.StartAsync(time: TimeProvider.System);
        var delayed = new DelayingLiveEventSink(rig.Sink, TimeSpan.FromMilliseconds(400), TimeProvider.System);

        IReadOnlyList<SseReceipt> receipts = await DashboardLatencyRun.RunAsync(rig.Client, rig.Uri, delayed, TimeProvider.System, HangGuard).WaitAsync(HangGuard);

        LatencyResult result = HeadlessSseClient.CheckLatency(receipts, DashboardLatencyRun.Budget);
        Assert.False(result.Passed, result.Message);
        Assert.True(result.MaximumMs > 250.0, result.Message);
        Assert.Contains("exceeds the budget of 250 ms", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_beyond_the_cap_gets_503_and_the_cap_is_logged_once()
    {
        await using ServerRig rig = await ServerRig.StartAsync();
        var streams = new List<HeadlessSseConnection>();
        try
        {
            for (int i = 0; i < DashboardServer.MaxClients; i++)
            {
                HeadlessSseConnection stream = await rig.Client.OpenEventsAsync(rig.Uri).WaitAsync(HangGuard);
                streams.Add(stream);
                Assert.Equal((200, "text/event-stream"), (stream.Status, stream.ContentType));
            }

            for (int i = 0; i < 2; i++)
            {
                HeadlessResponse refused = await rig.Client.GetAsync(rig.Uri, "/events", null).WaitAsync(HangGuard);
                Assert.Equal(503, refused.Status);
                Assert.Empty(refused.BodyBytes);
                Assert.NotNull(refused.Header("Content-Security-Policy"));
                Assert.Equal("nosniff", refused.Header("X-Content-Type-Options"));
            }

            RecordedLogEntry warning = Assert.Single(rig.Logger.At(LogLevel.Warning));
            Assert.Contains("16", warning.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", warning.Message, StringComparison.Ordinal);

            // A client that leaves frees its place.
            await streams[0].DisposeAsync();
            streams.RemoveAt(0);
            HeadlessResponse admitted;
            int attempts = 0;
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20));
                admitted = await rig.Client.GetAsync(rig.Uri, "/events", null).WaitAsync(HangGuard);
            }
            while (admitted.Status == 503 && ++attempts < 500);

            Assert.Equal(200, admitted.Status);
        }
        finally
        {
            foreach (HeadlessSseConnection stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_page_whose_link_would_publish_a_registered_key_is_refused()
    {
        string key = string.Concat("qk4r", "8vz2", "mw7p", "x3ty");
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(key));
        Uri repository = new UriBuilder(Uri.UriSchemeHttps, "repo.fake.test") { Path = "/owner/" + key }.Uri;
        using LoopbackPortBlock block = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        block.Release();
        var logger = new RecordingLogger();
        await using DashboardSink sink = DashboardFixtures.NewSink(logger, new FakeTimeProvider(Origin), redactor);
        await using var server = new DashboardServer(sink, new SingleLoggerFactory(logger));

        Uri? uri = await server.TryStartAsync(block.First, repository, CancellationToken.None).WaitAsync(HangGuard);

        Assert.Null(uri);
        Assert.Equal(DashboardServer.SecretInPageMessage, server.FailureReason);
        Assert.DoesNotContain(key, server.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain("repo.fake.test", server.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(key, StringComparison.Ordinal));

        // Nothing was bound.
        using LoopbackPortBlock? free = LoopbackPortBlock.TryHold(block.First);
        Assert.NotNull(free);
    }

    [Fact]
    public async Task A_second_start_throws_and_disposal_releases_the_port_and_is_idempotent()
    {
        await using ServerRig rig = await ServerRig.StartAsync();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Server.TryStartAsync(rig.Uri.Port, null, CancellationToken.None));
        await rig.Server.DisposeAsync();
        await rig.Server.DisposeAsync();

        using LoopbackPortBlock? free = LoopbackPortBlock.TryHold(rig.Uri.Port);
        Assert.NotNull(free);
    }

    [Fact]
    public async Task Dispose_with_a_connected_client_ends_its_stream_within_the_stop_timeout()
    {
        await using ServerRig rig = await ServerRig.StartAsync();
        using var guard = new CancellationTokenSource(HangGuard);
        await using HeadlessSseConnection stream = await rig.Client.OpenEventsAsync(rig.Uri, cancellationToken: guard.Token);
        await using IAsyncEnumerator<SseReceipt> events = stream.ReadEventsAsync(guard.Token).GetAsyncEnumerator(guard.Token);
        Assert.True(await events.MoveNextAsync());

        // Under 1 s: ApplicationStopping ends the open stream at once; without it the stop would wait for its 2 s bound (design 3.10, mutants M5 and M6).
        await rig.Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        bool ended;
        try
        {
            ended = !await events.MoveNextAsync();
        }
        catch (Exception failure) when (failure is IOException or HttpRequestException)
        {
            ended = true;
        }

        Assert.True(ended);
    }

    [Fact]
    public async Task A_cancelled_start_throws_and_binds_nothing()
    {
        using LoopbackPortBlock block = LoopbackPortBlock.Reserve(1);
        block.Release();
        await using DashboardSink sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        await using var server = new DashboardServer(sink, NullLoggerFactory.Instance);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.TryStartAsync(block.First, null, cancelled.Token));

        Assert.Null(server.Uri);
        using LoopbackPortBlock? free = LoopbackPortBlock.TryHold(block.First);
        Assert.NotNull(free);
    }

    [Fact]
    public async Task The_server_refuses_null_arguments_and_a_port_outside_the_range_and_disposes_before_start()
    {
        await using DashboardSink sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        _ = Assert.Throws<ArgumentNullException>(() => new DashboardServer(null!, NullLoggerFactory.Instance));
        _ = Assert.Throws<ArgumentNullException>(() => new DashboardServer(sink, null!));
        await using var server = new DashboardServer(sink, NullLoggerFactory.Instance);

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => server.TryStartAsync(0, null, CancellationToken.None));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => server.TryStartAsync(65536, null, CancellationToken.None));
        await server.DisposeAsync();
        Assert.Null(server.Uri);
    }

    // ---- AJ-091 run wiring (T5) ----

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("70000")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("+80")]
    public async Task An_invalid_dashboard_port_exits_2_before_any_host(string port)
    {
        var setup = new RunCommandSetup();
        setup.Settings[ProviderClientFixtures.NvidiaKeyVariable] = ProviderClientFixtures.FakeKey();

        CliRunResult result = await RunCommandHarness.RunAsync($"run --tier nvidia --dashboard --dashboard-port {port} --dry-run", setup).WaitAsync(HangGuard);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, setup.HostBuilds);
        Assert.Contains(DashboardPortOption.RangeMessage, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Dashboard:", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_with_the_dashboard_prints_the_loopback_url_disposes_at_once_and_exits_0()
    {
        using LoopbackPortBlock block = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        block.Release();
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler local = RunOrchestrationFixtures.Endpoint();
        RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["local"] = local, ["nvidia"] = RunOrchestrationFixtures.Endpoint() });
        setup.Gpu = new ScriptedNvmlApi(new FakeTimeProvider(Origin));

        CliRunResult result = await RunCommandHarness.RunAsync($"run --tier local --dashboard --dry-run --dashboard-port {block.First}", setup).WaitAsync(HangGuard);

        Assert.True(result.ExitCode == 0, result.Stderr);
        string[] lines = RunOrchestrationFixtures.StdoutLines(result);
        string line = Assert.Single(lines, static line => line.StartsWith("Dashboard:", StringComparison.Ordinal));
        Assert.Equal(lines[^1], line);
        Assert.StartsWith("local ", lines[0], StringComparison.Ordinal);
        Uri printed = new(line["Dashboard: ".Length..]);
        Assert.Equal(new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), printed.Port).Uri, printed);
        Assert.True(block.Contains(printed.Port), line);
        Assert.Equal(0, local.Count(FakeRoute.ChatCompletions));
        Assert.DoesNotContain(ReportSentences.GpuBoardOnly, result.Stdout, StringComparison.Ordinal);
        Assert.Empty(RunOrchestrationFixtures.EntriesIn(workspace.Root));

        // Disposed at once: the port is free again when the command returned.
        using LoopbackPortBlock? free = LoopbackPortBlock.TryHold(printed.Port);
        Assert.NotNull(free);
    }

    [Fact]
    [Trait("Spec", "EDGE-018")]
    public async Task A_run_whose_preferred_port_is_taken_serves_the_next_port_and_completes()
    {
        using LoopbackPortBlock held = LoopbackPortBlock.Reserve(1);
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        (RunCommandSetup setup, TimerTrackingTimeProvider time) = HostedDashboardRun(workspace, ("AgentJoules:Dashboard:PowerIntervalMs", "50"));

        CliRunResult result = await RunOrchestrationFixtures.RunDrivenAsync($"run --tier nvidia --task {DashboardTask} --repeat 1 --dashboard --dashboard-port {held.First}", setup, time).WaitAsync(HangGuard);

        Assert.True(result.ExitCode == 0, result.Stderr);
        string[] lines = RunOrchestrationFixtures.StdoutLines(result);
        int dashboard = Array.FindIndex(lines, static line => line.StartsWith("Dashboard: ", StringComparison.Ordinal));
        Assert.True(dashboard >= 0, result.Stdout);
        Uri printed = new(lines[dashboard]["Dashboard: ".Length..]);
        Assert.InRange(printed.Port, held.First + 1, held.First + DashboardServer.PortAttempts - 1);

        // Before the scope sentence, which stays the last line before the first step line (T-5).
        int scope = Array.IndexOf(lines, ReportSentences.GpuBoardOnly);
        int firstStep = Array.FindIndex(lines, static line => line.StartsWith(DashboardTask + " r1 ", StringComparison.Ordinal));
        Assert.True(dashboard < scope && scope == firstStep - 1, result.Stdout);
        // A hosted-only run samples nothing, so the note of a short power interval is not printed (P-74 as amended).
        Assert.DoesNotContain(PowerForwarding.ShortIntervalNote(50, 10.0)!, result.StderrLines);
        AssertCompletedFile(workspace);
    }

    [Fact]
    [Trait("Spec", "EDGE-018")]
    public async Task A_run_with_all_twenty_ports_busy_warns_continues_without_a_dashboard_and_exits_0()
    {
        using LoopbackPortBlock held = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        (RunCommandSetup setup, TimerTrackingTimeProvider time) = HostedDashboardRun(workspace);

        CliRunResult result = await RunOrchestrationFixtures.RunDrivenAsync($"run --tier nvidia --task {DashboardTask} --repeat 1 --dashboard --dashboard-port {held.First}", setup, time).WaitAsync(HangGuard);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.DoesNotContain(RunOrchestrationFixtures.StdoutLines(result), static line => line.StartsWith("Dashboard:", StringComparison.Ordinal));
        Assert.Contains($"Warning: the dashboard did not start ({DashboardServer.NoFreePortMessage(held.First)}); the run continues without it", result.StderrLines);
        Assert.Empty(result.ErrorLines);
        AssertCompletedFile(workspace);
    }

    [Fact]
    public async Task Without_the_dashboard_flag_no_sink_no_server_and_no_line()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nvidia"] = RunOrchestrationFixtures.Endpoint() });
        DashboardSink? registered = null;
        ILiveEventSink? live = null;
        CliHostOverrides overrides = setup.ToOverrides(workspace.Root) with
        {
            OnHostStarted = host =>
            {
                registered = host.Services.GetService<DashboardSink>();
                live = host.Services.GetRequiredService<ILiveEventSink>();
            },
        };

        CliRunResult result = await CliHarness.RunAsync($"run --tier nvidia --task {DashboardTask} --repeat 1", overrides).WaitAsync(HangGuard);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Null(registered);
        Assert.Same(NullLiveEventSink.Instance, live);
        Assert.DoesNotContain("Dashboard", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("dashboard", result.Stderr, StringComparison.OrdinalIgnoreCase);
        AssertCompletedFile(workspace);
    }

    [Fact]
    public async Task With_the_dashboard_flag_the_run_registers_the_dashboard_sink_as_its_live_sink()
    {
        using LoopbackPortBlock block = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        block.Release();
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        (RunCommandSetup setup, TimerTrackingTimeProvider time) = HostedDashboardRun(workspace);
        DashboardSink? registered = null;
        ILiveEventSink? live = null;
        CliHostOverrides overrides = setup.ToOverrides(workspace.Root) with
        {
            OnHostStarted = host =>
            {
                registered = host.Services.GetService<DashboardSink>();
                live = host.Services.GetRequiredService<ILiveEventSink>();
            },
        };

        Task<CliRunResult> run = CliHarness.RunAsync($"run --tier nvidia --task {DashboardTask} --repeat 1 --dashboard --dashboard-port {block.First}", overrides);
        await FakeTimeDriver.RunWaitsToCompletionAsync(run, time, maxStep: RunOrchestrationFixtures.SamplingPeriod).WaitAsync(HangGuard);
        CliRunResult result = await run;

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.NotNull(registered);
        Assert.Same(registered, live);
        Assert.Equal(0, registered.PumpFaultCount);
        AssertCompletedFile(workspace);
    }

    // ---- AJ-091 producers (T6) ----

    [Fact]
    public async Task A_measured_run_emits_run_started_baseline_progress_step_started_and_run_completed()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        var gpu = new ScriptedNvmlApi(time) { ConstantPowerWatts = 30.0 };
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
            {
                ["local"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)),
                ["nvidia"] = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), Clock = time, DefaultDuration = TimeSpan.FromSeconds(1), ChatText = "4" },
            });
        setup.Gpu = gpu;
        setup.UseProductionJudge = true;
        RunOrchestrationFixtures.UseClock(setup, time);
        var live = new RecordingLiveMetricsSink();
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<ILiveEventSink>();
            _ = services.AddSingleton<ILiveEventSink>(live);
        });

        CliRunResult result = await RunOrchestrationFixtures.RunDrivenAsync($"run --tier local --task {DashboardTask} --repeat 2 --baseline-seconds 2", setup, time).WaitAsync(HangGuard);

        Assert.True(result.ExitCode == 0, result.Stderr);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        LiveEvent[] events = [.. live.Events.Where(static item => item is not PowerEvent and not StepProgressEvent)];

        // The order of one tier run: run-started (the GPU known, before the warm-up and the baseline), the baseline, then per (task, repeat) its progress and its steps, then run-completed.
        RunStartedEvent started = Assert.IsType<RunStartedEvent>(events[0]);
        BaselineEvent baseline = Assert.IsType<BaselineEvent>(events[1]);
        Assert.IsType<RunCompletedEvent>(events[^1]);
        Assert.Equal((Tier.Local, RunStatus.Running), (started.Run.Tier, started.Run.Status));
        Assert.Equal(document.RootElement.GetProperty("probe").GetProperty("powerLimitW").GetDouble(), started.PowerLimitW);
        Assert.Equal(document.RootElement.GetProperty("baseline").GetProperty("meanW").GetProperty("value").GetDouble(), baseline.Baseline.MeanW.Value);
        Assert.All(events, static item => Assert.False(item.Replay));

        ProgressEvent[] progress = [.. events.OfType<ProgressEvent>()];
        Assert.Equal([(DashboardTask, 1, 1, 1, 2), (DashboardTask, 1, 1, 2, 2)], progress.Select(static item => (item.Task, item.TaskIndex, item.TaskCount, item.Repeat, item.RepeatCount)));

        // One step-started per workflow step and none for the judge, whose readings never reach the dashboard; each at its window's start, before its completion.
        StepStartedEvent[] steps = [.. events.OfType<StepStartedEvent>()];
        Assert.Equal(RunOrchestrationFixtures.Steps(document).Length, steps.Length);
        Assert.All(steps, static item => Assert.False(item.Step.IsJudge));
        Assert.Equal(live.Readings.Select(static reading => (reading.Agent, reading.Repeat, reading.Window!.Value.StartSeconds)), steps.Select(static item => (item.Step.Agent, item.Step.Repeat, item.StartSeconds)));
        Assert.True(Array.IndexOf(events, progress[1]) > Array.IndexOf(events, steps[3]), "The second progress event comes after the four steps of the first repeat.");

        // Progress k comes before the first step of repeat k (mutant M3: the progress after the repeat).
        Assert.True(Array.IndexOf(events, progress[0]) < Array.IndexOf(events, steps[0]), "The first progress event precedes the first step.");
        Assert.True(Array.IndexOf(events, progress[1]) < Array.IndexOf(events, steps[4]), "The second progress event precedes the first step of the second repeat.");

        RunCompletedEvent finished = Assert.Single(events.OfType<RunCompletedEvent>());
        Assert.Equal(RunStatus.Completed, finished.Run.Status);
        Assert.Equal(document.RootElement.GetProperty("totals").GetProperty("netJoules").GetProperty("value").GetDouble(), finished.Totals.NetJoules.Value);
        _ = Assert.Single(events.OfType<RunStartedEvent>());
        _ = Assert.Single(events.OfType<BaselineEvent>());
    }

    [Fact]
    public async Task A_hosted_tier_emits_one_not_applicable_power_event_right_after_run_started()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nvidia"] = RunOrchestrationFixtures.Endpoint() });
        var live = new RecordingLiveMetricsSink();
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<ILiveEventSink>();
            _ = services.AddSingleton<ILiveEventSink>(live);
        });

        CliRunResult result = await RunCommandHarness.RunAsync($"run --tier nvidia --task {DashboardTask} --repeat 1", setup).WaitAsync(HangGuard);

        Assert.True(result.ExitCode == 0, result.Stderr);
        LiveEvent[] events = [.. live.Events.Where(static item => item is not StepProgressEvent)];
        RunStartedEvent started = Assert.IsType<RunStartedEvent>(events[0]);
        Assert.Equal((Tier.Nvidia, null), (started.Run.Tier, started.PowerLimitW));
        PowerEvent power = Assert.IsType<PowerEvent>(events[1]);
        Assert.Equal((Metric.NotApplicable(MeterCapabilities.HostedTierReason), 0.0, false), (power.Watts, power.SampleSeconds, power.Replay));
        _ = Assert.Single(events.OfType<PowerEvent>());
        Assert.Empty(events.OfType<BaselineEvent>());
        Assert.Equal(RunStatus.Completed, Assert.IsType<RunCompletedEvent>(events[^1]).Run.Status);
        Assert.Equal(4, events.OfType<StepStartedEvent>().Count());
    }

    [Fact]
    public async Task A_failed_tier_emits_run_completed_with_the_failed_document()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint();
        nvidia.SetStatuses(FakeRoute.ChatCompletions, 500);
        RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nvidia"] = nvidia });
        var live = new RecordingLiveMetricsSink();
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<ILiveEventSink>();
            _ = services.AddSingleton<ILiveEventSink>(live);
        });

        CliRunResult result = await RunCommandHarness.RunAsync($"run --tier nvidia --task {DashboardTask} --repeat 1", setup).WaitAsync(HangGuard);

        Assert.Equal(5, result.ExitCode);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        RunCompletedEvent finished = Assert.Single(live.Of<RunCompletedEvent>());
        Assert.Equal(RunStatus.Failed, finished.Run.Status);
        Assert.Equal("failed", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.IsType<RunCompletedEvent>(live.Events[^1]);
    }

    [Fact]
    public async Task Opening_a_window_emits_one_step_started_event_at_the_window_start()
    {
        var time = new FakeTimeProvider(Origin);
        var live = new RecordingLiveSink();
        (MeteredChatClient client, InMemoryMetricsSink sink, IEnergyMeter meter) = await HostedClientAsync(new FakeChatClient(time), time, live);
        time.Advance(TimeSpan.FromMilliseconds(1500));

        _ = await client.GetResponseAsync(Prompt);

        await meter.DisposeAsync();
        StepReading reading = Assert.Single(sink.Snapshot());
        StepStartedEvent started = Assert.IsType<StepStartedEvent>(Assert.Single(live.Events));
        Assert.Equal(Identity(), started.Step);
        Assert.Equal(reading.Window!.Value.StartSeconds, started.StartSeconds);
        Assert.Equal(1.5, started.StartSeconds, 9);
        Assert.Equal((reading.StartUtc, false), (started.AtUtc, started.Replay));
    }

    [Fact]
    public async Task A_judge_call_emits_no_step_started_event()
    {
        var time = new FakeTimeProvider(Origin);
        var live = new RecordingLiveSink();
        var meter = new UnmeteredEnergyMeter(time);
        await meter.StartAsync(CancellationToken.None);
        MeteringServices services = new(meter, NoOpMeterGate.Instance, new InMemoryMetricsSink(), NotConfiguredCostCalculator.Instance, TestInstruments.Create(), time) { Live = live };
        var client = new MeteredChatClient(new FakeChatClient(time), Identity() with { IsJudge = true, JudgedAgent = "Writer" }, services);

        _ = await client.GetResponseAsync(Prompt);

        await meter.DisposeAsync();
        Assert.Empty(live.Of<StepStartedEvent>());
    }

    // ---- AJ-091 fix round ----

    [Fact]
    public async Task Dispose_async_is_bounded_on_the_sink_clock_while_a_logger_holds_the_pump()
    {
        // The pump's first fault is logged by a logger that blocks on a gate; the bound runs on the injected fake clock (P-42 as amended, mutant M8).
        var time = new FakeTimeProvider(Origin);
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var sink = DashboardFixtures.NewSink(new CallbackLogger(() =>
        {
            entered.Set();
            _ = gate.Wait(HangGuard);
        }), time);
        using DashboardConnection connection = sink.Connect();
        Task pump = Field<Task>(sink, "_pump");
        try
        {
            sink.Emit(new UnknownEvent(Origin));
            Assert.True(entered.Wait(HangGuard), "The pump never reached the logger.");

            Task disposing = sink.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            time.Advance(DashboardSink.DisposeTimeout);

            // At most 500 ms of real time: the bound must run on the injected clock, not on the system clock (mutant M8c).
            await disposing.WaitAsync(TimeSpan.FromMilliseconds(500));

            Assert.Equal(TimeSpan.FromSeconds(2), DashboardSink.DisposeTimeout);
            while (connection.Events.TryRead(out _))
            {
            }

            await connection.Events.Completion.WaitAsync(HangGuard);
            Assert.False(pump.IsCompleted, "The pump is still held by the logger.");
        }
        finally
        {
            gate.Set();
        }

        await pump.WaitAsync(HangGuard);
        await sink.DisposeAsync();
    }

    [Fact]
    [Trait("Spec", "EDGE-018")]
    public void A_refused_port_moves_on_and_any_other_failure_ends_the_start()
    {
        // The Windows reserved-port case surfaces bare (measured) or wrapped; both move on, as address-in-use does.
        Assert.True(DashboardServer.IsPortRefused(new AddressInUseException("taken")));
        Assert.True(DashboardServer.IsPortRefused(new IOException("bind", new AddressInUseException("taken", new SocketException((int)SocketError.AddressAlreadyInUse)))));
        Assert.True(DashboardServer.IsPortRefused(new SocketException((int)SocketError.AccessDenied)));
        Assert.True(DashboardServer.IsPortRefused(new IOException("bind", new SocketException((int)SocketError.AccessDenied))));
        Assert.True(DashboardServer.IsPortRefused(new SocketException((int)SocketError.AddressAlreadyInUse)));
        Assert.True(DashboardServer.IsPortRefused(new AggregateException(new InvalidDataException("other"), new SocketException((int)SocketError.AccessDenied))));

        // Anything else ends the start with start failed (<type>).
        Assert.False(DashboardServer.IsPortRefused(new InvalidOperationException("reloadConfigOnChange")));
        Assert.False(DashboardServer.IsPortRefused(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.False(DashboardServer.IsPortRefused(new IOException("disk")));
    }

    [Fact]
    public void The_address_check_admits_exactly_the_loopback_address_of_the_port()
    {
        string http = Uri.UriSchemeHttp + Uri.SchemeDelimiter;

        Assert.True(DashboardServer.ListensOnlyOnLoopback([http + "127.0.0.1:5200"], 5200));
        Assert.False(DashboardServer.ListensOnlyOnLoopback([http + "127.0.0.2:5200"], 5200));
        Assert.False(DashboardServer.ListensOnlyOnLoopback([http + "127.0.0.1:5201"], 5200));
        Assert.False(DashboardServer.ListensOnlyOnLoopback([http + "localhost:5200"], 5200));
        Assert.False(DashboardServer.ListensOnlyOnLoopback([http + "127.0.0.1:5200", http + "127.0.0.2:5200"], 5200));
        Assert.False(DashboardServer.ListensOnlyOnLoopback([], 5200));
    }

    [Fact]
    public async Task A_cancellation_during_the_attempts_is_thrown_not_reported()
    {
        // Every port busy; the first record of a failed attempt (Kestrel's bind failure, forwarded at Debug) cancels the command token, so the loop sees the cancellation mid-way (mutant M39).
        using LoopbackPortBlock held = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        using var cancellation = new CancellationTokenSource();
        await using DashboardSink sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        await using var server = new DashboardServer(sink, new SingleLoggerFactory(new CallbackLogger(cancellation.Cancel)));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.TryStartAsync(held.First, null, cancellation.Token)).WaitAsync(HangGuard);

        Assert.Null(server.Uri);
        Assert.Null(server.FailureReason);
    }

    [Fact]
    public async Task A_dispose_racing_the_start_never_leaves_a_listener()
    {
        // A real race (the gatekeeper's R3 shape): the start on one pool thread, the dispose from another after a random 0 to 40 ms, 60 rounds (mutant N10: the disposal re-check after the
        // publish removed leaves a listener in some rounds). The delay only spreads the interleavings; nothing is timed.
        var random = new Random(7);
        for (int round = 0; round < 60; round++)
        {
            using LoopbackPortBlock block = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
            block.Release();
            await using DashboardSink sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
            var server = new DashboardServer(sink, NullLoggerFactory.Instance);
            int delayMs = random.Next(0, 40);

            Task<Uri?> start = Task.Run(() => server.TryStartAsync(block.First, null, CancellationToken.None));
            var spread = Stopwatch.StartNew();
            while (spread.Elapsed.TotalMilliseconds < delayMs)
            {
                Thread.SpinWait(50);
            }

            Task dispose = Task.Run(async () => await server.DisposeAsync());
            try
            {
                _ = await start.WaitAsync(HangGuard);
            }
            catch (ObjectDisposedException)
            {
                // The disposal overtook the start: the start stopped its own application.
            }

            await dispose.WaitAsync(HangGuard);

            // Whichever side took the application stopped it: nothing listens on the block.
            Assert.False(Enumerable.Range(block.First, block.Count).SelectMany(ListenersOn).Any(), $"Round {round} (delay {delayMs} ms) left a listener.");
        }
    }

    [Fact]
    public async Task A_failing_event_stream_is_logged_once_at_warning_with_its_type_only()
    {
        await using ServerRig rig = await ServerRig.StartAsync();
        using var guard = new CancellationTokenSource(HangGuard);
        await using HeadlessSseConnection first = await rig.Client.OpenEventsAsync(rig.Uri, cancellationToken: guard.Token);
        await using HeadlessSseConnection second = await rig.Client.OpenEventsAsync(rig.Uri, cancellationToken: guard.Token);
        await using IAsyncEnumerator<SseReceipt> firstEvents = first.ReadEventsAsync(guard.Token).GetAsyncEnumerator(guard.Token);
        await using IAsyncEnumerator<SseReceipt> secondEvents = second.ReadEventsAsync(guard.Token).GetAsyncEnumerator(guard.Token);
        Assert.True(await firstEvents.MoveNextAsync());
        Assert.True(await secondEvents.MoveNextAsync());

        // Both connections' channels fail (their writers completed with an exception): both streams end, and the failure is logged once per server at Warning by type, never by message (mutant N7b).
        DashboardConnection[] connections;
        using (Field<Lock>(rig.Sink, "_lock").EnterScope())
        {
            connections = [.. Field<List<DashboardConnection>>(rig.Sink, "_connections")];
        }

        Assert.Equal(2, connections.Length);
        foreach (DashboardConnection connection in connections)
        {
            var channel = (Channel<string>)typeof(DashboardConnection).GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
            Assert.True(channel.Writer.TryComplete(new InvalidDataException(ThrowingMetricsSink.SecretMessage)));
        }

        Assert.True(await EndedAsync(firstEvents));
        Assert.True(await EndedAsync(secondEvents));
        RecordedLogEntry warning = Assert.Single(rig.Logger.At(LogLevel.Warning));
        Assert.Equal(314, warning.EventId.Id);
        Assert.Contains(nameof(InvalidDataException), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ThrowingMetricsSink.SecretMessage, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_local_run_with_a_short_power_interval_prints_the_note_once()
    {
        using LoopbackPortBlock block = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        block.Release();
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
            {
                ["local"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromMilliseconds(200)),
                ["nvidia"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromMilliseconds(10)),
            },
            ("AgentJoules:Dashboard:PowerIntervalMs", "50"));
        setup.Gpu = new ScriptedNvmlApi(time) { ConstantPowerWatts = 30.0 };
        RunOrchestrationFixtures.UseClock(setup, time);

        CliRunResult result = await RunOrchestrationFixtures.RunDrivenAsync($"run --tier local --task {DashboardTask} --repeat 1 --baseline-seconds 2 --dashboard --dashboard-port {block.First}", setup, time).WaitAsync(HangGuard);

        Assert.True(result.ExitCode == 0, result.Stderr);
        _ = Assert.Single(result.StderrLines, static line => line == PowerForwarding.ShortIntervalNote(50, 10.0));
        _ = Assert.Single(RunOrchestrationFixtures.StdoutLines(result), static line => line.StartsWith("Dashboard: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_completed_write_emits_run_completed_once_as_the_last_event()
    {
        (CliRunResult result, RecordingLiveMetricsSink live) = await RunWithFailingWriteAsync(RunStatus.Completed, failSteps: false);

        Assert.NotEqual(0, result.ExitCode);
        AssertLastRunCompleted(live, RunStatus.Completed);
    }

    [Fact]
    public async Task A_failed_partial_write_emits_run_completed_once_as_the_last_event()
    {
        (CliRunResult result, RecordingLiveMetricsSink live) = await RunWithFailingWriteAsync(RunStatus.Failed, failSteps: true);

        Assert.Equal(5, result.ExitCode);
        AssertLastRunCompleted(live, RunStatus.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_shutdown_emits_run_completed_once_as_the_last_event(bool failTheIncompleteWrite)
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        var held = new HoldingHttpMessageHandler(RunOrchestrationFixtures.Endpoint(), FakeRoute.ChatCompletions, number: 2);
        RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nvidia"] = held });
        RecordingLiveMetricsSink live = UseRecordingLiveSink(setup, failTheIncompleteWrite ? RunStatus.Incomplete : null);
        using var cancellation = new CancellationTokenSource();

        CliHarnessRun run = CliHarness.Start($"run --tier nvidia --task {DashboardTask} --repeat 1", setup.ToOverrides(workspace.Root), cancellationToken: cancellation.Token);
        try
        {
            await held.Reached.WaitAsync(HangGuard);
            await run.RaiseShutdownAsync(CancellationToken.None).WaitAsync(HangGuard);
            CliRunResult result = await run.Result.WaitAsync(HangGuard);

            Assert.Equal(5, result.ExitCode);
            AssertLastRunCompleted(live, RunStatus.Incomplete);
        }
        finally
        {
            await cancellation.CancelAsync();
            _ = await run.Result.WaitAsync(HangGuard);
        }
    }

    [Fact]
    public async Task A_shutdown_inside_the_completed_write_emits_run_completed_once_as_the_last_event()
    {
        // The completed write is attempted and then cancelled by the shutdown: only the incomplete write that follows emits its run-completed (mutant M43: two events).
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nvidia"] = RunOrchestrationFixtures.Endpoint() });
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingLiveMetricsSink live = UseRecordingLiveSink(setup, writer => new CompletedWriteHolder(writer, reached));
        using var cancellation = new CancellationTokenSource();

        CliHarnessRun run = CliHarness.Start($"run --tier nvidia --task {DashboardTask} --repeat 1", setup.ToOverrides(workspace.Root), cancellationToken: cancellation.Token);
        try
        {
            await reached.Task.WaitAsync(HangGuard);
            await run.RaiseShutdownAsync(CancellationToken.None).WaitAsync(HangGuard);
            CliRunResult result = await run.Result.WaitAsync(HangGuard);

            Assert.Equal(5, result.ExitCode);
            AssertLastRunCompleted(live, RunStatus.Incomplete);
        }
        finally
        {
            await cancellation.CancelAsync();
            _ = await run.Result.WaitAsync(HangGuard);
        }
    }

    // ---- helpers ----

    private const string DashboardTask = "incident-brief";

    // True when the stream ended: the server closed it, or the connection dropped.
    private static async Task<bool> EndedAsync(IAsyncEnumerator<SseReceipt> events)
    {
        try
        {
            return !await events.MoveNextAsync();
        }
        catch (Exception failure) when (failure is IOException or HttpRequestException)
        {
            return true;
        }
    }

    private static readonly string[] SecurityPaths = ["/", "/events", "/missing"];

    private static readonly string[] RootAndEvents = ["/", "/events"];

    // A hosted run (nvidia, also the judge tier) of the production orchestrator on a driven fake clock, so the grace after the run takes no wall-clock time.
    private static (RunCommandSetup Setup, TimerTrackingTimeProvider Time) HostedDashboardRun(TempWorkspace workspace, params (string Key, string? Value)[] extra)
    {
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nvidia"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromMilliseconds(10)) },
            extra);
        RunOrchestrationFixtures.UseClock(setup, time);
        return (setup, time);
    }

    // The active TCP listeners of the machine on one port (the gatekeeper's P1 pattern).
    private static IPEndPoint[] ListenersOn(int port) => [.. IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(listener => listener.Port == port)];

    // A recording live sink in place of the run's, and optionally the production orchestrator behind a decorator whose writers fail the write of one status.
    private static RecordingLiveMetricsSink UseRecordingLiveSink(RunCommandSetup setup, RunStatus? failOn) =>
        UseRecordingLiveSink(setup, failOn is { } status ? writer => new FailingWriter(writer, status) : null);

    // A recording live sink in place of the run's, and optionally the production orchestrator behind a decorator that wraps every writer of the plan.
    private static RecordingLiveMetricsSink UseRecordingLiveSink(RunCommandSetup setup, Func<IRunCheckpointWriter, IRunCheckpointWriter>? wrap)
    {
        var live = new RecordingLiveMetricsSink();
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<ILiveEventSink>();
            _ = services.AddSingleton<ILiveEventSink>(live);
            if (wrap is not null)
            {
                _ = services.RemoveAll<IRunOrchestrator>();
                _ = services.AddSingleton<IRunOrchestrator>(provider => new WriterWrappingOrchestrator(ActivatorUtilities.CreateInstance<RunOrchestrator>(provider), wrap));
            }
        });
        return live;
    }

    // A hosted run whose write of the given status fails; with failSteps every completion call answers 500, so the tier stops at its first step.
    private static async Task<(CliRunResult Result, RecordingLiveMetricsSink Live)> RunWithFailingWriteAsync(RunStatus failOn, bool failSteps)
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint();
        if (failSteps)
        {
            nvidia.SetStatuses(FakeRoute.ChatCompletions, 500);
        }

        RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nvidia"] = nvidia });
        RecordingLiveMetricsSink live = UseRecordingLiveSink(setup, failOn);
        CliRunResult result = await RunCommandHarness.RunAsync($"run --tier nvidia --task {DashboardTask} --repeat 1", setup).WaitAsync(HangGuard);
        return (result, live);
    }

    // Exactly one run-completed event, of the given status, and nothing after it.
    private static void AssertLastRunCompleted(RecordingLiveMetricsSink live, RunStatus status)
    {
        RunCompletedEvent finished = Assert.Single(live.Of<RunCompletedEvent>());
        Assert.Equal(status, finished.Run.Status);
        Assert.Same(finished, live.Events[^1]);
    }

    private static void AssertCompletedFile(TempWorkspace workspace)
    {
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        Assert.Equal("completed", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
        Assert.Equal(4, steps.Length);
        Assert.All(steps, static step => Assert.Equal(StepOutcomes.Ok, step.GetProperty("outcome").GetString()));
    }

    private static string Between(string text, string open, string close)
    {
        int start = text.IndexOf(open, StringComparison.Ordinal) + open.Length;
        return text[start..text.IndexOf(close, start, StringComparison.Ordinal)];
    }

    private static string Sha256Base64(string text) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static DateTimeOffset At(int index) => Origin.AddMilliseconds(index);

    private static PowerEvent Power(int index) => new(At(index), false, Metric.Measured(42.0, "W"), index / 10.0);

    private static RunStartedEvent RunStarted()
    {
        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);
        return new RunStartedEvent(Origin, false, run.Run, run.Environment, 85.0);
    }

    private static BaselineResult Baseline() => RunDocumentFixtures.Committed(Tier.Local).Baseline!;

    private static StepIdentity Identity() => new("Writer", "model-a", Tier.Nvidia, "summarise", 1, IsJudge: false);

    private static string TypeOf(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("type").GetString()!;
    }

    private static T Field<T>(DashboardSink sink, string name) => (T)typeof(DashboardSink).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sink)!;

    // The sink's counters and the snapshot's applied count, read under the sink's lock.
    private static (long Written, long Pumped, long Dropped, long Applied) Counts(DashboardSink sink)
    {
        using (Field<Lock>(sink, "_lock").EnterScope())
        {
            return (Field<long>(sink, "_written"), Field<long>(sink, "_pumped"), Field<long>(sink, "_dropped"), Field<DashboardSnapshot>(sink, "_snapshot").AppliedCount);
        }
    }

    private static bool ReplayOf(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("replay").GetBoolean();
    }

    private static DateTimeOffset AtUtcOf(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("atUtc").GetDateTimeOffset();
    }

    private static List<string> Drain(DashboardConnection connection)
    {
        var payloads = new List<string>();
        while (connection.Events.TryRead(out string? payload))
        {
            payloads.Add(payload);
        }

        return payloads;
    }

    // A hosted-tier client (no GPU) whose services carry the given live sink; the meter is started.
    private static async Task<(MeteredChatClient Client, InMemoryMetricsSink Sink, IEnergyMeter Meter)> HostedClientAsync(IChatClient inner, FakeTimeProvider time, ILiveEventSink live)
    {
        var meter = new UnmeteredEnergyMeter(time);
        await meter.StartAsync(CancellationToken.None);
        var sink = new InMemoryMetricsSink();
        MeteringServices services = new(meter, NoOpMeterGate.Instance, sink, NotConfiguredCostCalculator.Instance, TestInstruments.Create(), time) { Live = live };
        return (new MeteredChatClient(inner, Identity(), services), sink, meter);
    }

    // Advances the clock by one step per tick and yields (never waiting on the wall clock) until the loop took that tick.
    private static async Task TickAsync(SampledEnergyMeter meter, ScriptedNvmlApi api, FakeTimeProvider time, TimeSpan step, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            int target = api.PowerCallCount + 1;
            time.Advance(step);
            for (int yields = 0; api.PowerCallCount < target; yields++)
            {
                Assert.True(yields < MaxYields, "The sampling loop did not take its tick.");
                await Task.Yield();
            }

            _ = meter.SampleCount;
        }
    }

    // One second of a window on a scripted GPU at a constant 60 W, one sampling period per step, with or without forwarding; the counts are those of the meter (the open of the session excluded).
    private static async Task<MeterRun> MeterOneSecondAsync(double rateHz, int? intervalMs)
    {
        var time = new FakeTimeProvider(Origin);
        var api = new ScriptedNvmlApi(time) { ConstantPowerWatts = 60.0, CounterSupported = true };
        GpuOpenResult open = new NvmlGpuTelemetrySourceFactory(api).Open(0);
        Assert.True(open.IsAvailable, open.Reason);
        using IGpuTelemetrySource source = open.Source;
        int powerBefore = api.PowerCallCount;
        int counterBefore = api.CounterCallCount;
        var live = new RecordingLiveSink();
        PowerForwarding? forwarding = intervalMs is { } interval ? new PowerForwarding(live, interval) : null;
        var meter = new SampledEnergyMeter(source, MeterMethodSelector.Select("auto", source, rateHz), time, forwarding);
        await meter.StartAsync(CancellationToken.None);

        IMeterWindow window = meter.OpenWindow();
        int ticks = (int)Math.Round(rateHz);
        await TickAsync(meter, api, time, TimeSpan.FromSeconds(1.0 / rateHz), ticks);
        EnergyReading reading = window.Close();
        await meter.DisposeAsync();

        return new MeterRun(api.PowerCallCount - powerBefore, api.CounterCallCount - counterBefore, [.. live.Of<PowerEvent>()], reading);
    }

    private sealed record MeterRun(int PowerCalls, int CounterCalls, IReadOnlyList<PowerEvent> Events, EnergyReading Reading);

    // A live event type the dashboard does not know: the fault seam of criterion 3.
    private sealed record UnknownEvent(DateTimeOffset AtUtc) : LiveEvent(AtUtc, false);

    private sealed class ThrowingMetricsSink : IMetricsSink
    {
        public const string SecretMessage = "secret provider text";

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Publish(StepReading reading)
        {
            _ = Interlocked.Increment(ref _calls);
            throw new InvalidDataException(SecretMessage);
        }
    }

    private sealed class NamedMetricsSink(string name, ConcurrentQueue<string> order) : IMetricsSink
    {
        public void Publish(StepReading reading) => order.Enqueue(name);
    }

    private class RecordingLiveSink : ILiveEventSink
    {
        private readonly ConcurrentQueue<LiveEvent> _events = new();

        public IReadOnlyList<LiveEvent> Events => [.. _events];

        public IEnumerable<T> Of<T>()
            where T : LiveEvent => Events.OfType<T>();

        public void Emit(LiveEvent liveEvent) => _events.Enqueue(liveEvent);
    }

    private sealed class RecordingLiveMetricsSink : RecordingLiveSink, IMetricsSink
    {
        private readonly ConcurrentQueue<StepReading> _readings = new();

        public IReadOnlyList<StepReading> Readings => [.. _readings];

        public void Publish(StepReading reading) => _readings.Enqueue(reading);
    }

    private sealed class ThrowingLiveSink : ILiveEventSink
    {
        public void Emit(LiveEvent liveEvent) => throw new InvalidOperationException("The live sink is broken.");
    }

    // Asks another thread to take the meter lock while the emit runs: under the lock that thread would wait until the guard ends. Then it throws, as a broken sink may.
    private sealed class LockProbingLiveSink(Func<int> takeTheMeterLock) : ILiveEventSink
    {
        private int _calls;
        private int _lockFree;

        public int Calls => Volatile.Read(ref _calls);

        public int LockFree => Volatile.Read(ref _lockFree);

        public void Emit(LiveEvent liveEvent)
        {
            _ = Interlocked.Increment(ref _calls);
            if (Task.Run(takeTheMeterLock).Wait(TimeSpan.FromSeconds(5)))
            {
                _ = Interlocked.Increment(ref _lockFree);
            }

            throw new InvalidOperationException("The live sink is broken.");
        }
    }

    // A logger that runs a callback inside every record and counts the records.
    private sealed class CallbackLogger(Action onLog) : ILogger
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            onLog();
            _ = Interlocked.Increment(ref _calls);
        }
    }

    // A logger that runs a callback for the records of one event id and counts them.
    private sealed class EventCallbackLogger(int eventId, Action onLog) : ILogger
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId logEventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logEventId.Id == eventId)
            {
                onLog();
                _ = Interlocked.Increment(ref _calls);
            }
        }
    }

    // A logger that spins before it counts a record, so a flush that completes before the record ends is seen.
    private sealed class SlowLogger(int spin) : ILogger
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Thread.SpinWait(spin);
            _ = Interlocked.Increment(ref _count);
        }
    }

    // A logger whose every record throws, as a broken log provider may.
    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new AggregateException(new IOException("The log provider is down."));
    }

    // A logger that keeps the managed thread of every record.
    private sealed class ThreadRecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<int> _threads = new();

        public IReadOnlyList<int> Threads => [.. _threads];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _threads.Enqueue(Environment.CurrentManagedThreadId);
    }

    // The production orchestrator with the writers of the plan wrapped, so the write of one status fails (the writers are built by the command, not by the container).
    private sealed class WriterWrappingOrchestrator(IRunOrchestrator inner, Func<IRunCheckpointWriter, IRunCheckpointWriter> wrap) : IRunOrchestrator
    {
        public Task<int> RunAsync(RunPlan plan, CancellationToken cancellationToken)
        {
            Dictionary<Tier, IRunCheckpointWriter> writers = plan.Writers.ToDictionary(static pair => pair.Key, pair => wrap(pair.Value));
            var swapped = new RunPlan(plan.Tiers, plan.Tasks, plan.Mode, plan.Repeat, plan.BaselineSeconds, writers, plan.Keys, plan.Models, plan.StartUtc, plan.OutputDisplay);
            return inner.RunAsync(swapped, cancellationToken);
        }
    }

    private sealed class FailingWriter(IRunCheckpointWriter inner, RunStatus failOn) : IRunCheckpointWriter
    {
        public string FilePath => inner.FilePath;

        public Task WriteAsync(RunDocument document, CancellationToken cancellationToken) =>
            document.Run.Status == failOn ? Task.FromException(new IOException("The disk is full.")) : inner.WriteAsync(document, cancellationToken);
    }

    // Holds the write of the completed document until the run token is cancelled, so a shutdown lands inside that write.
    private sealed class CompletedWriteHolder(IRunCheckpointWriter inner, TaskCompletionSource reached) : IRunCheckpointWriter
    {
        public string FilePath => inner.FilePath;

        public async Task WriteAsync(RunDocument document, CancellationToken cancellationToken)
        {
            if (document.Run.Status != RunStatus.Completed)
            {
                await inner.WriteAsync(document, cancellationToken);
                return;
            }

            _ = reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    // The host settings of the dashboard's web host are process-wide environment variables, so these tests run alone (no other test builds a host meanwhile).
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class HostSettingsGroup
    {
        public const string Name = "AJ-091 dashboard host settings";
    }

    [Collection(HostSettingsGroup.Name)]
    [Trait("Spec", "REQ-032")]
    public sealed class HostSettings
    {
        [Theory]
        [InlineData("ASPNETCORE_")]
        [InlineData("DOTNET_")]
        [InlineData("")]
        public async Task Url_settings_of_the_process_cannot_rebind_the_dashboard(string prefix)
        {
            using LoopbackPortBlock block = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts + 1);
            block.Release();
            int rebound = block.Last;
            string urls = Uri.UriSchemeHttp + Uri.SchemeDelimiter + "127.0.0.2:" + rebound.ToString(CultureInfo.InvariantCulture);
            using var variables = new EnvironmentScope((prefix + "URLS", urls), (prefix + "PREFERHOSTINGURLS", "true"));
            await using DashboardSink sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
            await using var server = new DashboardServer(sink, NullLoggerFactory.Instance);

            Uri? uri = await server.TryStartAsync(block.First, null, CancellationToken.None).WaitAsync(HangGuard);

            Assert.NotNull(uri);
            Assert.Equal([new IPEndPoint(IPAddress.Loopback, uri.Port)], ListenersOn(uri.Port));
            Assert.Empty(ListenersOn(rebound));
            using var client = new HeadlessSseClient();
            Assert.Equal(200, (await client.GetAsync(uri, "/", null).WaitAsync(HangGuard)).Status);
        }

        [Theory]
        [InlineData("DOTNET_hostBuilder__reloadConfigOnChange")]
        [InlineData("ASPNETCORE_hostBuilder__reloadConfigOnChange")]
        public async Task A_host_setting_the_builder_refuses_ends_the_start_and_the_run_completes(string variable)
        {
            // The P10 shape: the builder throws when constructed; the start ends with start failed (<type>), the command prints its one warning line and the run completes (mutant M10).
            using LoopbackPortBlock block = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
            block.Release();
            using var variables = new EnvironmentScope((variable, "maybe"));
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            (RunCommandSetup setup, TimerTrackingTimeProvider time) = HostedDashboardRun(workspace);

            CliRunResult result = await RunOrchestrationFixtures.RunDrivenAsync($"run --tier nvidia --task {DashboardTask} --repeat 1 --dashboard --dashboard-port {block.First}", setup, time).WaitAsync(HangGuard);

            Assert.True(result.ExitCode == 0, result.Stderr);
            Assert.DoesNotContain(RunOrchestrationFixtures.StdoutLines(result), static line => line.StartsWith("Dashboard:", StringComparison.Ordinal));
            string[] stderr = [.. result.StderrLines.Where(static line => line.Length > 0)];
            Assert.Equal(["Warning: the dashboard did not start (start failed (InvalidOperationException)); the run continues without it"], stderr);
            AssertCompletedFile(workspace);
        }
    }

    // Sets process environment variables and restores their previous values.
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly (string Name, string? Previous)[] _previous;

        public EnvironmentScope(params (string Name, string Value)[] variables)
        {
            _previous = [.. variables.Select(static variable => (variable.Name, Environment.GetEnvironmentVariable(variable.Name)))];
            foreach ((string name, string value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach ((string name, string? previous) in _previous)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }

    // A logger factory whose every category writes to one logger, for the records of the server.
    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }

    // A started server on a block of free loopback ports (reserved, then released so the server can bind them), its sink, its records and the one client of the test.
    private sealed class ServerRig : IAsyncDisposable
    {
        private ServerRig(DashboardSink sink, DashboardServer server, Uri uri, RecordingLogger logger)
        {
            Sink = sink;
            Server = server;
            Uri = uri;
            Logger = logger;
        }

        public DashboardSink Sink { get; }

        public DashboardServer Server { get; }

        public Uri Uri { get; }

        public RecordingLogger Logger { get; }

        public HeadlessSseClient Client { get; } = new();

        public static async Task<ServerRig> StartAsync(Uri? repositoryUrl = null, TimeProvider? time = null)
        {
            using LoopbackPortBlock block = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
            block.Release();
            var logger = new RecordingLogger();
            DashboardSink sink = DashboardFixtures.NewSink(logger, time ?? new FakeTimeProvider(Origin));
            var server = new DashboardServer(sink, new SingleLoggerFactory(logger));
            Uri? uri = await server.TryStartAsync(block.First, repositoryUrl, CancellationToken.None).WaitAsync(HangGuard);
            if (uri is null)
            {
                await server.DisposeAsync();
                await sink.DisposeAsync();
                throw new InvalidOperationException("The dashboard did not start: " + server.FailureReason);
            }

            return new ServerRig(sink, server, uri, logger);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            await Sink.DisposeAsync();
        }
    }

    // A fake clock that holds the pump: once HoldPump is set, a read of the time from any thread other than the one that built the clock (the test's) waits until Release,
    // so the pump stops under its lock at the snapshot that follows a run-started event.
    private sealed class PumpHoldingTime(DateTimeOffset start) : FakeTimeProvider(start), IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly int _testThread = Environment.CurrentManagedThreadId;

        public bool HoldPump { get; set; }

        public ManualResetEventSlim PumpHeld { get; } = new();

        public void Release() => _release.Set();

        public override DateTimeOffset GetUtcNow()
        {
            if (HoldPump && Environment.CurrentManagedThreadId != _testThread && !_release.IsSet)
            {
                PumpHeld.Set();
                _ = _release.Wait(HangGuard);
            }

            return base.GetUtcNow();
        }

        public void Dispose()
        {
            _release.Dispose();
            PumpHeld.Dispose();
        }
    }
}