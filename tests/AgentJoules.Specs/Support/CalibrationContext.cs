using System.Text.Json;

using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-109 scenario (compute the counter versus sampling calibration, REQ-021), handed to the step class by Reqnroll context injection. The Given steps only record how the scripted GPU behind the real NVML
/// binding must behave; the When step builds a <see cref="LocalRunRig"/> (a NIM stand-in on a loopback address, the fake clock, the scripted GPU), applies them and runs the real <c>calibrate</c> command through the harness, so
/// the Then steps read the calibration file the command wrote. Nothing leaves the process: the GPU is the scripted one, the URLs are loopback addresses, the keys fake ones.
/// </summary>
public sealed class CalibrationContext : IDisposable
{
    /// <summary>The length of the window of the story's scenarios in seconds; the fake workflow takes 4 s per repeat on the fake clock, so a repeat ends exactly on the deadline.</summary>
    public const int StorySeconds = 60;

    private LocalRunRig? _rig;

    /// <summary>The factor on the energy the scripted counter counts; 1 (counter and samples agree) when null.</summary>
    public double? CounterScale { get; set; }

    /// <summary>The power the scripted GPU draws in watts as a function of seconds since the GPU was scripted; the rig's default when null.</summary>
    public Func<double, double>? WattsAt { get; set; }

    /// <summary>True when the scripted counter answers NOT_SUPPORTED.</summary>
    public bool CounterNotSupported { get; set; }

    /// <summary>True when the NVML loader is missing.</summary>
    public bool LibraryMissing { get; set; }

    /// <summary>The rig of the last run.</summary>
    /// <exception cref="XunitException">The command has not run.</exception>
    public LocalRunRig Rig => _rig ?? throw new XunitException("The command has not run yet.");

    /// <summary>The result of the last run.</summary>
    /// <exception cref="XunitException">The command has not run.</exception>
    public CliRunResult Result => Rig.Result ?? throw new XunitException("The command has not run yet.");

    /// <summary>Builds the rig from the recorded Given steps and runs <paramref name="arguments"/> through the real command.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunAsync(string arguments)
    {
        _rig = LocalRunRig.Nim();
        ScriptedNvmlApi gpu = _rig.Setup.Gpu ?? throw new XunitException("The rig has no scripted GPU.");
        if (WattsAt is { } wattsAt)
        {
            gpu.WattsAt = wattsAt;
        }

        if (CounterScale is { } scale)
        {
            gpu.CounterScale = scale;
        }

        gpu.CounterSupported = !CounterNotSupported;
        gpu.MissingLibrary = LibraryMissing;
        _ = await _rig.RunAsync(arguments);
    }

    /// <summary>The full paths of the calibration files in the results folder of the run, in ordinal order; empty when there are none.</summary>
    /// <returns>The paths.</returns>
    public string[] CalibrationFiles()
    {
        string folder = Path.Combine(Rig.Root, RunOrchestrationFixtures.ResultsFolder);
        return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "calibration-*").Order(StringComparer.Ordinal)] : [];
    }

    /// <summary>Reads the one calibration file the command wrote; the caller disposes the document.</summary>
    /// <returns>The document.</returns>
    public JsonDocument ReadFile()
    {
        string[] files = CalibrationFiles();
        return files.Length != 1
            ? throw new XunitException($"Expected one calibration file but found {files.Length}; exit code {Result.ExitCode}; stderr: {Result.Stderr}")
            : JsonDocument.Parse(File.ReadAllText(files[0]));
    }

    /// <inheritdoc />
    public void Dispose() => _rig?.Dispose();
}