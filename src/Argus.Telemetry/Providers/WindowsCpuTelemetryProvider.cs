using System.Runtime.InteropServices;
using Argus.Core.Contracts.Telemetry;
using Argus.Telemetry.Native;
using Microsoft.Win32;

namespace Argus.Telemetry.Providers;

public interface ICpuTelemetryProvider : IDisposable
{
    CpuSnapshot Sample(double? packageTemperatureCelsius = null, double? packagePowerWatts = null);
}

/// <summary>
/// Tier 1 (zero-driver) CPU telemetry using native Windows kernel APIs:
/// - NtQuerySystemInformation(SystemProcessorPerformanceInformation) for total and per-core load
/// - CallNtPowerInformation + PDH % Processor Performance for real dynamic frequency (MHz)
/// - Registry &amp; GetLogicalProcessorInformationEx for exact core/thread topology
/// </summary>
public sealed partial class WindowsCpuTelemetryProvider : ICpuTelemetryProvider
{
    private const int SystemProcessorPerformanceInformation = 8;
    private const int PowerInfoProcessorInformation = 11;
    private const int RelationProcessorCore = 0;

    private readonly string _cpuName;
    private readonly int _physicalCores;
    private readonly int _logicalProcessors;
    private readonly double _baseClockMHz;

    private readonly PdhQuerySession? _pdh;
    private readonly IntPtr _perfPercentCounter;
    private bool _pdhPrimed;

    private ProcessorPerformanceSample[]? _previousSamples;
    private bool _disposed;

    public WindowsCpuTelemetryProvider()
    {
        _logicalProcessors = Math.Max(1, Environment.ProcessorCount);
        (_cpuName, _baseClockMHz) = ReadRegistryCpuIdentity();
        _physicalCores = Math.Clamp(DetectPhysicalCoreCount(), 1, _logicalProcessors);

        _pdh = PdhQuerySession.TryCreate();
        if (_pdh is not null)
        {
            _perfPercentCounter = _pdh.TryAddEnglishCounter(@"\Processor Information(_Total)\% Processor Performance");
            if (_perfPercentCounter != IntPtr.Zero)
            {
                _pdhPrimed = _pdh.Collect();
            }
        }

        // Prime initial kernel CPU time sample so the very first UI tick already has real load data.
        _previousSamples = ReadProcessorPerformanceSamples(_logicalProcessors);
    }

    public CpuSnapshot Sample(double? packageTemperatureCelsius = null, double? packagePowerWatts = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var current = ReadProcessorPerformanceSamples(_logicalProcessors);
        var (totalLoad, perCore) = ComputeCpuLoads(_previousSamples, current, _logicalProcessors);
        if (current is not null)
        {
            _previousSamples = current;
        }

        double? currentClockMHz = SampleCurrentClockMHz();

        return new CpuSnapshot(
            Name: _cpuName,
            PhysicalCores: _physicalCores,
            LogicalProcessors: _logicalProcessors,
            TotalLoadPercent: totalLoad,
            PerCoreLoadPercent: perCore,
            CurrentClockMHz: currentClockMHz,
            PackageTemperatureCelsius: packageTemperatureCelsius,
            PackagePowerWatts: packagePowerWatts);
    }

    internal static (double TotalLoad, double[] PerCore) ComputeCpuLoads(
        ProcessorPerformanceSample[]? previous,
        ProcessorPerformanceSample[]? current,
        int logicalCount)
    {
        var perCore = new double[Math.Max(1, logicalCount)];
        if (previous is null || current is null || previous.Length == 0 || current.Length == 0)
        {
            return (0.0, perCore);
        }

        int count = Math.Min(Math.Min(previous.Length, current.Length), perCore.Length);
        long totalDeltaBusy = 0;
        long totalDeltaAll = 0;

        for (int i = 0; i < count; i++)
        {
            long deltaIdle = current[i].IdleTime - previous[i].IdleTime;
            long deltaKernel = current[i].KernelTime - previous[i].KernelTime;
            long deltaUser = current[i].UserTime - previous[i].UserTime;

            // KernelTime in Windows includes IdleTime.
            long deltaTotal = deltaKernel + deltaUser;
            long deltaBusy = deltaTotal - deltaIdle;

            if (deltaTotal > 0 && deltaBusy >= 0)
            {
                double load = Math.Clamp((double)deltaBusy / deltaTotal * 100.0, 0.0, 100.0);
                perCore[i] = Math.Round(load, 1);
                totalDeltaBusy += deltaBusy;
                totalDeltaAll += deltaTotal;
            }
        }

        double total = totalDeltaAll > 0
            ? Math.Round(Math.Clamp((double)totalDeltaBusy / totalDeltaAll * 100.0, 0.0, 100.0), 1)
            : 0.0;

        return (total, perCore);
    }

