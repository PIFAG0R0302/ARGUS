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

public sealed class ShellExpertModeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusShellExpertTests_{Guid.NewGuid():N}");

    public ShellExpertModeTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task MainWindowViewModel_IsExpertMode_SyncsBidirectionallyWithAppUiModeServiceAndPersists()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = CreateEnv(useUiMode: true);

        Assert.False(env.Main.IsExpertMode);
        Assert.False(env.UiMode!.IsExpertMode);

        var propertyChanges = new List<string?>();
        env.Main.PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName);

        // Toggle ON from MainWindowViewModel -> syncs to AppUiModeService, tabs, and disk
        env.Main.IsExpertMode = true;
        await env.UiMode.LastPersistenceTask;

        Assert.True(env.UiMode.IsExpertMode);
        Assert.True(env.Main.IsExpertMode);
        Assert.True(env.Devices.IsExpertMode);
        Assert.Contains(nameof(MainWindowViewModel.IsExpertMode), propertyChanges);

        var persistedOn = await env.SettingsStore.LoadAsync(ct);
        Assert.True(persistedOn.ExpertModeEnabled);

        // Toggle OFF via AppUiModeService -> syncs back to MainWindowViewModel on UI dispatcher
        await env.UiMode.SetExpertModeAsync(false);

        Assert.False(env.Main.IsExpertMode);
        Assert.False(env.Devices.IsExpertMode);

        var persistedOff = await env.SettingsStore.LoadAsync(ct);
        Assert.False(persistedOff.ExpertModeEnabled);
    }

    [Fact]
    public void MigrationNavigationItem_IsVisibleReflectsIsExpertMode_WhileKeepingAllPrimaryNavigationItems()
    {
        using var env = CreateEnv(useUiMode: true);

        Assert.Equal(10, env.Main.PrimaryNavigation.Count);
        var migrationNav = Assert.Single(env.Main.PrimaryNavigation, static item => item.SectionId == ArgusSectionId.Migration);
        Assert.Equal("CopyOS", migrationNav.BadgeText);
        Assert.False(migrationNav.IsVisible);

        var navPropertyChanges = new List<string?>();
        migrationNav.PropertyChanged += (_, e) => navPropertyChanges.Add(e.PropertyName);

        env.Main.IsExpertMode = true;
        Assert.True(migrationNav.IsVisible);
        Assert.Contains(nameof(NavigationItemViewModel.IsVisible), navPropertyChanges);

        env.Main.IsExpertMode = false;
        Assert.False(migrationNav.IsVisible);
        Assert.Equal(10, env.Main.PrimaryNavigation.Count);
    }

    [Fact]
    public void DisablingExpertMode_WhileOnMigrationSection_NavigatesBackToOverview()
    {
        using var env = CreateEnv(useUiMode: true);

        env.Main.IsExpertMode = true;
        env.Main.SelectSection(ArgusSectionId.Migration);
        Assert.Equal(ArgusSectionId.Migration, env.Main.ActiveSection.SectionId);

        env.Main.IsExpertMode = false;

        Assert.Equal(ArgusSectionId.Overview, env.Main.ActiveSection.SectionId);
        var selected = Assert.Single(env.Main.PrimaryNavigation.Concat(env.Main.FooterNavigation), static n => n.IsSelected);
        Assert.Equal(ArgusSectionId.Overview, selected.SectionId);
    }

    [Fact]
    public void DisablingExpertMode_WhileOnNonMigrationSection_KeepsActiveSection()
    {
        using var env = CreateEnv(useUiMode: true);

        env.Main.IsExpertMode = true;
        env.Main.SelectSection(ArgusSectionId.Devices);
        Assert.Equal(ArgusSectionId.Devices, env.Main.ActiveSection.SectionId);

        env.Main.IsExpertMode = false;

        Assert.Equal(ArgusSectionId.Devices, env.Main.ActiveSection.SectionId);
    }

    [Fact]
    public async Task MainWindowViewModel_WithoutUiModeService_FallsBackToSettingsStoreAndEventBus()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = CreateEnv(useUiMode: false);

        var migrationNav = Assert.Single(env.Main.PrimaryNavigation, static item => item.SectionId == ArgusSectionId.Migration);
        Assert.False(env.Main.IsExpertMode);
        Assert.False(migrationNav.IsVisible);

        env.Main.IsExpertMode = true;
        Assert.True(env.Main.IsExpertMode);
        Assert.True(migrationNav.IsVisible);

        // Allow fire-and-forget PersistSettingsAsync to complete
        await WaitUntilAsync(async () => (await env.SettingsStore.LoadAsync(ct)).ExpertModeEnabled);

        env.Main.SelectSection(ArgusSectionId.Migration);
        Assert.Equal(ArgusSectionId.Migration, env.Main.ActiveSection.SectionId);

        // Simulate external settings change turning expert mode OFF
        var disabledSettings = env.SettingsStore.Current with { ExpertModeEnabled = false };
        await env.SettingsStore.SaveAsync(disabledSettings, ct);
        env.EventBus.Publish(new SettingsChangedEvent(disabledSettings));

        Assert.False(env.Main.IsExpertMode);
        Assert.False(migrationNav.IsVisible);
        Assert.Equal(ArgusSectionId.Overview, env.Main.ActiveSection.SectionId);
    }

    [Fact]
    public void ShellAndViews_HaveGlobalTitleBarToggle_AndNoDuplicateInlineTogglesInOwnedViews()
    {
        string repoRoot = FindRepoRoot();

        string mainWindowXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "Argus.App", "MainWindow.axaml"));
        Assert.Contains("Classes=\"expert-toggle-bar\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsExpertMode}\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsVisible}\"", mainWindowXaml, StringComparison.Ordinal);

        string[] cleanedViews =
        [
            "CleanupView.axaml",
            "DevicesView.axaml",
            "NetworkView.axaml",
            "StartupAndServicesView.axaml"
        ];

        foreach (string viewName in cleanedViews)
        {
            string viewXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "Argus.App", "Views", viewName));
            Assert.DoesNotContain("expert-toggle-bar", viewXaml, StringComparison.Ordinal);
            Assert.Contains("IsExpertMode", viewXaml, StringComparison.Ordinal);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ARGUS.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ARGUS.sln from test output directory.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 40; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(await condition());
    }

    private TestEnv CreateEnv(bool useUiMode)
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        var settingsStore = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, $"settings_{Guid.NewGuid():N}.json"));
        var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "logs", $"journal_{Guid.NewGuid():N}.jsonl"));

        var privilege = new FakePrivilegeService();
        var startup = new FakeStartupService();
        var restore = new FakeRestorePointService();
        var uiMode = useUiMode ? new AppUiModeService(settingsStore, privilege, bus) : null;

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
        var monitoring = new MonitoringViewModel(settingsStore, telemetry, tierStatus, bus, uiMode: uiMode);
        var devices = new DevicesViewModel(autoLoadOnInit: false, uiMode: uiMode);
        var cleanup = new CleanupViewModel(settingsStore, autoLoadOnInit: false, uiMode: uiMode);
        var disks = new DisksViewModel(autoLoadOnInit: false, uiMode: uiMode);
        var migration = new MigrationViewModel(privilegeService: privilege, autoLoadOnInit: false, uiMode: uiMode);
        var journalVm = new JournalViewModel(journal, bus);
        var settings = new SettingsViewModel(settingsStore, journal, bus, startup, restore, uiMode: uiMode);
        var network = new NetworkViewModel(new FakeNetworkService(), uiMode: uiMode, autoLoadOnInit: false);
        var processes = new ProcessesViewModel(new FakeProcessMonitor(), uiMode: uiMode, autoLoadOnInit: false);
        var startupServices = new StartupAndServicesViewModel(new FakeStartupAndServices(), uiMode: uiMode, autoLoadOnInit: false);

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
            eventBus: bus,
            network: network,
            startupAndServices: startupServices,
            processes: processes,
            uiMode: uiMode);

        return new TestEnv(bus, settingsStore, journal, uiMode, devices, main);
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

    private sealed class FakePrivilegeService : IPrivilegeService
    {
        public bool IsAdministrator => false;
        public bool TryRelaunchElevated(string[]? arguments = null) => false;
    }

    private sealed class FakeStartupService : IStartupRegistrationService
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
        ArgusEventBus eventBus,
        JsonArgusSettingsStore settingsStore,
        FileBackedOperationJournal journal,
        AppUiModeService? uiMode,
        DevicesViewModel devices,
        MainWindowViewModel main) : IDisposable
    {
        public ArgusEventBus EventBus { get; } = eventBus;
        public JsonArgusSettingsStore SettingsStore { get; } = settingsStore;
        public AppUiModeService? UiMode { get; } = uiMode;
        public DevicesViewModel Devices { get; } = devices;
        public MainWindowViewModel Main { get; } = main;

        public void Dispose()
        {
            Main.Dispose();
            UiMode?.Dispose();
            journal.Dispose();
            SettingsStore.Dispose();
        }
    }
}
