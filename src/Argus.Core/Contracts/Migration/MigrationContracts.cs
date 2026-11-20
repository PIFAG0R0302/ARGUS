namespace Argus.Core.Contracts.Migration;

public enum MigrationProfile
{
    InternalMotherboardDisk,
    ExternalUsbDrive
}

public enum CloneMode
{
    Auto,
    BlockVssBitmap,
    DismShrinkToFit
}

public enum PartitionStyleChoice
{
    GptUefi,
    MbrLegacy
}

public enum MigrationPhase
{
    InspectingSource,
    ValidatingTarget,
    CreatingVssSnapshot,
    PartitioningTarget,
    CloningData,
    CheckingAndExtendingVolume,
    FixingUserFolders,
    UpdatingOfflineRegistry,
    InstallingBootloader,
    CleaningUp,
    Completed,
    Failed,
    Cancelled
}

public sealed record SourceOsInfo(
    string OsCaption,
    char SystemDriveLetter,
    int SourceDiskNumber,
    string SourceDiskModel,
    string SourceBusType,
    long PartitionSizeBytes,
    long UsedBytes,
    int LogicalSectorSize,
    bool Is64Bit,
    bool IsUefiHost,
    string BitLockerStatus);

public sealed record MigrationTargetDisk(
    int DiskNumber,
    string FriendlyName,
    string BusType,
    string MediaType,
    long SizeBytes,
    int LogicalSectorSize,
    string PartitionStyle,
    long UsedDataBytes,
    IReadOnlyList<char> DriveLetters);

public sealed record MigrationPlanRequest(
    MigrationProfile Profile,
    int TargetDiskNumber,
    PartitionStyleChoice PartitionStyle,
    CloneMode RequestedCloneMode = CloneMode.Auto,
    bool RegisterUefiBootPriorityFirst = false,
    bool CopyRedirectedDesktop = false);

public sealed record MigrationPreflightResult(
    bool CanProceed,
    CloneMode ResolvedCloneMode,
    char? ScratchDriveLetter,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> BlockingErrors);

public sealed record MigrationProgress(
    MigrationPhase Phase,
    int PercentComplete,
    long BytesCopied,
    long TotalBytes,
    double SpeedMegabytesPerSec,
    TimeSpan? EstimatedRemaining,
    string StatusMessage);

public sealed record MigrationResult(
    bool Succeeded,
    bool WasCancelled,
    MigrationProfile Profile,
    CloneMode UsedCloneMode,
    PartitionStyleChoice PartitionStyle,
    int TargetDiskNumber,
    TimeSpan Elapsed,
    string? LogFilePath,
    string Summary,
    string? ErrorDetails = null);

public static class MigrationConfirmationPolicy
{
    /// <summary>
    /// Generates the explicit human confirmation phrase required before wiping a target disk.
    /// </summary>
    public static string BuildExpectedToken(int targetDiskNumber) => $"WIPE-DISK-{targetDiskNumber}";

    public static bool IsValidConfirmation(int targetDiskNumber, string? userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return false;
        }

        string trimmed = userInput.Trim();
        return string.Equals(trimmed, BuildExpectedToken(targetDiskNumber), StringComparison.Ordinal);
    }
}

public interface IMigrationEngine
{
    Task<SourceOsInfo> InspectLiveOsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MigrationTargetDisk>> DiscoverTargetDisksAsync(
        MigrationProfile profile,
        CancellationToken cancellationToken = default);
    Task<MigrationPreflightResult> EvaluatePlanAsync(
        MigrationPlanRequest request,
        CancellationToken cancellationToken = default);
    Task<MigrationResult> ExecuteAsync(
        MigrationPlanRequest request,
        string explicitConfirmationToken,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
