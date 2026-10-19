using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Nvml;
using AgentJoules.Nvml.Interop;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

[Binding]
public sealed class MeteringSteps(ScenarioWorld world)
{
    private const nint SecondCandidateHandle = 0x2000;
    private const string ClosedGateReason = "Set AGENTJOULES_INTEGRATION=1";

    private readonly ScenarioWorld _world = world;

    private static TimeSpan ParseSeconds(string seconds) =>
        TimeSpan.FromSeconds(double.Parse(seconds, CultureInfo.InvariantCulture));

    private void StartSession(ScriptedNvmlApi api)
    {
        _world.Api = api;
        Assert.Equal(NvmlReturn.Success, api.Init());
        Assert.Equal(NvmlReturn.Success, api.GetHandleByIndex(0, out nint device));
        _world.Device = device;
    }

    private void ReadPower()
    {
        _world.LastReturn = _world.Api.GetPowerUsage(_world.Device, out uint milliwatts);
        _world.LastPowerMilliwatts = _world.LastReturn == NvmlReturn.Success ? milliwatts : null;
    }

    private void ReadCounter()
    {
        _world.LastReturn = _world.Api.GetTotalEnergyConsumption(_world.Device, out ulong millijoules);
        _world.LastEnergyMillijoules = _world.LastReturn == NvmlReturn.Success ? millijoules : null;
    }

    [Given(@"a ScriptedNvmlApi that draws a constant (\d+) W and was initialised")]
    public void GivenAScriptedNvmlApiThatDrawsAConstantWAndWasInitialised(uint watts)
    {
        StartSession(new ScriptedNvmlApi(_world.Time) { ConstantPowerWatts = watts });
    }

    [Given(@"a ScriptedNvmlApi at a constant (\d+) W with the counter supported")]
    public void GivenAScriptedNvmlApiAtAConstantWWithTheCounterSupported(uint watts)
    {
        StartSession(new ScriptedNvmlApi(_world.Time) { ConstantPowerWatts = watts, CounterSupported = true });
    }

    [Given(@"a ScriptedNvmlApi with the counter switch off")]
    public void GivenAScriptedNvmlApiWithTheCounterSwitchOff()
    {
        StartSession(new ScriptedNvmlApi(_world.Time) { ConstantPowerWatts = 50, CounterSupported = false });
    }

    [Given(@"a ScriptedNvmlApi in missing-library mode")]
    public void GivenAScriptedNvmlApiInMissingLibraryMode()
    {
        _world.Api = new ScriptedNvmlApi(_world.Time) { MissingLibrary = true };
    }

    [Given(@"a ScriptedNvmlApi that was never initialised")]
    public void GivenAScriptedNvmlApiThatWasNeverInitialised()
    {
        _world.Api = new ScriptedNvmlApi(_world.Time) { ConstantPowerWatts = 50 };
        _world.Device = ScriptedNvmlApi.FirstDeviceHandle;
    }

    [When(@"the fake time advances to (\d+(?:\.\d+)?) s and power is read")]
    public void WhenTheFakeTimeAdvancesToSAndPowerIsRead(string seconds)
    {
        _world.Time.Advance(ParseSeconds(seconds) - _world.Api.Elapsed);
        ReadPower();
    }

    [When(@"the fake time advances (\d+(?:\.\d+)?) s between two counter reads")]
    public void WhenTheFakeTimeAdvancesSBetweenTwoCounterReads(string seconds)
    {
        ReadCounter();
        Assert.Equal(NvmlReturn.Success, _world.LastReturn);
        _world.FirstEnergyMillijoules = _world.LastEnergyMillijoules;
        _world.Time.Advance(ParseSeconds(seconds));
        ReadCounter();
        Assert.Equal(NvmlReturn.Success, _world.LastReturn);
    }

    [When(@"the total energy counter is read")]
    public void WhenTheTotalEnergyCounterIsRead()
    {
        ReadCounter();
    }

    [When(@"Init is called")]
    public void WhenInitIsCalled()
    {
        try
        {
            _world.LastReturn = _world.Api.Init();
        }
        catch (DllNotFoundException ex)
        {
            _world.LastException = ex;
        }
    }

    [When(@"power is read")]
    public void WhenPowerIsRead()
    {
        ReadPower();
    }

