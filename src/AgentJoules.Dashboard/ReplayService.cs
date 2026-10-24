using System.Globalization;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;

namespace AgentJoules.Dashboard;

/// <summary>
/// Replays a committed run file on its recorded timeline (REQ-032, AJ-092): it starts the server of the dashboard, prints <c>Dashboard: &lt;uri&gt;</c>, waits for the first client or the start
/// delay, and then emits the events of <see cref="ReplayTimeline"/> into the same sink, server and page as a live run, each after its recorded delay divided by the speed, on the injected
/// <see cref="TimeProvider"/>. Every event is stamped with its emit instant on that clock.
/// </summary>
/// <remarks>
/// <para>
/// Until the first client connects (<see cref="DashboardSink.FirstClientConnected"/>) or the start delay passes, nothing is emitted. Without a start delay and without a client the replay
/// waits for the shorter of 120 s and <c>--exit-after</c>, then writes <c>Warning: no browser connected to the dashboard within &lt;n&gt; s; the replay ends</c> to the error writer and returns 0.
/// <c>--exit-after</c> counts from event 0 in wall seconds on the clock, not scaled by the speed; after the last event the server keeps serving until that deadline, or until the token is cancelled
/// without one. Ctrl+C is not a failure: a cancelled token ends the replay with exit 0, so a replay never takes the shutdown path of a measured run. The replay holds no grace: the server is disposed
/// when <see cref="RunAsync"/> returns. Nothing is written to disk.
/// </para>
/// <para>
/// Each wait is one timer at a time on the clock (the deadline shortens a wait; it is not a second timer), so a clock that is driven by the timers it is asked for plays the replay without
/// reordering it.
/// </para>
/// </remarks>
/// <param name="sink">The sink the events go to and whose first client starts the replay.</param>
/// <param name="server">The server over <paramref name="sink"/>; started by the replay and disposed when it ends.</param>
/// <param name="timeProvider">The clock of every delay and of the emit instants.</param>
/// <param name="output">Standard output: the <c>Dashboard:</c> line.</param>
/// <param name="error">Standard error: the no-client warning.</param>
public sealed class ReplayService(DashboardSink sink, DashboardServer server, TimeProvider timeProvider, TextWriter output, TextWriter error)
{
    /// <summary>How long the replay waits for the first client when neither a start delay nor a shorter <c>--exit-after</c> is given.</summary>
    public static readonly TimeSpan NoClientTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The message of a speed so small that a recorded gap divided by it would exceed the timer limit (exit 2); it names the option and never the value.</summary>
    public const string SpeedTooSmallMessage = "dashboard --replay: --speed is too small for the recorded gaps (a delay would exceed the timer limit)";

    // The longest delay a timer takes (4294967.294 s, the limit of Task.Delay); a longer wait is made of several.
    private static readonly TimeSpan TimerLimit = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    private readonly DashboardSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly DashboardServer _server = server ?? throw new ArgumentNullException(nameof(server));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly TextWriter _error = error ?? throw new ArgumentNullException(nameof(error));

    /// <summary>Starts the server and plays <paramref name="run"/> back.</summary>
    /// <param name="run">The run read and checked by the caller.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">Cancelled by Ctrl+C: the replay ends with 0.</param>
    /// <returns>0 when the replay ended (the deadline, no client, or the token); a failure to start the server is an <see cref="AgentJoulesException"/> with exit code 5.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> or <paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting is outside its range.</exception>
    /// <exception cref="AgentJoulesException">The server did not start: <see cref="ExitCode.Unreachable"/> with the reason of the server (<c>no free dashboard port in &lt;first&gt;-&lt;last&gt;</c> among them).</exception>
    public async Task<int> RunAsync(RunDocument run, ReplaySettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        try
        {
            IReadOnlyList<ReplayEntry> timeline = ReplayTimeline.Build(run);

            // Before the server starts and before any output: a speed for which a recorded gap would not fit one timer is refused, so no delay can overflow later.
            RequireDelaysWithinTimerLimit(timeline, settings.Speed);
            Uri uri = await _server.TryStartAsync(settings.PreferredPort, settings.RepositoryUrl, cancellationToken).ConfigureAwait(false)
                ?? throw new AgentJoulesException(ExitCode.Unreachable, _server.FailureReason ?? DashboardServer.NoFreePortMessage(settings.PreferredPort));
            _output.WriteLine("Dashboard: " + uri.AbsoluteUri);

            if (!await WaitForStartAsync(settings, cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            await PlayAsync(timeline, settings, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C or SIGTERM: the replay stops by itself and exits 0; it is not the shutdown (exit 5) of a measured run.
            return 0;
        }
        finally
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Refuses a speed for which any gap between two consecutive entries, divided by the speed, exceeds the limit of a timer (4294967.294 s).</summary>
    /// <param name="timeline">The timeline.</param>
    /// <param name="speed">The speed factor.</param>
    /// <exception cref="ConfigurationException">A delay would exceed the timer limit (exit 2).</exception>
    internal static void RequireDelaysWithinTimerLimit(IReadOnlyList<ReplayEntry> timeline, double speed)
    {
        TimeSpan previous = TimeSpan.Zero;
        foreach (ReplayEntry entry in timeline)
        {
            if ((entry.Offset - previous).TotalSeconds / speed > BaselineOptions.MaximumSeconds)
            {
                throw new ConfigurationException(SpeedTooSmallMessage);
            }

            previous = entry.Offset;
        }
    }

    private static void Validate(ReplaySettings settings)
    {
        if (!double.IsFinite(settings.Speed) || settings.Speed <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "The speed must be finite and above 0.");
        }

        if (settings.ExitAfter is { } exitAfter && exitAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "--exit-after must be above 0.");
        }

        if (settings.StartDelay is { } delay && delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "--start-delay must not be negative.");
        }

        if ((settings.ExitAfter ?? TimeSpan.Zero) > TimerLimit || (settings.StartDelay ?? TimeSpan.Zero) > TimerLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "--exit-after and --start-delay must not exceed the limit of a timer.");
        }
    }

