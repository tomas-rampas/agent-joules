using AgentJoules.Core.Energy;
using AgentJoules.Core.Energy.Testing;
using AgentJoules.Metering;

using Microsoft.Extensions.Time.Testing;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-032: the process-wide meter gate. Self-contained: its state lives here, not in ScenarioWorld. No sentence of this feature exists in
/// another binding (checked against the MeteringSteps, BaselineSteps, DeterministicMeterSteps and ConfigurationSteps attributes), so the class
/// is not scoped. Every task is an <c>async Task</c> started directly; a passing run never waits on the wall clock, and the fake clock moves
/// only when a step advances it. The one wall-clock timer is the hang guard of <see cref="CompletesAsync"/>, which only ends a run that has
/// already failed (a leaked slot) with an assertion instead of a hang.
/// </summary>
[Binding]
public sealed class MeterGateSteps : IDisposable
{
    private const double RateHz = 10.0;
    private const int WorkerCount = 3;
    private const int SettleYields = 1000;

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _time = new();

    private SerializedMeterGate? _serialized;
    private NoOpMeterGate? _noOp;
    private CancellationTokenSource? _cancellation;
    private IAsyncDisposable? _releaserA;
    private IAsyncDisposable? _releaserC;
    private Task<IAsyncDisposable>? _taskB;
    private Task<IAsyncDisposable>? _taskC;
    private Exception? _taskBException;

    private bool _firstCompletedAtOnce;
    private bool _secondCompletedAtOnce;

    private DeterministicEnergyMeter? _meter;
    private TaskCompletionSource[] _opened = [];
    private TaskCompletionSource[] _proceed = [];
    private Task<WindowSpan>[] _workers = [];
    private WindowSpan[] _spans = [];

