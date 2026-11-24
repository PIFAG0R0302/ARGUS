using Argus.App.Services;
using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Telemetry.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class UxMonitoringUpdateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusMonitoringUxTests_{Guid.NewGuid():N}");
    private readonly List<IDisposable> _disposables = [];

    public UxMonitoringUpdateTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void MonitoringViewModel_TogglesCpuThreadsAndGpuDetailsCollapsibleSections()
    {
        using var vm = CreateViewModel();

        Assert.True(vm.IsCpuThreadsExpanded);
        Assert.Equal("▼", vm.CpuThreadsChevron);
        Assert.True(vm.IsGpuDetailsExpanded);
        Assert.Equal("▼", vm.GpuDetailsChevron);

        vm.ToggleCpuThreadsCommand.Execute(null);
        Assert.False(vm.IsCpuThreadsExpanded);
        Assert.Equal("▶", vm.CpuThreadsChevron);

        vm.ToggleCpuThreadsCommand.Execute(null);
        Assert.True(vm.IsCpuThreadsExpanded);
        Assert.Equal("▼", vm.CpuThreadsChevron);

        vm.ToggleGpuDetailsCommand.Execute(null);
        Assert.False(vm.IsGpuDetailsExpanded);
        Assert.Equal("▶", vm.GpuDetailsChevron);

        vm.ToggleGpuDetailsCommand.Execute(null);
        Assert.True(vm.IsGpuDetailsExpanded);
        Assert.Equal("▼", vm.GpuDetailsChevron);
    }

    [Fact]
    public void MonitoringViewModel_InteractiveGaugeSelection_OpensSwitchesAndCollapsesPanel()
    {
        var processes = CreateSampleProcesses();
        using var vm = CreateViewModel(processMonitor: new FakeProcessMonitorService(processes));

        Assert.False(vm.IsGaugeConsumersOpen);
        Assert.Null(vm.ActiveGaugeConsumerKind);
        Assert.False(vm.IsCpuGaugeSelected);
        Assert.False(vm.IsRamGaugeSelected);
        Assert.False(vm.IsGpuGaugeSelected);
        Assert.False(vm.IsTempGaugeSelected);

        // Select CPU gauge
        vm.SelectGaugeConsumerKindCommand.Execute("CPU");
        Assert.True(vm.IsGaugeConsumersOpen);
        Assert.Equal("CPU", vm.ActiveGaugeConsumerKind);
        Assert.True(vm.IsCpuGaugeSelected);
        Assert.False(vm.IsRamGaugeSelected);

        // Switch directly to RAM gauge
        vm.SelectGaugeConsumerKindCommand.Execute("RAM");
        Assert.True(vm.IsGaugeConsumersOpen);
        Assert.Equal("RAM", vm.ActiveGaugeConsumerKind);
        Assert.False(vm.IsCpuGaugeSelected);
        Assert.True(vm.IsRamGaugeSelected);

        // Clicking the active gauge again collapses the panel
        vm.SelectGaugeConsumerKindCommand.Execute("RAM");
        Assert.False(vm.IsGaugeConsumersOpen);
        Assert.Null(vm.ActiveGaugeConsumerKind);
        Assert.False(vm.IsRamGaugeSelected);
    }

    [Fact]
    public async Task MonitoringViewModel_CpuAndRamGaugeConsumers_RanksTopProcessesCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var processes = CreateSampleProcesses();
        using var vm = CreateViewModel(processMonitor: new FakeProcessMonitorService(processes));

        vm.ActiveGaugeConsumerKind = "CPU";
        await vm.RefreshGaugeConsumersAsync(ct);

        Assert.True(vm.HasGaugeConsumers);
        Assert.Equal(6, vm.GaugeConsumers.Count);
        Assert.Equal("blender", vm.GaugeConsumers[0].ProcessName);
        Assert.Equal("#1", vm.GaugeConsumers[0].RankText);
        Assert.Equal("chrome", vm.GaugeConsumers[1].ProcessName);
        Assert.DoesNotContain(vm.GaugeConsumers, c => c.Pid == 0);

        vm.ActiveGaugeConsumerKind = "RAM";
        await vm.RefreshGaugeConsumersAsync(ct);

        Assert.True(vm.HasGaugeConsumers);
        Assert.Equal(6, vm.GaugeConsumers.Count);
        Assert.Equal("chrome", vm.GaugeConsumers[0].ProcessName);
        Assert.Equal("rider64", vm.GaugeConsumers[1].ProcessName);
    }

    [Fact]
    public async Task MonitoringViewModel_GpuGaugeConsumers_UsesPerPidGpuUtilizationAndFallsBackWhenUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var processes = CreateSampleProcesses();

        // 1) When per-PID GPU utilization is available
        var gpuReader = new FakeGpuProcessMetricsReader(new Dictionary<int, double>
        {
            [202] = 64.5, // blender
            [205] = 18.2  // obs64
        });
        using var vmWithGpu = CreateViewModel(
            processMonitor: new FakeProcessMonitorService(processes),
            gpuReader: gpuReader);

        vmWithGpu.ActiveGaugeConsumerKind = "GPU";
        await vmWithGpu.RefreshGaugeConsumersAsync(ct);

        Assert.True(vmWithGpu.HasGaugeConsumers);
        Assert.Equal(2, vmWithGpu.GaugeConsumers.Count);
        Assert.Equal("blender", vmWithGpu.GaugeConsumers[0].ProcessName);
        Assert.Contains("GPU", vmWithGpu.GaugeConsumers[0].PrimaryMetricText, StringComparison.Ordinal);
        Assert.Equal("obs64", vmWithGpu.GaugeConsumers[1].ProcessName);

        // 2) When per-PID GPU utilization is unavailable / all 0% -> graceful fallback
        var emptyGpuReader = new FakeGpuProcessMetricsReader(new Dictionary<int, double>());
        using var vmFallback = CreateViewModel(
            processMonitor: new FakeProcessMonitorService(processes),
            gpuReader: emptyGpuReader);

        vmFallback.ActiveGaugeConsumerKind = "GPU";
        await vmFallback.RefreshGaugeConsumersAsync(ct);

        Assert.True(vmFallback.HasGaugeConsumers);
        Assert.Equal(6, vmFallback.GaugeConsumers.Count);
        Assert.Equal("blender", vmFallback.GaugeConsumers[0].ProcessName);
        Assert.Contains("CPU/RAM", vmFallback.GaugeConsumersSubtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MonitoringViewModel_TempGaugeConsumers_ShowsThermalSummaryAndTopHeatingProcesses()
    {
        var ct = TestContext.Current.CancellationToken;
        var processes = CreateSampleProcesses();
        var gpuReader = new FakeGpuProcessMetricsReader(new Dictionary<int, double>
        {
            [205] = 75.0 // obs64: 12% CPU + 75% GPU = 87 heat score > blender's 55%
        });

        using var vm = CreateViewModel(
            processMonitor: new FakeProcessMonitorService(processes),
            gpuReader: gpuReader);

        vm.ActiveGaugeConsumerKind = "TEMP";
        await vm.RefreshGaugeConsumersAsync(ct);

        Assert.True(vm.HasGaugeConsumers);
        Assert.Contains("CPU: 68 °C", vm.GaugeConsumersSubtitle, StringComparison.Ordinal);
        Assert.Contains("GPU: 61 °C", vm.GaugeConsumersSubtitle, StringComparison.Ordinal);
        Assert.Equal("obs64", vm.GaugeConsumers[0].ProcessName);
        Assert.Contains("GPU", vm.GaugeConsumers[0].PrimaryMetricText, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuProcessMetricsReader_ParsesAndAggregatesPidUtilizationFromInstanceNames()
    {
        Assert.True(GpuProcessMetricsReader.TryParsePidFromInstanceName(
            "pid_4512_luid_0x00000000_0x00012345_phys_0_eng_0_engtype_3D",
            out int pid));
        Assert.Equal(4512, pid);

        Assert.False(GpuProcessMetricsReader.TryParsePidFromInstanceName("invalid_instance", out _));
        Assert.False(GpuProcessMetricsReader.TryParsePidFromInstanceName("pid_0_luid_0x0", out _));
        Assert.False(GpuProcessMetricsReader.TryParsePidFromInstanceName(null, out _));

        var aggregated = GpuProcessMetricsReader.AggregatePerPidUtilization(
        [
            new KeyValuePair<string, double>("pid_100_luid_0_eng_0_engtype_3D", 45.5),
            new KeyValuePair<string, double>("pid_100_luid_0_eng_1_engtype_Copy", 12.5),
            new KeyValuePair<string, double>("pid_200_luid_0_eng_0_engtype_3D", 80.0),
            new KeyValuePair<string, double>("pid_200_luid_0_eng_1_engtype_Compute", 40.0),
            new KeyValuePair<string, double>("pid_300_luid_0_eng_0_engtype_3D", 0.0),
            new KeyValuePair<string, double>("malformed", 25.0)
        ]);

        Assert.Equal(2, aggregated.Count);
        Assert.Equal(58.0, aggregated[100], precision: 1);
        Assert.Equal(100.0, aggregated[200], precision: 1);
        Assert.False(aggregated.ContainsKey(300));
    }

    private MonitoringViewModel CreateViewModel(
        IProcessMonitorService? processMonitor = null,
        IGpuProcessMetricsReader? gpuReader = null)
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        var settings = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, $"settings_{Guid.NewGuid():N}.json"));
        _disposables.Add(settings);

        var snapshot = new TelemetrySnapshot(
            DateTimeOffset.UtcNow,
            SensorTier.NativeZeroDriver,
            new CpuSnapshot("AMD Ryzen 7 5800X", 8, 16, 48.0, [40.0, 55.0, 35.0, 62.0], 4200, 68.0, null),
            new MemorySnapshot(32L * 1024 * 1024 * 1024, 14L * 1024 * 1024 * 1024, 18L * 1024 * 1024 * 1024, 43.75),
            [new GpuSnapshot("NVIDIA GeForce RTX 3070", "NVIDIA", 42.0, 8L * 1024 * 1024 * 1024, 3L * 1024 * 1024 * 1024, 61.0, 1800, 1400)],
            [],
            []);

        var telemetry = new StubTelemetryService(snapshot);
        var tier = new PawnIoSensorProvider(settings, () => false);
        return new MonitoringViewModel(
            settings,
            telemetry,
            tier,
            bus,
            uiDispatch: static a => a(),
            processMonitor: processMonitor,
            gpuProcessMetricsReader: gpuReader ?? new FakeGpuProcessMetricsReader(new Dictionary<int, double>()));
    }

    private static IReadOnlyList<ProcessMetricsItem> CreateSampleProcesses() =>
    [
        new ProcessMetricsItem(0, "Idle", 99.0, 8192, 0, 0, 8, null, true, true),
        new ProcessMetricsItem(201, "chrome", 22.4, 2_400_000_000L, 0, 0, 42, @"C:\chrome.exe", false, false),
        new ProcessMetricsItem(202, "blender", 55.0, 1_100_000_000L, 0, 0, 24, @"C:\blender.exe", false, false),
        new ProcessMetricsItem(203, "rider64", 15.2, 1_900_000_000L, 0, 0, 60, @"C:\rider64.exe", false, false),
        new ProcessMetricsItem(204, "discord", 6.1, 450_000_000L, 0, 0, 18, @"C:\discord.exe", false, false),
        new ProcessMetricsItem(205, "obs64", 12.0, 620_000_000L, 0, 0, 30, @"C:\obs64.exe", false, false),
        new ProcessMetricsItem(206, "notepad", 0.5, 25_000_000L, 0, 0, 4, @"C:\notepad.exe", false, false),
        new ProcessMetricsItem(207, "spotify", 3.3, 310_000_000L, 0, 0, 16, @"C:\spotify.exe", false, false)
    ];

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors on temp folder.
        }
    }

    private sealed class StubTelemetryService(TelemetrySnapshot snapshot) : ITelemetryService
    {
        public bool IsRunning => false;
        public TelemetrySnapshot? Latest => snapshot;
        public IReadOnlyList<TelemetrySnapshot> GetHistory(int maxCount) => [snapshot];
        public Task<TelemetrySnapshot> SampleOnceAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeProcessMonitorService(IReadOnlyList<ProcessMetricsItem> items) : IProcessMonitorService
    {
        public Task<IReadOnlyList<ProcessMetricsItem>> GetProcessMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(items);

        public Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));

        public bool OpenProcessFolder(string? executablePath) => true;
    }

    private sealed class FakeGpuProcessMetricsReader(IReadOnlyDictionary<int, double> values) : IGpuProcessMetricsReader
    {
        public IReadOnlyDictionary<int, double> SamplePerProcessGpuUtilization() => values;
        public void Dispose() { }
    }
}
