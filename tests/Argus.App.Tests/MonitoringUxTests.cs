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

public sealed class MonitoringUxTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusMonitoringUxV2Tests_{Guid.NewGuid():N}");
    private readonly List<IDisposable> _disposables = [];

    public MonitoringUxTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void MonitoringViewModel_InitializesCpuThreadsAndGpuDetailsCollapsedByDefault_AndPopulatesVram()
    {
        using var vm = CreateViewModel();

        Assert.False(vm.IsCpuThreadsExpanded);
        Assert.Equal("▶", vm.CpuThreadsChevron);
        Assert.False(vm.IsGpuDetailsExpanded);
        Assert.Equal("▶", vm.GpuDetailsChevron);
        Assert.Contains("VRAM:", vm.GpuVramDetailText, StringComparison.Ordinal);
        Assert.Contains("3", vm.GpuVramDetailText, StringComparison.Ordinal);

        vm.ToggleCpuThreadsCommand.Execute(null);
        Assert.True(vm.IsCpuThreadsExpanded);
        Assert.Equal("▼", vm.CpuThreadsChevron);

        vm.ToggleGpuDetailsCommand.Execute(null);
        Assert.True(vm.IsGpuDetailsExpanded);
        Assert.Equal("▼", vm.GpuDetailsChevron);
    }

    [Fact]
    public void GaugeConsumerItem_BackwardCompatibleConstructorAndEnrichedPropertiesWork()
    {
        var legacy = new GaugeConsumerItem(1, 201, "chrome", "22.4%", "2.4 ГіБ", 22.4);
        Assert.Null(legacy.ExecutablePath);
        Assert.False(legacy.HasExecutablePath);
        Assert.False(legacy.IsCriticalSystem);
        Assert.True(legacy.CanTerminate);

        var regular = new GaugeConsumerItem(
            2,
            202,
            "blender",
            "55.0%",
            "1.1 ГіБ",
            55.0,
            executablePath: @"C:\Tools\blender.exe",
            isCriticalSystem: false,
            isProtectedAccess: false);
        Assert.Equal(@"C:\Tools\blender.exe", regular.ExecutablePath);
        Assert.True(regular.HasExecutablePath);
        Assert.True(regular.CanTerminate);

        var criticalByName = new GaugeConsumerItem(
            3,
            512,
            "csrss",
            "1.0%",
            "12 МБ",
            1.0,
            executablePath: @"C:\Windows\System32\csrss.exe");
        Assert.True(criticalByName.IsCriticalSystem);
        Assert.False(criticalByName.CanTerminate);

        var currentProcess = new GaugeConsumerItem(
            4,
            Environment.ProcessId,
            "Argus.App",
            "2.0%",
            "90 МБ",
            2.0,
            executablePath: @"C:\Argus\Argus.App.exe");
        Assert.True(currentProcess.IsCriticalSystem);
        Assert.False(currentProcess.CanTerminate);
    }

    [Theory]
    [InlineData("CPU")]
    [InlineData("RAM")]
    [InlineData("GPU")]
    [InlineData("TEMP")]
    public async Task MonitoringViewModel_AllFourGauges_EnrichConsumersWithPathAndTerminateFlags(string gaugeKind)
    {
        var ct = TestContext.Current.CancellationToken;
        var processes = CreateSampleProcesses();
        var gpuReader = new FakeGpuProcessMetricsReader(new Dictionary<int, double>
        {
            [202] = 64.5,
            [205] = 18.2,
            [301] = 12.0
        });

        using var vm = CreateViewModel(
            processMonitor: new FakeProcessMonitorService(processes),
            gpuReader: gpuReader);

        vm.ActiveGaugeConsumerKind = gaugeKind;
        await vm.RefreshGaugeConsumersAsync(ct);

        Assert.True(vm.HasGaugeConsumers);
        var blender = Assert.Single(vm.GaugeConsumers, c => c.Pid == 202);
        Assert.Equal(@"C:\Apps\blender.exe", blender.ExecutablePath);
        Assert.True(blender.HasExecutablePath);
        Assert.True(blender.CanTerminate);

        var noPathProcess = vm.GaugeConsumers.FirstOrDefault(c => c.Pid == 301);
        if (noPathProcess is not null)
        {
            Assert.Null(noPathProcess.ExecutablePath);
            Assert.False(noPathProcess.HasExecutablePath);
            Assert.False(noPathProcess.CanTerminate);
        }
    }

    [Fact]
    public async Task MonitoringViewModel_NormalMode_IgnoresExpertGaugeConsumerActions()
    {
        var ct = TestContext.Current.CancellationToken;
        var inspector = new FakeSystemInspectorService();
        using var vm = CreateViewModel(
            processMonitor: new FakeProcessMonitorService(CreateSampleProcesses()),
            systemInspector: inspector);

        Assert.False(vm.IsExpertMode);
        vm.ActiveGaugeConsumerKind = "CPU";
        await vm.RefreshGaugeConsumersAsync(ct);

        var target = Assert.Single(vm.GaugeConsumers, c => c.Pid == 202);
        vm.OpenGaugeConsumerFolderCommand.Execute(target);
        vm.RequestTerminateGaugeConsumerCommand.Execute(target);

        Assert.Empty(inspector.OpenedFolders);
        Assert.Empty(inspector.TerminatedPids);
        Assert.False(vm.HasPendingGaugeConsumerTerminate);
        Assert.False(vm.HasGaugeConsumerFeedback);
    }

    [Fact]
    public async Task MonitoringViewModel_ExpertMode_OpensFolderAndTerminatesWithInlineConfirmation()
    {
        var ct = TestContext.Current.CancellationToken;
        var inspector = new FakeSystemInspectorService();
        using var vm = CreateViewModel(
            processMonitor: new FakeProcessMonitorService(CreateSampleProcesses()),
            systemInspector: inspector);

        vm.IsExpertMode = true;
        vm.ActiveGaugeConsumerKind = "CPU";
        await vm.RefreshGaugeConsumersAsync(ct);

        var blender = Assert.Single(vm.GaugeConsumers, c => c.Pid == 202);

        // Open folder works when executable path is present
        vm.OpenGaugeConsumerFolderCommand.Execute(blender);
        Assert.Single(inspector.OpenedFolders, @"C:\Apps\blender.exe");
        Assert.True(vm.HasGaugeConsumerFeedback);
        Assert.True(vm.GaugeConsumerFeedbackIsSuccess);

        // Request -> Cancel does not terminate
        vm.RequestTerminateGaugeConsumerCommand.Execute(blender);
        Assert.True(vm.HasPendingGaugeConsumerTerminate);
        Assert.Contains("blender", vm.PendingGaugeConsumerTerminatePrompt, StringComparison.Ordinal);

        vm.CancelTerminateGaugeConsumerCommand.Execute(null);
        Assert.False(vm.HasPendingGaugeConsumerTerminate);
        Assert.Empty(inspector.TerminatedPids);

        // Request -> Confirm terminates via ISystemInspectorService
        vm.RequestTerminateGaugeConsumerCommand.Execute(blender);
        await vm.ConfirmTerminateGaugeConsumerCommand.ExecuteAsync(null);

        Assert.False(vm.HasPendingGaugeConsumerTerminate);
        Assert.Single(inspector.TerminatedPids, 202);
        Assert.True(vm.HasGaugeConsumerFeedback);
        Assert.True(vm.GaugeConsumerFeedbackIsSuccess);
        Assert.False(vm.ShowGaugeConsumerUacCallout);
    }

    [Fact]
    public async Task MonitoringViewModel_ExpertMode_BlocksCriticalProcessAndShowsUacWhenElevationRequired()
    {
        var ct = TestContext.Current.CancellationToken;
        var inspector = new FakeSystemInspectorService
        {
            NextTerminateResult = new SystemActionResult(false, RequiresElevation: true, "Потрібні права адміністратора (UAC).")
        };

        using var vm = CreateViewModel(
            processMonitor: new FakeProcessMonitorService(CreateSampleProcesses()),
            systemInspector: inspector);

        vm.IsExpertMode = true;
        vm.ActiveGaugeConsumerKind = "CPU";
        await vm.RefreshGaugeConsumersAsync(ct);

        // Critical process is blocked before confirmation
        var critical = Assert.Single(vm.GaugeConsumers, c => c.Pid == 301);
        Assert.False(critical.CanTerminate);
        vm.RequestTerminateGaugeConsumerCommand.Execute(critical);
        Assert.False(vm.HasPendingGaugeConsumerTerminate);
        Assert.True(vm.HasGaugeConsumerFeedback);
        Assert.False(vm.GaugeConsumerFeedbackIsSuccess);
        Assert.Empty(inspector.TerminatedPids);

        // Protected non-critical process requiring elevation triggers contextual UAC callout
        var chrome = Assert.Single(vm.GaugeConsumers, c => c.Pid == 201);
        vm.RequestTerminateGaugeConsumerCommand.Execute(chrome);
        Assert.True(vm.HasPendingGaugeConsumerTerminate);
        await vm.ConfirmTerminateGaugeConsumerCommand.ExecuteAsync(null);

        Assert.Single(inspector.TerminatedPids, 201);
        Assert.False(vm.GaugeConsumerFeedbackIsSuccess);
        Assert.True(vm.ShowGaugeConsumerUacCallout);
        Assert.Contains("UAC", vm.GaugeConsumerElevationReason, StringComparison.Ordinal);

        // Disabling Expert Mode clears pending confirmation & UAC callout
        vm.RequestTerminateGaugeConsumerCommand.Execute(chrome);
        vm.IsExpertMode = false;
        Assert.False(vm.HasPendingGaugeConsumerTerminate);
        Assert.False(vm.ShowGaugeConsumerUacCallout);
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

    private static IReadOnlyList<ProcessMetricsItem> CreateSampleProcesses() =>
    [
        new ProcessMetricsItem(202, "blender", 55.0, 1_100_000_000L, 0, 0, 24, @"C:\Apps\blender.exe", false, false),
        new ProcessMetricsItem(201, "chrome", 22.4, 2_400_000_000L, 0, 0, 42, @"C:\Apps\chrome.exe", false, true),
        new ProcessMetricsItem(301, "csrss", 18.0, 900_000_000L, 0, 0, 16, null, true, true),
        new ProcessMetricsItem(205, "obs64", 12.0, 620_000_000L, 0, 0, 30, @"C:\Apps\obs64.exe", false, false)
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
            Task.FromResult(new SystemActionResult(true, false, $"Процес PID {pid} завершено."));

        public bool OpenProcessFolder(string? executablePath) => !string.IsNullOrWhiteSpace(executablePath);
    }

    private sealed class FakeSystemInspectorService : ISystemInspectorService
    {
        public List<string> OpenedFolders { get; } = [];
        public List<int> TerminatedPids { get; } = [];
        public SystemActionResult? NextTerminateResult { get; set; }

        public Task<IReadOnlyList<ProcessInspectItem>> GetProcessesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessInspectItem>>([]);

        public Task<IReadOnlyList<WindowsServiceInspectItem>> GetServicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WindowsServiceInspectItem>>([]);

        public Task<IReadOnlyList<LocalPortInspectItem>> GetLocalPortsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalPortInspectItem>>([]);

        public Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default)
        {
            TerminatedPids.Add(pid);
            return Task.FromResult(NextTerminateResult ?? new SystemActionResult(true, false, $"Процес PID {pid} завершено."));
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
