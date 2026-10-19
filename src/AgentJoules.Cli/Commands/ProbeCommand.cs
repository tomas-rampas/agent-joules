using System.CommandLine;

using AgentJoules.Cli.Hosting;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Gpu;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentJoules.Cli.Commands;

/// <summary>
/// <c>probe</c>: opens the configured GPU and reports whether one is present. AJ-034 delivers the command, its options and the exit-code
/// paths; AJ-035 replaces the placeholder output with the full text and JSON result and a later story writes <c>--save</c> and <c>--suffix</c>.
/// </summary>
internal static class ProbeCommand
{
    internal static Command Create(CommandHost host)
    {
        var json = new Option<bool>("--json") { Description = "Print the result as one JSON object on stdout." };
        var save = new Option<bool>("--save") { Description = "Write results/probe-<timestamp>[-<suffix>].json (not implemented yet)." };
        var suffix = new Option<string>("--suffix") { Description = "Suffix for the saved file name, for example cloudgpu (not implemented yet)." };
        var config = new Option<string>("--config") { Description = "Path of the configuration file (default config/appsettings.json)." };

        var command = new Command("probe", "Report the GPU, its driver and its energy-counter support.") { json, save, suffix, config };
        command.SetAction((parseResult, cancellationToken) =>
        {
            bool asJson = parseResult.GetValue(json);
            return host.RunAsync(
                parseResult.GetValue(config),
                (running, _) => Task.FromResult(Probe(running, host.Output, asJson)),
                cancellationToken);
        });
        return command;
    }

    private static int Probe(IHost running, TextWriter output, bool asJson)
    {
        ILogger logger = running.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Log.Category);
        int index = running.Services.GetRequiredService<IOptions<AgentJoulesOptions>>().Value.Gpu.Index;

        Log.OpeningGpu(logger, index);
        GpuOpenResult opened = running.Services.GetRequiredService<IGpuTelemetrySourceFactory>().Open(index);
        if (!opened.IsAvailable)
        {
            Log.GpuUnavailable(logger, opened.Reason);
            output.WriteLine(asJson ? "{\"gpuPresent\": false}" : "no GPU");
            return 0;
        }

        using IGpuTelemetrySource source = opened.Source;
        output.WriteLine(asJson ? "{\"gpuPresent\": true}" : "name: " + source.Identity.Name);
        return 0;
    }
}