    public void Dispose()
    {
        // The gate and the meter are plain objects; waiters still pending at the end of a scenario are dropped with them.
        _serialized?.Dispose();
        _serialized = null;
        _meter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _meter = null;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private SerializedMeterGate Gate => _serialized ?? throw new InvalidOperationException("No serialized gate was created.");

    private Task<IAsyncDisposable> TaskB => _taskB ?? throw new InvalidOperationException("Task B did not start.");

    private Task<IAsyncDisposable> TaskC => _taskC ?? throw new InvalidOperationException("Task C did not start.");

    private IAsyncDisposable GetReleaserA() => _releaserA ?? throw new InvalidOperationException("Task A does not hold the gate.");

    // Awaits a task that a correct gate completes, and fails the scenario instead of hanging it when a leaked slot keeps it from completing.
    // The guard is wall-clock time, but only a failing run ever reaches it: a passing run returns the moment the task completes.
    private static async Task<T> CompletesAsync<T>(Task<T> task, string what)
    {
        using CancellationTokenSource stop = new();
        Task winner = await Task.WhenAny(task, Task.Delay(HangGuard, stop.Token));
        await stop.CancelAsync();
        if (!ReferenceEquals(winner, task))
        {
            Assert.Fail($"{what} did not complete within {HangGuard.TotalSeconds} s: the gate leaked a slot.");
        }

        return await task;
    }

    private static async Task SettleAsync()
    {
        // A bounded run of yields that lets a wrongly admitted waiter run its continuation; it never decides a pass.
        for (int i = 0; i < SettleYields; i++)
        {
            await Task.Yield();
        }
    }

    // One measurement task: take the gate, open a window on the shared meter, tell the scenario, wait for the scenario to say go, close, release.
    private static async Task<WindowSpan> RunWorkerAsync(SerializedMeterGate gate, DeterministicEnergyMeter meter, TaskCompletionSource opened, TaskCompletionSource proceed)
    {
        await using IAsyncDisposable releaser = await gate.AcquireAsync(CancellationToken.None);
        IMeterWindow window = meter.OpenWindow();
        opened.SetResult();
        await proceed.Task;
        window.Close();
        return window.Span;
    }

    private static TaskCompletionSource[] NewSignals() =>
        [.. Enumerable.Range(0, WorkerCount).Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];

    [Given(@"^a serialized gate held by task A$")]
    public async Task GivenASerializedGateHeldByTaskA()
    {
        _serialized = new SerializedMeterGate();
        _releaserA = await _serialized.AcquireAsync(CancellationToken.None);
    }

    [Given(@"^three tasks that each acquire the serialized gate around a deterministic window$")]
    public async Task GivenThreeTasksThatEachAcquireTheSerializedGateAroundADeterministicWindow()
    {
        _meter = new DeterministicEnergyMeter(_time, static _ => 60.0, RateHz, counterSupported: true, MeterMethodSetting.Auto);
        await _meter.StartAsync(CancellationToken.None);
        _serialized = new SerializedMeterGate();
        _opened = NewSignals();
        _proceed = NewSignals();
        _workers = new Task<WindowSpan>[WorkerCount];
        for (int i = 0; i < WorkerCount; i++)
        {
            _workers[i] = RunWorkerAsync(_serialized, _meter, _opened[i], _proceed[i]);
        }
    }

    [Given(@"^a no-op gate$")]
    public void GivenANoOpGate() => _noOp = new NoOpMeterGate();

    [Given(@"^a serialized gate held by task A and a waiting task B that is cancelled$")]
    public async Task GivenASerializedGateHeldByTaskAAndAWaitingTaskBThatIsCancelled()
    {
        _serialized = new SerializedMeterGate();
        _cancellation = new CancellationTokenSource();
        _releaserA = await _serialized.AcquireAsync(CancellationToken.None);
        _taskB = _serialized.AcquireAsync(_cancellation.Token).AsTask();
        Assert.False(_taskB.IsCompleted, "Task B completed while task A held the gate.");

        await _cancellation.CancelAsync();
        _taskBException = await Record.ExceptionAsync(() => _taskB);
    }

    [Given(@"^a serialized gate held by task A with tasks B and C waiting$")]
    public async Task GivenASerializedGateHeldByTaskAWithTasksBAndCWaiting()
    {
        _serialized = new SerializedMeterGate();
        _releaserA = await _serialized.AcquireAsync(CancellationToken.None);
        _taskB = _serialized.AcquireAsync(CancellationToken.None).AsTask();
        _taskC = _serialized.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(_taskB.IsCompleted, "Task B completed while task A held the gate.");
        Assert.False(_taskC.IsCompleted, "Task C completed while task A held the gate.");
    }

    [When(@"^task B calls AcquireAsync$")]
    public void WhenTaskBCallsAcquireAsync() => _taskB = Gate.AcquireAsync(CancellationToken.None).AsTask();

    [When(@"^all tasks finish$")]
    public async Task WhenAllTasksFinish()
    {
        // One task at a time: it opens its window, the fake clock moves 1 s, it closes the window and releases the gate. Only then can the
        // next task open its window, so each step waits for the "opened" signal before it moves the clock.
        _spans = new WindowSpan[WorkerCount];
        for (int i = 0; i < WorkerCount; i++)
        {
            await _opened[i].Task;
            for (int later = i + 1; later < WorkerCount; later++)
            {
                Assert.False(_opened[later].Task.IsCompleted, $"Task {later} opened its window while task {i} held the gate.");
            }

            _time.Advance(TimeSpan.FromSeconds(1));
            _proceed[i].SetResult();
            _spans[i] = await _workers[i];
        }
    }

    [When(@"^two tasks acquire it at the same time$")]
    public async Task WhenTwoTasksAcquireItAtTheSameTime()
    {
        NoOpMeterGate gate = _noOp ?? throw new InvalidOperationException("No no-op gate was created.");

        // Both acquires are started before either result is read, and whether each completed on the spot is recorded before it is awaited.
        ValueTask<IAsyncDisposable> first = gate.AcquireAsync(CancellationToken.None);
        ValueTask<IAsyncDisposable> second = gate.AcquireAsync(CancellationToken.None);
        _firstCompletedAtOnce = first.IsCompletedSuccessfully;
        _secondCompletedAtOnce = second.IsCompletedSuccessfully;
        await first;
        await second;
    }

    [When(@"^task A releases and task C acquires$")]
    public async Task WhenTaskAReleasesAndTaskCAcquires()
    {
        await GetReleaserA().DisposeAsync();
        _taskC = Gate.AcquireAsync(CancellationToken.None).AsTask();
        _releaserC = await CompletesAsync(_taskC, "Task C");
    }

    [When(@"^task A disposes its releaser twice$")]
    public async Task WhenTaskADisposesItsReleaserTwice()
    {
        await GetReleaserA().DisposeAsync();
        await GetReleaserA().DisposeAsync();
    }

    [Then(@"^task B is still waiting until task A disposes its releaser, and then completes$")]
    public async Task ThenTaskBIsStillWaitingUntilTaskADisposesItsReleaserAndThenCompletes()
    {
        Assert.False(TaskB.IsCompleted, "Task B completed before task A disposed its releaser.");

        await GetReleaserA().DisposeAsync();
        await CompletesAsync(TaskB, "Task B");

        Assert.True(TaskB.IsCompletedSuccessfully);
    }

    [Then(@"^the three window spans are pairwise disjoint$")]
    public void ThenTheThreeWindowSpansArePairwiseDisjoint()
    {
        Assert.Equal(WorkerCount, _spans.Length);
        for (int i = 0; i < WorkerCount; i++)
        {
            Assert.True(_spans[i].StopSeconds > _spans[i].StartSeconds, $"Window {i} has no duration: {_spans[i]}.");
        }

        for (int i = 0; i < WorkerCount; i++)
        {
            for (int j = i + 1; j < WorkerCount; j++)
            {
                Assert.True(
                    _spans[i].StopSeconds <= _spans[j].StartSeconds,
                    $"Window {i} ({_spans[i]}) overlaps or follows window {j} ({_spans[j]}).");
            }
        }
    }

    [Then(@"^both complete immediately$")]
    public void ThenBothCompleteImmediately()
    {
        Assert.True(_firstCompletedAtOnce, "The first acquire of the no-op gate did not complete synchronously.");
        Assert.True(_secondCompletedAtOnce, "The second acquire of the no-op gate did not complete synchronously.");
    }

    [Then(@"^task B ended with OperationCanceledException and task C acquired successfully$")]
    public void ThenTaskBEndedWithOperationCanceledExceptionAndTaskCAcquiredSuccessfully()
    {
        // A waiter cancelled while queued surfaces exactly OperationCanceledException; a pre-cancelled token surfaces TaskCanceledException
        // (Task.FromCanceled). The step asserts the base type.
        Assert.IsType<OperationCanceledException>(_taskBException, exactMatch: false);
        Assert.True(TaskB.IsCanceled);
        Assert.True(TaskC.IsCompletedSuccessfully);
        Assert.NotNull(_releaserC);

        // Task C holds the only slot: the cancelled waiter took none.
        Assert.Equal(0, Gate.CurrentCount);
    }

    [Then(@"^only task B is admitted$")]
    public async Task ThenOnlyTaskBIsAdmitted()
    {
        IAsyncDisposable releaserB = await CompletesAsync(TaskB, "Task B");
        await SettleAsync();

        Assert.False(TaskC.IsCompleted, "Task C was admitted by the second disposal of task A's releaser.");
        Assert.Equal(0, Gate.CurrentCount);

        // B gives the slot to C, which is still queued; a wrong second release by A would have left a free slot, and the count reads 1 here.
        await releaserB.DisposeAsync();
        Assert.Equal(0, Gate.CurrentCount);
        await CompletesAsync(TaskC, "Task C");
    }
}