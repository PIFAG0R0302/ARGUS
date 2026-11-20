using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Argus.Core.Contracts.Migration;
using Microsoft.Win32.SafeHandles;

namespace Argus.Migration.Engine;

public readonly record struct VolumeBlockChunk(long Offset, int Length);

/// <summary>
/// Compiled native C# port of the CopyOS double-buffered NTFS Volume Bitmap cloner.
/// Reads live VSS snapshot clusters via FSCTL_GET_VOLUME_BITMAP, coalesces contiguous active
/// runs up to 16 MiB, writes unbuffered to the locked target volume, and patches both primary
/// and backup NTFS VBR Volume Serial numbers so the cloned partition never collides with C:.
/// </summary>
public static partial class NtfsVssCloner
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagSequentialScan = 0x08000000;

    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlUnlockVolume = 0x0009001C;
    private const uint FsctlDismountVolume = 0x00090020;
    private const uint FsctlGetVolumeBitmap = 0x0009006F;
    private const uint FsctlAllowExtendedDasdIo = 0x00090083;

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    private const long BlockSize = 1024L * 1024L; // 1 MiB coalescing quantum
    private const int MaxIoBytes = 16 * 1024 * 1024; // 16 MiB double-buffer slot

    private struct BufferSlot
    {
        public long Offset;
        public int Length;
        public bool HasData;
    }

    internal static (List<VolumeBlockChunk> Chunks, long TotalBytesToCopy) BuildBlockMap(
        byte[] bitmap,
        long bitmapSizeClusters,
        long partitionSize,
        long bytesPerCluster)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (partitionSize <= 0 || bytesPerCluster <= 0)
        {
            return ([], 0);
        }

        int totalBlocks = (int)((partitionSize + BlockSize - 1) / BlockSize);
        bool[] active = new bool[totalBlocks];
        int clustersPerBlock = (int)Math.Max(1, BlockSize / bytesPerCluster);
        int bytesPerBlockBitmap = clustersPerBlock / 8;

        for (int b = 0; b < totalBlocks; b++)
        {
            long startCluster = (long)b * clustersPerBlock;
            long endCluster = Math.Min(startCluster + clustersPerBlock, bitmapSizeClusters);
            if (startCluster >= bitmapSizeClusters)
            {
                break;
            }

            if (clustersPerBlock >= 8 &&
                (clustersPerBlock & 7) == 0 &&
                (endCluster - startCluster) == clustersPerBlock &&
                (startCluster & 7) == 0)
            {
                int startByteIdx = (int)(startCluster >> 3);
                if (startByteIdx + bytesPerBlockBitmap <= bitmap.Length)
                {
                    for (int k = 0; k < bytesPerBlockBitmap; k++)
                    {
                        if (bitmap[startByteIdx + k] != 0)
                        {
                            active[b] = true;
                            break;
                        }
                    }

                    continue;
                }
            }

            for (long c = startCluster; c < endCluster; c++)
            {
                long byteIdx = c >> 3;
                if (byteIdx >= bitmap.Length)
                {
                    break;
                }

                if ((bitmap[byteIdx] & (1 << (int)(c & 7))) != 0)
                {
                    active[b] = true;
                    break;
                }
            }
        }

        // Always include first 8 MiB (VBR, $MFT head) and last 8 MiB (backup VBR, metadata tail)
        for (int b = 0; b < Math.Min(8, totalBlocks); b++)
        {
            active[b] = true;
        }

        for (int b = Math.Max(0, totalBlocks - 8); b < totalBlocks; b++)
        {
            active[b] = true;
        }

        // Bridge isolated 1 MiB gaps to avoid unnecessary disk seeks
        bool[] bridged = (bool[])active.Clone();
        for (int b = 1; b < totalBlocks - 1; b++)
        {
            if (!active[b] && active[b - 1] && active[b + 1])
            {
                bridged[b] = true;
            }
        }

        active = bridged;

        var chunks = new List<VolumeBlockChunk>();
        long totalBytesToCopy = 0;
        int idx = 0;
        while (idx < totalBlocks)
        {
            if (!active[idx])
            {
                idx++;
                continue;
            }

            int runStart = idx;
            while (idx < totalBlocks && active[idx] && (idx - runStart) < 16)
            {
                idx++;
            }

            long offset = (long)runStart * BlockSize;
            long endOffset = Math.Min((long)idx * BlockSize, partitionSize);
            int len = (int)(endOffset - offset);
            if (len > 0)
            {
                chunks.Add(new VolumeBlockChunk(offset, len));
                totalBytesToCopy += len;
            }
        }

        return (chunks, totalBytesToCopy);
    }

    internal static (byte OldByte, byte NewByte, long BackupVbrOffset) PatchNtfsVbrSerial(
        byte[] vbrSector,
        long partitionSize,
        int sectorSize)
    {
        ArgumentNullException.ThrowIfNull(vbrSector);
        if (vbrSector.Length < Math.Max(512, sectorSize))
        {
            throw new ArgumentException("VBR buffer is smaller than one logical sector.", nameof(vbrSector));
        }

        string oemStr = Encoding.ASCII.GetString(vbrSector, 3, 8);
        if (!string.Equals(oemStr, "NTFS    ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cloned sector 0 does not contain the 'NTFS    ' signature.");
        }

        long ntfsTotalSectors = BitConverter.ToInt64(vbrSector, 0x28);
        long backupVbrOffset = ntfsTotalSectors * sectorSize;
        if (backupVbrOffset <= 0 || backupVbrOffset + sectorSize > partitionSize)
        {
            backupVbrOffset = partitionSize - sectorSize;
        }

        byte oldByte = vbrSector[0x4B];
        byte newByte = unchecked((byte)(oldByte + 1));
        vbrSector[0x4B] = newByte;

        return (oldByte, newByte, backupVbrOffset);
    }

    public static void CloneVolume(
        string sourceRootPath,
        string sourceDevicePath,
        string targetVolumePath,
        long partitionSize,
        Action<string> log,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDevicePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVolumePath);
        ArgumentNullException.ThrowIfNull(log);

        if (!GetDiskFreeSpaceW(
                sourceRootPath,
                out uint sectorsPerCluster,
                out uint bytesPerSector,
                out _,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetDiskFreeSpaceW failed on {sourceRootPath}");
        }

        long bytesPerCluster = (long)sectorsPerCluster * bytesPerSector;
        int sectorSize = (int)bytesPerSector;
        log($"NTFS Geometry: BytesPerSector={bytesPerSector}, SectorsPerCluster={sectorsPerCluster}, BytesPerCluster={bytesPerCluster}");

        using SafeFileHandle hSource = CreateFileW(
            sourceDevicePath,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagSequentialScan,
            IntPtr.Zero);

        if (hSource.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFileW failed for source VSS: {sourceDevicePath}");
        }

        _ = DeviceIoControl(hSource, FsctlAllowExtendedDasdIo, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

        long estClusters = partitionSize / bytesPerCluster;
        int bitmapBufSize = (int)Math.Max((16 * 1024 * 1024) + 64, (estClusters / 8) + 1_048_576);
        IntPtr inBuf = Marshal.AllocHGlobal(8);
        IntPtr outBuf = VirtualAlloc(IntPtr.Zero, (UIntPtr)bitmapBufSize, MemCommit | MemReserve, PageReadWrite);
        if (outBuf == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(inBuf);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAlloc failed for NTFS bitmap buffer.");
        }

        byte[] bitmap;
        long bitmapSizeClusters;
        try
        {
            Marshal.WriteInt64(inBuf, 0, 0L);
            if (!DeviceIoControl(hSource, FsctlGetVolumeBitmap, inBuf, 8, outBuf, bitmapBufSize, out int bytesReturned, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_GET_VOLUME_BITMAP failed.");
            }

            long startingLcn = Marshal.ReadInt64(outBuf, 0);
            bitmapSizeClusters = Marshal.ReadInt64(outBuf, 8);
            if (startingLcn != 0)
            {
                throw new InvalidOperationException($"Unexpected StartingLcn={startingLcn}");
            }

            int rawBitmapBytes = Math.Max(0, bytesReturned - 16);
            bitmap = new byte[rawBitmapBytes];
            Marshal.Copy(IntPtr.Add(outBuf, 16), bitmap, 0, rawBitmapBytes);
            log(string.Format(
                CultureInfo.InvariantCulture,
                "Volume bitmap read: {0:N0} clusters ({1:N2} MB bitmap).",
                bitmapSizeClusters,
                rawBitmapBytes / (1024.0 * 1024.0)));
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            VirtualFree(outBuf, UIntPtr.Zero, MemRelease);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var (chunks, totalBytesToCopy) = BuildBlockMap(bitmap, bitmapSizeClusters, partitionSize, bytesPerCluster);
        log(string.Format(
            CultureInfo.InvariantCulture,
            "Block map ready: {0:N0} chunks, {1:N2} GiB to copy (out of {2:N2} GiB partition).",
            chunks.Count,
            totalBytesToCopy / (1024.0 * 1024.0 * 1024.0),
            partitionSize / (1024.0 * 1024.0 * 1024.0)));

        using SafeFileHandle hTarget = CreateFileW(
            targetVolumePath,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagNoBuffering | FileFlagWriteThrough,
            IntPtr.Zero);

        if (hTarget.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFileW failed for target volume: {targetVolumePath}");
        }

        bool locked = false;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DeviceIoControl(hTarget, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                locked = true;
                break;
            }

            Thread.Sleep(500);
        }

        if (!locked)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_LOCK_VOLUME failed on target volume.");
        }

        try
        {
            _ = DeviceIoControl(hTarget, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            _ = DeviceIoControl(hTarget, FsctlAllowExtendedDasdIo, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

            CopyChunksAndPatchVbr(hSource, hTarget, chunks, partitionSize, sectorSize, totalBytesToCopy, log, progress, cancellationToken);
        }
        finally
        {
            _ = DeviceIoControl(hTarget, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            _ = DeviceIoControl(hTarget, FsctlUnlockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
    }

    private static void CopyChunksAndPatchVbr(
        SafeFileHandle hSource,
        SafeFileHandle hTarget,
        List<VolumeBlockChunk> chunks,
        long partitionSize,
        int sectorSize,
        long totalBytesToCopy,
        Action<string> log,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        IntPtr[] bufs =
        [
            VirtualAlloc(IntPtr.Zero, (UIntPtr)MaxIoBytes, MemCommit | MemReserve, PageReadWrite),
            VirtualAlloc(IntPtr.Zero, (UIntPtr)MaxIoBytes, MemCommit | MemReserve, PageReadWrite)
        ];

        if (bufs[0] == IntPtr.Zero || bufs[1] == IntPtr.Zero)
        {
            if (bufs[0] != IntPtr.Zero)
            {
                VirtualFree(bufs[0], UIntPtr.Zero, MemRelease);
            }

            if (bufs[1] != IntPtr.Zero)
            {
                VirtualFree(bufs[1], UIntPtr.Zero, MemRelease);
            }

            throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAlloc failed for double-buffer I/O.");
        }

        object sync = new();
        var slots = new BufferSlot[2];
        bool readerDone = false;
        Exception? workerErr = null;
        Thread? readerThread = null;

        using var cancelReg = cancellationToken.Register(() =>
        {
            lock (sync)
            {
                workerErr ??= new OperationCanceledException(cancellationToken);
                Monitor.PulseAll(sync);
            }
        });

        try
        {
            readerThread = new Thread(() =>
            {
                int readSlot = 0;
                try
                {
                    foreach (var c in chunks)
                    {
                        lock (sync)
                        {
                            while (slots[readSlot].HasData && workerErr is null)
                            {
                                Monitor.Wait(sync);
                            }

                            if (workerErr is not null)
                            {
                                break;
                            }
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        if (!SetFilePointerEx(hSource, c.Offset, out _, 0))
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetFilePointerEx failed on source at {c.Offset}");
                        }

                        int totalRead = 0;
                        while (totalRead < c.Length)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!ReadFile(hSource, IntPtr.Add(bufs[readSlot], totalRead), c.Length - totalRead, out int br, IntPtr.Zero))
                            {
                                int err = Marshal.GetLastWin32Error();
                                if (c.Offset + totalRead >= partitionSize - (sectorSize * 8L))
                                {
                                    break;
                                }

                                throw new Win32Exception(err, $"ReadFile failed on source at offset {c.Offset + totalRead}");
                            }

                            if (br == 0)
                            {
                                if (c.Offset + totalRead >= partitionSize - (sectorSize * 8L))
                                {
                                    break;
                                }

                                throw new EndOfStreamException($"Unexpected EOF on source at offset {c.Offset + totalRead}");
                            }

                            totalRead += br;
                        }

                        if (totalRead < c.Length)
                        {
                            RtlZeroMemory(IntPtr.Add(bufs[readSlot], totalRead), (UIntPtr)(c.Length - totalRead));
                        }

                        lock (sync)
                        {
                            slots[readSlot].Offset = c.Offset;
                            slots[readSlot].Length = c.Length;
                            slots[readSlot].HasData = true;
                            Monitor.PulseAll(sync);
                        }

                        readSlot = 1 - readSlot;
                    }
                }
                catch (Exception ex)
                {
                    lock (sync)
                    {
                        workerErr ??= ex;
                        Monitor.PulseAll(sync);
                    }
                }
                finally
                {
                    lock (sync)
                    {
                        readerDone = true;
                        Monitor.PulseAll(sync);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "Argus-NtfsVssReader"
            };

            readerThread.Start();

            var sw = Stopwatch.StartNew();
            long bytesCopied = 0;
            int writeSlot = 0;
            int nextLogPct = 5;
            double lastUiSec = -10.0;

            try
            {
                while (true)
                {
                    long offset;
                    int len;
                    lock (sync)
                    {
                        while (!slots[writeSlot].HasData && !readerDone && workerErr is null)
                        {
                            Monitor.Wait(sync);
                        }

                        if (workerErr is not null)
                        {
                            throw workerErr;
                        }

                        if (!slots[writeSlot].HasData && readerDone)
                        {
                            break;
                        }

                        offset = slots[writeSlot].Offset;
                        len = slots[writeSlot].Length;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!SetFilePointerEx(hTarget, offset, out _, 0))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetFilePointerEx failed on target at {offset}");
                    }

                    int totalWritten = 0;
                    while (totalWritten < len)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!WriteFile(hTarget, IntPtr.Add(bufs[writeSlot], totalWritten), len - totalWritten, out int bw, IntPtr.Zero))
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteFile failed on target at offset {offset + totalWritten}");
                        }

                        if (bw == 0)
                        {
                            throw new IOException($"WriteFile wrote 0 bytes at offset {offset + totalWritten}");
                        }

                        totalWritten += bw;
                    }

                    lock (sync)
                    {
                        slots[writeSlot].HasData = false;
                        Monitor.PulseAll(sync);
                    }

                    writeSlot = 1 - writeSlot;

                    bytesCopied += len;
                    int pct = totalBytesToCopy > 0 ? (int)Math.Clamp((bytesCopied * 100L) / totalBytesToCopy, 0, 100) : 100;
                    double elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                    double speedMBs = (bytesCopied / (1024.0 * 1024.0)) / elapsedSec;
                    double etaSec = (totalBytesToCopy - bytesCopied) / Math.Max(1.0, bytesCopied / elapsedSec);
                    var eta = TimeSpan.FromSeconds(Math.Max(0, etaSec));

                    if ((elapsedSec - lastUiSec) >= 0.45 || pct >= nextLogPct || bytesCopied >= totalBytesToCopy)
                    {
                        int overallPct = 20 + (int)(pct * 0.55); // 20%..75% overall wizard progress
                        progress?.Report(new MigrationProgress(
                            MigrationPhase.CloningData,
                            Math.Clamp(overallPct, 20, 75),
                            bytesCopied,
                            totalBytesToCopy,
                            Math.Round(speedMBs, 1),
                            eta,
                            $"Блокове VSS-клонування: {pct}% ({speedMBs:0.0} МБ/с)"));
                        lastUiSec = elapsedSec;
                    }

                    if (pct >= nextLogPct || bytesCopied >= totalBytesToCopy)
                    {
                        log(string.Format(
                            CultureInfo.InvariantCulture,
                            "Progress: {0}% ({1:N2} / {2:N2} GiB) | Speed: {3:N1} MB/s | ETA: {4:hh\\:mm\\:ss}",
                            pct,
                            bytesCopied / (1024.0 * 1024.0 * 1024.0),
                            totalBytesToCopy / (1024.0 * 1024.0 * 1024.0),
                            speedMBs,
                            eta));
                        while (nextLogPct <= pct)
                        {
                            nextLogPct += 5;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (sync)
                {
                    workerErr ??= ex;
                    Monitor.PulseAll(sync);
                }

                throw;
            }
            finally
            {
                readerThread.Join();
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Patch primary (sector 0) and backup NTFS VBR Volume Serial Number so it is unique from C:
            if (!SetFilePointerEx(hTarget, 0, out _, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Seek to sector 0 failed.");
            }

            if (!ReadFile(hTarget, bufs[0], sectorSize, out int vbrRead, IntPtr.Zero) || vbrRead != sectorSize)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read sector 0 failed.");
            }

            byte[] vbrBytes = new byte[sectorSize];
            Marshal.Copy(bufs[0], vbrBytes, 0, sectorSize);

            var (oldByte, newByte, backupVbrOffset) = PatchNtfsVbrSerial(vbrBytes, partitionSize, sectorSize);
            Marshal.Copy(vbrBytes, 0, bufs[0], sectorSize);

            log(string.Format(
                CultureInfo.InvariantCulture,
                "Updated NTFS Volume Serial byte 0x4B: 0x{0:X2} -> 0x{1:X2} (Backup VBR offset={2})",
                oldByte,
                newByte,
                backupVbrOffset));

            if (!SetFilePointerEx(hTarget, 0, out _, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Seek to sector 0 for write failed.");
            }

            if (!WriteFile(hTarget, bufs[0], sectorSize, out int vbrWritten, IntPtr.Zero) || vbrWritten != sectorSize)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Write updated sector 0 failed.");
            }

            if (!SetFilePointerEx(hTarget, backupVbrOffset, out _, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Seek to backup boot sector failed.");
            }

            if (!WriteFile(hTarget, bufs[0], sectorSize, out vbrWritten, IntPtr.Zero) || vbrWritten != sectorSize)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Write updated backup boot sector failed.");
            }

            FlushFileBuffers(hTarget);
            log(string.Format(CultureInfo.InvariantCulture, "Block clone completed in {0:hh\\:mm\\:ss}.", sw.Elapsed));
        }
        finally
        {
            if (readerThread is { IsAlive: true })
            {
                readerThread.Join();
            }

            VirtualFree(bufs[0], UIntPtr.Zero, MemRelease);
            VirtualFree(bufs[1], UIntPtr.Zero, MemRelease);
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
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
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle hFile);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceW(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAlloc(
        IntPtr lpAddress,
        UIntPtr dwSize,
        uint flAllocationType,
        uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(
        IntPtr lpAddress,
        UIntPtr dwSize,
        uint dwFreeType);

    [LibraryImport("kernel32.dll")]
    private static partial void RtlZeroMemory(IntPtr dst, UIntPtr length);
}
