namespace AgentJoules.TestSupport;

/// <summary>
/// A <see cref="TimeProvider"/> whose <see cref="GetUtcNow"/> returns one fixed instant carrying a non-zero offset, so a test can tell code that
/// reads the UTC value of the instant (<c>UtcDateTime</c>) from code that reads its local clock face (<c>DateTime</c>): the two differ by the
/// offset. A real provider returns offset zero; this one is deliberately not a faithful clock.
/// </summary>
/// <param name="instant">The instant <see cref="GetUtcNow"/> returns, with its offset.</param>
public sealed class OffsetClock(DateTimeOffset instant) : TimeProvider
{
    /// <summary>2026-10-19T14:30:15+05:00, which is 2026-10-19T09:30:15Z.</summary>
    public static OffsetClock FivePlus { get; } = new(new DateTimeOffset(2026, 10, 19, 14, 30, 15, TimeSpan.FromHours(5)));

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => instant;
}