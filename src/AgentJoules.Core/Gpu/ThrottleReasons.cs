namespace AgentJoules.Core.Gpu;

/// <summary>
/// The NVML clock-event reason bits. The values are the <c>nvmlClocksEventReason*</c> macros of nvml.h (the three hardware bits are
/// still named <c>nvmlClocksThrottleReason*</c> there); the page
/// https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlClocksEventReasons.html lists the names and nvml.h gives the values.
/// nvml.h defines three more bits that the design does not map: DisplayClockSetting 0x100, BoardLimit 0x200 and Reliability 0x400.
/// They are not members here and never set the throttled flag, pending the owner's decision.
/// </summary>
public enum ThrottleReasonBits : ulong
{
    /// <summary>The GPU is idle; not throttling.</summary>
    GpuIdle = 0x1,

    /// <summary>Clocks are set to application-specific values; not throttling.</summary>
    ApplicationsClocksSetting = 0x2,

    /// <summary>A software power cap is active; throttling.</summary>
    SwPowerCap = 0x4,

    /// <summary>A hardware slowdown is active; throttling.</summary>
    HwSlowdown = 0x8,

    /// <summary>Sync boost is active; not throttling.</summary>
    SyncBoost = 0x10,

    /// <summary>A software thermal slowdown is active; throttling.</summary>
    SwThermalSlowdown = 0x20,

    /// <summary>A hardware thermal slowdown is active; throttling.</summary>
    HwThermalSlowdown = 0x40,

    /// <summary>A hardware power-brake slowdown is active; throttling.</summary>
    HwPowerBrakeSlowdown = 0x80,
}

/// <summary>
/// The clock-event (throttle) bits one read returned. The five power and thermal bits count as throttling (design 3.1); the idle,
/// applications-clocks and sync-boost bits, and any bit without a name, do not.
/// </summary>
/// <param name="Bits">The raw NVML clock-event bitmask; 0 when unsupported.</param>
/// <param name="Supported">False when the driver cannot report clock events.</param>
public readonly record struct ThrottleReasons(ulong Bits, bool Supported)
{
    /// <summary>The bits that mean the GPU was throttled for power or thermal reasons.</summary>
    public const ulong ThrottlingMask =
        (ulong)ThrottleReasonBits.SwPowerCap
        | (ulong)ThrottleReasonBits.HwSlowdown
        | (ulong)ThrottleReasonBits.SwThermalSlowdown
        | (ulong)ThrottleReasonBits.HwThermalSlowdown
        | (ulong)ThrottleReasonBits.HwPowerBrakeSlowdown;

    // The one table of throttling names and their bit order; Names and AllNames derive from it. ThrottlingMask is written out by
    // hand (a constant cannot be computed from the table), and a unit test pins it to the table.
    private static readonly (ThrottleReasonBits Bit, string Name)[] Table =
    [
        (ThrottleReasonBits.SwPowerCap, nameof(ThrottleReasonBits.SwPowerCap)),
        (ThrottleReasonBits.HwSlowdown, nameof(ThrottleReasonBits.HwSlowdown)),
        (ThrottleReasonBits.SwThermalSlowdown, nameof(ThrottleReasonBits.SwThermalSlowdown)),
        (ThrottleReasonBits.HwThermalSlowdown, nameof(ThrottleReasonBits.HwThermalSlowdown)),
        (ThrottleReasonBits.HwPowerBrakeSlowdown, nameof(ThrottleReasonBits.HwPowerBrakeSlowdown)),
    ];

    /// <summary>The value of a read on a driver that cannot report clock events.</summary>
    public static ThrottleReasons Unsupported => new(0UL, false);

    /// <summary>The names of all five throttling reasons in bit order; the vocabulary of <c>EnergyReading.ThrottleReasons</c>. Built once; read-only.</summary>
    public static IReadOnlyList<string> AllNames { get; } = Array.AsReadOnly([.. Table.Select(static entry => entry.Name)]);

    /// <summary>True when the read is supported and at least one power or thermal bit is set.</summary>
    public bool IsThrottled => Supported && (Bits & ThrottlingMask) != 0;

    /// <summary>
    /// The value for <c>EnergyReading.ThrottleReasons</c>, where this value is the union of a window's reads. A throttling bit is a
    /// positive claim and stands on any evidence, so the names are returned whenever <see cref="IsThrottled"/>. "Nothing was active"
    /// is a negative claim and needs every read of the window to have been supported, so an empty list is returned only then;
    /// otherwise <see langword="null"/> (nothing can be said).
    /// </summary>
    /// <param name="everyReadSupported">True when the window had at least one read and every read was supported.</param>
    /// <returns>The names when throttled; an empty list when nothing was active and every read was supported; otherwise null.</returns>
    public IReadOnlyList<string>? ReadingNames(bool everyReadSupported)
    {
        // A throttling bit is a positive claim; "nothing active" is a negative claim that needs full coverage.
        IReadOnlyList<string>? quiet = Supported && everyReadSupported ? [] : null;
        return IsThrottled ? Names : quiet;
    }

    /// <summary>
    /// The names of the throttling bits that are set, in bit order; empty when the value is unsupported or no throttling bit is set.
    /// </summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            if (!Supported)
            {
                return [];
            }

            var names = new List<string>(Table.Length);
            foreach ((ThrottleReasonBits bit, string name) in Table)
            {
                if ((Bits & (ulong)bit) != 0)
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    /// <summary>Combines two reads: the bits of the supported ones or-ed together; unsupported only when neither is supported.</summary>
    /// <param name="other">The read to combine with this one.</param>
    /// <returns>The combined read; the bits of an unsupported read never count.</returns>
    public ThrottleReasons Or(ThrottleReasons other) =>
        Supported
            ? (other.Supported ? new ThrottleReasons(Bits | other.Bits, true) : this)
            : (other.Supported ? other : Unsupported);

    /// <summary>The union of several reads, for example those of a window's samples.</summary>
    /// <param name="reads">The reads to combine.</param>
    /// <returns><see cref="Unsupported"/> when no read is supported, otherwise a supported value with the bits of the supported reads or-ed together.</returns>
    public static ThrottleReasons Union(IEnumerable<ThrottleReasons> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);

        ThrottleReasons union = Unsupported;
        foreach (ThrottleReasons read in reads)
        {
            union = union.Or(read);
        }

        return union;
    }
}