using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.System;
using Argus.Core.Journal;

namespace Argus.Maintenance.Disks;

public interface IDefragRunner
{
    Task<(int ExitCode, string Output)> RunDefragAsync(
        string arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

internal sealed class ProcessDefragRunner : IDefragRunner
{
    public async Task<(int ExitCode, string Output)> RunDefragAsync(
        string arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string defragExe = Path.Combine(Environment.SystemDirectory, "defrag.exe");
        if (!File.Exists(defragExe))
        {
            return (-1, $"Утиліту defrag.exe не знайдено за шляхом {defragExe}.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = defragExe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var sb = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!process.Start())
        {
            return (-1, "Не вдалося запустити процес defrag.exe.");
        }

        // defrag.exe /U writes live progress updates using '\r' without '\n', so we read chunks
        // and flush on both '\r' and '\n' rather than BeginOutputReadLine().
        async Task ReadStreamAsync(StreamReader reader)
        {
            char[] buf = new char[256];
            var lineBuilder = new StringBuilder(128);
            while (true)
            {
                int read = await reader.ReadAsync(buf.AsMemory(0, buf.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    char ch = buf[i];
                    if (ch is '\r' or '\n')
                    {
                        if (lineBuilder.Length > 0)
                        {
                            string line = lineBuilder.ToString().Trim();
                            lineBuilder.Clear();
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                lock (sb)
                                {
                                    sb.AppendLine(line);
                                }

                                progress?.Report(line);
                            }
                        }
                    }
                    else
                    {
                        lineBuilder.Append(ch);
                    }
                }
            }

            if (lineBuilder.Length > 0)
            {
                string tail = lineBuilder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    lock (sb)
                    {
                        sb.AppendLine(tail);
                    }

                    progress?.Report(tail);
                }
            }
        }

        var outTask = ReadStreamAsync(process.StandardOutput);
        var errTask = ReadStreamAsync(process.StandardError);

        try
        {
            await Task.WhenAll(outTask, errTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best effort termination
            }

            throw;
        }

        return (process.ExitCode, sb.ToString().Trim());
    }
}

/// <summary>
/// Hardware-aware disk inventory, S.M.A.R.T. reader (Storage WMI + Win32 IOCTL),
/// and policy-enforced volume optimizer (SSD/NVMe -> TRIM only, HDD -> defragment).
/// </summary>
public sealed partial class WindowsDiskOptimizer : IDiskOptimizer
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint StorageDeviceTemperatureProperty = 50;
    private const uint PropertyStandardQuery = 0;
    private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
    private const uint OpenExisting = 3;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;

    private readonly IPrivilegeService _privilegeService;
    private readonly IOperationJournal _journal;
    private readonly IDefragRunner _defragRunner;

    public WindowsDiskOptimizer(
        IPrivilegeService privilegeService,
        IOperationJournal journal,
        IDefragRunner? defragRunner = null)
    {
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _defragRunner = defragRunner ?? new ProcessDefragRunner();
    }

    public Task<IReadOnlyList<PhysicalDiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CollectPhysicalDisks(cancellationToken), cancellationToken);
    }

