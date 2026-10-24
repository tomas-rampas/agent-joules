using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

using AgentJoules.Cli.Hosting;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Security;
using AgentJoules.Dashboard;
using AgentJoules.Reporting.Results;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentJoules.Cli.Commands;

/// <summary>
/// <c>dashboard --replay &lt;run file&gt;</c> (AJ-092, REQ-032): serves the same page as a live run and plays the recorded steps of a committed run file back on their timeline, so a judge without
/// a GPU sees the live view. The command runs in a host like <c>probe</c> (the default configuration, no telemetry, the redacting writers, the clock of the host and
/// <c>Report:RepositoryUrl</c>) and needs no API key: nothing here reads one. The checks stay in the command: the path resolves against the working directory, its file name must be a run file name,
/// and the file is read (strict UTF-8, at most <see cref="RunFileValidator.MaxBytes"/>) and checked like <c>validate</c> and <c>report</c> do, the failure lines on standard output and then exit 2; a step
/// whose agent is not one of the four configured roles (or the judge) is refused the same way, because a hostile file is the only way look-alike agents reach the page. The sink and the server are
/// owned by the command and disposed when the replay ends; <see cref="ReplayService"/> does the rest. Nothing is written to disk.
/// </summary>
internal static class DashboardCommand
{
    private const string Name = "dashboard";
    private const string JudgeAgent = "Judge";
    private const int PathEchoLength = 256;
    private const double MaximumSeconds = BaselineOptions.MaximumSeconds;

    internal static Command Create(CommandHost host)
    {
        var replay = new Option<string>("--replay")
        {
            Description = "The run file to replay: <timestamp>-<tier>[-baseline].json, relative to the working directory unless absolute.",
            HelpName = "run file",
            Required = true,
        };
        var speed = new Option<double>("--speed")
        {
            Description = "The speed of the replay: every recorded delay is divided by it (default 1.0).",
            HelpName = "factor",
            DefaultValueFactory = static _ => 1.0,
            CustomParser = static result => ParseSeconds(result, "--speed", allowZero: false, duration: false) ?? 1.0,
        };
        var exitAfter = new Option<double?>("--exit-after")
        {
            Description = "End the replay with exit 0 this many seconds after its first event (default: serve until Ctrl+C).",
            HelpName = "seconds",
            CustomParser = static result => ParseSeconds(result, "--exit-after", allowZero: false),
        };
        var startDelay = new Option<double?>("--start-delay")
        {
            Description = "Seconds to wait before the first event; an earlier first connection starts it sooner, 0 starts at once (default: wait for the first browser connection, at most 120 s or --exit-after).",
            HelpName = "seconds",
            CustomParser = static result => ParseSeconds(result, "--start-delay", allowZero: true),
        };
        Option<int> port = DashboardPortOption.Create();

        var command = new Command(Name, "Serve the live dashboard from a committed run file and replay its recorded steps on their timeline; needs no GPU and no API key.")
        {
            replay, speed, exitAfter, startDelay, port,
        };
        command.SetAction((parseResult, cancellationToken) =>
        {
            string file = parseResult.GetRequiredValue(replay);
            double factor = parseResult.GetValue(speed);
            TimeSpan? exit = Seconds(parseResult.GetResult(exitAfter) is null ? null : parseResult.GetValue(exitAfter));
            TimeSpan? delay = Seconds(parseResult.GetResult(startDelay) is null ? null : parseResult.GetValue(startDelay));
            int preferred = parseResult.GetValue(port);
            return host.RunAsync(
                configPath: null,
                configure: null,
                (running, token) => ReplayAsync(host, running, file, new ReplaySettings(factor, exit, delay, preferred, RepositoryUrl: null), token),
                cancellationToken);
        });
        return command;
    }