    [Then(@"the return code is Success and the value is (\d+) milliwatts")]
    public void ThenTheReturnCodeIsSuccessAndTheValueIsMilliwatts(uint expected)
    {
        Assert.Equal(NvmlReturn.Success, _world.LastReturn);
        Assert.Equal(expected, _world.LastPowerMilliwatts);
    }

    [Then(@"the difference is (\d+) millijoules")]
    public void ThenTheDifferenceIsMillijoules(ulong expected)
    {
        Assert.NotNull(_world.FirstEnergyMillijoules);
        Assert.NotNull(_world.LastEnergyMillijoules);
        Assert.Equal(expected, _world.LastEnergyMillijoules.Value - _world.FirstEnergyMillijoules.Value);
    }

    [Then(@"the return code is NotSupported \(3\) and no value is produced")]
    public void ThenTheReturnCodeIsNotSupportedAndNoValueIsProduced()
    {
        Assert.Equal(NvmlReturn.NotSupported, _world.LastReturn);
        Assert.Equal(3, (int)_world.LastReturn);
        Assert.Null(_world.LastEnergyMillijoules);
        Assert.Empty(_world.Api.CounterReads);
    }

    [Then(@"a DllNotFoundException is thrown")]
    public void ThenADllNotFoundExceptionIsThrown()
    {
        Assert.IsType<DllNotFoundException>(_world.LastException);
    }

    [Then(@"the return code is Uninitialized")]
    public void ThenTheReturnCodeIsUninitialized()
    {
        Assert.Equal(NvmlReturn.Uninitialized, _world.LastReturn);
        Assert.Null(_world.LastPowerMilliwatts);
    }

    [Given(@"the platform is Windows and ProgramW6432 is ""(.*)""")]
    public void GivenThePlatformIsWindowsAndProgramW6432Is(string programW6432)
    {
        _world.Platform = OSPlatform.Windows;
        _world.ProgramW6432 = programW6432;
    }

    [Given(@"ProgramW6432 is empty")]
    public void GivenProgramW6432IsEmpty()
    {
        _world.ProgramW6432 = string.Empty;
    }

    [Given(@"the platform is Linux")]
    public void GivenThePlatformIsLinux()
    {
        _world.Platform = OSPlatform.Linux;
    }

    [Given(@"the platform is neither Windows nor Linux")]
    public void GivenThePlatformIsNeitherWindowsNorLinux()
    {
        _world.Platform = OSPlatform.OSX;
    }

    [Given(@"an injected loader that fails the first candidate and succeeds on the second")]
    public void GivenAnInjectedLoaderThatFailsTheFirstCandidateAndSucceedsOnTheSecond()
    {
        string[] candidates = ["first-candidate", "second-candidate"];
        _world.LoaderCandidates = candidates;
        _world.Loader = name => name == candidates[1] ? SecondCandidateHandle : 0;
    }

    [Given(@"an injected loader that fails every candidate")]
    public void GivenAnInjectedLoaderThatFailsEveryCandidate()
    {
        _world.LoaderCandidates = ["first-candidate", "second-candidate"];
        _world.Loader = _ => 0;
    }

    [When(@"the candidate list is built")]
    public void WhenTheCandidateListIsBuilt()
    {
        _world.Candidates = NvmlLibraryCandidates.For(_world.Platform, _world.ProgramW6432);
    }

    [When(@"the Windows candidate list is built")]
    public void WhenTheWindowsCandidateListIsBuilt()
    {
        _world.Platform = OSPlatform.Windows;
        _world.Candidates = NvmlLibraryCandidates.For(_world.Platform, _world.ProgramW6432);
    }

    [When(@"TryLoadFirst runs")]
    public void WhenTryLoadFirstRuns()
    {
        Func<string, nint> loader = _world.Loader
            ?? throw new InvalidOperationException("No injected loader was scripted.");
        bool succeeded = false;
        nint handle = 0;
        _world.LastException = Record.Exception(
            () => succeeded = NvmlLibraryResolver.TryLoadFirst(_world.LoaderCandidates, loader, out handle));
        _world.LoadSucceeded = succeeded;
        _world.LoadedHandle = handle;
    }

    [Then(@"it is ""(.*)"" followed by ""(.*)""")]
    public void ThenItIsFollowedBy(string first, string second)
    {
        string[] expected = [first, second];
        Assert.Equal(expected, _world.Candidates);
    }