    // True when the replay starts now. False after the warning: no client came within the shorter of 120 s and --exit-after.
    private async Task<bool> WaitForStartAsync(ReplaySettings settings, CancellationToken cancellationToken)
    {
        Task firstClient = _sink.FirstClientConnected;
        bool delayed = settings.StartDelay is not null;
        TimeSpan wait = settings.StartDelay ?? (settings.ExitAfter is { } exitAfter && exitAfter < NoClientTimeout ? exitAfter : NoClientTimeout);

        // A client that is already there, or a start delay of 0, starts the replay at once: no timer is made.
        if (firstClient.IsCompleted || (delayed && wait == TimeSpan.Zero))
        {
            return true;
        }

        using CancellationTokenSource cancelWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task timer = Task.Delay(wait, _time, cancelWait.Token);
        Task first = await Task.WhenAny(firstClient, timer).ConfigureAwait(false);
        if (ReferenceEquals(first, firstClient) || firstClient.IsCompleted)
        {
            // An earlier client cuts a longer delay (or the wait for a client) short, without a warning.
            await cancelWait.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(timer).ConfigureAwait(false);
            return true;
        }

        // The wait ran out (or the token was cancelled, which throws here).
        await timer.ConfigureAwait(false);
        if (delayed)
        {
            return true;
        }

        _error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Warning: no browser connected to the dashboard within {wait.TotalSeconds:0.###} s; the replay ends"));
        return false;
    }

    // Emits the timeline: every entry after its recorded delay divided by the speed, until the deadline of --exit-after (counted from event 0); then serves until the deadline or the token.
    private async Task PlayAsync(IReadOnlyList<ReplayEntry> timeline, ReplaySettings settings, CancellationToken cancellationToken)
    {
        long eventZero = _time.GetTimestamp();
        TimeSpan previous = TimeSpan.Zero;
        foreach (ReplayEntry entry in timeline)
        {
            TimeSpan delay = (entry.Offset - previous) / settings.Speed;
            if (!await WaitAsync(delay, eventZero, settings.ExitAfter, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            previous = entry.Offset;
            _sink.EmitSafely(entry.Event with { AtUtc = _time.GetUtcNow() });

            // The replay waits until the sink applied the event: its 1,000-slot ingress drops its oldest event when a burst outruns the pump (P-42), which a replay must not do whatever the speed or the
            // gaps. Only the replay may wait on the sink: it measures nothing, so the rule that a producer of a measured run never blocks on the dashboard is not touched.
            await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (settings.ExitAfter is { } limit)
        {
            await DelayAsync(limit - _time.GetElapsedTime(eventZero), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Without --exit-after the server serves until Ctrl+C (the cancellation ends this wait).
        await Task.Delay(Timeout.InfiniteTimeSpan, _time, cancellationToken).ConfigureAwait(false);
    }

    // Waits for delay, or for what is left of --exit-after when that is shorter; false when the deadline came first (the wait is then over). An event scheduled exactly at the deadline is still emitted.
    private async Task<bool> WaitAsync(TimeSpan delay, long eventZero, TimeSpan? exitAfter, CancellationToken cancellationToken)
    {
        if (exitAfter is not { } limit)
        {
            await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }

        TimeSpan left = limit - _time.GetElapsedTime(eventZero);
        if (delay > left)
        {
            await DelayAsync(left, cancellationToken).ConfigureAwait(false);
            return false;
        }

        await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // The cancelled wait has nothing left to report; its cancellation is the expected end.
    private static async Task ObserveAsync(Task cancelled)
    {
        try
        {
            await cancelled.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled on purpose.
        }
    }

    // One timer at a time; a delay beyond the limit of a timer is made of several.
    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        TimeSpan remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            TimeSpan chunk = remaining < TimerLimit ? remaining : TimerLimit;
            await Task.Delay(chunk, _time, cancellationToken).ConfigureAwait(false);
            remaining -= chunk;
        }
    }
}