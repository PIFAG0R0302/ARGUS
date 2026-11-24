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

public sealed class UxStartupAndScalingTests
{
    [Fact]
    public void ArgusSettings_Normalize_ClampsUiScalePercentAndDefaultsNonPositiveTo100()
    {
        Assert.Equal(100, new ArgusSettings().Normalize().UiScalePercent);
        Assert.Equal(100, (new ArgusSettings { UiScalePercent = 0 }).Normalize().UiScalePercent);
        Assert.Equal(100, (new ArgusSettings { UiScalePercent = -25 }).Normalize().UiScalePercent);
        Assert.Equal(80, (new ArgusSettings { UiScalePercent = 50 }).Normalize().UiScalePercent);
        Assert.Equal(150, (new ArgusSettings { UiScalePercent = 220 }).Normalize().UiScalePercent);
        Assert.Equal(125, (new ArgusSettings { UiScalePercent = 125 }).Normalize().UiScalePercent);
    }

    [Fact]
    public async Task StartupAndServicesViewModel_FiltersStartupBySourceAndStatePills()
    {
        var manager = new FakeStartupAndServicesManager();
        using var vm = new StartupAndServicesViewModel(manager, autoLoadOnInit: false);

        await vm.RefreshAsync();

        Assert.True(vm.IsStartupSourceAll);
        Assert.True(vm.IsStartupStateAll);
        Assert.Equal(4, vm.StartupEntries.Count);

        vm.SetStartupFilterCommand.Execute("Registry");
        Assert.True(vm.IsStartupSourceRegistry);
        Assert.False(vm.IsStartupSourceAll);
        Assert.Equal(2, vm.StartupEntries.Count);

        vm.SetStartupStateFilterCommand.Execute("Вимкнено");
        Assert.True(vm.IsStartupStateDisabled);
        var disabledRegistry = Assert.Single(vm.StartupEntries);
        Assert.Equal("RealtekAudio", disabledRegistry.Name);

        vm.SetStartupFilterCommand.Execute("Startup");
        vm.SetStartupStateFilterCommand.Execute("Усі стани");
        Assert.True(vm.IsStartupSourceStartup);
        var folderItem = Assert.Single(vm.StartupEntries);
        Assert.Equal("NotesSync", folderItem.Name);

        vm.SetStartupFilterCommand.Execute("Task Scheduler");
        Assert.True(vm.IsStartupSourceTaskScheduler);
        var taskItem = Assert.Single(vm.StartupEntries);
        Assert.Equal("BackupTask", taskItem.Name);
    }

    [Fact]
    public async Task StartupAndServicesViewModel_FiltersServicesByStateAndStartModePills()
    {
        var manager = new FakeStartupAndServicesManager();
        using var vm = new StartupAndServicesViewModel(manager, autoLoadOnInit: false);

        await vm.RefreshAsync();
        vm.SelectSubTabCommand.Execute("1");

        Assert.True(vm.IsServicesTab);
        Assert.True(vm.IsServiceStateAll);
        Assert.True(vm.IsServiceStartModeAll);
        Assert.Equal(3, vm.Services.Count);

        vm.SetServiceFilterCommand.Execute("Працює");
        Assert.True(vm.IsServiceStateRunning);
        var running = Assert.Single(vm.Services);
        Assert.Equal("RpcSs", running.ServiceName);

        vm.SetServiceFilterCommand.Execute("Зупинено");
        Assert.True(vm.IsServiceStateStopped);
        Assert.Equal(2, vm.Services.Count);

        vm.SetServiceStartModeFilterCommand.Execute("Вимкнено");
        Assert.True(vm.IsServiceStartModeDisabled);
        var disabledSvc = Assert.Single(vm.Services);
        Assert.Equal("FaxSvc", disabledSvc.ServiceName);

        vm.SetServiceStartModeFilterCommand.Execute("Вручну");
        Assert.True(vm.IsServiceStartModeManual);
        var manualSvc = Assert.Single(vm.Services);
        Assert.Equal("Spooler", manualSvc.ServiceName);
    }

