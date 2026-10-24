namespace AgentJoules.Dashboard;

/// <summary>The settings of a replay (REQ-032, AJ-092), checked by the command line parser before they reach <see cref="ReplayService"/>.</summary>
/// <param name="Speed">The speed factor of the recorded timeline: every recorded delay is divided by it. Finite and above 0.</param>
/// <param name="ExitAfter">The wall-clock time from event 0 after which the replay ends with exit 0, not scaled by the speed; null to serve until the token is cancelled. Above 0 when given.</param>
/// <param name="StartDelay">The delay before event 0: the replay starts at the first of the first client and this delay (0 starts at once); null to wait for the first client, at most 120 s or <paramref name="ExitAfter"/>.</param>
/// <param name="PreferredPort">The preferred loopback port, 1 to 65535; the next 19 are tried when it is taken.</param>
/// <param name="RepositoryUrl">The repository link of the page (<c>Report:RepositoryUrl</c>), or null for the not-configured sentence.</param>
public sealed record ReplaySettings(double Speed, TimeSpan? ExitAfter, TimeSpan? StartDelay, int PreferredPort, Uri? RepositoryUrl);