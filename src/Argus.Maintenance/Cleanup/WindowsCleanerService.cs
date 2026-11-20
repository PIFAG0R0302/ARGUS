using System.Globalization;
using System.Runtime.InteropServices;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Journal;

namespace Argus.Maintenance.Cleanup;

public interface IRecycleBinInterop
{
    (int ItemCount, long TotalBytes) QueryRecycleBin();
    bool EmptyRecycleBin();
}

internal sealed partial class Win32RecycleBinInterop : IRecycleBinInterop
{
    private const uint SherbNoConfirmation = 0x00000001;
    private const uint SherbNoProgressUi = 0x00000002;
    private const uint SherbNoSound = 0x00000004;

    public (int ItemCount, long TotalBytes) QueryRecycleBin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (0, 0);
        }

        var info = new ShQueryRbInfo
        {
            CbSize = (uint)Marshal.SizeOf<ShQueryRbInfo>()
        };

        int hr = SHQueryRecycleBinW(null, ref info);
        if (hr != 0 || info.I64NumItems <= 0 || info.I64Size < 0)
        {
            return (0, 0);
        }

        int count = (int)Math.Min(info.I64NumItems, int.MaxValue);
        return (count, info.I64Size);
    }

    public bool EmptyRecycleBin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        int hr = SHEmptyRecycleBinW(
            IntPtr.Zero,
            null,
            SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);

        // S_OK (0) or E_UNEXPECTED (when already empty)
        return hr == 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct ShQueryRbInfo
    {
        public uint CbSize;
        public long I64Size;
        public long I64NumItems;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string? pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}

/// <summary>
/// Two-step OS Cleaner engine:
/// 1) AnalyzeAsync scans external JSON rule targets, skips reparse points/symlinks, enforces exclusions and self-protection, and caches exact file candidates.
/// 2) CleanAsync requires an approved CleanerScanReport, optionally creates a System Restore point, and deletes only verified files from the approved analysis pass.
/// </summary>
public sealed class WindowsCleanerService : ICleanerService
{
    private const string RecycleBinToken = "$RECYCLE.BIN";

    private readonly IRestorePointService _restorePointService;
    private readonly IOperationJournal _journal;
    private readonly IRecycleBinInterop _recycleBin;
    private readonly Lock _cacheLock = new();

    private DateTimeOffset _lastScannedAt;
    private Dictionary<string, List<AnalyzedFileCandidate>> _analyzedFilesByRule = new(StringComparer.OrdinalIgnoreCase);

    public WindowsCleanerService(
        IRestorePointService restorePointService,
        IOperationJournal journal,
        IRecycleBinInterop? recycleBinInterop = null)
    {
        _restorePointService = restorePointService ?? throw new ArgumentNullException(nameof(restorePointService));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _recycleBin = recycleBinInterop ?? new Win32RecycleBinInterop();
    }

