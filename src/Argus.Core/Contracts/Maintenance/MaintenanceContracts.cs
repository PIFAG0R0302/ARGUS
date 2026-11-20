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

public interface IDiskSpaceAnalyzer
{
    Task<VolumeSpaceReport> AnalyzeVolumeSpaceAsync(
        char driveLetter,
        int topFolderCount = 8,
        CancellationToken cancellationToken = default,
        IProgress<(int Percent, string FolderName)>? progress = null);
}

public sealed record RestorePointResult(
    bool Succeeded,
    string Description,
    string? Message);

public interface IRestorePointService
{
    Task<RestorePointResult> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default);
}
