using System.CommandLine;

namespace AgentJoules.Cli.Hosting;

/// <summary>
/// The command-line option of the telemetry setup, defined once so that <c>run</c> and <c>calibrate</c> offer the same flag with the same name and description (design 3.7). The console
/// metrics exporter is on for those two commands unless the flag is given, and off for every other command, which does not offer the flag.
/// </summary>
public static class TelemetryOptions
{
    /// <summary>The name of the option on the command line.</summary>
    public const string NoConsoleTelemetryName = "--no-console-telemetry";

    /// <summary>Creates the <c>--no-console-telemetry</c> option: a flag that is false unless given. Call it once per command, because an option instance belongs to one parent.</summary>
    /// <returns>A new option; the command passes <c>!parseResult.GetValue(option)</c> as <c>consoleEnabled</c> to <see cref="TelemetrySetup.Add"/>.</returns>
    public static Option<bool> CreateNoConsoleTelemetry() => new(NoConsoleTelemetryName)
    {
        Description = "Do not print the metrics to the console (OTLP export is unaffected).",
    };
}