    public async Task<CleanerScanReport> AnalyzeAsync(
        IReadOnlyList<CleanerRuleDefinition> rules,
        IReadOnlyList<string> exclusions,
        CancellationToken cancellationToken = default,
        IProgress<CleanerProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(exclusions);

        progress?.Report(new CleanerProgress(0, "Аналіз", "Підготовка JSON-правил…"));
        var report = await Task.Run(() => AnalyzeCore(rules, exclusions, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        progress?.Report(new CleanerProgress(100, "Аналіз завершено", $"Знайдено {report.TotalFiles} файлів ({FormatBytes(report.TotalBytes)})"));

        await _journal.RecordAsync(
            moduleId: "argus.maintenance",
            severity: OperationSeverity.Info,
            action: "CleanerAnalyzeCompleted",
            details: $"Проаналізовано правил: {rules.Count}, знайдено файлів: {report.TotalFiles} ({FormatBytes(report.TotalBytes)})",
            succeeded: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return report;
    }

    public async Task<CleanerExecutionResult> CleanAsync(
        CleanerScanReport approvedReport,
        IReadOnlySet<string> selectedRuleIds,
        IReadOnlyList<string> exclusions,
        bool createRestorePointFirst,
        CancellationToken cancellationToken = default,
        IProgress<CleanerProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(approvedReport);
        ArgumentNullException.ThrowIfNull(selectedRuleIds);
        ArgumentNullException.ThrowIfNull(exclusions);

        var errors = new List<string>();
        string? restorePointId = null;

        if (selectedRuleIds.Count == 0 || approvedReport.Items.Count == 0)
        {
            return new CleanerExecutionResult(DateTimeOffset.UtcNow, 0, 0, 0, null, ["Не вибрано жодної категорії для очищення."]);
        }

        if (createRestorePointFirst)
        {
            progress?.Report(new CleanerProgress(5, "Очищення", "Створення контрольної точки System Restore…"));
            var rpResult = await _restorePointService.CreateRestorePointAsync(
                "ARGUS — Контрольна точка перед очищенням ОС",
                cancellationToken).ConfigureAwait(false);

            if (rpResult.Succeeded)
            {
                restorePointId = rpResult.Description;
            }
            else if (!string.IsNullOrWhiteSpace(rpResult.Message))
            {
                errors.Add($"System Restore: {rpResult.Message}");
            }
        }

        progress?.Report(new CleanerProgress(createRestorePointFirst ? 12 : 2, "Очищення", "Видалення підтверджених файлів…"));
        var result = await Task.Run(
            () => ExecuteApprovedCleanup(approvedReport, selectedRuleIds, exclusions, restorePointId, errors, createRestorePointFirst ? 12 : 2, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new CleanerProgress(100, "Очищення завершено", $"Звільнено {FormatBytes(result.BytesFreed)}"));

        await _journal.RecordAsync(
            moduleId: "argus.maintenance",
            severity: result.Errors.Count == 0 ? OperationSeverity.Success : OperationSeverity.Warning,
            action: "CleanerExecutionCompleted",
            details: $"Видалено файлів: {result.FilesDeleted}, пропущено (зайняті): {result.FilesSkipped}, звільнено: {FormatBytes(result.BytesFreed)}",
            succeeded: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }

    private CleanerScanReport AnalyzeCore(
        IReadOnlyList<CleanerRuleDefinition> rules,
        IReadOnlyList<string> exclusions,
        IProgress<CleanerProgress>? progress,
        CancellationToken ct)
    {
        DateTimeOffset scannedAt = DateTimeOffset.UtcNow;
        string[] normalizedExclusions = BuildNormalizedExclusions(exclusions);

        var items = new List<CleanerScanItem>(rules.Count);
        var candidatesByRule = new Dictionary<string, List<AnalyzedFileCandidate>>(StringComparer.OrdinalIgnoreCase);
        int grandTotalFiles = 0;
        long grandTotalBytes = 0;
        int totalRules = Math.Max(1, rules.Count);

        for (int ruleIdx = 0; ruleIdx < rules.Count; ruleIdx++)
        {
            var rule = rules[ruleIdx];
            ct.ThrowIfCancellationRequested();
            int startPct = (int)(ruleIdx * 100.0 / totalRules);
            progress?.Report(new CleanerProgress(startPct, "Аналіз", $"{rule.DisplayName} ({ruleIdx + 1}/{rules.Count})"));

            if (IsRecycleBinRule(rule))
            {
                var (rbCount, rbBytes) = _recycleBin.QueryRecycleBin();
                items.Add(new CleanerScanItem(
                    RuleId: rule.Id,
                    Category: rule.Category,
                    DisplayName: rule.DisplayName,
                    FileCount: rbCount,
                    TotalBytes: rbBytes,
                    SamplePaths: rbCount > 0 ? ["Кошик Windows ($RECYCLE.BIN)"] : []));

                candidatesByRule[rule.Id] = [];
                grandTotalFiles += rbCount;
                grandTotalBytes += rbBytes;
                progress?.Report(new CleanerProgress((int)((ruleIdx + 1) * 100.0 / totalRules), "Аналіз", rule.DisplayName));
                continue;
            }

            var ruleCandidates = new List<AnalyzedFileCandidate>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var samplePaths = new List<string>(5);
            long ruleBytes = 0;

            DateTime utcAgeCutoff = rule.MinFileAgeHours > 0
                ? DateTime.UtcNow.AddHours(-rule.MinFileAgeHours)
                : DateTime.MaxValue;

            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = rule.Recursive,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            };

            foreach (string rawPath in rule.Paths)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(rawPath) || JsonCleanerRuleLoader.IsDangerousSystemPath(rawPath))
                {
                    continue;
                }

                string expandedDir = Environment.ExpandEnvironmentVariables(rawPath.Trim());
                if (JsonCleanerRuleLoader.IsDangerousSystemPath(expandedDir) || !Directory.Exists(expandedDir))
                {
                    continue;
                }

                if (IsExcludedPath(expandedDir, normalizedExclusions))
                {
                    continue;
                }

                IReadOnlyList<string> patterns = rule.FilePatterns is { Count: > 0 } ? rule.FilePatterns : ["*"];
                foreach (string pattern in patterns)
                {
                    string safePattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
                    IEnumerable<string> enumerated;
                    try
                    {
                        enumerated = Directory.EnumerateFiles(expandedDir, safePattern, enumOptions);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (string filePath in enumerated)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (ruleCandidates.Count >= 25_000)
                        {
                            break;
                        }

                        if (!seenFiles.Add(filePath) || IsExcludedPath(filePath, normalizedExclusions))
                        {
                            continue;
                        }

                        try
                        {
                            var fi = new FileInfo(filePath);
                            if (!fi.Exists || (fi.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }

                            if (rule.MinFileAgeHours > 0 && fi.LastWriteTimeUtc > utcAgeCutoff)
                            {
                                continue;
                            }

                            long len = Math.Max(0L, fi.Length);
                            ruleCandidates.Add(new AnalyzedFileCandidate(filePath, len));
                            ruleBytes += len;
                            if (samplePaths.Count < 5)
                            {
                                samplePaths.Add(filePath);
                            }

                            if (progress is not null && (ruleCandidates.Count % 25) == 0)
                            {
                                double ruleSpan = 100.0 / totalRules;
                                double subProgress = Math.Min(0.92, ruleCandidates.Count / 300.0);
                                int subPct = Math.Clamp((int)((ruleIdx * ruleSpan) + (ruleSpan * subProgress)), 1, 99);
                                progress.Report(new CleanerProgress(subPct, "Аналіз", $"{rule.DisplayName} ({ruleCandidates.Count} файлів)"));
                            }
                        }
                        catch
                        {
                            // Skip inaccessible or transient files
                        }
                    }
                }
            }

            candidatesByRule[rule.Id] = ruleCandidates;
            grandTotalFiles += ruleCandidates.Count;
            grandTotalBytes += ruleBytes;

            items.Add(new CleanerScanItem(
                RuleId: rule.Id,
                Category: rule.Category,
                DisplayName: rule.DisplayName,
                FileCount: ruleCandidates.Count,
                TotalBytes: ruleBytes,
                SamplePaths: samplePaths));

            int endPct = Math.Clamp((int)((ruleIdx + 1) * 100.0 / totalRules), 1, 99);
            progress?.Report(new CleanerProgress(endPct, "Аналіз", $"{rule.DisplayName} ({ruleIdx + 1}/{rules.Count})"));
        }

        lock (_cacheLock)
        {
            _lastScannedAt = scannedAt;
            _analyzedFilesByRule = candidatesByRule;
        }

        return new CleanerScanReport(
            ScannedAt: scannedAt,
            Items: items,
            TotalFiles: grandTotalFiles,
            TotalBytes: grandTotalBytes);
    }

    private CleanerExecutionResult ExecuteApprovedCleanup(
        CleanerScanReport approvedReport,
        IReadOnlySet<string> selectedRuleIds,
        IReadOnlyList<string> exclusions,
        string? restorePointId,
        List<string> errors,
        int basePercent,
        IProgress<CleanerProgress>? progress,
        CancellationToken ct)
    {
        string[] normalizedExclusions = BuildNormalizedExclusions(exclusions);

        Dictionary<string, List<AnalyzedFileCandidate>> snapshotCache;
        lock (_cacheLock)
        {
            snapshotCache = new Dictionary<string, List<AnalyzedFileCandidate>>(_analyzedFilesByRule, StringComparer.OrdinalIgnoreCase);
        }

        int totalWorkUnits = 0;
        foreach (var item in approvedReport.Items)
        {
            if (!selectedRuleIds.Contains(item.RuleId) || item.FileCount <= 0)
            {
                continue;
            }

            if (item.Category == CleanerCategory.RecycleBin ||
                item.RuleId.Equals("win.recycle_bin", StringComparison.OrdinalIgnoreCase))
            {
                totalWorkUnits += Math.Max(1, item.FileCount);
            }
            else if (snapshotCache.TryGetValue(item.RuleId, out var cands))
            {
                totalWorkUnits += cands.Count;
            }
        }

        int processedUnits = 0;
        int lastReportedPct = basePercent;
        int filesDeleted = 0;
        int filesSkipped = 0;
        long bytesFreed = 0;

        void ReportUnit(string label)
        {
            if (progress is null || totalWorkUnits <= 0)
            {
                return;
            }

            int pct = basePercent + (int)((100 - basePercent) * Math.Clamp((double)processedUnits / totalWorkUnits, 0.0, 1.0));
            if (pct > lastReportedPct)
            {
                lastReportedPct = pct;
                progress.Report(new CleanerProgress(Math.Clamp(pct, 0, 99), "Очищення", label));
            }
        }

        foreach (var item in approvedReport.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (!selectedRuleIds.Contains(item.RuleId) || item.FileCount <= 0)
            {
                continue;
            }

            if (item.Category == CleanerCategory.RecycleBin ||
                item.RuleId.Equals("win.recycle_bin", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new CleanerProgress(lastReportedPct, "Очищення", item.DisplayName));
                if (_recycleBin.EmptyRecycleBin())
                {
                    filesDeleted += item.FileCount;
                    bytesFreed += item.TotalBytes;
                }
                else
                {
                    filesSkipped += item.FileCount;
                    errors.Add("Не вдалося повністю очистити Кошик Windows.");
                }

                processedUnits += Math.Max(1, item.FileCount);
                ReportUnit(item.DisplayName);
                continue;
            }

            if (!snapshotCache.TryGetValue(item.RuleId, out var candidates) || candidates.Count == 0)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                processedUnits++;
                if (JsonCleanerRuleLoader.IsDangerousSystemPath(candidate.FullPath) ||
                    IsExcludedPath(candidate.FullPath, normalizedExclusions))
                {
                    filesSkipped++;
                    ReportUnit(item.DisplayName);
                    continue;
                }

                try
                {
                    var fi = new FileInfo(candidate.FullPath);
                    if (!fi.Exists)
                    {
                        ReportUnit(item.DisplayName);
                        continue;
                    }

                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        filesSkipped++;
                        ReportUnit(item.DisplayName);
                        continue;
                    }

                    if ((fi.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        fi.Attributes &= ~FileAttributes.ReadOnly;
                    }

                    fi.Delete();
                    filesDeleted++;
                    bytesFreed += candidate.SizeBytes;
                }
                catch
                {
                    // File is locked by an active application or protected by OS
                    filesSkipped++;
                }

                ReportUnit(item.DisplayName);
            }
        }

        return new CleanerExecutionResult(
            CompletedAt: DateTimeOffset.UtcNow,
            FilesDeleted: filesDeleted,
            FilesSkipped: filesSkipped,
            BytesFreed: bytesFreed,
            RestorePointId: restorePointId,
            Errors: errors);
    }

    internal static string[] BuildNormalizedExclusions(IReadOnlyList<string> userExclusions)
    {
        var list = new List<string>(userExclusions.Count + 2);

        // Self-protection: never scan or delete files inside Argus's own application or data directory.
        TryAddPathPrefix(list, AppContext.BaseDirectory);
        TryAddPathPrefix(list, ArgusPaths.GetDataDirectory());

        foreach (string raw in userExclusions)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
            TryAddPathPrefix(list, expanded);
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool IsExcludedPath(string candidatePath, IReadOnlyList<string> normalizedExclusions)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || normalizedExclusions.Count == 0)
        {
            return false;
        }

        string normCandidate = candidatePath.Replace('/', '\\').TrimEnd('\\');
        foreach (string excl in normalizedExclusions)
        {
            if (normCandidate.Equals(excl, StringComparison.OrdinalIgnoreCase) ||
                normCandidate.StartsWith(excl + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 Б";
        }

        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;

        if (bytes >= gb)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.00} ГіБ", bytes / gb);
        }

        if (bytes >= mb)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.0} МБ", bytes / mb);
        }

        if (bytes >= kb)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.0} КБ", bytes / kb);
        }

        return $"{bytes} Б";
    }

    private static bool IsRecycleBinRule(CleanerRuleDefinition rule) =>
        rule.Category == CleanerCategory.RecycleBin ||
        rule.Paths.Any(static p => p.Trim().Equals(RecycleBinToken, StringComparison.OrdinalIgnoreCase));

    private static void TryAddPathPrefix(List<string> target, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        if (normalized.Length > 2)
        {
            target.Add(normalized);
        }
    }

    private readonly record struct AnalyzedFileCandidate(string FullPath, long SizeBytes);
}
