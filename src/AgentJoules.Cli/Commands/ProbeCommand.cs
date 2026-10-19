using System.CommandLine;

using AgentJoules.Cli.Hosting;
using AgentJoules.Cli.Output;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentJoules.Cli.Commands;

/// <summary>
/// <c>probe</c>: opens the configured GPU and prints what will be measured, as text or, with <c>--json</c>, as one JSON object. Without NVML or a
/// device it prints <c>no GPU</c> (EDGE-001). Every GPU outcome exits 0: probe is diagnostic, and a forced counter on a GPU without one is reported as
/// a null method while <c>run</c> fails with exit 6 later. A factory exception outside the source contract still maps through <see cref="ExitCodeMapper"/>. <c>--save</c> and <c>--suffix</c> are accepted and inert until AJ-037.
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
        AgentJoulesOptions options = running.Services.GetRequiredService<IOptions<AgentJoulesOptions>>().Value;
        int index = options.Gpu.Index;

        Log.OpeningGpu(logger, index);
        GpuOpenResult opened = running.Services.GetRequiredService<IGpuTelemetrySourceFactory>().Open(index);
        ProbeResult result;
        if (opened.IsAvailable)
        {
            using IGpuTelemetrySource source = opened.Source;
            result = Describe(running, source, options.Meter);
        }
        else
        {
            Log.GpuUnavailable(logger, opened.Reason);
            result = ProbeResult.NoGpu;
        }

        Print(result, output, asJson);
        return 0;
    }

    // The selector decides the method; a forced counter on a GPU without one is not an error here, the method is simply null.
    private static ProbeResult Describe(IHost running, IGpuTelemetrySource source, MeterOptions meter)
    {
        MeterMethodSetting setting = MeterMethodSelector.Parse(meter.Method);
        if (!MeterMethodSelector.TrySelect(setting, source, meter.SampleRateHz, out MeterCapabilities? capabilities))
        {
            return ProbeResult.Create(source.Identity, source.EnergyCounterSupported, capabilities: null, meter.SampleRateHz);
        }

        // Stderr only: stdout carries nothing but the probe output.
        running.Services.GetRequiredService<ConsoleReporter>().ReportFallback(capabilities);
        return ProbeResult.Create(source.Identity, source.EnergyCounterSupported, capabilities, meter.SampleRateHz);
    }

    private static void Print(ProbeResult result, TextWriter output, bool asJson)
    {
        if (asJson)
        {
            // One Write with an explicit LF: the JSON uses LF on every platform, so WriteLine (the platform terminator) is not used.
            output.Write(ProbeFormatter.Json(result) + "\n");
            return;
        }

        foreach (string line in ProbeFormatter.Lines(result))
        {
            output.WriteLine(line);
        }
    }
}