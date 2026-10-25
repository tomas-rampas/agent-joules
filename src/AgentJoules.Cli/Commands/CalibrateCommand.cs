using System.CommandLine;

using AgentJoules.Cli.Hosting;
using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.Reporting.Results;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentJoules.Cli.Commands;

/// <summary>
/// <c>calibrate</c> (REQ-021, design 3.6, AJ-109): measures one window of the task workflow with the energy counter and with power sampling at once and writes
/// <c>results/calibration-&lt;yyyyMMddTHHmmssZ&gt;.json</c> with both totals and their relative difference. Only the local tier is calibrated: the hosted tiers have no GPU energy, so the command has no <c>--tier</c>; it
/// reads <c>Tiers:local</c> and <c>Local:Runtime</c> as <c>run --tier local</c> does and makes the same checks in the same order (the task, the output folder, the keys, the runtime and its model, the GPU), with the same exit
/// codes: 2 for a bad option or an unknown task, 1 for an output folder that cannot be written or a file that exists, 4 for a model the runtime does not list, 5 for a runtime that does not answer or a workflow step that fails, 6 for no GPU.
/// Invalid option values (<c>--seconds 0</c>, a blank <c>--out</c>) are parse errors, so the command line exits 2 before any host is built.
/// </summary>
/// <remarks>
/// <para>
/// The host of <c>calibrate</c> registers the telemetry of REQ-014 (the console metrics exporter unless <c>--no-console-telemetry</c> is given), the <see cref="CalibrationService"/> and nothing of a measured run: no meter
/// gate, no orchestrator, no judge call and no run file. The judge is not checked either: the key or the model list of <c>Judge:Tier</c> is never asked for. The command prints the energy-scope sentence once, before the
/// window, and after the file was written <c>calibration: &lt;path&gt;</c> and one line with both totals, the difference, the window and the sample count (<see cref="ConsoleReporter.ReportCalibration"/>); no line is printed per repeat,
/// because the window is one measurement.
/// </para>
/// <para>
/// The start of the command is taken once from the registered clock, before any check; it names the file. The name is claimed (<see cref="CalibrationFile.Prepare"/>) before the runtime is asked anything, so an existing file or an
/// unwritable folder ends the command before the window, and the file is written once, after it. A command that ends in any failure writes no file.
/// </para>
/// </remarks>
internal static class CalibrateCommand
{
    /// <summary>The default of <c>--seconds</c>: the length of the window.</summary>
    internal const int DefaultSeconds = 60;

    internal static Command Create(CommandHost host)
    {
        var task = new Option<string>("--task") { Description = "The task whose workflow repeats in the window (default: the first task of Paths:Tasks in file order)." };
        var seconds = new Option<int>("--seconds")
        {
            Description = "The length of the window in seconds (1 or more); the repeat in flight at the deadline completes, so the window can be longer.",
            DefaultValueFactory = static _ => DefaultSeconds,
            CustomParser = static result => RunCommand.WholeNumber(result, "--seconds must be a whole number of seconds") ?? 0,
        };
        seconds.Validators.Add(static result => RunCommand.RequireAtLeast(result, 1, "--seconds must be 1 or more"));
        var output = new Option<string>("--out") { Description = "The folder of the calibration file, relative to the working directory unless absolute.", DefaultValueFactory = static _ => RunCommand.DefaultOut };
        output.Validators.Add(static result => RunCommand.RequireNotBlank(result, "--out must not be blank"));
        Option<bool> noConsoleTelemetry = TelemetryOptions.CreateNoConsoleTelemetry();
        var config = new Option<string>("--config") { Description = "Path of the configuration file (default config/appsettings.json)." };

        var command = new Command("calibrate", "Measure one window of the task workflow with the energy counter and with power sampling at once and write both totals and their relative difference.")
        {
            task, seconds, output, noConsoleTelemetry, config,
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            var request = new CalibrateRequest(parseResult.GetValue(task), parseResult.GetRequiredValue(seconds), parseResult.GetRequiredValue(output));
            bool consoleEnabled = !parseResult.GetValue(noConsoleTelemetry);

            return host.RunAsync(
                parseResult.GetValue(config),
                (services, configuration) => AddCalibrateServices(services, configuration, consoleEnabled),
                (running, token) => ExecuteAsync(running, request, token),
                cancellationToken);
        });
        return command;
    }

    // The services of calibrate only: the telemetry of REQ-014, the instruments the inert metering services of the unmetered agent factory carry, and the calibration service. Applied after the composition root and before the
    // test overrides.
    private static void AddCalibrateServices(IServiceCollection services, IConfiguration configuration, bool consoleEnabled)
    {
        _ = TelemetrySetup.Add(services, configuration, consoleEnabled);
        services.TryAddSingleton<AgentJoulesInstruments>();
        _ = services.AddAgentJoulesCalibration();
    }

    private static async Task<int> ExecuteAsync(IHost running, CalibrateRequest request, CancellationToken cancellationToken)
    {
        IServiceProvider services = running.Services;

        // The start of the command, taken once, before any check: it names the file.
        DateTimeOffset startUtc = services.GetRequiredService<TimeProvider>().GetUtcNow();
        PreflightService preflight = services.GetRequiredService<PreflightService>();
        AgentJoulesOptions options = services.GetRequiredService<IOptions<AgentJoulesOptions>>().Value;
        string contentRoot = services.GetRequiredService<IHostEnvironment>().ContentRootPath;
        SecretRedactor redactor = services.GetRequiredService<SecretRedactor>();
        ConsoleReporter reporter = services.GetRequiredService<ConsoleReporter>();

        // The local tier only, and the first task in file order unless --task names one (an unknown name lists the known ones).
        IReadOnlyList<Tier> tiers = preflight.SelectTiers(TierOptions.NameOf(Tier.Local));
        TaskDefinition task = RunCommand.LoadTasks(contentRoot, options.Paths.Tasks, request.Task)[0];

        // The file is claimed before anything is asked of the runtime: --out resolves against the content root as it does for run, and the failure line echoes --out as given, never the absolute path.
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

        string path = CalibrationFile.Prepare(directory, CalibrationFile.Name(startUtc), redactor, request.Out);

        // The checks of run --tier local, minus the judge: the key of the tier, the runtime and its model, the GPU. Nothing is scored, so the judge key and the judge model are not asked for.
        KeyCheckResult keys = preflight.CheckKeys(tiers, dryRun: false);
        _ = await preflight.CheckModelsAsync(tiers, keys, checkJudge: false, cancellationToken);

        // Meter:Method is ignored: the counter when the GPU has one, sampling otherwise, so a forced counter on a counterless GPU is a sampling calibration and not exit 6.
        _ = preflight.CheckMeasuredTiers(tiers, services.GetRequiredService<IGpuTelemetrySourceFactory>(), ignoreMeterMethod: true);

        // Prompt honesty rule 6: the scope of the joules, once, before the window.
        reporter.ReportEnergyScope();

        CalibrationDocument document = await services.GetRequiredService<CalibrationService>().RunAsync(new CalibrationRequest(task, request.Seconds), cancellationToken);
        CalibrationFile.Write(path, document, redactor);
        reporter.ReportCalibration(RunAssembler.DisplayPath(request.Out, path), document);
        return (int)ExitCode.Success;
    }

    // The parsed command line; the values were validated by the parser.
    private sealed record CalibrateRequest(string? Task, int Seconds, string Out);
}