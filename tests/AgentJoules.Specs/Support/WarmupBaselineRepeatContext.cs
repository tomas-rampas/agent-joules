using System.Text.Json;

using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-106 scenario (order warm-up, idle baseline and repeats in local runs, REQ-021), handed to the step class by Reqnroll context injection. The Given steps only record how the loopback fakes must
/// behave (the power source the detector reports, a refused warm-up request); the When step builds a <see cref="LocalRunRig"/> on the Ollama fake, scripts the warm-up call to take <see cref="FakeWarmUp"/> on the fake
/// clock and runs the real <c>run</c> command through the harness; the Then steps read the operations the rig recorded and the run file the command wrote. Nothing leaves the process: the URLs are loopback
/// addresses, the keys fake ones, and the power source is a fake that never reads the host.
/// </summary>
public sealed class WarmupBaselineRepeatContext : IDisposable
{
    /// <summary>How long the fake warm-up call takes on the fake clock; the load time of the run file must equal it.</summary>
    public static readonly TimeSpan FakeWarmUp = TimeSpan.FromSeconds(7.5);

    private const long ModelSize = 1000;

    private LocalRunRig? _rig;

    /// <summary>The power source the fake detector reports, or null for the harness default (<c>unknown</c>).</summary>
    public string? PowerSource { get; set; }

    /// <summary>True when the fake Ollama endpoint refuses the warm-up request (and only that route).</summary>
    public bool RefuseWarmUp { get; set; }

    /// <summary>The rig of the last run.</summary>
    /// <exception cref="XunitException">The command has not run.</exception>
    public LocalRunRig Rig => _rig ?? throw new XunitException("The command has not run yet.");

    /// <summary>Builds the rig from the recorded Given steps and runs <paramref name="arguments"/> through the real command.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunAsync(string arguments)
    {
        string tag = OllamaFixtures.StoryTag;
        _rig = LocalRunRig.Ollama(OllamaFixtures.Ps(OllamaFixtures.PsEntry(tag, ModelSize, ModelSize)));
        _rig.Local.SetAnswers(FakeRoute.OllamaGenerate, FakeAnswer.Ok(FakeWarmUp));
        if (PowerSource is not null)
        {
            _rig.Setup.PowerSource.Value = PowerSource;
        }

        if (RefuseWarmUp)
        {
            _rig.Local.RefuseConnections(FakeRoute.OllamaGenerate);
        }

        _ = await _rig.RunAsync(arguments);
    }

    /// <summary>Reads the one run file the command wrote; the caller disposes the document.</summary>
    /// <returns>The document.</returns>
    public JsonDocument ReadRunFile() => Rig.ReadRunFile();

    /// <inheritdoc />
    public void Dispose() => _rig?.Dispose();
}