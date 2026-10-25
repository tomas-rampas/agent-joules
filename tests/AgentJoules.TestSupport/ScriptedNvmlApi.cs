using AgentJoules.Nvml;
using AgentJoules.Nvml.Interop;

using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.TestSupport;

/// <summary>
/// A scripted <see cref="INvmlApi"/> for tests that run with no GPU, no driver and no wall-clock waiting.
/// Power and the clock-event bits are functions of <see cref="FakeTimeProvider"/> time, measured from the
/// moment the instance is constructed. The energy counter is the integral of the power that was actually
/// scripted over time: replacing the script folds the old script's integral up to now into a base, so the
/// counter never decreases and never re-reads the past through a new script. Like NVML, Init and Shutdown are
/// reference counted, every read returns <see cref="NvmlReturn.Uninitialized"/> outside a session, and device
/// handles belong to the session that produced them. Members may be configured from the test thread while a
/// sampler reads; counters are guarded by a lock.
/// </summary>
public sealed class ScriptedNvmlApi : INvmlApi
{
    /// <summary>
    /// The handle <see cref="GetHandleByIndex"/> returns for index 0 in the first session. A handle is
    /// <c>(generation &lt;&lt; 16) | (index + 1)</c>; the generation is 1 in the first session and grows by one
    /// each time a new session starts.
    /// </summary>
    public const nint FirstDeviceHandle = (1 << 16) | 1;

    private readonly FakeTimeProvider _time;
    private readonly DateTimeOffset _origin;
    private readonly Lock _gate = new();
    private readonly List<ulong> _counterReads = [];

    private Func<double, double> _wattsAt = static _ => 0.0;
    private double? _constantWatts = 0.0;
    private double _segmentBaseMj;
    private double _segmentStartMs;
    private long _gridCount;
    private double _gridSum;
    private double _lastTotalMj;
    private int _initRefCount;
    private int _generation;
    private int _powerCallCount;
    private int _powerReadCount;
    private int _counterCallCount;
    private int _initCallCount;
    private int _shutdownCallCount;
    private int _readsInFlight;
    private int _maxConcurrentReads;

    public ScriptedNvmlApi(FakeTimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _origin = time.GetUtcNow();
    }

    /// <summary>Fake time elapsed since this instance was constructed.</summary>
    public TimeSpan Elapsed => _time.GetUtcNow() - _origin;

    /// <summary>True while the Init count exceeds the Shutdown count.</summary>
    public bool IsInitialised
    {
        get
        {
            lock (_gate)
            {
                return _initRefCount > 0;
            }
        }
    }

    /// <summary>
    /// Power in watts as a function of seconds since construction. Assigning it closes the current segment of
    /// the counter at the present fake time and starts a new one, so the counter keeps what the old script
    /// already drew. A non-constant script is integrated by trapezoid over a 1 ms grid counted from the
    /// start of its segment, with the sub-millisecond tail closed by one more trapezoid.
    /// A step script loses up to half the step's watt difference in millijoules per change at a whole-millisecond
    /// boundary (the trapezoid averages the two sides), so a test that needs exact counters should switch power
    /// through <see cref="ConstantPowerWatts"/>, which is segmented and exact.
    /// </summary>
    public Func<double, double> WattsAt
    {
        get
        {
            lock (_gate)
            {
                return _wattsAt;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                StartNewSegment();
                _wattsAt = value;
                _constantWatts = null;
            }
        }
    }

    /// <summary>
    /// Convenience over <see cref="WattsAt"/>: setting it scripts a constant draw from now on, for which the
    /// segment's counter is exactly watts times elapsed milliseconds. Reading it returns the scripted power at
    /// the current fake time.
    /// </summary>
    public double ConstantPowerWatts
    {
        get => WattsAt(Elapsed.TotalSeconds);
        set
        {
            lock (_gate)
            {
                StartNewSegment();
                _wattsAt = _ => value;
                _constantWatts = value;
            }
        }
    }

