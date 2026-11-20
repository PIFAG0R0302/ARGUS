using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Argus.Core.Contracts.Migration;
using Microsoft.Win32;

namespace Argus.Migration.Engine;

/// <summary>
/// Native compiled Windows implementation of all CopyOS host operations.
/// Uses Win32 APIs, WMI, NtfsVssCloner, and standard System32 utilities directly without PowerShell scripts.
/// </summary>
public sealed partial class WindowsMigrationHostOperations : IMigrationHostOperations
{
    private const string SystemHiveMountName = "ARGUS_CLONE_SYS";
    private const string UserHiveMountName = "ARGUS_CLONE_USR";
    private const string MsrGptType = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
    private const string EfiGptType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";

    internal readonly record struct DiskPartitionSnapshot(
        int PartitionNumber,
        char? DriveLetter,
        string? GptType,
        ushort? MbrType,
        long SizeBytes,
        long OffsetBytes,
        Guid? PartitionGuid,
        string? VolumeGuidPath);

    private static readonly char[] DriveLetterCandidates =
    [
        'Z', 'Y', 'X', 'W', 'V', 'U', 'T', 'S', 'R', 'Q',
        'P', 'O', 'N', 'M', 'L', 'K', 'J', 'I', 'H', 'G'
    ];

    private static readonly HashSet<string> SkippedProfileFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Public", "Default", "Default User", "All Users"
    };

    private static readonly (string ValueName, string DefaultExpand, string DefaultSuffix, bool CopyIfRedirected)[] UsbShellFolders =
    [
        ("Desktop", @"%USERPROFILE%\Desktop", "Desktop", true),
        ("Personal", @"%USERPROFILE%\Documents", "Documents", false),
        ("{374DE290-123F-4565-9164-39C4925E467B}", @"%USERPROFILE%\Downloads", "Downloads", false),
        ("My Pictures", @"%USERPROFILE%\Pictures", "Pictures", false),
        ("My Music", @"%USERPROFILE%\Music", "Music", false),
        ("My Video", @"%USERPROFILE%\Videos", "Videos", false),
        ("{AB5FB87B-7CE2-4F83-915D-550846C9537B}", @"%USERPROFILE%\Pictures\Camera Roll", @"Pictures\Camera Roll", false)
    ];

    [GeneratedRegex(@"\b(?<pct>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex DismPercentRegex();

    public Task<SourceOsInfo> InspectLiveOsAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => InspectLiveOsCore(cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<MigrationTargetDisk>> DiscoverTargetDisksAsync(
        MigrationProfile profile,
        int sourceDiskNumber,
        char systemDriveLetter,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => DiscoverTargetDisksCore(profile, sourceDiskNumber, systemDriveLetter, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<LocalScratchVolume>> GetLocalScratchVolumesAsync(CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<LocalScratchVolume>>(() =>
        {
            var result = new List<LocalScratchVolume>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.Name.Length < 1)
                    {
                        continue;
                    }

                    result.Add(new LocalScratchVolume(
                        char.ToUpperInvariant(drive.Name[0]),
                        drive.AvailableFreeSpace,
                        drive.DriveFormat));
                }
                catch
                {
                    // Skip unmounted/inaccessible drives
                }
            }

            return result;
        }, cancellationToken);
    }

    public (char BootLetter, char OsLetter) AllocateFreeDriveLetters()
    {
        var used = new HashSet<char>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.Name.Length > 0)
            {
                used.Add(char.ToUpperInvariant(d.Name[0]));
            }
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT DeviceID FROM Win32_LogicalDisk");
            foreach (ManagementObject obj in searcher.Get())
            {
                string? id = obj["DeviceID"]?.ToString();
                if (!string.IsNullOrEmpty(id))
                {
                    used.Add(char.ToUpperInvariant(id[0]));
                }
            }
        }
        catch
        {
            // DriveInfo already collected active letters
        }

        var free = new List<char>(2);
        foreach (char candidate in DriveLetterCandidates)
        {
            if (!used.Contains(candidate))
            {
                free.Add(candidate);
                if (free.Count == 2)
                {
                    return (free[0], free[1]);
                }
            }
        }

        throw new InvalidOperationException("Не знайдено 2 вільні літери диска в діапазоні G..Z.");
    }

    public async Task<VssSnapshotHandle> CreateVssSnapshotAsync(
        char systemDriveLetter,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string sysRoot = $"{char.ToUpperInvariant(systemDriveLetter)}:\\";
        log($"Запуск служби VSS та створення тіньового знімка для {sysRoot}...");

        // Ensure VSS service is running
        _ = await RunProcessAsync(
            Path.Combine(Environment.SystemDirectory, "sc.exe"),
            "start vss",
            null,
            cancellationToken).ConfigureAwait(false);

        return await Task.Run(() =>
        {
            using var shadowClass = new ManagementClass(@"\\.\root\cimv2", "Win32_ShadowCopy", null);
            using ManagementBaseObject inParams = shadowClass.GetMethodParameters("Create");
            inParams["Volume"] = sysRoot;
            inParams["Context"] = "ClientAccessible";

            using ManagementBaseObject outParams = shadowClass.InvokeMethod("Create", inParams, null);
            uint retVal = Convert.ToUInt32(outParams["ReturnValue"], CultureInfo.InvariantCulture);
            if (retVal != 0)
            {
                throw new InvalidOperationException($"Не вдалося створити VSS-знімок для {sysRoot} (код {retVal}).");
            }

            string shadowId = outParams["ShadowID"]?.ToString()
                ?? throw new InvalidOperationException("VSS повернув порожній ShadowID.");

            string? deviceObject = null;
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\cimv2",
                $"SELECT ID, DeviceObject FROM Win32_ShadowCopy WHERE ID = '{shadowId.Replace("'", "''", StringComparison.Ordinal)}'");

            foreach (ManagementObject obj in searcher.Get())
            {
                deviceObject = obj["DeviceObject"]?.ToString()?.TrimEnd('\\');
                break;
            }

            if (string.IsNullOrWhiteSpace(deviceObject))
            {
                throw new InvalidOperationException($"VSS-знімок {shadowId} створено, але не знайдено через WMI.");
            }

            log($"VSS-знімок готовий: {shadowId} ({deviceObject})");
            return new VssSnapshotHandle(shadowId, deviceObject);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteVssSnapshotAsync(string shadowId, Action<string> log)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(shadowId))
            {
                return;
            }

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"\\.\root\cimv2",
                    $"SELECT * FROM Win32_ShadowCopy WHERE ID = '{shadowId.Replace("'", "''", StringComparison.Ordinal)}'");

                foreach (ManagementObject obj in searcher.Get())
                {
                    obj.Delete();
                    log($"Тимчасовий VSS-знімок {shadowId} видалено.");
                }
            }
            catch (Exception ex)
            {
                log($"Попередження під час видалення VSS {shadowId}: {ex.Message}");
            }
        });
    }

    public async Task<PartitionedTargetInfo> PartitionAndPrepareTargetAsync(
        MigrationPlanRequest request,
        SourceOsInfo source,
        MigrationTargetDisk target,
        CloneMode resolvedMode,
        char bootLetter,
        char osLetter,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        int diskNum = request.TargetDiskNumber;
        bool isGpt = request.PartitionStyle == PartitionStyleChoice.GptUefi;
        string bootLabel = request.Profile == MigrationProfile.ExternalUsbDrive ? "ESP_USB" : "ESP_SYS";
        string mbrBootLabel = request.Profile == MigrationProfile.ExternalUsbDrive ? "BOOT_USB" : "BOOT_SYS";
        string osLabel = request.Profile == MigrationProfile.ExternalUsbDrive ? "WinUSB" : "WindowsSSD";

        long osSizeMiB = Math.Max(1024, (source.PartitionSizeBytes + (1024L * 1024L - 1)) / (1024L * 1024L) + 4);

        log($"Розмітка Диска {diskNum} ({(isGpt ? "GPT/UEFI" : "MBR/Legacy")}, режим {resolvedMode})...");

        // Step 1: Clean and initialize disk partition table first.
        // On Windows 10/11, `convert gpt` automatically creates a 16 MB MSR partition as Partition 1;
        // detect and remove any auto-created partitions before laying out ESP + MSR + OS so numbering stays canonical.
        string initScript = BuildDiskpartInitScript(diskNum, isGpt);
        await RunDiskpartScriptAsync(initScript, log, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DiskPartitionSnapshot> autoCreatedPartitions = [];
        if (isGpt)
        {
            for (int check = 0; check < 3; check++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(check == 0 ? 700 : 400, cancellationToken).ConfigureAwait(false);
                RefreshDiskStorageCache(diskNum);
                autoCreatedPartitions = QueryDiskPartitions(diskNum);
                if (autoCreatedPartitions.Count > 0)
                {
                    break;
                }
            }

            if (autoCreatedPartitions.Count > 0)
            {
                string partNums = string.Join(", ", autoCreatedPartitions.Select(static p => p.PartitionNumber));
                log($"Видалення автоматично створених після convert gpt розділів ({partNums}) для чистої схеми ESP + MSR + OS...");
            }
        }
        else
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        // Step 2: Remove any auto-created partitions (descending order) and create target Boot / MSR / OS partitions.
        string layoutScript = BuildDiskpartLayoutScript(
            diskNum,
            isGpt,
            resolvedMode,
            bootLabel,
            mbrBootLabel,
            osLabel,
            bootLetter,
            osLetter,
            osSizeMiB,
            autoCreatedPartitions.Select(static p => p.PartitionNumber).ToArray());

        await RunDiskpartScriptAsync(layoutScript, log, cancellationToken).ConfigureAwait(false);
        await Task.Delay(1200, cancellationToken).ConfigureAwait(false);

        // Step 3: Dynamically discover the actual Boot and OS partition numbers from MSFT_Partition
        // rather than relying on hardcoded partition indices.
        DiskPartitionSnapshot? resolvedBoot = null;
        DiskPartitionSnapshot? resolvedOs = null;
        IReadOnlyList<DiskPartitionSnapshot> lastPartitions = [];
        uint mbrSignature = 0;
        var strippedAutoLetters = new HashSet<char>();

        for (int attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt == 0 || attempt % 4 == 0)
            {
                RefreshDiskStorageCache(diskNum);
            }

            lastPartitions = QueryDiskPartitions(diskNum);
            mbrSignature = QueryMbrDiskSignature(diskNum);

            var resolved = ResolveTargetPartitions(
                lastPartitions,
                request.PartitionStyle,
                resolvedMode,
                bootLetter,
                osLetter,
                source.PartitionSizeBytes);

            if (resolved is not null)
            {
                var (bootPart, osPart) = resolved.Value;
                resolvedBoot = bootPart;
                resolvedOs = osPart;

                if (resolvedMode == CloneMode.BlockVssBitmap &&
                    osPart.DriveLetter is char autoLetter &&
                    strippedAutoLetters.Add(autoLetter))
                {
                    log($"Зняття автоматично призначеної літери {autoLetter}: з цільового розділу {osPart.PartitionNumber} перед блоковим клонуванням...");
                    await RunDiskpartScriptAsync(
                        $"select disk {diskNum}\r\nselect partition {osPart.PartitionNumber}\r\nremove letter={autoLetter} noerr\r\n",
                        log,
                        cancellationToken).ConfigureAwait(false);
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (resolvedMode != CloneMode.BlockVssBitmap || !string.IsNullOrWhiteSpace(osPart.VolumeGuidPath))
                {
                    break;
                }
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        if (resolvedBoot is null || resolvedOs is null)
        {
            string layoutSummary = FormatPartitionSummary(lastPartitions);
            throw new InvalidOperationException(
                $"Не вдалося визначити цільові розділи Boot ({bootLetter}:) та OS на Диску {diskNum}. Знайдено розділи: {layoutSummary}.");
        }

        if (resolvedMode == CloneMode.BlockVssBitmap && string.IsNullOrWhiteSpace(resolvedOs.Value.VolumeGuidPath))
        {
            string layoutSummary = FormatPartitionSummary(lastPartitions);
            throw new InvalidOperationException(
                $"Не вдалося отримати Volume GUID для цільового розділу {resolvedOs.Value.PartitionNumber} на Диску {diskNum}. Знайдено розділи: {layoutSummary}.");
        }

        log(string.Format(
            CultureInfo.InvariantCulture,
            "Розмітку Диска {0} підтверджено: Boot=Розділ {1} ({2}:), OS=Розділ {3} ({4:N2} ГіБ, Volume={5}).",
            diskNum,
            resolvedBoot.Value.PartitionNumber,
            bootLetter,
            resolvedOs.Value.PartitionNumber,
            resolvedOs.Value.SizeBytes / (1024.0 * 1024.0 * 1024.0),
            resolvedOs.Value.VolumeGuidPath ?? "n/a"));

        return new PartitionedTargetInfo(
            diskNum,
            resolvedBoot.Value.PartitionNumber,
            resolvedOs.Value.PartitionNumber,
            bootLetter,
            osLetter,
            resolvedOs.Value.VolumeGuidPath,
            resolvedOs.Value.PartitionGuid,
            mbrSignature,
            resolvedOs.Value.OffsetBytes);
    }

    internal static string BuildDiskpartInitScript(int diskNum, bool isGpt)
    {
        var script = new StringBuilder();
        script.AppendLine(CultureInfo.InvariantCulture, $"select disk {diskNum}");
        script.AppendLine("online disk noerr");
        script.AppendLine("attributes disk clear readonly noerr");
        script.AppendLine("clean");
        script.AppendLine(isGpt ? "convert gpt" : "convert mbr");
        script.AppendLine("rescan");
        return script.ToString();
    }

    internal static string BuildDiskpartLayoutScript(
        int diskNum,
        bool isGpt,
        CloneMode resolvedMode,
        string bootLabel,
        string mbrBootLabel,
        string osLabel,
        char bootLetter,
        char osLetter,
        long osSizeMiB,
        IReadOnlyList<int> preExistingPartitionNumbers)
    {
        var script = new StringBuilder();
        script.AppendLine(CultureInfo.InvariantCulture, $"select disk {diskNum}");

        foreach (int partNum in preExistingPartitionNumbers.Where(static n => n > 0).Distinct().OrderByDescending(static n => n))
        {
            script.AppendLine(CultureInfo.InvariantCulture, $"select partition {partNum}");
            script.AppendLine("delete partition override");
        }

        if (isGpt)
        {
            // Create temporary basic data 100 MB FAT32 for bcdboot (converted to ESP GUID after bcdboot)
            script.AppendLine("create partition primary size=100");
            script.AppendLine(CultureInfo.InvariantCulture, $"format quick fs=fat32 label=\"{bootLabel}\"");
            script.AppendLine(CultureInfo.InvariantCulture, $"assign letter={bootLetter}");
            script.AppendLine("create partition msr size=16");

            if (resolvedMode == CloneMode.BlockVssBitmap)
            {
                script.AppendLine(CultureInfo.InvariantCulture, $"create partition primary size={osSizeMiB}");
            }
            else
            {
                script.AppendLine("create partition primary");
                script.AppendLine(CultureInfo.InvariantCulture, $"format quick fs=ntfs label=\"{osLabel}\"");
                script.AppendLine(CultureInfo.InvariantCulture, $"assign letter={osLetter}");
            }
        }
        else
        {
            script.AppendLine("create partition primary size=100");
            script.AppendLine(CultureInfo.InvariantCulture, $"format quick fs=ntfs label=\"{mbrBootLabel}\"");
            script.AppendLine("active");
            script.AppendLine(CultureInfo.InvariantCulture, $"assign letter={bootLetter}");

            if (resolvedMode == CloneMode.BlockVssBitmap)
            {
                script.AppendLine(CultureInfo.InvariantCulture, $"create partition primary size={osSizeMiB}");
            }
            else
            {
                script.AppendLine("create partition primary");
                script.AppendLine(CultureInfo.InvariantCulture, $"format quick fs=ntfs label=\"{osLabel}\"");
                script.AppendLine(CultureInfo.InvariantCulture, $"assign letter={osLetter}");
            }
        }

        script.AppendLine("rescan");
        return script.ToString();
    }

    internal static (DiskPartitionSnapshot BootPartition, DiskPartitionSnapshot OsPartition)? ResolveTargetPartitions(
        IReadOnlyList<DiskPartitionSnapshot> partitions,
        PartitionStyleChoice partitionStyle,
        CloneMode resolvedMode,
        char bootLetter,
        char osLetter,
        long minimumOsSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        if (partitions.Count == 0)
        {
            return null;
        }

        char bootUpper = char.ToUpperInvariant(bootLetter);
        char osUpper = char.ToUpperInvariant(osLetter);

        var bootCandidates = partitions
            .Where(p => p.DriveLetter.HasValue && char.ToUpperInvariant(p.DriveLetter.Value) == bootUpper)
            .ToList();

        if (bootCandidates.Count == 0)
        {
            return null;
        }

        DiskPartitionSnapshot bootPart = bootCandidates[0];

        static bool IsReservedPartitionType(DiskPartitionSnapshot p) =>
            string.Equals(p.GptType, MsrGptType, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.GptType, EfiGptType, StringComparison.OrdinalIgnoreCase);

        var dataCandidates = partitions
            .Where(p => p.PartitionNumber != bootPart.PartitionNumber && !IsReservedPartitionType(p))
            .ToList();

        if (dataCandidates.Count == 0)
        {
            return null;
        }

        DiskPartitionSnapshot osPart;
        if (resolvedMode == CloneMode.DismShrinkToFit)
        {
            var letterMatches = dataCandidates
                .Where(p => p.DriveLetter.HasValue && char.ToUpperInvariant(p.DriveLetter.Value) == osUpper)
                .ToList();

            if (letterMatches.Count == 0)
            {
                return null;
            }

            osPart = letterMatches[0];
        }
        else
        {
            var sizeFitting = dataCandidates
                .Where(p => p.SizeBytes >= minimumOsSizeBytes)
                .OrderByDescending(static p => p.SizeBytes)
                .ThenByDescending(static p => p.PartitionNumber)
                .ToList();

            if (sizeFitting.Count == 0)
            {
                return null;
            }

            osPart = sizeFitting[0];
        }

        if (partitionStyle == PartitionStyleChoice.GptUefi &&
            (osPart.PartitionGuid is null || osPart.PartitionGuid == Guid.Empty))
        {
            return null;
        }

        return (bootPart, osPart);
    }

    public Task ExecuteBlockCloneAsync(
        SourceOsInfo source,
        PartitionedTargetInfo targetInfo,
        VssSnapshotHandle snapshot,
        Action<string> log,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            string sourceRoot = $"{char.ToUpperInvariant(source.SystemDriveLetter)}:\\";
            string targetVol = targetInfo.TargetVolumeGuidPath
                ?? throw new InvalidOperationException("TargetVolumeGuidPath відсутній для блокового клонування.");

            NtfsVssCloner.CloneVolume(
                sourceRoot,
                snapshot.DeviceObjectPath,
                targetVol,
                source.PartitionSizeBytes,
                log,
                progress,
                cancellationToken);
        }, cancellationToken);
    }

    public async Task<PartitionedTargetInfo> MountCheckAndExtendBlockVolumeAsync(
        MigrationProfile profile,
        PartitionedTargetInfo targetInfo,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string osLabel = profile == MigrationProfile.ExternalUsbDrive ? "WinUSB" : "WindowsSSD";
        log($"Монтування клонованого розділу як {targetInfo.OsDriveLetter}:...");

        var assignScript = new StringBuilder();
        assignScript.AppendLine("rescan");
        assignScript.AppendLine(CultureInfo.InvariantCulture, $"select disk {targetInfo.TargetDiskNumber}");
        assignScript.AppendLine(CultureInfo.InvariantCulture, $"select partition {targetInfo.OsPartitionNumber}");
        assignScript.AppendLine(CultureInfo.InvariantCulture, $"assign letter={targetInfo.OsDriveLetter}");
        await RunDiskpartScriptAsync(assignScript.ToString(), log, cancellationToken).ConfigureAwait(false);

        bool mounted = false;
        for (int i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists($"{targetInfo.OsDriveLetter}:\\Windows"))
            {
                mounted = true;
                break;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        if (!mounted)
        {
            throw new InvalidOperationException($"Не вдалося змонтувати клонований розділ як {targetInfo.OsDriveLetter}:\\.");
        }

        log($"Запуск chkdsk {targetInfo.OsDriveLetter}: /f /x...");
        var (chkdskCode, chkdskOut) = await RunProcessAsync(
            Path.Combine(Environment.SystemDirectory, "chkdsk.exe"),
            $"{targetInfo.OsDriveLetter}: /f /x",
            null,
            cancellationToken).ConfigureAwait(false);

        log($"chkdsk завершено з кодом {chkdskCode}.");
        if (chkdskCode >= 3)
        {
            throw new InvalidOperationException($"chkdsk {targetInfo.OsDriveLetter}: /f /x завершився з помилкою {chkdskCode}: {chkdskOut}");
        }

        log($"Розширення розділу {targetInfo.OsDriveLetter}: на весь обсяг цільового диска...");
        var extendScript = new StringBuilder();
        extendScript.AppendLine(CultureInfo.InvariantCulture, $"select disk {targetInfo.TargetDiskNumber}");
        extendScript.AppendLine(CultureInfo.InvariantCulture, $"select partition {targetInfo.OsPartitionNumber}");
        extendScript.AppendLine("extend noerr");
        extendScript.AppendLine(CultureInfo.InvariantCulture, $"select volume {targetInfo.OsDriveLetter}");
        extendScript.AppendLine("extend filesystem noerr");
        await RunDiskpartScriptAsync(extendScript.ToString(), log, cancellationToken).ConfigureAwait(false);

        try
        {
            var di = new DriveInfo($"{targetInfo.OsDriveLetter}:\\");
            di.VolumeLabel = osLabel;
        }
        catch
        {
            // Non-fatal label update
        }

        var refreshed = QueryPartitionMetadata(targetInfo.TargetDiskNumber, targetInfo.OsPartitionNumber);
        if (refreshed is not null)
        {
            return targetInfo with
            {
                OsPartitionGuid = refreshed.Value.PartitionGuid ?? targetInfo.OsPartitionGuid,
                MbrDiskSignature = refreshed.Value.MbrSignature != 0 ? refreshed.Value.MbrSignature : targetInfo.MbrDiskSignature,
                OsPartitionOffset = refreshed.Value.Offset > 0 ? refreshed.Value.Offset : targetInfo.OsPartitionOffset
            };
        }

        return targetInfo;
    }

    public async Task ExecuteDismCaptureAndApplyAsync(
        MigrationProfile profile,
        SourceOsInfo source,
        PartitionedTargetInfo targetInfo,
        VssSnapshotHandle snapshot,
        char scratchDriveLetter,
        MigrationCleanupState cleanupState,
        Action<string> log,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        int pid = Environment.ProcessId;
        string tempWorkDir = $"{char.ToUpperInvariant(scratchDriveLetter)}:\\_ArgusClone_Temp_{pid}";
        string scratchDir = Path.Combine(tempWorkDir, "Scratch");
        string wimPath = Path.Combine(tempWorkDir, "install.wim");
        string wimCfg = Path.Combine(tempWorkDir, "WimScript.ini");
        string vssJunction = $"{char.ToUpperInvariant(source.SystemDriveLetter)}:\\_ArgusClone_VSS_{pid}";

        Directory.CreateDirectory(scratchDir);
        cleanupState.TempWorkDirectory = tempWorkDir;

        await File.WriteAllTextAsync(wimCfg, MigrationPlanEvaluator.BuildWimExclusionScript(), Encoding.ASCII, cancellationToken)
            .ConfigureAwait(false);

        log($"Створення тимчасового VSS-посилання {vssJunction} -> {snapshot.DeviceObjectPath}\\...");
        Directory.CreateSymbolicLink(vssJunction, snapshot.DeviceObjectPath + "\\");
        cleanupState.VssJunctionPath = vssJunction;

        string dismExe = Path.Combine(Environment.SystemDirectory, "dism.exe");
        string imageName = profile == MigrationProfile.ExternalUsbDrive ? "PortableWindows" : "WindowsSSD";

        log($"Захоплення WIM-образу через DISM (/Compress:fast) у {wimPath}...");
        string captureArgs = $"/Capture-Image /ImageFile:\"{wimPath}\" /CaptureDir:\"{vssJunction}\" /Name:\"{imageName}\" /Compress:fast /ConfigFile:\"{wimCfg}\" /ScratchDir:\"{scratchDir}\"";
        var (capExit, capOut) = await RunProcessAsync(
            dismExe,
            captureArgs,
            line => ReportDismProgress(line, 20, 48, "DISM: захоплення образу ОС", source.UsedBytes, progress),
            cancellationToken).ConfigureAwait(false);

        if (capExit != 0)
        {
            throw new InvalidOperationException($"DISM /Capture-Image завершився з кодом {capExit}: {capOut}");
        }

        log($"Розгортання WIM-образу на {targetInfo.OsDriveLetter}:\\ через DISM...");
        string applyArgs = $"/Apply-Image /ImageFile:\"{wimPath}\" /Index:1 /ApplyDir:\"{targetInfo.OsDriveLetter}:\\\" /ScratchDir:\"{scratchDir}\"";
        var (appExit, appOut) = await RunProcessAsync(
            dismExe,
            applyArgs,
            line => ReportDismProgress(line, 48, 76, "DISM: запис образу на цільовий диск", source.UsedBytes, progress),
            cancellationToken).ConfigureAwait(false);

        if (appExit != 0)
        {
            throw new InvalidOperationException($"DISM /Apply-Image завершився з кодом {appExit}: {appOut}");
        }
    }

    public async Task FixRedirectedUserFoldersAsync(
        MigrationPlanRequest request,
        SourceOsInfo source,
        char osLetter,
        MigrationCleanupState cleanupState,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        bool isUsb = request.Profile == MigrationProfile.ExternalUsbDrive;
        if (!isUsb && !request.CopyRedirectedDesktop)
        {
            return;
        }

        string usersRoot = $"{osLetter}:\\Users";
        if (!Directory.Exists(usersRoot))
        {
            return;
        }

        string regExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
        string robocopyExe = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
        char sysDrive = char.ToUpperInvariant(source.SystemDriveLetter);

        foreach (string userDir in Directory.EnumerateDirectories(usersRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string userName = Path.GetFileName(userDir);
            if (string.IsNullOrWhiteSpace(userName) || SkippedProfileFolders.Contains(userName))
            {
                continue;
            }

            string ntuser = Path.Combine(userDir, "NTUSER.DAT");
            if (!File.Exists(ntuser))
            {
                continue;
            }

            var (loadExit, _) = await RunProcessAsync(
                regExe,
                $"load HKLM\\{UserHiveMountName} \"{ntuser}\"",
                null,
                cancellationToken).ConfigureAwait(false);

            if (loadExit != 0)
            {
                log($"Не вдалося завантажити NTUSER.DAT для «{userName}», пропуск.");
                continue;
            }

            cleanupState.UserHiveLoaded = true;
            try
            {
                using RegistryKey? usfKey = Registry.LocalMachine.OpenSubKey(
                    $@"{UserHiveMountName}\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                    writable: true);
                using RegistryKey? sfKey = Registry.LocalMachine.OpenSubKey(
                    $@"{UserHiveMountName}\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
                    writable: true);

                if (usfKey is not null)
                {
                    var foldersToCheck = isUsb
                        ? UsbShellFolders
                        : [UsbShellFolders[0]]; // Desktop only for internal opt-in

                    foreach (var (valName, defExpand, defSuffix, shouldCopyDesktop) in foldersToCheck)
                    {
                        string? rawVal = usfKey.GetValue(valName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                        if (string.IsNullOrWhiteSpace(rawVal))
                        {
                            continue;
                        }

                        string expanded = rawVal.Replace("%USERPROFILE%", $@"{sysDrive}:\Users\{userName}", StringComparison.OrdinalIgnoreCase);
                        if (expanded.Length < 3 || expanded[1] != ':' || char.ToUpperInvariant(expanded[0]) == sysDrive)
                        {
                            continue;
                        }

                        if (shouldCopyDesktop && Directory.Exists(expanded))
                        {
                            long srcBytes = ComputeDirectorySizeSafe(expanded, cancellationToken);
                            long freeBytes = new DriveInfo($"{osLetter}:\\").AvailableFreeSpace;
                            if (!isUsb || srcBytes < freeBytes - (4L * 1024 * 1024 * 1024))
                            {
                                string dstDesktop = Path.Combine(userDir, "Desktop");
                                Directory.CreateDirectory(dstDesktop);
                                log($"Копіювання перенесеного Робочого столу «{expanded}» -> «{dstDesktop}»...");
                                _ = await RunProcessAsync(
                                    robocopyExe,
                                    $"\"{expanded}\" \"{dstDesktop}\" /E /COPY:DAT /XJ /R:1 /W:1 /MT:8 /NP /NFL /NDL",
                                    null,
                                    cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                log($"Перенесений Робочий стіл «{expanded}» завеликий для вільного місця на USB — скидаємо лише шлях.");
                            }
                        }

                        usfKey.SetValue(valName, defExpand, RegistryValueKind.ExpandString);
                        if (sfKey is not null && (valName == "Desktop" || valName == "Personal"))
                        {
                            sfKey.SetValue(valName, $@"{sysDrive}:\Users\{userName}\{defSuffix}", RegistryValueKind.String);
                        }

                        log($"Виправлено шлях теки «{valName}» для «{userName}»: «{rawVal}» -> «{defExpand}».");
                    }
                }
            }
            finally
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                _ = await RunProcessAsync(regExe, $"unload HKLM\\{UserHiveMountName}", null, CancellationToken.None)
                    .ConfigureAwait(false);
                cleanupState.UserHiveLoaded = false;
            }
        }
    }

    public async Task UpdateOfflineSystemRegistryAsync(
        MigrationPlanRequest request,
        SourceOsInfo source,
        PartitionedTargetInfo targetInfo,
        MigrationCleanupState cleanupState,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string regExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
        string sysHivePath = $"{targetInfo.OsDriveLetter}:\\Windows\\System32\\config\\SYSTEM";
        char sysDrive = char.ToUpperInvariant(source.SystemDriveLetter);

        log($"Завантаження офлайн-реєстру SYSTEM ({sysHivePath})...");
        var (loadExit, loadOut) = await RunProcessAsync(
            regExe,
            $"load HKLM\\{SystemHiveMountName} \"{sysHivePath}\"",
            null,
            cancellationToken).ConfigureAwait(false);

        if (loadExit != 0)
        {
            throw new InvalidOperationException($"Не вдалося завантажити офлайн-реєстр SYSTEM: {loadOut}");
        }

        cleanupState.SystemHiveLoaded = true;
        try
        {
            using RegistryKey sysRoot = Registry.LocalMachine.OpenSubKey(SystemHiveMountName, writable: true)
                ?? throw new InvalidOperationException($"Не вдалося відкрити HKLM\\{SystemHiveMountName}.");

            // 1. Rebind MountedDevices \DosDevices\C: to the new target partition
            using (RegistryKey mounted = sysRoot.CreateSubKey("MountedDevices", writable: true))
            {
                foreach (string existingVal in mounted.GetValueNames())
                {
                    mounted.DeleteValue(existingVal, throwOnMissingValue: false);
                }

                byte[] idBytes = MigrationPlanEvaluator.BuildMountedDevicesBinary(
                    request.PartitionStyle,
                    targetInfo.OsPartitionGuid,
                    targetInfo.MbrDiskSignature,
                    targetInfo.OsPartitionOffset);

                mounted.SetValue($@"\DosDevices\{sysDrive}:", idBytes, RegistryValueKind.Binary);
                log($"Оновлено MountedDevices \\DosDevices\\{sysDrive}: ({Convert.ToHexString(idBytes)}).");
            }

            // 2. HardwareConfig\*\BootDriverFlags = 28 (0x1C)
            using (RegistryKey? hwRoot = sysRoot.OpenSubKey("HardwareConfig", writable: true))
            {
                if (hwRoot is not null)
                {
                    foreach (string sub in hwRoot.GetSubKeyNames())
                    {
                        using RegistryKey? subKey = hwRoot.OpenSubKey(sub, writable: true);
                        subKey?.SetValue("BootDriverFlags", 28, RegistryValueKind.DWord);
                    }
                }
            }

            // 3. ControlSet* boot-start storage or USB/UASP drivers
            bool isUsb = request.Profile == MigrationProfile.ExternalUsbDrive;
            IReadOnlyList<string> bootDrivers = isUsb
                ? MigrationPlanEvaluator.ExternalUsbBootDrivers
                : MigrationPlanEvaluator.InternalBootDrivers;

            foreach (string subName in sysRoot.GetSubKeyNames())
            {
                if (!subName.StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using (RegistryKey controlKey = sysRoot.CreateSubKey($@"{subName}\Control", writable: true))
                {
                    controlKey.SetValue("BootDriverFlags", 28, RegistryValueKind.DWord);
                    if (isUsb)
                    {
                        using RegistryKey pnpKey = controlKey.CreateSubKey("PnP", writable: true);
                        pnpKey.SetValue("PollBootPartitionTimeout", 30_000, RegistryValueKind.DWord);
                    }
                }

                foreach (string drv in bootDrivers)
                {
                    using RegistryKey? svcKey = sysRoot.OpenSubKey($@"{subName}\Services\{drv}", writable: true);
                    if (svcKey is null)
                    {
                        continue;
                    }

                    svcKey.SetValue("Start", 0, RegistryValueKind.DWord);
                    try
                    {
                        svcKey.DeleteSubKeyTree("StartOverride", throwOnMissingSubKey: false);
                    }
                    catch
                    {
                        // Ignore if absent
                    }
                }

                log($"Налаштовано драйвери завантаження ({(isUsb ? "USB/UASP" : "SATA/NVMe/RAID")}) у {subName}.");
            }
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var (unloadExit, unloadOut) = await RunProcessAsync(
                regExe,
                $"unload HKLM\\{SystemHiveMountName}",
                null,
                CancellationToken.None).ConfigureAwait(false);

            if (unloadExit == 0)
            {
                cleanupState.SystemHiveLoaded = false;
            }
            else
            {
                log($"Попередження вивантаження HKLM\\{SystemHiveMountName}: {unloadOut}");
            }
        }
    }

    public async Task InstallBootloaderAsync(
        MigrationPlanRequest request,
        SourceOsInfo source,
        PartitionedTargetInfo targetInfo,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string bcdbootExe = Path.Combine(Environment.SystemDirectory, "bcdboot.exe");
        string bcdeditExe = Path.Combine(Environment.SystemDirectory, "bcdedit.exe");
        string bootsectExe = Path.Combine(Environment.SystemDirectory, "bootsect.exe");

        bool isUsb = request.Profile == MigrationProfile.ExternalUsbDrive;
        bool isGpt = request.PartitionStyle == PartitionStyleChoice.GptUefi;
        char bootLetter = targetInfo.BootDriveLetter;
        char osLetter = targetInfo.OsDriveLetter;
        string desc = isUsb
            ? (isGpt ? "Windows Portable (USB SSD)" : "Windows Portable (USB)")
            : "Windows (New Internal SSD)";

        if (isGpt)
        {
            var (_, helpText) = await RunProcessAsync(bcdbootExe, "/?", null, cancellationToken).ConfigureAwait(false);
            string noFwFlag = helpText.Contains("/nofirmwaresync", StringComparison.OrdinalIgnoreCase)
                ? " /nofirmwaresync"
                : string.Empty;

            var (bcdExit, bcdOut) = await RunProcessAsync(
                bcdbootExe,
                $"{osLetter}:\\Windows /s {bootLetter}: /f UEFI{noFwFlag}",
                null,
                cancellationToken).ConfigureAwait(false);

            if (bcdExit != 0)
            {
                throw new InvalidOperationException($"bcdboot (UEFI) помилка ({bcdExit}): {bcdOut}");
            }

            string fallbackBoot = $"{bootLetter}:\\EFI\\Boot\\bootx64.efi";
            string msBoot = $"{bootLetter}:\\EFI\\Microsoft\\Boot\\bootmgfw.efi";
            if (!File.Exists(fallbackBoot) && File.Exists(msBoot))
            {
                Directory.CreateDirectory($"{bootLetter}:\\EFI\\Boot");
                File.Copy(msBoot, fallbackBoot, overwrite: true);
            }

            string bcdStore = $"{bootLetter}:\\EFI\\Microsoft\\Boot\\BCD";
            await ConfigureBcdStoreAsync(bcdeditExe, bcdStore, desc, includeBootMgr: true, cancellationToken).ConfigureAwait(false);

            // Convert boot partition to official EFI System Partition GUID
            await RunDiskpartScriptAsync(
                $"select disk {targetInfo.TargetDiskNumber}\r\nselect partition {targetInfo.BootPartitionNumber}\r\nset id=c12a7328-f81f-11d2-ba4b-00a0c93ec93b override\r\n",
                log,
                cancellationToken).ConfigureAwait(false);

            if (!isUsb && request.RegisterUefiBootPriorityFirst && source.IsUefiHost)
            {
                await RunDiskpartScriptAsync(
                    $"select disk {targetInfo.TargetDiskNumber}\r\nselect partition {targetInfo.BootPartitionNumber}\r\nassign letter={bootLetter}\r\n",
                    log,
                    cancellationToken).ConfigureAwait(false);

                _ = await RunProcessAsync(
                    bcdbootExe,
                    $"{osLetter}:\\Windows /s {bootLetter}: /f UEFI",
                    null,
                    cancellationToken).ConfigureAwait(false);

                await ConfigureBcdStoreAsync(bcdeditExe, bcdStore, desc, includeBootMgr: false, cancellationToken).ConfigureAwait(false);
                log("Зареєстровано новий внутрішній SSD першим у черзі UEFI NVRAM.");
            }

            await RunDiskpartScriptAsync(
                $"select disk {targetInfo.TargetDiskNumber}\r\nselect partition {targetInfo.BootPartitionNumber}\r\nremove letter={bootLetter} noerr\r\n",
                log,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var (bcdExit, bcdOut) = await RunProcessAsync(
                bcdbootExe,
                $"{osLetter}:\\Windows /s {bootLetter}: /f BIOS",
                null,
                cancellationToken).ConfigureAwait(false);

            if (bcdExit != 0)
            {
                throw new InvalidOperationException($"bcdboot (BIOS) помилка ({bcdExit}): {bcdOut}");
            }

            _ = await RunProcessAsync(bootsectExe, $"/nt60 {bootLetter}: /mbr", null, cancellationToken).ConfigureAwait(false);

            string bcdStore = $"{bootLetter}:\\Boot\\BCD";
            await ConfigureBcdStoreAsync(bcdeditExe, bcdStore, desc, includeBootMgr: false, cancellationToken).ConfigureAwait(false);

            await RunDiskpartScriptAsync(
                $"select disk {targetInfo.TargetDiskNumber}\r\nselect partition {targetInfo.BootPartitionNumber}\r\nactive\r\nremove letter={bootLetter} noerr\r\n",
                log,
                cancellationToken).ConfigureAwait(false);
        }

        log($"Завантажувач ({(isGpt ? "UEFI/GPT" : "Legacy BIOS/MBR")}) встановлено.");
    }

    public async Task CleanupResourcesAsync(MigrationCleanupState state, Action<string> log)
    {
        string regExe = Path.Combine(Environment.SystemDirectory, "reg.exe");

        if (state.SystemHiveLoaded)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            _ = await RunProcessAsync(regExe, $"unload HKLM\\{SystemHiveMountName}", null, CancellationToken.None).ConfigureAwait(false);
            state.SystemHiveLoaded = false;
        }

        if (state.UserHiveLoaded)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            _ = await RunProcessAsync(regExe, $"unload HKLM\\{UserHiveMountName}", null, CancellationToken.None).ConfigureAwait(false);
            state.UserHiveLoaded = false;
        }

        if (!string.IsNullOrWhiteSpace(state.VssJunctionPath) && Directory.Exists(state.VssJunctionPath))
        {
            try
            {
                Directory.Delete(state.VssJunctionPath, recursive: false);
            }
            catch (Exception ex)
            {
                log($"Попередження видалення VSS-посилання: {ex.Message}");
            }

            state.VssJunctionPath = null;
        }

        if (!string.IsNullOrWhiteSpace(state.TempWorkDirectory) && Directory.Exists(state.TempWorkDirectory))
        {
            try
            {
                Directory.Delete(state.TempWorkDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                log($"Попередження видалення тимчасової теки DISM: {ex.Message}");
            }

            state.TempWorkDirectory = null;
        }

        if (!string.IsNullOrWhiteSpace(state.ShadowId))
        {
            await DeleteVssSnapshotAsync(state.ShadowId, log).ConfigureAwait(false);
            state.ShadowId = null;
        }
    }

    private static SourceOsInfo InspectLiveOsCore(CancellationToken ct)
    {
        string sysDriveRaw = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        char sysLetter = char.ToUpperInvariant(sysDriveRaw.TrimEnd(':')[0]);

        string osCaption = $"Windows {Environment.OSVersion.Version}";
        try
        {
            using var osSearcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Caption FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in osSearcher.Get())
            {
                string? cap = obj["Caption"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(cap))
                {
                    osCaption = cap;
                    break;
                }
            }
        }
        catch
        {
            // fallback kept
        }

        int srcDiskNumber = 0;
        long srcPartSize = 0;
        long srcUsedBytes = 0;
        string srcDiskModel = "Системний накопичувач";
        string srcBusType = "SATA";
        int logicalSectorSize = 512;

        try
        {
            var di = new DriveInfo($"{sysLetter}:\\");
            if (di.IsReady)
            {
                srcPartSize = di.TotalSize;
                srcUsedBytes = Math.Max(0, di.TotalSize - di.AvailableFreeSpace);
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            using var partSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT DiskNumber, Size FROM MSFT_Partition WHERE DriveLetter = '{sysLetter}'");
            foreach (ManagementObject p in partSearcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                srcDiskNumber = Convert.ToInt32(p["DiskNumber"], CultureInfo.InvariantCulture);
                long sz = Convert.ToInt64(p["Size"], CultureInfo.InvariantCulture);
                if (sz > 0)
                {
                    srcPartSize = sz;
                }

                break;
            }

            using var diskSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT Number, FriendlyName, BusType, LogicalSectorSize FROM MSFT_Disk WHERE Number = {srcDiskNumber}");
            foreach (ManagementObject d in diskSearcher.Get())
            {
                srcDiskModel = d["FriendlyName"]?.ToString()?.Trim() ?? srcDiskModel;
                srcBusType = MapBusType(Convert.ToUInt16(d["BusType"], CultureInfo.InvariantCulture));
                int sec = Convert.ToInt32(d["LogicalSectorSize"], CultureInfo.InvariantCulture);
                if (sec >= 512)
                {
                    logicalSectorSize = sec;
                }

                break;
            }
        }
        catch
        {
            // fallback kept
        }

        bool isUefi = DetectUefiFirmware();
        string bitLockerStatus = DetectBitLockerStatus(sysLetter);

        return new SourceOsInfo(
            OsCaption: osCaption,
            SystemDriveLetter: sysLetter,
            SourceDiskNumber: srcDiskNumber,
            SourceDiskModel: srcDiskModel,
            SourceBusType: srcBusType,
            PartitionSizeBytes: srcPartSize,
            UsedBytes: srcUsedBytes,
            LogicalSectorSize: logicalSectorSize,
            Is64Bit: Environment.Is64BitOperatingSystem,
            IsUefiHost: isUefi,
            BitLockerStatus: bitLockerStatus);
    }

    private static IReadOnlyList<MigrationTargetDisk> DiscoverTargetDisksCore(
        MigrationProfile profile,
        int sourceDiskNumber,
        char systemDriveLetter,
        CancellationToken ct)
    {
        var results = new List<MigrationTargetDisk>();
        var mediaByDisk = new Dictionary<int, string>();

        try
        {
            using var physSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk");
            foreach (ManagementObject p in physSearcher.Get())
            {
                if (int.TryParse(p["DeviceId"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int devId))
                {
                    ushort mt = Convert.ToUInt16(p["MediaType"] ?? 0, CultureInfo.InvariantCulture);
                    mediaByDisk[devId] = mt switch
                    {
                        3 => "HDD",
                        4 => "SSD",
                        5 => "SCM",
                        _ => "Disk"
                    };
                }
            }
        }
        catch
        {
            // optional metadata
        }

        var lettersByDisk = new Dictionary<int, List<char>>();
        try
        {
            using var partSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DiskNumber, DriveLetter FROM MSFT_Partition");
            foreach (ManagementObject p in partSearcher.Get())
            {
                int dNum = Convert.ToInt32(p["DiskNumber"], CultureInfo.InvariantCulture);
                object? letterObj = p["DriveLetter"];
                if (letterObj is not null)
                {
                    char letter = Convert.ToChar(letterObj, CultureInfo.InvariantCulture);
                    if (letter is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                    {
                        char upper = char.ToUpperInvariant(letter);
                        if (!lettersByDisk.TryGetValue(dNum, out var list))
                        {
                            list = [];
                            lettersByDisk[dNum] = list;
                        }

                        if (!list.Contains(upper))
                        {
                            list.Add(upper);
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            using var diskSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT Number, FriendlyName, BusType, Size, LogicalSectorSize, PartitionStyle, IsBoot, IsSystem FROM MSFT_Disk");

            foreach (ManagementObject d in diskSearcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                int num = Convert.ToInt32(d["Number"], CultureInfo.InvariantCulture);
                bool isBoot = d["IsBoot"] is bool b && b;
                bool isSystem = d["IsSystem"] is bool s && s;
                long size = Convert.ToInt64(d["Size"] ?? 0L, CultureInfo.InvariantCulture);
                string bus = MapBusType(Convert.ToUInt16(d["BusType"] ?? 0, CultureInfo.InvariantCulture));

                if (num == sourceDiskNumber || isBoot || isSystem || size <= 0)
                {
                    continue;
                }

                if (!MigrationPlanEvaluator.IsBusEligible(profile, bus))
                {
                    continue;
                }

                IReadOnlyList<char> letters = lettersByDisk.TryGetValue(num, out var foundLetters)
                    ? foundLetters.OrderBy(static c => c).ToArray()
                    : [];

                if (letters.Contains(char.ToUpperInvariant(systemDriveLetter)))
                {
                    continue;
                }

                long usedDataBytes = 0;
                foreach (char l in letters)
                {
                    try
                    {
                        var di = new DriveInfo($"{l}:\\");
                        if (di.IsReady)
                        {
                            usedDataBytes += Math.Max(0, di.TotalSize - di.AvailableFreeSpace);
                        }
                    }
                    catch
                    {
                        // skip unreadable volume
                    }
                }

                string friendly = d["FriendlyName"]?.ToString()?.Trim() ?? $"Диск {num}";
                int sector = Math.Max(512, Convert.ToInt32(d["LogicalSectorSize"] ?? 512, CultureInfo.InvariantCulture));
                ushort styleCode = Convert.ToUInt16(d["PartitionStyle"] ?? 0, CultureInfo.InvariantCulture);
                string partStyle = styleCode switch
                {
                    1 => "MBR",
                    2 => "GPT",
                    _ => "RAW"
                };

                string media = mediaByDisk.TryGetValue(num, out var m)
                    ? m
                    : (profile == MigrationProfile.ExternalUsbDrive ? "USB" : "Disk");

                results.Add(new MigrationTargetDisk(
                    DiskNumber: num,
                    FriendlyName: friendly,
                    BusType: bus,
                    MediaType: media,
                    SizeBytes: size,
                    LogicalSectorSize: sector,
                    PartitionStyle: partStyle,
                    UsedDataBytes: usedDataBytes,
                    DriveLetters: letters));
            }
        }
        catch
        {
            // storage WMI unavailable
        }

        return results.OrderBy(static d => d.DiskNumber).ToArray();
    }

    private static void RefreshDiskStorageCache(int diskNumber)
    {
        try
        {
            using var diskSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}");
            foreach (ManagementObject d in diskSearcher.Get())
            {
                _ = d.InvokeMethod("Refresh", null);
                break;
            }
        }
        catch
        {
            // best-effort storage cache refresh
        }
    }

    private static uint QueryMbrDiskSignature(int diskNumber)
    {
        try
        {
            using var diskSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT Signature FROM MSFT_Disk WHERE Number = {diskNumber}");
            foreach (ManagementObject d in diskSearcher.Get())
            {
                if (d["Signature"] is not null)
                {
                    return Convert.ToUInt32(d["Signature"], CultureInfo.InvariantCulture);
                }

                break;
            }
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private static IReadOnlyList<DiskPartitionSnapshot> QueryDiskPartitions(int diskNumber)
    {
        var result = new List<DiskPartitionSnapshot>();
        try
        {
            using var partSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT PartitionNumber, DriveLetter, GptType, MbrType, Size, Offset, Guid, AccessPaths FROM MSFT_Partition WHERE DiskNumber = {diskNumber}");

            foreach (ManagementObject p in partSearcher.Get())
            {
                int partNum = p["PartitionNumber"] is not null
                    ? Convert.ToInt32(p["PartitionNumber"], CultureInfo.InvariantCulture)
                    : 0;
                if (partNum <= 0)
                {
                    continue;
                }

                char? driveLetter = ParseDriveLetter(p["DriveLetter"]);
                string? gptType = p["GptType"]?.ToString();
                ushort? mbrType = p["MbrType"] is not null
                    ? Convert.ToUInt16(p["MbrType"], CultureInfo.InvariantCulture)
                    : null;
                long size = p["Size"] is not null
                    ? Convert.ToInt64(p["Size"], CultureInfo.InvariantCulture)
                    : 0L;
                long offset = p["Offset"] is not null
                    ? Convert.ToInt64(p["Offset"], CultureInfo.InvariantCulture)
                    : 0L;
                Guid? partGuid = Guid.TryParse(p["Guid"]?.ToString(), out var g) ? g : null;

                string? volGuid = null;
                if (p["AccessPaths"] is string[] paths)
                {
                    volGuid = paths
                        .FirstOrDefault(static x => x.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
                        ?.TrimEnd('\\');
                }

                result.Add(new DiskPartitionSnapshot(
                    partNum,
                    driveLetter,
                    gptType,
                    mbrType,
                    size,
                    offset,
                    partGuid,
                    volGuid));
            }
        }
        catch
        {
            // retry caller loop
        }

        return result.OrderBy(static p => p.PartitionNumber).ToArray();
    }

    private static char? ParseDriveLetter(object? letterObj)
    {
        if (letterObj is null)
        {
            return null;
        }

        char letter = letterObj switch
        {
            char c => c,
            string s when s.Length > 0 => s[0],
            _ => Convert.ToChar(letterObj, CultureInfo.InvariantCulture)
        };

        return letter is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            ? char.ToUpperInvariant(letter)
            : null;
    }

    private static string FormatPartitionSummary(IReadOnlyList<DiskPartitionSnapshot> partitions)
    {
        if (partitions.Count == 0)
        {
            return "(немає)";
        }

        return string.Join(
            "; ",
            partitions.Select(static p => string.Format(
                CultureInfo.InvariantCulture,
                "#{0} ({1}, {2:N2} GiB, Type={3}, Vol={4})",
                p.PartitionNumber,
                p.DriveLetter.HasValue ? $"{p.DriveLetter.Value}:" : "без літери",
                p.SizeBytes / (1024.0 * 1024.0 * 1024.0),
                string.IsNullOrWhiteSpace(p.GptType) ? (p.MbrType?.ToString(CultureInfo.InvariantCulture) ?? "n/a") : p.GptType,
                string.IsNullOrWhiteSpace(p.VolumeGuidPath) ? "none" : "ok")));
    }

    private static (string? VolumeGuidPath, Guid? PartitionGuid, uint MbrSignature, long Offset)? QueryPartitionMetadata(
        int diskNumber,
        int partitionNumber)
    {
        try
        {
            uint mbrSig = QueryMbrDiskSignature(diskNumber);

            using var partSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT AccessPaths, Guid, Offset FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}");

            foreach (ManagementObject p in partSearcher.Get())
            {
                string? volGuid = null;
                if (p["AccessPaths"] is string[] paths)
                {
                    volGuid = paths.FirstOrDefault(static x => x.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))?.TrimEnd('\\');
                }

                Guid? partGuid = Guid.TryParse(p["Guid"]?.ToString(), out var g) ? g : null;
                long offset = p["Offset"] is not null ? Convert.ToInt64(p["Offset"], CultureInfo.InvariantCulture) : 0L;
                return (volGuid, partGuid, mbrSig, offset);
            }
        }
        catch
        {
            // retry caller loop
        }

        return null;
    }

    private static async Task ConfigureBcdStoreAsync(
        string bcdeditExe,
        string bcdStore,
        string description,
        bool includeBootMgr,
        CancellationToken ct)
    {
        _ = await RunProcessAsync(bcdeditExe, $"/store \"{bcdStore}\" /set {{default}} description \"{description}\"", null, ct).ConfigureAwait(false);
        _ = await RunProcessAsync(bcdeditExe, $"/store \"{bcdStore}\" /set {{default}} bootstatuspolicy IgnoreAllFailures", null, ct).ConfigureAwait(false);
        _ = await RunProcessAsync(bcdeditExe, $"/store \"{bcdStore}\" /set {{default}} recoveryenabled No", null, ct).ConfigureAwait(false);
        if (includeBootMgr)
        {
            _ = await RunProcessAsync(bcdeditExe, $"/store \"{bcdStore}\" /set {{bootmgr}} default {{default}}", null, ct).ConfigureAwait(false);
            _ = await RunProcessAsync(bcdeditExe, $"/store \"{bcdStore}\" /set {{bootmgr}} displayorder {{default}}", null, ct).ConfigureAwait(false);
        }
    }

    private static async Task RunDiskpartScriptAsync(string scriptContent, Action<string> log, CancellationToken ct)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"argus-dp-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(tempFile, scriptContent, Encoding.ASCII, ct).ConfigureAwait(false);
            var (exitCode, output) = await RunProcessAsync(
                Path.Combine(Environment.SystemDirectory, "diskpart.exe"),
                $"/s \"{tempFile}\"",
                null,
                ct).ConfigureAwait(false);

            if (exitCode != 0)
            {
                log($"diskpart вивід: {output}");
                throw new InvalidOperationException($"diskpart завершився з помилкою {exitCode}.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void ReportDismProgress(
        string line,
        int startPct,
        int endPct,
        string label,
        long estimatedTotalBytes,
        IProgress<MigrationProgress>? progress)
    {
        if (progress is null || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Match m = DismPercentRegex().Match(line);
        if (!m.Success)
        {
            return;
        }

        if (double.TryParse(m.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double localPct))
        {
            double clamped = Math.Clamp(localPct, 0.0, 100.0);
            int overall = startPct + (int)((endPct - startPct) * (clamped / 100.0));
            long copied = (long)(estimatedTotalBytes * (clamped / 100.0));
            progress.Report(new MigrationProgress(
                MigrationPhase.CloningData,
                Math.Clamp(overall, startPct, endPct),
                copied,
                estimatedTotalBytes,
                0,
                null,
                $"{label}: {clamped:0.0}%"));
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        string arguments,
        Action<string>? onLine,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var sb = new StringBuilder();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                outDone.TrySetResult();
            }
            else if (!string.IsNullOrWhiteSpace(e.Data))
            {
                string trimmed = e.Data.Trim();
                lock (sb)
                {
                    sb.AppendLine(trimmed);
                }

                onLine?.Invoke(trimmed);
            }
        };

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                errDone.TrySetResult();
            }
            else if (!string.IsNullOrWhiteSpace(e.Data))
            {
                string trimmed = e.Data.Trim();
                lock (sb)
                {
                    sb.AppendLine(trimmed);
                }

                onLine?.Invoke(trimmed);
            }
        };

        if (!proc.Start())
        {
            return (-1, $"Не вдалося запустити {fileName}");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(outDone.Task, errDone.Task).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best effort
            }

            throw;
        }

        return (proc.ExitCode, sb.ToString().Trim());
    }

    private static long ComputeDirectorySizeSafe(string dirPath, CancellationToken ct)
    {
        long total = 0;
        var opts = new System.IO.EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var fi in new DirectoryInfo(dirPath).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    total += fi.Length;
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ignore
        }

        return total;
    }

    private static bool DetectUefiFirmware()
    {
        if (GetFirmwareType(out uint fwType))
        {
            return fwType == 2; // FirmwareTypeUefi = 2
        }

        string? envFw = Environment.GetEnvironmentVariable("firmware_type");
        return string.Equals(envFw, "UEFI", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectBitLockerStatus(char driveLetter)
    {
        // root\CIMV2\Security\MicrosoftVolumeEncryption always denies non-admin callers after a ~5s WMI timeout.
        // Skip WMI BitLocker query when non-elevated; ExecuteAsync always re-inspects under administrator privileges.
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                return "FullyDecrypted";
            }

            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                $"SELECT ConversionStatus, ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{driveLetter}:'");

            foreach (ManagementObject obj in searcher.Get())
            {
                uint conv = Convert.ToUInt32(obj["ConversionStatus"] ?? 0, CultureInfo.InvariantCulture);
                return conv switch
                {
                    0 => "FullyDecrypted",
                    1 => "FullyEncrypted",
                    2 => "EncryptionInProgress",
                    3 => "DecryptionInProgress",
                    4 => "EncryptionPaused",
                    5 => "DecryptionPaused",
                    _ => "Unknown"
                };
            }
        }
        catch
        {
            // BitLocker WMI unavailable -> assume FullyDecrypted
        }

        return "FullyDecrypted";
    }

    private static string MapBusType(ushort code) => code switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        17 => "NVMe",
        _ => "Unknown"
    };

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFirmwareType(out uint firmwareType);
}
