using System.Threading.Channels;

namespace AgentJoules.Dashboard;

/// <summary>
/// One subscriber of a <see cref="DashboardSink"/> (one <c>/events</c> client): the snapshot envelope taken when it subscribed, then every payload the pump wrote after that, through a
/// channel of its own that is bounded at 1000 and drops its oldest payload when full, so a slow or absent browser loses its oldest events and nothing else. Disposing it unsubscribes
/// and completes <see cref="Events"/>.
/// </summary>
public sealed class DashboardConnection : IDisposable
{
    private readonly Channel<string> _channel;
    private readonly Action<DashboardConnection> _unsubscribe;
    private int _disposed;

    internal DashboardConnection(string snapshot, Channel<string> channel, long appliedAtConnect, Action<DashboardConnection> unsubscribe)
    {
        Snapshot = snapshot;
        _channel = channel;
        AppliedAtConnect = appliedAtConnect;
        _unsubscribe = unsubscribe;
    }

    /// <summary>The snapshot envelope at the instant of subscribing; every payload in <see cref="Events"/> comes from an event applied after it.</summary>
    public string Snapshot { get; }

    /// <summary>The payloads written after the snapshot, oldest first; completed when the connection or the sink is disposed.</summary>
    public ChannelReader<string> Events => _channel.Reader;

    /// <summary>The number of events the sink had applied when the snapshot was taken, for the subscribe tests.</summary>
    internal long AppliedAtConnect { get; }

    /// <summary>Unsubscribes and completes <see cref="Events"/>; the payloads already in it can still be read. Safe to call twice.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _unsubscribe(this);
            _ = _channel.Writer.TryComplete();
        }
    }

    // Called by the pump under the sink's lock; a full channel drops its oldest payload, a completed one drops this payload.
    internal void Write(string payload) => _ = _channel.Writer.TryWrite(payload);

    // Called by the sink under its lock when the sink ends.
    internal void Complete() => _ = _channel.Writer.TryComplete();
}