    /// <summary>
    /// Shorthand over <see cref="CounterReturn"/>: false sets it to <see cref="NvmlReturn.NotSupported"/>,
    /// true to <see cref="NvmlReturn.Success"/>; reads true unless the code is NotSupported.
    /// </summary>
    public bool CounterSupported
    {
        get => CounterReturn != NvmlReturn.NotSupported;
        set => CounterReturn = value ? NvmlReturn.Success : NvmlReturn.NotSupported;
    }

    /// <summary>The return code of every counter read; anything but Success produces no value.</summary>
    public NvmlReturn CounterReturn { get; set; } = NvmlReturn.Success;

    /// <summary>
    /// Maps the 1-based number of a counter call (see <see cref="CounterCallCount"/>) to a return code; a code
    /// other than Success produces no value and is not a successful read. A non-Success result takes
    /// precedence; Success defers to <see cref="CounterReturn"/>.
    /// </summary>
    public Func<int, NvmlReturn>? CounterReturnForRead { get; set; }

    /// <summary>
    /// Maps the 1-based number of a power call (see <see cref="PowerCallCount"/>) to a return code; a code other
    /// than Success produces no value and is not a successful read. A non-Success result takes precedence;
    /// Success defers to the normal scripted read.
    /// </summary>
    public Func<int, NvmlReturn>? PowerReturnForRead { get; set; }

    /// <summary>
    /// Added to every counter value: the real counter counts since driver load, so a consumer that reports an
    /// absolute value instead of a delta must fail a test. The default is 123456789.
    /// </summary>
    public ulong CounterOffsetMillijoules { get; set; } = 123456789;

    /// <summary>When true, every member throws <see cref="DllNotFoundException"/>, as a missing nvml library does.</summary>
    public bool MissingLibrary { get; set; }

    /// <summary>
    /// Called with the member name (for example "GetName") right after the missing-library check; a non-null
    /// result is thrown. It scripts the other exceptions the real binding may raise, such as
    /// <see cref="EntryPointNotFoundException"/> and <see cref="BadImageFormatException"/>, from any call.
    /// </summary>
    public Func<string, Exception?>? FaultFor { get; set; }

    /// <summary>The recorder that is told <see cref="OperationRecorder.Probe"/> each time a new session starts (the count of <see cref="Init"/> goes from 0 to 1), or null for none (AJ-106).</summary>
    public OperationRecorder? Recorder { get; set; }

    /// <summary>The return code <see cref="Init"/> reports; anything but Success starts no session.</summary>
    public NvmlReturn InitReturn { get; set; } = NvmlReturn.Success;

    /// <summary>Clock-event bits as a function of seconds since construction.</summary>
    public Func<double, ulong> ClockEventReasonsAt { get; set; } = static _ => 0UL;

    /// <summary>
    /// Convenience over <see cref="ClockEventReasonsAt"/>: setting it scripts constant bits. Reading it
    /// returns the scripted bits at the current fake time.
    /// </summary>
    public ulong ClockEventReasons
    {
        get => ClockEventReasonsAt(Elapsed.TotalSeconds);
        set => ClockEventReasonsAt = _ => value;
    }

    /// <summary>The return code of <see cref="GetClocksEventReasons"/>; NotSupported scripts "throttle API unsupported".</summary>
    public NvmlReturn ClockEventReturn { get; set; } = NvmlReturn.Success;

    /// <summary>The return code of <see cref="GetMigMode"/>; rc 3 was measured on the laptop.</summary>
    public NvmlReturn MigModeReturn { get; set; } = NvmlReturn.Success;

    public uint MigModeCurrent { get; set; }

    public uint MigModePending { get; set; }

    /// <summary>
    /// The 1-based number of the power call that throws <see cref="InvalidOperationException"/>; null never throws.
    /// This is a source-level fault outside the contract of the real binding (see <see cref="INvmlApi"/>); use
    /// <see cref="FaultFor"/> for the exceptions the real binding may raise.
    /// </summary>
    public int? ThrowOnPowerReadNumber { get; set; }

    public string Name { get; set; } = "Scripted GPU 3080";

    public string Uuid { get; set; } = "GPU-00000000-0000-0000-0000-000000000000";

