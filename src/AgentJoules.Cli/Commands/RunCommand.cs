using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

using AgentJoules.Cli.Hosting;
using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Core.Tasks;
using AgentJoules.Dashboard;
using AgentJoules.Metering;
using AgentJoules.Reporting.Results;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Runs;
using AgentJoules.Workflow.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentJoules.Cli.Commands;

/// <summary>
/// <c>run</c> (REQ-017, design 3.6): runs the workflow on one tier or on every enabled tier and measures it; with <c>--dry-run</c> it checks everything a run needs and makes no
/// completion call. Invalid option values (<c>--tier bogus</c>, <c>--mode bogus</c>, <c>--repeat 0</c>, <c>--baseline-seconds 0</c>, a blank <c>--out</c>) are parse errors, so the command line exits 2
/// before any host is built. The host of <c>run</c> registers the telemetry of REQ-014 (the console metrics exporter unless <c>--no-console-telemetry</c> or <c>--dry-run</c> is given),
/// which <c>probe</c> never does, and the services of a measured run: the meter gate of <c>--mode</c>, the agent factory and the instruments (AJ-056).
/// </summary>
/// <remarks>
/// <para>
/// Both paths select the tiers and load the task set of <c>Paths:Tasks</c> under the content root first (a configuration error is exit 2, an unknown <c>--task</c> lists the names).
/// A dry run then runs <see cref="DryRunService"/> (keys with the judge key, models, the GPU of a measured tier) and prints <c>&lt;tier&gt; &lt;endpoint&gt; &lt;matched ids&gt; OK</c>
/// per selected tier; nothing is written and <c>--out</c> is not touched.
/// </para>
/// <para>
/// Outside a dry run the order is: the key check (a missing judge key only warns), one <see cref="ResultsFileWriter"/> per tier in <c>--out</c> (its constructor probes the directory,
/// so an unwritable directory exits 1 before any request), the model check, the GPU check of a measured tier, and then the <see cref="IRunOrchestrator"/> with the <see cref="RunPlan"/>.
/// The start of the command is taken once from the registered clock, before any check: every file name of the command and every <c>run.startUtc</c> carry it. The sampling-fallback line
/// is printed once per command, not once per measured tier, and so is the energy-scope sentence (<see cref="ConsoleReporter.ReportEnergyScope"/>, prompt honesty rule 6) on stdout, after the checks and
/// before the first step line; a dry run prints no joules and so no such sentence.
/// </para>
/// <para>
/// With <c>--dashboard</c> (AJ-091) the command registers the <see cref="DashboardSink"/> as its live sink and, after the GPU check, starts the <see cref="DashboardServer"/> on
/// <c>--dashboard-port</c> or one of the next 19 ports and prints <c>Dashboard: &lt;uri&gt;</c>, or warns on stderr and runs without it; the energy-scope sentence still comes last before the
/// first step line. The server stays up for <see cref="DashboardServer.GracePeriod"/> after the orchestrator returned or failed (at once after a shutdown) and is then disposed with the sink, both
/// bounded; a dry run starts it, prints the line and disposes it at once.
/// </para>
/// </remarks>
internal static class RunCommand
{
    /// <summary>The default of <c>--out</c>, relative to the content root.</summary>
    internal const string DefaultOut = "results";

    /// <summary>The default of <c>--repeat</c> (REQ-021).</summary>
    internal const int DefaultRepeat = 3;

    private const string Serialized = "serialized";
    private const string Concurrent = "concurrent";

    private static readonly string[] TierValues = [.. TierOptions.KnownNames, "all"];

