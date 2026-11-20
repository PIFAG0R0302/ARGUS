using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Events;
using Argus.Core.Journal;
using Argus.Maintenance.Cleanup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class CleanerEngineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusCleanerTests_{Guid.NewGuid():N}");

    public CleanerEngineTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task DefaultCleanerRuleCatalog_ProvisionsAndValidatesExternalJsonRulesWithoutWarnings()
    {
        var ct = TestContext.Current.CancellationToken;
        string rulesDir = Path.Combine(_tempDir, "rules");
        var loader = new JsonCleanerRuleLoader();

        var result = await DefaultCleanerRuleCatalog.EnsureAndLoadRulesAsync(loader, rulesDir, ct);

        Assert.True(File.Exists(Path.Combine(rulesDir, "default-windows-cleanup.json")));
        Assert.True(File.Exists(Path.Combine(rulesDir, "browsers-and-apps.json")));
        Assert.Empty(result.Warnings);
        Assert.Equal(9, result.ValidRules.Count);
        Assert.Contains(result.ValidRules, static r => r.Id == "win.user_temp");
        Assert.Contains(result.ValidRules, static r => r.Id == "browser.chrome_cache");
        Assert.Contains(result.ValidRules, static r => r.Id == "win.recycle_bin");
        Assert.DoesNotContain("51", WindowsCleanerService.FormatBytes(5L * 1024 * 1024), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsCleanerService_TwoStepAnalyzeAndClean_RespectsAgeExclusionsAndRestorePoint()
    {
        var ct = TestContext.Current.CancellationToken;
        string targetDir = Path.Combine(_tempDir, "scan-target");
        string keepDir = Path.Combine(targetDir, "keep-subdir");
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(keepDir);

        string oldFile1 = Path.Combine(targetDir, "old1.tmp");
        string oldFile2 = Path.Combine(targetDir, "old2.tmp");
        string freshFile = Path.Combine(targetDir, "fresh.tmp");
        string excludedFile = Path.Combine(keepDir, "protected.tmp");

        await File.WriteAllBytesAsync(oldFile1, new byte[2048], ct);
        await File.WriteAllBytesAsync(oldFile2, new byte[4096], ct);
        await File.WriteAllBytesAsync(freshFile, new byte[1024], ct);
        await File.WriteAllBytesAsync(excludedFile, new byte[8192], ct);

        DateTime threeHoursAgo = DateTime.UtcNow.AddHours(-3);
        File.SetLastWriteTimeUtc(oldFile1, threeHoursAgo);
        File.SetLastWriteTimeUtc(oldFile2, threeHoursAgo);
        File.SetLastWriteTimeUtc(excludedFile, threeHoursAgo);
        File.SetLastWriteTimeUtc(freshFile, DateTime.UtcNow);

        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "logs", "journal.jsonl"));
        var restore = new StubRestorePointService();
        var recycleBin = new StubRecycleBinInterop();
        var cleaner = new WindowsCleanerService(restore, journal, recycleBin);

        var rule = new CleanerRuleDefinition(
            Id: "test.temp",
            DisplayName: "Test Temp Rule",
            Category: CleanerCategory.UserTemp,
            Description: "Test description",
            Paths: [targetDir],
            FilePatterns: ["*.tmp"],
            Recursive: true,
            MinFileAgeHours: 1,
            RequiresElevation: false,
            EnabledByDefault: true);

        // Step 1: Analyze (never deletes files)
        var report = await cleaner.AnalyzeAsync([rule], [keepDir], ct);

        Assert.Equal(2, report.TotalFiles);
        Assert.Equal(6144, report.TotalBytes);
        Assert.True(File.Exists(oldFile1));
        Assert.True(File.Exists(oldFile2));
        Assert.True(File.Exists(freshFile));
        Assert.True(File.Exists(excludedFile));

        // Step 2: Approved Clean with System Restore point
        var execResult = await cleaner.CleanAsync(
            report,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test.temp" },
            [keepDir],
            createRestorePointFirst: true,
            ct);

        Assert.Equal(1, restore.Calls);
        Assert.Equal(2, execResult.FilesDeleted);
        Assert.Equal(6144, execResult.BytesFreed);
        Assert.Empty(execResult.Errors);
        Assert.False(File.Exists(oldFile1));
        Assert.False(File.Exists(oldFile2));
        Assert.True(File.Exists(freshFile));
        Assert.True(File.Exists(excludedFile));
    }

    [Fact]
    public async Task WindowsCleanerService_HandlesRecycleBinRuleViaInteropSeam()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "rb-logs", "journal.jsonl"));
        var restore = new StubRestorePointService();
        var recycleBin = new StubRecycleBinInterop { Items = 5, Bytes = 10_485_760 };
        var cleaner = new WindowsCleanerService(restore, journal, recycleBin);

        var rbRule = DefaultCleanerRuleCatalog.GetBuiltInSystemRules()
            .First(static r => r.Category == CleanerCategory.RecycleBin);

        var report = await cleaner.AnalyzeAsync([rbRule], [], ct);
        Assert.Equal(5, report.TotalFiles);
        Assert.Equal(10_485_760, report.TotalBytes);

        var result = await cleaner.CleanAsync(
            report,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rbRule.Id },
            [],
            createRestorePointFirst: false,
            ct);

        Assert.True(recycleBin.Emptied);
        Assert.Equal(5, result.FilesDeleted);
        Assert.Equal(10_485_760, result.BytesFreed);
    }

    [Fact]
    public async Task CleanupViewModel_EnforcesTwoStepFlowManagesExclusionsAndExecutesCleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        string workDir = Path.Combine(_tempDir, "vm-work");
        string rulesDir = Path.Combine(workDir, "rules");
        string targetDir = Path.Combine(workDir, "cache");
        string excludedDir = Path.Combine(targetDir, "excluded");
        Directory.CreateDirectory(rulesDir);
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(excludedDir);

        string customRuleJson = $$"""
        {
          "rules": [
            {
              "id": "custom.cache",
              "displayName": "Тестовий кеш",
              "category": "Custom",
              "description": "Тестове правило очищення",
              "paths": [ "{{targetDir.Replace("\\", "\\\\")}}" ],
              "filePatterns": [ "*.cache" ],
              "recursive": true,
              "minFileAgeHours": 0,
              "requiresElevation": false,
              "enabledByDefault": true
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(rulesDir, "custom.json"), customRuleJson, ct);

        string cleanMe = Path.Combine(targetDir, "item1.cache");
        string keepMe = Path.Combine(excludedDir, "item2.cache");
        await File.WriteAllBytesAsync(cleanMe, new byte[4096], ct);
        await File.WriteAllBytesAsync(keepMe, new byte[8192], ct);

        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var store = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(workDir, "settings.json"));
        using var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(workDir, "logs", "journal.jsonl"));
        var restore = new StubRestorePointService();
        var cleaner = new WindowsCleanerService(restore, journal, new StubRecycleBinInterop());

        using var vm = new CleanupViewModel(
            store,
            new JsonCleanerRuleLoader(),
            cleaner,
            bus,
            rulesDirectory: rulesDir,
            autoLoadOnInit: false);

        await vm.ReloadRulesAsync();
        Assert.Single(vm.Rules);
        Assert.False(vm.CanClean);

        // First analyze without exclusion -> finds both files (12288 bytes)
        await vm.AnalyzeAsync();
        Assert.True(vm.HasScanReport);
        Assert.True(vm.CanClean);
        Assert.Equal(2, vm.Rules[0].ScannedFileCount);

        // Add exclusion -> must invalidate scan report and disable Clean until re-analyzed
        vm.NewExclusionPath = excludedDir;
        await vm.AddExclusionAsync();
        Assert.Contains(excludedDir, vm.Exclusions);
        Assert.False(vm.HasScanReport);
        Assert.False(vm.CanClean);

        // Re-analyze -> now finds only 1 file (4096 bytes)
        await vm.AnalyzeAsync();
        Assert.True(vm.CanClean);
        Assert.Equal(1, vm.Rules[0].ScannedFileCount);

        // Clean -> deletes only item1.cache, keeps item2.cache, and resets CanClean
        await vm.CleanSelectedAsync();
        Assert.False(vm.CanClean);
        Assert.False(File.Exists(cleanMe));
        Assert.True(File.Exists(keepMe));
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
            // best-effort temp cleanup
        }
    }

    private sealed class StubRestorePointService : IRestorePointService
    {
        public int Calls { get; private set; }
        public Task<RestorePointResult> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new RestorePointResult(true, description, "OK"));
        }
    }

    private sealed class StubRecycleBinInterop : IRecycleBinInterop
    {
        public int Items { get; set; }
        public long Bytes { get; set; }
        public bool Emptied { get; private set; }

        public (int ItemCount, long TotalBytes) QueryRecycleBin() => (Items, Bytes);
        public bool EmptyRecycleBin()
        {
            Emptied = true;
            return true;
        }
    }
}