    public string DriverVersion { get; set; } = "616.92";

    /// <summary>The VBIOS version; null makes <see cref="GetVbios"/> return <see cref="NvmlReturn.NotSupported"/>.</summary>
    public string? Vbios { get; set; } = "94.02.5C.00.01";

    /// <summary>The compute capability; null makes <see cref="GetComputeCapability"/> return <see cref="NvmlReturn.NotSupported"/>.</summary>
    public (int Major, int Minor)? ComputeCapability { get; set; } = (8, 6);

    public uint PowerLimitMilliwatts { get; set; } = 85059;

    public uint DeviceCount { get; set; } = 1;

    /// <summary>Number of power calls that passed the session and handle checks, whatever they returned or threw.</summary>
    public int PowerCallCount => Locked(() => _powerCallCount);

    /// <summary>Number of power calls that returned a value.</summary>
    public int PowerReadCount => Locked(() => _powerReadCount);

    /// <summary>Number of counter calls that passed the session and handle checks, whatever they returned.</summary>
    public int CounterCallCount => Locked(() => _counterCallCount);

    /// <summary>Number of counter calls that returned a value.</summary>
    public int CounterReadCount => Locked(() => _counterReads.Count);

    /// <summary>A snapshot of the values the counter reads returned (offset included), in order.</summary>
    public IReadOnlyList<ulong> CounterReads => Locked(() => (IReadOnlyList<ulong>)[.. _counterReads]);

    /// <summary>Number of <see cref="Init"/> calls, including ones that threw or failed.</summary>
    public int InitCallCount => Locked(() => _initCallCount);

    /// <summary>Number of <see cref="Shutdown"/> calls, including ones that threw or failed.</summary>
    public int ShutdownCallCount => Locked(() => _shutdownCallCount);

    /// <summary>The highest number of member calls that were in flight at the same moment; 1 means serialised.</summary>
    public int MaxConcurrentReads => Volatile.Read(ref _maxConcurrentReads);