    [Then(@"it contains only ""(.*)""")]
    public void ThenItContainsOnly(string only)
    {
        string[] expected = [only];
        Assert.Equal(expected, _world.Candidates);
    }

    [Then(@"it is empty")]
    public void ThenItIsEmpty()
    {
        Assert.NotNull(_world.Candidates);
        Assert.Empty(_world.Candidates);
    }

    [Then(@"it returns true with the second candidate's handle")]
    public void ThenItReturnsTrueWithTheSecondCandidatesHandle()
    {
        Assert.Null(_world.LastException);
        Assert.True(_world.LoadSucceeded);
        Assert.Equal(SecondCandidateHandle, _world.LoadedHandle);
    }

    [Then(@"it returns false and no exception escapes")]
    public void ThenItReturnsFalseAndNoExceptionEscapes()
    {
        Assert.Null(_world.LastException);
        Assert.False(_world.LoadSucceeded);
        Assert.Equal(0, _world.LoadedHandle);
    }

    private static GpuOpenResult OpenThroughFactory(ScriptedNvmlApi api, OSPlatform? platform, int index) =>
        new NvmlGpuTelemetrySourceFactory(api, platform).Open(index);

    private void OpenAndHold(int index)
    {
        _world.OpenResult = OpenThroughFactory(_world.Api, _world.FactoryPlatform, index);
        Assert.True(_world.OpenResult.IsAvailable, _world.OpenResult.Reason);
        _world.Source = _world.OpenResult.Source;
    }

    [Given(@"a ScriptedNvmlApi with one device named ""(.*)"" and a (\d+) W power limit")]
    public void GivenAScriptedNvmlApiWithOneDeviceNamedAndAWPowerLimit(string name, uint watts)
    {
        _world.Api = new ScriptedNvmlApi(_world.Time) { Name = name, PowerLimitMilliwatts = watts * 1000, DeviceCount = 1 };
    }

    [Given(@"the scripted MIG query returns rc (\d+)")]
    public void GivenTheScriptedMigQueryReturnsRc(int code)
    {
        _world.Api = new ScriptedNvmlApi(_world.Time) { MigModeReturn = (NvmlReturn)code };
    }

    [Given(@"the scripted API is in missing-library mode on Windows")]
    public void GivenTheScriptedApiIsInMissingLibraryModeOnWindows()
    {
        _world.Api = new ScriptedNvmlApi(_world.Time) { MissingLibrary = true };
        _world.FactoryPlatform = OSPlatform.Windows;
    }

    [Given(@"nvmlInit_v2 returns rc (\d+)")]
    public void GivenNvmlInitV2ReturnsRc(int code)
    {
        _world.Api = new ScriptedNvmlApi(_world.Time) { InitReturn = (NvmlReturn)code };
    }

    [Given(@"the device count is (\d+)")]
    public void GivenTheDeviceCountIs(uint count)
    {
        _world.Api = new ScriptedNvmlApi(_world.Time) { DeviceCount = count };
    }

    [Given(@"an open source")]
    public void GivenAnOpenSource()
    {
        _world.Api = new ScriptedNvmlApi(_world.Time);
        OpenAndHold(0);
    }

    [Given(@"a disposed source")]
    public void GivenADisposedSource()
    {
        _world.Api = new ScriptedNvmlApi(_world.Time);
        OpenAndHold(0);
        _world.Source!.Dispose();
    }

    [When(@"the factory opens index (-?\d+)")]
    public void WhenTheFactoryOpensIndex(int index)
    {
        GpuOpenResult? result = null;
        _world.LastException = Record.Exception(() => result = OpenThroughFactory(_world.Api, _world.FactoryPlatform, index));
        _world.OpenResult = result;
        _world.Source = result?.Source;
    }

    [When(@"Dispose is called twice")]
    public void WhenDisposeIsCalledTwice()
    {
        IGpuTelemetrySource source = _world.Source ?? throw new InvalidOperationException("No source is open.");
        source.Dispose();
        source.Dispose();
    }

    [When(@"ReadPowerWatts is called")]
    public void WhenReadPowerWattsIsCalled()
    {
        IGpuTelemetrySource source = _world.Source ?? throw new InvalidOperationException("No source is open.");
        _world.LastException = Record.Exception(() => source.ReadPowerWatts());
    }

