using System.Threading.Channels;

using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Security;
using AgentJoules.Metering;

using Microsoft.Extensions.Logging;

namespace AgentJoules.Dashboard;

/// <summary>
/// The sink of the live dashboard (REQ-032, EDGE-019, design 3.10): it takes the readings of a run (as an <see cref="IMetricsSink"/> behind <see cref="FanOutMetricsSink"/>) and its live
/// events (as an <see cref="ILiveEventSink"/>) and never blocks or throws into the run. <see cref="Emit"/> is one <c>TryWrite</c> into a bounded ingress channel of 1000 events that drops
/// its oldest event when full, inside a catch-all; nothing on the measured path awaits the dashboard.
/// </summary>
/// <remarks>
/// <para>
/// One pump loop, started by the constructor, reads the ingress. Under one lock it applies each event to the <see cref="DashboardSnapshot"/>, which returns the payload, and writes the
/// payload to the channel of every connection (bounded at 1000, drop-oldest each), so a slow or absent browser loses its oldest events and nothing else. A <see cref="RunStartedEvent"/>
/// is followed by a snapshot payload. <see cref="Connect"/> takes the same lock, so the snapshot of a new connection and its registration are one step: no event is lost or doubled
/// at subscribe.
/// </para>
/// <para>
/// A pump failure drops the event that caused it and the loop goes on; the first failure is logged at Warning with its exception type only, and <see cref="PumpFaultCount"/> counts every
/// one. <see cref="DisposeAsync"/> completes the ingress, waits for the pump to apply what is left and completes every connection; <see cref="Dispose"/> does the same without waiting.
/// After disposal <see cref="Emit"/> and <see cref="Publish"/> are silent no-ops.
/// </para>
/// </remarks>
public sealed partial class DashboardSink : IMetricsSink, ILiveEventSink, IAsyncDisposable, IDisposable
{
    /// <summary>The capacity of the ingress channel and of every connection channel.</summary>
    internal const int Capacity = 1000;

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Channel<LiveEvent> _ingress;
    private readonly Lock _lock = new();

    // Guarded by _lock.
    private readonly DashboardSnapshot _snapshot;
    private readonly List<DashboardConnection> _connections = [];
    private readonly List<(long Target, TaskCompletionSource Done)> _flushes = [];
    private long _pumped;
    private bool _ended;

    private readonly Task _pump;
    private long _written;
    private long _dropped;
    private int _faults;
    private int _faultLogged;
    private int _degradedLogged;
    private int _disposed;

    /// <summary>Creates the sink and starts its pump.</summary>
    /// <param name="logger">The logger of the first pump failure.</param>
    /// <param name="timeProvider">The clock of the emit instants of the readings and of the snapshots.</param>
    /// <param name="redactor">The redactor of the host (the registered keys and the <c>nvapi-</c> pattern): every payload passes it before a connection sees it.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public DashboardSink(ILogger logger, TimeProvider timeProvider, SecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(redactor);
        _snapshot = new DashboardSnapshot(redactor) { DegradedObserver = ReportDegraded };

