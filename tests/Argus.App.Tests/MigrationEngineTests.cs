using System.Text;
using Argus.App.ViewModels;
using Argus.Core.Contracts.Migration;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Argus.Core.Journal;
using Argus.Migration.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class MigrationEngineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusMigrationTests_{Guid.NewGuid():N}");

    public MigrationEngineTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void NtfsVssCloner_BuildBlockMap_ForcesEndpointsBridgesSingleGapsAndCaps16MiB()
    {
        // 40 MiB partition with 4 KiB clusters -> 40 blocks of 1 MiB (256 clusters/block = 32 bitmap bytes/block)
        const long blockSize = 1024L * 1024L;
        const long partitionSize = 40 * blockSize;
        const long bytesPerCluster = 4096;
        const long totalClusters = partitionSize / bytesPerCluster;
        byte[] bitmap = new byte[totalClusters / 8];

        // Mark block 15 and block 17 active (block 16 is a 1 MiB gap -> must be bridged!)
        bitmap[15 * 32] = 0x01;
        bitmap[17 * 32] = 0x80;

        var (chunks, totalBytes) = NtfsVssCloner.BuildBlockMap(bitmap, totalClusters, partitionSize, bytesPerCluster);

        // Expect: [0..8 MiB] (8 MiB), [15..18 MiB] (3 MiB bridged), [32..40 MiB] (8 MiB) => 19 MiB total
        Assert.Equal(3, chunks.Count);
        Assert.Equal(new VolumeBlockChunk(0, 8 * (int)blockSize), chunks[0]);
        Assert.Equal(new VolumeBlockChunk(15 * blockSize, 3 * (int)blockSize), chunks[1]);
        Assert.Equal(new VolumeBlockChunk(32 * blockSize, 8 * (int)blockSize), chunks[2]);
        Assert.Equal(19 * blockSize, totalBytes);
        Assert.All(chunks, static c => Assert.True(c.Length <= 16 * 1024 * 1024));
    }

    [Fact]
    public void NtfsVssCloner_PatchNtfsVbrSerial_ValidatesSignatureAndIncrementsByte4B()
    {
        byte[] vbr = new byte[512];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(vbr, 3);
        BitConverter.GetBytes(200_000L).CopyTo(vbr, 0x28);
        vbr[0x4B] = 0xFE;

        var (oldByte, newByte, backupOffset) = NtfsVssCloner.PatchNtfsVbrSerial(
            vbr,
            partitionSize: 200_001L * 512L,
            sectorSize: 512);

        Assert.Equal((byte)0xFE, oldByte);
        Assert.Equal((byte)0xFF, newByte);
        Assert.Equal((byte)0xFF, vbr[0x4B]);
        Assert.Equal(200_000L * 512L, backupOffset);

        byte[] badVbr = new byte[512];
        Assert.Throws<InvalidOperationException>(() => NtfsVssCloner.PatchNtfsVbrSerial(badVbr, 1024 * 1024, 512));
    }

    [Fact]
    public void MigrationPlanEvaluator_ResolvesBlockVsDismEnforcesBusAndBuildsMountedDevices()
    {
        var source = CreateSourceOs(partitionGiB: 240, usedGiB: 90, bitLocker: "FullyDecrypted");
        var largeInternal = new MigrationTargetDisk(1, "NVMe 1TB", "NVMe", "SSD", 1000L * GiB, 512, "GPT", 0, []);
        var smallUsb = new MigrationTargetDisk(3, "USB 128GB", "USB", "SSD", 128L * GiB, 512, "GPT", 2L * GiB, ['F']);
        var scratchVolumes = new[] { new LocalScratchVolume('D', 200L * GiB, "NTFS") };

        // 1. Large internal SSD -> Auto resolves to BLOCK
        var internalOk = MigrationPlanEvaluator.Evaluate(
            source,
            largeInternal,
            scratchVolumes,
            new MigrationPlanRequest(MigrationProfile.InternalMotherboardDisk, 1, PartitionStyleChoice.GptUefi),
            isAdministrator: true);

        Assert.True(internalOk.CanProceed);
        Assert.Equal(CloneMode.BlockVssBitmap, internalOk.ResolvedCloneMode);
        Assert.Null(internalOk.ScratchDriveLetter);

        // 2. Smaller USB SSD -> Auto resolves to DISM shrink-to-fit with D: scratch
        var usbDism = MigrationPlanEvaluator.Evaluate(
            source,
            smallUsb,
            scratchVolumes,
            new MigrationPlanRequest(MigrationProfile.ExternalUsbDrive, 3, PartitionStyleChoice.GptUefi),
            isAdministrator: true);

        Assert.True(usbDism.CanProceed);
        Assert.Equal(CloneMode.DismShrinkToFit, usbDism.ResolvedCloneMode);
        Assert.Equal('D', usbDism.ScratchDriveLetter);
        Assert.Contains(usbDism.Warnings, static w => w.Contains("2,0 ГіБ", StringComparison.Ordinal) || w.Contains("2.0 ГіБ", StringComparison.Ordinal));

        // 3. Wrong bus for profile (USB disk in Internal profile) -> blocked
        var wrongBus = MigrationPlanEvaluator.Evaluate(
            source,
            smallUsb,
            scratchVolumes,
            new MigrationPlanRequest(MigrationProfile.InternalMotherboardDisk, 3, PartitionStyleChoice.GptUefi),
            isAdministrator: true);
        Assert.False(wrongBus.CanProceed);

        // 4. Source disk itself -> blocked
        var selfDisk = new MigrationTargetDisk(source.SourceDiskNumber, "System", "SATA", "SSD", 500L * GiB, 512, "GPT", 90L * GiB, ['C']);
        var selfBlocked = MigrationPlanEvaluator.Evaluate(
            source,
            selfDisk,
            scratchVolumes,
            new MigrationPlanRequest(MigrationProfile.InternalMotherboardDisk, source.SourceDiskNumber, PartitionStyleChoice.GptUefi),
            isAdministrator: true);
        Assert.False(selfBlocked.CanProceed);

        // 5. MountedDevices binary encoding (GPT 24B DMIO:ID: + GUID; MBR 12B Sig + Offset)
        Guid partGuid = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
        byte[] gptBytes = MigrationPlanEvaluator.BuildMountedDevicesBinary(PartitionStyleChoice.GptUefi, partGuid, 0, 0);
        Assert.Equal(24, gptBytes.Length);
        Assert.Equal("DMIO:ID:", Encoding.ASCII.GetString(gptBytes, 0, 8));

        byte[] mbrBytes = MigrationPlanEvaluator.BuildMountedDevicesBinary(PartitionStyleChoice.MbrLegacy, null, 0xAABBCCDD, 104_857_600);
        Assert.Equal(12, mbrBytes.Length);
        Assert.Equal(0xAABBCCDDu, BitConverter.ToUInt32(mbrBytes, 0));
        Assert.Equal(104_857_600L, BitConverter.ToInt64(mbrBytes, 4));
    }

    [Fact]
    public async Task WindowsMigrationEngine_BlocksNonAdminAndInvalidConfirmationSafely()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(bus, NullLogger<FileBackedOperationJournal>.Instance, Path.Combine(_tempDir, "j1.jsonl"));
        var host = new FakeMigrationHost(CreateSourceOs(240, 80), [new(1, "NVMe Target", "NVMe", "SSD", 500L * GiB, 512, "GPT", 0, [])]);

        // Non-admin blocked
        var nonAdminEngine = new WindowsMigrationEngine(host, new StubPrivilege(false), journal, NullLogger<WindowsMigrationEngine>.Instance, _tempDir);
        var req = new MigrationPlanRequest(MigrationProfile.InternalMotherboardDisk, 1, PartitionStyleChoice.GptUefi);
        var resNoAdmin = await nonAdminEngine.ExecuteAsync(req, "WIPE-DISK-1", cancellationToken: ct);
        Assert.False(resNoAdmin.Succeeded);
        Assert.Equal(0, host.PartitionCalls);
        Assert.Contains(journal.GetRecent(10), static e => e.Action == "MigrationBlockedNotAdmin");

        // Wrong confirmation token blocked
        var adminEngine = new WindowsMigrationEngine(host, new StubPrivilege(true), journal, NullLogger<WindowsMigrationEngine>.Instance, _tempDir);
        var resBadToken = await adminEngine.ExecuteAsync(req, "YES", cancellationToken: ct);
        Assert.False(resBadToken.Succeeded);
        Assert.Equal(0, host.PartitionCalls);
        Assert.Contains(journal.GetRecent(10), static e => e.Action == "MigrationBlockedConfirmation");
    }

    [Fact]
    public async Task WindowsMigrationEngine_RunsFullBlockPipelineAndCleansUpOnCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(bus, NullLogger<FileBackedOperationJournal>.Instance, Path.Combine(_tempDir, "j2.jsonl"));

        var host = new FakeMigrationHost(
            CreateSourceOs(240, 80),
            [new(1, "NVMe 1TB", "NVMe", "SSD", 1000L * GiB, 512, "GPT", 0, [])]);
        var engine = new WindowsMigrationEngine(host, new StubPrivilege(true), journal, NullLogger<WindowsMigrationEngine>.Instance, _tempDir);

        var phases = new List<MigrationPhase>();
        var progress = new SyncProgress<MigrationProgress>(p => phases.Add(p.Phase));
        var req = new MigrationPlanRequest(MigrationProfile.InternalMotherboardDisk, 1, PartitionStyleChoice.GptUefi);

        var result = await engine.ExecuteAsync(req, "WIPE-DISK-1", progress, ct);

        Assert.True(result.Succeeded);
        Assert.False(result.WasCancelled);
        Assert.Equal(CloneMode.BlockVssBitmap, result.UsedCloneMode);
        Assert.NotNull(result.LogFilePath);
        Assert.True(File.Exists(result.LogFilePath));
        Assert.Equal(1, host.BlockCloneCalls);
        Assert.Equal(1, host.MountExtendCalls);
        Assert.Equal(1, host.RegistryCalls);
        Assert.Equal(1, host.BootloaderCalls);
        Assert.True(host.CleanupCalls >= 1);
        Assert.Contains(MigrationPhase.Completed, phases);
        Assert.Contains(journal.GetRecent(10), static e => e.Action == "MigrationCompleted");

        // Cancellation mid-clone must trigger guaranteed cleanup
        using var cancelCts = new CancellationTokenSource();
        host.CancelDuringClone = cancelCts;
        var cancelled = await engine.ExecuteAsync(req, "WIPE-DISK-1", progress, cancelCts.Token);

        Assert.False(cancelled.Succeeded);
        Assert.True(cancelled.WasCancelled);
        Assert.True(host.CleanupCalls >= 2);
        Assert.Contains(journal.GetRecent(10), static e => e.Action == "MigrationCancelled");
    }

    [Fact]
    public async Task MigrationViewModel_RefreshesTargetsValidatesTokenAndTracksLiveProgress()
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(bus, NullLogger<FileBackedOperationJournal>.Instance, Path.Combine(_tempDir, "j3.jsonl"));
        var host = new FakeMigrationHost(
            CreateSourceOs(240, 80),
            [new(1, "Samsung 990 PRO", "NVMe", "SSD", 1000L * GiB, 512, "GPT", 0, [])]);
        var engine = new WindowsMigrationEngine(host, new StubPrivilege(true), journal, NullLogger<WindowsMigrationEngine>.Instance, _tempDir);

        var vm = new MigrationViewModel(engine, new StubPrivilege(true), autoLoadOnInit: false);
        await vm.RefreshEnvironmentAsync();

        Assert.Single(vm.TargetDisks);
        Assert.Equal("WIPE-DISK-1", vm.ExpectedConfirmationToken);
        Assert.True(vm.PreflightCanProceed);
        Assert.True(vm.CanStartMigration);
        Assert.False(vm.IsConfirmationDialogOpen);

        vm.RequestStartMigrationCommand.Execute(null);
        Assert.True(vm.IsConfirmationDialogOpen);

        await vm.ConfirmAndStartMigrationAsync();

        Assert.False(vm.IsConfirmationDialogOpen);
        Assert.Equal(100, vm.ProgressPercent);
        Assert.True(vm.HasLastLogPath);
        Assert.True(vm.PhaseSteps.All(static s => s.IsDone));
    }

    [Fact]
    public void WindowsMigrationHostOperations_ResolveTargetPartitions_HandlesShiftedAutoMsrGptAndCanonicalLayouts()
    {
        const string msrType = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
        const string basicType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
        var bootGuid = Guid.Parse("45b47093-15ff-4d5d-94ed-b33dda6bbb66");
        var osGuid = Guid.Parse("3fa973c3-2fcb-4e36-92a5-2e4d3e8f9fe5");

        // Exact shifted layout produced when `diskpart convert gpt` auto-creates an initial 16 MB MSR as Partition 1
        var shiftedGpt = new[]
        {
            new WindowsMigrationHostOperations.DiskPartitionSnapshot(1, null, msrType, null, 16_759_808, 17_408, Guid.NewGuid(), null),
            new WindowsMigrationHostOperations.DiskPartitionSnapshot(2, 'Y', basicType, null, 104_857_600, 16_777_216, bootGuid, @"\\?\Volume{45b47093-15ff-4d5d-94ed-b33dda6bbb66}"),
            new WindowsMigrationHostOperations.DiskPartitionSnapshot(3, null, msrType, null, 16_777_216, 121_634_816, Guid.NewGuid(), null),
            new WindowsMigrationHostOperations.DiskPartitionSnapshot(4, null, basicType, null, 254_937_137_152, 138_412_032, osGuid, @"\\?\Volume{3fa973c3-2fcb-4e36-92a5-2e4d3e8f9fe5}")
        };

        var resolvedShifted = WindowsMigrationHostOperations.ResolveTargetPartitions(
            shiftedGpt,
            PartitionStyleChoice.GptUefi,
            CloneMode.BlockVssBitmap,
            bootLetter: 'Y',
            osLetter: 'X',
            minimumOsSizeBytes: 254_932_936_192);

        Assert.NotNull(resolvedShifted);
        Assert.Equal(2, resolvedShifted.Value.BootPartition.PartitionNumber);
        Assert.Equal(4, resolvedShifted.Value.OsPartition.PartitionNumber);
        Assert.Equal(@"\\?\Volume{3fa973c3-2fcb-4e36-92a5-2e4d3e8f9fe5}", resolvedShifted.Value.OsPartition.VolumeGuidPath);
        Assert.Equal(osGuid, resolvedShifted.Value.OsPartition.PartitionGuid);

        // Canonical clean 3-partition GPT layout (after auto-MSR removal)
        var canonicalGpt = new[]
        {
            new WindowsMigrationHostOperations.DiskPartitionSnapshot(1, 'Y', basicType, null, 104_857_600, 1_048_576, bootGuid, @"\\?\Volume{45b47093-15ff-4d5d-94ed-b33dda6bbb66}"),
            new WindowsMigrationHostOperations.DiskPartitionSnapshot(2, null, msrType, null, 16_777_216, 105_906_176, Guid.NewGuid(), null),
            new WindowsMigrationHostOperations.DiskPartitionSnapshot(3, 'X', basicType, null, 499_000_000_000, 122_683_392, osGuid, @"\\?\Volume{3fa973c3-2fcb-4e36-92a5-2e4d3e8f9fe5}")
        };

        var resolvedDism = WindowsMigrationHostOperations.ResolveTargetPartitions(
            canonicalGpt,
            PartitionStyleChoice.GptUefi,
            CloneMode.DismShrinkToFit,
            bootLetter: 'Y',
            osLetter: 'X',
            minimumOsSizeBytes: 254_932_936_192);

        Assert.NotNull(resolvedDism);
        Assert.Equal(1, resolvedDism.Value.BootPartition.PartitionNumber);
        Assert.Equal(3, resolvedDism.Value.OsPartition.PartitionNumber);

        // MBR Legacy layout (no GPT partition GUIDs)
        var mbrLayout = new[]
        {
            new WindowsMigrationHostOperations.DiskPartitionSnapshot(1, 'Y', null, 7, 104_857_600, 1_048_576, null, @"\\?\Volume{11111111-1111-1111-1111-111111111111}"),
            new WindowsMigrationHostOperations.DiskPartitionSnapshot(2, null, null, 7, 254_937_137_152, 105_906_176, null, @"\\?\Volume{22222222-2222-2222-2222-222222222222}")
        };

        var resolvedMbr = WindowsMigrationHostOperations.ResolveTargetPartitions(
            mbrLayout,
            PartitionStyleChoice.MbrLegacy,
            CloneMode.BlockVssBitmap,
            bootLetter: 'Y',
            osLetter: 'X',
            minimumOsSizeBytes: 254_932_936_192);

        Assert.NotNull(resolvedMbr);
        Assert.Equal(1, resolvedMbr.Value.BootPartition.PartitionNumber);
        Assert.Equal(2, resolvedMbr.Value.OsPartition.PartitionNumber);
    }

    [Fact]
    public void WindowsMigrationHostOperations_BuildDiskpartScripts_DeletesAutoCreatedPartitionsInDescendingOrder()
    {
        string init = WindowsMigrationHostOperations.BuildDiskpartInitScript(diskNum: 3, isGpt: true);
        Assert.Contains("select disk 3", init, StringComparison.Ordinal);
        Assert.Contains("clean", init, StringComparison.Ordinal);
        Assert.Contains("convert gpt", init, StringComparison.Ordinal);

        string layout = WindowsMigrationHostOperations.BuildDiskpartLayoutScript(
            diskNum: 3,
            isGpt: true,
            resolvedMode: CloneMode.BlockVssBitmap,
            bootLabel: "ESP_USB",
            mbrBootLabel: "BOOT_USB",
            osLabel: "WinUSB",
            bootLetter: 'Y',
            osLetter: 'X',
            osSizeMiB: 243_127,
            preExistingPartitionNumbers: [1, 2]);

        int del2 = layout.IndexOf("select partition 2", StringComparison.Ordinal);
        int del1 = layout.IndexOf("select partition 1", StringComparison.Ordinal);
        int createBoot = layout.IndexOf("create partition primary size=100", StringComparison.Ordinal);
        Assert.True(del2 >= 0 && del1 > del2 && createBoot > del1);
        Assert.Contains("delete partition override", layout, StringComparison.Ordinal);
        Assert.Contains("create partition primary size=243127", layout, StringComparison.Ordinal);
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
            // best effort
        }
    }

    private const long GiB = 1024L * 1024L * 1024L;

    private static SourceOsInfo CreateSourceOs(long partitionGiB, long usedGiB, string bitLocker = "FullyDecrypted") =>
        new(
            OsCaption: "Microsoft Windows 11 Pro",
            SystemDriveLetter: 'C',
            SourceDiskNumber: 2,
            SourceDiskModel: "SPCC Solid State Disk",
            SourceBusType: "SATA",
            PartitionSizeBytes: partitionGiB * GiB,
            UsedBytes: usedGiB * GiB,
            LogicalSectorSize: 512,
            Is64Bit: true,
            IsUefiHost: true,
            BitLockerStatus: bitLocker);

    private sealed class StubPrivilege(bool isAdmin) : IPrivilegeService
    {
        public bool IsAdministrator { get; } = isAdmin;
        public bool TryRelaunchElevated(string[]? arguments = null) => false;
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class FakeMigrationHost(SourceOsInfo source, IReadOnlyList<MigrationTargetDisk> targets)
        : IMigrationHostOperations
    {
        public int PartitionCalls { get; private set; }
        public int BlockCloneCalls { get; private set; }
        public int MountExtendCalls { get; private set; }
        public int RegistryCalls { get; private set; }
        public int BootloaderCalls { get; private set; }
        public int CleanupCalls { get; private set; }
        public CancellationTokenSource? CancelDuringClone { get; set; }

        public Task<SourceOsInfo> InspectLiveOsAsync(CancellationToken cancellationToken) => Task.FromResult(source);

        public Task<IReadOnlyList<MigrationTargetDisk>> DiscoverTargetDisksAsync(
            MigrationProfile profile,
            int sourceDiskNumber,
            char systemDriveLetter,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<MigrationTargetDisk> filtered = targets
                .Where(d => MigrationPlanEvaluator.IsBusEligible(profile, d.BusType))
                .ToArray();
            return Task.FromResult(filtered);
        }

        public Task<IReadOnlyList<LocalScratchVolume>> GetLocalScratchVolumesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LocalScratchVolume>>([new('D', 200L * GiB, "NTFS")]);

        public (char BootLetter, char OsLetter) AllocateFreeDriveLetters() => ('Z', 'Y');

        public Task<VssSnapshotHandle> CreateVssSnapshotAsync(char systemDriveLetter, Action<string> log, CancellationToken cancellationToken)
            => Task.FromResult(new VssSnapshotHandle("{shadow-1}", @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1"));

        public Task DeleteVssSnapshotAsync(string shadowId, Action<string> log) => Task.CompletedTask;

        public Task<PartitionedTargetInfo> PartitionAndPrepareTargetAsync(
            MigrationPlanRequest request,
            SourceOsInfo src,
            MigrationTargetDisk target,
            CloneMode resolvedMode,
            char bootLetter,
            char osLetter,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            PartitionCalls++;
            return Task.FromResult(new PartitionedTargetInfo(
                target.DiskNumber, 1, 3, bootLetter, osLetter,
                @"\\?\Volume{00000000-0000-0000-0000-000000000001}",
                Guid.NewGuid(), 0x12345678, 104_857_600));
        }

        public Task ExecuteBlockCloneAsync(
            SourceOsInfo src,
            PartitionedTargetInfo targetInfo,
            VssSnapshotHandle snapshot,
            Action<string> log,
            IProgress<MigrationProgress>? progress,
            CancellationToken cancellationToken)
        {
            BlockCloneCalls++;
            progress?.Report(new MigrationProgress(
                MigrationPhase.CloningData, 50, src.UsedBytes / 2, src.UsedBytes, 420.5, TimeSpan.FromMinutes(2), "50%"));

            if (CancelDuringClone is not null)
            {
                CancelDuringClone.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        }

        public Task<PartitionedTargetInfo> MountCheckAndExtendBlockVolumeAsync(
            MigrationProfile profile,
            PartitionedTargetInfo targetInfo,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            MountExtendCalls++;
            return Task.FromResult(targetInfo);
        }

        public Task ExecuteDismCaptureAndApplyAsync(
            MigrationProfile profile,
            SourceOsInfo src,
            PartitionedTargetInfo targetInfo,
            VssSnapshotHandle snapshot,
            char scratchDriveLetter,
            MigrationCleanupState cleanupState,
            Action<string> log,
            IProgress<MigrationProgress>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FixRedirectedUserFoldersAsync(
            MigrationPlanRequest request,
            SourceOsInfo src,
            char osLetter,
            MigrationCleanupState cleanupState,
            Action<string> log,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateOfflineSystemRegistryAsync(
            MigrationPlanRequest request,
            SourceOsInfo src,
            PartitionedTargetInfo targetInfo,
            MigrationCleanupState cleanupState,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            RegistryCalls++;
            return Task.CompletedTask;
        }

        public Task InstallBootloaderAsync(
            MigrationPlanRequest request,
            SourceOsInfo src,
            PartitionedTargetInfo targetInfo,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            BootloaderCalls++;
            return Task.CompletedTask;
        }

        public Task CleanupResourcesAsync(MigrationCleanupState state, Action<string> log)
        {
            CleanupCalls++;
            state.ShadowId = null;
            state.SystemHiveLoaded = false;
            state.UserHiveLoaded = false;
            return Task.CompletedTask;
        }
    }
}
