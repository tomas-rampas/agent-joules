using System.Text;

using AgentJoules.Cli;
using AgentJoules.Core.Results;
using AgentJoules.Dashboard;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentJoules.TestSupport;

/// <summary>
/// A writer that keeps what it was given and tells a test when the <c>Dashboard: &lt;uri&gt;</c> line was written (AJ-092): the replay prints it as soon as the server listens, and a test
/// connects its client from that line. Safe to read while the command writes.
/// </summary>
public sealed class SignalWriter : TextWriter
{
    private const string DashboardPrefix = "Dashboard: ";

    private readonly Lock _gate = new();
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _line = new();
    private readonly TaskCompletionSource<Uri> _dashboard = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>Completes with the URI of the first line that starts with <c>Dashboard: </c>.</summary>
    public Task<Uri> DashboardUri => _dashboard.Task;

    /// <summary>Everything written so far.</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    /// <inheritdoc />
    public override void Write(char value)
    {
        lock (_gate)
        {
            _ = _text.Append(value);
            if (value != '\n')
            {
                _ = _line.Append(value);
                return;
            }

            string line = _line.ToString().TrimEnd('\r');
            _ = _line.Clear();
            if (line.StartsWith(DashboardPrefix, StringComparison.Ordinal))
            {
                _ = _dashboard.TrySetResult(new Uri(line[DashboardPrefix.Length..], UriKind.Absolute));
            }
        }
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        foreach (char c in value ?? string.Empty)
        {
            Write(c);
        }
    }

    /// <summary>The lines written so far, without their terminators.</summary>
    /// <returns>The lines.</returns>
    public string[] Lines() => [.. Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => line.TrimEnd('\r'))];

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>The events one headless client received, in order, with waiting by predicate (AJ-092).</summary>
public sealed class SseLog
{
    private readonly Lock _gate = new();
    private readonly List<SseReceipt> _items = [];
    private readonly List<(Func<IReadOnlyList<SseReceipt>, bool> Predicate, TaskCompletionSource Done)> _waiters = [];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _ended;

    /// <summary>The events received so far.</summary>
    public IReadOnlyList<SseReceipt> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    /// <summary>The number of events of <paramref name="type"/> received so far.</summary>
    /// <param name="type">The type string of the envelope.</param>
    /// <returns>The count.</returns>
    public int Count(string type) => Items.Count(item => string.Equals(item.Type, type, StringComparison.Ordinal));

    /// <summary>Completes when the stream ended: the server stopped, or the client left.</summary>
    public Task Completion => _completion.Task;

    /// <summary>True once the server ended the stream (or the client left).</summary>
    public bool Ended
    {
        get
        {
            lock (_gate)
            {
                return _ended;
            }
        }
    }

    /// <summary>Completes when <paramref name="predicate"/> holds over the events received; fails when the stream ended first or the guard ran out.</summary>
    /// <param name="predicate">The condition.</param>
    /// <param name="guard">The real-time bound that only ends a test that already failed.</param>
    /// <returns>The wait.</returns>
    public Task WaitForAsync(Func<IReadOnlyList<SseReceipt>, bool> predicate, TimeSpan guard)
    {
        lock (_gate)
        {
            if (predicate(_items))
            {
                return Task.CompletedTask;
            }

            if (_ended)
            {
                return Task.FromException(new InvalidOperationException("The stream ended before the condition held; received: " + Describe()));
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((predicate, done));
            return done.Task.WaitAsync(guard);
        }
    }

    /// <summary>Waits until <paramref name="count"/> events of <paramref name="type"/> were received.</summary>
    /// <param name="type">The type string.</param>
    /// <param name="count">The count.</param>
    /// <param name="guard">The real-time bound.</param>
    /// <returns>The wait.</returns>
    public Task WaitForCountAsync(string type, int count, TimeSpan guard) =>
        WaitForAsync(items => items.Count(item => string.Equals(item.Type, type, StringComparison.Ordinal)) >= count, guard);

    internal void Add(SseReceipt receipt)
    {
        lock (_gate)
        {
            _items.Add(receipt);
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Predicate(_items))
                {
                    _ = _waiters[i].Done.TrySetResult();
                    _waiters.RemoveAt(i);
                }
            }
        }
    }

    internal void End()
    {
        lock (_gate)
        {
            _ended = true;
            _ = _completion.TrySetResult();
            foreach ((Func<IReadOnlyList<SseReceipt>, bool> _, TaskCompletionSource done) in _waiters)
            {
                _ = done.TrySetException(new InvalidOperationException("The stream ended before the condition held; received: " + Describe()));
            }

            _waiters.Clear();
        }
    }

    private string Describe() => string.Join(", ", _items.Select(static item => item.Type));
}

