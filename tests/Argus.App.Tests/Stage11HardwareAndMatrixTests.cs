using System.Collections.Concurrent;
using System.Diagnostics;
using Argus.App.ViewModels;
using Argus.Core.Collections;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.Migration;
using Argus.Core.Contracts.System;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Core.Journal;
using Argus.Maintenance.Cleanup;
using Argus.Maintenance.Disks;
using Argus.Migration.Engine;
using Argus.Telemetry.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class Stage11HardwareAndMatrixTests : IDisposable
{
    private const long GiB = 1024L * 1024L * 1024L;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusStage11Tests_{Guid.NewGuid():N}");

    public Stage11HardwareAndMatrixTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Theory]
    [InlineData(MigrationProfile.InternalMotherboardDisk, CloneMode.BlockVssBitmap, PartitionStyleChoice.GptUefi)]
    [InlineData(MigrationProfile.InternalMotherboardDisk, CloneMode.BlockVssBitmap, PartitionStyleChoice.MbrLegacy)]
    [InlineData(MigrationProfile.InternalMotherboardDisk, CloneMode.DismShrinkToFit, PartitionStyleChoice.GptUefi)]
    [InlineData(MigrationProfile.InternalMotherboardDisk, CloneMode.DismShrinkToFit, PartitionStyleChoice.MbrLegacy)]
    [InlineData(MigrationProfile.ExternalUsbDrive, CloneMode.BlockVssBitmap, PartitionStyleChoice.GptUefi)]
    [InlineData(MigrationProfile.ExternalUsbDrive, CloneMode.BlockVssBitmap, PartitionStyleChoice.MbrLegacy)]
    [InlineData(MigrationProfile.ExternalUsbDrive, CloneMode.DismShrinkToFit, PartitionStyleChoice.GptUefi)]
    [InlineData(MigrationProfile.ExternalUsbDrive, CloneMode.DismShrinkToFit, PartitionStyleChoice.MbrLegacy)]
    public async Task CopyOS_FullEightWayMatrix_ExecutesCleanlyAndAlwaysCleansResources(
        MigrationProfile profile,
        CloneMode requestedMode,
        PartitionStyleChoice partitionStyle)
    {
        var ct = TestContext.Current.CancellationToken;
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(
            eventBus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, $"journal_{profile}_{requestedMode}_{partitionStyle}.jsonl"));
        string logsDir = Path.Combine(_tempDir, $"logs_{profile}_{requestedMode}_{partitionStyle}");
        Directory.CreateDirectory(logsDir);

        string bus = profile == MigrationProfile.ExternalUsbDrive ? "USB" : "NVMe";
        bool smallerTarget = requestedMode == CloneMode.DismShrinkToFit;
        var host = new MatrixFakeMigrationHost(bus, smallerTarget);
        var engine = new WindowsMigrationEngine(
            host,
            new Stage11AdminPrivilegeService(isAdmin: true),
            journal,
            NullLogger<WindowsMigrationEngine>.Instance,
            logsDir);

        var request = new MigrationPlanRequest(
            Profile: profile,
            TargetDiskNumber: 1,
            PartitionStyle: partitionStyle,
            RequestedCloneMode: CloneMode.Auto,
            RegisterUefiBootPriorityFirst: profile == MigrationProfile.InternalMotherboardDisk && partitionStyle == PartitionStyleChoice.GptUefi,
            CopyRedirectedDesktop: profile == MigrationProfile.InternalMotherboardDisk);

        string token = MigrationConfirmationPolicy.BuildExpectedToken(1);
        var phases = new List<MigrationPhase>();
        var progress = new SyncProgress<MigrationProgress>(p => phases.Add(p.Phase));

        var result = await engine.ExecuteAsync(request, token, progress, ct);

        Assert.True(result.Succeeded, result.Summary);
        Assert.False(result.WasCancelled);
        Assert.Equal(profile, result.Profile);
        Assert.Equal(requestedMode, result.UsedCloneMode);
        Assert.Equal(partitionStyle, result.PartitionStyle);
        Assert.NotNull(result.LogFilePath);
        Assert.True(File.Exists(result.LogFilePath));

        Assert.True(host.VssCreated);
        Assert.True(host.CleanupCalled);
        Assert.True(host.OfflineRegistryUpdated);
        Assert.True(host.BootloaderInstalled);

        if (requestedMode == CloneMode.BlockVssBitmap)
        {
            Assert.True(host.BlockCloneCalled);
            Assert.True(host.BlockExtendCalled);
            Assert.False(host.DismCalled);
        }
        else
        {
            Assert.False(host.BlockCloneCalled);
            Assert.False(host.BlockExtendCalled);
            Assert.True(host.DismCalled);
        }

        if (profile == MigrationProfile.ExternalUsbDrive || request.CopyRedirectedDesktop)
        {
            Assert.True(host.RedirectedFoldersFixed);
        }

        Assert.Contains(MigrationPhase.CreatingVssSnapshot, phases);
        Assert.Contains(MigrationPhase.InstallingBootloader, phases);
        Assert.Contains(MigrationPhase.Completed, phases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyOS_MidFlightCancellationAndFaultInjection_GuaranteeCleanupInFinally(bool injectBootloaderFault)
    {
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(
            eventBus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, $"journal_fault_{injectBootloaderFault}.jsonl"));
        string logsDir = Path.Combine(_tempDir, $"logs_fault_{injectBootloaderFault}");
        Directory.CreateDirectory(logsDir);

        using var cts = new CancellationTokenSource();
        var host = new MatrixFakeMigrationHost("SATA", smallerTarget: false)
        {
            CancelTokenDuringClone = injectBootloaderFault ? null : cts,
            ThrowOnBootloader = injectBootloaderFault
        };

        var engine = new WindowsMigrationEngine(
            host,
            new Stage11AdminPrivilegeService(isAdmin: true),
            journal,
            NullLogger<WindowsMigrationEngine>.Instance,
            logsDir);

        var request = new MigrationPlanRequest(
            MigrationProfile.InternalMotherboardDisk,
            TargetDiskNumber: 1,
            PartitionStyleChoice.GptUefi,
            CloneMode.BlockVssBitmap);

        var result = await engine.ExecuteAsync(
            request,
            MigrationConfirmationPolicy.BuildExpectedToken(1),
            progress: null,
            cancellationToken: cts.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(!injectBootloaderFault, result.WasCancelled);
        Assert.True(host.VssCreated);
        Assert.True(host.CleanupCalled);
    }

    [Fact]
    public void MultiVendorHardwareMatrix_AndUiVisibilityGate_HandleIntelAmdNvidiaAndTrayPause()
    {
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var settingsStore = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "settings_hw.json"));
        var fakeTelemetry = new Stage11FakeTelemetryService();
        var tierProvider = new FakeSensorTierStatusProvider();

        using var monitoring = new MonitoringViewModel(
            settingsStore,
            fakeTelemetry,
            tierProvider,
            eventBus,
            uiDispatch: static a => a());

        var scenarios = new[]
        {
            // 1. AMD Ryzen + Radeon (WDDM fallback, no GPU temp, no PawnIO CPU temp, Windows 10/11 HVCI)
            CreateHardwareSnapshot("AMD Ryzen 7 7800X3D", 8, 16, 24.5, 4850, null, "AMD Radeon RX 7800 XT", "AMD", 42.0, null, 16 * GiB, 3 * GiB, null),
            // 2. Intel Core + Intel Arc / UHD (zero-driver Tier 1, null disk temp)
            CreateHardwareSnapshot("Intel(R) Core(TM) i5-13600K", 14, 20, 18.2, 5100, null, "Intel(R) Arc(TM) A750 Graphics", "Intel", 31.0, null, 8 * GiB, 2 * GiB, null),
            // 3. Intel Core + NVIDIA RTX (NVML full telemetry + disk temperature)
            CreateHardwareSnapshot("Intel(R) Core(TM) i7-9700K", 8, 8, 52.0, 4600, null, "NVIDIA GeForce RTX 4070", "NVIDIA", 66.0, 62.0, 12 * GiB, 5 * GiB, 38.0),
            // 4. Hybrid Dual-GPU Laptop (NVIDIA dGPU + Intel iGPU)
            CreateHardwareSnapshot("Intel(R) Core(TM) i7-13700H", 14, 20, 35.0, 3900, 71.0, "NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", 55.0, 68.0, 8 * GiB, 4 * GiB, 41.0)
        };

        foreach (var snap in scenarios)
        {
            fakeTelemetry.Push(snap);
            eventBus.Publish(new TelemetryUpdatedEvent(snap));

            Assert.True(monitoring.HasLiveSample);
            Assert.Equal(snap.Cpu.Name, monitoring.CpuModelName);
            Assert.Equal(snap.Cpu.LogicalProcessors, monitoring.CoreLoads.Count);
            Assert.Equal(snap.Gpus[0].Name, monitoring.GpuNameText);
            Assert.NotEmpty(monitoring.CpuSeries);
        }

        // Simulate minimizing to system tray -> UI updates must be suppressed until restored
        string lastCpuBeforeHide = monitoring.CpuModelName;
        monitoring.SetUiActive(false);

        var hiddenSnap = CreateHardwareSnapshot("AMD EPYC Hidden Sample", 16, 32, 99.0, 3500, null, "AMD Radeon Pro", "AMD", 88.0, 77.0, 16 * GiB, 8 * GiB, 45.0);
        fakeTelemetry.Push(hiddenSnap);
        eventBus.Publish(new TelemetryUpdatedEvent(hiddenSnap));

        Assert.Equal(lastCpuBeforeHide, monitoring.CpuModelName);

        // Restoring window immediately catches up with latest snapshot
        monitoring.SetUiActive(true);
        Assert.Equal("AMD EPYC Hidden Sample", monitoring.CpuModelName);
    }

    [Fact]
    public async Task RingBuffer_SnapshotTail_ConcurrentSoak_PreservesOrderAndBoundedCapacity()
    {
        const int capacity = 3600;
        const int totalWrites = 10_000;
        var buffer = new RingBuffer<int>(capacity);
        var errors = new ConcurrentBag<Exception>();
        using var done = new ManualResetEventSlim(false);

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!done.IsSet)
                {
                    var tail60 = buffer.SnapshotTail(60);
                    for (int i = 1; i < tail60.Length; i++)
                    {
                        if (tail60[i] <= tail60[i - 1])
                        {
                            throw new InvalidOperationException($"Out-of-order tail sample: {tail60[i - 1]} -> {tail60[i]}");
                        }
                    }

                    Assert.True(tail60.Length <= 60);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        })).ToArray();

        for (int i = 1; i <= totalWrites; i++)
        {
            buffer.Add(i);
        }

        done.Set();
        await Task.WhenAll(readers);

        Assert.Empty(errors);
        Assert.Equal(capacity, buffer.Count);

        int[] finalTail = buffer.SnapshotTail(60);
        Assert.Equal(60, finalTail.Length);
        Assert.Equal(totalWrites - 59, finalTail[0]);
        Assert.Equal(totalWrites, finalTail[^1]);
    }

    [Fact]
    public async Task CleanerAndSpaceAnalyzer_RealTempSandboxWithJunction_NeverTraversesReparsePoints()
    {
        var ct = TestContext.Current.CancellationToken;
        string targetRoot = Path.Combine(_tempDir, "clean_target");
        string keepSubdir = Path.Combine(targetRoot, "KeepMe");
        string outsideSecret = Path.Combine(_tempDir, "outside_protected");
        string junctionPath = Path.Combine(targetRoot, "LinkToOutside");

        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(keepSubdir);
        Directory.CreateDirectory(outsideSecret);

        string oldFile = Path.Combine(targetRoot, "stale_cache.tmp");
        string freshFile = Path.Combine(targetRoot, "fresh_active.tmp");
        string whitelistedFile = Path.Combine(keepSubdir, "important.tmp");
        string secretFile = Path.Combine(outsideSecret, "do_not_delete.txt");

        await File.WriteAllBytesAsync(oldFile, new byte[4096], ct);
        await File.WriteAllBytesAsync(freshFile, new byte[2048], ct);
        await File.WriteAllBytesAsync(whitelistedFile, new byte[4096], ct);
        await File.WriteAllBytesAsync(secretFile, new byte[4096], ct);

        DateTime oldUtc = DateTime.UtcNow.AddHours(-5);
        File.SetLastWriteTimeUtc(oldFile, oldUtc);
        File.SetLastWriteTimeUtc(whitelistedFile, oldUtc);
        File.SetLastWriteTimeUtc(secretFile, oldUtc);

        // Create a real Windows NTFS directory junction (requires no admin rights via cmd mklink /J)
        using (var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{outsideSecret}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }))
        {
            Assert.NotNull(proc);
            await proc.WaitForExitAsync(ct);
            Assert.Equal(0, proc.ExitCode);
        }

        Assert.True((File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0);

        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(
            eventBus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "journal_clean.jsonl"));
        var cleaner = new WindowsCleanerService(
            new NoOpRestorePointService(),
            journal,
            new NoOpRecycleBinInterop());

        var rule = new CleanerRuleDefinition(
            Id: "stage11.sandbox.tmp",
            DisplayName: "Stage 11 Sandbox Rule",
            Category: CleanerCategory.UserTemp,
            Description: "Sandboxed temp rule for junction and age verification",
            Paths: [targetRoot],
            FilePatterns: ["*.tmp", "*.txt"],
            Recursive: true,
            MinFileAgeHours: 1,
            RequiresElevation: false,
            EnabledByDefault: true);

        var scan = await cleaner.AnalyzeAsync([rule], [keepSubdir], ct);
        Assert.Equal(1, scan.TotalFiles);
        Assert.Equal(4096, scan.TotalBytes);

        var cleanResult = await cleaner.CleanAsync(
            scan,
            new HashSet<string> { rule.Id },
            [keepSubdir],
            createRestorePointFirst: false,
            ct);
        Assert.Empty(cleanResult.Errors);
        Assert.Equal(1, cleanResult.FilesDeleted);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(freshFile));
        Assert.True(File.Exists(whitelistedFile));
        Assert.True(File.Exists(secretFile));

        // Space analyzer must also report incremental progress safely on real volume C:
        var analyzer = new WindowsDiskSpaceAnalyzer();
        var dirProgress = new List<(int Percent, string Folder)>();
        var volume = await analyzer.AnalyzeVolumeSpaceAsync(
            'C',
            topFolderCount: 3,
            cancellationToken: ct,
            progress: new SyncProgress<(int, string)>(p => dirProgress.Add(p)));

        Assert.Equal('C', volume.DriveLetter);
        Assert.True(volume.TotalSizeBytes > 0);
        Assert.NotEmpty(dirProgress);
    }

    public void Dispose()
    {
        try
        {
            string junctionPath = Path.Combine(_tempDir, "clean_target", "LinkToOutside");
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath, recursive: false);
            }

            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup races
        }
    }

    private static TelemetrySnapshot CreateHardwareSnapshot(
        string cpuName,
        int physicalCores,
        int logicalCores,
        double cpuLoad,
        double cpuClock,
        double? cpuTemp,
        string gpuName,
        string gpuVendor,
        double gpuLoad,
        double? gpuTemp,
        long vramTotal,
        long vramUsed,
        double? diskTemp)
    {
        var perCore = Enumerable.Range(0, logicalCores).Select(i => Math.Min(100.0, cpuLoad + (i % 5))).ToArray();
        return new TelemetrySnapshot(
            Timestamp: DateTimeOffset.UtcNow,
            ActiveHighestTier: cpuTemp.HasValue ? SensorTier.SignedPawnIo : SensorTier.NativeZeroDriver,
            Cpu: new CpuSnapshot(cpuName, physicalCores, logicalCores, cpuLoad, perCore, cpuClock, cpuTemp, null),
            Memory: new MemorySnapshot(32 * GiB, 14 * GiB, 18 * GiB, 43.75),
            Gpus: [new GpuSnapshot(gpuName, gpuVendor, gpuLoad, vramTotal, vramUsed, gpuTemp, 1950, 1200)],
            Storage: [new StorageThermalSnapshot(0, "Test SSD", diskTemp, 12_000_000, 4_000_000)],
            ExtraSensors: []);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class Stage11AdminPrivilegeService(bool isAdmin) : IPrivilegeService
    {
        public bool IsAdministrator => isAdmin;
        public bool TryRelaunchElevated(string[]? arguments = null) => false;
    }

    private sealed class NoOpRestorePointService : IRestorePointService
    {
        public Task<RestorePointResult> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default)
            => Task.FromResult(new RestorePointResult(true, description, "OK"));
    }

    private sealed class NoOpRecycleBinInterop : IRecycleBinInterop
    {
        public (int ItemCount, long TotalBytes) QueryRecycleBin() => (0, 0);
        public bool EmptyRecycleBin() => true;
    }

    private sealed class FakeSensorTierStatusProvider : ISensorTierStatusProvider
    {
        public PawnIoAvailabilityState GetPawnIoState() => PawnIoAvailabilityState.DisabledBySetting;
        public string GetStatusSummary() => "Рівень 1: без сторонніх драйверів";
    }

    private sealed class Stage11FakeTelemetryService : ITelemetryService
    {
        private readonly RingBuffer<TelemetrySnapshot> _buffer = new(180);
        public bool IsRunning => true;
        public TelemetrySnapshot? Latest { get; private set; }
        public void Push(TelemetrySnapshot snap)
        {
            Latest = snap;
            _buffer.Add(snap);
        }

        public IReadOnlyList<TelemetrySnapshot> GetHistory(int maxSamples = 180) => _buffer.SnapshotTail(maxSamples);
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MatrixFakeMigrationHost(string targetBus, bool smallerTarget) : IMigrationHostOperations
    {
        public bool VssCreated { get; private set; }
        public bool BlockCloneCalled { get; private set; }
        public bool BlockExtendCalled { get; private set; }
        public bool DismCalled { get; private set; }
        public bool RedirectedFoldersFixed { get; private set; }
        public bool OfflineRegistryUpdated { get; private set; }
        public bool BootloaderInstalled { get; private set; }
        public bool CleanupCalled { get; private set; }
        public CancellationTokenSource? CancelTokenDuringClone { get; init; }
        public bool ThrowOnBootloader { get; init; }

        public Task<SourceOsInfo> InspectLiveOsAsync(CancellationToken cancellationToken) => Task.FromResult(new SourceOsInfo(
            OsCaption: "Microsoft Windows 11 Pro (x64)",
            SystemDriveLetter: 'C',
            SourceDiskNumber: 0,
            SourceDiskModel: "Samsung SSD 980 PRO 500GB",
            SourceBusType: "NVMe",
            PartitionSizeBytes: 240L * GiB,
            UsedBytes: 85L * GiB,
            LogicalSectorSize: 512,
            Is64Bit: true,
            IsUefiHost: true,
            BitLockerStatus: "FullyDecrypted"));

        public Task<IReadOnlyList<MigrationTargetDisk>> DiscoverTargetDisksAsync(
            MigrationProfile profile,
            int sourceDiskNumber,
            char systemDriveLetter,
            CancellationToken cancellationToken)
        {
            long targetBytes = smallerTarget ? 128L * GiB : 500L * GiB;
            IReadOnlyList<MigrationTargetDisk> disks =
            [
                new MigrationTargetDisk(1, $"Stage11 Target ({targetBus})", targetBus, "SSD", targetBytes, 512, "GPT", 0, [])
            ];
            return Task.FromResult(disks);
        }

        public Task<IReadOnlyList<LocalScratchVolume>> GetLocalScratchVolumesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalScratchVolume>>([new LocalScratchVolume('D', 200L * GiB, "NTFS")]);

        public (char BootLetter, char OsLetter) AllocateFreeDriveLetters() => ('Y', 'Z');

        public Task<VssSnapshotHandle> CreateVssSnapshotAsync(char systemDriveLetter, Action<string> log, CancellationToken cancellationToken)
        {
            VssCreated = true;
            log("VSS created");
            return Task.FromResult(new VssSnapshotHandle("{STAGE11-SHADOW}", @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy99"));
        }

        public Task DeleteVssSnapshotAsync(string shadowId, Action<string> log)
        {
            log($"VSS deleted: {shadowId}");
            return Task.CompletedTask;
        }

        public Task<PartitionedTargetInfo> PartitionAndPrepareTargetAsync(
            MigrationPlanRequest request,
            SourceOsInfo source,
            MigrationTargetDisk target,
            CloneMode resolvedMode,
            char bootLetter,
            char osLetter,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            log($"Partitioned {request.PartitionStyle} for {resolvedMode}");
            return Task.FromResult(new PartitionedTargetInfo(
                TargetDiskNumber: target.DiskNumber,
                BootPartitionNumber: 1,
                OsPartitionNumber: request.PartitionStyle == PartitionStyleChoice.GptUefi ? 3 : 2,
                BootDriveLetter: bootLetter,
                OsDriveLetter: osLetter,
                TargetVolumeGuidPath: @"\\?\Volume{00000000-0000-0000-0000-000000000011}",
                OsPartitionGuid: Guid.NewGuid(),
                MbrDiskSignature: 0x11223344u,
                OsPartitionOffset: 1024L * 1024L * 256L));
        }

        public Task ExecuteBlockCloneAsync(
            SourceOsInfo source,
            PartitionedTargetInfo targetInfo,
            VssSnapshotHandle snapshot,
            Action<string> log,
            IProgress<MigrationProgress>? progress,
            CancellationToken cancellationToken)
        {
            BlockCloneCalled = true;
            if (CancelTokenDuringClone is not null)
            {
                CancelTokenDuringClone.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            progress?.Report(new MigrationProgress(MigrationPhase.CloningData, 55, source.UsedBytes / 2, source.UsedBytes, 850.0, TimeSpan.FromSeconds(45), "BLOCK 50%"));
            return Task.CompletedTask;
        }

        public Task<PartitionedTargetInfo> MountCheckAndExtendBlockVolumeAsync(
            MigrationProfile profile,
            PartitionedTargetInfo targetInfo,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            BlockExtendCalled = true;
            return Task.FromResult(targetInfo);
        }

        public Task ExecuteDismCaptureAndApplyAsync(
            MigrationProfile profile,
            SourceOsInfo source,
            PartitionedTargetInfo targetInfo,
            VssSnapshotHandle snapshot,
            char scratchDriveLetter,
            MigrationCleanupState cleanupState,
            Action<string> log,
            IProgress<MigrationProgress>? progress,
            CancellationToken cancellationToken)
        {
            DismCalled = true;
            progress?.Report(new MigrationProgress(MigrationPhase.CloningData, 60, source.UsedBytes / 2, source.UsedBytes, 420.0, TimeSpan.FromSeconds(90), "DISM 60%"));
            return Task.CompletedTask;
        }

        public Task FixRedirectedUserFoldersAsync(
            MigrationPlanRequest request,
            SourceOsInfo source,
            char osLetter,
            MigrationCleanupState cleanupState,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            RedirectedFoldersFixed = true;
            return Task.CompletedTask;
        }

        public Task UpdateOfflineSystemRegistryAsync(
            MigrationPlanRequest request,
            SourceOsInfo source,
            PartitionedTargetInfo targetInfo,
            MigrationCleanupState cleanupState,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            OfflineRegistryUpdated = true;
            return Task.CompletedTask;
        }

        public Task InstallBootloaderAsync(
            MigrationPlanRequest request,
            SourceOsInfo source,
            PartitionedTargetInfo targetInfo,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            if (ThrowOnBootloader)
            {
                throw new InvalidOperationException("Simulated bcdboot failure for Stage 11 fault injection.");
            }

            BootloaderInstalled = true;
            return Task.CompletedTask;
        }

        public Task CleanupResourcesAsync(MigrationCleanupState state, Action<string> log)
        {
            CleanupCalled = true;
            return Task.CompletedTask;
        }
    }
}
