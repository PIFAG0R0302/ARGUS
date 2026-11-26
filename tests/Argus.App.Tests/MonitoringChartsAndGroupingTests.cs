using Argus.App.Controls;
using Argus.App.Services;
using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Telemetry.Providers;
using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class MonitoringChartsAndGroupingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusMonitoringChartsGroupingTests_{Guid.NewGuid():N}");
    private readonly List<IDisposable> _disposables = [];

    public MonitoringChartsAndGroupingTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Theory]
    [InlineData("CPU")]
    [InlineData("RAM")]
    [InlineData("GPU")]
    [InlineData("TEMP")]
    public async Task MonitoringViewModel_GroupsTopConsumersByApplication_AcrossAllFourGauges(string gaugeKind)
    {
        var ct = TestContext.Current.CancellationToken;
        var processes = CreateMultiInstanceSampleProcesses();
        var gpuReader = new FakeGpuProcessMetricsReader(new Dictionary<int, double>
        {
            [101] = 22.0,
            [102] = 18.0,
            [103] = 4.0,
            [201] = 15.0,
            [301] = 5.0
        });

        using var vm = CreateViewModel(
            processMonitor: new FakeProcessMonitorService(processes),
            gpuReader: gpuReader);

        vm.ActiveGaugeConsumerKind = gaugeKind;
        await vm.RefreshGaugeConsumersAsync(ct);

        Assert.True(vm.HasGaugeConsumers);
        Assert.NotEmpty(vm.GaugeConsumers);
        Assert.NotEmpty(vm.GaugeConsumerGroups);

        // chrome + Chrome.exe + chrome (3 instances) should be grouped into one group
        var chromeGroup = Assert.Single(vm.GaugeConsumerGroups, g => string.Equals(g.GroupKey, "chrome", StringComparison.OrdinalIgnoreCase));
        Assert.True(chromeGroup.IsMultiInstance);
        Assert.False(chromeGroup.IsSingleInstance);
        Assert.Equal(3, chromeGroup.InstanceCount);
        Assert.Equal("3 проц.", chromeGroup.CountBadgeText);
        Assert.Equal("3 проц.", chromeGroup.PidOrCountText);
        Assert.Equal(3, chromeGroup.Items.Count);
        Assert.False(chromeGroup.IsExpanded);
        Assert.Equal("▶", chromeGroup.ExpandGlyph);
        Assert.True(chromeGroup.CanTerminate);
        Assert.True(chromeGroup.HasExecutablePath);

        // blender has 1 instance
        var blenderGroup = Assert.Single(vm.GaugeConsumerGroups, g => string.Equals(g.GroupKey, "blender", StringComparison.OrdinalIgnoreCase));
        Assert.True(blenderGroup.IsSingleInstance);
        Assert.False(blenderGroup.IsMultiInstance);
        Assert.Equal(1, blenderGroup.InstanceCount);
        Assert.Equal("PID 201", blenderGroup.PidOrCountText);
        Assert.Single(blenderGroup.Items);

        // System Idle (PID 0) must be excluded
        Assert.DoesNotContain(vm.GaugeConsumerGroups, g => g.Items.Any(i => i.Pid == 0));
    }

    [Fact]
    public async Task MonitoringViewModel_PreservesGroupExpandedStateAcrossRefreshes()
    {
        var ct = TestContext.Current.CancellationToken;
        var processes = CreateMultiInstanceSampleProcesses();
        var monitor = new FakeProcessMonitorService(processes);

        using var vm = CreateViewModel(processMonitor: monitor);

        vm.ActiveGaugeConsumerKind = "CPU";
        await vm.RefreshGaugeConsumersAsync(ct);

        var chromeGroup = Assert.Single(vm.GaugeConsumerGroups, g => g.GroupKey == "chrome");
        Assert.False(chromeGroup.IsExpanded);

        chromeGroup.ToggleExpandedCommand.Execute(null);
        Assert.True(chromeGroup.IsExpanded);
        Assert.Equal("▼", chromeGroup.ExpandGlyph);

        // Simulate 2.5s periodic refresh with updated CPU values
        monitor.SetItems(
        [
            new ProcessMetricsItem(101, "chrome", 25.0, 900_000_000L, 0, 0, 20, @"C:\Apps\chrome.exe", false, false),
            new ProcessMetricsItem(102, "Chrome.exe", 19.0, 800_000_000L, 0, 0, 14, @"C:\Apps\chrome.exe", false, false),
            new ProcessMetricsItem(103, "chrome", 8.0, 300_000_000L, 0, 0, 8, @"C:\Apps\chrome.exe", false, false),
            new ProcessMetricsItem(201, "blender", 30.0, 1_100_000_000L, 0, 0, 24, @"C:\Apps\blender.exe", false, false)
        ]);

        await vm.RefreshGaugeConsumersAsync(ct);

        var refreshedChrome = Assert.Single(vm.GaugeConsumerGroups, g => g.GroupKey == "chrome");
        Assert.Same(chromeGroup, refreshedChrome);
        Assert.True(refreshedChrome.IsExpanded);
        Assert.Equal("▼", refreshedChrome.ExpandGlyph);
        Assert.Equal("52,0%".Replace(',', '.'), refreshedChrome.PrimaryMetricText.Replace(',', '.'));

        // Switching gauge kind resets expansion cleanly
        vm.SelectGaugeConsumerKindCommand.Execute("RAM");
        await vm.RefreshGaugeConsumersAsync(ct);
        var ramChrome = Assert.Single(vm.GaugeConsumerGroups, g => g.GroupKey == "chrome");
        Assert.False(ramChrome.IsExpanded);
    }

    [Fact]
    public async Task MonitoringViewModel_ExpertMode_TerminatesMultiInstanceGroup_SkippingCriticalProcesses()
    {
        var ct = TestContext.Current.CancellationToken;
        var processes = new List<ProcessMetricsItem>
        {
            new(101, "myworker", 10.0, 400_000_000L, 0, 0, 10, @"C:\Apps\helper\myworker.exe", true, true),
            new(102, "myworker", 8.0, 200_000_000L, 0, 0, 8, @"C:\Apps\helper\myworker.exe", false, false),
            new(201, "chrome", 15.0, 700_000_000L, 0, 0, 18, @"C:\Apps\chrome.exe", false, false),
            new(202, "chrome", 12.0, 500_000_000L, 0, 0, 12, @"C:\Apps\chrome.exe", false, false)
        };

        var inspector = new FakeSystemInspectorService();
        using var vm = CreateViewModel(
            processMonitor: new FakeProcessMonitorService(processes),
            systemInspector: inspector);

        vm.IsExpertMode = true;
        vm.ActiveGaugeConsumerKind = "CPU";
        await vm.RefreshGaugeConsumersAsync(ct);

        // Open folder on group works
        var chromeGroup = Assert.Single(vm.GaugeConsumerGroups, g => g.GroupKey == "chrome");
        vm.OpenGaugeConsumerGroupFolderCommand.Execute(chromeGroup);
        Assert.Single(inspector.OpenedFolders, @"C:\Apps\chrome.exe");

        // Request group terminate shows confirmation prompt with count
        vm.RequestTerminateGaugeConsumerGroupCommand.Execute(chromeGroup);
        Assert.True(vm.HasPendingGaugeConsumerTerminate);
        Assert.Contains("2", vm.PendingGaugeConsumerTerminatePrompt, StringComparison.Ordinal);
        Assert.Contains("chrome", vm.PendingGaugeConsumerTerminatePrompt, StringComparison.Ordinal);

        await vm.ConfirmTerminateGaugeConsumerCommand.ExecuteAsync(null);
        Assert.Equal(new[] { 201, 202 }, inspector.TerminatedPids);
        Assert.True(vm.HasGaugeConsumerFeedback);
        Assert.True(vm.GaugeConsumerFeedbackIsSuccess);

        // Mixed critical + non-critical group skips critical PID 101
        inspector.TerminatedPids.Clear();
        var mixedGroup = Assert.Single(vm.GaugeConsumerGroups, g => g.GroupKey == "myworker");
        Assert.True(mixedGroup.CanTerminate);
        vm.RequestTerminateGaugeConsumerGroupCommand.Execute(mixedGroup);
        Assert.True(vm.HasPendingGaugeConsumerTerminate);
        Assert.Contains("пропущено", vm.PendingGaugeConsumerTerminatePrompt, StringComparison.Ordinal);

        await vm.ConfirmTerminateGaugeConsumerCommand.ExecuteAsync(null);
        Assert.Single(inspector.TerminatedPids, 102);
        Assert.DoesNotContain(101, inspector.TerminatedPids);
    }

    [Fact]
    public void TelemetryLineChart_UsesDarkGreenGpuSeriesColor()
    {
        var expected = Color.Parse("#16A34A");
        Assert.Equal(expected, TelemetryLineChart.GpuColor);
        Assert.Equal(expected, TelemetryLineChart.GpuCoreBrush.Color);
        Assert.Equal(Color.FromArgb(80, expected.R, expected.G, expected.B), TelemetryLineChart.GpuHaloBrush.Color);
        Assert.Same(TelemetryLineChart.GpuCoreBrush, TelemetryLineChart.GpuPen.Brush);
    }

    [Fact]
    public void MonitoringViewModel_GatesHardwareDetailsPanelsByExpertMode()
    {
        using var vm = CreateViewModel();

        Assert.False(vm.IsExpertMode);
        Assert.False(vm.ShowCpuThreadsPanel);
        Assert.False(vm.ShowGpuDetailsPanel);

        // Even if internal expanded flags are toggled while in Basic mode, gated panel properties stay false
        vm.ToggleCpuThreadsCommand.Execute(null);
        vm.ToggleGpuDetailsCommand.Execute(null);
        Assert.True(vm.IsCpuThreadsExpanded);
        Assert.True(vm.IsGpuDetailsExpanded);
        Assert.False(vm.ShowCpuThreadsPanel);
        Assert.False(vm.ShowGpuDetailsPanel);

        // Enabling Expert mode reveals the expanded hardware details panels
        vm.IsExpertMode = true;
        Assert.True(vm.ShowCpuThreadsPanel);
        Assert.True(vm.ShowGpuDetailsPanel);

        // Turning Expert mode back off hides them again
        vm.IsExpertMode = false;
        Assert.False(vm.ShowCpuThreadsPanel);
        Assert.False(vm.ShowGpuDetailsPanel);
    }

    private MonitoringViewModel CreateViewModel(
        IProcessMonitorService? processMonitor = null,
        IGpuProcessMetricsReader? gpuReader = null,
        ISystemInspectorService? systemInspector = null)
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
            systemInspector: systemInspector,
            processMonitor: processMonitor,
            gpuProcessMetricsReader: gpuReader ?? new FakeGpuProcessMetricsReader(new Dictionary<int, double>()));
    }

    private static IReadOnlyList<ProcessMetricsItem> CreateMultiInstanceSampleProcesses() =>
    [
        new ProcessMetricsItem(0, "Idle", 99.0, 8192, 0, 0, 8, null, true, true),
        new ProcessMetricsItem(101, "chrome", 14.0, 900_000_000L, 0, 0, 20, @"C:\Apps\chrome.exe", false, false),
        new ProcessMetricsItem(102, "Chrome.exe", 11.5, 700_000_000L, 0, 0, 14, @"C:\Apps\chrome.exe", false, false),
        new ProcessMetricsItem(103, "chrome", 6.5, 400_000_000L, 0, 0, 10, @"C:\Apps\chrome.exe", false, false),
        new ProcessMetricsItem(201, "blender", 45.0, 1_100_000_000L, 0, 0, 24, @"C:\Apps\blender.exe", false, false),
        new ProcessMetricsItem(301, "msedge", 4.2, 350_000_000L, 0, 0, 12, @"C:\Apps\msedge.exe", false, false),
        new ProcessMetricsItem(302, "msedge", 3.1, 250_000_000L, 0, 0, 9, @"C:\Apps\msedge.exe", false, false)
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
        private IReadOnlyList<ProcessMetricsItem> _items = items;

        public void SetItems(IReadOnlyList<ProcessMetricsItem> items) => _items = items;

        public Task<IReadOnlyList<ProcessMetricsItem>> GetProcessMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_items);

        public Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, $"Процес PID {pid} завершено."));

        public bool OpenProcessFolder(string? executablePath) => !string.IsNullOrWhiteSpace(executablePath);
    }

    private sealed class FakeSystemInspectorService : ISystemInspectorService
    {
        public List<string> OpenedFolders { get; } = [];
        public List<int> TerminatedPids { get; } = [];

        public Task<IReadOnlyList<ProcessInspectItem>> GetProcessesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessInspectItem>>([]);

        public Task<IReadOnlyList<WindowsServiceInspectItem>> GetServicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WindowsServiceInspectItem>>([]);

        public Task<IReadOnlyList<LocalPortInspectItem>> GetLocalPortsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalPortInspectItem>>([]);

        public Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default)
        {
            TerminatedPids.Add(pid);
            return Task.FromResult(new SystemActionResult(true, false, $"Процес PID {pid} завершено."));
        }

        public Task<SystemActionResult> ControlServiceAsync(string serviceName, ServiceControlAction action, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));

        public bool OpenProcessFolder(string? executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            OpenedFolders.Add(executablePath);
            return true;
        }
    }

    private sealed class FakeGpuProcessMetricsReader(IReadOnlyDictionary<int, double> values) : IGpuProcessMetricsReader
    {
        public IReadOnlyDictionary<int, double> SamplePerProcessGpuUtilization() => values;
        public void Dispose() { }
    }
}
