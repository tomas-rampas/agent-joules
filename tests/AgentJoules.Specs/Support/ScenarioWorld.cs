using System.Runtime.InteropServices;

using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.Specs.Support;

/// <summary>
/// Per-scenario state shared between step classes through Reqnroll context injection.
/// <see cref="Hooks"/> creates the fake clock before each scenario and disposes the state after it.
/// </summary>
public sealed class ScenarioWorld : IDisposable
{
    private ScriptedNvmlApi? _api;

    /// <summary>The scenario clock; <see cref="Hooks"/> replaces it with a fresh one before the first step runs.</summary>
    public FakeTimeProvider Time { get; set; } = new();

    /// <summary>The scripted NVML session under test; set by the Given step of a metering scenario.</summary>
    public ScriptedNvmlApi Api
    {
        get => _api ?? throw new InvalidOperationException("No scripted NVML session was created.");
        set => _api = value;
    }

    /// <summary>The device handle the read steps pass to the API.</summary>
    public nint Device { get; set; }

    /// <summary>The return code of the last API call a step made.</summary>
    public NvmlReturn LastReturn { get; set; }

    /// <summary>The power in milliwatts the last successful power read produced, or null.</summary>
    public uint? LastPowerMilliwatts { get; set; }

    /// <summary>The counter in millijoules the last successful counter read produced, or null.</summary>
    public ulong? LastEnergyMillijoules { get; set; }

    /// <summary>The counter value of the read before the last one in a two-read scenario, or null.</summary>
    public ulong? FirstEnergyMillijoules { get; set; }

    /// <summary>The exception the last API call threw, or null.</summary>
    public Exception? LastException { get; set; }

    /// <summary>The platform a candidate-list scenario builds the NVML library candidates for.</summary>
    public OSPlatform Platform { get; set; }

    /// <summary>The ProgramW6432 value a candidate-list scenario passes; null models an unset variable.</summary>
    public string? ProgramW6432 { get; set; }

    /// <summary>The candidate list the last build step produced, or null.</summary>
    public IReadOnlyList<string>? Candidates { get; set; }

    /// <summary>The candidate names a loader scenario offers to TryLoadFirst, in order.</summary>
    public IReadOnlyList<string> LoaderCandidates { get; set; } = [];

    /// <summary>The injected loader of a loader scenario; it returns 0 for a candidate that fails to load.</summary>
    public Func<string, nint>? Loader { get; set; }

    /// <summary>The result TryLoadFirst returned, or null when it has not run.</summary>
    public bool? LoadSucceeded { get; set; }

    /// <summary>The handle TryLoadFirst produced.</summary>
    public nint LoadedHandle { get; set; }

    public void Dispose()
    {
        // A scenario may script a blanket fault; clear it so releasing the session cannot fail the hook.
        if (_api is not null)
        {
            _api.FaultFor = null;
            _api.MissingLibrary = false;
        }

        // Init is reference counted, so release every outstanding session.
        while (_api is { IsInitialised: true })
        {
            _api.Shutdown();
        }

        // FakeTimeProvider 10.11.0 is not IDisposable (measured by reflection); dropping the reference at the end of the scenario releases it.
        _api = null;
    }
}