    private double? SampleCurrentClockMHz()
    {
        double baseOrMaxMHz = _baseClockMHz;
        double? powerInfoCurrentMHz = TryReadPowerInfoClockMHz(out double maxPowerMHz);
        if (maxPowerMHz > 0)
        {
            baseOrMaxMHz = maxPowerMHz;
        }

        if (_pdh is not null && _perfPercentCounter != IntPtr.Zero && baseOrMaxMHz > 0)
        {
            if (_pdh.Collect())
            {
                if (_pdhPrimed && _pdh.TryGetDouble(_perfPercentCounter, out double perfPct, allowOver100: true) && perfPct > 1.0)
                {
                    return Math.Round(baseOrMaxMHz * (perfPct / 100.0), 0);
                }

                _pdhPrimed = true;
            }
        }

        if (powerInfoCurrentMHz is > 0)
        {
            return Math.Round(powerInfoCurrentMHz.Value, 0);
        }

        return baseOrMaxMHz > 0 ? Math.Round(baseOrMaxMHz, 0) : null;
    }

    private unsafe double? TryReadPowerInfoClockMHz(out double maxMhz)
    {
        maxMhz = 0;
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        int size = Marshal.SizeOf<ProcessorPowerInformation>() * _logicalProcessors;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            int status = CallNtPowerInformation(
                PowerInfoProcessorInformation,
                IntPtr.Zero,
                0,
                buffer,
                (uint)size);

            if (status != 0)
            {
                return null;
            }

            var entries = (ProcessorPowerInformation*)buffer;
            double sumCurrent = 0;
            uint maxSeen = 0;
            int valid = 0;
            for (int i = 0; i < _logicalProcessors; i++)
            {
                if (entries[i].CurrentMhz > 0)
                {
                    sumCurrent += entries[i].CurrentMhz;
                    valid++;
                }

                if (entries[i].MaxMhz > maxSeen)
                {
                    maxSeen = entries[i].MaxMhz;
                }
            }

            maxMhz = maxSeen;
            return valid > 0 ? sumCurrent / valid : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe ProcessorPerformanceSample[]? ReadProcessorPerformanceSamples(int logicalCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        int structSize = Marshal.SizeOf<SystemProcessorPerformanceInfo>();
        int bufferSize = structSize * Math.Max(1, logicalCount);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            int status = NtQuerySystemInformation(
                SystemProcessorPerformanceInformation,
                buffer,
                bufferSize,
                out int returnLength);

            if (status != 0 || returnLength < structSize)
            {
                return null;
            }

            int count = Math.Min(logicalCount, returnLength / structSize);
            var samples = new ProcessorPerformanceSample[count];
            var native = (SystemProcessorPerformanceInfo*)buffer;
            for (int i = 0; i < count; i++)
            {
                samples[i] = new ProcessorPerformanceSample(
                    native[i].IdleTime,
                    native[i].KernelTime,
                    native[i].UserTime);
            }

            return samples;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (string Name, double BaseMHz) ReadRegistryCpuIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ("CPU", 0);
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Процесор Windows";
            double mhz = key?.GetValue("~MHz") is int rawMhz && rawMhz > 0 ? rawMhz : 0;
            return (NormalizeWhitespace(name), mhz);
        }
        catch
        {
            return ("Процесор Windows", 0);
        }
    }

    private static int DetectPhysicalCoreCount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Environment.ProcessorCount;
        }

        try
        {
            uint len = 0;
            _ = GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len == 0)
            {
                return Environment.ProcessorCount;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref len))
                {
                    return Environment.ProcessorCount;
                }

                int cores = 0;
                int offset = 0;
                while (offset < (int)len)
                {
                    int entrySize = Marshal.ReadInt32(buffer, offset + 4);
                    if (entrySize <= 0)
                    {
                        break;
                    }

                    cores++;
                    offset += entrySize;
                }

                return cores > 0 ? cores : Environment.ProcessorCount;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return Environment.ProcessorCount;
        }
    }

    private static string NormalizeWhitespace(string text)
    {
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pdh?.Dispose();
    }

    internal readonly record struct ProcessorPerformanceSample(long IdleTime, long KernelTime, long UserTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessorPerformanceInfo
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [LibraryImport("powrprof.dll")]
    private static partial int CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        uint inputBufferLength,
        IntPtr outputBuffer,
        uint outputBufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);
}
