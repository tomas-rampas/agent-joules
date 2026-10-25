using AgentJoules.Core.Energy;

namespace AgentJoules.Nvml.Platform;

/// <summary>
/// The production <see cref="IPowerSourceDetector"/> (REQ-021, AJ-106, design 5.6): on Windows it reads <c>ACLineStatus</c> of <c>GetSystemPowerStatus</c> (1 is <see cref="PowerSources.Ac"/>, 0 is
/// <see cref="PowerSources.Battery"/>, anything else and a failed call are <see cref="PowerSources.Unknown"/>); on Linux it reads <c>/sys/class/power_supply/*/type</c> and the matching <c>online</c> file.
/// Any other operating system, a missing folder and every failure are <see cref="PowerSources.Unknown"/>: the detector never throws and never guesses.
/// </summary>
/// <remarks>
/// <para>
/// The Linux rule, in this order. A supply whose <c>scope</c> file reads <c>Device</c> (a mouse, a headset) is skipped. If a <c>Mains</c> supply exists (types are compared without regard to case): <c>online</c> 1 is <c>ac</c>;
/// <c>online</c> 0 is <c>battery</c> when a <c>Battery</c> supply exists and <c>unknown</c> otherwise; a missing or unreadable <c>online</c> file is <c>unknown</c>. Without mains, a <c>USB</c> supply (a USB-C source
/// cannot be told from a sink) is <c>unknown</c> whatever it reports, and <c>Battery</c> supplies alone are <c>battery</c>. No supply, no folder and every failure are <c>unknown</c>; a directory without a <c>type</c> file is no supply.
/// </para>
/// <para>
/// The platform readers are injected so that a test never calls the real API and never reads the <c>/sys</c> of the machine that runs it. The parameterless constructor selects the reader of the current operating system.
/// </para>
/// </remarks>
/// <param name="windowsAcLineStatus">Returns the <c>ACLineStatus</c> byte of the Windows API, or null when the call failed; null for no Windows reader. The Windows reader is used first when it is not null.</param>
/// <param name="linuxPowerSupplyRoot">The folder that holds one directory per power supply; null for no Linux reader.</param>
public sealed class PowerSourceDetector(Func<byte?>? windowsAcLineStatus, string? linuxPowerSupplyRoot) : IPowerSourceDetector
{
    /// <summary>The folder of the Linux power supplies.</summary>
    public const string LinuxPowerSupplyRoot = "/sys/class/power_supply";

    private const string TypeFile = "type";
    private const string OnlineFile = "online";
    private const string ScopeFile = "scope";
    private const string DeviceScope = "Device";
    private const string UsbType = "USB";
    private const string MainsType = "Mains";
    private const string BatteryType = "Battery";
    private const byte AcLineOnline = 1;
    private const byte AcLineOffline = 0;

    /// <summary>Creates the detector of the current operating system: the Windows reader on Windows, the sysfs reader on Linux, none elsewhere.</summary>
    public PowerSourceDetector()
        : this(OperatingSystem.IsWindows() ? PowerStatusNative.ReadAcLineStatus : null, OperatingSystem.IsLinux() ? LinuxPowerSupplyRoot : null)
    {
    }

    /// <inheritdoc />
    public string Detect()
    {
        try
        {
            return windowsAcLineStatus is { } windows
                ? FromAcLineStatus(windows())
                : linuxPowerSupplyRoot is { } root ? FromPowerSupplyRoot(root) : PowerSources.Unknown;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Any failure of the platform (a missing library, an access error, a vanished file) is an unknown source, not a failed run.
            return PowerSources.Unknown;
        }
    }

    /// <summary>Maps the <c>ACLineStatus</c> of the Windows API to a power source.</summary>
    /// <param name="acLineStatus">The status byte, or null when the call failed.</param>
    /// <returns><see cref="PowerSources.Ac"/> for 1, <see cref="PowerSources.Battery"/> for 0, otherwise <see cref="PowerSources.Unknown"/>.</returns>
    public static string FromAcLineStatus(byte? acLineStatus) => acLineStatus switch
    {
        AcLineOnline => PowerSources.Ac,
        AcLineOffline => PowerSources.Battery,
        _ => PowerSources.Unknown,
    };

    /// <summary>Reads the power supplies under <paramref name="root"/> (the Linux rule of the remarks).</summary>
    /// <param name="root">The folder with one directory per supply.</param>
    /// <returns>The power source; <see cref="PowerSources.Unknown"/> when the folder does not exist or holds no mains or battery supply.</returns>
    /// <exception cref="IOException">A supply could not be read; <see cref="Detect"/> turns it into <see cref="PowerSources.Unknown"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">A supply could not be read; <see cref="Detect"/> turns it into <see cref="PowerSources.Unknown"/>.</exception>
    public static string FromPowerSupplyRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            return PowerSources.Unknown;
        }

        bool mains = false;
        bool mainsOnline = false;
        bool mainsUnreadable = false;
        bool battery = false;
        bool usb = false;
        foreach (string supply in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            // A mouse, a headset or a phone is a power supply too, but not the machine's: a supply of scope Device says nothing about where the machine's power comes from.
            if (string.Equals(ReadTrimmed(Path.Combine(supply, ScopeFile)), DeviceScope, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? type = ReadTrimmed(Path.Combine(supply, TypeFile));
            if (string.Equals(type, MainsType, StringComparison.OrdinalIgnoreCase))
            {
                mains = true;
                switch (ReadTrimmed(Path.Combine(supply, OnlineFile)))
                {
                    case "1":
                        mainsOnline = true;
                        break;
                    case "0":
                        break;
                    default:
                        mainsUnreadable = true;
                        break;
                }
            }
            else if (string.Equals(type, BatteryType, StringComparison.OrdinalIgnoreCase))
            {
                battery = true;
            }
            else if (type is not null && type.StartsWith(UsbType, StringComparison.OrdinalIgnoreCase))
            {
                usb = true;
            }
        }

        // A mains supply decides: online is ac, offline is battery when there is a battery to run on, and a state that cannot be read is unknown. Without mains, a USB source says nothing (a source cannot be told from a sink),
        // and only batteries mean battery.
        return mains
            ? mainsOnline ? PowerSources.Ac : mainsUnreadable || !battery ? PowerSources.Unknown : PowerSources.Battery
            : usb ? PowerSources.Unknown : battery ? PowerSources.Battery : PowerSources.Unknown;
    }

    // The text of a small file, trimmed; null when the file does not exist.
    private static string? ReadTrimmed(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}