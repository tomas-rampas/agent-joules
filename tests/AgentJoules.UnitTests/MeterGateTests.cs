using AgentJoules.Core.Energy;
using AgentJoules.Core.Energy.Testing;
using AgentJoules.Metering;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-032: the process-wide meter gate, serialized and no-op. Nothing here waits on the wall clock. Every task is an <c>async Task</c>
/// method started directly and awaited: "still waiting" is asserted as <c>IsCompleted == false</c> before the release (a waiter cannot
/// complete before it), then the release happens, then the waiter is awaited. A waiter that must stay waiting is also checked after
/// <see cref="SettleAsync"/>, a bounded run of <see cref="Task.Yield"/> calls that gives a wrongly admitted waiter a chance to run its
/// continuation; it never decides a pass, it only makes a wrong admission visible sooner. The M2 story for REQ-013 reuses these types for
/// the <c>--mode</c> wiring; this class tests only the gates.
/// </summary>
[Trait("Spec", "REQ-005")]
public sealed class MeterGateTests
{
    private const int SettleYields = 1000;
    private const int RaceIterations = 2000;
    private const double RateHz = 10.0;

    private static async Task SettleAsync()
    {
        for (int i = 0; i < SettleYields; i++)
        {
            await Task.Yield();
        }
    }

    // Passing a ValueTask to a method is the form CA2012 accepts; both results here are already complete or throw at the call.
    private static void Consume(ValueTask task) => task.AsTask().GetAwaiter().GetResult();

    private static void Consume(ValueTask<IAsyncDisposable> task) => task.AsTask().GetAwaiter().GetResult();

    // Two threads released together by a barrier (no wall-clock wait); returns the first exception either action threw, or null.
    private static Exception? RaceTwoThreads(Action first, Action second)
    {
        using Barrier barrier = new(2);
        Exception?[] failures = new Exception?[2];
        Thread one = new(() =>
        {
            barrier.SignalAndWait();
            failures[0] = Record.Exception(first);
        });
        Thread two = new(() =>
        {
            barrier.SignalAndWait();
            failures[1] = Record.Exception(second);
        });

        one.Start();
        two.Start();
        one.Join();
        two.Join();
        return failures[0] ?? failures[1];
    }

    private static Task<IAsyncDisposable> Acquire(SerializedMeterGate gate, CancellationToken cancellationToken = default) =>
        gate.AcquireAsync(cancellationToken).AsTask();

    private static TaskCompletionSource[] NewSignals(int count) =>
        [.. Enumerable.Range(0, count).Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];

    // One measurement task: take the gate, open a window on the shared meter, tell the test, wait for the test to say go, close, release.
    private static async Task<WindowSpan> RunWorkerAsync(SerializedMeterGate gate, DeterministicEnergyMeter meter, TaskCompletionSource opened, TaskCompletionSource proceed)
    {
        await using IAsyncDisposable releaser = await gate.AcquireAsync(CancellationToken.None);
        IMeterWindow window = meter.OpenWindow();
        opened.SetResult();
        await proceed.Task;
        window.Close();
        return window.Span;
    }

    [Fact]
    public async Task A_default_token_is_accepted_by_both_gates_through_the_interface()
    {
        using SerializedMeterGate serialized = new();
        IMeterGate[] gates = [serialized, new NoOpMeterGate()];

        foreach (IMeterGate gate in gates)
        {
            ValueTask<IAsyncDisposable> acquiring = gate.AcquireAsync(default);
            IAsyncDisposable releaser = await acquiring;
            await releaser.DisposeAsync();
        }

        Assert.Equal(1, serialized.CurrentCount);
    }