/// <summary>
/// The rig of the replay tests (AJ-092, P-90): a <see cref="TimerTrackingTimeProvider"/>, a block of loopback ports (the server binds the first free one, the preferred port is passed explicitly,
/// P-79), a real <see cref="DashboardSink"/> and <see cref="DashboardServer"/> and the one <see cref="HeadlessSseClient"/>. The replay runs either as <see cref="ReplayService"/> directly or as the
/// real <c>dashboard</c> command of the CLI under the deny-by-default harness, on the fake clock; the clock moves only when a test advances it, so a client always connects before it moves.
/// </summary>
public sealed class ReplayRig : IAsyncDisposable
{
    /// <summary>The real-time bound of every wait: it only ends a test that already failed.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private static readonly DateTimeOffset Origin = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TimelineTolerance = TimeSpan.FromMilliseconds(1);

    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<HeadlessSseConnection> _streams = [];
    private readonly List<Task> _readers = [];
    private TempWorkspace? _workspace;
    private DateTimeOffset? _eventZero;
    private TimeSpan? _timelineSpan;
    private SseLog? _holdLog;

    private ReplayRig()
    {
        Time = new TimerTrackingTimeProvider();
        Time.SetUtcNow(Origin);
        Ports = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        PreferredPort = Ports.First;

        // The block was only a reservation of free consecutive ports: it is released so that the server of the rig binds its first port.
        Ports.Release();
        Logger = new RecordingLogger();
        Sink = DashboardFixtures.NewSink(Logger, Time);
        Server = new DashboardServer(Sink, NullLoggerFactory.Instance);
    }

    /// <summary>The fake clock; it reports every one-shot timer created on it.</summary>
    public TimerTrackingTimeProvider Time { get; }

    /// <summary>The ports of the rig.</summary>
    public LoopbackPortBlock Ports { get; }

    /// <summary>The preferred port every replay of the rig is told to use.</summary>
    public int PreferredPort { get; }

    /// <summary>The logger of the sink.</summary>
    public RecordingLogger Logger { get; }

    /// <summary>The sink of a replay run as <see cref="ReplayService"/>; the command builds its own.</summary>
    public DashboardSink Sink { get; }

    /// <summary>The server of a replay run as <see cref="ReplayService"/>; the command builds its own.</summary>
    public DashboardServer Server { get; }

    /// <summary>The one HTTP client of the rig.</summary>
    public HeadlessSseClient Client { get; } = new();

    /// <summary>Standard output of the replay.</summary>
    public SignalWriter Output { get; } = new();

    /// <summary>Standard error of the replay.</summary>
    public SignalWriter Error { get; } = new();

    /// <summary>The exit code of the replay, once it ended.</summary>
    public Task<int>? Exit { get; private set; }

    /// <summary>The root of the working directory of the command, or null when the replay runs as the service.</summary>
    public string? WorkspaceRoot => _workspace?.Root;

    /// <summary>The result of the command run, or null when the replay runs as the service.</summary>
    public Task<CliRunResult>? Command { get; private set; }

    /// <summary>Creates a rig.</summary>
    /// <returns>The rig.</returns>
    public static ReplayRig Create() => new();

