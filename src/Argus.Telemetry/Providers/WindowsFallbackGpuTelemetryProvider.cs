using System.Management;
using Argus.Core.Contracts.Telemetry;
using Argus.Telemetry.Native;
using Microsoft.Win32;

namespace Argus.Telemetry.Providers;

/// <summary>
/// Composite Tier 1 (zero-driver) GPU provider combining:
/// 1) NVIDIA NVML (exact VRAM, temperature, core clock, power, fan, utilization)
/// 2) Universal Windows WDDM PDH counters (\GPU Engine + \GPU Adapter Memory) for AMD/Intel/fallback adapters
/// </summary>
public sealed class CompositeGpuTelemetryProvider : IGpuTelemetryProvider
{
    private readonly NvmlGpuTelemetryProvider _nvml;
    private readonly List<VideoAdapterIdentity> _wmiAdapters;
    private readonly PdhQuerySession? _pdh;
    private readonly IntPtr _gpuEngineCounter;
    private readonly IntPtr _gpuDedicatedMemCounter;
    private bool _pdhPrimed;
    private bool _disposed;

    public CompositeGpuTelemetryProvider()
    {
        _nvml = new NvmlGpuTelemetryProvider();
        _wmiAdapters = DiscoverWmiAdapters();

        _pdh = PdhQuerySession.TryCreate();
        if (_pdh is not null)
        {
            _gpuEngineCounter = _pdh.TryAddEnglishCounter(@"\GPU Engine(*)\Utilization Percentage");
            _gpuDedicatedMemCounter = _pdh.TryAddEnglishCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
            if (_gpuEngineCounter != IntPtr.Zero || _gpuDedicatedMemCounter != IntPtr.Zero)
            {
                _pdhPrimed = _pdh.Collect();
            }
        }
    }

    public (IReadOnlyList<GpuSnapshot> Gpus, IReadOnlyList<SensorReading> ExtraSensors) Sample(DateTimeOffset timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var (nvmlGpus, extraSensors) = _nvml.Sample(timestamp);
        var result = new List<GpuSnapshot>(nvmlGpus);

        bool needWddmFallback = _wmiAdapters.Any(a => !MatchesAnyNvmlAdapter(a, nvmlGpus));
        if (!needWddmFallback)
        {
            return (result, extraSensors);
        }

        double? wddmLoad = null;
        long? wddmDedicatedUsed = null;
        if (_pdh is not null && _pdh.Collect())
        {
            if (_pdhPrimed)
            {
                if (_gpuEngineCounter != IntPtr.Zero)
                {
                    var engineSamples = _pdh.GetDoubleArray(_gpuEngineCounter, allowOver100: false);
                    if (engineSamples.Count > 0)
                    {
                        double sum3d = 0;
                        double maxAny = 0;
                        foreach (var kv in engineSamples)
                        {
                            if (kv.Value > maxAny)
                            {
                                maxAny = kv.Value;
                            }

                            if (kv.Key.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                            {
                                sum3d += kv.Value;
                            }
                        }

                        wddmLoad = Math.Round(Math.Clamp(sum3d > 0 ? sum3d : maxAny, 0.0, 100.0), 1);
                    }
                }

                if (_gpuDedicatedMemCounter != IntPtr.Zero)
                {
                    var memSamples = _pdh.GetDoubleArray(_gpuDedicatedMemCounter, allowOver100: true);
                    if (memSamples.Count > 0)
                    {
                        double maxBytes = memSamples.Values.Max();
                        if (maxBytes >= 0)
                        {
                            wddmDedicatedUsed = (long)maxBytes;
                        }
                    }
                }
            }
            else
            {
                _pdhPrimed = true;
            }
        }

        bool appliedWddmMetrics = false;
        foreach (var adapter in _wmiAdapters)
        {
            if (MatchesAnyNvmlAdapter(adapter, nvmlGpus))
            {
                continue;
            }

            result.Add(new GpuSnapshot(
                Name: adapter.Name,
                Vendor: adapter.Vendor,
                CoreLoadPercent: appliedWddmMetrics ? null : wddmLoad,
                VramTotalBytes: adapter.VramTotalBytes,
                VramUsedBytes: appliedWddmMetrics ? null : wddmDedicatedUsed,
                TemperatureCelsius: null,
                CoreClockMHz: null,
                FanRpm: null));

            appliedWddmMetrics = true;
        }

        return (result, extraSensors);
    }

    private static bool MatchesAnyNvmlAdapter(VideoAdapterIdentity adapter, List<GpuSnapshot> nvmlGpus)
    {
        if (nvmlGpus.Count == 0)
        {
            return false;
        }

        return adapter.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
               adapter.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
               adapter.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
               adapter.Name.Contains("Quadro", StringComparison.OrdinalIgnoreCase) ||
               adapter.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
               adapter.Name.Contains("GTX", StringComparison.OrdinalIgnoreCase);
    }

    private static List<VideoAdapterIdentity> DiscoverWmiAdapters()
    {
        var list = new List<VideoAdapterIdentity>();
        if (!OperatingSystem.IsWindows())
        {
            return list;
        }

        var registryVramByName = ReadRegistryVramMap();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, AdapterRAM FROM Win32_VideoController");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    string name = (obj["Name"] as string)?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string vendor = NormalizeVendor((obj["AdapterCompatibility"] as string)?.Trim(), name);
                    long? vramBytes = null;
                    if (registryVramByName.TryGetValue(name, out long regBytes) && regBytes > 0)
                    {
                        vramBytes = regBytes;
                    }
                    else if (obj["AdapterRAM"] is uint rawUInt && rawUInt > 0)
                    {
                        vramBytes = rawUInt;
                    }

                    list.Add(new VideoAdapterIdentity(name, vendor, vramBytes));
                }
            }
        }
        catch
        {
            // Ignore WMI query errors on restricted environments
        }

        return list;
    }

    private static Dictionary<string, long> ReadRegistryVramMap()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null)
            {
                return map;
            }

            foreach (string subName in classKey.GetSubKeyNames())
            {
                if (subName.Length != 4 || !int.TryParse(subName, out _))
                {
                    continue;
                }

                using var sub = classKey.OpenSubKey(subName);
                string? desc = sub?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc))
                {
                    continue;
                }

                object? qwMem = sub?.GetValue("HardwareInformation.qwMemorySize");
                if (qwMem is long l && l > 0)
                {
                    map[desc.Trim()] = l;
                }
                else if (qwMem is byte[] bytes && bytes.Length >= 8)
                {
                    long val = BitConverter.ToInt64(bytes, 0);
                    if (val > 0)
                    {
                        map[desc.Trim()] = val;
                    }
                }
            }
        }
        catch
        {
            // Registry access optional
        }

        return map;
    }

    private static string NormalizeVendor(string? compatibility, string name)
    {
        string probe = $"{compatibility} {name}";
        if (probe.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA";
        }

        if (probe.Contains("AMD", StringComparison.OrdinalIgnoreCase) || probe.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (probe.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        return string.IsNullOrWhiteSpace(compatibility) ? "GPU" : compatibility;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nvml.Dispose();
        _pdh?.Dispose();
    }

    private sealed record VideoAdapterIdentity(string Name, string Vendor, long? VramTotalBytes);
}
