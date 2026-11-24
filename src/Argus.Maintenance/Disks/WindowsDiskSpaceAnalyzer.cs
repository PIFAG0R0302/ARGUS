using System.Collections.Concurrent;
using System.Diagnostics;
using Argus.Core.Contracts.Maintenance;

namespace Argus.Maintenance.Disks;

/// <summary>
/// Fast, bounded top-level large-folder space map analyzer per drive volume.
/// Skips reparse points/junctions and special NTFS system folders so UI rendering stays responsive.
/// </summary>
public sealed class WindowsDiskSpaceAnalyzer : IDiskSpaceAnalyzer
{
    private const int MaxFilesPerTopFolder = 18_000;
    private const int MaxDirsPerTopFolder = 1_200;
    private const long MaxFolderScanMs = 220;
    private const long MaxVolumeScanMs = 1_600;

    private readonly IRecycleBinDeleter _recycleBinDeleter;

    public WindowsDiskSpaceAnalyzer()
        : this(null)
    {
    }

    internal WindowsDiskSpaceAnalyzer(IRecycleBinDeleter? recycleBinDeleter)
    {
        _recycleBinDeleter = recycleBinDeleter ?? new WindowsShellRecycleBinDeleter();
    }

    private static readonly HashSet<string> SkippedRootFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN",
        "System Volume Information",
        "$WinREAgent",
        "Recovery",
        "Config.Msi",
        "Documents and Settings",
        "$GetCurrent",
        ".tmp.drivedownload"
    };

    public Task<VolumeSpaceReport> AnalyzeVolumeSpaceAsync(
        char driveLetter,
        int topFolderCount = 8,
        CancellationToken cancellationToken = default,
        IProgress<(int Percent, string FolderName)>? progress = null)
    {
        char letter = char.ToUpperInvariant(driveLetter);
        return Task.Run(() => AnalyzeCore(letter, topFolderCount, progress, cancellationToken), cancellationToken);
    }

    private static VolumeSpaceReport AnalyzeCore(
        char letter,
        int topFolderCount,
        IProgress<(int Percent, string FolderName)>? progress,
        CancellationToken ct)
    {
        string rootPath = $"{letter}:\\";
        var drive = new DriveInfo(rootPath);
        if (!drive.IsReady)
        {
            return new VolumeSpaceReport(letter, string.Empty, "—", 0, 0, 0, []);
        }

        long totalBytes = drive.TotalSize;
        long freeBytes = drive.AvailableFreeSpace;
        long usedBytes = Math.Max(0, totalBytes - freeBytes);

        return AnalyzeDirectoryRoot(
            rootPath,
            letter,
            drive.VolumeLabel,
            drive.DriveFormat,
            totalBytes,
            usedBytes,
            freeBytes,
            topFolderCount,
            ct,
            progress);
    }

    internal static VolumeSpaceReport AnalyzeDirectoryRoot(
        string rootPath,
        char driveLetter,
        string volumeLabel,
        string fileSystem,
        long totalSizeBytes,
        long usedBytes,
        long freeBytes,
        int topFolderCount,
        CancellationToken ct,
        IProgress<(int Percent, string FolderName)>? progress = null)
    {
        int takeCount = Math.Clamp(topFolderCount, 1, 20);
        if (!Directory.Exists(rootPath))
        {
            return new VolumeSpaceReport(driveLetter, volumeLabel, fileSystem, totalSizeBytes, usedBytes, freeBytes, []);
        }

        var dirEnumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var rawBag = new ConcurrentBag<(string Name, string FullPath, long SizeBytes, int FileCount)>();
        var candidateDirs = new List<(string Name, string FullPath)>();

        try
        {
            foreach (string dirPath in Directory.EnumerateDirectories(rootPath, "*", dirEnumOptions))
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(dirPath);
                if (string.IsNullOrWhiteSpace(name) || SkippedRootFolderNames.Contains(name))
                {
                    continue;
                }

                try
                {
                    var di = new DirectoryInfo(dirPath);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                candidateDirs.Add((name, dirPath));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Root enumeration failed
        }

        long volumeStartTicks = Stopwatch.GetTimestamp();
        int totalCandidates = Math.Max(1, candidateDirs.Count);
        int completedCandidates = 0;

        Parallel.ForEach(
            candidateDirs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 6),
                CancellationToken = ct
            },
            entry =>
            {
                var (sizeBytes, fileCount) = MeasureFolderBounded(entry.FullPath, volumeStartTicks, ct);
                if (sizeBytes > 0)
                {
                    rawBag.Add((entry.Name, entry.FullPath, sizeBytes, fileCount));
                }

                int done = Interlocked.Increment(ref completedCandidates);
                int pct = Math.Clamp((int)(done * 100.0 / totalCandidates), 1, 99);
                progress?.Report((pct, entry.Name));
            });

        var rawFolders = rawBag.ToList();

        rawFolders.Sort(static (a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        var topRaw = rawFolders.Take(takeCount).ToArray();
        long maxFolderBytes = topRaw.Length > 0 ? Math.Max(1L, topRaw[0].SizeBytes) : 1L;
        long effectiveUsed = usedBytes > 0 ? usedBytes : Math.Max(1L, topRaw.Sum(static f => f.SizeBytes));

        var nodes = new List<FolderSpaceNode>(topRaw.Length);
        foreach (var item in topRaw)
        {
            double ofUsed = Math.Clamp((double)item.SizeBytes / effectiveUsed * 100.0, 0.0, 100.0);
            double ofTop = Math.Clamp((double)item.SizeBytes / maxFolderBytes * 100.0, 0.0, 100.0);

            nodes.Add(new FolderSpaceNode(
                Name: item.Name,
                FullPath: item.FullPath,
                SizeBytes: item.SizeBytes,
                FileCount: item.FileCount,
                ShareOfVolumeUsedPercent: Math.Round(ofUsed, 1),
                ShareOfTopListPercent: Math.Round(ofTop, 1)));
        }

        return new VolumeSpaceReport(
            DriveLetter: driveLetter,
            VolumeLabel: volumeLabel,
            FileSystem: fileSystem,
            TotalSizeBytes: totalSizeBytes,
            UsedBytes: usedBytes,
            FreeBytes: freeBytes,
            LargestFolders: nodes);
    }

    private static (long SizeBytes, int FileCount) MeasureFolderBounded(
        string folderPath,
        long volumeStartTicks,
        CancellationToken ct)
    {
        var flatEnumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        long sum = 0;
        int count = 0;
        int visitedDirs = 0;
        long folderStartTicks = Stopwatch.GetTimestamp();

        var queue = new Queue<string>();
        queue.Enqueue(folderPath);

        try
        {
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                if (visitedDirs > 0 &&
                    (visitedDirs >= MaxDirsPerTopFolder ||
                     Stopwatch.GetElapsedTime(folderStartTicks).TotalMilliseconds >= MaxFolderScanMs ||
                     Stopwatch.GetElapsedTime(volumeStartTicks).TotalMilliseconds >= MaxVolumeScanMs))
                {
                    break;
                }

                string currentDir = queue.Dequeue();
                visitedDirs++;

                DirectoryInfo dirInfo;
                try
                {
                    dirInfo = new DirectoryInfo(currentDir);
                    foreach (var fi in dirInfo.EnumerateFiles("*", flatEnumOptions))
                    {
                        if ((count & 0x1F) == 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (count > 0 &&
                                (Stopwatch.GetElapsedTime(folderStartTicks).TotalMilliseconds >= MaxFolderScanMs ||
                                 Stopwatch.GetElapsedTime(volumeStartTicks).TotalMilliseconds >= MaxVolumeScanMs))
                            {
                                return (sum, count);
                            }
                        }

                        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        try
                        {
                            sum += Math.Max(0L, fi.Length);
                            count++;
                        }
                        catch
                        {
                            // Skip unreadable entries
                        }

                        if (count >= MaxFilesPerTopFolder)
                        {
                            return (sum, count);
                        }
                    }

                    if (visitedDirs + queue.Count < MaxDirsPerTopFolder)
                    {
                        foreach (var subDir in dirInfo.EnumerateDirectories("*", flatEnumOptions))
                        {
                            if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }

                            queue.Enqueue(subDir.FullName);
                            if (visitedDirs + queue.Count >= MaxDirsPerTopFolder)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Skip inaccessible directories
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore top-level access errors
        }

        return (sum, count);
    }

    public Task<FolderContentsReport> AnalyzeFolderContentsAsync(
        string folderPath,
        int maxEntries = 40,
        CancellationToken cancellationToken = default,
        IProgress<(int Percent, string EntryName)>? progress = null)
    {
        return Task.Run(
            () => AnalyzeFolderContentsInternal(folderPath, maxEntries, cancellationToken, progress),
            cancellationToken);
    }

    internal static FolderContentsReport AnalyzeFolderContentsInternal(
        string folderPath,
        int maxEntries,
        CancellationToken ct,
        IProgress<(int Percent, string EntryName)>? progress = null)
    {
        int takeCount = Math.Clamp(maxEntries, 1, 200);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return new FolderContentsReport('C', string.Empty, null, true, 0, []);
        }

        string normalizedPath;
        try
        {
            normalizedPath = DiskDeleteSafetyPolicy.NormalizePath(folderPath);
        }
        catch
        {
            return new FolderContentsReport('C', folderPath, null, true, 0, []);
        }

        string? rootPath = Path.GetPathRoot(normalizedPath);
        char driveLetter = !string.IsNullOrWhiteSpace(rootPath) && rootPath.Length >= 2 && rootPath[1] == ':'
            ? char.ToUpperInvariant(rootPath[0])
            : 'C';

        bool isVolumeRoot = !string.IsNullOrWhiteSpace(rootPath) &&
            string.Equals(
                normalizedPath.TrimEnd('\\') + "\\",
                rootPath.TrimEnd('\\') + "\\",
                StringComparison.OrdinalIgnoreCase);

        string? parentPath = isVolumeRoot ? null : Directory.GetParent(normalizedPath)?.FullName;

        if (!Directory.Exists(normalizedPath))
        {
            return new FolderContentsReport(driveLetter, normalizedPath, parentPath, isVolumeRoot, 0, []);
        }

        // Do not skip ReparsePoints at this immediate level so we can surface them as disabled/blocked
        // without ever traversing into their targets.
        var immediateEnumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        var candidateDirs = new List<(string Name, string FullPath, bool IsReparsePoint)>();
        var rawEntries = new ConcurrentBag<(string Name, string FullPath, bool IsDirectory, long SizeBytes, int FileCount, bool IsReparsePoint)>();

        try
        {
            var dirInfo = new DirectoryInfo(normalizedPath);

            foreach (var subDir in dirInfo.EnumerateDirectories("*", immediateEnumOptions))
            {
                ct.ThrowIfCancellationRequested();
                string name = subDir.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (isVolumeRoot && SkippedRootFolderNames.Contains(name))
                {
                    continue;
                }

                bool isReparse = false;
                try
                {
                    isReparse = (subDir.Attributes & FileAttributes.ReparsePoint) != 0;
                }
                catch
                {
                    continue;
                }

                candidateDirs.Add((name, subDir.FullName, isReparse));
            }

            foreach (var file in dirInfo.EnumerateFiles("*", immediateEnumOptions))
            {
                ct.ThrowIfCancellationRequested();
                string name = file.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                bool isReparse = false;
                long size = 0;
                try
                {
                    isReparse = (file.Attributes & FileAttributes.ReparsePoint) != 0;
                    if (!isReparse)
                    {
                        size = Math.Max(0L, file.Length);
                    }
                }
                catch
                {
                    continue;
                }

                rawEntries.Add((name, file.FullName, false, size, 1, isReparse));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore top-level enumeration errors
        }

        long scanStartTicks = Stopwatch.GetTimestamp();
        int totalDirs = Math.Max(1, candidateDirs.Count);
        int completedDirs = 0;

        Parallel.ForEach(
            candidateDirs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 6),
                CancellationToken = ct
            },
            entry =>
            {
                if (entry.IsReparsePoint)
                {
                    // Never follow symlinks / junctions
                    rawEntries.Add((entry.Name, entry.FullPath, true, 0L, 0, true));
                }
                else
                {
                    var (sizeBytes, fileCount) = MeasureFolderBounded(entry.FullPath, scanStartTicks, ct);
                    rawEntries.Add((entry.Name, entry.FullPath, true, sizeBytes, fileCount, false));
                }

                int done = Interlocked.Increment(ref completedDirs);
                int pct = Math.Clamp((int)(done * 100.0 / totalDirs), 1, 99);
                progress?.Report((pct, entry.Name));
            });

        var allEntries = rawEntries.ToList();
        allEntries.Sort(static (a, b) =>
        {
            int bySize = b.SizeBytes.CompareTo(a.SizeBytes);
            if (bySize != 0)
            {
                return bySize;
            }

            int byKind = b.IsDirectory.CompareTo(a.IsDirectory);
            if (byKind != 0)
            {
                return byKind;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        long totalMeasuredBytes = 0;
        foreach (var e in allEntries)
        {
            totalMeasuredBytes += e.SizeBytes;
        }

        var topEntries = allEntries.Take(takeCount).ToArray();
        long maxEntryBytes = topEntries.Length > 0 ? Math.Max(1L, topEntries[0].SizeBytes) : 1L;
        long effectiveTotal = Math.Max(1L, totalMeasuredBytes);

        var resultEntries = new List<DiskSpaceEntry>(topEntries.Length);
        foreach (var item in topEntries)
        {
            double ofParent = totalMeasuredBytes > 0
                ? Math.Clamp((double)item.SizeBytes / effectiveTotal * 100.0, 0.0, 100.0)
                : 0.0;
            double ofTop = item.SizeBytes > 0
                ? Math.Clamp((double)item.SizeBytes / maxEntryBytes * 100.0, 0.0, 100.0)
                : 0.0;

            var policy = DiskDeleteSafetyPolicy.EvaluatePath(item.FullPath, isReparsePoint: item.IsReparsePoint);

            resultEntries.Add(new DiskSpaceEntry(
                Name: item.Name,
                FullPath: item.FullPath,
                IsDirectory: item.IsDirectory,
                SizeBytes: item.SizeBytes,
                FileCount: item.FileCount,
                ShareOfParentPercent: Math.Round(ofParent, 1),
                ShareOfTopListPercent: Math.Round(ofTop, 1),
                IsProtectedPath: policy.IsProtectedPath,
                IsReparsePoint: policy.IsReparsePoint,
                ProtectionReason: policy.BlockReason));
        }

        return new FolderContentsReport(
            DriveLetter: driveLetter,
            CurrentPath: normalizedPath,
            ParentPath: parentPath,
            IsVolumeRoot: isVolumeRoot,
            TotalMeasuredBytes: totalMeasuredBytes,
            Entries: resultEntries);
    }

    public Task<DiskRecycleDeleteResult> DeleteToRecycleBinAsync(
        IReadOnlyList<string> fullPaths,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => DeleteToRecycleBinCore(fullPaths, cancellationToken), cancellationToken);
    }

    private DiskRecycleDeleteResult DeleteToRecycleBinCore(
        IReadOnlyList<string> fullPaths,
        CancellationToken ct)
    {
        if (fullPaths is null || fullPaths.Count == 0)
        {
            return new DiskRecycleDeleteResult(0, 0, 0, [], []);
        }

        var deletedPaths = new List<string>();
        var failures = new List<DiskDeleteFailure>();
        int blockedCount = 0;
        long freedBytes = 0;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawPath in fullPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string dedupKey = rawPath.Trim();
            if (!seen.Add(dedupKey))
            {
                continue;
            }

            var decision = DiskDeleteSafetyPolicy.EvaluateExistingPath(rawPath);
            if (!decision.IsAllowed)
            {
                blockedCount++;
                failures.Add(new DiskDeleteFailure(
                    rawPath,
                    decision.BlockReason ?? "Шлях заблоковано політикою безпеки."));
                continue;
            }

            string normalized = DiskDeleteSafetyPolicy.NormalizePath(rawPath);
            long estimatedBytes = EstimatePathSizeBytes(normalized, ct);

            var (succeeded, errorMessage) = _recycleBinDeleter.MoveToRecycleBin(normalized);
            if (succeeded)
            {
                deletedPaths.Add(normalized);
                freedBytes += estimatedBytes;
            }
            else
            {
                failures.Add(new DiskDeleteFailure(
                    normalized,
                    errorMessage ?? "Не вдалося перемістити елемент у Кошик."));
            }
        }

        return new DiskRecycleDeleteResult(
            DeletedCount: deletedPaths.Count,
            FreedBytes: freedBytes,
            BlockedCount: blockedCount,
            DeletedPaths: deletedPaths,
            Failures: failures);
    }

    private static long EstimatePathSizeBytes(string normalizedPath, CancellationToken ct)
    {
        try
        {
            if (File.Exists(normalizedPath))
            {
                return Math.Max(0L, new FileInfo(normalizedPath).Length);
            }

            if (Directory.Exists(normalizedPath))
            {
                var (sizeBytes, _) = MeasureFolderBounded(normalizedPath, Stopwatch.GetTimestamp(), ct);
                return Math.Max(0L, sizeBytes);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // best-effort size estimate
        }

        return 0L;
    }
}
