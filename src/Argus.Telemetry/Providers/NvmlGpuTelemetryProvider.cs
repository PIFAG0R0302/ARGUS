using System.Runtime.InteropServices;
using System.Text;
using Argus.Core.Contracts.Telemetry;

namespace Argus.Telemetry.Providers;

public interface IGpuTelemetryProvider : IDisposable
{
    (IReadOnlyList<GpuSnapshot> Gpus, IReadOnlyList<SensorReading> ExtraSensors) Sample(DateTimeOffset timestamp);
}

/// <summary>
/// Tier 1 (zero-driver) NVIDIA GPU telemetry via official user-mode nvml.dll.
/// Dynamically probed at runtime so systems without NVIDIA hardware incur zero overhead or errors.
/// </summary>
internal sealed unsafe class NvmlGpuTelemetryProvider : IDisposable
{
    private const int NvmlSuccess = 0;
    private const uint NvmlTemperatureGpu = 0;
    private const uint NvmlClockGraphics = 0;
    private const uint NvmlFanSpeedInfoV1 = 12u | (1u << 24);

    private readonly IntPtr _libraryHandle;
    private readonly bool _initialized;
    private bool _disposed;

    private readonly delegate* unmanaged[Cdecl]<int> _nvmlShutdown;
    private readonly delegate* unmanaged[Cdecl]<uint*, int> _nvmlDeviceGetCount;
    private readonly delegate* unmanaged[Cdecl]<uint, IntPtr*, int> _nvmlDeviceGetHandleByIndex;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int> _nvmlDeviceGetName;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, NvmlUtilization*, int> _nvmlDeviceGetUtilizationRates;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, NvmlMemory*, int> _nvmlDeviceGetMemoryInfo;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, int> _nvmlDeviceGetTemperature;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, int> _nvmlDeviceGetClockInfo;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, int> _nvmlDeviceGetFanSpeed;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, NvmlFanSpeedInfo*, int> _nvmlDeviceGetFanSpeedRpm;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, int> _nvmlDeviceGetPowerUsage;

    public bool IsAvailable => _initialized && !_disposed;

