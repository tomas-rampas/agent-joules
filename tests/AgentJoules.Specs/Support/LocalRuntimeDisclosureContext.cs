using System.Text.Json;

using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-105 scenario (disclose the local runtime, tag, offload and precision, REQ-020), handed to the step class by Reqnroll context injection. The Given steps only record how the loopback fake
/// that stands where the local runtime stands must answer (the process list, the quantization level, the runtime); the When step builds a <see cref="LocalRunRig"/> from them, runs the real
/// <c>run --tier local</c> command through the harness on a fake clock and keeps the rig, so the Then steps read the run file the command wrote and the header the report command renders from it. Nothing
/// leaves the process: the URLs are loopback addresses, the keys fake ones.
/// </summary>
public sealed class LocalRuntimeDisclosureContext : IDisposable
{
    /// <summary>The <c>size</c> the fake process list reports for the configured tag, in bytes.</summary>
    public const long ModelSize = 1000;

    private LocalRunRig? _rig;

    /// <summary>True for the NIM runtime; false for Ollama.</summary>
    public bool Nim { get; set; }

    /// <summary>The <c>Tiers:local:Precision</c> of a NIM scenario; the shipped value when null.</summary>
    public string? NimPrecision { get; set; }

    /// <summary>The <c>size_vram</c> of the configured tag as a share of <see cref="ModelSize"/> in percent; 100 by default.</summary>
    public int VramPercent { get; set; } = 100;

    /// <summary>True when the process list holds no model at all.</summary>
    public bool NoModelListed { get; set; }

    /// <summary>True when the process list reports a <c>size</c> of 0 for the configured tag.</summary>
    public bool ZeroSize { get; set; }

    /// <summary>The <c>quantization_level</c> the fake <c>/api/show</c> reports.</summary>
    public string Quantization { get; set; } = "Q4_K_M";

    /// <summary>The rig of the last run.</summary>
    /// <exception cref="XunitException">The command has not run.</exception>
    public LocalRunRig Rig => _rig ?? throw new XunitException("The command has not run yet.");

    /// <summary>Builds the rig from the recorded Given steps and runs <paramref name="arguments"/> through the real command.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunAsync(string arguments)
    {
        string tag = OllamaFixtures.StoryTag;
        string ps = NoModelListed
            ? OllamaFixtures.Ps()
            : OllamaFixtures.Ps(OllamaFixtures.PsEntry(tag, ZeroSize ? 0 : ModelSize, ModelSize * VramPercent / 100));
        _rig = Nim ? LocalRunRig.Nim(NimPrecision) : LocalRunRig.Ollama(ps, OllamaFixtures.Show(Quantization));
        _ = await _rig.RunAsync(arguments);
    }

    /// <summary>Reads the one run file the command wrote; the caller disposes the document.</summary>
    /// <returns>The document.</returns>
    public JsonDocument ReadRunFile() => Rig.ReadRunFile();

    /// <inheritdoc />
    public void Dispose() => _rig?.Dispose();
}