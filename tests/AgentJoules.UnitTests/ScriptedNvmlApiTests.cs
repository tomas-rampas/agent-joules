using AgentJoules.Nvml;
using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-003")]
public sealed class ScriptedNvmlApiTests
{
    private const nint Device = ScriptedNvmlApi.FirstDeviceHandle;

    private static readonly string[] ReadMemberNames =
    [
        "GetHandleByIndex",
        "GetPowerUsage",
        "GetTotalEnergyConsumption",
        "GetClocksEventReasons",
        "GetMigMode",
        "GetCount",
        "GetName",
        "GetUuid",
        "GetVbios",
        "GetComputeCapability",
        "GetPowerLimit",
        "GetDriverVersion",
    ];

    public static TheoryData<string> ReadMembers
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string member in ReadMemberNames)
            {
                data.Add(member);
            }

            return data;
        }
    }

    public static TheoryData<string> AllMembers
    {
        get
        {
            TheoryData<string> data = ReadMembers;
            data.Add("Init");
            data.Add("Shutdown");
            return data;
        }
    }

    private static ScriptedNvmlApi CreateInitialised(FakeTimeProvider time, double watts = 50)
    {
        var api = new ScriptedNvmlApi(time) { ConstantPowerWatts = watts, CounterOffsetMillijoules = 0 };
        Assert.Equal(NvmlReturn.Success, api.Init());
        return api;
    }

    private static ulong ReadCounter(ScriptedNvmlApi api)
    {
        Assert.Equal(NvmlReturn.Success, api.GetTotalEnergyConsumption(Device, out ulong millijoules));
        return millijoules;
    }

    private static NvmlReturn InvokeAny(ScriptedNvmlApi api, string member) => member switch
    {
        "Init" => api.Init(),
        "Shutdown" => api.Shutdown(),
        "GetHandleByIndex" => api.GetHandleByIndex(0, out _),
        "GetPowerUsage" => api.GetPowerUsage(Device, out _),
        "GetTotalEnergyConsumption" => api.GetTotalEnergyConsumption(Device, out _),
        "GetClocksEventReasons" => api.GetClocksEventReasons(Device, out _),
        "GetMigMode" => api.GetMigMode(Device, out _, out _),
        "GetCount" => api.GetCount(out _),
        "GetName" => api.GetName(Device, out _),
        "GetUuid" => api.GetUuid(Device, out _),
        "GetVbios" => api.GetVbios(Device, out _),
        "GetComputeCapability" => api.GetComputeCapability(Device, out _, out _),
        "GetPowerLimit" => api.GetPowerLimit(Device, out _),
        "GetDriverVersion" => api.GetDriverVersion(out _),
        _ => throw new ArgumentOutOfRangeException(nameof(member)),
    };

    [Fact]
    public void INvmlApi_DeclaresExactlyTheFourteenMembersReturningNvmlReturn()
    {
        string[] expected =
        [
            "GetClocksEventReasons",
            "GetComputeCapability",
            "GetCount",
            "GetDriverVersion",
            "GetHandleByIndex",
            "GetMigMode",
            "GetName",
            "GetPowerLimit",
            "GetPowerUsage",
            "GetTotalEnergyConsumption",
            "GetUuid",
            "GetVbios",
            "Init",
            "Shutdown",
        ];

        var methods = typeof(INvmlApi).GetMethods();

        Assert.Equal(expected, methods.Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.All(methods, m => Assert.Equal(typeof(NvmlReturn), m.ReturnType));
        Assert.Empty(typeof(INvmlApi).GetProperties());
        Assert.Empty(typeof(INvmlApi).GetEvents());
    }

    [Fact]
    public void NvmlReturn_Values_MatchNvmlHeader()
    {
        Assert.Equal(0, (int)NvmlReturn.Success);
        Assert.Equal(1, (int)NvmlReturn.Uninitialized);
        Assert.Equal(2, (int)NvmlReturn.InvalidArgument);
        Assert.Equal(3, (int)NvmlReturn.NotSupported);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetPowerUsage_FiftyWattsAtOneSecondOfFakeTime_Returns50000MilliwattsWithSuccess()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time);

        time.Advance(TimeSpan.FromSeconds(1.0));
        NvmlReturn rc = api.GetPowerUsage(Device, out uint milliwatts);

        Assert.Equal(NvmlReturn.Success, rc);
        Assert.Equal(50_000u, milliwatts);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetPowerUsage_ConstantScript_ReturnsTheSameWattsAtAnyFakeTime()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time, 120);

        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out uint atStart));
        time.Advance(TimeSpan.FromSeconds(7.5));
        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out uint later));
        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out uint repeated));

        Assert.Equal(120_000u, atStart);
        Assert.Equal(120_000u, later);
        Assert.Equal(later, repeated);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetPowerUsage_NonConstantScript_FollowsFakeTime()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time);
        api.WattsAt = seconds => 10.0 + (20.0 * seconds);

        time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out uint first));
        time.Advance(TimeSpan.FromSeconds(1.0));
        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out uint second));

        Assert.Equal(20_000u, first);
        Assert.Equal(40_000u, second);
    }

    [Fact]
    public void GetPowerUsage_FractionalMilliwatt_RoundsAwayFromZero()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider(), 0.0625);

        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out uint milliwatts));

        Assert.Equal(63u, milliwatts);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-5.0)]
    [InlineData(double.NegativeInfinity)]
    public void GetPowerUsage_NanOrNegativeWatts_ClampToZero(double watts)
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider(), watts);

        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out uint milliwatts));

        Assert.Equal(0u, milliwatts);
    }

    [Fact]
    public void Reads_HandleOutsideTheSession_ReturnInvalidArgument()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());

        Assert.Equal(NvmlReturn.InvalidArgument, api.GetPowerUsage(0, out uint zeroHandle));
        Assert.Equal(NvmlReturn.InvalidArgument, api.GetPowerUsage(Device + 1, out _));
        Assert.Equal(NvmlReturn.InvalidArgument, api.GetName(Device + (1 << 16), out string name));

        Assert.Equal(0u, zeroHandle);
        Assert.Equal(string.Empty, name);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetTotalEnergyConsumption_TwoSecondsAtFiftyWatts_DifferenceIs100000Millijoules()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time);

        ulong first = ReadCounter(api);
        time.Advance(TimeSpan.FromSeconds(2.0));
        ulong second = ReadCounter(api);

        Assert.Equal(100_000UL, second - first);
        Assert.Equal([first, second], api.CounterReads);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetTotalEnergyConsumption_DefaultOffset_IsAddedToEveryValue()
    {
        var time = new FakeTimeProvider();
        var api = new ScriptedNvmlApi(time) { ConstantPowerWatts = 50 };
        Assert.Equal(NvmlReturn.Success, api.Init());

        ulong first = ReadCounter(api);
        time.Advance(TimeSpan.FromSeconds(2.0));
        ulong second = ReadCounter(api);

        Assert.Equal(123_456_789UL, first);
        Assert.Equal(123_556_789UL, second);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetTotalEnergyConsumption_CounterSwitchOff_ReturnsNotSupportedAndNoValue()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time);
        api.CounterSupported = false;
        time.Advance(TimeSpan.FromSeconds(1.0));

        NvmlReturn rc = api.GetTotalEnergyConsumption(Device, out ulong millijoules);

        Assert.Equal(NvmlReturn.NotSupported, rc);
        Assert.Equal(NvmlReturn.NotSupported, api.CounterReturn);
        Assert.False(api.CounterSupported);
        Assert.Equal(0UL, millijoules);
        Assert.Empty(api.CounterReads);
    }

    [Fact]
    public void GetTotalEnergyConsumption_ScriptedFailureCode_IsReturnedWithNoValue()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.CounterReturn = (NvmlReturn)999;

        NvmlReturn rc = api.GetTotalEnergyConsumption(Device, out ulong millijoules);

        Assert.Equal((NvmlReturn)999, rc);
        Assert.Equal(0UL, millijoules);
        Assert.Equal(1, api.CounterCallCount);
        Assert.Equal(0, api.CounterReadCount);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetTotalEnergyConsumption_ScriptChange_KeepsWhatTheOldScriptDrew()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time);

        time.Advance(TimeSpan.FromSeconds(1.0));
        ulong afterFirstSecond = ReadCounter(api);
        api.ConstantPowerWatts = 10;
        ulong rightAfterTheChange = ReadCounter(api);
        time.Advance(TimeSpan.FromSeconds(1.0));
        ulong afterSecondSecond = ReadCounter(api);

        Assert.Equal(50_000UL, afterFirstSecond);
        Assert.Equal(50_000UL, rightAfterTheChange);
        Assert.Equal(60_000UL, afterSecondSecond);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetTotalEnergyConsumption_ScriptChangeWithNoReadBefore_StillKeepsTheOldEnergy()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time);

        time.Advance(TimeSpan.FromSeconds(1.0));
        api.ConstantPowerWatts = 10;
        time.Advance(TimeSpan.FromSeconds(1.0));

        Assert.Equal(60_000UL, ReadCounter(api));
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetTotalEnergyConsumption_SeveralScriptChanges_NeverDecreases()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time, 100);
        var reads = new List<ulong>();

        time.Advance(TimeSpan.FromSeconds(0.3));
        reads.Add(ReadCounter(api));
        api.WattsAt = seconds => 50.0 + (50.0 * Math.Sin(seconds * 7.0));
        time.Advance(TimeSpan.FromSeconds(0.2005));
        reads.Add(ReadCounter(api));
        api.ConstantPowerWatts = 0;
        reads.Add(ReadCounter(api));
        time.Advance(TimeSpan.FromSeconds(1.0));
        reads.Add(ReadCounter(api));
        api.WattsAt = _ => 5.0;
        time.Advance(TimeSpan.FromSeconds(0.001));
        reads.Add(ReadCounter(api));
        api.ConstantPowerWatts = 200;
        time.Advance(TimeSpan.FromSeconds(0.5));
        reads.Add(ReadCounter(api));

        for (int i = 1; i < reads.Count; i++)
        {
            Assert.True(reads[i] >= reads[i - 1], $"read {i} ({reads[i]}) is below read {i - 1} ({reads[i - 1]})");
        }

        Assert.Equal(30_000UL, reads[0]);
        Assert.Equal(reads[1], reads[2]);
        Assert.Equal(reads[2], reads[3]);
        Assert.Equal(reads[4] + 100_000UL, reads[5]);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetTotalEnergyConsumption_BaselineThenLoad_DeltaIsTheEnergyOfTheWindow()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time, 20);

        time.Advance(TimeSpan.FromSeconds(30.0));
        ulong windowOpens = ReadCounter(api);
        time.Advance(TimeSpan.FromSeconds(1.0));
        api.ConstantPowerWatts = 60;
        time.Advance(TimeSpan.FromSeconds(2.0));
        ulong windowCloses = ReadCounter(api);

        Assert.Equal(140_000UL, windowCloses - windowOpens);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetTotalEnergyConsumption_RampScript_IntegratesByTrapezoid()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time);
        api.WattsAt = seconds => 10.0 * seconds;

        time.Advance(TimeSpan.FromSeconds(2.0));
        ulong atTwoSeconds = ReadCounter(api);
        time.Advance(TimeSpan.FromMilliseconds(500.5));
        ulong atTail = ReadCounter(api);

        Assert.InRange(atTwoSeconds, 19_999UL, 20_001UL);
        Assert.InRange(atTail, 31_262UL, 31_263UL);
        Assert.True(atTail >= atTwoSeconds);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetTotalEnergyConsumption_NonLinearScriptWithSubMillisecondTail_StaysWithinOneMillijoule()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time);
        api.WattsAt = seconds => 100.0 * seconds * seconds;

        time.Advance(TimeSpan.FromMilliseconds(750.25));
        ulong actual = ReadCounter(api);

        double elapsedSeconds = 0.75025;
        double expected = 1000.0 * 100.0 * elapsedSeconds * elapsedSeconds * elapsedSeconds / 3.0;
        Assert.InRange((double)actual, expected - 1.0, expected + 1.0);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetTotalEnergyConsumption_RampScript_IncrementalReadsMatchASingleRead()
    {
        var incrementalTime = new FakeTimeProvider();
        var singleTime = new FakeTimeProvider();
        ScriptedNvmlApi incremental = CreateInitialised(incrementalTime);
        ScriptedNvmlApi single = CreateInitialised(singleTime);
        incremental.WattsAt = seconds => 30.0 + (5.0 * seconds);
        single.WattsAt = seconds => 30.0 + (5.0 * seconds);

        for (int i = 0; i < 4; i++)
        {
            incrementalTime.Advance(TimeSpan.FromSeconds(0.75));
            ReadCounter(incremental);
        }

        singleTime.Advance(TimeSpan.FromSeconds(3.0));
        ulong once = ReadCounter(single);

        Assert.Equal(once, incremental.CounterReads[^1]);
    }

    [Fact]
    [Trait("Spec", "REQ-004")]
    public void GetTotalEnergyConsumption_SubMillisecondTails_FourReadsMatchOneRead()
    {
        var fourTime = new FakeTimeProvider();
        var oneTime = new FakeTimeProvider();
        ScriptedNvmlApi four = CreateInitialised(fourTime, 0);
        ScriptedNvmlApi one = CreateInitialised(oneTime, 0);
        fourTime.Advance(TimeSpan.FromMilliseconds(0.25));
        oneTime.Advance(TimeSpan.FromMilliseconds(0.25));
        four.WattsAt = seconds => 100.0 * seconds * seconds;
        one.WattsAt = seconds => 100.0 * seconds * seconds;

        for (int i = 0; i < 4; i++)
        {
            fourTime.Advance(TimeSpan.FromMilliseconds(750.25));
            ReadCounter(four);
        }

        oneTime.Advance(TimeSpan.FromMilliseconds(4 * 750.25));
        ulong single = ReadCounter(one);

        Assert.InRange((double)four.CounterReads[^1], single - 1.0, single + 1.0);
    }

    [Fact]
    public void ReadCounts_CountCallsThatPassedTheSessionChecks()
    {
        ScriptedNvmlApi api = new(new FakeTimeProvider()) { ConstantPowerWatts = 50 };
        api.GetPowerUsage(Device, out _);
        api.GetTotalEnergyConsumption(Device, out _);
        Assert.Equal(0, api.PowerCallCount);
        Assert.Equal(0, api.CounterCallCount);
        Assert.Equal(NvmlReturn.Success, api.Init());

        api.GetPowerUsage(Device, out _);
        api.GetPowerUsage(Device, out _);
        api.GetTotalEnergyConsumption(Device, out _);
        api.CounterSupported = false;
        api.GetTotalEnergyConsumption(Device, out _);

        Assert.Equal(2, api.PowerCallCount);
        Assert.Equal(2, api.PowerReadCount);
        Assert.Equal(2, api.CounterCallCount);
        Assert.Equal(1, api.CounterReadCount);
        Assert.Single(api.CounterReads);
    }

    [Fact]
    public void GetPowerUsage_ScriptedReturnForRead_FailsOnlyThatCall()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.PowerReturnForRead = call => call == 2 ? (NvmlReturn)999 : NvmlReturn.Success;

        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out _));
        Assert.Equal((NvmlReturn)999, api.GetPowerUsage(Device, out uint failed));
        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out _));

        Assert.Equal(0u, failed);
        Assert.Equal(3, api.PowerCallCount);
        Assert.Equal(2, api.PowerReadCount);
    }

    [Fact]
    public void GetTotalEnergyConsumption_ScriptedReturnForRead_FailsOnlyThatCall()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.CounterReturnForRead = call => call == 1 ? NvmlReturn.InvalidArgument : NvmlReturn.Success;

        Assert.Equal(NvmlReturn.InvalidArgument, api.GetTotalEnergyConsumption(Device, out ulong failed));
        Assert.Equal(NvmlReturn.Success, api.GetTotalEnergyConsumption(Device, out _));

        Assert.Equal(0UL, failed);
        Assert.Equal(2, api.CounterCallCount);
        Assert.Equal(1, api.CounterReadCount);
    }

    [Fact]
    public void GetPowerUsage_ThrowOnPowerReadNumber_ThrowsOnThatCallOnly()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.ThrowOnPowerReadNumber = 2;

        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out _));
        var thrown = Assert.Throws<InvalidOperationException>(() => api.GetPowerUsage(Device, out _));
        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out uint afterwards));

        Assert.Equal("scripted power read failure", thrown.Message);
        Assert.Equal(50_000u, afterwards);
        Assert.Equal(3, api.PowerCallCount);
        Assert.Equal(2, api.PowerReadCount);
    }

    [Theory]
    [MemberData(nameof(AllMembers))]
    public void AllMembers_MissingLibraryMode_ThrowDllNotFoundException(string member)
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider()) { MissingLibrary = true };

        Assert.Throws<DllNotFoundException>(() => InvokeAny(api, member));
        Assert.False(api.IsInitialised);
    }

    [Fact]
    public void FaultFor_ScriptedForOneMember_IsThrownFromThatMemberOnly()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.FaultFor = member => member switch
        {
            "GetName" => new BadImageFormatException("scripted"),
            "GetClocksEventReasons" => new BadImageFormatException("scripted"),
            _ => null,
        };

        Assert.Throws<BadImageFormatException>(() => api.GetName(Device, out _));
        Assert.Throws<BadImageFormatException>(() => api.GetClocksEventReasons(Device, out _));
        Assert.Equal(NvmlReturn.Success, api.GetUuid(Device, out string uuid));
        Assert.False(string.IsNullOrEmpty(uuid));
    }

    [Fact]
    public void FaultFor_OnAnUninitialisedFake_FiresBeforeTheSessionCheck()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider())
        {
            FaultFor = _ => new EntryPointNotFoundException("scripted"),
        };

        Assert.Throws<EntryPointNotFoundException>(() => api.GetPowerUsage(Device, out _));
        Assert.False(api.IsInitialised);
    }

    [Fact]
    public void MissingLibrary_WinsOverFaultFor()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider())
        {
            MissingLibrary = true,
            FaultFor = _ => new BadImageFormatException("scripted"),
        };

        Assert.Throws<DllNotFoundException>(() => api.GetName(Device, out _));
    }

    [Theory]
    [MemberData(nameof(ReadMembers))]
    public void ReadMembers_BeforeInit_ReturnUninitialized(string member)
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider()) { ConstantPowerWatts = 50 };

        Assert.Equal(NvmlReturn.Uninitialized, InvokeAny(api, member));
    }

    [Theory]
    [MemberData(nameof(ReadMembers))]
    public void ReadMembers_AfterShutdown_ReturnUninitialized(string member)
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        Assert.Equal(NvmlReturn.Success, api.Shutdown());

        Assert.Equal(NvmlReturn.Uninitialized, InvokeAny(api, member));
    }

    [Fact]
    public void GetPowerUsage_BeforeInit_ProducesNoValue()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider()) { ConstantPowerWatts = 50 };

        NvmlReturn rc = api.GetPowerUsage(Device, out uint milliwatts);

        Assert.Equal(NvmlReturn.Uninitialized, rc);
        Assert.Equal(0u, milliwatts);
    }

    [Fact]
    public void InitAndShutdown_AreReferenceCounted()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider());
        Assert.Equal(NvmlReturn.Success, api.Init());
        Assert.Equal(NvmlReturn.Success, api.Init());

        Assert.Equal(NvmlReturn.Success, api.Shutdown());
        Assert.True(api.IsInitialised);
        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(Device, out _));

        Assert.Equal(NvmlReturn.Success, api.Shutdown());
        Assert.False(api.IsInitialised);
        Assert.Equal(NvmlReturn.Uninitialized, api.Shutdown());
        Assert.Equal(NvmlReturn.Uninitialized, api.GetPowerUsage(Device, out _));
    }

    [Fact]
    public void Handles_BelongToTheSessionThatProducedThem()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider());
        Assert.Equal(NvmlReturn.Success, api.Init());
        Assert.Equal(NvmlReturn.Success, api.GetHandleByIndex(0, out nint firstSession));
        Assert.Equal(NvmlReturn.Success, api.Shutdown());
        Assert.Equal(NvmlReturn.Success, api.Init());

        NvmlReturn stale = api.GetPowerUsage(firstSession, out _);
        Assert.Equal(NvmlReturn.Success, api.GetHandleByIndex(0, out nint secondSession));

        Assert.Equal(Device, firstSession);
        Assert.Equal(NvmlReturn.InvalidArgument, stale);
        Assert.NotEqual(firstSession, secondSession);
        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(secondSession, out _));
    }

    [Fact]
    public void GetClocksEventReasons_ScriptedBits_AreReturned()
    {
        const ulong swPowerCap = 0x4;
        const ulong hwSlowdown = 0x8;
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.ClockEventReasons = swPowerCap | hwSlowdown;

        NvmlReturn rc = api.GetClocksEventReasons(Device, out ulong reasons);

        Assert.Equal(NvmlReturn.Success, rc);
        Assert.Equal(swPowerCap | hwSlowdown, reasons);
    }

    [Fact]
    public void GetClocksEventReasons_ScriptedFunctionOfTime_FollowsFakeTime()
    {
        var time = new FakeTimeProvider();
        ScriptedNvmlApi api = CreateInitialised(time);
        api.ClockEventReasonsAt = seconds => seconds >= 1.0 ? 0x4UL : 0UL;

        Assert.Equal(NvmlReturn.Success, api.GetClocksEventReasons(Device, out ulong before));
        time.Advance(TimeSpan.FromSeconds(1.0));
        Assert.Equal(NvmlReturn.Success, api.GetClocksEventReasons(Device, out ulong after));

        Assert.Equal(0UL, before);
        Assert.Equal(0x4UL, after);
    }

    [Fact]
    public void GetClocksEventReasons_ScriptedNotSupported_ReturnsRc3WithNoValue()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.ClockEventReasons = 0x4;
        api.ClockEventReturn = NvmlReturn.NotSupported;

        NvmlReturn rc = api.GetClocksEventReasons(Device, out ulong reasons);

        Assert.Equal(NvmlReturn.NotSupported, rc);
        Assert.Equal(0UL, reasons);
    }

    [Fact]
    public void GetMigMode_ScriptedNotSupported_ReturnsRc3WithNoValues()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.MigModeReturn = NvmlReturn.NotSupported;

        NvmlReturn rc = api.GetMigMode(Device, out uint current, out uint pending);

        Assert.Equal(NvmlReturn.NotSupported, rc);
        Assert.Equal(3, (int)rc);
        Assert.Equal(0u, current);
        Assert.Equal(0u, pending);
    }

    [Fact]
    public void GetMigMode_ScriptedSuccess_ReturnsScriptedModes()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.MigModeCurrent = 1;
        api.MigModePending = 0;

        NvmlReturn rc = api.GetMigMode(Device, out uint current, out uint pending);

        Assert.Equal(NvmlReturn.Success, rc);
        Assert.Equal(1u, current);
        Assert.Equal(0u, pending);
    }

    [Fact]
    public void IdentityMembers_ReturnScriptedValues()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.Uuid = "GPU-11111111-2222-3333-4444-555555555555";

        Assert.Equal(NvmlReturn.Success, api.GetCount(out uint count));
        Assert.Equal(NvmlReturn.Success, api.GetName(Device, out string name));
        Assert.Equal(NvmlReturn.Success, api.GetUuid(Device, out string uuid));
        Assert.Equal(NvmlReturn.Success, api.GetVbios(Device, out string vbios));
        Assert.Equal(NvmlReturn.Success, api.GetComputeCapability(Device, out int major, out int minor));
        Assert.Equal(NvmlReturn.Success, api.GetPowerLimit(Device, out uint limit));
        Assert.Equal(NvmlReturn.Success, api.GetDriverVersion(out string driver));

        Assert.Equal(1u, count);
        Assert.Equal("Scripted GPU 3080", name);
        Assert.Equal("GPU-11111111-2222-3333-4444-555555555555", uuid);
        Assert.False(string.IsNullOrEmpty(vbios));
        Assert.Equal((8, 6), (major, minor));
        Assert.Equal(85059u, limit);
        Assert.Equal("616.92", driver);
    }

    [Fact]
    public void GetVbiosAndComputeCapability_ScriptedAbsent_ReturnNotSupported()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.Vbios = null;
        api.ComputeCapability = null;

        Assert.Equal(NvmlReturn.NotSupported, api.GetVbios(Device, out string vbios));
        Assert.Equal(NvmlReturn.NotSupported, api.GetComputeCapability(Device, out int major, out int minor));

        Assert.Equal(string.Empty, vbios);
        Assert.Equal((0, 0), (major, minor));
    }

    [Fact]
    public void GetHandleByIndex_IndexAtOrAboveDeviceCount_ReturnsInvalidArgument()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        api.DeviceCount = 2;

        Assert.Equal(NvmlReturn.Success, api.GetHandleByIndex(1, out nint second));
        Assert.Equal(NvmlReturn.InvalidArgument, api.GetHandleByIndex(2, out nint missing));

        Assert.Equal(Device + 1, second);
        Assert.Equal(0, missing);
    }

    [Fact]
    public void Init_ScriptedFailureCode_IsReturnedWithoutInitialising()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider()) { InitReturn = NvmlReturn.InvalidArgument };

        Assert.Equal(NvmlReturn.InvalidArgument, api.Init());

        Assert.False(api.IsInitialised);
        Assert.Equal(NvmlReturn.Uninitialized, api.GetPowerUsage(Device, out _));
        Assert.Equal(1, api.InitCallCount);
    }

    [Fact]
    public void InitAndShutdown_CallCounts_IncludeEveryCall()
    {
        var api = new ScriptedNvmlApi(new FakeTimeProvider());

        Assert.Equal(NvmlReturn.Uninitialized, api.Shutdown());
        Assert.Equal(NvmlReturn.Success, api.Init());
        Assert.Equal(NvmlReturn.Success, api.Shutdown());

        Assert.Equal(1, api.InitCallCount);
        Assert.Equal(2, api.ShutdownCallCount);
    }

    [Fact]
    public void MaxConcurrentReads_SequentialCalls_StaysAtOne()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());

        api.GetPowerUsage(Device, out _);
        api.GetTotalEnergyConsumption(Device, out _);
        api.GetName(Device, out _);

        Assert.Equal(1, api.MaxConcurrentReads);
    }

    [Fact]
    public async Task MaxConcurrentReads_OverlappingCalls_IsObserved()
    {
        ScriptedNvmlApi api = CreateInitialised(new FakeTimeProvider());
        using var barrier = new Barrier(2);
        api.FaultFor = _ =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            return null;
        };

        await Task.WhenAll(
            Task.Run(() => api.GetPowerUsage(Device, out _)),
            Task.Run(() => api.GetPowerUsage(Device, out _)));

        Assert.Equal(2, api.MaxConcurrentReads);
    }
}