    [Then(@"the result is Available and the identity shows the name and a power limit of (\d+) W")]
    public void ThenTheResultIsAvailableAndTheIdentityShowsTheNameAndAPowerLimitOfW(double watts)
    {
        Assert.Null(_world.LastException);
        Assert.NotNull(_world.OpenResult);
        Assert.True(_world.OpenResult.IsAvailable, _world.OpenResult.Reason);
        Assert.NotNull(_world.OpenResult.Source);
        GpuIdentity identity = _world.OpenResult.Source.Identity;
        Assert.Equal(_world.Api.Name, identity.Name);
        Assert.Equal(watts, identity.PowerLimitWatts);
    }

    [Then(@"the identity MIG mode is ""(.*)""")]
    public void ThenTheIdentityMigModeIs(string expected)
    {
        Assert.Null(_world.LastException);
        Assert.NotNull(_world.OpenResult);
        Assert.True(_world.OpenResult.IsAvailable, _world.OpenResult.Reason);
        Assert.NotNull(_world.OpenResult.Source);
        Assert.Equal(expected, _world.OpenResult.Source.Identity.MigMode);
    }

    [Then(@"Shutdown was called exactly once")]
    public void ThenShutdownWasCalledExactlyOnce()
    {
        Assert.Equal(1, _world.Api.ShutdownCallCount);
    }

    [Then(@"the result is Unavailable, the reason contains ""(.*)"" and no exception escapes")]
    public void ThenTheResultIsUnavailableTheReasonContainsAndNoExceptionEscapes(string fragment)
    {
        Assert.Null(_world.LastException);
        Assert.NotNull(_world.OpenResult);
        Assert.False(_world.OpenResult.IsAvailable);
        Assert.Null(_world.OpenResult.Source);
        Assert.NotNull(_world.OpenResult.Reason);
        Assert.Contains(fragment, _world.OpenResult.Reason, StringComparison.Ordinal);
    }

    [Then(@"the result is Unavailable with reason ""(.*)""")]
    public void ThenTheResultIsUnavailableWithReason(string expected)
    {
        Assert.Null(_world.LastException);
        Assert.NotNull(_world.OpenResult);
        Assert.False(_world.OpenResult.IsAvailable);
        Assert.Null(_world.OpenResult.Source);
        Assert.Equal(expected, _world.OpenResult.Reason);
    }

    [Then(@"an ObjectDisposedException is thrown")]
    public void ThenAnObjectDisposedExceptionIsThrown()
    {
        Assert.IsType<ObjectDisposedException>(_world.LastException);
    }

    private void EvaluateGate()
    {
        _world.GateSkipReason = IntegrationGate.SkipReason(_world.GateValue);
        _world.GateEvaluated = true;
    }

    private void RunNvmlIntegrationTest()
    {
        EvaluateGate();
        if (_world.GateSkipReason is not null)
        {
            return;
        }

        var factory = new NvmlGpuTelemetrySourceFactory(_world.Api, _world.FactoryPlatform);
        LaptopGpuCheck? check = null;
        _world.CheckFailure = Record.Exception(() => check = NvmlIntegrationChecks.AssertLaptopGpu(factory));
        _world.LaptopCheck = check;
    }

    [Given(@"AGENTJOULES_INTEGRATION is ""(.*)"" and the NVIDIA driver is installed")]
    public void GivenAgentJoulesIntegrationIsAndTheNvidiaDriverIsInstalled(string value)
    {
        _world.GateValue = value;
        _world.Api = new ScriptedNvmlApi(_world.Time);
        _world.FactoryPlatform = OSPlatform.Windows;
    }

    [Given(@"AGENTJOULES_INTEGRATION is ""(.*)"" and NVML cannot be loaded")]
    public void GivenAgentJoulesIntegrationIsAndNvmlCannotBeLoaded(string value)
    {
        _world.GateValue = value;
        _world.Api = new ScriptedNvmlApi(_world.Time) { MissingLibrary = true };
        _world.FactoryPlatform = OSPlatform.Windows;
    }

    [Given(@"the variable value is ""(.*)""")]
    public void GivenTheVariableValueIs(string value)
    {
        _world.GateValue = value;
    }

    [Given(@"the variable is unset")]
    public void GivenTheVariableIsUnset()
    {
        _world.GateValue = null;
    }

