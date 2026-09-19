using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace JevBrowse.Diagnostics;

/// <summary>Raw OS memory/power readings. Kept renderer-free and AI-free; the Resource OS turns these into bands.</summary>
[SupportedOSPlatform("windows")]
public static class SystemPressureSampler
{
    public sealed record Reading(long AvailableBytes, long TotalBytes, bool OnBattery, DateTimeOffset At);

    public static Reading Sample()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) throw new InvalidOperationException("GlobalMemoryStatusEx failed");
        bool onBattery = GetSystemPowerStatus(out var p) && p.ACLineStatus == 0;
        return new Reading((long)m.ullAvailPhys, (long)m.ullTotalPhys, onBattery, DateTimeOffset.UtcNow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
