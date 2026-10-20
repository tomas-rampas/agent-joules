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
/// a null method while <c>run</c> fails with exit 6 later. A factory exception outside the source contract still maps through <see cref="ExitCodeMapper"/>.
/// <c>--save</c> also writes the JSON form to <c>results/probe-&lt;yyyyMMddTHHmmssZ&gt;[-&lt;suffix&gt;].json</c> under the content root (see <see cref="ProbeFile"/>), before anything is
/// printed, so a refused save (exit 1, the file exists) leaves stdout empty; <c>--suffix</c> needs <c>--save</c> (exit 2). A no-GPU probe is saved too, as an honest record.
/// </summary>
internal static class ProbeCommand
{
    internal static Command Create(CommandHost host)
    {
        var json = new Option<bool>("--json") { Description = "Print the result as one JSON object on stdout." };
        var save = new Option<bool>("--save") { Description = "Write the JSON form to results/probe-<timestamp>[-<suffix>].json; an existing file is never overwritten." };
        var suffix = new Option<string>("--suffix") { Description = "With --save: 1 to 20 ASCII letters or digits appended to the file name, for example cloudgpu." };
        var config = new Option<string>("--config") { Description = "Path of the configuration file (default config/appsettings.json)." };

        var command = new Command("probe", "Report the GPU, its driver and its energy-counter support.") { json, save, suffix, config };
        command.SetAction((parseResult, cancellationToken) =>
        {
            bool asJson = parseResult.GetValue(json);
            bool saving = parseResult.GetValue(save);

            // Null when --suffix was not given; an empty value is given, and fails the rule.
            string? fileSuffix = parseResult.GetResult(suffix) is null ? null : parseResult.GetValue(suffix) ?? string.Empty;
            return host.RunAsync(
                parseResult.GetValue(config),
                (running, _) => Task.FromResult(Probe(running, host.Output, asJson, saving, fileSuffix)),
                cancellationToken);
        });
        return command;
    }

    private static int Probe(IHost running, TextWriter output, bool asJson, bool save, string? fileSuffix)
    {
        // A usage error comes before the GPU is opened and before anything is written.
        ProbeFile.ValidateOptions(save, fileSuffix);

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

        // The JSON is formatted once: the saved file and --json stdout are the same string, so they are equal by construction.
        string? jsonText = asJson || save ? ProbeFormatter.Json(result) + "\n" : null;
        if (save)
        {
            Save(running, jsonText!, fileSuffix);
        }

        Print(result, output, asJson, jsonText);
        return 0;
    }

    // The file holds the bytes of `probe --json` whichever form stdout takes. It is written before stdout, so a refused save leaves stdout empty.
    private static void Save(IHost running, string jsonText, string? fileSuffix)
    {
        string contentRoot = running.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;
        DateTimeOffset now = running.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        string saved = ProbeFile.Save(contentRoot, ProbeFile.Name(now, fileSuffix), jsonText);
        running.Services.GetRequiredService<ConsoleReporter>().ReportSaved(saved);
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

    // jsonText is the formatted JSON with its trailing LF; it is set whenever the JSON form is needed (--json or --save).
    private static void Print(ProbeResult result, TextWriter output, bool asJson, string? jsonText)
    {
        if (asJson)
        {
            // One Write with an explicit LF: the JSON uses LF on every platform, so WriteLine (the platform terminator) is not used.
            output.Write(jsonText);
            return;
        }

        foreach (string line in ProbeFormatter.Lines(result))
        {
            output.WriteLine(line);
        }
    }
}