    [Fact]
    public async Task StartupAndServicesViewModel_SortsColumnsAndPreservesRowInstancesOnRefresh()
    {
        var manager = new FakeStartupAndServicesManager();
        using var vm = new StartupAndServicesViewModel(manager, autoLoadOnInit: false);

        await vm.RefreshAsync();

        // Default startup sort: NAME ascending
        Assert.True(vm.IsStartupSortByName);
        Assert.EndsWith("▲", vm.StartupSortNameHeader, StringComparison.Ordinal);
        Assert.Equal(["ARGUS", "BackupTask", "NotesSync", "RealtekAudio"], vm.StartupEntries.Select(static r => r.Name).ToArray());

        var argusRowBeforeRefresh = vm.StartupEntries[0];

        // Toggle NAME descending
        vm.SetStartupSortCommand.Execute("NAME");
        Assert.EndsWith("▼", vm.StartupSortNameHeader, StringComparison.Ordinal);
        Assert.Equal(["RealtekAudio", "NotesSync", "BackupTask", "ARGUS"], vm.StartupEntries.Select(static r => r.Name).ToArray());

        // Sort by STATE (defaults descending: enabled first)
        vm.SetStartupSortCommand.Execute("STATE");
        Assert.True(vm.IsStartupSortByState);
        Assert.EndsWith("▼", vm.StartupSortStateHeader, StringComparison.Ordinal);
        Assert.True(vm.StartupEntries[0].IsEnabled);
        Assert.False(vm.StartupEntries[^1].IsEnabled);

        // Mutate backend state and refresh -> SyncByKey updates row in-place without recreating
        manager.ArgusEnabled = false;
        await vm.RefreshAsync();
        var argusRowAfterRefresh = vm.StartupEntries.Single(static r => r.Model.Id == "HKCU:Argus");
        Assert.Same(argusRowBeforeRefresh, argusRowAfterRefresh);
        Assert.False(argusRowAfterRefresh.IsEnabled);

        // Services sorting: STARTMODE ascending (Automatic -> Manual -> Disabled)
        vm.SetServiceSortCommand.Execute("STARTMODE");
        Assert.True(vm.IsServiceSortByStartMode);
        Assert.EndsWith("▲", vm.ServiceSortStartModeHeader, StringComparison.Ordinal);
        Assert.Equal(["RpcSs", "Spooler", "FaxSvc"], vm.Services.Select(static s => s.ServiceName).ToArray());

        // Toggle STARTMODE descending
        vm.SetServiceSortCommand.Execute("STARTMODE");
        Assert.EndsWith("▼", vm.ServiceSortStartModeHeader, StringComparison.Ordinal);
        Assert.Equal(["FaxSvc", "Spooler", "RpcSs"], vm.Services.Select(static s => s.ServiceName).ToArray());
    }

    [Fact]
    public async Task UiScaling_ZoomCommandsAndSettingsViewModelStaySynchronizedAndClamped()
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        var settingsStore = new FakeSettingsStore(new ArgusSettings { UiScalePercent = 100 });
        var journal = new FakeOperationJournal();
        var privilege = new FakePrivilegeService();
        var startup = new FakeStartupRegistration();
        var restore = new FakeRestorePointService();
        var telemetry = new FakeTelemetryService();
        var tierStatus = new PawnIoSensorProvider(settingsStore, () => false);

        IArgusModule[] modules =
        [
            new CoreModule(),
            new TelemetryModule(),
            new DevicesModule(),
            new MaintenanceModule(),
            new MigrationModule()
        ];

        var overview = new OverviewViewModel(modules, settingsStore, journal, bus, telemetry, tierStatus);
        var monitoring = new MonitoringViewModel(settingsStore, telemetry, tierStatus, bus);
        var devices = new DevicesViewModel();
        var cleanup = new CleanupViewModel(settingsStore);
        var disks = new DisksViewModel();
        var migration = new MigrationViewModel();
        var journalVm = new JournalViewModel(journal, bus);
        var settingsVm = new SettingsViewModel(settingsStore, journal, bus, startup, restore);
        var mainVm = new MainWindowViewModel(
            overview,
            monitoring,
            devices,
            cleanup,
            disks,
            migration,
            journalVm,
            settingsVm,
            settingsStore,
            privilege,
            eventBus: bus);

