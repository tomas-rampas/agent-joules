using AgentJoules.Core.Cli;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Nvml;
using AgentJoules.Nvml.Interop;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>The scripted GPU and the failing-selection helper shared by the method-selection tests (PowerIntegrationTests) and the exit-code tests (ExitCodeTests).</summary>
public static class MeterSelectionFixtures
{
    /// <summary>The sample rate in hertz the selection tests pass.</summary>
    public const double SelectedRateHz = 10.0;

    /// <summary>The reason a counter that answers NOT_SUPPORTED carries.</summary>
    public const string NotSupportedReason = "counter-not-supported: NVML_ERROR_NOT_SUPPORTED";

    /// <summary>A scripted NVML session at a constant 50 W whose counter read returns <paramref name="counter"/>.</summary>
    public static ScriptedNvmlApi NewApi(NvmlReturn counter = NvmlReturn.Success) =>
        new(new FakeTimeProvider()) { ConstantPowerWatts = 50, CounterReturn = counter };

    /// <summary>Opens the scripted GPU; the caller disposes the source.</summary>
    public static IGpuTelemetrySource OpenSource(ScriptedNvmlApi api)
    {
        ArgumentNullException.ThrowIfNull(api);

        GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api).Open(0);
        Assert.True(result.IsAvailable, result.Reason);
        Assert.NotNull(result.Source);
        return result.Source;
    }

    /// <summary>Runs <see cref="MeterMethodSelector.Select(string?, IGpuTelemetrySource, double)"/> against the scripted GPU and returns the exception it throws.</summary>
    public static T SelectionFails<T>(string? configured, NvmlReturn counter = NvmlReturn.Success, double rateHz = SelectedRateHz)
        where T : AgentJoulesException
    {
        using IGpuTelemetrySource source = OpenSource(NewApi(counter));
        return Assert.Throws<T>(() => { _ = MeterMethodSelector.Select(configured, source, rateHz); });
    }
}