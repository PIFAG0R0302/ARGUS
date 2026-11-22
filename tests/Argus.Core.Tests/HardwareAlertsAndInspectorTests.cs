using System.Diagnostics;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.System;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Core.Journal;
using Argus.Maintenance.SystemServices;
using Argus.Telemetry.Alerts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Core.Tests;

public sealed class HardwareAlertsAndInspectorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusCoreInspectorTests_{Guid.NewGuid():N}");

    public HardwareAlertsAndInspectorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ArgusSettings_NewDefaultsClampingAndLegacyJsonBackwardCompatibility()
    {
        var ct = TestContext.Current.CancellationToken;
        var defaults = new ArgusSettings().Normalize();
        Assert.False(defaults.ExpertModeEnabled);
        Assert.False(defaults.EnableSystemTray);
        Assert.Equal(10.0, defaults.DiskFreeSpaceAlertPercent);
        Assert.True(defaults.EnableSmartDegradationAlert);

        var clampedLow = new ArgusSettings { DiskFreeSpaceAlertPercent = 0.5 }.Normalize();
        Assert.Equal(2.0, clampedLow.DiskFreeSpaceAlertPercent);

        var clampedHigh = new ArgusSettings { DiskFreeSpaceAlertPercent = 95.0 }.Normalize();
        Assert.Equal(50.0, clampedHigh.DiskFreeSpaceAlertPercent);

        // Legacy settings.json without the new properties must deserialize cleanly with defaults.
        string legacyPath = Path.Combine(_tempDir, "legacy-settings.json");
        await File.WriteAllTextAsync(
            legacyPath,
            """
            {
              "telemetryIntervalMs": 1500,
              "cpuTemperatureAlertCelsius": 88,
              "minimizeToTrayOnClose": true
            }
            """,
            ct);

        using var store = new JsonArgusSettingsStore(NullLogger<JsonArgusSettingsStore>.Instance, legacyPath);
        var loaded = await store.LoadAsync(ct);

        Assert.Equal(1500, loaded.TelemetryIntervalMs);
        Assert.Equal(88.0, loaded.CpuTemperatureAlertCelsius);
        Assert.True(loaded.MinimizeToTrayOnClose);
        Assert.False(loaded.ExpertModeEnabled);
        Assert.False(loaded.EnableSystemTray);
        Assert.Equal(10.0, loaded.DiskFreeSpaceAlertPercent);
        Assert.True(loaded.EnableSmartDegradationAlert);
    }

    [Theory]
    [InlineData(TelemetryAlertKind.CpuTemperatureHigh, true)]
    [InlineData(TelemetryAlertKind.GpuTemperatureHigh, true)]
    [InlineData(TelemetryAlertKind.DiskSpaceLow, true)]
    [InlineData(TelemetryAlertKind.DiskSmartDegraded, true)]
    [InlineData(TelemetryAlertKind.RamUsageHigh, false)]
    public void TelemetryAlertPolicy_AllowsOnlyHardwareAndDiskTrayAlerts(TelemetryAlertKind kind, bool expected)
    {
        Assert.Equal(expected, TelemetryAlertPolicy.IsHardwareOrDiskTrayAlert(kind));
    }

    [Fact]
    public async Task DiskHealthAlertEvaluator_EmitsLowSpaceAndSmartDegradedWithCooldown()
    {
        var ct = TestContext.Current.CancellationToken;
        var settingsStore = new InMemorySettingsStore(new ArgusSettings
        {
            DiskFreeSpaceAlertPercent = 10.0,
            EnableSmartDegradationAlert = true
        });
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = CreateJournal(eventBus, "disk-alerts.jsonl");

        var published = new List<TelemetryAlertEvent>();
        using var sub = eventBus.Subscribe<TelemetryAlertEvent>(e => published.Add(e));

        IReadOnlyList<DriveSpaceSample> drives =
        [
            new DriveSpaceSample("C:", "System", TotalBytes: 100L * 1024 * 1024 * 1024, FreeBytes: 5L * 1024 * 1024 * 1024),
            new DriveSpaceSample("D:", "Data", TotalBytes: 500L * 1024 * 1024 * 1024, FreeBytes: 250L * 1024 * 1024 * 1024)
        ];

        IReadOnlyList<DiskSmartSample> disks =
        [
            new DiskSmartSample(0, "Samsung SSD 980", SmartHealthStatus.Healthy),
            new DiskSmartSample(1, "WD Blue HDD", SmartHealthStatus.Caution),
            new DiskSmartSample(2, "Old SATA SSD", SmartHealthStatus.Critical)
        ];

        var evaluator = new DiskHealthAlertEvaluator(
            settingsStore,
            eventBus,
            journal,
            driveSpaceReader: () => drives,
            smartHealthReader: _ => Task.FromResult(disks),
            cooldown: TimeSpan.FromMinutes(15));

        var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var first = await evaluator.EvaluateAsync(t0, ct);

        Assert.Equal(3, first.Count);
        Assert.Contains(first, a => a.Kind == TelemetryAlertKind.DiskSpaceLow && a.Component == "C:");
        Assert.Contains(first, a => a.Kind == TelemetryAlertKind.DiskSmartDegraded && a.Component == "WD Blue HDD");
        Assert.Contains(first, a => a.Kind == TelemetryAlertKind.DiskSmartDegraded && a.Component == "Old SATA SSD");
        Assert.Equal(3, published.Count);
        Assert.Equal(3, journal.GetRecent(10).Count);

        // Within 15 min cooldown: no duplicate alerts
        var second = await evaluator.EvaluateAsync(t0.AddMinutes(10), ct);
        Assert.Empty(second);
        Assert.Equal(3, published.Count);

        // After cooldown expires: alerts fire again
        var third = await evaluator.EvaluateAsync(t0.AddMinutes(16), ct);
        Assert.Equal(3, third.Count);
        Assert.Equal(6, published.Count);
    }

    [Fact]
    public async Task DiskHealthAlertEvaluator_RespectsDisabledSmartAlertsSetting()
    {
        var ct = TestContext.Current.CancellationToken;
        var settingsStore = new InMemorySettingsStore(new ArgusSettings
        {
            DiskFreeSpaceAlertPercent = 10.0,
            EnableSmartDegradationAlert = false
        });
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = CreateJournal(eventBus, "smart-disabled.jsonl");

        bool smartReaderCalled = false;
        var evaluator = new DiskHealthAlertEvaluator(
            settingsStore,
            eventBus,
            journal,
            driveSpaceReader: () =>
            [
                new DriveSpaceSample("C:", "OS", 100_000, 50_000)
            ],
            smartHealthReader: _ =>
            {
                smartReaderCalled = true;
                return Task.FromResult<IReadOnlyList<DiskSmartSample>>(
                [
                    new DiskSmartSample(0, "Failing Disk", SmartHealthStatus.Critical)
                ]);
            });

        var alerts = await evaluator.EvaluateAsync(DateTimeOffset.UtcNow, ct);
        Assert.Empty(alerts);
        Assert.False(smartReaderCalled);
    }

    [Fact]
    public async Task WindowsSystemInspectorService_EnumeratesProcessesServicesAndPortsWithoutAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = CreateJournal(eventBus, "inspector.jsonl");
        var privilege = new StubPrivilegeService(isAdmin: false, canWrite: true);
        var inspector = new WindowsSystemInspectorService(
            privilege,
            journal,
            NullLogger<WindowsSystemInspectorService>.Instance);

        var processes = await inspector.GetProcessesAsync(ct);
        Assert.NotEmpty(processes);
        Assert.Contains(processes, p => p.Pid == Environment.ProcessId && p.IsSystemOrProtected);
        for (int i = 1; i < processes.Count; i++)
        {
            Assert.True(processes[i - 1].WorkingSetBytes >= processes[i].WorkingSetBytes);
        }

        var services = await inspector.GetServicesAsync(ct);
        Assert.NotEmpty(services);
        Assert.All(services, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.ServiceName));
            Assert.False(string.IsNullOrWhiteSpace(s.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(s.Status));
        });

        var ports = await inspector.GetLocalPortsAsync(ct);
        Assert.NotEmpty(ports);
        Assert.All(ports, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Protocol));
            Assert.False(string.IsNullOrWhiteSpace(p.LocalAddress));
            Assert.InRange(p.LocalPort, 0, 65535);
            Assert.False(string.IsNullOrWhiteSpace(p.ProcessName));
        });

        Assert.Equal(80, WindowsSystemInspectorService.DecodeNetworkPort(0x5000));
        Assert.Equal(443, WindowsSystemInspectorService.DecodeNetworkPort(0xBB01));
    }

    [Fact]
    public async Task WindowsSystemInspectorService_ProtectsCriticalPidsAndRequiresUacForServices()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = CreateJournal(eventBus, "inspector-actions.jsonl");
        var privilege = new StubPrivilegeService(isAdmin: false, canWrite: false);
        var inspector = new WindowsSystemInspectorService(
            privilege,
            journal,
            NullLogger<WindowsSystemInspectorService>.Instance);

        var r0 = await inspector.TerminateProcessAsync(0, ct);
        var r4 = await inspector.TerminateProcessAsync(4, ct);
        var rSelf = await inspector.TerminateProcessAsync(Environment.ProcessId, ct);

        Assert.False(r0.Succeeded);
        Assert.False(r0.RequiresElevation);
        Assert.False(r4.Succeeded);
        Assert.False(rSelf.Succeeded);

        var svcRes = await inspector.ControlServiceAsync("Spooler", ServiceControlAction.Restart, ct);
        Assert.False(svcRes.Succeeded);
        Assert.True(svcRes.RequiresElevation);
        Assert.Contains("UAC", svcRes.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(inspector.OpenProcessFolder(null));
        Assert.False(inspector.OpenProcessFolder(Path.Combine(_tempDir, "missing.exe")));
        Assert.True(journal.GetRecent(10).Count >= 4);
    }

    [Fact]
    public void PrivilegeServiceAndUpdater_ElevateCmdInstallerOnlyWhenDirectoryNotWritable()
    {
        var privilegeService = new WindowsPrivilegeService(NullLogger<WindowsPrivilegeService>.Instance);
        Assert.True(privilegeService.CanWriteToDirectory(_tempDir));
        Assert.False(privilegeService.CanWriteToDirectory(Path.Combine(_tempDir, "does-not-exist")));
        Assert.False(privilegeService.CanWriteToDirectory(string.Empty));

        var settingsStore = new InMemorySettingsStore(new ArgusSettings());
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = CreateJournal(eventBus, "updater-uac.jsonl");

        string cmdPath = Path.Combine(_tempDir, "apply-update.cmd");
        File.WriteAllText(cmdPath, "@echo off\r\n");

        using var nonWritableUpdater = new GitHubAppUpdateService(
            settingsStore,
            journal,
            eventBus,
            updatesDirectoryOverride: _tempDir,
            signatureInspector: _ => (false, null, null),
            privilegeService: new StubPrivilegeService(isAdmin: false, canWrite: false));

        var elevatedPsi = nonWritableUpdater.BuildInstallerStartInfo(cmdPath, @"C:\Program Files\ARGUS");
        Assert.True(elevatedPsi.UseShellExecute);
        Assert.Equal("runas", elevatedPsi.Verb);
        Assert.Equal(ProcessWindowStyle.Hidden, elevatedPsi.WindowStyle);

        using var writableUpdater = new GitHubAppUpdateService(
            settingsStore,
            journal,
            eventBus,
            updatesDirectoryOverride: _tempDir,
            signatureInspector: _ => (false, null, null),
            privilegeService: new StubPrivilegeService(isAdmin: false, canWrite: true));

        var standardPsi = writableUpdater.BuildInstallerStartInfo(cmdPath, _tempDir);
        Assert.False(standardPsi.UseShellExecute);
        Assert.True(standardPsi.CreateNoWindow);
        Assert.True(string.IsNullOrEmpty(standardPsi.Verb));
    }

    private FileBackedOperationJournal CreateJournal(IArgusEventBus bus, string fileName) =>
        new(bus, NullLogger<FileBackedOperationJournal>.Instance, Path.Combine(_tempDir, fileName));

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
            // Best-effort temp cleanup
        }
    }

    private sealed class InMemorySettingsStore(ArgusSettings initial) : IArgusSettingsStore
    {
        public string SettingsFilePath => string.Empty;
        public ArgusSettings Current { get; private set; } = initial.Normalize();
        public Task<ArgusSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task SaveAsync(ArgusSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings.Normalize();
            return Task.CompletedTask;
        }
    }

    private sealed class StubPrivilegeService(bool isAdmin, bool canWrite) : IPrivilegeService
    {
        public bool IsAdministrator => isAdmin;
        public bool TryRelaunchElevated(string[]? arguments = null) => false;
        public bool CanWriteToDirectory(string directoryPath) => canWrite;
    }
}
