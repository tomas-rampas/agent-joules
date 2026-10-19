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