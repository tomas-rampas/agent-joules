using System.Runtime.InteropServices;

using AgentJoules.Nvml;
using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-003")]
public sealed class IntegrationGateTests
{
    private const string ClosedReason = "Set AGENTJOULES_INTEGRATION=1";

    private static NvmlGpuTelemetrySourceFactory FactoryFor(ScriptedNvmlApi api) =>
        new(api, OSPlatform.Windows);

    [Fact]
    public void VariableName_IsAgentJoulesIntegration()
    {
        Assert.Equal("AGENTJOULES_INTEGRATION", IntegrationGate.VariableName);
    }

    // Trim removes every whitespace character, so " 1 " (a trailing space from cmd.exe "set X=1 &&") and "1\n" count as set.
    [Theory]
    [InlineData("1", null)]
    [InlineData(" 1 ", null)]
    [InlineData("1\n", null)]
    [InlineData("0", ClosedReason)]
    [InlineData("true", ClosedReason)]
    [InlineData("", ClosedReason)]
    [InlineData("  ", ClosedReason)]
    [InlineData(null, ClosedReason)]
    [InlineData("01", ClosedReason)]
    public void SkipReason_IsNullOnlyForTheTrimmedValueOne(string? value, string? expected)
    {
        Assert.Equal(expected, IntegrationGate.SkipReason(value));
    }

    [Fact]
    public void IntegrationFact_WithValueOne_IsNotSkipped()
    {
        var attribute = IntegrationFactAttribute.ForValue("1");

        Assert.Null(attribute.Skip);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public void IntegrationFact_WithAnyOtherValue_IsSkippedWithTheReason(string? value)
    {
        var attribute = IntegrationFactAttribute.ForValue(value);

        Assert.Equal(ClosedReason, attribute.Skip);
    }

    [Theory]
    [InlineData(50.0)]
    [InlineData(85.059)]
    [InlineData(200.0)]
    public void PowerLimitInRange_PassesInsideFiftyToTwoHundredWatts(double watts)
    {
        GpuAssertions.PowerLimitInRange(watts);
    }

    [Theory]
    [InlineData(49.9)]
    [InlineData(200.1)]
    [InlineData(300.0)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void PowerLimitInRange_FailsOutsideTheRange(double watts)
    {
        Assert.ThrowsAny<XunitException>(() => GpuAssertions.PowerLimitInRange(watts));
    }

    [Fact]
    public void PowerLimitInRange_FailureMessageNamesTheValueAndTheRange()
    {
        XunitException failure = Assert.ThrowsAny<XunitException>(() => GpuAssertions.PowerLimitInRange(300.0));

        Assert.Contains("300", failure.Message, StringComparison.Ordinal);
        Assert.Contains("50", failure.Message, StringComparison.Ordinal);
        Assert.Contains("200", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NameContains3080_PassesForTheScriptedName()
    {
        GpuAssertions.NameContains3080(new ScriptedNvmlApi(new FakeTimeProvider()).Name);
    }

    [Fact]
    public void NameContains3080_FailsForAnotherModel()
    {
        Assert.ThrowsAny<XunitException>(() => GpuAssertions.NameContains3080("Scripted GPU 4090"));
    }

    [Fact]
    public void AssertLaptopGpu_WithAScriptedGpu_ReturnsTheRecord()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider()) { MigModeReturn = NvmlReturn.NotSupported };

        LaptopGpuCheck check = NvmlIntegrationChecks.AssertLaptopGpu(FactoryFor(api));

        Assert.Equal("Scripted GPU 3080", check.Name);
        Assert.Equal("616.92", check.DriverVersion);
        Assert.Equal(85.059, check.PowerLimitWatts, 6);
        Assert.True(check.EnergyCounterSupported);
        Assert.True(check.ThrottleReasonsSupported);
        Assert.Equal("not-supported", check.MigMode);
    }

    [Fact]
    public void AssertLaptopGpu_ReportsAnUnsupportedCounterWithoutFailing()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider()) { CounterSupported = false };

        LaptopGpuCheck check = NvmlIntegrationChecks.AssertLaptopGpu(FactoryFor(api));

        Assert.False(check.EnergyCounterSupported);
    }

    [Fact]
    public void AssertLaptopGpu_DisposesTheSourceSoNvmlIsShutDown()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider());

        _ = NvmlIntegrationChecks.AssertLaptopGpu(FactoryFor(api));

        Assert.Equal(1, api.ShutdownCallCount);
    }

    [Fact]
    public void AssertLaptopGpu_WithAMissingLibrary_FailsWithTheUnavailableReason()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider()) { MissingLibrary = true };

        XunitException failure = Assert.ThrowsAny<XunitException>(() => NvmlIntegrationChecks.AssertLaptopGpu(FactoryFor(api)));

        Assert.StartsWith("NVML unavailable: ", failure.Message, StringComparison.Ordinal);
        Assert.Contains("nvml.dll", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertLaptopGpu_WithAnotherModel_FailsTheNameAssertion()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider()) { Name = "Scripted GPU 4090" };

        XunitException failure = Assert.ThrowsAny<XunitException>(() => NvmlIntegrationChecks.AssertLaptopGpu(FactoryFor(api)));

        Assert.DoesNotContain("NVML unavailable", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, api.ShutdownCallCount);
    }

    [Fact]
    public void AssertLaptopGpu_WithAPowerLimitOutsideTheRange_Fails()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider()) { PowerLimitMilliwatts = 300_000 };

        XunitException failure = Assert.ThrowsAny<XunitException>(() => NvmlIntegrationChecks.AssertLaptopGpu(FactoryFor(api)));

        Assert.DoesNotContain("NVML unavailable", failure.Message, StringComparison.Ordinal);
        Assert.Contains("300 W is outside", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, api.ShutdownCallCount);
    }
}