using System.Runtime.InteropServices;

namespace AgentJoules.Nvml.Platform;

/// <summary>
/// The one Windows entry point of the power source (REQ-021, AJ-106): <c>GetSystemPowerStatus</c> of kernel32. The import loads from the system directory only, never from the application folder
/// (<see cref="DefaultDllImportSearchPathsAttribute"/> with <see cref="DllImportSearchPath.System32"/>, as the assembly default of this project also says), so a copy of <c>kernel32.dll</c> next to the executable is never bound.
/// </summary>
internal static partial class PowerStatusNative
{
    /// <summary>The <c>SYSTEM_POWER_STATUS</c> structure of the Windows API; only <see cref="ACLineStatus"/> is read, the rest keeps the layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        /// <summary>0 offline (battery), 1 online (mains), 255 unknown.</summary>
        public byte ACLineStatus;

        public byte BatteryFlag;

        public byte BatteryLifePercent;

        public byte SystemStatusFlag;

        public uint BatteryLifeTime;

        public uint BatteryFullLifeTime;
    }

    /// <summary>Reads the AC line status.</summary>
    /// <returns>The <c>ACLineStatus</c> byte, or null when the call failed.</returns>
    internal static byte? ReadAcLineStatus() => GetSystemPowerStatus(out SystemPowerStatus status) ? status.ACLineStatus : null;

    // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getsystempowerstatus
    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}