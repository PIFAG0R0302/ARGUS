using Argus.App.ViewModels;
using Argus.Core.Contracts.Maintenance;
using Xunit;

namespace Argus.App.Tests;

public sealed class DisksUxTests
{
    [Fact]
    public void FolderSpaceRowItem_EnforcesOpenAndQuickDeleteCapabilitiesByEntryKindAndPolicy()
    {
        var safeDir = new FolderSpaceRowItem(
            new DiskSpaceEntry("OldRpg", @"D:\Games\OldRpg", true, 30_000_000_000, 500, 60.0, 100.0),
            onOpen: static _ => Task.CompletedTask,
            onQuickDelete: static _ => { });

        Assert.True(safeDir.CanOpen);
        Assert.True(safeDir.OpenCommand.CanExecute(null));
        Assert.True(safeDir.CanSelect);
        Assert.True(safeDir.QuickDeleteCommand.CanExecute(null));
        Assert.Contains("OldRpg", safeDir.QuickDeleteTooltip, StringComparison.Ordinal);

        var safeFile = new FolderSpaceRowItem(
            new DiskSpaceEntry("patch.iso", @"D:\Games\patch.iso", false, 15_000_000_000, 1, 30.0, 50.0),
            onOpen: static _ => Task.CompletedTask,
            onQuickDelete: static _ => { });

        Assert.True(safeFile.IsFile);
        Assert.False(safeFile.CanOpen);
        Assert.False(safeFile.OpenCommand.CanExecute(null));
        Assert.True(safeFile.CanSelect);
        Assert.True(safeFile.QuickDeleteCommand.CanExecute(null));

        var protectedDir = new FolderSpaceRowItem(
            new FolderSpaceNode("Windows", @"C:\Windows", 25_000_000_000, 90_000, 50.0, 100.0),
            onOpen: static _ => Task.CompletedTask,
            onQuickDelete: static _ => { });

        Assert.True(protectedDir.IsProtectedPath);
        Assert.True(protectedDir.CanOpen);
        Assert.False(protectedDir.CanSelect);
        Assert.False(protectedDir.QuickDeleteCommand.CanExecute(null));
        Assert.False(string.IsNullOrWhiteSpace(protectedDir.ProtectionReason));
        Assert.Equal(protectedDir.ProtectionReason, protectedDir.QuickDeleteTooltip);

        var reparseDir = new FolderSpaceRowItem(
            new DiskSpaceEntry(
                "ModsLink",
                @"D:\Games\ModsLink",
                true,
                0,
                0,
                0.0,
                0.0,
                IsProtectedPath: false,
                IsReparsePoint: true,
                ProtectionReason: "Символічне посилання заблоковано"),
            onOpen: static _ => Task.CompletedTask,
            onQuickDelete: static _ => { });

        Assert.False(reparseDir.CanOpen);
        Assert.False(reparseDir.OpenCommand.CanExecute(null));
        Assert.False(reparseDir.CanSelect);
        Assert.False(reparseDir.QuickDeleteCommand.CanExecute(null));
        Assert.Equal("Символічне посилання заблоковано", reparseDir.QuickDeleteTooltip);
    }

    [Fact]
    public async Task DisksViewModel_QuickDeleteSingleItem_ConfirmsDeletesOnlyThatItemAndRefreshes()
    {
        var analyzer = new StubDiskSpaceAnalyzer();
        var vm = new DisksViewModel(diskOptimizer: null, spaceAnalyzer: analyzer, autoLoadOnInit: false);

        await vm.SelectVolumeForSpaceMapAsync('D');
        await vm.OpenFolderAsync(vm.LargestFolders[0]); // D:\Games

        var folderItem = vm.LargestFolders[0]; // OldRpg
        var fileItem = vm.LargestFolders[1];   // patch.iso

        // Pre-select folderItem via checkbox to ensure single-item quick delete ignores bulk selection
        folderItem.IsSelected = true;
        Assert.Equal(1, vm.SelectedEntriesCount);

        // Trigger trash-can button on fileItem
        Assert.True(fileItem.QuickDeleteCommand.CanExecute(null));
        fileItem.QuickDeleteCommand.Execute(null);

        Assert.True(vm.IsDeleteConfirmationOpen);
        Assert.Same(fileItem, vm.PendingSingleDeleteItem);
        Assert.Contains(fileItem.Name, vm.DeleteConfirmationText, StringComparison.Ordinal);
        Assert.Contains(fileItem.SizeText, vm.DeleteConfirmationText, StringComparison.Ordinal);
        Assert.True(folderItem.IsSelected);

        int scansBeforeDelete = analyzer.FolderScanCount;
        await vm.ConfirmDeleteSelectedAsync();

        Assert.False(vm.IsDeleteConfirmationOpen);
        Assert.Null(vm.PendingSingleDeleteItem);
        Assert.True(vm.StatusIsSuccess);
        Assert.Single(analyzer.LastDeletedPaths);
        Assert.Equal(@"D:\Games\patch.iso", analyzer.LastDeletedPaths[0]);
        Assert.Equal(scansBeforeDelete + 1, analyzer.FolderScanCount);
        Assert.DoesNotContain(vm.LargestFolders, static i => i.Name == "patch.iso");
        Assert.Contains(vm.LargestFolders, static i => i.Name == "OldRpg");
    }

