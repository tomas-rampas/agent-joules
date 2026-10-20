using AgentJoules.Core.Gpu;

using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>What <see cref="NvmlIntegrationChecks.AssertLaptopGpu"/> observed on device 0.</summary>
/// <param name="Name">The product name.</param>
/// <param name="DriverVersion">The driver version string.</param>
/// <param name="PowerLimitWatts">The enforced power limit in watts.</param>
/// <param name="EnergyCounterSupported">True when the cumulative energy counter answered the probe read.</param>
/// <param name="ThrottleReasonsSupported">True when the driver reports clock events.</param>
/// <param name="MigMode">One of <c>disabled</c>, <c>enabled</c> or <c>not-supported</c>.</param>
public sealed record LaptopGpuCheck(
    string Name,
    string DriverVersion,
    double PowerLimitWatts,
    bool EnergyCounterSupported,
    bool ThrottleReasonsSupported,
    string MigMode);

/// <summary>The check behind the gated NVML integration test, shared with the scenarios that run it against the scripted fake.</summary>
public static class NvmlIntegrationChecks
{
    /// <summary>
    /// Opens device 0 and asserts it is an RTX 3080 with a power limit between 50 and 200 W. An unavailable GPU fails
    /// the assertion with the reason; it never skips. The source is disposed before the method returns.
    /// </summary>
    public static LaptopGpuCheck AssertLaptopGpu(IGpuTelemetrySourceFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        GpuOpenResult result = factory.Open(0);
        if (!result.IsAvailable)
        {
            Assert.Fail($"NVML unavailable: {result.Reason}");
        }

        IGpuTelemetrySource source = result.Source;
        try
        {
            GpuIdentity identity = source.Identity;
            GpuAssertions.NameContains3080(identity.Name);
            GpuAssertions.PowerLimitInRange(identity.PowerLimitWatts);
            return new LaptopGpuCheck(
                identity.Name,
                identity.DriverVersion,
                identity.PowerLimitWatts,
                source.EnergyCounterSupported,
                source.ReadThrottleReasons().Supported,
                identity.MigMode);
        }
        finally
        {
            source.Dispose();
        }
    }
}