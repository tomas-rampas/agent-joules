namespace AgentJoules.Metering;

/// <summary>
/// The count and the total wait of the rate-limit retries of the call in flight (EDGE-006, design 3.3). The retry layer (<see cref="RateLimitRetryChatClient"/>)
/// writes it and the metering decorator (<see cref="MeteredChatClient"/>) reads it, which is how the two inner layers of an agent's pipeline share the
/// numbers of a call without an ambient context: one ledger per pipeline, handed to both, reset by the decorator at the start of every call.
/// </summary>
/// <remarks>
/// A pipeline makes one call at a time (the decorator refuses an overlapping call), so the ledger describes one call at a time; its members are still
/// guarded by a lock, because the decorator reads it on whatever thread the call finishes on.
/// </remarks>
public sealed class RetryLedger
{
    private readonly Lock _gate = new();

    private int _retries;
    private long _waitMs;

    /// <summary>The retries recorded since the last <see cref="Reset"/>.</summary>
    public int Retries
    {
        get
        {
            lock (_gate)
            {
                return _retries;
            }
        }
    }

    /// <summary>The summed waits, in whole milliseconds, recorded since the last <see cref="Reset"/>.</summary>
    public long WaitMs
    {
        get
        {
            lock (_gate)
            {
                return _waitMs;
            }
        }
    }

    /// <summary>Starts a new call: both numbers go back to 0.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _retries = 0;
            _waitMs = 0;
        }
    }

    /// <summary>Adds time that was waited without counting a retry: the part of a wait that passed before the call was cancelled, so that the latency of a cancelled call still excludes it.</summary>
    /// <param name="waited">The time waited; not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="waited"/> is negative.</exception>
    public void AddWait(TimeSpan waited)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(waited, TimeSpan.Zero);

        lock (_gate)
        {
            _waitMs += (long)Math.Round(waited.TotalMilliseconds);
        }
    }

    /// <summary>Records one retry and the wait that preceded it.</summary>
    /// <param name="delay">How long the call waited before sending the request again; not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is negative.</exception>
    public void Record(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        lock (_gate)
        {
            _retries++;
            _waitMs += (long)Math.Round(delay.TotalMilliseconds);
        }
    }
}