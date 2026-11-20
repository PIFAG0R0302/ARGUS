using System.Text;
using Argus.Core.Contracts.Migration;

namespace Argus.Migration.Engine;

public sealed record LocalScratchVolume(char DriveLetter, long FreeBytes, string FileSystem);

/// <summary>
/// Pure deterministic preflight validator, bus filter, driver policy catalog, and binary layout helper
/// unifying both legacy CopyOS profiles (Internal SATA/NVMe/RAID and External USB).
/// </summary>
public static class MigrationPlanEvaluator
{
    private const long BlockOverheadBytes = 150L * 1024 * 1024;
    private const long DismMinFreeHeadroomBytes = 12L * 1024 * 1024 * 1024;
    private const long DismScratchHeadroomBytes = 4L * 1024 * 1024 * 1024;
    private const long ExistingDataWarningBytes = 512L * 1024 * 1024;

    private static readonly HashSet<string> InternalBusTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SATA", "NVMe", "RAID", "ATA", "SAS", "SCSI"
    };

    public static IReadOnlyList<string> InternalBootDrivers { get; } =
    [
        "stornvme", "storahci", "pciide", "intelide", "amd_sata", "amd_xata", "amdxata",
        "iaStorV", "iaStorA", "iaStorAC", "iaStorAVC", "iaStorVD", "nvraid", "lsi_sas",
        "pci", "acpi", "partmgr", "volmgr", "disk"
    ];

    public static IReadOnlyList<string> ExternalUsbBootDrivers { get; } =
    [
        "USBXHCI", "USBHUB3", "UCX01000", "UASPStor", "USBSTOR", "usbccgp",
        "usbehci", "usbuhci", "usbohci", "USB4DeviceRouter", "USB4HostRouter",
        "storahci", "stornvme"
    ];

    public static bool IsBusEligible(MigrationProfile profile, string? busType)
    {
        if (string.IsNullOrWhiteSpace(busType))
        {
            return false;
        }

        string trimmed = busType.Trim();
        return profile switch
        {
            MigrationProfile.InternalMotherboardDisk => InternalBusTypes.Contains(trimmed),
            MigrationProfile.ExternalUsbDrive => string.Equals(trimmed, "USB", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public static MigrationPreflightResult Evaluate(
        SourceOsInfo source,
        MigrationTargetDisk? target,
        IReadOnlyList<LocalScratchVolume> localVolumes,
        MigrationPlanRequest request,
        bool isAdministrator)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(localVolumes);
        ArgumentNullException.ThrowIfNull(request);

        var warnings = new List<string>();
        var blocking = new List<string>();

        if (!isAdministrator)
        {
            warnings.Add("Для виконання клонування потрібні права адміністратора (запустіть кореневий Argus.exe або натисніть щит UAC).");
        }

        if (target is null)
        {
            blocking.Add("Не вибрано цільовий диск для міграції.");
            return new MigrationPreflightResult(false, CloneMode.BlockVssBitmap, null, warnings, blocking);
        }

        if (target.DiskNumber == source.SourceDiskNumber)
        {
            blocking.Add($"Критичний захист: Диск {target.DiskNumber} містить поточну операційну систему і не може бути ціллю.");
        }

        if (target.DriveLetters.Any(l => char.ToUpperInvariant(l) == char.ToUpperInvariant(source.SystemDriveLetter)))
        {
            blocking.Add($"Критичний захист: Диск {target.DiskNumber} містить активний системний розділ {source.SystemDriveLetter}:.");
        }

        if (!IsBusEligible(request.Profile, target.BusType))
        {
            blocking.Add($"Шина цільового диска ({target.BusType}) не відповідає вибраному профілю CopyOS.");
        }

        if (target.SizeBytes <= 0)
        {
            blocking.Add($"Цільовий Диск {target.DiskNumber} недоступний або має нульовий обсяг.");
        }

        if (target.UsedDataBytes > ExistingDataWarningBytes)
        {
            double usedGiB = target.UsedDataBytes / (1024.0 * 1024.0 * 1024.0);
            warnings.Add($"На Диску {target.DiskNumber} зараз зайнято {usedGiB:0.0} ГіБ — усі ці дані буде повністю знищено.");
        }

        if (!source.Is64Bit && request.PartitionStyle == PartitionStyleChoice.GptUefi)
        {
            warnings.Add("32-бітна Windows може вимагати Legacy BIOS / MBR замість UEFI / GPT.");
        }

        if (request.Profile == MigrationProfile.ExternalUsbDrive && request.RegisterUefiBootPriorityFirst)
        {
            warnings.Add("У профілі USB примусово діє /nofirmwaresync — порядок завантаження основного ПК не змінюватиметься.");
        }

        if (blocking.Count > 0)
        {
            return new MigrationPreflightResult(false, CloneMode.BlockVssBitmap, null, warnings, blocking);
        }

        bool sameSectorSize = source.LogicalSectorSize == target.LogicalSectorSize;
        bool fitsBlockClone = target.SizeBytes >= source.PartitionSizeBytes + BlockOverheadBytes;
        bool bitlockerClean = string.Equals(source.BitLockerStatus, "FullyDecrypted", StringComparison.OrdinalIgnoreCase);

        CloneMode resolvedMode;
        if (request.RequestedCloneMode == CloneMode.BlockVssBitmap)
        {
            resolvedMode = CloneMode.BlockVssBitmap;
            if (!sameSectorSize)
            {
                blocking.Add($"Режим BLOCK потребує однакового розміру секторів (джерело {source.LogicalSectorSize} Б, ціль {target.LogicalSectorSize} Б). Оберіть Авто або DISM.");
            }

            if (!fitsBlockClone)
            {
                double srcGiB = source.PartitionSizeBytes / (1024.0 * 1024.0 * 1024.0);
                double dstGiB = target.SizeBytes / (1024.0 * 1024.0 * 1024.0);
                blocking.Add($"Режим BLOCK потребує цільовий диск не менший за розділ {source.SystemDriveLetter}: ({srcGiB:0.1} ГіБ), а вибраний має {dstGiB:0.1} ГіБ. Оберіть Авто або DISM.");
            }

            if (!bitlockerClean)
            {
                blocking.Add($"На {source.SystemDriveLetter}: активний BitLocker ({source.BitLockerStatus}) — для блокового режиму потрібне розшифрування або режим DISM.");
            }
        }
        else if (request.RequestedCloneMode == CloneMode.DismShrinkToFit)
        {
            resolvedMode = CloneMode.DismShrinkToFit;
        }
        else
        {
            resolvedMode = (sameSectorSize && fitsBlockClone && bitlockerClean)
                ? CloneMode.BlockVssBitmap
                : CloneMode.DismShrinkToFit;
        }

        char? scratchDrive = null;
        if (resolvedMode == CloneMode.DismShrinkToFit)
        {
            long minTargetNeeded = source.UsedBytes + DismMinFreeHeadroomBytes;
            if (target.SizeBytes < minTargetNeeded)
            {
                double usedGiB = source.UsedBytes / (1024.0 * 1024.0 * 1024.0);
                double needGiB = minTargetNeeded / (1024.0 * 1024.0 * 1024.0);
                double dstGiB = target.SizeBytes / (1024.0 * 1024.0 * 1024.0);
                blocking.Add($"Недостатньо місця на цільовому диску: на {source.SystemDriveLetter}: зайнято {usedGiB:0.1} ГіБ (потрібно мінімум {needGiB:0.1} ГіБ), а вибрано диск на {dstGiB:0.1} ГіБ.");
            }

            long neededWimSpace = (long)(source.UsedBytes * 0.78) + DismScratchHeadroomBytes;
            var targetLetters = target.DriveLetters
                .Select(char.ToUpperInvariant)
                .ToHashSet();

            var candidate = localVolumes
                .Where(v =>
                    string.Equals(v.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) &&
                    !targetLetters.Contains(char.ToUpperInvariant(v.DriveLetter)) &&
                    v.FreeBytes >= neededWimSpace)
                .OrderByDescending(static v => v.FreeBytes)
                .FirstOrDefault();

            if (candidate is null)
            {
                double wimGiB = neededWimSpace / (1024.0 * 1024.0 * 1024.0);
                blocking.Add($"Для тимчасового WIM-образу DISM потрібно {wimGiB:0.1} ГіБ вільного місця на іншому локальному NTFS-томі, але відповідного тому не знайдено.");
            }
            else
            {
                scratchDrive = char.ToUpperInvariant(candidate.DriveLetter);
                if (!fitsBlockClone)
                {
                    warnings.Add($"Обрано адаптивний режим VSS + DISM (цільовий диск менший за розділ {source.SystemDriveLetter}:); буфер WIM: {scratchDrive}:.");
                }
                else if (!bitlockerClean)
                {
                    warnings.Add($"Через статус BitLocker ({source.BitLockerStatus}) обрано адаптивний режим VSS + DISM; буфер WIM: {scratchDrive}:.");
                }
                else if (!sameSectorSize)
                {
                    warnings.Add($"Через різний розмір секторів ({source.LogicalSectorSize} Б і {target.LogicalSectorSize} Б) обрано адаптивний режим VSS + DISM; буфер WIM: {scratchDrive}:.");
                }
            }
        }

        return new MigrationPreflightResult(
            CanProceed: blocking.Count == 0,
            ResolvedCloneMode: resolvedMode,
            ScratchDriveLetter: scratchDrive,
            Warnings: warnings,
            BlockingErrors: blocking);
    }

    public static byte[] BuildMountedDevicesBinary(
        PartitionStyleChoice style,
        Guid? gptPartitionGuid,
        uint mbrDiskSignature,
        long mbrPartitionOffset)
    {
        if (style == PartitionStyleChoice.GptUefi)
        {
            if (gptPartitionGuid is null || gptPartitionGuid == Guid.Empty)
            {
                throw new InvalidOperationException("GPT partition GUID is required to build MountedDevices entry.");
            }

            byte[] prefix = Encoding.ASCII.GetBytes("DMIO:ID:");
            byte[] guidBytes = gptPartitionGuid.Value.ToByteArray();
            byte[] combined = new byte[prefix.Length + guidBytes.Length];
            Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
            Buffer.BlockCopy(guidBytes, 0, combined, prefix.Length, guidBytes.Length);
            return combined;
        }

        byte[] sig = BitConverter.GetBytes(mbrDiskSignature);
        byte[] off = BitConverter.GetBytes(mbrPartitionOffset);
        byte[] mbrCombined = new byte[sig.Length + off.Length];
        Buffer.BlockCopy(sig, 0, mbrCombined, 0, sig.Length);
        Buffer.BlockCopy(off, 0, mbrCombined, sig.Length, off.Length);
        return mbrCombined;
    }

    public static string BuildWimExclusionScript() =>
        """
        [ExclusionList]
        \pagefile.sys
        \hiberfil.sys
        \swapfile.sys
        \$Recycle.Bin
        \System Volume Information
        \_ArgusClone_Temp_*
        \_ArgusClone_VSS_*
        """;
}