        using (mainVm)
        {
            Assert.Equal(100, mainVm.UiScalePercent);
            Assert.Equal(1.0, mainVm.UiScaleFactor, precision: 4);
            Assert.Equal("100%", mainVm.UiScaleText);
            Assert.True(settingsVm.IsUiScale100);

            // ZoomIn via MainWindowViewModel -> updates store & syncs SettingsViewModel
            mainVm.ZoomIn();
            Assert.Equal(110, mainVm.UiScalePercent);
            Assert.Equal(1.1, mainVm.UiScaleFactor, precision: 4);
            Assert.Equal("110%", mainVm.UiScaleText);
            Assert.Equal(110, settingsStore.Current.UiScalePercent);
            Assert.Equal(110, settingsVm.UiScalePercent);
            Assert.True(settingsVm.IsUiScale110);

            // Clamp at 150%
            for (int i = 0; i < 10; i++)
            {
                mainVm.ZoomIn();
            }

            Assert.Equal(150, mainVm.UiScalePercent);
            Assert.Equal(1.5, mainVm.UiScaleFactor, precision: 4);
            Assert.True(settingsVm.IsUiScale150);

            // ResetZoom -> 100%
            mainVm.ResetZoom();
            Assert.Equal(100, mainVm.UiScalePercent);
            Assert.Equal(100, settingsStore.Current.UiScalePercent);
            Assert.True(settingsVm.IsUiScale100);

            // Clamp at 80%
            for (int i = 0; i < 10; i++)
            {
                mainVm.ZoomOut();
            }

            Assert.Equal(80, mainVm.UiScalePercent);
            Assert.Equal(0.8, mainVm.UiScaleFactor, precision: 4);
            Assert.True(settingsVm.IsUiScale80);

            // Preset in SettingsViewModel -> syncs MainWindowViewModel & persists on Save
            settingsVm.SetUiScalePresetCommand.Execute("125");
            Assert.Equal(125, settingsVm.UiScalePercent);
            Assert.Equal("125%", settingsVm.UiScaleText);
            Assert.True(settingsVm.IsUiScale125);
            Assert.Equal(125, mainVm.UiScalePercent);
            Assert.Equal(1.25, mainVm.UiScaleFactor, precision: 4);

            await settingsVm.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Equal(125, settingsStore.Current.UiScalePercent);

            // Reset defaults restores 100%
            await settingsVm.ResetDefaultsCommand.ExecuteAsync(null);
            Assert.Equal(100, settingsStore.Current.UiScalePercent);
            Assert.Equal(100, mainVm.UiScalePercent);
            Assert.True(settingsVm.IsUiScale100);
        }
    }

    private sealed class FakeSettingsStore(ArgusSettings? initial = null) : IArgusSettingsStore
    {
        public string SettingsFilePath => "memory://settings.json";
        public ArgusSettings Current { get; private set; } = (initial ?? new ArgusSettings()).Normalize();
        public Task<ArgusSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task SaveAsync(ArgusSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings.Normalize();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOperationJournal : IOperationJournal
    {
        public Task RecordAsync(
            string moduleId,
            OperationSeverity severity,
            string action,
            string details,
            bool succeeded = true,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<OperationJournalEntry> GetRecent(int maxCount = 200) => [];
    }

    private sealed class FakePrivilegeService : IPrivilegeService
    {
        public bool IsAdministrator => true;
        public bool TryRelaunchElevated(string[]? arguments = null) => false;
    }

    private sealed class FakeStartupRegistration : IStartupRegistrationService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled, string executablePath) { }
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

    private sealed class FakeStartupAndServicesManager : IStartupAndServicesManager
    {
        public bool ArgusEnabled { get; set; } = true;

        public Task<IReadOnlyList<StartupEntryItem>> GetStartupEntriesAsync(
            bool includeSystemTasks = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StartupEntryItem>>(
            [
                new("HKCU:Argus", "ARGUS", "C:\\Program Files\\ARGUS\\Argus.exe", StartupSourceKind.RegistryCurrentUserRun, @"HKCU\...\Run", ArgusEnabled, false, false, "Поточний користувач"),
                new("HKLM:Audio", "RealtekAudio", "C:\\Program Files\\Realtek\\audio.exe", StartupSourceKind.RegistryLocalMachineRun, @"HKLM\...\Run", false, true, false, "Усі користувачі"),
                new("Folder:Notes", "NotesSync", "C:\\Apps\\notes.lnk", StartupSourceKind.StartupFolderUser, @"Startup\notes.lnk", true, false, false, "Тека автозапуску"),
                new("Task:\\Backup", "BackupTask", "C:\\Tools\\backup.exe", StartupSourceKind.TaskScheduler, @"\Backup", true, true, false, "User")
            ]);

        public Task<SystemActionResult> SetStartupEntryEnabledAsync(StartupEntryItem entry, bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));

        public Task<IReadOnlyList<ManagedServiceItem>> GetManagedServicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedServiceItem>>(
            [
                new("RpcSs", "Remote Procedure Call (RPC)", "Працює", ServiceStartModeOption.Automatic, "Автоматично", false, true, "C:\\Windows\\system32\\svchost.exe", "Core RPC"),
                new("Spooler", "Print Spooler", "Зупинено", ServiceStartModeOption.Manual, "Вручну", false, false, "C:\\Windows\\system32\\spoolsv.exe", "Printing"),
                new("FaxSvc", "Fax Service", "Зупинено", ServiceStartModeOption.Disabled, "Вимкнено", false, false, "C:\\Windows\\system32\\fxssvc.exe", "Fax")
            ]);

        public Task<SystemActionResult> ControlServiceAsync(string serviceName, ServiceControlAction action, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));

        public Task<SystemActionResult> SetServiceStartModeAsync(string serviceName, ServiceStartModeOption startMode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));
    }
}
