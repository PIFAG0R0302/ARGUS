using System.Runtime.InteropServices;
using Argus.Core.Contracts.Telemetry;

namespace Argus.Telemetry.Providers;

public interface IMemoryTelemetryProvider
{
    MemorySnapshot Sample();
}

/// <summary>
/// Tier 1 (zero-driver) RAM telemetry via Win32 GlobalMemoryStatusEx.
/// </summary>
public sealed partial class WindowsMemoryTelemetryProvider : IMemoryTelemetryProvider
{
    public MemorySnapshot Sample()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx
            {
                DwLength = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };

            if (GlobalMemoryStatusEx(ref status) && status.UllTotalPhys > 0)
            {
                long total = (long)Math.Min(status.UllTotalPhys, (ulong)long.MaxValue);
                long avail = (long)Math.Min(status.UllAvailPhys, (ulong)total);
                long used = Math.Max(0, total - avail);
                double load = Math.Round((double)used / total * 100.0, 1);
                return new MemorySnapshot(total, used, avail, Math.Clamp(load, 0.0, 100.0));
            }
        }

        long fallbackTotal = Math.Max(1, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        return new MemorySnapshot(fallbackTotal, 0, fallbackTotal, 0.0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint DwLength;
        public uint DwMemoryLoad;
        public ulong UllTotalPhys;
        public ulong UllAvailPhys;
        public ulong UllTotalPageFile;
        public ulong UllAvailPageFile;
        public ulong UllTotalVirtual;
        public ulong UllAvailVirtual;
        public ulong UllAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
