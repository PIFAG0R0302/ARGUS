using System.Text.Json;
using Argus.App.ViewModels;
using Argus.Core;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Core.Journal;
using Argus.Telemetry;
using Argus.Telemetry.Alerts;
using Argus.Telemetry.Providers;
using Argus.Telemetry.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class TelemetryEngineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusTelemetryTests_{Guid.NewGuid():N}");

    public TelemetryEngineTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void CpuLoadComputation_CalculatesAccuratePerCoreAndTotalPercentages()
    {
        // Windows KernelTime includes IdleTime.
        // Core 0: deltaTotal = 1000 (kernel 800 + user 200), deltaIdle = 500 => 50% busy.
        // Core 1: deltaTotal = 1000 (kernel 400 + user 600), deltaIdle = 100 => 90% busy.
        var prev = new[]
        {
            new WindowsCpuTelemetryProvider.ProcessorPerformanceSample(1000, 2000, 500),
            new WindowsCpuTelemetryProvider.ProcessorPerformanceSample(2000, 3000, 1000)
        };
        var curr = new[]
        {
            new WindowsCpuTelemetryProvider.ProcessorPerformanceSample(1500, 2800, 700),
            new WindowsCpuTelemetryProvider.ProcessorPerformanceSample(2100, 3400, 1600)
        };

        var (total, perCore) = WindowsCpuTelemetryProvider.ComputeCpuLoads(prev, curr, 2);

        Assert.Equal(2, perCore.Length);
        Assert.Equal(50.0, perCore[0]);
        Assert.Equal(90.0, perCore[1]);
        Assert.Equal(70.0, total);
    }

    [Fact]
    public void AcpiThermalConversion_ConvertsValidReadingsAndRejectsSentinels()
    {
        Assert.True(AcpiThermalSensorProvider.TryConvertDeciKelvinToCelsius(3010, out double c1));
        Assert.Equal(27.9, c1);

        Assert.True(AcpiThermalSensorProvider.TryConvertKelvinToCelsius(315, out double c2));
        Assert.Equal(41.9, c2);

        Assert.False(AcpiThermalSensorProvider.TryConvertDeciKelvinToCelsius(0, out _));
        Assert.False(AcpiThermalSensorProvider.TryConvertDeciKelvinToCelsius(5000, out _));
    }

    [Fact]
    public void StorageDiskIndexParser_ExtractsPhysicalDriveNumbersAndSkipsTotal()
    {
        Assert.True(WindowsStorageTelemetryProvider.TryParsePhysicalDiskIndex("0 C:", out int d0));
        Assert.Equal(0, d0);

        Assert.True(WindowsStorageTelemetryProvider.TryParsePhysicalDiskIndex("2 E: F:", out int d2));
        Assert.Equal(2, d2);

        Assert.False(WindowsStorageTelemetryProvider.TryParsePhysicalDiskIndex("_Total", out _));
        Assert.False(WindowsStorageTelemetryProvider.TryParsePhysicalDiskIndex("invalid", out _));
    }

    [Fact]
    public async Task PawnIoProvider_ReflectsSettingAndDriverProbeTruthfully()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "pawn-settings.json"));

        var providerWithoutDriver = new PawnIoSensorProvider(store, () => false);
        Assert.Equal(PawnIoAvailabilityState.DisabledBySetting, providerWithoutDriver.GetPawnIoState());
        Assert.False(providerWithoutDriver.IsAvailable);

        await store.SaveAsync(store.Current with { EnablePawnIoExtendedSensors = true }, ct);
        Assert.Equal(PawnIoAvailabilityState.DriverNotInstalled, providerWithoutDriver.GetPawnIoState());
        Assert.False(providerWithoutDriver.IsAvailable);

        var providerWithDriver = new PawnIoSensorProvider(store, () => true);
        Assert.Equal(PawnIoAvailabilityState.Ready, providerWithDriver.GetPawnIoState());
        Assert.True(providerWithDriver.IsAvailable);
    }

    [Fact]
    public async Task TelemetryService_SamplesIntoRingBufferPublishesEventsAndIsolatesFaultyProviders()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var store = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "svc-settings.json"));

        var received = new List<TelemetrySnapshot>();
        using var sub = bus.Subscribe<TelemetryUpdatedEvent>(e => received.Add(e.Snapshot));

        using var service = new ArgusTelemetryService(
            new StubCpuProvider(),
            new StubMemoryProvider(),
            new StubGpuProvider(),
            new StubStorageProvider(),
            [new ThrowingSensorProvider()],
            store,
            bus,
            NullLogger<ArgusTelemetryService>.Instance);

        var snap = await service.SampleOnceAsync(ct);

        Assert.NotNull(service.Latest);
        Assert.Single(received);
        Assert.Single(service.GetHistory(10));
        Assert.Equal(42.5, snap.Cpu.TotalLoadPercent);
        Assert.Equal(50.0, snap.Memory.LoadPercent);
        Assert.Single(snap.Gpus);
        Assert.Equal(56.0, snap.Gpus[0].TemperatureCelsius);
        Assert.Equal(SensorTier.NativeZeroDriver, snap.ActiveHighestTier);
    }

    [Fact]
    public async Task MonitoringViewModel_UpdatesAllGaugesCoresAndPeakTemperatureFromSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var store = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "vm-settings.json"));

        using var service = new ArgusTelemetryService(
            new StubCpuProvider(),
            new StubMemoryProvider(),
            new StubGpuProvider(),
            new StubStorageProvider(),
            [],
            store,
            bus,
            NullLogger<ArgusTelemetryService>.Instance);

        var tierStatus = new PawnIoSensorProvider(store, () => false);
        using var vm = new MonitoringViewModel(store, service, tierStatus, bus);

        Assert.True(vm.CpuStandby);
        Assert.True(vm.RamStandby);

        var snap = await service.SampleOnceAsync(ct);

        Assert.False(vm.CpuStandby);
        Assert.False(vm.RamStandby);
        Assert.False(vm.GpuStandby);
        Assert.False(vm.TempStandby);
        Assert.Equal(42.5, vm.CpuLoadValue);
        Assert.Equal(4, vm.CoreLoads.Count);
        Assert.Equal(56.0, vm.TempValue);
        Assert.Contains("GTX 1660", vm.TempSubLabel, StringComparison.Ordinal);
        Assert.Single(vm.StorageItems);
        Assert.Contains("37", vm.StorageItems[0].TemperatureText, StringComparison.Ordinal);

        var (peak, source) = MonitoringViewModel.ResolvePeakTemperature(snap);
        Assert.Equal(56.0, peak);
        Assert.Contains("GTX 1660", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealWindowsTier1Providers_CollectLiveSystemSnapshotWithoutErrors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var store = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "real-settings.json"));

        using var cpu = new WindowsCpuTelemetryProvider();
        var mem = new WindowsMemoryTelemetryProvider();
        using var gpu = new CompositeGpuTelemetryProvider();
        using var storage = new WindowsStorageTelemetryProvider();
        using var acpi = new AcpiThermalSensorProvider();
        var pawnIo = new PawnIoSensorProvider(store);

        using var service = new ArgusTelemetryService(
            cpu,
            mem,
            gpu,
            storage,
            [acpi, pawnIo],
            store,
            bus,
            NullLogger<ArgusTelemetryService>.Instance);

        await Task.Delay(120, ct);
        var snap = await service.SampleOnceAsync(ct);

        Assert.False(string.IsNullOrWhiteSpace(snap.Cpu.Name));
        Assert.True(snap.Cpu.LogicalProcessors >= 1);
        Assert.Equal(snap.Cpu.LogicalProcessors, snap.Cpu.PerCoreLoadPercent.Count);
        Assert.InRange(snap.Cpu.TotalLoadPercent, 0.0, 100.0);
        Assert.True(snap.Memory.TotalBytes > 0);
        Assert.InRange(snap.Memory.LoadPercent, 0.0, 100.0);
        Assert.True(snap.Storage.Count >= 1);
    }

    [Fact]
    public async Task TelemetryAlertEvaluator_TriggersAlertsRecordsJournalAndEnforcesCooldown()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var store = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "alert-settings.json"));
        await store.SaveAsync(new ArgusSettings
        {
            CpuTemperatureAlertCelsius = 80,
            GpuTemperatureAlertCelsius = 82,
            RamUsageAlertPercent = 88
        }, ct);

        using var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "alert-logs", "journal.jsonl"));

        var evaluator = new TelemetryAlertEvaluator(store, bus, journal, cooldown: TimeSpan.FromSeconds(60));
        var publishedAlerts = new List<TelemetryAlertEvent>();
        using var sub = bus.Subscribe<TelemetryAlertEvent>(e => publishedAlerts.Add(e));

        var t0 = DateTimeOffset.UtcNow;
        var hotSnap1 = new TelemetrySnapshot(
            t0,
            SensorTier.SignedPawnIo,
            new CpuSnapshot("Test CPU", 4, 8, 85.0, [85.0], 4500, PackageTemperatureCelsius: 84.5, PackagePowerWatts: 95),
            new MemorySnapshot(32_000_000_000, 29_440_000_000, 2_560_000_000, 92.0),
            [new GpuSnapshot("NVIDIA GTX 1660", "NVIDIA", 95.0, 6_000_000_000, 3_000_000_000, 86.0, 1800, 2100)],
            [],
            []);

        var firstBatch = await evaluator.EvaluateAsync(hotSnap1, ct);
        Assert.Equal(3, firstBatch.Count);
        Assert.Equal(3, publishedAlerts.Count);
        Assert.Equal(3, journal.GetRecent(10).Count(static j => j.Action == "TelemetryAlert"));

        // Second evaluation 10s later falls inside 60s cooldown -> suppressed
        var hotSnap2 = hotSnap1 with { Timestamp = t0.AddSeconds(10) };
        var suppressedBatch = await evaluator.EvaluateAsync(hotSnap2, ct);
        Assert.Empty(suppressedBatch);
        Assert.Equal(3, publishedAlerts.Count);

        // Third evaluation after cooldown expires -> fires again
        var hotSnap3 = hotSnap1 with { Timestamp = t0.AddSeconds(65) };
        var resumedBatch = await evaluator.EvaluateAsync(hotSnap3, ct);
        Assert.Equal(3, resumedBatch.Count);
        Assert.Equal(6, publishedAlerts.Count);
    }

    [Fact]
    public async Task TelemetryReportExporter_ExportsValidHtmlJsonAndCsvReports()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var store = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "export-settings.json"));
        using var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "export-logs", "journal.jsonl"));

        using var service = new ArgusTelemetryService(
            new StubCpuProvider(),
            new StubMemoryProvider(),
            new StubGpuProvider(),
            new StubStorageProvider(),
            [],
            store,
            bus,
            NullLogger<ArgusTelemetryService>.Instance);

        await service.SampleOnceAsync(ct);
        await service.SampleOnceAsync(ct);

        var exporter = new TelemetryReportExporter(service, store, journal);
        string reportsDir = Path.Combine(_tempDir, "reports");

        var html = await exporter.ExportAsync(TelemetryReportFormat.Html, reportsDir, 60, ct);
        var json = await exporter.ExportAsync(TelemetryReportFormat.Json, reportsDir, 60, ct);
        var csv = await exporter.ExportAsync(TelemetryReportFormat.Csv, reportsDir, 60, ct);

        Assert.True(html.Succeeded);
        Assert.True(File.Exists(html.FilePath));
        string htmlText = await File.ReadAllTextAsync(html.FilePath, ct);
        Assert.Contains("<!DOCTYPE html>", htmlText, StringComparison.Ordinal);
        Assert.Contains("Intel Core i7-9700K", htmlText, StringComparison.Ordinal);
        Assert.Contains("NVIDIA GeForce GTX 1660", htmlText, StringComparison.Ordinal);

        Assert.True(json.Succeeded);
        Assert.True(File.Exists(json.FilePath));
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(json.FilePath, ct)))
        {
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("history").ValueKind);
            Assert.Equal(2, doc.RootElement.GetProperty("history").GetArrayLength());
        }

        Assert.True(csv.Succeeded);
        Assert.True(File.Exists(csv.FilePath));
        string[] csvLines = await File.ReadAllLinesAsync(csv.FilePath, ct);
        Assert.Equal(3, csvLines.Length); // header + 2 rows
        Assert.StartsWith("TimestampUtc,SensorTier,CpuLoadPercent", csvLines[0], StringComparison.Ordinal);
        Assert.Equal(3, journal.GetRecent(10).Count(static j => j.Action == "TelemetryReportExported"));
    }

    [Fact]
    public async Task MonitoringViewModel_SwitchesTimeWindowsPopulatesChartSeriesHandlesAlertsAndExportsReports()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var store = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "mon-chart-settings.json"));
        using var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "mon-chart-logs", "journal.jsonl"));

        using var service = new ArgusTelemetryService(
            new StubCpuProvider(),
            new StubMemoryProvider(),
            new StubGpuProvider(),
            new StubStorageProvider(),
            [],
            store,
            bus,
            NullLogger<ArgusTelemetryService>.Instance);

        for (int i = 0; i < 40; i++)
        {
            await service.SampleOnceAsync(ct);
        }

        var exporter = new TelemetryReportExporter(service, store, journal);
        var tierStatus = new PawnIoSensorProvider(store, () => false);
        using var vm = new MonitoringViewModel(store, service, tierStatus, bus, reportExporter: exporter);

        Assert.True(vm.IsWindow60Selected);
        Assert.Equal(40, vm.CpuSeries.Count);
        Assert.Equal(40, vm.RamSeries.Count);
        Assert.Equal(40, vm.GpuSeries.Count);
        Assert.Equal(40, vm.TempSeries.Count);

        vm.SelectWindow30Command.Execute(null);
        Assert.True(vm.IsWindow30Selected);
        Assert.False(vm.IsWindow60Selected);
        Assert.Equal(30, vm.CpuSeries.Count);

        vm.SelectWindow180Command.Execute(null);
        Assert.True(vm.IsWindow180Selected);
        Assert.Equal(40, vm.CpuSeries.Count);

        bus.Publish(new TelemetryAlertEvent(
            TelemetryAlertKind.GpuTemperatureHigh,
            "NVIDIA GeForce GTX 1660",
            88.0,
            85.0,
            "°C",
            "Перегрів GPU GTX 1660 (88 °C)",
            DateTimeOffset.UtcNow));

        Assert.True(vm.HasActiveAlert);
        Assert.Contains("88", vm.ActiveAlertText, StringComparison.Ordinal);
        vm.DismissAlertCommand.Execute(null);
        Assert.False(vm.HasActiveAlert);

        await vm.ExportJsonReportCommand.ExecuteAsync(null);
        Assert.True(vm.HasExportFeedback);
        Assert.True(vm.ExportSucceeded);
        Assert.Contains("Json", vm.ExportFeedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverviewViewModel_PopulatesCpuAndRamSparklinesOnTelemetryUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var store = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "ov-spark-settings.json"));
        using var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "ov-spark-logs", "journal.jsonl"));

        using var service = new ArgusTelemetryService(
            new StubCpuProvider(),
            new StubMemoryProvider(),
            new StubGpuProvider(),
            new StubStorageProvider(),
            [],
            store,
            bus,
            NullLogger<ArgusTelemetryService>.Instance);

        var tierStatus = new PawnIoSensorProvider(store, () => false);
        using var overview = new OverviewViewModel([new TelemetryModule()], store, journal, bus, service, tierStatus);

        Assert.Empty(overview.CpuSparklineValues);
        await service.SampleOnceAsync(ct);
        await service.SampleOnceAsync(ct);

        Assert.Equal(2, overview.CpuSparklineValues.Count);
        Assert.Equal(2, overview.RamSparklineValues.Count);
        Assert.All(overview.CpuSparklineValues, static v => Assert.Equal(42.5, v));
        Assert.All(overview.RamSparklineValues, static v => Assert.Equal(50.0, v));
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

    private sealed class StubCpuProvider : ICpuTelemetryProvider
    {
        public CpuSnapshot Sample(double? packageTemperatureCelsius = null, double? packagePowerWatts = null) =>
            new("Intel Core i7-9700K", 4, 4, 42.5, [30.0, 50.0, 40.0, 50.0], 4200, packageTemperatureCelsius, packagePowerWatts);
        public void Dispose() { }
    }

    private sealed class StubMemoryProvider : IMemoryTelemetryProvider
    {
        public MemorySnapshot Sample() =>
            new(32L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024, 50.0);
    }

    private sealed class StubGpuProvider : IGpuTelemetryProvider
    {
        public (IReadOnlyList<GpuSnapshot> Gpus, IReadOnlyList<SensorReading> ExtraSensors) Sample(DateTimeOffset timestamp) =>
            ([new GpuSnapshot("NVIDIA GeForce GTX 1660", "NVIDIA", 28.0, 6L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024, 56.0, 1785, null)], []);
        public void Dispose() { }
    }

    private sealed class StubStorageProvider : IStorageTelemetryProvider
    {
        public IReadOnlyList<StorageThermalSnapshot> Sample() =>
            [new StorageThermalSnapshot(0, "Test NVMe SSD", 37.0, 12_500_000, 4_200_000)];
        public void Dispose() { }
    }

    private sealed class ThrowingSensorProvider : ISensorProvider
    {
        public string ProviderId => "test.faulty";
        public string DisplayName => "Faulty";
        public SensorTier Tier => SensorTier.NativeZeroDriver;
        public bool IsAvailable => true;
        public Task<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated sensor read failure");
    }
}
