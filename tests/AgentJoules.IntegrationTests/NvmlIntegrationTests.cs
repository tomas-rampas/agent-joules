using System.Globalization;

using AgentJoules.Nvml;
using AgentJoules.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace AgentJoules.IntegrationTests;

[Trait("Spec", "REQ-003")]
public sealed class NvmlIntegrationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [IntegrationFact]
    public void Laptop_gpu_is_identified_by_the_real_binding()
    {
        LaptopGpuCheck check = NvmlIntegrationChecks.AssertLaptopGpu(new NvmlGpuTelemetrySourceFactory(new NvmlApi()));

        _output.WriteLine($"name: {check.Name}");
        _output.WriteLine($"driver version: {check.DriverVersion}");
        _output.WriteLine($"power limit W: {check.PowerLimitWatts.ToString("F3", CultureInfo.InvariantCulture)}");
        _output.WriteLine($"energy counter supported: {check.EnergyCounterSupported}");
        _output.WriteLine($"throttle reasons supported: {check.ThrottleReasonsSupported}");
        _output.WriteLine($"MIG mode: {check.MigMode}");
    }
}