    [Given(@"a reported power limit of (\d+) W")]
    public void GivenAReportedPowerLimitOfW(double watts)
    {
        _world.ReportedPowerLimitWatts = watts;
    }

    [When(@"IntegrationGate\.SkipReason is evaluated")]
    public void WhenIntegrationGateSkipReasonIsEvaluated()
    {
        EvaluateGate();
    }

    [When(@"the NVML integration test opens device 0")]
    public void WhenTheNvmlIntegrationTestOpensDevice0()
    {
        RunNvmlIntegrationTest();
    }

    [When(@"the NVML integration test runs")]
    public void WhenTheNvmlIntegrationTestRuns()
    {
        RunNvmlIntegrationTest();
    }

    [When(@"PowerLimitInRange is evaluated")]
    public void WhenPowerLimitInRangeIsEvaluated()
    {
        _world.CheckFailure = Record.Exception(() => GpuAssertions.PowerLimitInRange(_world.ReportedPowerLimitWatts));
    }

    [Then(@"it returns null")]
    public void ThenItReturnsNull()
    {
        Assert.True(_world.GateEvaluated);
        Assert.Null(_world.GateSkipReason);
    }

    [Then(@"it returns ""(.*)""")]
    public void ThenItReturns(string expected)
    {
        Assert.True(_world.GateEvaluated);
        Assert.Equal(expected, _world.GateSkipReason);
    }

    [Then(@"the test is skipped")]
    public void ThenTheTestIsSkipped()
    {
        Assert.True(_world.GateEvaluated);
        Assert.Equal(ClosedGateReason, _world.GateSkipReason);
    }

    [Then(@"the name contains ""(.*)"" and the power limit is between (\d+) and (\d+) W")]
    public void ThenTheNameContainsAndThePowerLimitIsBetweenAndW(string fragment, double minimum, double maximum)
    {
        Assert.Null(_world.GateSkipReason);
        Assert.Null(_world.CheckFailure);
        Assert.NotNull(_world.LaptopCheck);
        Assert.Contains(fragment, _world.LaptopCheck.Name, StringComparison.Ordinal);
        Assert.InRange(_world.LaptopCheck.PowerLimitWatts, minimum, maximum);

        // The check must release its NVML session itself; ScenarioWorld.Dispose would otherwise hide a leak.
        Assert.False(_world.Api.IsInitialised);
    }

    [Then(@"it fails with the Unavailable reason instead of skipping")]
    public void ThenItFailsWithTheUnavailableReasonInsteadOfSkipping()
    {
        Assert.True(_world.GateEvaluated);
        Assert.Null(_world.GateSkipReason);
        Assert.Null(_world.LaptopCheck);
        XunitException failure = Assert.IsType<XunitException>(_world.CheckFailure, exactMatch: false);
        Assert.StartsWith("NVML unavailable: ", failure.Message, StringComparison.Ordinal);
    }

    [Then(@"it fails")]
    public void ThenItFails()
    {
        Assert.IsType<XunitException>(_world.CheckFailure, exactMatch: false);
    }

    [Given(@"a metric created with Measured\((\d+), ""([^""]*)""\)")]
    public void GivenAMetricCreatedWithMeasured(double value, string unit)
    {
        _world.CurrentMetric = Metric.Measured(value, unit);
    }

    [Given(@"a metric created with NotReported\(""([^""]*)""\)")]
    public void GivenAMetricCreatedWithNotReported(string reason)
    {
        _world.CurrentMetric = Metric.NotReported(reason);
    }

    [Given(@"the flags (\w+) and (\w+)")]
    public void GivenTheFlagsAnd(string first, string second)
    {
        _world.Flags = Enum.Parse<ReadingFlags>(first) | Enum.Parse<ReadingFlags>(second);
    }

    [Given(@"a metric constructed with label N/A and value (\d+)")]
    public void GivenAMetricConstructedWithLabelNAAndValue(int value)
    {
        _world.Construction = () => Deserialize(string.Create(CultureInfo.InvariantCulture, $"{{\"value\":{value},\"label\":\"N/A\",\"reason\":\"x\"}}"));
    }

    [Given(@"a metric constructed with label measured and a null value")]
    public void GivenAMetricConstructedWithLabelMeasuredAndANullValue()
    {
        _world.Construction = () => Deserialize("""{"value":null,"label":"measured","unit":"J"}""");
    }