        _logger = logger;
        _time = timeProvider;
        _ingress = Channel.CreateBounded<LiveEvent>(
            // AllowSynchronousContinuations stays false on purpose: the pump must never run on the emitting thread (the sampling loop, a metered call), so a write only queues
            // the pump's continuation to the thread pool.
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false },
            _ => Interlocked.Increment(ref _dropped));
        _pump = PumpAsync();
    }

    /// <summary>The number of events the pump could not handle and dropped.</summary>
    public int PumpFaultCount => Volatile.Read(ref _faults);

    /// <inheritdoc />
    /// <remarks>The reading becomes one <see cref="StepCompletedEvent"/> stamped with the clock's current instant.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null.</exception>
    public void Publish(StepReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        Emit(new StepCompletedEvent(_time.GetUtcNow(), false, reading));
    }

    /// <inheritdoc />
    /// <remarks>One <c>TryWrite</c> into the ingress inside a catch-all; a full ingress drops its oldest event. Never awaits, never throws; a no-op after disposal.</remarks>
    public void Emit(LiveEvent liveEvent)
    {
        try
        {
            if (liveEvent is null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            // Counted before the write, so a flush that starts after this call returned always waits for this event, whichever writer got into the channel first; a write
            // that is refused (the sink was completed meanwhile) takes its count back, and a flush still waiting then is completed when the pump ends.
            _ = Interlocked.Increment(ref _written);
            if (!_ingress.Writer.TryWrite(liveEvent))
            {
                _ = Interlocked.Decrement(ref _written);
            }
        }
        catch (Exception)
        {
            // A live view must never reach the run (EDGE-019): whatever went wrong here is dropped.
        }
    }

    /// <summary>
    /// Subscribes a client: the returned connection holds the snapshot envelope of this instant and receives every payload of the events applied after it. After the sink ended the
    /// connection's events are already complete.
    /// </summary>
    /// <returns>The connection; the caller disposes it to unsubscribe.</returns>
    public DashboardConnection Connect()
    {
        lock (_lock)
        {
            Channel<string> channel = Channel.CreateBounded<string>(
                new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
            var connection = new DashboardConnection(_snapshot.ToSnapshotPayload(_time.GetUtcNow()), channel, _snapshot.AppliedCount, Unsubscribe);
            if (_ended)
            {
                connection.Complete();
            }
            else
            {
                _connections.Add(connection);
            }

            return connection;
        }
    }

    /// <summary>Completes when every event emitted before the call was applied by the pump or dropped by a full ingress, or when the sink ended.</summary>
    /// <param name="cancellationToken">Ends the wait (not the pump).</param>
    /// <returns>The wait.</returns>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        long target = Interlocked.Read(ref _written);
        lock (_lock)
        {
            if (_ended || SettledCount() >= target)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushes.Add((target, done));
            return done.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Completes the ingress, waits for the pump to apply what is left, and completes every connection; safe to call twice.</summary>
    /// <returns>A task that completes when the pump ended.</returns>
    public async ValueTask DisposeAsync()
    {
        Dispose();

        // The pump never ends faulted by design, and if it did, the fault would not be the disposer's business (as the meter's DisposeAsync).
        await _pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Completes the ingress without waiting; the pump applies what is left and then completes every connection. Safe to call twice.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = _ingress.Writer.TryComplete();
        }
    }

    // The events the pump settled plus those a full ingress dropped. Read under the lock, this never covers an event still in the ingress: the pump takes the oldest event, a full
    // ingress drops the oldest one it holds, and an event the pump took but has not counted yet (the first fault, while it is logged) was already taken from the ingress.
    private long SettledCount() => _pumped + Interlocked.Read(ref _dropped);

    private async Task PumpAsync()
    {
        ChannelReader<LiveEvent> reader = _ingress.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (PumpOne(reader))
                {
                }
            }
        }
        finally
        {
            End();
        }
    }

    // Takes one event and handles it under the lock; false when the ingress is empty. A failure drops the event and is logged after the lock is released, the first time only.
    private bool PumpOne(ChannelReader<LiveEvent> reader)
    {
        string? faultType = null;
        lock (_lock)
        {
            if (!reader.TryRead(out LiveEvent? next))
            {
                return false;
            }

            try
            {
                Broadcast(_snapshot.Apply(next));
                if (next is RunStartedEvent)
                {
                    Broadcast(_snapshot.ToSnapshotPayload(_time.GetUtcNow()));
                }
            }
            catch (Exception failure)
            {
                _ = Interlocked.Increment(ref _faults);
                if (Interlocked.Exchange(ref _faultLogged, 1) == 0)
                {
                    faultType = failure.GetType().Name;
                }
            }

            if (faultType is null)
            {
                _pumped++;
                CompleteFlushes(all: false);
                return true;
            }
        }

        // The first fault is logged outside the lock and before the event counts as settled, so a flush that covers it completes after the record. Until then the event
        // is not counted, which only makes a waiting flush wait longer.
        LogFaultSafely(_logger, faultType);
        lock (_lock)
        {
            _pumped++;
            CompleteFlushes(all: false);
        }

        return true;
    }

    // The caller holds the lock.
    private void Broadcast(string payload)
    {
        foreach (DashboardConnection connection in _connections)
        {
            connection.Write(payload);
        }
    }

    // The caller holds the lock. Every flush whose events are settled completes; at the end, every flush does.
    private void CompleteFlushes(bool all)
    {
        if (_flushes.Count == 0)
        {
            return;
        }

        long settled = SettledCount();
        for (int i = _flushes.Count - 1; i >= 0; i--)
        {
            if (all || _flushes[i].Target <= settled)
            {
                _ = _flushes[i].Done.TrySetResult();
                _flushes.RemoveAt(i);
            }
        }
    }

    private void End()
    {
        lock (_lock)
        {
            _ended = true;
            foreach (DashboardConnection connection in _connections)
            {
                connection.Complete();
            }

            _connections.Clear();
            CompleteFlushes(all: true);
        }
    }

    private void Unsubscribe(DashboardConnection connection)
    {
        lock (_lock)
        {
            _ = _connections.Remove(connection);
        }
    }

    // A logger that throws must not end the pump or reach a caller: its failure is dropped. Static and non-capturing, so PumpOne allocates no closure or delegate.
    private static void LogFaultSafely(ILogger logger, string exceptionType)
    {
        try
        {
            PumpFaulted(logger, exceptionType);
        }
        catch (Exception)
        {
            // Dropped on purpose (EDGE-019).
        }
    }

    // Called by the snapshot, under the lock, when a snapshot fell back to its smaller form. The first time per sink it is logged with the exception type only, from another thread, so a logger can never
    // run under the lock of the sink; a logger that throws is dropped (EDGE-019).
    private void ReportDegraded(string exceptionType)
    {
        if (Interlocked.Exchange(ref _degradedLogged, 1) == 0)
        {
            _ = ThreadPool.QueueUserWorkItem(static state => LogDegradedSafely(state.Logger, state.ExceptionType), (Logger: _logger, ExceptionType: exceptionType), preferLocal: false);
        }
    }

    private static void LogDegradedSafely(ILogger logger, string exceptionType)
    {
        try
        {
            SnapshotDegraded(logger, exceptionType);
        }
        catch (Exception)
        {
            // Dropped on purpose (EDGE-019).
        }
    }

    [LoggerMessage(EventId = 302, Level = LogLevel.Warning, Message = "The dashboard wrote a smaller snapshot because its state could not be serialized ({ExceptionType}); later occurrences are not logged")]
    private static partial void SnapshotDegraded(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 301, Level = LogLevel.Warning, Message = "The dashboard dropped a live event it could not handle ({ExceptionType}); the dashboard continues and later failures are counted, not logged")]
    private static partial void PumpFaulted(ILogger logger, string exceptionType);
}