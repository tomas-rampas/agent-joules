using System.Net;
using System.Net.Sockets;

namespace AgentJoules.TestSupport;

/// <summary>
/// Holds a block of consecutive loopback ports for a dashboard test (EDGE-018, P-79): <see cref="Reserve"/> binds a listener on <c>127.0.0.1</c> to each of <c>count</c> consecutive
/// ports from a random base in 20000 to 60000 and retries with another base when one of them is taken, so the test owns the block and passes <see cref="First"/> explicitly; no test
/// relies on the default port 5178, because the unit tests and the scenarios run as two processes at once. <see cref="Release"/> (or <see cref="Dispose"/>) closes every listener;
/// a test that needs free ports reserves a block and releases it before the server binds.
/// </summary>
public sealed class LoopbackPortBlock : IDisposable
{
    private const int LowestBase = 20000;
    private const int HighestPort = 60000;
    private const int MaxAttempts = 200;

    private readonly List<TcpListener> _listeners;

    private LoopbackPortBlock(int first, int count, List<TcpListener> listeners)
    {
        First = first;
        Count = count;
        _listeners = listeners;
    }

    /// <summary>The first port of the block.</summary>
    public int First { get; }

    /// <summary>The number of ports in the block.</summary>
    public int Count { get; }

    /// <summary>The last port of the block.</summary>
    public int Last => First + Count - 1;

    /// <summary>True while the listeners hold the ports.</summary>
    public bool Holding { get; private set; } = true;

    /// <summary>Holds <paramref name="count"/> consecutive loopback ports.</summary>
    /// <param name="count">The number of ports, 1 to 100.</param>
    /// <returns>The block; the caller disposes it.</returns>
    /// <exception cref="InvalidOperationException">No free block was found in 200 attempts.</exception>
    public static LoopbackPortBlock Reserve(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 100);

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // A port of the block that is taken (or excluded by the system) makes TryHold close what it bound; another base is tried.
            if (TryHold(Random.Shared.Next(LowestBase, HighestPort - count + 1), count) is { } block)
            {
                return block;
            }
        }

        throw new InvalidOperationException($"No block of {count} free loopback ports was found in {MaxAttempts} attempts.");
    }

    /// <summary>Holds the <paramref name="count"/> ports from <paramref name="first"/>, or returns null when one of them is taken: the check that a server let its port go.</summary>
    /// <param name="first">The first port.</param>
    /// <param name="count">The number of ports.</param>
    /// <returns>The block, or null; the caller disposes it.</returns>
    public static LoopbackPortBlock? TryHold(int first, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var listeners = new List<TcpListener>(count);
        try
        {
            for (int port = first; port < first + count; port++)
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listeners.Add(listener);
            }

            return new LoopbackPortBlock(first, count, listeners);
        }
        catch (SocketException)
        {
            foreach (TcpListener listener in listeners)
            {
                listener.Dispose();
            }

            return null;
        }
    }

    /// <summary>True when <paramref name="port"/> is in the block.</summary>
    /// <param name="port">The port.</param>
    /// <returns>True when First &lt;= port &lt;= Last.</returns>
    public bool Contains(int port) => port >= First && port <= Last;

    /// <summary>Closes every listener, so the ports are free again; safe to call twice.</summary>
    public void Release()
    {
        if (!Holding)
        {
            return;
        }

        Holding = false;
        foreach (TcpListener listener in _listeners)
        {
            listener.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose() => Release();
}