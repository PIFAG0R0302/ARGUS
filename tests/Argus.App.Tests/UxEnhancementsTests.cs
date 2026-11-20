using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Core.Journal;
using Argus.Telemetry.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class UxEnhancementsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusUxTests_{Guid.NewGuid():N}");

    public UxEnhancementsTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void MonitoringViewModel_FiltersChartSeriesIndependentlyAndRestoresAll()
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var settings = new JsonArgusSettingsStore(NullLogger<JsonArgusSettingsStore>.Instance, Path.Combine(_tempDir, "s.json"));
        var snapshot = new TelemetrySnapshot(
            DateTimeOffset.UtcNow,
            SensorTier.NativeZeroDriver,
            new CpuSnapshot("Test CPU", 4, 8, 42.5, [40.0, 45.0], 3800, 62.0, null),
            new MemorySnapshot(32L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024, 50.0),
            [new GpuSnapshot("NVIDIA GeForce GTX 1660", "NVIDIA", 33.0, 6L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024, 54.0, 1500, 1200)],
            [],
            []);

        var telemetry = new SingleSnapshotTelemetryService(snapshot);
        var tier = new PawnIoSensorProvider(settings, () => false);
        using var vm = new MonitoringViewModel(settings, telemetry, tier, bus);

        Assert.True(vm.AreAllSeriesVisible);
        Assert.Single(vm.CpuSeries);
        Assert.Single(vm.RamSeries);
        Assert.Single(vm.GpuSeries);
        Assert.Single(vm.TempSeries);

        // Leave only CPU + GPU visible
        vm.ToggleRamSeriesCommand.Execute(null);
        vm.ToggleTempSeriesCommand.Execute(null);
        Assert.False(vm.AreAllSeriesVisible);
        Assert.Single(vm.CpuSeries);
        Assert.Empty(vm.RamSeries);
        Assert.Single(vm.GpuSeries);
        Assert.Empty(vm.TempSeries);

        // Leave only CPU
        vm.ToggleGpuSeriesCommand.Execute(null);
        Assert.Single(vm.CpuSeries);
        Assert.Empty(vm.GpuSeries);

        // Restore all series
        vm.ShowAllSeriesCommand.Execute(null);
        Assert.True(vm.AreAllSeriesVisible);
        Assert.Single(vm.CpuSeries);
        Assert.Single(vm.RamSeries);
        Assert.Single(vm.GpuSeries);
        Assert.Single(vm.TempSeries);
    }

    [Fact]
    public async Task JournalViewModel_FiltersBySeverityAndSearchQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(bus, NullLogger<FileBackedOperationJournal>.Instance, Path.Combine(_tempDir, "j.jsonl"));

        await journal.RecordAsync("argus.app", OperationSeverity.Info, "AppStarted", "Старт програми", true, ct);
        await journal.RecordAsync("argus.maintenance", OperationSeverity.Warning, "DiskBlocked", "Потрібен UAC", false, ct);
        await journal.RecordAsync("argus.migration", OperationSeverity.Error, "CloneFailed", "Помилка запису", false, ct);

        using var vm = new JournalViewModel(journal, bus);
        Assert.Equal(3, vm.FilteredEntries.Count);

        vm.SetFilterWarningCommand.Execute(null);
        Assert.Single(vm.FilteredEntries);
        Assert.Equal("DiskBlocked", vm.FilteredEntries[0].Action);

        vm.SetFilterAllCommand.Execute(null);
        vm.SearchQuery = "CloneFailed";
        Assert.Single(vm.FilteredEntries);
        Assert.Equal("argus.migration", vm.FilteredEntries[0].ModuleId);
    }

    [Fact]
    public async Task DisksViewModel_ParsesDefragPercentProgressFromZeroToHundred()
    {
        var optimizer = new ProgressDiskOptimizer();
        var vm = new DisksViewModel(optimizer, autoLoadOnInit: false);

        await vm.OptimizeVolumeAsync('D');

        Assert.Equal(100, vm.OperationProgressPercent);
        Assert.Equal("100 %", vm.OperationProgressText);
        Assert.True(vm.StatusIsSuccess);
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
            // ignore
        }
    }

    private sealed class SingleSnapshotTelemetryService(TelemetrySnapshot snapshot) : ITelemetryService
    {
        public bool IsRunning => true;
        public TelemetrySnapshot? Latest => snapshot;
        public IReadOnlyList<TelemetrySnapshot> GetHistory(int maxSamples = 180) => [snapshot];
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ProgressDiskOptimizer : IDiskOptimizer
    {
        public Task<IReadOnlyList<PhysicalDiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhysicalDiskInfo>>([]);

        public Task<DiskOptimizationResult> OptimizeVolumeAsync(
            char driveLetter,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report("Analysis: 35% complete...");
            progress?.Report("Defragmentation: 80% complete...");
            progress?.Report("100% completed.");
            return Task.FromResult(new DiskOptimizationResult(
                driveLetter,
                StorageMediaKind.Hdd,
                DiskOptimizationAction.DefragmentHdd,
                true,
                TimeSpan.FromSeconds(1.5),
                "Дефрагментація завершена."));
        }
    }
}
