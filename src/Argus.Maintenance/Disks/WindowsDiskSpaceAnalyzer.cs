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
}
