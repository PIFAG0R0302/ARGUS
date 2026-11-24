using Argus.App.ViewModels;
using Argus.Core;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.System;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Core.Extensions;
using Argus.Core.Journal;
using Argus.Devices;
using Argus.Maintenance;
using Argus.Migration;
using Argus.Telemetry;
using Argus.Telemetry.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class ViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusAppTests_{Guid.NewGuid():N}");

    public ViewModelTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void MainWindowViewModel_NavigatesAcrossAllSevenSectionsExclusively()
    {
        using var env = CreateEnv(isAdmin: false);
        var vm = env.Main;

        Assert.Equal(ArgusSectionId.Overview, vm.ActiveSection.SectionId);
        Assert.Equal("v1.2.1", vm.VersionBadge);
        Assert.Equal(10, vm.PrimaryNavigation.Count);
        Assert.Single(vm.FooterNavigation);

        foreach (ArgusSectionId id in Enum.GetValues<ArgusSectionId>())
        {
            vm.SelectSection(id);
            Assert.Equal(id, vm.ActiveSection.SectionId);
            Assert.False(string.IsNullOrWhiteSpace(vm.ActiveSectionTitle));
            Assert.False(string.IsNullOrWhiteSpace(vm.ActiveSectionSubtitle));

            var allNav = vm.PrimaryNavigation.Concat(vm.FooterNavigation).ToArray();
            var selected = Assert.Single(allNav, static n => n.IsSelected);
            Assert.Equal(id, selected.SectionId);
        }
    }

    [Fact]
    public void MainWindowViewModel_ElevationCommandRelaunchesOnlyWhenNotAdmin()
    {
        using var standardEnv = CreateEnv(isAdmin: false);
        standardEnv.Main.SelectSection(ArgusSectionId.Cleanup);
        standardEnv.Main.RequestElevationCommand.Execute(null);

        Assert.Equal(1, standardEnv.Privilege.RelaunchCalls);
        Assert.True(standardEnv.ExitRequested);
        Assert.Contains("cleanup", standardEnv.Privilege.LastArgs ?? []);

        using var adminEnv = CreateEnv(isAdmin: true);
        adminEnv.Main.RequestElevationCommand.Execute(null);

        Assert.Equal(0, adminEnv.Privilege.RelaunchCalls);
        Assert.False(adminEnv.ExitRequested);
    }

    [Fact]
    public void MigrationViewModel_SwitchesBetweenInternalAndExternalCopyOsProfiles()
    {
        var vm = new MigrationViewModel();

        Assert.True(vm.IsInternalSelected);
        Assert.False(vm.IsExternalSelected);
        Assert.Contains("SATA", vm.BusFilterSummary, StringComparison.Ordinal);

        vm.SelectExternalProfileCommand.Execute(null);
        Assert.True(vm.IsExternalSelected);
        Assert.Contains("USB", vm.BusFilterSummary, StringComparison.Ordinal);
        Assert.Contains("/nofirmwaresync", vm.FirmwarePolicySummary, StringComparison.Ordinal);

        vm.SelectMbrCommand.Execute(null);
        Assert.True(vm.IsMbrSelected);
        Assert.Contains("MBR", vm.PartitionLayoutSummary, StringComparison.Ordinal);

        vm.SelectGptCommand.Execute(null);
        Assert.True(vm.IsGptSelected);
        Assert.Contains("GPT", vm.PartitionLayoutSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsViewModel_SavesSettingsUpdatesStartupAndInvokesRestorePoint()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = CreateEnv(isAdmin: true);
        var vm = env.Settings;

        vm.TelemetryIntervalMs = 500;
        vm.HistoryWindowSeconds = 300;
        vm.CpuTemperatureAlertCelsius = 90;
        vm.EnablePawnIoExtendedSensors = true;
        vm.StartWithWindows = true;
        vm.MinimizeToTrayOnClose = true;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        var persisted = await env.SettingsStore.LoadAsync(ct);
        Assert.Equal(500, persisted.TelemetryIntervalMs);
        Assert.Equal(300, persisted.HistoryWindowSeconds);
        Assert.Equal(90.0, persisted.CpuTemperatureAlertCelsius);
        Assert.True(persisted.EnablePawnIoExtendedSensors);
        Assert.True(persisted.StartWithWindows);
        Assert.True(persisted.MinimizeToTrayOnClose);
        Assert.True(env.Startup.IsEnabled());
        Assert.True(vm.HasSaveFeedback);
        Assert.Contains(env.Journal.GetRecent(10), static j => j.Action == "SettingsSaved");

        await vm.CreateRestorePointNowCommand.ExecuteAsync(null);
        Assert.True(vm.HasRestoreFeedback);
        Assert.True(vm.RestoreFeedbackSuccess);
        Assert.Equal(1, env.Restore.CallCount);

        await vm.ResetDefaultsCommand.ExecuteAsync(null);

        var reset = await env.SettingsStore.LoadAsync(ct);
        Assert.Equal(1000, reset.TelemetryIntervalMs);
        Assert.False(reset.EnablePawnIoExtendedSensors);
        Assert.False(reset.StartWithWindows);
        Assert.False(env.Startup.IsEnabled());
        Assert.Contains(env.Journal.GetRecent(10), static j => j.Action == "SettingsReset");
    }

    private TestEnv CreateEnv(bool isAdmin)
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        var settingsStore = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "settings.json"));
        var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "logs", "journal.jsonl"));

        var privilege = new FakePrivilegeService(isAdmin);
        var startup = new FakeStartupService();
        var restore = new FakeRestorePointService();
        var exitFlag = new ExitBox();

        IArgusModule[] modules =
        [
            new CoreModule(),
            new TelemetryModule(),
            new DevicesModule(),
            new MaintenanceModule(),
            new MigrationModule()
        ];

        var telemetry = new FakeTelemetryService();
        var tierStatus = new PawnIoSensorProvider(settingsStore, () => false);

        var overview = new OverviewViewModel(modules, settingsStore, journal, bus, telemetry, tierStatus);
        var monitoring = new MonitoringViewModel(settingsStore, telemetry, tierStatus, bus);
        var devices = new DevicesViewModel();
        var cleanup = new CleanupViewModel(settingsStore);
        var disks = new DisksViewModel();
        var migration = new MigrationViewModel();
        var journalVm = new JournalViewModel(journal, bus);
        var settings = new SettingsViewModel(settingsStore, journal, bus, startup, restore);
        var networkVm = new NetworkViewModel(new FakeNetworkService(), autoLoadOnInit: false);
        var processesVm = new ProcessesViewModel(new FakeProcessMonitor(), autoLoadOnInit: false);
        var startupServicesVm = new StartupAndServicesViewModel(new FakeStartupAndServices(), autoLoadOnInit: false);

        var main = new MainWindowViewModel(
            overview,
            monitoring,
            devices,
            cleanup,
            disks,
            migration,
            journalVm,
            settings,
            settingsStore,
            privilege,
            requestAppExit: () => exitFlag.Requested = true,
            network: networkVm,
            startupAndServices: startupServicesVm,
            processes: processesVm);

        return new TestEnv(settingsStore, journal, settings, main, privilege, startup, restore, exitFlag);
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

    private sealed class ExitBox { public bool Requested; }

    private sealed class FakePrivilegeService(bool isAdmin) : IPrivilegeService
    {
        public bool IsAdministrator { get; } = isAdmin;
        public int RelaunchCalls { get; private set; }
        public string[]? LastArgs { get; private set; }

        public bool TryRelaunchElevated(string[]? arguments = null)
        {
            RelaunchCalls++;
            LastArgs = arguments;
            return true;
        }
    }

    private sealed class FakeStartupService : IStartupRegistrationService
    {
        private bool _enabled;
        public bool IsEnabled() => _enabled;
        public void SetEnabled(bool enabled, string executablePath) => _enabled = enabled;
    }

    private sealed class FakeRestorePointService : IRestorePointService
    {
        public int CallCount { get; private set; }
        public Task<RestorePointResult> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new RestorePointResult(true, description, "OK"));
        }
    }

    private sealed class FakeTelemetryService : ITelemetryService
    {
        public bool IsRunning => false;
        public TelemetrySnapshot? Latest => null;
        public IReadOnlyList<TelemetrySnapshot> GetHistory(int maxSamples = 180) => [];
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeNetworkService : INetworkManagerService
    {
        public Task<IReadOnlyList<ActiveConnectionItem>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActiveConnectionItem>>([]);
        public Task<IReadOnlyList<FirewallRuleItem>> GetFirewallRulesAsync(bool argusOnly = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirewallRuleItem>>([]);
        public Task<SystemActionResult> BlockApplicationAsync(string appName, string executablePath, FirewallRuleDirection direction = FirewallRuleDirection.Outbound, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));
        public Task<SystemActionResult> CreateFirewallRuleAsync(FirewallRuleCreateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));
        public Task<SystemActionResult> SetFirewallRuleEnabledAsync(string ruleName, bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));
        public Task<SystemActionResult> DeleteArgusFirewallRuleAsync(string ruleName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));
    }

    private sealed class FakeProcessMonitor : IProcessMonitorService
    {
        public Task<IReadOnlyList<ProcessMetricsItem>> GetProcessMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessMetricsItem>>([]);
        public Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));
        public bool OpenProcessFolder(string? executablePath) => true;
    }

    private sealed class FakeStartupAndServices : IStartupAndServicesManager
    {
        public Task<IReadOnlyList<StartupEntryItem>> GetStartupEntriesAsync(bool includeSystemTasks = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StartupEntryItem>>([]);
        public Task<SystemActionResult> SetStartupEntryEnabledAsync(StartupEntryItem entry, bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));
        public Task<IReadOnlyList<ManagedServiceItem>> GetManagedServicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedServiceItem>>([]);
        public Task<SystemActionResult> ControlServiceAsync(string serviceName, ServiceControlAction action, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));
        public Task<SystemActionResult> SetServiceStartModeAsync(string serviceName, ServiceStartModeOption startMode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));
    }

    private sealed class TestEnv(
        JsonArgusSettingsStore settingsStore,
        FileBackedOperationJournal journal,
        SettingsViewModel settings,
        MainWindowViewModel main,
        FakePrivilegeService privilege,
        FakeStartupService startup,
        FakeRestorePointService restore,
        ExitBox exitBox) : IDisposable
    {
        public JsonArgusSettingsStore SettingsStore { get; } = settingsStore;
        public FileBackedOperationJournal Journal { get; } = journal;
        public SettingsViewModel Settings { get; } = settings;
        public MainWindowViewModel Main { get; } = main;
        public FakePrivilegeService Privilege { get; } = privilege;
        public FakeStartupService Startup { get; } = startup;
        public FakeRestorePointService Restore { get; } = restore;
        public bool ExitRequested => exitBox.Requested;

        public void Dispose()
        {
            Main.Dispose();
            Journal.Dispose();
            SettingsStore.Dispose();
        }
    }
}
