using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Energy;
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
        // The label scenarios record a construction failure, the integration scenarios an integrator failure; only one is ever set.
        Exception? failure = _world.ConstructionFailure ?? _world.IntegrationFailure;
        Assert.NotNull(failure);
        Assert.IsType<ArgumentException>(failure, exactMatch: false);
        if (_world.ConstructionFailure is null)
        {
            // An integration rejection in these scenarios blames a sample, so its message must name the index.
            Assert.Contains("index", failure.Message, StringComparison.Ordinal);
        }
    }

    private static double[] ParseTimestamps(string list) =>
        [.. list.Replace(" and ", ", ", StringComparison.Ordinal)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(token => double.Parse(token, CultureInfo.InvariantCulture))];

    private static double ParseNumber(string number) => double.Parse(number, CultureInfo.InvariantCulture);

    private void AddSamples(IEnumerable<double> timestamps, Func<double, double> wattsAt)
    {
        foreach (double seconds in timestamps)
        {
            _world.PowerSamples.Add(new PowerSample(seconds, wattsAt(seconds), ThrottleReasons.Unsupported));
        }
    }

    private void IntegrateWindow()
    {
        _world.IntegrationRan = true;
        _world.IntegratedJoules = null;
        _world.IntegrationFailure = null;
        try
        {
            _world.IntegratedJoules = PowerIntegrator.IntegrateJoules(_world.PowerSamples, _world.WindowStartSeconds, _world.WindowStopSeconds);
        }
        catch (ArgumentException ex)
        {
            _world.IntegrationFailure = ex;
        }
    }

    [Given(@"^(\d+) samples of (\d+) mW at t = 0\.0, 0\.1, \.\.\., 2\.0 s$")]
    public void GivenSamplesOfMilliwattsAtTenthSecondSteps(int count, int milliwatts)
    {
        double watts = milliwatts / 1000.0;
        AddSamples(Enumerable.Range(0, count).Select(i => i / 10.0), _ => watts);
    }

    [Given(@"^a (\d+) W to (\d+) W linear ramp sampled at (.+) s$")]
    public void GivenALinearRampSampledAt(double fromWatts, double toWatts, string timestamps)
    {
        double[] times = ParseTimestamps(timestamps);
        double span = times[^1] - times[0];
        AddSamples(times, t => fromWatts + (toWatts - fromWatts) * (t - times[0]) / span);
    }

    [Given(@"^two (\d+) W samples at (\S+) s and (\S+) s$")]
    public void GivenTwoSamplesAt(double watts, string first, string second)
    {
        AddSamples([ParseNumber(first), ParseNumber(second)], _ => watts);
    }

    [Given(@"^samples at (.+) s following power = 100 W/s x t$")]
    public void GivenSamplesFollowingPowerEqualsHundredWattsPerSecondTimesT(string timestamps)
    {
        AddSamples(ParseTimestamps(timestamps), t => 100.0 * t);
    }

    [Given(@"^samples at (.+) s$")]
    public void GivenSamplesAt(string timestamps)
    {
        AddSamples(ParseTimestamps(timestamps), _ => 50.0);
    }

    [Given(@"^a single sample in the window$")]
    public void GivenASingleSampleInTheWindow()
    {
        AddSamples([1.0], _ => 50.0);
    }

    [Given(@"^a sample at (\S+) s and the window (\S+) to (\S+) s$")]
    public void GivenASampleAtAndTheWindow(string sample, string start, string stop)
    {
        AddSamples([ParseNumber(sample)], _ => 50.0);
        _world.WindowStartSeconds = ParseNumber(start);
        _world.WindowStopSeconds = ParseNumber(stop);
    }

    [When(@"^the window (\S+) to (\S+) s is integrated$")]
    public void WhenTheWindowIsIntegrated(string start, string stop)
    {
        _world.WindowStartSeconds = ParseNumber(start);
        _world.WindowStopSeconds = ParseNumber(stop);
        IntegrateWindow();
        Assert.Null(_world.IntegrationFailure);
    }

    [When(@"^the integrator runs$")]
    public void WhenTheIntegratorRuns()
    {
        IntegrateWindow();
    }

    [Then(@"^the result is (\S+) J within 1 percent$")]
    public void ThenTheResultIsJoulesWithinOnePercent(string expected)
    {
        Assert.NotNull(_world.IntegratedJoules);
        double target = ParseNumber(expected);
        Assert.InRange(_world.IntegratedJoules.Value, target * 0.99, target * 1.01);
    }

    [Then(@"^the result is (\S+) J$")]
    public void ThenTheResultIsJoules(string expected)
    {
        Assert.NotNull(_world.IntegratedJoules);
        Assert.Equal(ParseNumber(expected), _world.IntegratedJoules.Value, 1e-9);
    }

    [Then(@"^the result is null, not 0$")]
    public void ThenTheResultIsNullNotZero()
    {
        Assert.True(_world.IntegrationRan);
        Assert.Null(_world.IntegrationFailure);
        Assert.Null(_world.IntegratedJoules);
    }

    [Then(@"it fails with a typed error")]
    public void ThenItFailsWithATypedError()
    {
        ArgumentException failure = Assert.IsType<ArgumentException>(_world.ParseFailure, exactMatch: false);
        Assert.Contains(_world.FlagName!, failure.Message, StringComparison.Ordinal);
    }

    private EnergyReading ComputedReading =>
        _world.Reading ?? throw new InvalidOperationException("No reading was computed.");

    private void GivenConstantPowerOverWindow(double watts, double windowSeconds)
    {
        int count = (int)Math.Round(windowSeconds * 10.0) + 1;
        AddSamples(Enumerable.Range(0, count).Select(i => i / 10.0), _ => watts);
        _world.WindowStartSeconds = 0.0;
        _world.WindowStopSeconds = windowSeconds;
    }

    private static void AssertMeasured(Metric metric, double expected, string unit)
    {
        Assert.Equal(MetricLabel.Measured, metric.Label);
        Assert.Equal(unit, metric.Unit);
        Assert.NotNull(metric.Value);
        Assert.Equal(expected, metric.Value.Value, 1e-9);
    }

    private static void AssertNotReported(Metric metric, string reason)
    {
        Assert.Equal(MetricLabel.NotReported, metric.Label);
        Assert.Equal(reason, metric.Reason);
        Assert.Null(metric.Value);
    }

    [Given(@"^(\d+) samples of (\d+) W over a (\d+) s window and a baseline of (\d+) W$")]
    public void GivenSamplesOfWattsOverAWindowAndABaseline(int count, double watts, double windowSeconds, double baselineWatts)
    {
        AddSamples(Enumerable.Range(0, count).Select(i => i / 10.0), _ => watts);
        _world.WindowStartSeconds = 0.0;
        _world.WindowStopSeconds = windowSeconds;
        _world.BaselineMeanWatts = baselineWatts;
    }

    [Given(@"^a (\d+) s window at (\d+) W and a baseline of (\d+) W$")]
    public void GivenAWindowAtWattsAndABaseline(double windowSeconds, double watts, double baselineWatts)
    {
        GivenConstantPowerOverWindow(watts, windowSeconds);
        _world.BaselineMeanWatts = baselineWatts;
    }

    [Given(@"^method counter, a counter delta of (-?\d+) mJ and samples at (\d+) W for (\d+) s$")]
    public void GivenMethodCounterWithADeltaAndSamples(long deltaMillijoules, double watts, double windowSeconds)
    {
        _world.ReadingMethod = EnergyMethod.Counter;
        _world.CounterDeltaMillijoules = deltaMillijoules;
        GivenConstantPowerOverWindow(watts, windowSeconds);
    }

    [Given(@"^a (\d+) ms window at (\d+) Hz sampling$")]
    public void GivenAMillisecondWindowAtHzSampling(int milliseconds, double hertz)
    {
        double stop = milliseconds / 1000.0;
        List<double> timestamps = [];
        for (int i = 0; i / hertz < stop - 1e-9; i++)
        {
            timestamps.Add(i / hertz);
        }

        timestamps.Add(stop);
        AddSamples(timestamps, _ => 60.0);
        _world.WindowStartSeconds = 0.0;
        _world.WindowStopSeconds = stop;
        _world.ReadingSampleRateHz = hertz;
    }

    [Given(@"^a window and no baseline$")]
    public void GivenAWindowAndNoBaseline()
    {
        GivenConstantPowerOverWindow(60.0, 3.0);
        _world.BaselineMeanWatts = null;
    }

    [Given(@"^a counter delta of (-?\d+) mJ$")]
    public void GivenACounterDeltaOf(long deltaMillijoules)
    {
        _world.ReadingMethod = EnergyMethod.Counter;
        _world.CounterDeltaMillijoules = deltaMillijoules;
        GivenConstantPowerOverWindow(60.0, 3.0);
    }

    [Given(@"^a window with one sample and method sampling$")]
    public void GivenAWindowWithOneSampleAndMethodSampling()
    {
        _world.ReadingMethod = EnergyMethod.Sampling;
        AddSamples([1.5], _ => 60.0);
        _world.WindowStartSeconds = 0.0;
        _world.WindowStopSeconds = 3.0;
    }

    [When(@"^the reading is computed$")]
    public void WhenTheReadingIsComputed()
    {
        _world.Reading = ReadingCalculator.Compute(new ReadingInput(
            Samples: _world.PowerSamples,
            WindowStartSeconds: _world.WindowStartSeconds,
            WindowStopSeconds: _world.WindowStopSeconds,
            Method: _world.ReadingMethod,
            CounterDeltaMillijoules: _world.CounterDeltaMillijoules,
            BaselineMeanWatts: _world.BaselineMeanWatts,
            SampleRateHz: _world.ReadingSampleRateHz,
            IsSamplingFallback: _world.ReadingIsSamplingFallback));
    }

    [Then(@"^gross is (\S+) J, net is (\S+) J, average is (\S+) W, peak is (\S+) W and sampleCount is (\d+)$")]
    public void ThenTheFourMetricsAndTheSampleCountAre(string gross, string net, string average, string peak, int sampleCount)
    {
        EnergyReading reading = ComputedReading;
        AssertMeasured(reading.GrossJoules, ParseNumber(gross), "J");
        AssertMeasured(reading.NetJoules, ParseNumber(net), "J");
        AssertMeasured(reading.AverageWatts, ParseNumber(average), "W");
        AssertMeasured(reading.PeakWatts, ParseNumber(peak), "W");
        Assert.Equal(sampleCount, reading.SampleCount);
    }

    [Then(@"^net is (\S+) J and the flags contain ""([^""]*)""$")]
    public void ThenNetIsAndTheFlagsContain(string net, string flag)
    {
        AssertMeasured(ComputedReading.NetJoules, ParseNumber(net), "J");
        Assert.Contains(flag, ReadingFlagNames.ToStrings(ComputedReading.Flags));
    }

    [Then(@"^gross is (\S+) J and average is (\S+) W from the samples$")]
    public void ThenGrossIsAndAverageIsFromTheSamples(string gross, string average)
    {
        AssertMeasured(ComputedReading.GrossJoules, ParseNumber(gross), "J");
        AssertMeasured(ComputedReading.AverageWatts, ParseNumber(average), "W");
    }

    [Then(@"^the flags contain ""([^""]*)"" and the reading is kept$")]
    public void ThenTheFlagsContainAndTheReadingIsKept(string flag)
    {
        EnergyReading reading = ComputedReading;
        Assert.Contains(flag, ReadingFlagNames.ToStrings(reading.Flags));
        Assert.Equal(MetricLabel.Measured, reading.GrossJoules.Label);
        Assert.Equal(_world.PowerSamples.Count, reading.SampleCount);
    }

    [Then(@"^the flags do not contain ""([^""]*)""$")]
    public void ThenTheFlagsDoNotContain(string flag)
    {
        Assert.DoesNotContain(flag, ReadingFlagNames.ToStrings(ComputedReading.Flags));
    }

    [Then(@"^net is label not-reported with reason ""([^""]*)"" and value null$")]
    public void ThenNetIsNotReportedWithReason(string reason)
    {
        AssertNotReported(ComputedReading.NetJoules, reason);
    }

    [Then(@"^gross and net are not-reported with reason ""([^""]*)"" and no value is 0$")]
    public void ThenGrossAndNetAreNotReportedAndNoValueIsZero(string reason)
    {
        EnergyReading reading = ComputedReading;
        AssertNotReported(reading.GrossJoules, reason);
        AssertNotReported(reading.NetJoules, reason);
        foreach (Metric metric in new[] { reading.GrossJoules, reading.NetJoules, reading.AverageWatts, reading.PeakWatts })
        {
            Assert.True(metric.Value is not 0.0, "No metric of the reading may carry the value 0.");
        }
    }

    [Then(@"^gross, average and peak are not-reported with reason ""([^""]*)""$")]
    public void ThenGrossAverageAndPeakAreNotReported(string reason)
    {
        EnergyReading reading = ComputedReading;
        AssertNotReported(reading.GrossJoules, reason);
        AssertNotReported(reading.AverageWatts, reason);
        AssertNotReported(reading.PeakWatts, reason);
    }

    private static readonly JsonSerializerOptions MetricJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static Metric Deserialize(string json) =>
        JsonSerializer.Deserialize<Metric>(json, MetricJsonOptions) ?? throw new InvalidOperationException("The JSON document deserialised to null.");

    private void OpenFakeSource(ScriptedNvmlApi api)
    {
        _world.Api = api;
        GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api).Open(0);
        Assert.True(result.IsAvailable, result.Reason);
        _world.Source = result.Source;
    }

    private static string MethodName(MeterCapabilities capabilities) =>
        capabilities.Method == EnergyMethod.Counter ? "counter" : "sampling";

    private void SelectMethod(string? configured)
    {
        // A scenario that only states the configured value has no GPU yet; give it a default one with the counter supported.
        if (_world.Source is null)
        {
            OpenFakeSource(new ScriptedNvmlApi(_world.Time) { ConstantPowerWatts = 50, CounterSupported = true });
        }

        IGpuTelemetrySource source = _world.Source ?? throw new InvalidOperationException("No GPU source was opened.");
        _world.Capabilities = null;
        _world.SelectionFailure = null;
        try
        {
            _world.Capabilities = MeterMethodSelector.Select(configured, source, _world.ReadingSampleRateHz);
        }
        catch (AgentJoulesException failure)
        {
            _world.SelectionFailure = failure;
        }
    }

    private MeterCapabilities SelectedCapabilities =>
        _world.Capabilities ?? throw new InvalidOperationException("No method was selected: " + _world.SelectionFailure);

    // A reading over a one second window at 50 W, computed from what the selector decided and nothing else.
    private EnergyReading ReadingFromCapabilities()
    {
        MeterCapabilities capabilities = SelectedCapabilities;
        _world.Reading = ReadingCalculator.Compute(new ReadingInput(
            Samples: [new PowerSample(0.0, 50.0, ThrottleReasons.Unsupported), new PowerSample(1.0, 50.0, ThrottleReasons.Unsupported)],
            WindowStartSeconds: 0.0,
            WindowStopSeconds: 1.0,
            Method: capabilities.Method,
            CounterDeltaMillijoules: capabilities.Method == EnergyMethod.Counter ? 50000L : null,
            BaselineMeanWatts: null,
            SampleRateHz: capabilities.SampleRateHz,
            IsSamplingFallback: capabilities.IsFallback));
        return _world.Reading;
    }

    [Given(@"^a fake NVML whose counter is supported$")]
    public void GivenAFakeNvmlWhoseCounterIsSupported()
    {
        OpenFakeSource(new ScriptedNvmlApi(_world.Time) { ConstantPowerWatts = 50, CounterSupported = true });
    }

    [Given(@"^a fake NVML returning NOT_SUPPORTED for the counter$")]
    public void GivenAFakeNvmlReturningNotSupportedForTheCounter()
    {
        OpenFakeSource(new ScriptedNvmlApi(_world.Time) { ConstantPowerWatts = 50, CounterReturn = NvmlReturn.NotSupported });
    }

    [Given(@"^the counter read returns rc (\d+)$")]
    public void GivenTheCounterReadReturnsRc(int code)
    {
        OpenFakeSource(new ScriptedNvmlApi(_world.Time) { ConstantPowerWatts = 50, CounterReturn = (NvmlReturn)code });
    }

    [Given(@"^Meter:Method is ""([^""]*)""$")]
    public void GivenMeterMethodIs(string method)
    {
        _world.ConfiguredMethod = method;
    }

    [When(@"^the method is selected with Meter:Method (\S+)$")]
    public void WhenTheMethodIsSelectedWithMeterMethod(string method)
    {
        SelectMethod(method);
    }

    [When(@"^the method is selected with Meter:Method (\S+) and a reading is computed$")]
    public void WhenTheMethodIsSelectedWithMeterMethodAndAReadingIsComputed(string method)
    {
        SelectMethod(method);
        _ = ReadingFromCapabilities();
    }

    [When(@"^the method is selected$")]
    public void WhenTheMethodIsSelected()
    {
        SelectMethod(_world.ConfiguredMethod);
    }

    [Then(@"^the method is ""([^""]*)"" and IsFallback is false$")]
    public void ThenTheMethodIsAndIsFallbackIsFalse(string method)
    {
        MeterCapabilities capabilities = SelectedCapabilities;
        Assert.Equal(method, MethodName(capabilities));
        Assert.False(capabilities.IsFallback);
    }

    [Then(@"^the method is ""([^""]*)"", the reading flags contain ""([^""]*)"" and Describe says ""([^""]*)""$")]
    public void ThenTheMethodIsTheReadingFlagsContainAndDescribeSays(string method, string flag, string description)
    {
        MeterCapabilities capabilities = SelectedCapabilities;
        Assert.Equal(method, MethodName(capabilities));
        Assert.True(capabilities.IsFallback);
        Assert.Contains(flag, ReadingFlagNames.ToStrings(ComputedReading.Flags));
        Assert.Equal(description, capabilities.Describe());
    }

    [Then(@"^the method is ""([^""]*)"" and the flags do not contain ""([^""]*)""$")]
    public void ThenTheMethodIsAndTheFlagsDoNotContain(string method, string flag)
    {
        MeterCapabilities capabilities = SelectedCapabilities;
        Assert.Equal(method, MethodName(capabilities));
        Assert.DoesNotContain(flag, ReadingFlagNames.ToStrings(ReadingFromCapabilities().Flags));
    }

    [Then(@"^a MeasurementPreconditionException with exit code (\d+) is thrown and its message names ""([^""]*)""$")]
    public void ThenAMeasurementPreconditionExceptionIsThrown(int exitCode, string name)
    {
        MeasurementPreconditionException failure = Assert.IsType<MeasurementPreconditionException>(_world.SelectionFailure);
        Assert.Equal(exitCode, (int)failure.ExitCode);
        Assert.Contains(name, failure.Message, StringComparison.Ordinal);
    }

    [Then(@"^a ConfigurationException with exit code (\d+) is thrown$")]
    public void ThenAConfigurationExceptionIsThrown(int exitCode)
    {
        ConfigurationException failure = Assert.IsType<ConfigurationException>(_world.SelectionFailure);
        Assert.Equal(exitCode, (int)failure.ExitCode);
    }

    [Then(@"^the method is ""([^""]*)"" with reason ""([^""]*)"" including the code$")]
    public void ThenTheMethodIsWithReasonIncludingTheCode(string method, string reason)
    {
        MeterCapabilities capabilities = SelectedCapabilities;
        Assert.Equal(method, MethodName(capabilities));
        Assert.True(capabilities.IsFallback);
        string code = ((int)_world.Api.CounterReturn).ToString(CultureInfo.InvariantCulture);
        Assert.Contains(reason, capabilities.CounterUnsupportedReason, StringComparison.Ordinal);
        Assert.Contains(code, capabilities.CounterUnsupportedReason, StringComparison.Ordinal);
    }

    // AJ-028: throttle detection. A window is 11 samples at 50 W, 0.0 to 1.0 s, both edges included, each carrying scripted clock-event bits.
    private const int ThrottleSampleCount = 11;
    private const string SupportedSentence = "throttle reasons are supported on this GPU";
    private const string UnavailableSentence = "throttle reasons are unavailable on this GPU";
    private const string ProbeNotesHeading = "## Probe and WSL2 notes";

    private static ulong ParseBits(string hex) =>
        ulong.Parse(hex[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    // Every sample's reasons come from NvmlGpuTelemetrySource.ReadThrottleReasons() over a scripted NVML session: the fake reports
    // bitsAt(i) for sample i, or answers every clock-event query with clockReturn when that is not Success.
    private void AddThrottleWindow(Func<int, ulong> bitsAt, NvmlReturn clockReturn = NvmlReturn.Success)
    {
        var api = new ScriptedNvmlApi(_world.Time) { ConstantPowerWatts = 50, CounterSupported = true, ClockEventReturn = clockReturn };
        OpenFakeSource(api);
        IGpuTelemetrySource source = _world.Source ?? throw new InvalidOperationException("No GPU source was opened.");
        for (int i = 0; i < ThrottleSampleCount; i++)
        {
            api.ClockEventReasons = bitsAt(i);
            _world.PowerSamples.Add(new PowerSample(i / 10.0, 50.0, source.ReadThrottleReasons()));
        }

        _world.WindowStartSeconds = 0.0;
        _world.WindowStopSeconds = (ThrottleSampleCount - 1) / 10.0;
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "global.json")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("global.json was not found above " + AppContext.BaseDirectory);
    }

    // The text of one "## " section of the build log, from its heading line to the next "## " heading or the end of the file.
    private static string BuildLogSection(string heading)
    {
        string text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "build-log.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = text.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs/build-log.md has no section '" + heading + "'.");
        int next = text.IndexOf("\n## ", start + heading.Length + 2, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }

    [Given(@"^a fake NVML reporting clock event bit (0x[0-9A-Fa-f]+) for the whole window$")]
    public void GivenAFakeNvmlReportingClockEventBitForTheWholeWindow(string bits)
    {
        AddThrottleWindow(_ => ParseBits(bits));
    }

    [Given(@"^bit (0x[0-9A-Fa-f]+) appears only in the last sample of the window$")]
    public void GivenBitAppearsOnlyInTheLastSampleOfTheWindow(string bits)
    {
        AddThrottleWindow(i => i == ThrottleSampleCount - 1 ? ParseBits(bits) : 0UL);
    }

    [Given(@"^the fake reports only the GpuIdle bit (0x[0-9A-Fa-f]+)$")]
    public void GivenTheFakeReportsOnlyTheGpuIdleBit(string bits)
    {
        AddThrottleWindow(_ => ParseBits(bits));
    }

    [Given(@"^both throttle exports are missing$")]
    public void GivenBothThrottleExportsAreMissing()
    {
        // The fake models a driver without either export as NotSupported; the source maps it to Unsupported on every sample.
        AddThrottleWindow(_ => 0UL, NvmlReturn.NotSupported);
    }

    [Given(@"^the fake has no nvmlDeviceGetCurrentClocksEventReasons export but has the throttle-reasons export$")]
    public void GivenTheFakeHasNoCurrentExportButHasTheDeprecatedOne()
    {
        int Current(nint device, out ulong reasons)
        {
            _world.ClocksCurrentCalls++;
            throw new EntryPointNotFoundException("nvmlDeviceGetCurrentClocksEventReasons");
        }

        int Deprecated(nint device, out ulong reasons)
        {
            _world.ClocksDeprecatedCalls++;
            reasons = 0x4UL;
            return (int)NvmlReturn.Success;
        }

        _world.ClocksReader = new ClocksEventReader(Current, Deprecated);
    }

    [When(@"^throttle reasons are read$")]
    public void WhenThrottleReasonsAreRead()
    {
        ClocksEventReader reader = _world.ClocksReader ?? throw new InvalidOperationException("No clocks-event reader was created.");
        _world.LastReturn = reader.Read(0x1000, out ulong bits);
        _world.ClocksBits = bits;
    }

    [Then(@"^the bits are returned from the deprecated export$")]
    public void ThenTheBitsAreReturnedFromTheDeprecatedExport()
    {
        Assert.Equal(NvmlReturn.Success, _world.LastReturn);
        Assert.Equal(0x4UL, _world.ClocksBits);
        Assert.Equal(1, _world.ClocksCurrentCalls);
        Assert.Equal(1, _world.ClocksDeprecatedCalls);
    }

    [Then(@"^the flags contain ""throttled"" and throttleReasons contains ""([^""]*)""$")]
    public void ThenTheFlagsContainThrottledAndThrottleReasonsContains(string reason)
    {
        EnergyReading reading = ComputedReading;
        Assert.Contains("throttled", ReadingFlagNames.ToStrings(reading.Flags));
        Assert.NotNull(reading.ThrottleReasons);
        Assert.Contains(reason, reading.ThrottleReasons);
    }

    [Then(@"^the flags do not contain ""throttled"" and throttleReasons is an empty list$")]
    public void ThenTheFlagsDoNotContainThrottledAndThrottleReasonsIsAnEmptyList()
    {
        EnergyReading reading = ComputedReading;
        Assert.DoesNotContain("throttled", ReadingFlagNames.ToStrings(reading.Flags));
        Assert.NotNull(reading.ThrottleReasons);
        Assert.Empty(reading.ThrottleReasons);
    }

    [Then(@"^throttleReasons is null, the flags do not contain ""throttled"" and nothing claims the GPU was not throttled$")]
    public void ThenThrottleReasonsIsNullAndNothingClaimsTheGpuWasNotThrottled()
    {
        EnergyReading reading = ComputedReading;
        Assert.Null(reading.ThrottleReasons);
        Assert.DoesNotContain("throttled", ReadingFlagNames.ToStrings(reading.Flags));

        // "Not throttled" would have to appear as a throttled flag or a throttled property; the document carries neither.
        string json = JsonSerializer.Serialize(reading, MetricJsonOptions);
        Assert.Contains("\"throttleReasons\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"throttled\"", json, StringComparison.Ordinal);
    }

    [Given(@"^AGENTJOULES_INTEGRATION is ""([^""]*)""$")]
    public void GivenAgentJoulesIntegrationIs(string value)
    {
        _world.GateValue = value;
    }

    [When(@"^the throttle API is queried on the laptop$")]
    public void WhenTheThrottleApiIsQueriedOnTheLaptop()
    {
        // The query itself is the gated NVML integration test of AJ-023 (nvmlDeviceGetCurrentClocksEventReasons, rc 0 on this laptop).
        // The Given step sets the literal "1", so the gate is open by construction; this scenario proves the AJ-023 record exists in
        // docs/build-log.md, which is what criterion 7 asks for, and does not query the GPU itself.
        EvaluateGate();
        Assert.Null(_world.GateSkipReason);
    }

    [Then(@"^docs/build-log\.md records ""supported"" or ""unsupported"" with the UTC date$")]
    public static void ThenTheBuildLogRecordsSupportedOrUnsupportedWithTheUtcDate()
    {
        string section = BuildLogSection(ProbeNotesHeading);

        // The sentence and a UTC timestamp (2026-10-19T10:42Z form) must sit on one line (one note), so the date belongs to the result.
        string? line = section.Split('\n').FirstOrDefault(candidate =>
            candidate.Contains(SupportedSentence, StringComparison.Ordinal) || candidate.Contains(UnavailableSentence, StringComparison.Ordinal));
        Assert.True(line is not null, $"'{ProbeNotesHeading}' contains neither '{SupportedSentence}' nor '{UnavailableSentence}'.");
        Assert.Matches(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?Z", line);
    }

    // AJ-029: the sampled meter over a scripted NVML session. FakeTimeProvider runs each due tick inline on the thread that calls
    // Advance (measured), so the read is normally counted before Advance returns; a step still moves the fake clock one timer period
    // at a time and yields (never waits on the wall clock) until the fake has counted that tick's read, in case a clock does not.
    private const int MeterMaxYields = 2_000_000;

    private SampledEnergyMeter Meter => _world.Meter ?? throw new InvalidOperationException("No meter was created.");

    private EnergyReading MeterReading(int index) =>
        index < _world.MeterReadings.Count ? _world.MeterReadings[index] : throw new InvalidOperationException($"Window {index} was not closed.");

    private async Task StartMeterAsync(string method, double watts, bool start = true, Action<ScriptedNvmlApi>? afterOpen = null)
    {
        OpenFakeSource(new ScriptedNvmlApi(_world.Time) { ConstantPowerWatts = watts, CounterSupported = true });
        afterOpen?.Invoke(_world.Api);
        IGpuTelemetrySource source = _world.Source ?? throw new InvalidOperationException("No GPU source was opened.");
        MeterCapabilities capabilities = MeterMethodSelector.Select(method, source, _world.ReadingSampleRateHz);
        _world.Capabilities = capabilities;
        _world.PowerReadBaseline = _world.Api.PowerReadCount;
        SampledEnergyMeter meter = new(source, capabilities, _world.Time);
        _world.Meter = meter;
        if (start)
        {
            await meter.StartAsync(CancellationToken.None);
        }
    }

    private async Task AdvanceMeterClockAsync(double seconds)
    {
        SampledEnergyMeter meter = Meter;
        double rateHz = (_world.Capabilities ?? throw new InvalidOperationException("No capabilities were selected.")).SampleRateHz;
        TimeSpan period = TimeSpan.FromSeconds(1.0 / rateHz);
        int ticks = (int)Math.Round(seconds * rateHz);
        for (int i = 0; i < ticks; i++)
        {
            int target = _world.Api.PowerCallCount + 1;
            _world.Time.Advance(period);
            int yields = 0;
            while (_world.Api.PowerCallCount < target)
            {
                Assert.True(yields++ < MeterMaxYields, "The sampling loop did not take its tick.");
                await Task.Yield();
            }

            // Reading the count takes the meter lock, which the loop holds until its sample is stored.
            _ = meter.SampleCount;
        }
    }

    private void OpenAndTrackWindow() => _world.Windows.Add(Meter.OpenWindow());

    private void CloseAndTrackWindow(IMeterWindow window) => _world.MeterReadings.Add(window.Close());

    [Given(@"^a started meter at 10 Hz over a fake NVML$")]
    public async Task GivenAStartedMeterAt10HzOverAFakeNvml() => await StartMeterAsync("auto", 60.0);

    [Given(@"^a started meter and a scripted constant 60 W$")]
    public async Task GivenAStartedMeterAndAScriptedConstant60W() => await StartMeterAsync("sampling", 60.0);

    [Given(@"^a started meter on the counter method$")]
    public async Task GivenAStartedMeterOnTheCounterMethod() => await StartMeterAsync("counter", 60.0);

    [Given(@"^a started meter$")]
    public async Task GivenAStartedMeter() => await StartMeterAsync("auto", 60.0);

    [Given(@"^windows opened at 0 s and 1 s on one meter$")]
    public async Task GivenWindowsOpenedAt0SAnd1SOnOneMeter()
    {
        await StartMeterAsync("auto", 60.0);
        OpenAndTrackWindow();
        await AdvanceMeterClockAsync(1.0);
        OpenAndTrackWindow();
    }

    [Given(@"^a meter that was not started$")]
    public async Task GivenAMeterThatWasNotStarted() => await StartMeterAsync("auto", 60.0, start: false);

    [Given(@"^a closed window$")]
    public async Task GivenAClosedWindow()
    {
        await StartMeterAsync("auto", 60.0);
        OpenAndTrackWindow();

        // 50 ms is half a 10 Hz period, so the window closes between ticks.
        _world.Time.Advance(TimeSpan.FromMilliseconds(50));
        CloseAndTrackWindow(_world.Windows[0]);
    }

    [Given(@"^the fake throws on the third power read$")]
    public async Task GivenTheFakeThrowsOnTheThirdPowerRead()
    {
        // Read 1 is the start sample and read 2 the opening edge of the window below, so read 3 is the first tick.
        await StartMeterAsync("auto", 60.0, afterOpen: api => api.ThrowOnPowerReadNumber = api.PowerCallCount + 3);
        OpenAndTrackWindow();
    }

    [When(@"^the (?:fake )?clock advances (\S+) s$")]
    public async Task WhenTheFakeClockAdvances(string seconds) => await AdvanceMeterClockAsync(ParseNumber(seconds));

    [When(@"^a window is opened, the clock advances 3 s and the window is closed$")]
    public async Task WhenAWindowIsOpenedTheClockAdvances3SAndTheWindowIsClosed()
    {
        OpenAndTrackWindow();
        await AdvanceMeterClockAsync(3.0);
        CloseAndTrackWindow(_world.Windows[0]);
    }

    [When(@"^a window spans 3 s at 60 W$")]
    public async Task WhenAWindowSpans3SAt60W()
    {
        OpenAndTrackWindow();
        await AdvanceMeterClockAsync(3.0);
        CloseAndTrackWindow(_world.Windows[0]);
    }

    [When(@"^both close at 3 s at a constant 60 W$")]
    public async Task WhenBothCloseAt3SAtAConstant60W()
    {
        await AdvanceMeterClockAsync(2.0);
        CloseAndTrackWindow(_world.Windows[0]);
        CloseAndTrackWindow(_world.Windows[1]);
    }

    [When(@"^OpenWindow is called$")]
    public void WhenOpenWindowIsCalled()
    {
        SampledEnergyMeter meter = Meter;
        _world.LastException = Record.Exception(() => meter.OpenWindow());
    }

    [When(@"^Close is called again$")]
    public void WhenCloseIsCalledAgain()
    {
        IMeterWindow window = _world.Windows[0];
        _world.LastException = Record.Exception(() => window.Close());
    }

    [When(@"^the meter is disposed and the clock advances 5 s$")]
    public async Task WhenTheMeterIsDisposedAndTheClockAdvances5S()
    {
        _world.PowerReadsAtDispose = _world.Api.PowerReadCount;
        await Meter.DisposeAsync();

        // Both in 0.1 s steps (one period each, as a running loop would see them) and in one 5 s step.
        for (int i = 0; i < 50; i++)
        {
            _world.Time.Advance(TimeSpan.FromMilliseconds(100));
        }

        _world.Time.Advance(TimeSpan.FromSeconds(5.0));
    }

    [Then(@"^the fake recorded exactly (\d+) power reads$")]
    public void ThenTheFakeRecordedExactlyPowerReads(int expected)
    {
        Assert.Equal(expected, _world.Api.PowerReadCount - _world.PowerReadBaseline);
    }

    [Then(@"^gross is 180 J within 1 percent and average is 60 W$")]
    public void ThenGrossIs180JWithin1PercentAndAverageIs60W()
    {
        EnergyReading reading = MeterReading(0);
        Assert.Equal(EnergyMethod.Sampling, reading.Method);
        Assert.Equal(MetricLabel.Measured, reading.GrossJoules.Label);
        Assert.Equal(180.0, reading.GrossJoules.Value ?? double.NaN, 1.8);
        AssertMeasured(reading.AverageWatts, 60.0, "W");
    }

    [Then(@"^gross is 180 J from the counter delta and sampleCount comes from the samples$")]
    public void ThenGrossIs180JFromTheCounterDeltaAndSampleCountComesFromTheSamples()
    {
        EnergyReading reading = MeterReading(0);
        Assert.Equal(EnergyMethod.Counter, reading.Method);
        AssertMeasured(reading.GrossJoules, 180.0, "J");

        // The last two successful counter reads are the open and close marks of the window; the fake adds a fixed offset to both.
        IReadOnlyList<ulong> counter = _world.Api.CounterReads;
        Assert.Equal(180000UL, counter[^1] - counter[^2]);

        // 3 s at 10 Hz is thirty ticks plus the start sample, whose slot the opening edge takes over, so 31 samples with both edges.
        Assert.Equal(31, reading.SampleCount);
    }

    [Then(@"^the first reports 180 J and the second reports 120 J$")]
    public void ThenTheFirstReports180JAndTheSecondReports120J()
    {
        AssertMeasured(MeterReading(0).GrossJoules, 180.0, "J");
        AssertMeasured(MeterReading(1).GrossJoules, 120.0, "J");
        Assert.Equal(31, MeterReading(0).SampleCount);
        Assert.Equal(21, MeterReading(1).SampleCount);
    }

    [Then(@"^an InvalidOperationException is thrown$")]
    public void ThenAnInvalidOperationExceptionIsThrown()
    {
        // Exactly this type: ObjectDisposedException derives from it and would mean the meter was disposed, not misused.
        Assert.IsType<InvalidOperationException>(_world.LastException);
    }

    [Then(@"^no sample with 0 W exists and the loop is still running$")]
    public void ThenNoSampleWith0WExistsAndTheLoopIsStillRunning()
    {
        // The start sample, the opening edge and ten ticks are twelve power calls; the third threw, so eleven returned a value.
        // Ticks 4 to 12 came after the failure, so the loop kept running past it.
        Assert.Equal(11, _world.Api.PowerReadCount - _world.PowerReadBaseline);
        Assert.Equal(1, Meter.SkippedReadCount);

        // A recorded 0 W would pull the time-weighted average below 60 W.
        EnergyReading reading = _world.Windows[0].Close();
        AssertMeasured(reading.AverageWatts, 60.0, "W");
        AssertMeasured(reading.PeakWatts, 60.0, "W");
        Assert.Equal(10, reading.SampleCount);
    }

    [Then(@"^the fake records no further power reads$")]
    public void ThenTheFakeRecordsNoFurtherPowerReads()
    {
        Assert.Equal(_world.PowerReadsAtDispose, _world.Api.PowerReadCount);
    }
}