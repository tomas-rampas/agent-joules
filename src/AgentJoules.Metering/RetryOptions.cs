namespace AgentJoules.Metering;

/// <summary>
/// The rate-limit retry policy of a run (EDGE-006, ADR-11): at most <see cref="MaxAttempts"/> HTTP attempts per call, and between two attempts a wait of
/// full jitter over <c>min(<see cref="MaxDelay"/>, <see cref="BaseDelay"/> x <see cref="Factor"/>^n)</c>, where n is 0 for the first retry. A
/// <c>Retry-After</c> header of the 429 answer replaces the computed wait.
/// </summary>
public sealed record RetryOptions
{
    /// <summary>The policy of the design: 5 attempts, a 1 second base, a factor of 2 and a 30 second cap.</summary>
    public static RetryOptions Default { get; } = new();

    /// <summary>The most HTTP attempts of one call, the first one included; at least 1. The fifth 429 of the default policy ends the call with a <see cref="AgentJoules.Core.Cli.RetriesExhaustedException"/>.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>The ceiling of the wait before the first retry, before jitter; not negative.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How much the ceiling of the wait grows per retry; at least 1.</summary>
    public double Factor { get; init; } = 2.0;

    /// <summary>The longest <c>Retry-After</c> that is honoured; a valid header above it ends the call at once with a <see cref="AgentJoules.Core.Cli.RetriesExhaustedException"/> (no wait, no jitter fallback). Positive; 60 seconds by default, as the hosted endpoints limit per minute.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The most a single call may wait in total over all its retries; a wait that would exceed it ends the call at once with a <see cref="AgentJoules.Core.Cli.RetriesExhaustedException"/>. Positive; 120 seconds by default.</summary>
    public TimeSpan MaxTotalWait { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>The ceiling of every computed wait, before jitter; not above <see cref="MaxRetryAfter"/>. It does not cap a <c>Retry-After</c> header, which <see cref="MaxRetryAfter"/> bounds.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
}