    /// <summary>The path of a committed fixture run file under the repository root.</summary>
    /// <param name="fileName">The file name.</param>
    /// <returns>The absolute path.</returns>
    public static string FixturePath(string fileName) => Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunDocumentFixtures.CommittedFolder, fileName);

    /// <summary>Starts <see cref="ReplayService"/> over the sink and the server of the rig, on the fake clock.</summary>
    /// <param name="run">The run to replay.</param>
    /// <param name="speed">The speed factor.</param>
    /// <param name="exitAfter">The <c>--exit-after</c> value, or null.</param>
    /// <param name="startDelay">The <c>--start-delay</c> value, or null.</param>
    /// <param name="repositoryUrl">The repository link, or null.</param>
    /// <returns>The task of the exit code.</returns>
    public Task<int> StartService(RunDocument run, double speed = 1.0, TimeSpan? exitAfter = null, TimeSpan? startDelay = null, Uri? repositoryUrl = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        var service = new ReplayService(Sink, Server, Time, Output, Error);
        var settings = new ReplaySettings(speed, exitAfter, startDelay, PreferredPort, repositoryUrl);
        Exit = Task.Run(() => service.RunAsync(run, settings, _cancellation.Token), CancellationToken.None);
        return Exit;
    }

    /// <summary>
    /// Starts the real command line on the fake clock under the deny-by-default harness. A command line without <c>--dashboard-port</c> gets the first port of the rig appended (P-79: the unit tests
    /// and the scenarios run as two processes, so no test may bind the default port). No key is configured: the replay needs none.
    /// </summary>
    /// <param name="arguments">The arguments without the program name.</param>
    /// <returns>The task of the exit code.</returns>
    public Task<int> StartCommand(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string line = arguments.Contains("--dashboard-port", StringComparison.Ordinal)
            ? arguments
            : arguments + " --dashboard-port " + PreferredPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _workspace = TempWorkspace.Create();
        var overrides = new CliHostOverrides(
            Services: services =>
            {
                _ = services.RemoveAll<TimeProvider>();
                _ = services.AddSingleton<TimeProvider>(Time);
            },
            WorkingDirectory: _workspace.Root,
            Stdout: Output,
            Stderr: Error);
        Command = CliHarness.Start(line, overrides, cancellationToken: _cancellation.Token).Result;
        Exit = Command.ContinueWith(static finished => finished.Result.ExitCode, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return Exit;
    }

    /// <summary>The URI the replay printed; fails at once when the replay ended without printing it.</summary>
    /// <returns>The URI.</returns>
    public async Task<Uri> UriAsync()
    {
        Task<Uri> printed = Output.DashboardUri;
        Task first = await Task.WhenAny(printed, Exit ?? (Task)printed).WaitAsync(HangGuard);
        if (!ReferenceEquals(first, printed))
        {
            // The replay ended first: its failure, or its exit code with what it wrote, is the failure of the test.
            int code = await Exit!;
            throw new InvalidOperationException($"The replay ended with exit code {code} before it printed its Dashboard line; stdout: {Output.Text}; stderr: {Error.Text}");
        }

        return await printed;
    }

    /// <summary>Opens <c>/events</c> once the replay printed its URI and reads the stream in the background into the returned log.</summary>
    /// <returns>The log of this connection; the first item is the snapshot.</returns>
    public async Task<SseLog> ConnectAsync()
    {
        Uri uri = await UriAsync();
        using var guard = new CancellationTokenSource(HangGuard);
        HeadlessSseConnection stream = await Client.OpenEventsAsync(uri, cancellationToken: guard.Token);
        var log = new SseLog();
        lock (_streams)
        {
            _streams.Add(stream);
            _readers.Add(Task.Run(() => ReadAsync(stream, log), CancellationToken.None));
        }

        return log;
    }

    /// <summary>Closes the connection the log belongs to, as a browser tab that is closed does; the log ends.</summary>
    /// <param name="log">The log returned by <see cref="ConnectAsync"/>.</param>
    /// <param name="index">The position of the connection among the connections of the rig, from 0.</param>
    /// <returns>A task that completes when the stream was closed and its reader ended.</returns>
    public async Task DisconnectAsync(SseLog log, int index)
    {
        ArgumentNullException.ThrowIfNull(log);

        HeadlessSseConnection stream;
        Task reader;
        lock (_streams)
        {
            stream = _streams[index];
            reader = _readers[index];
        }

        await stream.DisposeAsync();
        await reader.WaitAsync(HangGuard);
        if (!log.Ended)
        {
            throw new InvalidOperationException("The reader of the closed connection did not end its log.");
        }
    }

    /// <summary>
    /// Waits until the replay emitted event 0 (the client has received <c>run-started</c>) and remembers its instant, as the clock of <c>--exit-after</c> starts there. A start wait that was armed
    /// before the client connected (a 120 s or <c>--start-delay</c> timer) is dropped from the clock driver: it was cancelled when the client connected, and advancing the clock by it would move
    /// the clock past event 0.
    /// </summary>
    /// <param name="log">The log of the client that started the replay.</param>
    /// <param name="startWait">The duration of the start wait that may have been armed, or null for none.</param>
    /// <param name="timelineSpan">The recorded span of the timeline divided by the speed; after this much time since event 0 the driver holds until the client has <c>run-completed</c>.</param>
    /// <returns>The wait.</returns>
    public async Task EventZeroAsync(SseLog log, TimeSpan? startWait, TimeSpan timelineSpan)
    {
        ArgumentNullException.ThrowIfNull(log);

        await log.WaitForCountAsync("run-started", 1, HangGuard);
        _eventZero = log.Items.First(static item => item.Type == "run-started").AtUtc;
        _timelineSpan = timelineSpan;
        _holdLog = log;
        if (startWait is { } wait && Time.DueTimes.TryPeek(out TimeSpan armed) && armed == wait)
        {
            _ = Time.DueTimes.TryRead(out _);
        }
    }

    /// <summary>Waits until the replay armed its next wait (or ended): everything due so far has been emitted.</summary>
    /// <returns>The wait.</returns>
    public async Task QuiescentAsync()
    {
        Task next = Time.DueTimes.WaitToReadAsync().AsTask();
        _ = await Task.WhenAny(Exit ?? throw new InvalidOperationException("No replay was started."), next).WaitAsync(HangGuard);
    }

    /// <summary>
    /// Advances the clock by the next <paramref name="timers"/> waits of the replay, each when the replay has armed it (so everything before it has been emitted). The last advance is not waited for: a test
    /// waits for its effect on the client, or with <see cref="QuiescentAsync"/> for the next wait.
    /// </summary>
    /// <param name="timers">The number of waits to advance.</param>
    /// <returns>The wait.</returns>
    public async Task AdvanceAsync(int timers)
    {
        for (int i = 0; i < timers; i++)
        {
            await QuiescentAsync();
            if (!Time.DueTimes.TryRead(out TimeSpan due))
            {
                return;
            }

            Time.Advance(due);
        }
    }

    /// <summary>
    /// Drives the clock until the replay ended: each wait is advanced when it is armed (<see cref="FakeTimeDriver"/>). When <see cref="EventZeroAsync"/> told the rig the span of the timeline, the
    /// clock is held before the first advance past it until the client has received <c>run-completed</c>, so the server is not stopped while events are on their way to the client.
    /// </summary>
    /// <returns>The wait.</returns>
    public Task DriveToEndAsync() =>
        FakeTimeDriver.RunWaitsToCompletionAsync(Exit ?? throw new InvalidOperationException("No replay was started."), Time, guard: HangGuard, beforeStep: HoldAsync);

    /// <summary>Cancels the token of the replay, as Ctrl+C does.</summary>
    public void Cancel() => _cancellation.Cancel();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        if (Exit is { } exit)
        {
            try
            {
                _ = await exit.WaitAsync(HangGuard);
            }
            catch (Exception failure) when (failure is TimeoutException or OperationCanceledException)
            {
                // A replay that did not end after its token was cancelled already failed its test.
            }
        }

        HeadlessSseConnection[] streams;
        lock (_streams)
        {
            streams = [.. _streams];
        }

        foreach (HeadlessSseConnection stream in streams)
        {
            await stream.DisposeAsync();
        }

        await Task.WhenAll(_readers).WaitAsync(HangGuard);
        Client.Dispose();
        await Server.DisposeAsync();
        await Sink.DisposeAsync();
        Ports.Dispose();
        _workspace?.Dispose();
        _cancellation.Dispose();
    }

    private async Task HoldAsync()
    {
        if (_eventZero is { } zero && _timelineSpan is { } span && _holdLog is { } log && Time.GetUtcNow() - zero >= span - TimelineTolerance)
        {
            await log.WaitForCountAsync("run-completed", 1, HangGuard);
        }
    }

    private static async Task ReadAsync(HeadlessSseConnection stream, SseLog log)
    {
        try
        {
            await foreach (SseReceipt receipt in stream.ReadEventsAsync(CancellationToken.None))
            {
                log.Add(receipt);
            }
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException or OperationCanceledException or System.Net.Http.HttpRequestException)
        {
            // The stream was closed by the client or reset by the server: the log ends.
        }
        finally
        {
            log.End();
        }
    }
}