    public NvmlGpuTelemetryProvider()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!TryLoadNvml(out _libraryHandle))
        {
            return;
        }

        if (!NativeLibrary.TryGetExport(_libraryHandle, "nvmlInit_v2", out IntPtr initPtr) ||
            !NativeLibrary.TryGetExport(_libraryHandle, "nvmlShutdown", out IntPtr shutdownPtr) ||
            !NativeLibrary.TryGetExport(_libraryHandle, "nvmlDeviceGetCount_v2", out IntPtr countPtr) ||
            !NativeLibrary.TryGetExport(_libraryHandle, "nvmlDeviceGetHandleByIndex_v2", out IntPtr handlePtr) ||
            !NativeLibrary.TryGetExport(_libraryHandle, "nvmlDeviceGetName", out IntPtr namePtr))
        {
            NativeLibrary.Free(_libraryHandle);
            _libraryHandle = IntPtr.Zero;
            return;
        }

        var nvmlInit = (delegate* unmanaged[Cdecl]<int>)initPtr;
        if (nvmlInit() != NvmlSuccess)
        {
            NativeLibrary.Free(_libraryHandle);
            _libraryHandle = IntPtr.Zero;
            return;
        }

        _initialized = true;
        _nvmlShutdown = (delegate* unmanaged[Cdecl]<int>)shutdownPtr;
        _nvmlDeviceGetCount = (delegate* unmanaged[Cdecl]<uint*, int>)countPtr;
        _nvmlDeviceGetHandleByIndex = (delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)handlePtr;
        _nvmlDeviceGetName = (delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int>)namePtr;

        if (NativeLibrary.TryGetExport(_libraryHandle, "nvmlDeviceGetUtilizationRates", out IntPtr utilPtr))
        {
            _nvmlDeviceGetUtilizationRates = (delegate* unmanaged[Cdecl]<IntPtr, NvmlUtilization*, int>)utilPtr;
        }

        if (NativeLibrary.TryGetExport(_libraryHandle, "nvmlDeviceGetMemoryInfo", out IntPtr memPtr))
        {
            _nvmlDeviceGetMemoryInfo = (delegate* unmanaged[Cdecl]<IntPtr, NvmlMemory*, int>)memPtr;
        }

        if (NativeLibrary.TryGetExport(_libraryHandle, "nvmlDeviceGetTemperature", out IntPtr tempPtr))
        {
            _nvmlDeviceGetTemperature = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, int>)tempPtr;
        }

        if (NativeLibrary.TryGetExport(_libraryHandle, "nvmlDeviceGetClockInfo", out IntPtr clockPtr))
        {
            _nvmlDeviceGetClockInfo = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, int>)clockPtr;
        }

        if (NativeLibrary.TryGetExport(_libraryHandle, "nvmlDeviceGetFanSpeed", out IntPtr fanPtr))
        {
            _nvmlDeviceGetFanSpeed = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)fanPtr;
        }

        if (NativeLibrary.TryGetExport(_libraryHandle, "nvmlDeviceGetFanSpeedRPM", out IntPtr fanRpmPtr))
        {
            _nvmlDeviceGetFanSpeedRpm = (delegate* unmanaged[Cdecl]<IntPtr, NvmlFanSpeedInfo*, int>)fanRpmPtr;
        }

        if (NativeLibrary.TryGetExport(_libraryHandle, "nvmlDeviceGetPowerUsage", out IntPtr powerPtr))
        {
            _nvmlDeviceGetPowerUsage = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)powerPtr;
        }
    }

    public (List<GpuSnapshot> Gpus, List<SensorReading> ExtraSensors) Sample(DateTimeOffset timestamp)
    {
        var gpus = new List<GpuSnapshot>();
        var extra = new List<SensorReading>();

        if (!IsAvailable)
        {
            return (gpus, extra);
        }

        uint count = 0;
        if (_nvmlDeviceGetCount(&count) != NvmlSuccess || count == 0)
        {
            return (gpus, extra);
        }

        byte* nameBuf = stackalloc byte[96];

        for (uint i = 0; i < count; i++)
        {
            IntPtr device = IntPtr.Zero;
            if (_nvmlDeviceGetHandleByIndex(i, &device) != NvmlSuccess || device == IntPtr.Zero)
            {
                continue;
            }

            string name = "NVIDIA GPU";
            if (_nvmlDeviceGetName(device, nameBuf, 96) == NvmlSuccess)
            {
                int len = 0;
                while (len < 96 && nameBuf[len] != 0)
                {
                    len++;
                }

                if (len > 0)
                {
                    name = Encoding.UTF8.GetString(nameBuf, len).Trim();
                }
            }

            double? coreLoad = null;
            if (_nvmlDeviceGetUtilizationRates != null)
            {
                NvmlUtilization util = default;
                if (_nvmlDeviceGetUtilizationRates(device, &util) == NvmlSuccess)
                {
                    coreLoad = Math.Clamp(util.Gpu, 0u, 100u);
                }
            }

            long? vramTotal = null;
            long? vramUsed = null;
            if (_nvmlDeviceGetMemoryInfo != null)
            {
                NvmlMemory mem = default;
                if (_nvmlDeviceGetMemoryInfo(device, &mem) == NvmlSuccess && mem.Total > 0)
                {
                    vramTotal = (long)Math.Min(mem.Total, (ulong)long.MaxValue);
                    vramUsed = (long)Math.Min(mem.Used, (ulong)vramTotal.Value);
                }
            }

            double? tempC = null;
            if (_nvmlDeviceGetTemperature != null)
            {
                uint rawTemp = 0;
                if (_nvmlDeviceGetTemperature(device, NvmlTemperatureGpu, &rawTemp) == NvmlSuccess && rawTemp is > 0 and < 140)
                {
                    tempC = rawTemp;
                }
            }

            double? coreClockMHz = null;
            if (_nvmlDeviceGetClockInfo != null)
            {
                uint rawClock = 0;
                if (_nvmlDeviceGetClockInfo(device, NvmlClockGraphics, &rawClock) == NvmlSuccess && rawClock > 0)
                {
                    coreClockMHz = rawClock;
                }
            }

            int? fanRpm = null;
            if (_nvmlDeviceGetFanSpeedRpm != null)
            {
                var fanInfo = new NvmlFanSpeedInfo
                {
                    Version = NvmlFanSpeedInfoV1,
                    Fan = 0
                };
                if (_nvmlDeviceGetFanSpeedRpm(device, &fanInfo) == NvmlSuccess && fanInfo.Speed is > 0 and < 20000)
                {
                    fanRpm = (int)fanInfo.Speed;
                }
            }

            if (_nvmlDeviceGetFanSpeed != null)
            {
                uint fanPct = 0;
                if (_nvmlDeviceGetFanSpeed(device, &fanPct) == NvmlSuccess && fanPct <= 100)
                {
                    extra.Add(new SensorReading(
                        SensorId: $"nvml.gpu.{i}.fan_percent",
                        Name: $"{name} — вентилятор",
                        Component: name,
                        Kind: SensorKind.LoadPercent,
                        Value: fanPct,
                        Unit: "%",
                        Tier: SensorTier.NativeZeroDriver,
                        Timestamp: timestamp));
                }
            }

            if (_nvmlDeviceGetPowerUsage != null)
            {
                uint milliwatts = 0;
                if (_nvmlDeviceGetPowerUsage(device, &milliwatts) == NvmlSuccess && milliwatts > 0)
                {
                    extra.Add(new SensorReading(
                        SensorId: $"nvml.gpu.{i}.power",
                        Name: $"{name} — споживання",
                        Component: name,
                        Kind: SensorKind.PowerWatts,
                        Value: Math.Round(milliwatts / 1000.0, 1),
                        Unit: "Вт",
                        Tier: SensorTier.NativeZeroDriver,
                        Timestamp: timestamp));
                }
            }

            gpus.Add(new GpuSnapshot(
                Name: name,
                Vendor: "NVIDIA",
                CoreLoadPercent: coreLoad,
                VramTotalBytes: vramTotal,
                VramUsedBytes: vramUsed,
                TemperatureCelsius: tempC,
                CoreClockMHz: coreClockMHz,
                FanRpm: fanRpm));
        }

        return (gpus, extra);
    }

    private static bool TryLoadNvml(out IntPtr handle)
    {
        string[] candidates =
        [
            "nvml.dll",
            Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll")
        ];

        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out handle))
            {
                return true;
            }
        }

        handle = IntPtr.Zero;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initialized && _nvmlShutdown != null)
        {
            try
            {
                _ = _nvmlShutdown();
            }
            catch
            {
                // best effort teardown
            }
        }

        if (_libraryHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_libraryHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlFanSpeedInfo
    {
        public uint Version;
        public uint Fan;
        public uint Speed;
    }
}