    [Given(@"^a metric constructed with label N/A and an empty reason$")]
    public void GivenAMetricConstructedWithLabelNAAndAnEmptyReason()
    {
        _world.Construction = () => Deserialize("""{"value":null,"label":"N/A","reason":""}""");
    }

    [Given(@"a measured metric with value NaN")]
    public void GivenAMeasuredMetricWithValueNaN()
    {
        _world.Construction = () => Metric.Measured(double.NaN, "J");
    }

    [Given(@"the string ""([^""]*)""")]
    public void GivenTheString(string text)
    {
        _world.FlagName = text;
    }

    [When(@"it is serialized")]
    public void WhenItIsSerialized()
    {
        _world.Json = JsonSerializer.Serialize(_world.CurrentMetric, MetricJsonOptions);
    }

    [When(@"ToStrings is called")]
    public void WhenToStringsIsCalled()
    {
        _world.FlagStrings = ReadingFlagNames.ToStrings(_world.Flags);
    }

    [When(@"the constructor runs")]
    public void WhenTheConstructorRuns()
    {
        Assert.NotNull(_world.Construction);
        try
        {
            _world.CurrentMetric = _world.Construction();
        }
        catch (Exception ex)
        {
            _world.ConstructionFailure = ex;
        }
    }

    [When(@"ReadingFlagNames\.Parse runs")]
    public void WhenReadingFlagNamesParseRuns()
    {
        Assert.NotNull(_world.FlagName);
        try
        {
            _ = ReadingFlagNames.Parse(_world.FlagName);
        }
        catch (Exception ex)
        {
            _world.ParseFailure = ex;
        }
    }

    [Then(@"the JSON has ""value"": (\d+), ""label"": ""([^""]*)"" and ""unit"": ""([^""]*)""")]
    public void ThenTheJsonHasValueLabelAndUnit(double value, string label, string unit)
    {
        Assert.NotNull(_world.Json);
        using JsonDocument document = JsonDocument.Parse(_world.Json);
        JsonElement root = document.RootElement;

        Assert.Equal(value, root.GetProperty("value").GetDouble());
        Assert.Equal(label, root.GetProperty("label").GetString());
        Assert.Equal(unit, root.GetProperty("unit").GetString());
        Assert.False(root.TryGetProperty("reason", out _));
        Assert.False(root.TryGetProperty("currency", out _));
    }

    [Then(@"the JSON has ""value"": null, ""label"": ""([^""]*)"" and the reason")]
    public void ThenTheJsonHasValueNullLabelAndTheReason(string label)
    {
        Assert.NotNull(_world.Json);
        Assert.NotNull(_world.CurrentMetric);
        using JsonDocument document = JsonDocument.Parse(_world.Json);
        JsonElement root = document.RootElement;

        Assert.True(root.TryGetProperty("value", out JsonElement value));
        Assert.Equal(JsonValueKind.Null, value.ValueKind);
        Assert.Equal(label, root.GetProperty("label").GetString());
        Assert.False(string.IsNullOrWhiteSpace(_world.CurrentMetric.Reason));
        Assert.Equal(_world.CurrentMetric.Reason, root.GetProperty("reason").GetString());
        Assert.False(root.TryGetProperty("unit", out _));
        Assert.False(root.TryGetProperty("currency", out _));
    }

    [Then(@"the result is ""([^""]*)"" and ""([^""]*)""")]
    public void ThenTheResultIsAnd(string first, string second)
    {
        Assert.NotNull(_world.FlagStrings);
        string[] expectedNames = [first, second];
        Assert.Equal(expectedNames, _world.FlagStrings);
    }

    [Then(@"an ArgumentException is thrown")]
    public void ThenAnArgumentExceptionIsThrown()
    {
        Exception? failure = _world.ConstructionFailure;
        Assert.NotNull(failure);
        Assert.IsType<ArgumentException>(failure, exactMatch: false);
    }

    [Then(@"it fails with a typed error")]
    public void ThenItFailsWithATypedError()
    {
        ArgumentException failure = Assert.IsType<ArgumentException>(_world.ParseFailure, exactMatch: false);
        Assert.Contains(_world.FlagName!, failure.Message, StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions MetricJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static Metric Deserialize(string json) =>
        JsonSerializer.Deserialize<Metric>(json, MetricJsonOptions) ?? throw new InvalidOperationException("The JSON document deserialised to null.");
}