    [Fact]
    public async Task Serialized_gate_starts_with_one_free_slot_and_an_acquire_takes_it_and_the_release_gives_it_back()
    {
        using SerializedMeterGate gate = new();
        Assert.Equal(1, gate.CurrentCount);

        IAsyncDisposable releaser = await gate.AcquireAsync(CancellationToken.None);
        Assert.Equal(0, gate.CurrentCount);

        await releaser.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Each_serialized_acquire_returns_its_own_releaser()
    {
        using SerializedMeterGate gate = new();

        IAsyncDisposable first = await gate.AcquireAsync(CancellationToken.None);
        await first.DisposeAsync();
        IAsyncDisposable second = await gate.AcquireAsync(CancellationToken.None);
        await second.DisposeAsync();

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Second_acquire_stays_incomplete_until_the_first_releaser_is_disposed_and_then_completes()
    {
        using SerializedMeterGate gate = new();
        IAsyncDisposable first = await gate.AcquireAsync(CancellationToken.None);
        Assert.Equal(0, gate.CurrentCount);

        Task<IAsyncDisposable> second = Acquire(gate);

        Assert.False(second.IsCompleted);
        Assert.Equal(0, gate.CurrentCount);

        await first.DisposeAsync();
        IAsyncDisposable secondReleaser = await second;

        Assert.True(second.IsCompletedSuccessfully);

        // The slot passed straight to the waiter, so the gate is held by the second task now.
        Assert.Equal(0, gate.CurrentCount);
        await secondReleaser.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Waiters_are_admitted_one_at_a_time_in_arrival_order()
    {
        using SerializedMeterGate gate = new();
        IAsyncDisposable first = await gate.AcquireAsync(CancellationToken.None);
        Task<IAsyncDisposable> second = Acquire(gate);
        Task<IAsyncDisposable> third = Acquire(gate);

        await first.DisposeAsync();

        // Out-of-order admission makes the third task win here, so the test fails instead of hanging on the await below.
        Assert.Same(second, await Task.WhenAny(second, third));
        IAsyncDisposable secondReleaser = await second;
        await SettleAsync();

        Assert.False(third.IsCompleted);

        await secondReleaser.DisposeAsync();
        IAsyncDisposable thirdReleaser = await third;
        await thirdReleaser.DisposeAsync();

        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Cancelling_a_waiting_acquire_throws_and_leaves_the_gate_usable_with_no_count_leaked()
    {
        using SerializedMeterGate gate = new();
        using CancellationTokenSource cancellation = new();
        IAsyncDisposable first = await gate.AcquireAsync(CancellationToken.None);
        Task<IAsyncDisposable> waiter = Acquire(gate, cancellation.Token);
        Assert.False(waiter.IsCompleted);

        await cancellation.CancelAsync();

        // A waiter cancelled while queued surfaces exactly OperationCanceledException; a pre-cancelled token surfaces TaskCanceledException
        // (Task.FromCanceled). The tests assert the base type.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.True(waiter.IsCanceled);
        Assert.Equal(0, gate.CurrentCount);

        // The cancelled waiter took nothing: the holder's release frees the slot, and a third acquire gets it.
        await first.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);

        IAsyncDisposable third = await Acquire(gate);
        Assert.Equal(0, gate.CurrentCount);

        await third.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task A_waiter_cancelled_ahead_of_another_waiter_does_not_swallow_the_release()
    {
        using SerializedMeterGate gate = new();
        using CancellationTokenSource cancellation = new();
        IAsyncDisposable first = await gate.AcquireAsync(CancellationToken.None);
        Task<IAsyncDisposable> cancelled = Acquire(gate, cancellation.Token);
        Task<IAsyncDisposable> survivor = Acquire(gate);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(survivor.IsCompleted);

        await first.DisposeAsync();
        IAsyncDisposable survivorReleaser = await survivor;
        Assert.Equal(0, gate.CurrentCount);

        await survivorReleaser.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Acquire_with_an_already_cancelled_token_throws_and_takes_no_count()
    {
        using SerializedMeterGate gate = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gate.AcquireAsync(cancellation.Token));

        Assert.Equal(1, gate.CurrentCount);
        IAsyncDisposable releaser = await gate.AcquireAsync(CancellationToken.None);
        await releaser.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Disposing_a_releaser_twice_with_two_waiters_admits_exactly_one_of_them()
    {
        using SerializedMeterGate gate = new();
        IAsyncDisposable a = await gate.AcquireAsync(CancellationToken.None);
        Task<IAsyncDisposable> b = Acquire(gate);
        Task<IAsyncDisposable> c = Acquire(gate);
        Assert.False(b.IsCompleted);
        Assert.False(c.IsCompleted);

        await a.DisposeAsync();
        await a.DisposeAsync();
        IAsyncDisposable bReleaser = await b;
        await SettleAsync();

        Assert.False(c.IsCompleted);
        Assert.Equal(0, gate.CurrentCount);

        await bReleaser.DisposeAsync();
        IAsyncDisposable cReleaser = await c;
        Assert.True(c.IsCompletedSuccessfully);

        await cReleaser.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Disposing_a_releaser_twice_with_one_waiter_does_not_free_the_slot_the_waiter_took()
    {
        using SerializedMeterGate gate = new();
        IAsyncDisposable a = await gate.AcquireAsync(CancellationToken.None);
        Task<IAsyncDisposable> b = Acquire(gate);

        await a.DisposeAsync();
        await a.DisposeAsync();
        IAsyncDisposable bReleaser = await b;

        // A second release would have left a free slot behind (or thrown), so a later acquire would complete while b still holds the gate.
        Assert.Equal(0, gate.CurrentCount);
        Task<IAsyncDisposable> d = Acquire(gate);
        await SettleAsync();
        Assert.False(d.IsCompleted);

        await bReleaser.DisposeAsync();
        IAsyncDisposable dReleaser = await d;
        await dReleaser.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Disposing_a_releaser_twice_with_no_waiter_does_not_throw_and_frees_one_slot()
    {
        using SerializedMeterGate gate = new();
        IAsyncDisposable a = await gate.AcquireAsync(CancellationToken.None);

        await a.DisposeAsync();
        await a.DisposeAsync();

        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task A_stale_releaser_does_not_release_a_later_holders_slot()
    {
        using SerializedMeterGate gate = new();
        IAsyncDisposable a = await gate.AcquireAsync(CancellationToken.None);
        await a.DisposeAsync();
        IAsyncDisposable b = await gate.AcquireAsync(CancellationToken.None);
        Assert.Equal(0, gate.CurrentCount);

        await a.DisposeAsync();

        Assert.Equal(0, gate.CurrentCount);
        await b.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task No_op_gate_admits_two_simultaneous_acquires_at_once_with_the_same_cached_releaser()
    {
        NoOpMeterGate gate = new();

        ValueTask<IAsyncDisposable> first = gate.AcquireAsync(CancellationToken.None);
        ValueTask<IAsyncDisposable> second = gate.AcquireAsync(CancellationToken.None);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        IAsyncDisposable firstReleaser = await first;
        IAsyncDisposable secondReleaser = await second;
        Assert.Same(firstReleaser, secondReleaser);

        // Disposing the shared releaser, twice and from both holders, changes nothing: the gate still admits at once.
        await firstReleaser.DisposeAsync();
        await firstReleaser.DisposeAsync();
        await secondReleaser.DisposeAsync();
        ValueTask<IAsyncDisposable> third = gate.AcquireAsync(CancellationToken.None);
        Assert.True(third.IsCompletedSuccessfully);
        Assert.Same(firstReleaser, await third);
    }

    [Fact]
    public async Task No_op_releaser_disposes_synchronously()
    {
        IAsyncDisposable releaser = await new NoOpMeterGate().AcquireAsync(CancellationToken.None);

        ValueTask disposing = releaser.DisposeAsync();

        Assert.True(disposing.IsCompletedSuccessfully);
        await disposing;
    }

    [Fact]
    public async Task No_op_gate_instance_is_shared_and_its_releaser_is_the_one_every_no_op_gate_returns()
    {
        Assert.NotNull(NoOpMeterGate.Instance);
        Assert.Same(NoOpMeterGate.Instance, NoOpMeterGate.Instance);

        IAsyncDisposable fromInstance = await NoOpMeterGate.Instance.AcquireAsync(CancellationToken.None);
        IAsyncDisposable fromNew = await new NoOpMeterGate().AcquireAsync(CancellationToken.None);

        Assert.Same(fromInstance, fromNew);
    }

    [Fact]
    public async Task No_op_gate_with_an_already_cancelled_token_gives_a_cancelled_task()
    {
        NoOpMeterGate gate = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        ValueTask<IAsyncDisposable> acquiring = gate.AcquireAsync(cancellation.Token);

        Assert.True(acquiring.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await acquiring);
    }

    [Fact]
    public async Task Three_tasks_holding_the_serialized_gate_around_deterministic_windows_produce_disjoint_ordered_spans()
    {
        FakeTimeProvider time = new();
        await using DeterministicEnergyMeter meter = new(time, static _ => 60.0, RateHz, counterSupported: true, MeterMethodSetting.Auto);
        await meter.StartAsync(CancellationToken.None);
        using SerializedMeterGate gate = new();

        const int workerCount = 3;
        TaskCompletionSource[] opened = NewSignals(workerCount);
        TaskCompletionSource[] proceed = NewSignals(workerCount);
        Task<WindowSpan>[] workers = new Task<WindowSpan>[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = RunWorkerAsync(gate, meter, opened[i], proceed[i]);
        }

        // One task at a time: it opens its window, the fake clock moves 1 s, it closes the window and releases the gate. Only then can the next
        // task open its window, so the test waits for each "opened" signal before it moves the clock.
        WindowSpan[] spans = new WindowSpan[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            // Task i must be the next one in. A later task that opens first (out-of-order admission), or any task that ends early, wins
            // this race and fails the test instead of hanging it on the await.
            List<Task> contenders = [opened[i].Task, workers[i]];
            for (int later = i + 1; later < workerCount; later++)
            {
                contenders.Add(opened[later].Task);
                contenders.Add(workers[later]);
            }

            Assert.Same(opened[i].Task, await Task.WhenAny(contenders));
            await opened[i].Task;
            for (int later = i + 1; later < workerCount; later++)
            {
                Assert.False(opened[later].Task.IsCompleted);
            }

            time.Advance(TimeSpan.FromSeconds(1));
            proceed[i].SetResult();
            spans[i] = await workers[i];
        }

        for (int i = 0; i < workerCount; i++)
        {
            Assert.True(spans[i].StopSeconds > spans[i].StartSeconds);
        }

        for (int i = 0; i < workerCount; i++)
        {
            for (int j = i + 1; j < workerCount; j++)
            {
                Assert.True(
                    spans[i].StopSeconds <= spans[j].StartSeconds,
                    $"Window {i} ({spans[i]}) overlaps or follows window {j} ({spans[j]}).");
            }
        }

        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Dispose_is_safe_twice()
    {
        SerializedMeterGate gate = new();

        gate.Dispose();
        gate.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await gate.AcquireAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Acquire_after_dispose_throws_ObjectDisposedException()
    {
        SerializedMeterGate gate = new();
        gate.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await gate.AcquireAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_releaser_disposed_after_the_gate_is_disposed_does_not_throw()
    {
        SerializedMeterGate gate = new();
        IAsyncDisposable releaser = await gate.AcquireAsync(CancellationToken.None);

        gate.Dispose();

        Exception? failure = await Record.ExceptionAsync(async () => await releaser.DisposeAsync());
        Assert.Null(failure);

        // And twice, for the same reason.
        failure = await Record.ExceptionAsync(async () => await releaser.DisposeAsync());
        Assert.Null(failure);
    }

    [Fact]
    public void Acquire_after_dispose_throws_ObjectDisposedException_synchronously()
    {
        SerializedMeterGate gate = new();
        gate.Dispose();

        // The call itself throws: nothing is awaited and no task is returned to a caller.
        Exception? failure = Record.Exception(() => Consume(gate.AcquireAsync(CancellationToken.None)));

        Assert.IsType<ObjectDisposedException>(failure);
    }

    [Fact]
    public async Task Two_threads_disposing_the_same_releaser_at_once_release_exactly_once()
    {
        for (int i = 0; i < RaceIterations; i++)
        {
            using SerializedMeterGate gate = new();
            IAsyncDisposable a = await gate.AcquireAsync(CancellationToken.None);
            Task<IAsyncDisposable>? waiter = i % 2 == 0 ? Acquire(gate) : null;

            Exception? failure = RaceTwoThreads(() => Consume(a.DisposeAsync()), () => Consume(a.DisposeAsync()));

            Assert.Null(failure);
            if (waiter is null)
            {
                // No waiter: one release frees the slot; a second one would have thrown or left the count at 2.
                Assert.Equal(1, gate.CurrentCount);
            }
            else
            {
                // One waiter: it holds the slot, and a second release would have left a free slot behind.
                IAsyncDisposable waiterReleaser = await waiter;
                Assert.Equal(0, gate.CurrentCount);
                await waiterReleaser.DisposeAsync();
                Assert.Equal(1, gate.CurrentCount);
            }
        }
    }

    [Fact]
    public async Task A_releaser_disposed_while_the_gate_is_disposed_by_another_thread_never_throws()
    {
        for (int i = 0; i < RaceIterations; i++)
        {
            SerializedMeterGate gate = new();
            IAsyncDisposable a = await gate.AcquireAsync(CancellationToken.None);

            // Both orders occur; nothing is asserted about which one won.
            Exception? failure = RaceTwoThreads(() => Consume(a.DisposeAsync()), gate.Dispose);

            Assert.Null(failure);
        }
    }
}