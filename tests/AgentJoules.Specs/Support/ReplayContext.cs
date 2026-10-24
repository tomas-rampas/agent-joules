using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AgentJoules.Cli;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Dashboard;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-092 scenario (REQ-032), handed to the step class by Reqnroll context injection: the fixture run file of the scenario (read from the repository root, never a copy:
/// P-11), the <see cref="ReplayRig"/> (fake clock, real sink, server and client, ports from a <see cref="LoopbackPortBlock"/>, P-79) or a run of the real command through the
/// deny-by-default harness, and what the scenario observed. Every replay of every scenario runs on the fake clock (P-90): the clock moves only when a step advances it, after the client has connected.
/// </summary>
public sealed partial class ReplayContext : IAsyncDisposable
{
    /// <summary>The placeholder of the fixture run file in a command line of the feature.</summary>
    public const string FixtureToken = "<fixture>";

    /// <summary>The placeholder of the scenario's own file in a command line of the feature.</summary>
    public const string FileToken = "<file>";

    /// <summary>The file name of a file that is not a run file name.</summary>
    public const string NotARunFileName = "results.json";

    private const string PortOption = "--dashboard-port";

    private static readonly TimeSpan HangGuard = ReplayRig.HangGuard;
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(120);

    private TempWorkspace? _workspace;

    /// <summary>The tier of the fixture run file of the scenario.</summary>
    public Tier FixtureTier { get; private set; } = Tier.Local;

    /// <summary>The run document of the scenario, read from the committed fixture file or edited from it.</summary>
    public RunDocument? Run { get; private set; }

    /// <summary>The ports a scenario holds (a listener on each), or null.</summary>
    public LoopbackPortBlock? Held { get; private set; }

    /// <summary>The rig of a scenario that runs the replay on the fake clock, or null.</summary>
    public ReplayRig? Rig { get; private set; }

    /// <summary>The result of a command that ran to its end under the harness, or null.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The exit code the scenario observed, or null before the replay ended.</summary>
    public int? ExitCode { get; set; }

    /// <summary>The events the first client received.</summary>
    public SseLog? Log { get; set; }

    /// <summary>The events the reconnected client received.</summary>
    public SseLog? Second { get; set; }

    /// <summary>The timeline a scenario built.</summary>
    public IReadOnlyList<ReplayEntry> Entries { get; set; } = [];

    /// <summary>The state of the dashboard when the client dropped (the snapshot envelope of that instant).</summary>
    public string? SnapshotBeforeDrop { get; set; }

    /// <summary>The files of the working directory of the command before it ran.</summary>
    public string[] FilesBefore { get; private set; } = [];

    /// <summary>The path of the committed fixture run file of <see cref="FixtureTier"/>.</summary>
    public string FixturePath => ReplayRig.FixturePath(RunDocumentFixtures.CommittedFileName(FixtureTier));

    /// <summary>Reads the committed fixture run file of <paramref name="tier"/>.</summary>
    /// <param name="tier">The local, nvidia or nebius tier.</param>
    public void UseFixture(Tier tier)
    {
        FixtureTier = tier;
        Run = RunDocumentFixtures.Read(File.ReadAllText(FixturePath));
    }

    /// <summary>The local fixture with the first two steps 8 seconds apart: every later step is moved 6 seconds later, behind the second.</summary>
    public void UseShiftedFixture()
    {
        FixtureTier = Tier.Local;
        string text = RunDocumentFixtures.Edit(File.ReadAllText(FixturePath), static root =>
        {
            JsonArray steps = root["steps"]!.AsArray();
            for (int i = 1; i < steps.Count; i++)
            {
                JsonObject step = steps[i]!.AsObject();
                step["startUtc"] = Shifted(step["startUtc"]!.GetValue<string>(), 6);
                step["endUtc"] = Shifted(step["endUtc"]!.GetValue<string>(), 6);
            }
        });
        Run = RunDocumentFixtures.Read(text);
    }

    /// <summary>A working directory with a copy of the fixture under a name that is not a run file name.</summary>
    public void UseFileWithAnInvalidName()
    {
        _workspace = TempWorkspace.Create();
        _ = _workspace.WriteFile(NotARunFileName, File.ReadAllText(FixturePath));
    }

    /// <summary>Holds <paramref name="count"/> consecutive loopback ports; the first is the preferred port of the command (P-79).</summary>
    /// <param name="count">The number of ports.</param>
    public void HoldPorts(int count) => Held = LoopbackPortBlock.Reserve(count);

    /// <summary>Creates the rig of the scenario.</summary>
    /// <returns>The rig.</returns>
    public ReplayRig CreateRig() => Rig ??= ReplayRig.Create();

    /// <summary>
    /// Runs a command line of the feature to its end under the deny-by-default harness. <c>&lt;fixture&gt;</c> is the quoted path of the committed fixture and <c>&lt;file&gt;</c> the scenario's own
    /// file in its working directory. A <c>dashboard</c> command line that names no <c>--dashboard-port</c> gets <c>--dashboard-port &lt;port&gt;</c> appended (P-79): the unit tests and the
    /// scenarios run as two processes, so no scenario may bind the default port; the port is the first of the block the scenario holds, or the first of a block of free ports it reserved and released.
    /// </summary>
    /// <param name="arguments">The command line of the scenario.</param>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunAsync(string arguments)
    {
        string line = Expand(arguments);
        if (!line.Contains(PortOption, StringComparison.Ordinal))
        {
            line += " " + PortOption + " " + PreferredPort().ToString(CultureInfo.InvariantCulture);
        }

        _workspace ??= TempWorkspace.Create();
        Result = await CliHarness.RunAsync(line, new CliHostOverrides(WorkingDirectory: _workspace.Root)).WaitAsync(HangGuard);
        ExitCode = Result.ExitCode;
    }

    /// <summary>Starts the real command on the rig's fake clock, connects the client, and plays the replay out.</summary>
    /// <param name="arguments">The command line, with <c>&lt;fixture&gt;</c> for the fixture path.</param>
    /// <returns>A task that completes when the command ended and the client's stream ended.</returns>
    public async Task RunWithClientAsync(string arguments)
    {
        string line = Expand(arguments);
        ReplayRig rig = CreateRig();
        Task<int> exit = rig.StartCommand(line);
        SseLog log = await rig.ConnectAsync();
        Log = log;

        // The wait that was armed before the client connected is the shorter of 120 s and --exit-after; the clock of --exit-after starts at event 0.
        double speed = NumberOf(line, "--speed") ?? 1.0;
        TimeSpan startWait = TimeSpan.FromSeconds(Math.Min(StartWait.TotalSeconds, NumberOf(line, "--exit-after") ?? StartWait.TotalSeconds));
        await rig.EventZeroAsync(log, startWait, SpanOf(Run!) / speed);
        await rig.DriveToEndAsync();
        ExitCode = await exit.WaitAsync(HangGuard);
        await log.Completion.WaitAsync(HangGuard);
        Result = await rig.Command!;
    }

    /// <summary>Starts the real command on the rig's fake clock with a connected client and advances it three recorded seconds, so the replay is in progress.</summary>
    /// <returns>A task that completes when the first step has completed on the client.</returns>
    public async Task StartInProgressCommandAsync()
    {
        ReplayRig rig = CreateRig();
        _ = rig.StartCommand($"dashboard --replay \"{FixturePath}\"");
        FilesBefore = FilesOf(rig.WorkspaceRoot!);
        SseLog log = await rig.ConnectAsync();
        Log = log;
        await rig.EventZeroAsync(log, StartWait, TimeSpan.Zero);
        await rig.AdvanceAsync(3);
        await log.WaitForCountAsync("step-completed", 1, HangGuard);
    }

    /// <summary>The files below <paramref name="root"/>, as relative paths in ordinal order.</summary>
    /// <param name="root">The folder.</param>
    /// <returns>The paths.</returns>
    public static string[] FilesOf(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal)];

    /// <summary>The recorded span of a run: from the start of the run to the last step end.</summary>
    /// <param name="run">The run.</param>
    /// <returns>The span.</returns>
    public static TimeSpan SpanOf(RunDocument run) => run.Steps.Max(static step => step.EndUtc) - run.Run.StartUtc;

    /// <summary>The number of waits the replay makes up to the end of the <paramref name="steps"/>-th step: one per distinct offset after 0.</summary>
    /// <param name="entries">The timeline.</param>
    /// <param name="steps">The number of steps completed, from 1.</param>
    /// <returns>The number of timers to advance.</returns>
    public static int WaitsUntilStepsCompleted(IReadOnlyList<ReplayEntry> entries, int steps)
    {
        TimeSpan until = entries.Where(static entry => entry.Event is AgentJoules.Core.Live.StepCompletedEvent).ElementAt(steps - 1).Offset;
        return entries.Select(static entry => entry.Offset).Distinct().Count(offset => offset > TimeSpan.Zero && offset <= until);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Rig is not null)
        {
            await Rig.DisposeAsync();
        }

        Held?.Dispose();
        _workspace?.Dispose();
    }

    private string Expand(string arguments) => arguments
        .Replace(FixtureToken, "\"" + FixturePath + "\"", StringComparison.Ordinal)
        .Replace(FileToken, NotARunFileName, StringComparison.Ordinal);

    // The preferred port of a command whose line names none: the first of the held block, or the first of a block of free ports reserved and released.
    private int PreferredPort()
    {
        if (Held is { } held)
        {
            return held.First;
        }

        using LoopbackPortBlock free = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        return free.First;
    }

    private static double? NumberOf(string line, string option)
    {
        foreach (Match match in OptionValues().Matches(line))
        {
            if (string.Equals(match.Groups["option"].Value, option, StringComparison.Ordinal))
            {
                return double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?<option>--speed|--exit-after) (?<value>\d+(\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex OptionValues();

    private static string Shifted(string utc, int seconds) =>
        DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture).AddSeconds(seconds).ToString("O", CultureInfo.InvariantCulture);
}