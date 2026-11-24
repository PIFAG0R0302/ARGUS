namespace Argus.Core.Contracts.Maintenance;

public enum CleanerCategory
{
    WindowsTemp,
    UserTemp,
    BrowserCache,
    SystemLogs,
    CrashDumps,
    WindowsUpdateCache,
    Thumbnails,
    RecycleBin,
    Custom
}

public sealed record CleanerRuleDefinition(
    string Id,
    string DisplayName,
    CleanerCategory Category,
    string Description,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> FilePatterns,
    bool Recursive = true,
    int MinFileAgeHours = 0,
    bool RequiresElevation = false,
    bool EnabledByDefault = true);

public sealed record CleanerRuleLoadResult(
    IReadOnlyList<CleanerRuleDefinition> ValidRules,
    IReadOnlyList<string> Warnings);

public sealed record CleanerScanItem(
    string RuleId,
    CleanerCategory Category,
    string DisplayName,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> SamplePaths);

public sealed record CleanerScanReport(
    DateTimeOffset ScannedAt,
    IReadOnlyList<CleanerScanItem> Items,
    int TotalFiles,
    long TotalBytes);

public sealed record CleanerExecutionResult(
    DateTimeOffset CompletedAt,
    int FilesDeleted,
    int FilesSkipped,
    long BytesFreed,
    string? RestorePointId,
    IReadOnlyList<string> Errors);

public sealed record CleanerProgress(
    int PercentComplete,
    string StageLabel,
    string CurrentItem);

public interface ICleanerRuleLoader
{
    Task<CleanerRuleLoadResult> LoadRulesAsync(string rulesDirectory, CancellationToken cancellationToken = default);
}

public interface ICleanerService
{
    Task<CleanerScanReport> AnalyzeAsync(
        IReadOnlyList<CleanerRuleDefinition> rules,
        IReadOnlyList<string> exclusions,
        CancellationToken cancellationToken = default,
        IProgress<CleanerProgress>? progress = null);

    Task<CleanerExecutionResult> CleanAsync(
        CleanerScanReport approvedReport,
        IReadOnlySet<string> selectedRuleIds,
        IReadOnlyList<string> exclusions,
        bool createRestorePointFirst,
        CancellationToken cancellationToken = default,
        IProgress<CleanerProgress>? progress = null);
}

public enum StorageMediaKind
{
    Hdd,
    SataSsd,
    NvmeSsd,
    RemovableUsb,
    Unknown
}

public enum DiskOptimizationAction
{
    DefragmentHdd,
    TrimSsd,
    AnalyzeOnly
}

public enum SmartHealthStatus
{
    Healthy,
    Caution,
    Critical,
    Unknown
}

public sealed record SmartSummary(
    SmartHealthStatus Health,
    double? TemperatureCelsius,
    int? WearLevelPercent,
    long? PowerOnHours,
    long? ReallocatedSectorCount,
    long? MediaErrorCount);

public sealed record PhysicalDiskInfo(
    int DiskNumber,
    string Model,
    string SerialNumber,
    string BusType,
    StorageMediaKind MediaKind,
    long SizeBytes,
    string PartitionStyle,
    IReadOnlyList<char> DriveLetters,
    SmartSummary Smart);

public sealed record DiskOptimizationResult(
    char DriveLetter,
    StorageMediaKind MediaKind,
    DiskOptimizationAction ActionExecuted,
    bool Succeeded,
    TimeSpan Duration,
    string Summary);

public static class DiskOptimizationPolicy
{
    /// <summary>
    /// Enforces safe optimization: SSD/NVMe drives only receive TRIM, never HDD defragmentation.
    /// </summary>
    public static DiskOptimizationAction ResolveSafeAction(StorageMediaKind mediaKind) => mediaKind switch
    {
        StorageMediaKind.Hdd => DiskOptimizationAction.DefragmentHdd,
        StorageMediaKind.SataSsd or StorageMediaKind.NvmeSsd => DiskOptimizationAction.TrimSsd,
        _ => DiskOptimizationAction.AnalyzeOnly
    };
}

public interface IDiskOptimizer
{
    Task<IReadOnlyList<PhysicalDiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default);
    Task<DiskOptimizationResult> OptimizeVolumeAsync(
        char driveLetter,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record FolderSpaceNode(
    string Name,
    string FullPath,
    long SizeBytes,
    int FileCount,
    double ShareOfVolumeUsedPercent,
    double ShareOfTopListPercent);

public sealed record VolumeSpaceReport(
    char DriveLetter,
    string VolumeLabel,
    string FileSystem,
    long TotalSizeBytes,
    long UsedBytes,
    long FreeBytes,
    IReadOnlyList<FolderSpaceNode> LargestFolders);

public sealed record DiskSpaceEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    int FileCount,
    double ShareOfParentPercent,
    double ShareOfTopListPercent,
    bool IsProtectedPath = false,
    bool IsReparsePoint = false,
    string? ProtectionReason = null);

public sealed record FolderContentsReport(
    char DriveLetter,
    string CurrentPath,
    string? ParentPath,
    bool IsVolumeRoot,
    long TotalMeasuredBytes,
    IReadOnlyList<DiskSpaceEntry> Entries);

public sealed record DiskDeleteFailure(
    string FullPath,
    string Reason);

public sealed record DiskRecycleDeleteResult(
    int DeletedCount,
    long FreedBytes,
    int BlockedCount,
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyList<DiskDeleteFailure> Failures);

public interface IDiskSpaceAnalyzer
{
    Task<VolumeSpaceReport> AnalyzeVolumeSpaceAsync(
        char driveLetter,
        int topFolderCount = 8,
        CancellationToken cancellationToken = default,
        IProgress<(int Percent, string FolderName)>? progress = null);

    Task<FolderContentsReport> AnalyzeFolderContentsAsync(
        string folderPath,
        int maxEntries = 40,
        CancellationToken cancellationToken = default,
        IProgress<(int Percent, string EntryName)>? progress = null)
    {
        char letter = !string.IsNullOrWhiteSpace(folderPath) && folderPath.Length >= 2 && folderPath[1] == ':'
            ? char.ToUpperInvariant(folderPath[0])
            : 'C';

        return Task.FromResult(new FolderContentsReport(
            DriveLetter: letter,
            CurrentPath: folderPath ?? string.Empty,
            ParentPath: null,
            IsVolumeRoot: true,
            TotalMeasuredBytes: 0,
            Entries: []));
    }

    Task<DiskRecycleDeleteResult> DeleteToRecycleBinAsync(
        IReadOnlyList<string> fullPaths,
        CancellationToken cancellationToken = default)
    {
        var paths = fullPaths ?? [];
        var failures = paths
            .Select(static p => new DiskDeleteFailure(p, "Переміщення у Кошик не підтримується поточним аналізатором."))
            .ToArray();

        return Task.FromResult(new DiskRecycleDeleteResult(
            DeletedCount: 0,
            FreedBytes: 0,
            BlockedCount: failures.Length,
            DeletedPaths: [],
            Failures: failures));
    }
}

public sealed record RestorePointResult(
    bool Succeeded,
    string Description,
    string? Message);

public interface IRestorePointService
{
    Task<RestorePointResult> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default);
}