    internal static Command Create(CommandHost host)
    {
        var tier = new Option<string>("--tier") { Description = "The tier to run: local, cloudgpu, nvidia, nebius, or all enabled tiers in that order.", Required = true };
        tier.AcceptOnlyFromAmong(TierValues);
        var task = new Option<string>("--task") { Description = "Run only the task of this name (default: every task of Paths:Tasks)." };
        var mode = new Option<string>("--mode") { Description = "serialized: one metered call at a time; concurrent: parallel branches overlap.", DefaultValueFactory = static _ => Serialized };
        mode.AcceptOnlyFromAmong(Serialized, Concurrent);
        var output = new Option<string>("--out") { Description = "The folder of the results files, relative to the working directory unless absolute.", DefaultValueFactory = static _ => DefaultOut };
        output.Validators.Add(static result => RequireNotBlank(result, "--out must not be blank"));
        var baseline = new Option<int?>("--baseline-seconds")
        {
            Description = "The idle baseline of a measured tier in seconds (default: Baseline:Seconds, 30).",
            CustomParser = static result => WholeNumber(result, "--baseline-seconds must be a whole number of seconds"),
        };
        baseline.Validators.Add(static result => RequireBaselineRange(result));
        var repeat = new Option<int>("--repeat")
        {
            Description = "The repeats of every task (1 or more).",
            DefaultValueFactory = static _ => DefaultRepeat,
            CustomParser = static result => WholeNumber(result, "--repeat must be a whole number") ?? 0,
        };
        repeat.Validators.Add(static result => RequireAtLeast(result, 1, "--repeat must be 1 or more"));
        var dryRun = new Option<bool>("--dry-run") { Description = "Check the configuration, the keys, the model IDs and the GPU, print one line per tier, and make no completion call." };
        var dashboard = new Option<bool>("--dashboard") { Description = "Serve the live dashboard on a loopback port while the run measures; a dashboard failure never stops the run." };
        Option<int> dashboardPort = DashboardPortOption.Create();
        Option<bool> noConsoleTelemetry = TelemetryOptions.CreateNoConsoleTelemetry();
        var config = new Option<string>("--config") { Description = "Path of the configuration file (default config/appsettings.json)." };

        var command = new Command("run", "Run the agent workflow on a tier and measure it; --dry-run checks everything without a completion call.")
        {
            tier, task, mode, output, baseline, repeat, dryRun, dashboard, dashboardPort, noConsoleTelemetry, config,
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            var request = new RunRequest(
                parseResult.GetRequiredValue(tier),
                parseResult.GetValue(task),
                ParseMode(parseResult.GetRequiredValue(mode)),
                parseResult.GetRequiredValue(output),
                parseResult.GetValue(baseline),
                parseResult.GetValue(repeat),
                parseResult.GetValue(dryRun),
                parseResult.GetValue(dashboard),
                parseResult.GetValue(dashboardPort));

            // A dry run records no metric, and the console exporter would still print the resource block of the SDK after the tier lines when the host is disposed, so a dry
            // run registers the telemetry (the OTLP settings are still checked) without the console exporter.
            bool consoleEnabled = !parseResult.GetValue(noConsoleTelemetry) && !request.DryRun;

            return host.RunAsync(
                parseResult.GetValue(config),
                (services, configuration) => AddRunServices(services, configuration, request.Mode, consoleEnabled, request.Dashboard),
                (running, token) => ExecuteAsync(running, host.Output, request, token),
                cancellationToken);
        });
        return command;
    }

    // The services of run only (AJ-054, AJ-056): the telemetry of REQ-014, the one meter gate of the process chosen by --mode (REQ-013), the agent factory of a tier run and the
    // OpenTelemetry instruments every reading is recorded on (over the meter factory the host registers). Applied after the composition root and before the test overrides. With --dashboard (AJ-091,
    // P-74) the command's live sink is the one dashboard sink of the process, over the host's clock and redactor; without it the root's null live sink stays and no dashboard type is built.
    private static void AddRunServices(IServiceCollection services, IConfiguration configuration, RunMode mode, bool consoleEnabled, bool dashboard)
    {
        _ = TelemetrySetup.Add(services, configuration, consoleEnabled);
        _ = services.AddAgentJoulesMeterGate(mode).AddAgentJoulesAgentFactory();
        services.TryAddSingleton<AgentJoulesInstruments>();
        if (dashboard)
        {
            _ = services.AddSingleton(static provider => new DashboardSink(
                provider.GetRequiredService<ILoggerFactory>().CreateLogger<DashboardSink>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<SecretRedactor>()));
            _ = services.Replace(ServiceDescriptor.Singleton<ILiveEventSink>(static provider => provider.GetRequiredService<DashboardSink>()));
        }
    }

    // A validator runs only when the option was given. The token is read here rather than the converted value, because reading the value of a token that does not convert throws;
    // a token that is not an integer is left to the parser, which reports it as a parse error of its own.
    private static void RequireAtLeast(OptionResult result, int minimum, string message)
    {
        if (result.Tokens.Count == 1
            && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value < minimum)
        {
            result.AddError(message);
        }
    }

    // The parser of a whole-number option: the default one names the .NET type of the option in its error, so a token that is not a whole number gets a plain message.
    private static int? WholeNumber(ArgumentResult result, string message)
    {
        if (result.Tokens.Count == 1 && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        result.AddError(message);
        return null;
    }

    // The rule of Baseline:Seconds (BaselineOptions): greater than 0 and at most the longest wait of the meter's timer; the flag takes whole seconds.
    private static void RequireBaselineRange(OptionResult result)
    {
        if (result.Tokens.Count == 1
            && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && (value <= 0 || value > BaselineOptions.MaximumSeconds))
        {
            result.AddError(string.Create(
                CultureInfo.InvariantCulture,
                $"--baseline-seconds must be greater than 0 and at most {BaselineOptions.MaximumSeconds}, the rule of Baseline:Seconds"));
        }
    }

    private static void RequireNotBlank(OptionResult result, string message)
    {
        if (result.Tokens.Count == 1 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
        {
            result.AddError(message);
        }
    }

    private static RunMode ParseMode(string value) => value switch
    {
        Serialized => RunMode.Serialized,
        Concurrent => RunMode.Concurrent,
        _ => throw new ConfigurationException("--mode must be serialized or concurrent; the value is not shown"),
    };

    private static async Task<int> ExecuteAsync(IHost running, TextWriter output, RunRequest request, CancellationToken cancellationToken)
    {
        IServiceProvider services = running.Services;

        // The start of the command, taken once: every run file name of the command and every run.startUtc carry it (design 5.4).
        DateTimeOffset startUtc = services.GetRequiredService<TimeProvider>().GetUtcNow();
        PreflightService preflight = services.GetRequiredService<PreflightService>();
        AgentJoulesOptions options = services.GetRequiredService<IOptions<AgentJoulesOptions>>().Value;
        string contentRoot = services.GetRequiredService<IHostEnvironment>().ContentRootPath;

        IReadOnlyList<Tier> tiers = preflight.SelectTiers(request.Tier);
        IReadOnlyList<TaskDefinition> tasks = LoadTasks(contentRoot, options.Paths.Tasks, request.Task);

        // Paths:Prices is read now, a dry run included (AJ-071): the calculator is built on first resolution, and resolving it here turns a missing or invalid price file into exit 2 before any check or request.
        _ = services.GetRequiredService<ICostCalculator>();

        // Paths:Rubric is read now too, a dry run included (AJ-076): the judge is built on first resolution, and resolving it here turns a missing or invalid rubric into exit 2 before any check or request.
        _ = services.GetRequiredService<IJudgeService>();

        return request.DryRun
            ? await DryRunAsync(services, options, output, request, tiers, cancellationToken)
            : await MeasureAsync(services, options, contentRoot, request, tiers, tasks, startUtc, cancellationToken);
    }

    // Paths:Tasks under the content root, then --task; a problem of the folder or of a file names the key (and the file name), never a path.
    private static IReadOnlyList<TaskDefinition> LoadTasks(string contentRoot, string configured, string? taskName)
    {
        string directory = ContentPaths.Resolve(contentRoot, configured, PathsOptions.TasksKey);
        IReadOnlyList<TaskDefinition> all;
        try
        {
            all = TaskSetLoader.Load(directory);
        }
        catch (ConfigurationException failure)
        {
            throw new ConfigurationException(PathsOptions.TasksKey + ": " + failure.Message);
        }

        return taskName is null ? all : [TaskSetLoader.Select(all, taskName)];
    }

    private static async Task<int> DryRunAsync(IServiceProvider services, AgentJoulesOptions options, TextWriter output, RunRequest request, IReadOnlyList<Tier> tiers, CancellationToken cancellationToken)
    {
        DryRunResult result = await services.GetRequiredService<DryRunService>().RunAsync(tiers, cancellationToken);

        ConsoleReporter reporter = services.GetRequiredService<ConsoleReporter>();
        ReportFallbacks(reporter, result.Meters);
        foreach (ModelCheckEntry entry in result.Tiers)
        {
            output.WriteLine(DryRunService.FormatLine(entry));
        }

        // With --dashboard the dry run proves that the server starts (P-6): start, print the line, dispose at once; no event, no browser, no grace.
        if (request.Dashboard)
        {
            DashboardSink sink = services.GetRequiredService<DashboardSink>();
            DashboardServer? server = await StartDashboardAsync(services, options, reporter, request.DashboardPort, cancellationToken);
            await CloseDashboardAsync(server, sink, grace: false, services.GetRequiredService<TimeProvider>(), cancellationToken);
        }

        return (int)ExitCode.Success;
    }

    private static async Task<int> MeasureAsync(
        IServiceProvider services,
        AgentJoulesOptions options,
        string contentRoot,
        RunRequest request,
        IReadOnlyList<Tier> tiers,
        IReadOnlyList<TaskDefinition> tasks,
        DateTimeOffset startUtc,
        CancellationToken cancellationToken)
    {
        PreflightService preflight = services.GetRequiredService<PreflightService>();
        ConsoleReporter reporter = services.GetRequiredService<ConsoleReporter>();

        KeyCheckResult keys = preflight.CheckKeys(tiers, dryRun: false);
        if (keys.MissingJudgeVariable is { } judgeVariable)
        {
            reporter.ReportMissingJudgeKey(judgeVariable);
        }

        // One writer per tier before any request: the constructor probes the directory, so an unwritable one ends the command with exit 1 here. --out resolves against the
        // content root: an absolute or UNC value is kept; a drive-relative one joins the content root only on the content root's own drive (on another drive, Z:results becomes
        // Z:\results). The failure line echoes --out as given, never the absolute path.
        string directory;
        try
        {
            directory = Path.GetFullPath(request.Out, contentRoot);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not attached: the message of the failure can quote the path.
            throw new AgentJoulesException(ExitCode.Unexpected, $"The output directory \"{TextEcho.ForMessage(request.Out)}\" cannot be written ({failure.GetType().Name}).");
        }

        SecretRedactor redactor = services.GetRequiredService<SecretRedactor>();
        var writers = new Dictionary<Tier, IRunCheckpointWriter>();
        foreach (Tier tier in tiers)
        {
            // A run of the same tier that started in the same second wrote this name already: refused now, before the model check and any completion call (exit 1).
            var writer = new ResultsFileWriter(directory, RunFileName.Create(startUtc, tier, ModelIdsOf(options, tier)), redactor, request.Out);
            writer.ThrowIfFileExists();
            writers[tier] = writer;
        }

        ModelCheckResult models = await preflight.CheckModelsAsync(tiers, keys, cancellationToken);
        ReportFallbacks(reporter, preflight.CheckMeasuredTiers(tiers, services.GetRequiredService<IGpuTelemetrySourceFactory>()));

        // The dashboard of --dashboard starts after the checks and before any warm-up or baseline (P-4, P-74), bounded by its own 5 s; one that did not start is a warning and the run goes on.
        DashboardSink? sink = request.Dashboard ? services.GetRequiredService<DashboardSink>() : null;
        DashboardServer? server = sink is null ? null : await StartDashboardAsync(services, options, reporter, request.DashboardPort, cancellationToken);

        // The try opens right after the start, so nothing that follows can leave the server bound.
        try
        {
            // The note of a short power interval concerns the sampling of a measured tier only; a hosted-only run samples nothing (P-74 as amended).
            if (sink is not null
                && tiers.Any(TierKinds.IsMeasured)
                && PowerForwarding.ShortIntervalNote(options.Dashboard.PowerIntervalMs, options.Meter.SampleRateHz) is { } note)
            {
                reporter.ReportNote(note);
            }

            // Prompt honesty rule 6: the scope of the joules, once per command (the orchestrator loops the tiers), before the first step line. A dry run prints no joules and never reaches this.
            reporter.ReportEnergyScope();

            double baselineSeconds = request.BaselineSeconds ?? options.Baseline.Seconds;
            var plan = new RunPlan(tiers, tasks, request.Mode, request.Repeat, baselineSeconds, writers, keys, models, startUtc, request.Out);
            return await services.GetRequiredService<IRunOrchestrator>().RunAsync(plan, cancellationToken);
        }
        finally
        {
            // One grace per command after the last tier, whether the orchestrator returned or failed (P-71), so the page renders the final state; a shutdown has cancelled the token,
            // which ends the grace at once.
            if (sink is not null)
            {
                await CloseDashboardAsync(server, sink, grace: true, services.GetRequiredService<TimeProvider>(), cancellationToken);
            }
        }
    }

    // Starts the server of --dashboard over the command's dashboard sink with the repository link of Report:RepositoryUrl (validated at host start, AJ-086), and tells the user where it
    // listens or why it did not start. Returns the started server, or null; a start that threw (a shutdown) disposes the server first.
    private static async Task<DashboardServer?> StartDashboardAsync(IServiceProvider services, AgentJoulesOptions options, ConsoleReporter reporter, int port, CancellationToken cancellationToken)
    {
        var server = new DashboardServer(services.GetRequiredService<DashboardSink>(), services.GetRequiredService<ILoggerFactory>());
        Uri? repository = options.Report.RepositoryUrl is { } text ? new Uri(text, UriKind.Absolute) : null;
        Uri? uri;
        try
        {
            uri = await server.TryStartAsync(port, repository, cancellationToken);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }

        if (uri is null)
        {
            reporter.ReportDashboardNotStarted(server.FailureReason ?? DashboardServer.NoFreePortMessage(port));
            await server.DisposeAsync();
            return null;
        }

        reporter.ReportDashboard(uri);
        return server;
    }

    // The end of the dashboard of a command: the grace on the host's clock when asked for and a server is up (a cancelled token ends it early and silently), then the server within its
    // stop timeout, then the sink within the same bound on the wall clock (its DisposeAsync waits for the pump, which a blocking logger can hold; the ingress is completed either way).
    private static async Task CloseDashboardAsync(DashboardServer? server, DashboardSink sink, bool grace, TimeProvider time, CancellationToken cancellationToken)
    {
        if (server is not null)
        {
            if (grace)
            {
                try
                {
                    await Task.Delay(DashboardServer.GracePeriod, time, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // A shutdown: the server goes at once.
                }
            }

            await server.DisposeAsync();
        }

        // After a shutdown the 15 s termination budget is mostly the incomplete write's: the sink is completed without waiting for its pump.
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

    // The model IDs of every agent of the tier, which decide the -baseline suffix of the file name.
    private static string[] ModelIdsOf(AgentJoulesOptions options, Tier tier)
    {
        string name = TierOptions.NameOf(tier);
        TierOptions configured = options.Tiers.TryGetValue(name, out TierOptions? found) && found is not null
            ? found
            : throw new ConfigurationException("Tiers:" + name + " is not configured");

        return [.. TierOptions.KnownAgentNames.Select(configured.ResolveModelId)];
    }

    // An automatic fallback to sampling is disclosed on stderr, as probe does, once per command: every measured tier reads the same GPU, so the first fallback stands for all of them.
    private static void ReportFallbacks(ConsoleReporter reporter, IEnumerable<KeyValuePair<Tier, MeterCapabilities>> meters)
    {
        if (meters.Select(static pair => pair.Value).FirstOrDefault(static capabilities => capabilities.IsFallback) is { } fallback)
        {
            reporter.ReportFallback(fallback);
        }
    }

    // The parsed command line; the values were validated by the parser.
    private sealed record RunRequest(string Tier, string? Task, RunMode Mode, string Out, int? BaselineSeconds, int Repeat, bool DryRun, bool Dashboard, int DashboardPort);
}