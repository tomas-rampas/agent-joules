namespace AgentJoules.Core.Energy;

/// <summary>The window edge a <see cref="CounterMark"/> was taken at (design 3.2 "Union").</summary>
internal enum CounterEdge
{
    /// <summary>The mark was taken when a window opened.</summary>
    Open = 1,

    /// <summary>The mark was taken when a window closed.</summary>
    Close = 2,
}