    [Fact]
    public async Task DisksViewModel_QuickDeleteCancel_ClosesBannerWithoutLosingCheckboxSelections()
    {
        var analyzer = new StubDiskSpaceAnalyzer();
        var vm = new DisksViewModel(diskOptimizer: null, spaceAnalyzer: analyzer, autoLoadOnInit: false);

        await vm.SelectVolumeForSpaceMapAsync('D');
        await vm.OpenFolderAsync(vm.LargestFolders[0]); // D:\Games

        var folderItem = vm.LargestFolders[0];
        var fileItem = vm.LargestFolders[1];
        folderItem.IsSelected = true;
        fileItem.IsSelected = true;

        Assert.Equal(2, vm.SelectedEntriesCount);
        Assert.True(vm.HasSelectedEntries);

        // Open quick-delete for single item, then cancel
        fileItem.QuickDeleteCommand.Execute(null);
        Assert.True(vm.IsDeleteConfirmationOpen);
        Assert.Same(fileItem, vm.PendingSingleDeleteItem);

        vm.CancelDeleteSelected();

        Assert.False(vm.IsDeleteConfirmationOpen);
        Assert.Null(vm.PendingSingleDeleteItem);
        Assert.Equal(string.Empty, vm.DeleteConfirmationText);
        Assert.True(folderItem.IsSelected);
        Assert.True(fileItem.IsSelected);
        Assert.Equal(2, vm.SelectedEntriesCount);
        Assert.True(vm.HasSelectedEntries);
    }

    [Fact]
    public async Task DisksViewModel_QuickDelete_RejectsProtectedAndReparseItems()
    {
        var analyzer = new StubDiskSpaceAnalyzer();
        var vm = new DisksViewModel(diskOptimizer: null, spaceAnalyzer: analyzer, autoLoadOnInit: false);

        await vm.SelectVolumeForSpaceMapAsync('D');
        await vm.OpenFolderAsync(vm.LargestFolders[0]); // D:\Games

        var reparseItem = vm.LargestFolders[2];
        var protectedItem = vm.LargestFolders[3];

        Assert.False(reparseItem.CanSelect);
        Assert.False(reparseItem.QuickDeleteCommand.CanExecute(null));
        vm.RequestQuickDelete(reparseItem);
        Assert.False(vm.IsDeleteConfirmationOpen);
        Assert.Null(vm.PendingSingleDeleteItem);

        Assert.False(protectedItem.CanSelect);
        Assert.False(protectedItem.QuickDeleteCommand.CanExecute(null));
        vm.RequestQuickDelete(protectedItem);
        Assert.False(vm.IsDeleteConfirmationOpen);
        Assert.Null(vm.PendingSingleDeleteItem);
    }

    [Fact]
    public async Task DisksViewModel_SwitchingFromQuickDeleteToBulkDelete_DeletesAllCheckedItems()
    {
        var analyzer = new StubDiskSpaceAnalyzer();
        var vm = new DisksViewModel(diskOptimizer: null, spaceAnalyzer: analyzer, autoLoadOnInit: false);

        await vm.SelectVolumeForSpaceMapAsync('D');
        await vm.OpenFolderAsync(vm.LargestFolders[0]); // D:\Games

        vm.LargestFolders[0].IsSelected = true;
        vm.LargestFolders[1].IsSelected = true;

        // First click single-item trash on item 1, then click bulk delete button
        vm.LargestFolders[1].QuickDeleteCommand.Execute(null);
        Assert.Same(vm.LargestFolders[1], vm.PendingSingleDeleteItem);

        vm.RequestDeleteSelected();
        Assert.Null(vm.PendingSingleDeleteItem);
        Assert.True(vm.IsDeleteConfirmationOpen);
        Assert.Contains("2 шт.", vm.DeleteConfirmationText, StringComparison.Ordinal);

        await vm.ConfirmDeleteSelectedAsync();

        Assert.Equal(2, analyzer.LastDeletedPaths.Count);
        Assert.Contains(@"D:\Games\OldRpg", analyzer.LastDeletedPaths);
        Assert.Contains(@"D:\Games\patch.iso", analyzer.LastDeletedPaths);
    }

    [Fact]
    public async Task DisksViewModel_UncheckingOtherRows_DoesNotCloseSingleItemQuickDeleteBanner()
    {
        var analyzer = new StubDiskSpaceAnalyzer();
        var vm = new DisksViewModel(diskOptimizer: null, spaceAnalyzer: analyzer, autoLoadOnInit: false);

        await vm.SelectVolumeForSpaceMapAsync('D');
        await vm.OpenFolderAsync(vm.LargestFolders[0]); // D:\Games

        var folderItem = vm.LargestFolders[0];
        var fileItem = vm.LargestFolders[1];

        folderItem.IsSelected = true;
        fileItem.QuickDeleteCommand.Execute(null);
        Assert.True(vm.IsDeleteConfirmationOpen);

        folderItem.IsSelected = false;
        Assert.Equal(0, vm.SelectedEntriesCount);
        Assert.True(vm.IsDeleteConfirmationOpen);
        Assert.Same(fileItem, vm.PendingSingleDeleteItem);
    }

    private sealed class StubDiskSpaceAnalyzer : IDiskSpaceAnalyzer
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

            DiskSpaceEntry[] gamesEntries =
            [
                new("OldRpg", @"D:\Games\OldRpg", true, 30_000_000_000, 500, 60.0, 100.0),
                new("patch.iso", @"D:\Games\patch.iso", false, 15_000_000_000, 1, 30.0, 50.0),
                new("ModsLink", @"D:\Games\ModsLink", true, 0, 0, 0.0, 0.0, IsProtectedPath: false, IsReparsePoint: true, ProtectionReason: "ReparsePoint"),
                new("Windows", @"C:\Windows", true, 20_000_000_000, 1000, 20.0, 40.0, IsProtectedPath: true, IsReparsePoint: false, ProtectionReason: "Системна тека захищена")
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
                FreedBytes: 15_000_000_000,
                BlockedCount: 0,
                DeletedPaths: fullPaths,
                Failures: []));
        }
    }
}