    // Ctrl+C is not a failure of a replay at any phase of the action, the read of the file included: a cancelled token ends it with 0 and nothing reaches the shutdown mapping of the host (exit 5).
    // A cancellation inside the host's own StartAsync, before the action runs, stays exit 5: CommandHost owns that phase.
    private static async Task<int> ReplayAsync(CommandHost host, IHost running, string given, ReplaySettings settings, CancellationToken cancellationToken)
    {
        try
        {
            return await PlayAsync(host, running, given, settings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static async Task<int> PlayAsync(CommandHost host, IHost running, string given, ReplaySettings settings, CancellationToken cancellationToken)
    {
        (string name, RunDocument document) = await ReadRunFileAsync(host, given, cancellationToken);
        RequireKnownAgents(host, name, document);

        AgentJoulesOptions options = running.Services.GetRequiredService<IOptions<AgentJoulesOptions>>().Value;
        Uri? repository = options.Report.RepositoryUrl is { } text ? new Uri(text, UriKind.Absolute) : null;
        TimeProvider time = running.Services.GetRequiredService<TimeProvider>();
        ILoggerFactory loggers = running.Services.GetRequiredService<ILoggerFactory>();

        // The sink and the server belong to the command and end with it; the redactor is the one of the host, so a registered key can never reach a payload or the page.
        var sink = new DashboardSink(loggers.CreateLogger<DashboardSink>(), time, running.Services.GetRequiredService<SecretRedactor>());
        var server = new DashboardServer(sink, loggers);
        try
        {
            return await new ReplayService(sink, server, time, host.Output, host.Error).RunAsync(document, settings with { RepositoryUrl = repository }, cancellationToken);
        }
        finally
        {
            await server.DisposeAsync();
            await DisposeSinkAsync(sink, cancellationToken);
        }
    }

    // The file of --replay: the name, then the strict bounded read and the checks of validate. A failure prints its lines on standard output and ends as exit 2 naming the file.
    private static async Task<(string Name, RunDocument Document)> ReadRunFileAsync(CommandHost host, string given, CancellationToken cancellationToken)
    {
        string echo = TextEcho.ForMessage(host.Redactor.Redact(given), PathEchoLength);
        if (string.IsNullOrWhiteSpace(given))
        {
            throw new ConfigurationException($"{Name} --replay: the path must not be blank");
        }

        string full;
        try
        {
            full = Path.GetFullPath(given, host.WorkingDirectory);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ConfigurationException($"{Name} --replay: {echo} is not a valid path");
        }

        string name = Path.GetFileName(full);
        if (!RunFileName.IsValid(name))
        {
            throw new ConfigurationException($"{Name} --replay: {echo} is not a run file name");
        }

        (IReadOnlyList<string> failures, RunDocument? document) = await ReportCommand.ReadAndCheckAsync(full, name, host.Redactor, cancellationToken);
        if (failures.Count > 0 || document is null)
        {
            foreach (string line in failures)
            {
                host.Output.WriteLine(line);
            }

            throw new ConfigurationException($"{Name} --replay: {name} failed validation");
        }

        return (name, document);
    }

    // A step whose agent is not an ordinal match of one of the four configured roles or the judge is refused with a validate-format line per step (never the agent name), so a file cannot put many or
    // look-alike agents on the page.
    private static void RequireKnownAgents(CommandHost host, string name, RunDocument document)
    {
        // The rule is ordinal and case-sensitive: "planner" is not "Planner". It covers the agent of every step and of every environment.models[] entry, because both reach the page.
        var lines = new List<string>();
        int stepOffenders = 0;
        for (int index = 0; index < document.Steps.Count; index++)
        {
            if (!IsKnownAgent(document.Steps[index].Agent))
            {
                stepOffenders++;
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: /steps/{index}/agent is not one of the configured agent roles"));
            }
        }

        for (int index = 0; index < document.Environment.Models.Count; index++)
        {
            if (!IsKnownAgent(document.Environment.Models[index].Agent))
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: /environment/models/{index}/agent is not one of the configured agent roles"));
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        foreach (string line in lines.Take(RunFileValidator.MaxFailureLines))
        {
            host.Output.WriteLine(line);
        }

        if (lines.Count > RunFileValidator.MaxFailureLines)
        {
            host.Output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: (root) and {lines.Count - RunFileValidator.MaxFailureLines} more failures"));
        }

        throw new ConfigurationException(stepOffenders > 0
            ? $"{Name} --replay: {name} has a step agent outside the configured roles"
            : $"{Name} --replay: {name} has an environment agent outside the configured roles");
    }

    private static bool IsKnownAgent(string agent) =>
        TierOptions.KnownAgentNames.Contains(agent, StringComparer.Ordinal) || string.Equals(agent, JudgeAgent, StringComparison.Ordinal);

    // After a shutdown the sink is completed without waiting for its pump; otherwise it is disposed within the same bound as the server's stop, on the wall clock.
    private static async Task DisposeSinkAsync(DashboardSink sink, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            sink.Dispose();
            return;
        }

        try
        {
            await sink.DisposeAsync().AsTask().WaitAsync(DashboardServer.StopTimeout, TimeProvider.System, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // The pump finishes in the background; nothing waits for it.
        }
    }

    private static TimeSpan? Seconds(double? value) => value is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    // The value of --speed, --exit-after or --start-delay: a finite number above 0 (from 0 for the delay) and at most 4294967.294, the limit of a timer. A duration (--exit-after, --start-delay) above 0
    // must also be at least one tick (0.0000001 s): a smaller one would truncate to 0 and be the opposite of what was asked. The error names the rule and not the value. A speed too small for the
    // recorded gaps is the check of the replay, once the timeline is known.
    private static double? ParseSeconds(ArgumentResult result, string option, bool allowZero, bool duration = true)
    {
        if (result.Tokens.Count == 1
            && double.TryParse(result.Tokens[0].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value)
            && ((value > 0.0 && (!duration || TimeSpan.FromSeconds(value) > TimeSpan.Zero)) || (allowZero && value == 0.0))
            && value <= MaximumSeconds)
        {
            return value;
        }

        string lowest = (allowZero, duration) switch
        {
            (true, true) => "0 or at least 0.0000001",
            (false, true) => "at least 0.0000001",
            _ => "greater than 0",
        };
        string unit = duration ? " of seconds" : string.Empty;
        result.AddError(string.Create(CultureInfo.InvariantCulture, $"{option} must be a number{unit} {lowest} and at most {MaximumSeconds}"));
        return null;
    }
}