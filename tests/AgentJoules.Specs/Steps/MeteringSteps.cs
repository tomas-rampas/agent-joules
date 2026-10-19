using System.Globalization;
using System.Runtime.InteropServices;

using AgentJoules.Nvml.Interop;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

[Binding]
public sealed class MeteringSteps(ScenarioWorld world)
{
    private const nint SecondCandidateHandle = 0x2000;

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
}