    public NvmlReturn Init()
    {
        Locked(() => ++_initCallCount);
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(Init));
        lock (_gate)
        {
            if (InitReturn != NvmlReturn.Success)
            {
                return InitReturn;
            }

            if (_initRefCount++ == 0)
            {
                _generation++;
                Recorder?.Record(OperationRecorder.Probe);
            }

            return NvmlReturn.Success;
        }
    }

    public NvmlReturn Shutdown()
    {
        Locked(() => ++_shutdownCallCount);
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(Shutdown));
        lock (_gate)
        {
            if (_initRefCount == 0)
            {
                return NvmlReturn.Uninitialized;
            }

            _initRefCount--;
            return NvmlReturn.Success;
        }
    }

    public NvmlReturn GetHandleByIndex(uint index, out nint device)
    {
        device = 0;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetHandleByIndex));
        lock (_gate)
        {
            if (_initRefCount == 0)
            {
                return NvmlReturn.Uninitialized;
            }

            if (index >= DeviceCount)
            {
                return NvmlReturn.InvalidArgument;
            }

            device = (nint)((_generation << 16) | (int)(index + 1));
            return NvmlReturn.Success;
        }
    }

    public NvmlReturn GetPowerUsage(nint device, out uint milliwatts)
    {
        milliwatts = 0;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetPowerUsage));
        NvmlReturn session = CheckSession(device);
        if (session != NvmlReturn.Success)
        {
            return session;
        }

        int call = Locked(() => ++_powerCallCount);
        if (ThrowOnPowerReadNumber == call)
        {
            throw new InvalidOperationException("scripted power read failure");
        }

        NvmlReturn scripted = PowerReturnForRead?.Invoke(call) ?? NvmlReturn.Success;
        if (scripted != NvmlReturn.Success)
        {
            return scripted;
        }

        double watts = ClampWatts(WattsAt(Elapsed.TotalSeconds));
        milliwatts = (uint)Math.Min(Math.Round(watts * 1000.0, MidpointRounding.AwayFromZero), uint.MaxValue);
        Locked(() => ++_powerReadCount);
        return NvmlReturn.Success;
    }

    public NvmlReturn GetTotalEnergyConsumption(nint device, out ulong millijoules)
    {
        millijoules = 0;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetTotalEnergyConsumption));
        NvmlReturn session = CheckSession(device);
        if (session != NvmlReturn.Success)
        {
            return session;
        }

        lock (_gate)
        {
            int call = ++_counterCallCount;
            NvmlReturn scripted = CounterReturnForRead?.Invoke(call) ?? NvmlReturn.Success;
            if (scripted != NvmlReturn.Success)
            {
                return scripted;
            }

            if (CounterReturn != NvmlReturn.Success)
            {
                return CounterReturn;
            }

            millijoules = (ulong)Math.Round(TotalMillijoules(ElapsedMilliseconds()), MidpointRounding.AwayFromZero)
                + CounterOffsetMillijoules;
            _counterReads.Add(millijoules);
            return NvmlReturn.Success;
        }
    }

    public NvmlReturn GetClocksEventReasons(nint device, out ulong reasons)
    {
        reasons = 0;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetClocksEventReasons));
        NvmlReturn session = CheckSession(device);
        if (session != NvmlReturn.Success)
        {
            return session;
        }

        if (ClockEventReturn != NvmlReturn.Success)
        {
            return ClockEventReturn;
        }

        reasons = ClockEventReasonsAt(Elapsed.TotalSeconds);
        return NvmlReturn.Success;
    }

    public NvmlReturn GetMigMode(nint device, out uint current, out uint pending)
    {
        current = 0;
        pending = 0;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetMigMode));
        NvmlReturn session = CheckSession(device);
        if (session != NvmlReturn.Success)
        {
            return session;
        }

        if (MigModeReturn != NvmlReturn.Success)
        {
            return MigModeReturn;
        }

        current = MigModeCurrent;
        pending = MigModePending;
        return NvmlReturn.Success;
    }

    public NvmlReturn GetCount(out uint count)
    {
        count = 0;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetCount));
        if (!IsInitialised)
        {
            return NvmlReturn.Uninitialized;
        }

        count = DeviceCount;
        return NvmlReturn.Success;
    }

    public NvmlReturn GetName(nint device, out string name)
    {
        name = string.Empty;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetName));
        NvmlReturn session = CheckSession(device);
        if (session != NvmlReturn.Success)
        {
            return session;
        }

        name = Name;
        return NvmlReturn.Success;
    }

    public NvmlReturn GetUuid(nint device, out string uuid)
    {
        uuid = string.Empty;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetUuid));
        NvmlReturn session = CheckSession(device);
        if (session != NvmlReturn.Success)
        {
            return session;
        }

        uuid = Uuid;
        return NvmlReturn.Success;
    }

    public NvmlReturn GetVbios(nint device, out string vbios)
    {
        vbios = string.Empty;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetVbios));
        NvmlReturn session = CheckSession(device);
        if (session != NvmlReturn.Success)
        {
            return session;
        }

        if (Vbios is null)
        {
            return NvmlReturn.NotSupported;
        }

        vbios = Vbios;
        return NvmlReturn.Success;
    }

    public NvmlReturn GetComputeCapability(nint device, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetComputeCapability));
        NvmlReturn session = CheckSession(device);
        if (session != NvmlReturn.Success)
        {
            return session;
        }

        if (ComputeCapability is not { } capability)
        {
            return NvmlReturn.NotSupported;
        }

        major = capability.Major;
        minor = capability.Minor;
        return NvmlReturn.Success;
    }

    public NvmlReturn GetPowerLimit(nint device, out uint milliwatts)
    {
        milliwatts = 0;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetPowerLimit));
        NvmlReturn session = CheckSession(device);
        if (session != NvmlReturn.Success)
        {
            return session;
        }

        milliwatts = PowerLimitMilliwatts;
        return NvmlReturn.Success;
    }

    public NvmlReturn GetDriverVersion(out string version)
    {
        version = string.Empty;
        ThrowIfLibraryMissing();
        using ReadScope scope = EnterCall();
        ThrowScriptedFault(nameof(GetDriverVersion));
        if (!IsInitialised)
        {
            return NvmlReturn.Uninitialized;
        }

        version = DriverVersion;
        return NvmlReturn.Success;
    }

    private static double ClampWatts(double watts) => double.IsFinite(watts) ? Math.Max(0.0, watts) : 0.0;

    private T Locked<T>(Func<T> action)
    {
        lock (_gate)
        {
            return action();
        }
    }

    private void ThrowIfLibraryMissing()
    {
        if (MissingLibrary)
        {
            throw new DllNotFoundException("scripted missing NVML library");
        }
    }

    private void ThrowScriptedFault(string member)
    {
        Exception? fault = FaultFor?.Invoke(member);
        if (fault is not null)
        {
            throw fault;
        }
    }

    private ReadScope EnterCall()
    {
        int inFlight = Interlocked.Increment(ref _readsInFlight);
        int seen;
        do
        {
            seen = Volatile.Read(ref _maxConcurrentReads);
        }
        while (seen < inFlight && Interlocked.CompareExchange(ref _maxConcurrentReads, inFlight, seen) != seen);

        return new ReadScope(this);
    }

    private NvmlReturn CheckSession(nint device)
    {
        lock (_gate)
        {
            if (_initRefCount == 0)
            {
                return NvmlReturn.Uninitialized;
            }

            long generation = device >> 16;
            long slot = device & 0xFFFF;
            return generation == _generation && slot >= 1 && slot <= DeviceCount
                ? NvmlReturn.Success
                : NvmlReturn.InvalidArgument;
        }
    }

    private double ElapsedMilliseconds() => Math.Max(0.0, Elapsed.TotalMilliseconds);

    // Folds everything the old script drew up to now into the base and starts a new segment at the present
    // fake time, so the counter total is continuous across a script change. The caller holds _gate.
    private void StartNewSegment()
    {
        double now = ElapsedMilliseconds();
        _segmentBaseMj = TotalMillijoules(now);
        _segmentStartMs = now;
        _gridCount = 0;
        _gridSum = 0.0;
    }

    // Millijoules since construction: the base plus the current script's integral over [segment start, now],
    // never below a total that was already reported. The caller holds _gate.
    private double TotalMillijoules(double nowMs)
    {
        double total = Math.Max(_lastTotalMj, _segmentBaseMj + SegmentIntegral(nowMs));
        _lastTotalMj = total;
        return total;
    }

    // 1 W for 1 ms is 1 mJ. A constant is exactly watts x milliseconds; otherwise a trapezoid sum over a 1 ms
    // grid from the segment start, with the sub-millisecond tail closed by one more trapezoid. The sum up to
    // the last whole millisecond is cached, so a long run is not re-integrated on every read.
    private double SegmentIntegral(double nowMs)
    {
        double spanMs = Math.Max(0.0, nowMs - _segmentStartMs);
        if (_constantWatts is { } constant)
        {
            return ClampWatts(constant) * spanMs;
        }

        long wholeMs = (long)Math.Floor(spanMs);
        if (wholeMs < _gridCount)
        {
            _gridCount = 0;
            _gridSum = 0.0;
        }

        long grid = _gridCount;
        double sum = _gridSum;
        double previous = ClampWatts(_wattsAt((_segmentStartMs + grid) / 1000.0));
        for (; grid < wholeMs; grid++)
        {
            double next = ClampWatts(_wattsAt((_segmentStartMs + grid + 1) / 1000.0));
            sum += (previous + next) / 2.0;
            previous = next;
        }

        _gridCount = grid;
        _gridSum = sum;

        double tailMs = spanMs - wholeMs;
        if (tailMs > 0.0)
        {
            double end = ClampWatts(_wattsAt((_segmentStartMs + spanMs) / 1000.0));
            sum += (previous + end) / 2.0 * tailMs;
        }

        return sum;
    }

    private readonly struct ReadScope(ScriptedNvmlApi owner) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref owner._readsInFlight);
    }
}