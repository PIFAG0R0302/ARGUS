using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Argus.Core.Contracts.Telemetry;
using Argus.Telemetry.Native;

namespace Argus.Telemetry.Providers;

public interface IStorageTelemetryProvider : IDisposable
{
    IReadOnlyList<StorageThermalSnapshot> Sample();
}

/// <summary>
/// Tier 1 (zero-driver) physical disk throughput and thermal telemetry:
/// - Locale-independent PDH PhysicalDisk read/write bytes/sec
/// - Native Windows IOCTL_STORAGE_QUERY_PROPERTY (StorageDeviceTemperatureProperty) for NVMe/SATA temperatures
/// - Throttled Windows Storage Management WMI (MSFT_StorageReliabilityCounter) fallback when elevated
/// </summary>
public sealed partial class WindowsStorageTelemetryProvider : IStorageTelemetryProvider
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint StorageDeviceTemperatureProperty = 50;
    private const uint PropertyStandardQuery = 0;
    private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
    private const uint OpenExisting = 3;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;

    private static readonly TimeSpan ThermalPollInterval = TimeSpan.FromSeconds(10);

    private readonly List<DiskIdentity> _disks;
    private readonly Dictionary<int, double?> _cachedTemperatures = [];
    private DateTimeOffset _lastThermalPollUtc = DateTimeOffset.MinValue;

    private readonly PdhQuerySession? _pdh;
    private readonly IntPtr _readBytesCounter;
    private readonly IntPtr _writeBytesCounter;
    private bool _pdhPrimed;
    private bool _storageWmiUnavailable;
    private bool _disposed;

    public WindowsStorageTelemetryProvider()
    {
        _disks = DiscoverDisks();

        _pdh = PdhQuerySession.TryCreate();
        if (_pdh is not null)
        {
            _readBytesCounter = _pdh.TryAddEnglishCounter(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
            _writeBytesCounter = _pdh.TryAddEnglishCounter(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
            if (_readBytesCounter != IntPtr.Zero || _writeBytesCounter != IntPtr.Zero)
            {
                _pdhPrimed = _pdh.Collect();
            }
        }
    }

    public IReadOnlyList<StorageThermalSnapshot> Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var readByDisk = new Dictionary<int, double>();
        var writeByDisk = new Dictionary<int, double>();

        if (_pdh is not null && _pdh.Collect())
        {
            if (_pdhPrimed)
            {
                if (_readBytesCounter != IntPtr.Zero)
                {
                    ParseDiskCounterMap(_pdh.GetDoubleArray(_readBytesCounter, allowOver100: true), readByDisk);
                }

                if (_writeBytesCounter != IntPtr.Zero)
                {
                    ParseDiskCounterMap(_pdh.GetDoubleArray(_writeBytesCounter, allowOver100: true), writeByDisk);
                }
            }
            else
            {
                _pdhPrimed = true;
            }
        }

        if (DateTimeOffset.UtcNow - _lastThermalPollUtc >= ThermalPollInterval)
        {
            RefreshTemperatures();
            _lastThermalPollUtc = DateTimeOffset.UtcNow;
        }

        var snapshots = new List<StorageThermalSnapshot>(_disks.Count);
        foreach (var disk in _disks)
        {
            _cachedTemperatures.TryGetValue(disk.DiskNumber, out double? temp);
            double? read = readByDisk.TryGetValue(disk.DiskNumber, out double r) ? Math.Round(r, 0) : 0;
            double? write = writeByDisk.TryGetValue(disk.DiskNumber, out double w) ? Math.Round(w, 0) : 0;

            snapshots.Add(new StorageThermalSnapshot(
                DiskNumber: disk.DiskNumber,
                Model: disk.Model,
                TemperatureCelsius: temp,
                ReadBytesPerSec: read,
                WriteBytesPerSec: write));
        }

        return snapshots;
    }

    internal static bool TryParsePhysicalDiskIndex(string instanceName, out int diskNumber)
    {
        diskNumber = -1;
        if (string.IsNullOrWhiteSpace(instanceName) || instanceName.Equals("_Total", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int spaceIndex = instanceName.IndexOf(' ');
        string token = spaceIndex > 0 ? instanceName[..spaceIndex] : instanceName;
        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out diskNumber) && diskNumber >= 0;
    }

    private static void ParseDiskCounterMap(Dictionary<string, double> raw, Dictionary<int, double> target)
    {
        foreach (var kv in raw)
        {
            if (TryParsePhysicalDiskIndex(kv.Key, out int diskNumber))
            {
                target[diskNumber] = Math.Max(0.0, kv.Value);
            }
        }
    }

    private void RefreshTemperatures()
    {
        if (!OperatingSystem.IsWindows() || _disks.Count == 0)
        {
            return;
        }

        bool anyMissing = false;
        foreach (var disk in _disks)
        {
            double? ioctlTemp = TryReadTemperatureViaIoctl(disk.DiskNumber);
            if (ioctlTemp.HasValue)
            {
                _cachedTemperatures[disk.DiskNumber] = ioctlTemp;
            }
            else
            {
                anyMissing = true;
            }
        }

        if (anyMissing && !_storageWmiUnavailable)
        {
            PopulateMissingTemperaturesViaStorageWmi();
        }
    }

    private static unsafe double? TryReadTemperatureViaIoctl(int diskNumber)
    {
        string path = $@"\\.\PhysicalDrive{diskNumber}";
        IntPtr handle = CreateFileW(
            path,
            GenericRead | GenericWrite,
            FileShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            handle = CreateFileW(
                path,
                0,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
        }

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var query = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceTemperatureProperty,
                QueryType = PropertyStandardQuery
            };

            byte* outBuf = stackalloc byte[512];
            if (!DeviceIoControl(
                handle,
                IoctlStorageQueryProperty,
                ref query,
                (uint)Marshal.SizeOf<StoragePropertyQuery>(),
                (IntPtr)outBuf,
                512,
                out uint bytesReturned,
                IntPtr.Zero))
            {
                return null;
            }

            if (bytesReturned < 32)
            {
                return null;
            }

            // STORAGE_TEMPERATURE_DATA_DESCRIPTOR:
            // offset 12: USHORT InfoCount; offset 24: STORAGE_TEMPERATURE_INFO[0] (offset 26: SHORT Temperature)
            ushort infoCount = *(ushort*)(outBuf + 12);
            if (infoCount == 0)
            {
                return null;
            }

            short tempC = *(short*)(outBuf + 26);
            return tempC is >= 5 and <= 115 ? tempC : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private void PopulateMissingTemperaturesViaStorageWmi()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, Temperature FROM MSFT_StorageReliabilityCounter"));
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    string? deviceIdText = obj["DeviceId"]?.ToString();
                    if (!int.TryParse(deviceIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int diskNumber))
                    {
                        continue;
                    }

                    if (_cachedTemperatures.TryGetValue(diskNumber, out double? existing) && existing.HasValue)
                    {
                        continue;
                    }

                    object? rawTemp = obj["Temperature"];
                    if (rawTemp is null)
                    {
                        continue;
                    }

                    int tempC = Convert.ToInt32(rawTemp, CultureInfo.InvariantCulture);
                    if (tempC is >= 5 and <= 115)
                    {
                        _cachedTemperatures[diskNumber] = tempC;
                    }
                }
            }
        }
        catch
        {
            // Non-elevated or unsupported storage namespace: disable repeated WMI retries to avoid periodic CPU spikes.
            _storageWmiUnavailable = true;
        }
    }

    private static List<DiskIdentity> DiscoverDisks()
    {
        var list = new List<DiskIdentity>();
        if (!OperatingSystem.IsWindows())
        {
            return list;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    int index = obj["Index"] is uint u ? (int)u : -1;
                    string model = (obj["Model"] as string)?.Trim() ?? string.Empty;
                    if (index >= 0)
                    {
                        list.Add(new DiskIdentity(index, string.IsNullOrWhiteSpace(model) ? $"Диск {index}" : model));
                    }
                }
            }

            list.Sort(static (a, b) => a.DiskNumber.CompareTo(b.DiskNumber));
        }
        catch
        {
            // Ignore WMI query errors
        }

        return list;
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

    private sealed record DiskIdentity(int DiskNumber, string Model);

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public uint AdditionalParameters;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}
