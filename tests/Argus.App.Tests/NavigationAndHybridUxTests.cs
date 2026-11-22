using Argus.App.Services;
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

public sealed class NavigationAndHybridUxTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusNavUxTests_{Guid.NewGuid():N}");

    public NavigationAndHybridUxTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void MainWindowViewModel_ExposesExpectedNavigationGroupsAndFlattenedPrimaryOrder()
    {
        using var env = CreateEnv(isAdmin: false);
        var main = env.Main;

        Assert.Equal(3, main.NavigationGroups.Count);

        Assert.Equal("monitoring", main.NavigationGroups[0].Id);
        Assert.Equal("МОНІТОРИНГ", main.NavigationGroups[0].Title);
        Assert.Equal(
            [ArgusSectionId.Overview, ArgusSectionId.Monitoring, ArgusSectionId.Devices],
            main.NavigationGroups[0].Items.Select(static i => i.SectionId).ToArray());

        Assert.Equal("maintenance", main.NavigationGroups[1].Id);
        Assert.Equal("ОБСЛУГОВУВАННЯ", main.NavigationGroups[1].Title);
        Assert.Equal(
            [ArgusSectionId.Cleanup, ArgusSectionId.Disks, ArgusSectionId.Migration],
            main.NavigationGroups[1].Items.Select(static i => i.SectionId).ToArray());
        Assert.Equal("CopyOS", main.NavigationGroups[1].Items[2].BadgeText);

        Assert.Equal("system", main.NavigationGroups[2].Id);
        Assert.Equal("СИСТЕМА", main.NavigationGroups[2].Title);
        Assert.Equal(
            [ArgusSectionId.Journal],
            main.NavigationGroups[2].Items.Select(static i => i.SectionId).ToArray());

        // Flattened backward-compatible PrimaryNavigation matches visual group order
        var flattenedFromGroups = main.NavigationGroups.SelectMany(static g => g.Items).Select(static i => i.SectionId).ToArray();
        Assert.Equal(flattenedFromGroups, main.PrimaryNavigation.Select(static i => i.SectionId).ToArray());
        Assert.Equal(7, main.PrimaryNavigation.Count);
        Assert.Single(main.FooterNavigation);
        Assert.Equal(ArgusSectionId.Settings, main.FooterNavigation[0].SectionId);

        Assert.Equal("Стандартний · UAC", main.PrivilegeBadgeText);
    }

    [Fact]
    public async Task ExpertMode_DefaultsFalse_SyncsAcrossTabs_Persists_AndLoadsInspectorLazily()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = CreateEnv(isAdmin: false);

        // Basic mode by default; inspector must stay completely idle
        Assert.False(env.UiMode.IsExpertMode);
        Assert.False(env.Monitoring.IsExpertMode);
        Assert.False(env.Devices.IsExpertMode);
        Assert.False(env.Cleanup.IsExpertMode);
        Assert.False(env.Disks.IsExpertMode);
        Assert.False(env.Settings.ExpertModeEnabled);
        Assert.Equal(0, env.Inspector.ProcessCalls);
        Assert.Equal(0, env.Inspector.ServiceCalls);
        Assert.Equal(0, env.Inspector.PortCalls);

        // Activate Monitoring tab & toggle Expert Mode ON
        env.Main.SelectSection(ArgusSectionId.Monitoring);
        env.Monitoring.IsExpertMode = true;
        await env.UiMode.LastPersistenceTask;

        // Synced across every tab
        Assert.True(env.UiMode.IsExpertMode);
        Assert.True(env.Monitoring.IsExpertMode);
        Assert.True(env.Devices.IsExpertMode);
        Assert.True(env.Cleanup.IsExpertMode);
        Assert.True(env.Disks.IsExpertMode);
        Assert.True(env.Settings.ExpertModeEnabled);

        // Persisted to settings.json
        var persisted = await env.SettingsStore.LoadAsync(ct);
        Assert.True(persisted.ExpertModeEnabled);

        // Inspector loaded lazily once Expert Mode turned ON on active Monitoring tab
        await env.Monitoring.RefreshInspectorAsync();
        Assert.True(env.Inspector.ProcessCalls >= 1);
        Assert.NotEmpty(env.Monitoring.FilteredProcesses);

        // Toggle OFF from Devices tab -> syncs back everywhere & persists
        env.Devices.IsExpertMode = false;
        await env.UiMode.LastPersistenceTask;

        Assert.False(env.Monitoring.IsExpertMode);
        Assert.False(env.Cleanup.IsExpertMode);
        Assert.False(env.Disks.IsExpertMode);
        Assert.False(env.Settings.ExpertModeEnabled);

        var reloaded = await env.SettingsStore.LoadAsync(ct);
        Assert.False(reloaded.ExpertModeEnabled);
    }

    [Fact]
    public void ContextualUac_NeedsElevationAndSectionScopedRelaunchWorkPerTab()
    {
        using var standard = CreateEnv(isAdmin: false);

        Assert.True(standard.Disks.NeedsElevation);
        Assert.True(standard.Migration.NeedsElevation);
        Assert.True(standard.Settings.NeedsElevation);

        // Cleanup NeedsElevation depends on selected UAC rules or System Restore toggle
        standard.Cleanup.CreateRestorePointBeforeCleanup = false;
        foreach (var rule in standard.Cleanup.Rules)
        {
            rule.IsSelected = !rule.RequiresElevation;
        }

        Assert.False(standard.Cleanup.NeedsElevation);

        standard.Cleanup.CreateRestorePointBeforeCleanup = true;
        Assert.True(standard.Cleanup.NeedsElevation);

        standard.Cleanup.RequestElevationCommand.Execute(null);
        Assert.Equal(1, standard.Privilege.RelaunchCalls);
        Assert.Equal(["--section", "cleanup"], standard.Privilege.LastArgs ?? []);
        Assert.True(standard.ExitRequested);

        standard.Disks.RequestElevationCommand.Execute(null);
        Assert.Equal(["--section", "disks"], standard.Privilege.LastArgs ?? []);

        standard.Migration.RequestElevationCommand.Execute(null);
        Assert.Equal(["--section", "migration"], standard.Privilege.LastArgs ?? []);

        standard.Settings.RequestElevationCommand.Execute(null);
        Assert.Equal(["--section", "settings"], standard.Privilege.LastArgs ?? []);

        // Monitoring Services sub-tab shows contextual UAC callout when non-admin
        standard.Monitoring.SelectServicesTabCommand.Execute(null);
        Assert.True(standard.Monitoring.ShowContextualInspectorUac);
        standard.Monitoring.RequestElevationCommand.Execute(null);
        Assert.Equal(["--section", "monitoring"], standard.Privilege.LastArgs ?? []);

        // Elevated admin session hides contextual callouts and never relaunches
        using var admin = CreateEnv(isAdmin: true);
        Assert.False(admin.Disks.NeedsElevation);
        Assert.False(admin.Migration.NeedsElevation);
        Assert.False(admin.Settings.NeedsElevation);
        Assert.False(admin.Cleanup.NeedsElevation);
        admin.Disks.RequestElevationCommand.Execute(null);
        Assert.Equal(0, admin.Privilege.RelaunchCalls);
    }

    [Fact]
    public async Task SettingsViewModel_PersistsTrayDiskAlertsAndExpertModeSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = CreateEnv(isAdmin: false);
        var vm = env.Settings;

        // Verify safe defaults
        Assert.False(vm.EnableSystemTray);
        Assert.False(vm.ExpertModeEnabled);
        Assert.Equal(10m, vm.DiskFreeSpaceAlertPercent);
        Assert.True(vm.EnableSmartDegradationAlert);

        vm.EnableSystemTray = true;
        vm.MinimizeToTrayOnClose = true;
        vm.DiskFreeSpaceAlertPercent = 15m;
        vm.EnableSmartDegradationAlert = false;
        vm.ExpertModeEnabled = true;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        var persisted = await env.SettingsStore.LoadAsync(ct);
        Assert.True(persisted.EnableSystemTray);
        Assert.True(persisted.MinimizeToTrayOnClose);
        Assert.Equal(15.0, persisted.DiskFreeSpaceAlertPercent);
        Assert.False(persisted.EnableSmartDegradationAlert);
        Assert.True(persisted.ExpertModeEnabled);

        // Reset defaults restores tray OFF, expert OFF, disk threshold 10%, SMART ON
        await vm.ResetDefaultsCommand.ExecuteAsync(null);

        var reset = await env.SettingsStore.LoadAsync(ct);
        Assert.False(reset.EnableSystemTray);
        Assert.False(reset.ExpertModeEnabled);
        Assert.Equal(10.0, reset.DiskFreeSpaceAlertPercent);
        Assert.True(reset.EnableSmartDegradationAlert);
        Assert.False(env.UiMode.IsExpertMode);
    }

    private TestEnv CreateEnv(bool isAdmin)
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        var settingsStore = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, $"settings_{Guid.NewGuid():N}.json"));
        var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "logs", $"journal_{Guid.NewGuid():N}.jsonl"));

        var privilege = new FakePrivilegeService(isAdmin);
        var startup = new FakeStartupService();
        var restore = new FakeRestorePointService();
        var inspector = new FakeSystemInspectorService();
        var exitBox = new ExitBox();

        var uiMode = new AppUiModeService(
            settingsStore,
            privilege,
            bus,
            requestAppExit: () => exitBox.Requested = true);

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
        var monitoring = new MonitoringViewModel(settingsStore, telemetry, tierStatus, bus, uiMode: uiMode, systemInspector: inspector);
        var devices = new DevicesViewModel(autoLoadOnInit: false, uiMode: uiMode);
        var cleanup = new CleanupViewModel(settingsStore, autoLoadOnInit: false, uiMode: uiMode);
        var disks = new DisksViewModel(autoLoadOnInit: false, uiMode: uiMode);
        var migration = new MigrationViewModel(privilegeService: privilege, autoLoadOnInit: false, uiMode: uiMode);
        var journalVm = new JournalViewModel(journal, bus);
        var settings = new SettingsViewModel(settingsStore, journal, bus, startup, restore, uiMode: uiMode);

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
            requestAppExit: () => exitBox.Requested = true);

        return new TestEnv(
            settingsStore,
            journal,
            uiMode,
            monitoring,
            devices,
            cleanup,
            disks,
            migration,
            settings,
            main,
            privilege,
            inspector,
            exitBox);
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
        public Task<RestorePointResult> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RestorePointResult(true, description, "OK"));
    }

    private sealed class FakeTelemetryService : ITelemetryService
    {
        public bool IsRunning => false;
        public TelemetrySnapshot? Latest => null;
        public IReadOnlyList<TelemetrySnapshot> GetHistory(int maxSamples = 180) => [];
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSystemInspectorService : ISystemInspectorService
    {
        public int ProcessCalls { get; private set; }
        public int ServiceCalls { get; private set; }
        public int PortCalls { get; private set; }

        public Task<IReadOnlyList<ProcessInspectItem>> GetProcessesAsync(CancellationToken cancellationToken = default)
        {
            ProcessCalls++;
            IReadOnlyList<ProcessInspectItem> items =
            [
                new ProcessInspectItem(1234, "argus-sample", 64L * 1024 * 1024, 12, @"C:\Tools\sample.exe", false)
            ];
            return Task.FromResult(items);
        }

        public Task<IReadOnlyList<WindowsServiceInspectItem>> GetServicesAsync(CancellationToken cancellationToken = default)
        {
            ServiceCalls++;
            IReadOnlyList<WindowsServiceInspectItem> items =
            [
                new WindowsServiceInspectItem("Spooler", "Диспетчер друку", "Працює", "Автоматично", true)
            ];
            return Task.FromResult(items);
        }

        public Task<IReadOnlyList<LocalPortInspectItem>> GetLocalPortsAsync(CancellationToken cancellationToken = default)
        {
            PortCalls++;
            IReadOnlyList<LocalPortInspectItem> items =
            [
                new LocalPortInspectItem("TCP", "127.0.0.1", 5000, "LISTEN", 1234, "argus-sample")
            ];
            return Task.FromResult(items);
        }

        public Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(false, RequiresElevation: true, "Потрібні права адміністратора (UAC)."));

        public Task<SystemActionResult> ControlServiceAsync(string serviceName, ServiceControlAction action, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(false, RequiresElevation: true, "Потрібні права адміністратора (UAC)."));

        public bool OpenProcessFolder(string? executablePath) => !string.IsNullOrWhiteSpace(executablePath);
    }

    private sealed class TestEnv(
        JsonArgusSettingsStore settingsStore,
        FileBackedOperationJournal journal,
        AppUiModeService uiMode,
        MonitoringViewModel monitoring,
        DevicesViewModel devices,
        CleanupViewModel cleanup,
        DisksViewModel disks,
        MigrationViewModel migration,
        SettingsViewModel settings,
        MainWindowViewModel main,
        FakePrivilegeService privilege,
        FakeSystemInspectorService inspector,
        ExitBox exitBox) : IDisposable
    {
        public JsonArgusSettingsStore SettingsStore { get; } = settingsStore;
        public AppUiModeService UiMode { get; } = uiMode;
        public MonitoringViewModel Monitoring { get; } = monitoring;
        public DevicesViewModel Devices { get; } = devices;
        public CleanupViewModel Cleanup { get; } = cleanup;
        public DisksViewModel Disks { get; } = disks;
        public MigrationViewModel Migration { get; } = migration;
        public SettingsViewModel Settings { get; } = settings;
        public MainWindowViewModel Main { get; } = main;
        public FakePrivilegeService Privilege { get; } = privilege;
        public FakeSystemInspectorService Inspector { get; } = inspector;
        public bool ExitRequested => exitBox.Requested;

        public void Dispose()
        {
            Main.Dispose();
            UiMode.Dispose();
            journal.Dispose();
            SettingsStore.Dispose();
        }
    }
}
