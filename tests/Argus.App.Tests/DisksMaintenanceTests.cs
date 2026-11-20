using Argus.App.ViewModels;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Argus.Core.Journal;
using Argus.Maintenance.Disks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class DisksMaintenanceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusDisksTests_{Guid.NewGuid():N}");

    public DisksMaintenanceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void MediaClassificationAndDefragArgs_EnforceTrimForSsdAndDefragForHdd()
    {
        Assert.Equal(StorageMediaKind.NvmeSsd, WindowsDiskOptimizer.ClassifyMediaKind(4, 17, 0, "Samsung SSD 980 PRO"));
        Assert.Equal(StorageMediaKind.SataSsd, WindowsDiskOptimizer.ClassifyMediaKind(4, 11, 0, "SPCC Solid State Disk"));
        Assert.Equal(StorageMediaKind.Hdd, WindowsDiskOptimizer.ClassifyMediaKind(3, 11, uint.MaxValue, "WDC WD10EZEX-08WN4A0"));
        Assert.Equal(StorageMediaKind.RemovableUsb, WindowsDiskOptimizer.ClassifyMediaKind(0, 7, 0, "SanDisk Extreme USB"));

        Assert.Equal("C: /L /U", WindowsDiskOptimizer.BuildDefragArguments('c', DiskOptimizationAction.TrimSsd));
        Assert.Equal("D: /O /U", WindowsDiskOptimizer.BuildDefragArguments('d', DiskOptimizationAction.DefragmentHdd));
        Assert.Equal("E: /A /U", WindowsDiskOptimizer.BuildDefragArguments('e', DiskOptimizationAction.AnalyzeOnly));
    }

    [Fact]
    public async Task WindowsDiskOptimizer_BlocksNonAdminAndExecutesSafeActionWhenAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "disk-logs", "journal.jsonl"));

        var defragStub = new StubDefragRunner();

        // Non-admin attempt must be blocked safely without calling defrag.exe
        var nonAdminOptimizer = new WindowsDiskOptimizer(new StubPrivilegeService(false), journal, defragStub);
        var blockedResult = await nonAdminOptimizer.OptimizeVolumeAsync('C', cancellationToken: ct);
        Assert.False(blockedResult.Succeeded);
        Assert.Equal(0, defragStub.CallCount);
        Assert.Contains(journal.GetRecent(10), static j => j.Action == "DiskOptimizeBlockedNotAdmin");

        // Admin run on live workstation resolves real media kind of C: (SSD -> /L /U, HDD -> /O /U)
        var adminOptimizer = new WindowsDiskOptimizer(new StubPrivilegeService(true), journal, defragStub);
        var okResult = await adminOptimizer.OptimizeVolumeAsync('C', cancellationToken: ct);
        Assert.True(okResult.Succeeded);
        Assert.Equal(1, defragStub.CallCount);
        Assert.StartsWith("C: ", defragStub.LastArgs, StringComparison.Ordinal);

        if (okResult.MediaKind is StorageMediaKind.NvmeSsd or StorageMediaKind.SataSsd)
        {
            Assert.Equal("C: /L /U", defragStub.LastArgs);
            Assert.Equal(DiskOptimizationAction.TrimSsd, okResult.ActionExecuted);
        }

        Assert.Contains(journal.GetRecent(10), static j => j.Action == "DiskVolumeOptimized");
    }

    [Fact]
    public async Task WindowsDiskSpaceAnalyzer_ComputesRankedFolderSpaceMapCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        string root = Path.Combine(_tempDir, "fake-drive");
        string bigDir = Path.Combine(root, "Games");
        string medDir = Path.Combine(root, "Projects");
        string smallDir = Path.Combine(root, "Docs");
        string sysDir = Path.Combine(root, "System Volume Information");

        Directory.CreateDirectory(bigDir);
        Directory.CreateDirectory(medDir);
        Directory.CreateDirectory(smallDir);
        Directory.CreateDirectory(sysDir);

        await File.WriteAllBytesAsync(Path.Combine(bigDir, "pak1.bin"), new byte[10_240], ct);
        await File.WriteAllBytesAsync(Path.Combine(bigDir, "pak2.bin"), new byte[10_240], ct);
        await File.WriteAllBytesAsync(Path.Combine(medDir, "src.zip"), new byte[5_120], ct);
        await File.WriteAllBytesAsync(Path.Combine(smallDir, "notes.txt"), new byte[1_024], ct);
        await File.WriteAllBytesAsync(Path.Combine(sysDir, "hidden.dat"), new byte[40_960], ct);

        var report = WindowsDiskSpaceAnalyzer.AnalyzeDirectoryRoot(
            root,
            'Z',
            "TestDrive",
            "NTFS",
            totalSizeBytes: 100_000,
            usedBytes: 40_000,
            freeBytes: 60_000,
            topFolderCount: 5,
            ct);

        Assert.Equal(3, report.LargestFolders.Count); // System Volume Information skipped
        Assert.Equal("Games", report.LargestFolders[0].Name);
        Assert.Equal(20_480, report.LargestFolders[0].SizeBytes);
        Assert.Equal(100.0, report.LargestFolders[0].ShareOfTopListPercent);
        Assert.Equal("Projects", report.LargestFolders[1].Name);
        Assert.Equal(25.0, report.LargestFolders[1].ShareOfTopListPercent);
        Assert.Equal("Docs", report.LargestFolders[2].Name);
    }

    [Fact]
    public async Task DisksViewModel_LoadsDisksOptimizesVolumeAndSwitchesSpaceMap()
    {
        var optimizer = new StubDiskOptimizer();
        var analyzer = new StubSpaceAnalyzer();

        var vm = new DisksViewModel(optimizer, analyzer, autoLoadOnInit: false);
        await vm.RefreshDisksAsync();

        Assert.Equal(2, vm.Disks.Count);
        Assert.Equal("Оптимізація SSD (C:)", vm.Disks[0].OptimizeButtonLabel);
        Assert.Equal("Дефрагментація HDD (D:)", vm.Disks[1].OptimizeButtonLabel);
        Assert.Equal(2, vm.VolumeTabs.Count);
        Assert.Equal('C', vm.SelectedVolumeLetter);
        Assert.Single(vm.LargestFolders);
        Assert.Equal("Windows", vm.LargestFolders[0].Name);

        await vm.OptimizeVolumeAsync('C');
        Assert.Equal('C', optimizer.LastOptimizedLetter);
        Assert.Contains("TRIM тома C:", vm.StatusBannerText, StringComparison.Ordinal);

        await vm.SelectVolumeForSpaceMapAsync('D');
        Assert.Equal('D', vm.SelectedVolumeLetter);
        Assert.False(vm.VolumeTabs[0].IsSelected);
        Assert.True(vm.VolumeTabs[1].IsSelected);
        Assert.Equal("Archive", vm.LargestFolders[0].Name);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private sealed class StubPrivilegeService(bool isAdmin) : IPrivilegeService
    {
        public bool IsAdministrator { get; } = isAdmin;
        public bool TryRelaunchElevated(string[]? arguments = null) => false;
    }

    private sealed class StubDefragRunner : IDefragRunner
    {
        public int CallCount { get; private set; }
        public string LastArgs { get; private set; } = string.Empty;

        public Task<(int ExitCode, string Output)> RunDefragAsync(
            string arguments,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastArgs = arguments;
            progress?.Report("100% completed.");
            return Task.FromResult((0, "The operation completed successfully."));
        }
    }

    private sealed class StubDiskOptimizer : IDiskOptimizer
    {
        public char LastOptimizedLetter { get; private set; }

        public Task<IReadOnlyList<PhysicalDiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<PhysicalDiskInfo> list =
            [
                new(2, "SPCC Solid State Disk", "SN-SSD", "SATA", StorageMediaKind.SataSsd, 256_000_000_000, "GPT", ['C'], new SmartSummary(SmartHealthStatus.Healthy, 35, 98, 1200, 0, 0)),
                new(0, "WDC WD10EZEX-08WN4A0", "SN-HDD", "SATA", StorageMediaKind.Hdd, 1_000_000_000_000, "GPT", ['D'], new SmartSummary(SmartHealthStatus.Healthy, 32, null, 8500, 0, 0))
            ];
            return Task.FromResult(list);
        }

        public Task<DiskOptimizationResult> OptimizeVolumeAsync(char driveLetter, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            LastOptimizedLetter = driveLetter;
            return Task.FromResult(new DiskOptimizationResult(
                driveLetter,
                StorageMediaKind.SataSsd,
                DiskOptimizationAction.TrimSsd,
                Succeeded: true,
                TimeSpan.FromSeconds(1.2),
                $"TRIM тома {driveLetter}: успішно завершено."));
        }
    }

    private sealed class StubSpaceAnalyzer : IDiskSpaceAnalyzer
    {
        public Task<VolumeSpaceReport> AnalyzeVolumeSpaceAsync(
            char driveLetter,
            int topFolderCount = 8,
            CancellationToken cancellationToken = default,
            IProgress<(int Percent, string FolderName)>? progress = null)
        {
            var folder = driveLetter == 'D'
                ? new FolderSpaceNode("Archive", @"D:\Archive", 50_000_000_000, 400, 70.0, 100.0)
                : new FolderSpaceNode("Windows", @"C:\Windows", 32_000_000_000, 5000, 45.0, 100.0);

            return Task.FromResult(new VolumeSpaceReport(
                driveLetter,
                driveLetter == 'D' ? "Data" : "System",
                "NTFS",
                250_000_000_000,
                100_000_000_000,
                150_000_000_000,
                [folder]));
        }
    }
}