    public async Task<DiskOptimizationResult> OptimizeVolumeAsync(
        char driveLetter,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        char letter = char.ToUpperInvariant(driveLetter);
        var disks = await GetDisksAsync(cancellationToken).ConfigureAwait(false);
        var ownerDisk = disks.FirstOrDefault(d => d.DriveLetters.Contains(letter));

        var mediaKind = ownerDisk?.MediaKind ?? StorageMediaKind.Unknown;
        var action = DiskOptimizationPolicy.ResolveSafeAction(mediaKind);

        if (!_privilegeService.IsAdministrator)
        {
            const string adminMsg = "Для оптимізації чи TRIM потрібні права адміністратора (запустіть кореневий Argus.exe або натисніть щит UAC).";
            await _journal.RecordAsync(
                moduleId: "argus.maintenance",
                severity: OperationSeverity.Warning,
                action: "DiskOptimizeBlockedNotAdmin",
                details: $"Том {letter}: ({action}) заблоковано без прав адміністратора",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new DiskOptimizationResult(
                DriveLetter: letter,
                MediaKind: mediaKind,
                ActionExecuted: action,
                Succeeded: false,
                Duration: TimeSpan.Zero,
                Summary: adminMsg);
        }

        string args = BuildDefragArguments(letter, action);
        var sw = Stopwatch.StartNew();
        var (exitCode, output) = await _defragRunner.RunDefragAsync(args, progress, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        bool succeeded = exitCode == 0;
        string actionName = action switch
        {
            DiskOptimizationAction.TrimSsd => "TRIM",
            DiskOptimizationAction.DefragmentHdd => "Дефрагментація",
            _ => "Аналіз"
        };

        string summary = succeeded
            ? $"{actionName} тома {letter}: успішно завершено за {sw.Elapsed.TotalSeconds:0.1} с."
            : $"{actionName} тома {letter}: завершився з кодом {exitCode}. {(string.IsNullOrWhiteSpace(output) ? string.Empty : output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault())}";

        await _journal.RecordAsync(
            moduleId: "argus.maintenance",
            severity: succeeded ? OperationSeverity.Success : OperationSeverity.Warning,
            action: "DiskVolumeOptimized",
            details: $"Том={letter}:, Носій={mediaKind}, Дія={action}, Код={exitCode}, Тривалість={sw.Elapsed.TotalSeconds:0.1} с",
            succeeded: succeeded,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new DiskOptimizationResult(
            DriveLetter: letter,
            MediaKind: mediaKind,
            ActionExecuted: action,
            Succeeded: succeeded,
            Duration: sw.Elapsed,
            Summary: summary.Trim());
    }

    internal static StorageMediaKind ClassifyMediaKind(ushort mediaType, ushort busType, uint spindleSpeed, string model)
    {
        if (busType == 7)
        {
            return StorageMediaKind.RemovableUsb;
        }

        if (busType == 17 || model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
        {
            return StorageMediaKind.NvmeSsd;
        }

        if (mediaType == 4 || spindleSpeed == 0 || model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("Solid State", StringComparison.OrdinalIgnoreCase))
        {
            return StorageMediaKind.SataSsd;
        }

        if (mediaType == 3 || (spindleSpeed > 0 && spindleSpeed < uint.MaxValue))
        {
            return StorageMediaKind.Hdd;
        }

        return StorageMediaKind.Unknown;
    }

    internal static string FormatBusType(ushort busType) => busType switch
    {
        17 => "NVMe",
        11 => "SATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        3 => "ATA",
        _ => "Storage"
    };

    internal static SmartHealthStatus MapHealthStatus(ushort healthStatus) => healthStatus switch
    {
        0 => SmartHealthStatus.Healthy,
        1 => SmartHealthStatus.Caution,
        2 => SmartHealthStatus.Critical,
        _ => SmartHealthStatus.Unknown
    };

    internal static string BuildDefragArguments(char driveLetter, DiskOptimizationAction action)
    {
        char letter = char.ToUpperInvariant(driveLetter);
        return action switch
        {
            DiskOptimizationAction.TrimSsd => $"{letter}: /L /U",
            DiskOptimizationAction.DefragmentHdd => $"{letter}: /O /U",
            _ => $"{letter}: /A /U"
        };
    }

    private static IReadOnlyList<PhysicalDiskInfo> CollectPhysicalDisks(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var result = new List<PhysicalDiskInfo>();
        try
        {
            var storageScope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            storageScope.Connect();

            var partitionStyles = ReadPartitionStyles(storageScope);
            var lettersByDisk = ReadDriveLettersByDisk(storageScope);
            var reliabilityByDisk = ReadReliabilityCounters(storageScope);

            using var diskSearcher = new ManagementObjectSearcher(
                storageScope,
                new ObjectQuery("SELECT DeviceId, FriendlyName, SerialNumber, MediaType, BusType, SpindleSpeed, HealthStatus, Size FROM MSFT_PhysicalDisk"));

            foreach (ManagementBaseObject obj in diskSearcher.Get())
            {
                ct.ThrowIfCancellationRequested();

                if (!int.TryParse(obj["DeviceId"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int diskNumber) ||
                    diskNumber < 0)
                {
                    continue;
                }

                string model = NormalizeSpaces((obj["FriendlyName"] as string) ?? $"Диск {diskNumber}");
                string serial = NormalizeSpaces((obj["SerialNumber"] as string) ?? "—");
                ushort mediaType = TryConvertUInt16(obj["MediaType"]);
                ushort busType = TryConvertUInt16(obj["BusType"]);
                uint spindleSpeed = TryConvertUInt32(obj["SpindleSpeed"], fallback: uint.MaxValue);
                ushort rawHealth = TryConvertUInt16(obj["HealthStatus"], fallback: 3);
                long sizeBytes = obj["Size"] is ulong u ? (long)Math.Min(u, (ulong)long.MaxValue) : 0L;

                var mediaKind = ClassifyMediaKind(mediaType, busType, spindleSpeed, model);
                string busLabel = FormatBusType(busType);
                string partStyle = partitionStyles.TryGetValue(diskNumber, out string? ps) ? ps : "RAW";
                IReadOnlyList<char> driveLetters = lettersByDisk.TryGetValue(diskNumber, out var list)
                    ? list
                    : [];

                reliabilityByDisk.TryGetValue(diskNumber, out var smart);
                double? tempC = smart?.TemperatureCelsius ?? TryReadTemperatureViaIoctl(diskNumber);

                var smartSummary = new SmartSummary(
                    Health: MapHealthStatus(rawHealth),
                    TemperatureCelsius: tempC,
                    WearLevelPercent: smart?.WearLevelPercent,
                    PowerOnHours: smart?.PowerOnHours,
                    ReallocatedSectorCount: smart?.ReallocatedSectorCount,
                    MediaErrorCount: smart?.MediaErrorCount);

                result.Add(new PhysicalDiskInfo(
                    DiskNumber: diskNumber,
                    Model: model,
                    SerialNumber: string.IsNullOrWhiteSpace(serial) ? "—" : serial,
                    BusType: busLabel,
                    MediaKind: mediaKind,
                    SizeBytes: sizeBytes,
                    PartitionStyle: partStyle,
                    DriveLetters: driveLetters,
                    Smart: smartSummary));
            }
        }
        catch
        {
            // Fallback to classic Win32_DiskDrive if Storage WMI namespace is unavailable
            result.Clear();
            CollectFallbackWin32Disks(result, ct);
        }

        result.Sort(static (a, b) => a.DiskNumber.CompareTo(b.DiskNumber));
        return result;
    }

    private static Dictionary<int, string> ReadPartitionStyles(ManagementScope scope)
    {
        var map = new Dictionary<int, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Number, PartitionStyle FROM MSFT_Disk"));
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                int number = (int)TryConvertUInt32(obj["Number"], uint.MaxValue);
                if (number < 0)
                {
                    continue;
                }

                ushort style = TryConvertUInt16(obj["PartitionStyle"]);
                map[number] = style switch
                {
                    1 => "MBR",
                    2 => "GPT",
                    _ => "RAW"
                };
            }
        }
        catch
        {
            // optional
        }

        return map;
    }

    private static Dictionary<int, List<char>> ReadDriveLettersByDisk(ManagementScope scope)
    {
        var map = new Dictionary<int, List<char>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition"));
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                int diskNumber = (int)TryConvertUInt32(obj["DiskNumber"], uint.MaxValue);
                if (diskNumber < 0 || !TryExtractDriveLetter(obj["DriveLetter"], out char letter))
                {
                    continue;
                }

                if (!map.TryGetValue(diskNumber, out var list))
                {
                    list = [];
                    map[diskNumber] = list;
                }

                if (!list.Contains(letter))
                {
                    list.Add(letter);
                    list.Sort();
                }
            }
        }
        catch
        {
            // optional
        }

        return map;
    }

    private static Dictionary<int, SmartSummary> ReadReliabilityCounters(ManagementScope scope)
    {
        var map = new Dictionary<int, SmartSummary>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT DeviceId, Temperature, Wear, PowerOnHours, ReadErrorsUncorrected, WriteErrorsUncorrected FROM MSFT_StorageReliabilityCounter"));

            foreach (ManagementBaseObject obj in searcher.Get())
            {
                if (!int.TryParse(obj["DeviceId"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int diskNumber) ||
                    diskNumber < 0)
                {
                    continue;
                }

                double? temp = TryReadNullableDouble(obj["Temperature"]);
                if (temp is < 5 or > 115)
                {
                    temp = null;
                }

                int? wear = TryReadNullableInt(obj["Wear"]);
                if (wear is < 0 or > 100)
                {
                    wear = null;
                }

                long? poh = TryReadNullableLong(obj["PowerOnHours"]);
                long? readErr = TryReadNullableLong(obj["ReadErrorsUncorrected"]);
                long? writeErr = TryReadNullableLong(obj["WriteErrorsUncorrected"]);
                long? mediaErrors = (readErr, writeErr) switch
                {
                    (null, null) => null,
                    _ => (readErr ?? 0L) + (writeErr ?? 0L)
                };

                map[diskNumber] = new SmartSummary(
                    Health: SmartHealthStatus.Healthy,
                    TemperatureCelsius: temp,
                    WearLevelPercent: wear,
                    PowerOnHours: poh,
                    ReallocatedSectorCount: null,
                    MediaErrorCount: mediaErrors);
            }
        }
        catch
        {
            // Non-elevated runs do not have permission to read MSFT_StorageReliabilityCounter; IOCTL fallback handles temperature.
        }

        return map;
    }

    private static void CollectFallbackWin32Disks(List<PhysicalDiskInfo> target, CancellationToken ct)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Index, Model, SerialNumber, InterfaceType, Size FROM Win32_DiskDrive");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                int idx = obj["Index"] is uint u ? (int)u : -1;
                if (idx < 0)
                {
                    continue;
                }

                string model = NormalizeSpaces((obj["Model"] as string) ?? $"Диск {idx}");
                string serial = NormalizeSpaces((obj["SerialNumber"] as string) ?? "—");
                string iface = (obj["InterfaceType"] as string)?.Trim() ?? "SATA";
                long size = obj["Size"] is ulong s ? (long)Math.Min(s, (ulong)long.MaxValue) : 0L;

                ushort busType = iface.Equals("USB", StringComparison.OrdinalIgnoreCase) ? (ushort)7 : (ushort)11;
                var kind = ClassifyMediaKind(0, busType, uint.MaxValue, model);
                double? temp = TryReadTemperatureViaIoctl(idx);

                target.Add(new PhysicalDiskInfo(
                    DiskNumber: idx,
                    Model: model,
                    SerialNumber: serial,
                    BusType: iface,
                    MediaKind: kind,
                    SizeBytes: size,
                    PartitionStyle: "GPT",
                    DriveLetters: [],
                    Smart: new SmartSummary(SmartHealthStatus.Unknown, temp, null, null, null, null)));
            }
        }
        catch
        {
            // ignore
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
            handle = CreateFileW(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
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
                IntPtr.Zero) || bytesReturned < 32)
            {
                return null;
            }

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

    private static bool TryExtractDriveLetter(object? raw, out char letter)
    {
        letter = '\0';
        if (raw is char c)
        {
            char upper = char.ToUpperInvariant(c);
            if (upper is >= 'A' and <= 'Z')
            {
                letter = upper;
                return true;
            }
        }
        else if (raw is ushort u and >= 'A' and <= 'z')
        {
            char upper = char.ToUpperInvariant((char)u);
            if (upper is >= 'A' and <= 'Z')
            {
                letter = upper;
                return true;
            }
        }
        else if (raw is string s && !string.IsNullOrWhiteSpace(s))
        {
            char upper = char.ToUpperInvariant(s.Trim()[0]);
            if (upper is >= 'A' and <= 'Z')
            {
                letter = upper;
                return true;
            }
        }

        return false;
    }

    private static ushort TryConvertUInt16(object? val, ushort fallback = 0)
    {
        if (val is null)
        {
            return fallback;
        }

        try
        {
            return Convert.ToUInt16(val, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static uint TryConvertUInt32(object? val, uint fallback = 0)
    {
        if (val is null)
        {
            return fallback;
        }

        try
        {
            return Convert.ToUInt32(val, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static double? TryReadNullableDouble(object? val)
    {
        if (val is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(val, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static int? TryReadNullableInt(object? val)
    {
        if (val is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(val, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static long? TryReadNullableLong(object? val)
    {
        if (val is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(val, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeSpaces(string input) =>
        string.Join(' ', input.Split(' ', StringSplitOptions.RemoveEmptyEntries));

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
