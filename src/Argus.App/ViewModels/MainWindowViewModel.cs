using System.Runtime.InteropServices;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly Dictionary<ArgusSectionId, SectionViewModelBase> _sections;
    private readonly OverviewViewModel _overview;
    private readonly MonitoringViewModel _monitoring;
    private readonly DevicesViewModel _devices;
    private readonly CleanupViewModel _cleanup;
    private readonly DisksViewModel _disks;
    private readonly MigrationViewModel _migration;
    private readonly IPrivilegeService _privilegeService;
    private readonly Action? _requestAppExit;
    private readonly IDisposable? _updateSubscription;
    private bool _isWindowVisible = true;

    [ObservableProperty]
    private bool _hasUpdateAvailable;

    [ObservableProperty]
    private string _updateBadgeText = "Доступне оновлення";

    [ObservableProperty]
    private SectionViewModelBase _activeSection;

    [ObservableProperty]
    private string _activeSectionTitle = string.Empty;

    [ObservableProperty]
    private string _activeSectionSubtitle = string.Empty;

    [ObservableProperty]
    private bool _isMaximized;

    public MainWindowViewModel(
        OverviewViewModel overview,
        MonitoringViewModel monitoring,
        DevicesViewModel devices,
        CleanupViewModel cleanup,
        DisksViewModel disks,
        MigrationViewModel migration,
        JournalViewModel journal,
        SettingsViewModel settings,
        IArgusSettingsStore settingsStore,
        IPrivilegeService privilegeService,
        Action? requestAppExit = null,
        bool startVisible = true,
        Argus.Core.Events.IArgusEventBus? eventBus = null,
        Action<Action>? uiDispatch = null)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(monitoring);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(disks);
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(privilegeService);

        _isWindowVisible = startVisible;
        _overview = overview;
        _monitoring = monitoring;
        _devices = devices;
        _cleanup = cleanup;
        _disks = disks;
        _migration = migration;
        _privilegeService = privilegeService;
        _requestAppExit = requestAppExit;

        _sections = new Dictionary<ArgusSectionId, SectionViewModelBase>
        {
            [ArgusSectionId.Overview] = overview,
            [ArgusSectionId.Monitoring] = monitoring,
            [ArgusSectionId.Devices] = devices,
            [ArgusSectionId.Cleanup] = cleanup,
            [ArgusSectionId.Disks] = disks,
            [ArgusSectionId.Migration] = migration,
            [ArgusSectionId.Journal] = journal,
            [ArgusSectionId.Settings] = settings
        };

        overview.AttachNavigator(SelectSection);

        PrimaryNavigation =
        [
            new(ArgusSectionId.Overview, "Огляд", "IconOverview"),
            new(ArgusSectionId.Monitoring, "Моніторинг", "IconMonitoring"),
            new(ArgusSectionId.Devices, "Пристрої", "IconDevices"),
            new(ArgusSectionId.Cleanup, "Очищення", "IconCleanup"),
            new(ArgusSectionId.Disks, "Диски", "IconDisks"),
            new(ArgusSectionId.Migration, "Міграція ОС", "IconMigration", "CopyOS"),
            new(ArgusSectionId.Journal, "Журнал записів", "IconJournal")
        ];

        FooterNavigation =
        [
            new(ArgusSectionId.Settings, "Налаштування", "IconSettings")
        ];

        _activeSection = overview;
        SelectSection(ArgusSectionId.Overview);

        RuntimeCaption = RuntimeInformation.FrameworkDescription;
        EngineCaption = "Avalonia 12 + Skia (DirectX)";
        var asmVer = typeof(MainWindowViewModel).Assembly.GetName().Version;
        VersionBadge = asmVer is not null ? $"v{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}" : "v0.5.0";
        SecurityBadge = settingsStore.Current.EnablePawnIoExtendedSensors
            ? "HVCI-сумісно (PawnIO)"
            : "Defender-safe (без драйверів)";

        IsAdministrator = _privilegeService.IsAdministrator;
        PrivilegeBadgeText = IsAdministrator ? "Адміністратор" : "Підвищити права (UAC)";
        PrivilegeTooltip = IsAdministrator
            ? "Програма працює з правами адміністратора — доступні System Restore, оптимізація дисків і CopyOS."
            : "Натисніть, щоб перезапустити Argus від імені адміністратора для System Restore, TRIM/дефрагментації та CopyOS.";

        if (eventBus is not null)
        {
            var dispatch = uiDispatch ?? (static a => a());
            _updateSubscription = eventBus.Subscribe<UpdateAvailableEvent>(e => dispatch(() =>
            {
                HasUpdateAvailable = e.Update.IsUpdateAvailable;
                UpdateBadgeText = $"Доступне оновлення v{e.Update.LatestVersion}";
            }));
        }
    }

    public string BrandName => "ARGUS";
    public string VersionBadge { get; }
    public string RuntimeCaption { get; }
    public string EngineCaption { get; }
    public string SecurityBadge { get; }
    public bool IsAdministrator { get; }
    public string PrivilegeBadgeText { get; }
    public string PrivilegeTooltip { get; }

    public IReadOnlyList<NavigationItemViewModel> PrimaryNavigation { get; }
    public IReadOnlyList<NavigationItemViewModel> FooterNavigation { get; }

    [RelayCommand]
    public void SelectSection(ArgusSectionId sectionId)
    {
        if (!_sections.TryGetValue(sectionId, out var target))
        {
            return;
        }

        ActiveSection = target;
        ActiveSectionTitle = target.Title;
        ActiveSectionSubtitle = target.Subtitle;

        foreach (var item in PrimaryNavigation)
        {
            item.IsSelected = item.SectionId == sectionId;
        }

        foreach (var item in FooterNavigation)
        {
            item.IsSelected = item.SectionId == sectionId;
        }

        SyncUiActivationAndLazyInit();
    }

    public void SetWindowVisible(bool visible)
    {
        if (_isWindowVisible == visible)
        {
            return;
        }

        _isWindowVisible = visible;
        SyncUiActivationAndLazyInit();
    }

    private void SyncUiActivationAndLazyInit()
    {
        var activeId = ActiveSection.SectionId;
        _overview.SetUiActive(_isWindowVisible && activeId == ArgusSectionId.Overview);
        _monitoring.SetUiActive(_isWindowVisible && activeId == ArgusSectionId.Monitoring);

        if (!_isWindowVisible)
        {
            return;
        }

        switch (activeId)
        {
            case ArgusSectionId.Overview:
                _devices.EnsureInitialized();
                _cleanup.EnsureInitialized();
                _disks.EnsureInitialized();
                break;
            case ArgusSectionId.Devices:
                _devices.EnsureInitialized();
                break;
            case ArgusSectionId.Cleanup:
                _cleanup.EnsureInitialized();
                break;
            case ArgusSectionId.Disks:
                _disks.EnsureInitialized();
                break;
            case ArgusSectionId.Migration:
                _migration.EnsureInitialized();
                break;
        }
    }

    [RelayCommand]
    private void RequestElevation()
    {
        if (IsAdministrator)
        {
            return;
        }

        string sectionArg = ActiveSection.SectionId.ToString().ToLowerInvariant();
        if (_privilegeService.TryRelaunchElevated(["--section", sectionArg]))
        {
            _requestAppExit?.Invoke();
        }
    }

    [RelayCommand]
    private void OpenUpdateSettings()
    {
        SelectSection(ArgusSectionId.Settings);
    }

    public void Dispose()
    {
        _updateSubscription?.Dispose();
        foreach (var section in _sections.Values)
        {
            if (section is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
