using Argus.Core.Contracts.Migration;

namespace Argus.Migration.Engine;

public sealed record VssSnapshotHandle(string ShadowId, string DeviceObjectPath);

public sealed record PartitionedTargetInfo(
    int TargetDiskNumber,
    int BootPartitionNumber,
    int OsPartitionNumber,
    char BootDriveLetter,
    char OsDriveLetter,
    string? TargetVolumeGuidPath,
    Guid? OsPartitionGuid,
    uint MbrDiskSignature,
    long OsPartitionOffset);

public sealed class MigrationCleanupState
{
    public string? ShadowId { get; set; }
    public bool SystemHiveLoaded { get; set; }
    public bool UserHiveLoaded { get; set; }
    public string? VssJunctionPath { get; set; }
    public string? TempWorkDirectory { get; set; }
}

/// <summary>
/// Abstraction over host Windows OS/WMI/storage/registry/bootloader operations for CopyOS migration.
/// Keeps the full migration pipeline deterministic and unit-testable without wiping real disks during CI.
/// </summary>
public interface IMigrationHostOperations
{
    Task<SourceOsInfo> InspectLiveOsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MigrationTargetDisk>> DiscoverTargetDisksAsync(
        MigrationProfile profile,
        int sourceDiskNumber,
        char systemDriveLetter,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LocalScratchVolume>> GetLocalScratchVolumesAsync(CancellationToken cancellationToken);

    (char BootLetter, char OsLetter) AllocateFreeDriveLetters();

    Task<VssSnapshotHandle> CreateVssSnapshotAsync(
        char systemDriveLetter,
        Action<string> log,
        CancellationToken cancellationToken);

    Task DeleteVssSnapshotAsync(string shadowId, Action<string> log);

    Task<PartitionedTargetInfo> PartitionAndPrepareTargetAsync(
        MigrationPlanRequest request,
        SourceOsInfo source,
        MigrationTargetDisk target,
        CloneMode resolvedMode,
        char bootLetter,
        char osLetter,
        Action<string> log,
        CancellationToken cancellationToken);

    Task ExecuteBlockCloneAsync(
        SourceOsInfo source,
        PartitionedTargetInfo targetInfo,
        VssSnapshotHandle snapshot,
        Action<string> log,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken);

    Task<PartitionedTargetInfo> MountCheckAndExtendBlockVolumeAsync(
        MigrationProfile profile,
        PartitionedTargetInfo targetInfo,
        Action<string> log,
        CancellationToken cancellationToken);

    Task ExecuteDismCaptureAndApplyAsync(
        MigrationProfile profile,
        SourceOsInfo source,
        PartitionedTargetInfo targetInfo,
        VssSnapshotHandle snapshot,
        char scratchDriveLetter,
        MigrationCleanupState cleanupState,
        Action<string> log,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken);

    Task FixRedirectedUserFoldersAsync(
        MigrationPlanRequest request,
        SourceOsInfo source,
        char osLetter,
        MigrationCleanupState cleanupState,
        Action<string> log,
        CancellationToken cancellationToken);

    Task UpdateOfflineSystemRegistryAsync(
        MigrationPlanRequest request,
        SourceOsInfo source,
        PartitionedTargetInfo targetInfo,
        MigrationCleanupState cleanupState,
        Action<string> log,
        CancellationToken cancellationToken);

    Task InstallBootloaderAsync(
        MigrationPlanRequest request,
        SourceOsInfo source,
        PartitionedTargetInfo targetInfo,
        Action<string> log,
        CancellationToken cancellationToken);

    Task CleanupResourcesAsync(MigrationCleanupState state, Action<string> log);
}
