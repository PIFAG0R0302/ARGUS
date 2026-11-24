using Argus.App.ViewModels;
using Argus.Core.Contracts.Maintenance;
using Argus.Maintenance.Disks;
using Xunit;

namespace Argus.App.Tests;

public sealed class UxDisksDrilldownTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusUxDisksTests_{Guid.NewGuid():N}");

    public UxDisksDrilldownTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\Common Files")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\Default")]
    [InlineData(@"C:\Users\Public")]
    [InlineData(@"C:\Users\Public\Documents")]
    [InlineData(@"D:\System Volume Information")]
    [InlineData(@"D:\$Recycle.Bin")]
    [InlineData(@"C:\Boot")]
    [InlineData(@"C:\Recovery")]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"D:\swapfile.sys")]
    [InlineData(@"\\server\share\file.bin")]
    public void DiskDeleteSafetyPolicy_HardBlocksProtectedSystemPaths(string path)
    {
        var decision = DiskDeleteSafetyPolicy.EvaluatePath(
            path,
            isReparsePoint: false,
            systemDriveRoot: @"C:\",
            appBaseDirectory: @"E:\Apps\ARGUS",
            argusLocalAppData: @"C:\Users\Alice\AppData\Local\Argus");

        Assert.False(decision.IsAllowed);
        Assert.True(decision.IsProtectedPath);
        Assert.False(string.IsNullOrWhiteSpace(decision.BlockReason));
    }

    [Fact]
    public void DiskDeleteSafetyPolicy_BlocksAppDirectoriesAndReparsePoints_AllowsUserPaths()
    {
        const string appBase = @"E:\Apps\ARGUS";
        const string localArgus = @"C:\Users\Alice\AppData\Local\Argus";

        var appBaseDecision = DiskDeleteSafetyPolicy.EvaluatePath(
            appBase,
            systemDriveRoot: @"C:\",
            appBaseDirectory: appBase,
            argusLocalAppData: localArgus);
        Assert.False(appBaseDecision.IsAllowed);
        Assert.True(appBaseDecision.IsProtectedPath);

        var appSubfileDecision = DiskDeleteSafetyPolicy.EvaluatePath(
            Path.Combine(appBase, "Argus.App.dll"),
            systemDriveRoot: @"C:\",
            appBaseDirectory: appBase,
            argusLocalAppData: localArgus);
        Assert.False(appSubfileDecision.IsAllowed);

        var localArgusDecision = DiskDeleteSafetyPolicy.EvaluatePath(
            Path.Combine(localArgus, "settings.json"),
            systemDriveRoot: @"C:\",
            appBaseDirectory: appBase,
            argusLocalAppData: localArgus);
        Assert.False(localArgusDecision.IsAllowed);

        var reparseDecision = DiskDeleteSafetyPolicy.EvaluatePath(
            @"D:\Games\LinkedMods",
            isReparsePoint: true,
            systemDriveRoot: @"C:\",
            appBaseDirectory: appBase,
            argusLocalAppData: localArgus);
        Assert.False(reparseDecision.IsAllowed);
        Assert.True(reparseDecision.IsReparsePoint);

        var allowedGameDir = DiskDeleteSafetyPolicy.EvaluatePath(
            @"D:\Games\OldGame",
            systemDriveRoot: @"C:\",
            appBaseDirectory: appBase,
            argusLocalAppData: localArgus);
        Assert.True(allowedGameDir.IsAllowed);
        Assert.False(allowedGameDir.IsProtectedPath);
        Assert.False(allowedGameDir.IsReparsePoint);

        var allowedUserFile = DiskDeleteSafetyPolicy.EvaluatePath(
            @"C:\Users\Alice\Downloads\archive.zip",
            systemDriveRoot: @"C:\",
            appBaseDirectory: appBase,
            argusLocalAppData: localArgus);
        Assert.True(allowedUserFile.IsAllowed);
    }

    [Fact]
    public async Task WindowsDiskSpaceAnalyzer_AnalyzeFolderContents_ReturnsSubfoldersAndFilesSortedBySize()
    {
        var ct = TestContext.Current.CancellationToken;
        string gamesDir = Path.Combine(_tempDir, "Games");
        string subBig = Path.Combine(gamesDir, "CyberQuest");
        string subSmall = Path.Combine(gamesDir, "IndieMini");
        Directory.CreateDirectory(subBig);
        Directory.CreateDirectory(subSmall);

        await File.WriteAllBytesAsync(Path.Combine(subBig, "assets.pak"), new byte[20_000], ct);
        await File.WriteAllBytesAsync(Path.Combine(gamesDir, "installer.iso"), new byte[12_000], ct);
        await File.WriteAllBytesAsync(Path.Combine(subSmall, "data.bin"), new byte[4_000], ct);
        await File.WriteAllBytesAsync(Path.Combine(gamesDir, "readme.txt"), new byte[500], ct);

        var analyzer = new WindowsDiskSpaceAnalyzer(new FakeRecycleBinDeleter());
        var report = await analyzer.AnalyzeFolderContentsAsync(gamesDir, maxEntries: 20, cancellationToken: ct);

        Assert.False(report.IsVolumeRoot);
        Assert.Equal(Path.GetFullPath(gamesDir), report.CurrentPath);
        Assert.Equal(Path.GetFullPath(_tempDir), report.ParentPath);
        Assert.Equal(4, report.Entries.Count);

        Assert.Equal("CyberQuest", report.Entries[0].Name);
        Assert.True(report.Entries[0].IsDirectory);
        Assert.Equal(20_000, report.Entries[0].SizeBytes);

        Assert.Equal("installer.iso", report.Entries[1].Name);
        Assert.False(report.Entries[1].IsDirectory);
        Assert.Equal(12_000, report.Entries[1].SizeBytes);

        Assert.Equal("IndieMini", report.Entries[2].Name);
        Assert.True(report.Entries[2].IsDirectory);
        Assert.Equal(4_000, report.Entries[2].SizeBytes);

        Assert.Equal("readme.txt", report.Entries[3].Name);
        Assert.False(report.Entries[3].IsDirectory);
        Assert.Equal(500, report.Entries[3].SizeBytes);
    }

    [Fact]
    public async Task WindowsDiskSpaceAnalyzer_DeleteToRecycleBin_InvokesDeleterForSafeItemsAndBlocksProtected()
    {
        var ct = TestContext.Current.CancellationToken;
        string safeFile = Path.Combine(_tempDir, "old_dump.bin");
        await File.WriteAllBytesAsync(safeFile, new byte[8_192], ct);

        var fakeDeleter = new FakeRecycleBinDeleter();
        var analyzer = new WindowsDiskSpaceAnalyzer(fakeDeleter);

        var result = await analyzer.DeleteToRecycleBinAsync(
            [safeFile, @"C:\Windows\System32", @"C:\"],
            cancellationToken: ct);

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(8_192, result.FreedBytes);
        Assert.Equal(2, result.BlockedCount);
        Assert.Single(fakeDeleter.DeletedPaths);
        Assert.Equal(Path.GetFullPath(safeFile), fakeDeleter.DeletedPaths[0]);
        Assert.Equal(2, result.Failures.Count);
    }

    [Fact]
    public async Task DisksViewModel_DrillDownNavigateUpAndRoot_WorksCorrectly()
    {
        var analyzer = new StubDrilldownSpaceAnalyzer();
        var vm = new DisksViewModel(diskOptimizer: null, spaceAnalyzer: analyzer, autoLoadOnInit: false);

        await vm.SelectVolumeForSpaceMapAsync('D');
        Assert.Equal(@"D:\", vm.CurrentBrowsePath);
        Assert.False(vm.IsInsideSubfolder);
        Assert.Single(vm.LargestFolders);
        Assert.Equal("Games", vm.LargestFolders[0].Name);

        // Drill into D:\Games
        await vm.OpenFolderAsync(vm.LargestFolders[0]);
        Assert.Equal(@"D:\Games", vm.CurrentBrowsePath);
        Assert.True(vm.IsInsideSubfolder);
        Assert.Equal(3, vm.LargestFolders.Count);
        Assert.Equal("Тека", vm.LargestFolders[0].TypeBadge);
        Assert.Equal("Файл", vm.LargestFolders[1].TypeBadge);

        // Drill into D:\Games\OldRpg
        await vm.OpenFolderAsync(vm.LargestFolders[0]);
        Assert.Equal(@"D:\Games\OldRpg", vm.CurrentBrowsePath);
        Assert.True(vm.IsInsideSubfolder);
        Assert.Single(vm.LargestFolders);

        // Navigate up -> D:\Games
        await vm.NavigateUpAsync();
        Assert.Equal(@"D:\Games", vm.CurrentBrowsePath);
        Assert.True(vm.IsInsideSubfolder);

        // Navigate root -> D:\
        await vm.NavigateRootAsync();
        Assert.Equal(@"D:\", vm.CurrentBrowsePath);
        Assert.False(vm.IsInsideSubfolder);
    }

    [Fact]
    public async Task DisksViewModel_SelectionConfirmationAndDelete_EnforcesGuardrailsAndRefreshes()
    {
        var analyzer = new StubDrilldownSpaceAnalyzer();
        var vm = new DisksViewModel(diskOptimizer: null, spaceAnalyzer: analyzer, autoLoadOnInit: false);

        await vm.SelectVolumeForSpaceMapAsync('D');
        await vm.OpenFolderAsync(vm.LargestFolders[0]); // D:\Games

        // Entry 2 is a ReparsePoint junction — selecting it must be rejected
        var reparseItem = vm.LargestFolders[2];
        Assert.True(reparseItem.IsReparsePoint);
        Assert.False(reparseItem.CanSelect);
        reparseItem.IsSelected = true;
        Assert.False(reparseItem.IsSelected);
        Assert.False(vm.HasSelectedEntries);

        // Select the safe folder and safe file
        vm.LargestFolders[0].IsSelected = true;
        vm.LargestFolders[1].IsSelected = true;
        Assert.True(vm.HasSelectedEntries);
        Assert.Equal(2, vm.SelectedEntriesCount);

        // Open and cancel inline confirmation banner
        vm.RequestDeleteSelected();
        Assert.True(vm.IsDeleteConfirmationOpen);
        Assert.Contains("2 шт.", vm.DeleteConfirmationText, StringComparison.Ordinal);

        vm.CancelDeleteSelected();
        Assert.False(vm.IsDeleteConfirmationOpen);

        // Re-request and confirm deletion -> must invoke DeleteToRecycleBinAsync and refresh folder
        int scansBeforeDelete = analyzer.FolderScanCount;
        vm.RequestDeleteSelected();
        await vm.ConfirmDeleteSelectedAsync();

        Assert.False(vm.IsDeleteConfirmationOpen);
        Assert.True(vm.StatusIsSuccess);
        Assert.Equal(2, analyzer.LastDeletedPaths.Count);
        Assert.Contains(@"D:\Games\OldRpg", analyzer.LastDeletedPaths);
        Assert.Contains(@"D:\Games\patch.iso", analyzer.LastDeletedPaths);
        Assert.Equal(scansBeforeDelete + 1, analyzer.FolderScanCount);
        Assert.Equal(@"D:\Games", vm.CurrentBrowsePath);
        Assert.False(vm.HasSelectedEntries);
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

    private sealed class FakeRecycleBinDeleter : IRecycleBinDeleter
    {
        public List<string> DeletedPaths { get; } = [];

        public (bool Succeeded, string? ErrorMessage) MoveToRecycleBin(string fullPath)
        {
            DeletedPaths.Add(fullPath);
            return (true, null);
        }
    }

    private sealed class StubDrilldownSpaceAnalyzer : IDiskSpaceAnalyzer
    {
        private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);

        public int FolderScanCount { get; private set; }
        public List<string> LastDeletedPaths { get; private set; } = [];

        public Task<VolumeSpaceReport> AnalyzeVolumeSpaceAsync(
            char driveLetter,
            int topFolderCount = 8,
            CancellationToken cancellationToken = default,
            IProgress<(int Percent, string FolderName)>? progress = null)
        {
            var nodes = new[]
            {
                new FolderSpaceNode("Games", $@"{driveLetter}:\Games", 50_000_000_000, 1200, 60.0, 100.0)
            };

            return Task.FromResult(new VolumeSpaceReport(
                driveLetter,
                "Data",
                "NTFS",
                250_000_000_000,
                100_000_000_000,
                150_000_000_000,
                nodes));
        }

        public Task<FolderContentsReport> AnalyzeFolderContentsAsync(
            string folderPath,
            int maxEntries = 40,
            CancellationToken cancellationToken = default,
            IProgress<(int Percent, string EntryName)>? progress = null)
        {
            FolderScanCount++;

            if (string.Equals(folderPath, @"D:\Games\OldRpg", StringComparison.OrdinalIgnoreCase))
            {
                DiskSpaceEntry[] entries =
                [
                    new("world.pak", @"D:\Games\OldRpg\world.pak", false, 20_000_000_000, 1, 100.0, 100.0)
                ];

                return Task.FromResult(new FolderContentsReport(
                    'D',
                    @"D:\Games\OldRpg",
                    @"D:\Games",
                    false,
                    20_000_000_000,
                    entries.Where(e => !_deleted.Contains(e.FullPath)).ToArray()));
            }

            DiskSpaceEntry[] gamesEntries =
            [
                new("OldRpg", @"D:\Games\OldRpg", true, 30_000_000_000, 500, 60.0, 100.0),
                new("patch.iso", @"D:\Games\patch.iso", false, 15_000_000_000, 1, 30.0, 50.0),
                new("ModsLink", @"D:\Games\ModsLink", true, 0, 0, 0.0, 0.0, IsProtectedPath: false, IsReparsePoint: true, ProtectionReason: "ReparsePoint")
            ];

            var remaining = gamesEntries.Where(e => !_deleted.Contains(e.FullPath)).ToArray();

            return Task.FromResult(new FolderContentsReport(
                'D',
                @"D:\Games",
                @"D:\",
                false,
                remaining.Sum(static e => e.SizeBytes),
                remaining));
        }

        public Task<DiskRecycleDeleteResult> DeleteToRecycleBinAsync(
            IReadOnlyList<string> fullPaths,
            CancellationToken cancellationToken = default)
        {
            LastDeletedPaths = fullPaths.ToList();
            foreach (string path in fullPaths)
            {
                _deleted.Add(path);
            }

            return Task.FromResult(new DiskRecycleDeleteResult(
                DeletedCount: fullPaths.Count,
                FreedBytes: 45_000_000_000,
                BlockedCount: 0,
                DeletedPaths: fullPaths,
                Failures: []));
        }
    }
}
