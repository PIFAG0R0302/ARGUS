using System.Runtime.InteropServices;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    public const int UiScaleStepPercent = 10;

    private readonly Dictionary<ArgusSectionId, SectionViewModelBase> _sections;
    private readonly OverviewViewModel _overview;
    private readonly MonitoringViewModel _monitoring;
    private readonly NetworkViewModel? _network;
    private readonly DevicesViewModel _devices;
    private readonly CleanupViewModel _cleanup;
    private readonly DisksViewModel _disks;
    private readonly MigrationViewModel _migration;
    private readonly ProcessesViewModel? _processes;
    private readonly StartupAndServicesViewModel? _startupAndServices;
    private readonly IArgusSettingsStore _settingsStore;
    private readonly Argus.Core.Events.IArgusEventBus? _eventBus;
    private readonly IPrivilegeService _privilegeService;
    private readonly Action? _requestAppExit;
    private readonly IDisposable? _updateSubscription;
    private readonly IDisposable? _settingsSubscription;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiScaleFactor))]
    [NotifyPropertyChangedFor(nameof(UiScaleText))]
    private int _uiScalePercent = ArgusSettings.DefaultUiScalePercent;

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
        Action<Action>? uiDispatch = null,
        NetworkViewModel? network = null,
        StartupAndServicesViewModel? startupAndServices = null,
        ProcessesViewModel? processes = null)
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
        _network = network;
        _devices = devices;
        _cleanup = cleanup;
        _disks = disks;
        _migration = migration;
        _processes = processes;
        _startupAndServices = startupAndServices;
        _settingsStore = settingsStore;
        _eventBus = eventBus;
        _privilegeService = privilegeService;
        _requestAppExit = requestAppExit;
        _uiScalePercent = settingsStore.Current.Normalize().UiScalePercent;

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

        if (network is not null)
        {
            _sections[ArgusSectionId.Network] = network;
        }

        if (processes is not null)
        {
            _sections[ArgusSectionId.Processes] = processes;
        }

        if (startupAndServices is not null)
        {
            _sections[ArgusSectionId.StartupAndServices] = startupAndServices;
        }

        overview.AttachNavigator(SelectSection);
        monitoring.AttachNavigator(SelectSection);

        var monitoringItems = new List<NavigationItemViewModel>
        {
            new(ArgusSectionId.Overview, "Огляд", "IconOverview"),
            new(ArgusSectionId.Monitoring, "Моніторинг", "IconMonitoring")
        };
        if (network is not null)
        {
            monitoringItems.Add(new(ArgusSectionId.Network, "Мережа", "IconNetwork"));
        }

        monitoringItems.Add(new(ArgusSectionId.Devices, "Пристрої", "IconDevices"));

        var systemItems = new List<NavigationItemViewModel>();
        if (processes is not null)
        {
            systemItems.Add(new(ArgusSectionId.Processes, "Процеси", "IconProcesses"));
        }

        if (startupAndServices is not null)
        {
            systemItems.Add(new(ArgusSectionId.StartupAndServices, "Автозапуск і служби", "IconStartupServices"));
        }

        systemItems.Add(new(ArgusSectionId.Journal, "Журнал записів", "IconJournal"));

        NavigationGroups =
        [
            new NavigationGroupViewModel("monitoring", "МОНІТОРИНГ", monitoringItems),
            new NavigationGroupViewModel(
                "maintenance",
                "ОБСЛУГОВУВАННЯ",
                [
                    new(ArgusSectionId.Cleanup, "Очищення", "IconCleanup"),
                    new(ArgusSectionId.Disks, "Диски", "IconDisks"),
                    new(ArgusSectionId.Migration, "Міграція ОС", "IconMigration", "CopyOS")
                ]),
            new NavigationGroupViewModel("system", "СИСТЕМА", systemItems)
        ];

        PrimaryNavigation = NavigationGroups.SelectMany(static g => g.Items).ToArray();

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
        PrivilegeBadgeText = IsAdministrator ? "Адміністратор" : "Стандартний · UAC";
        PrivilegeTooltip = IsAdministrator
            ? "Програма працює з правами адміністратора — доступні System Restore, оптимізація дисків, керування службами та CopyOS."
            : "Безпечний стандартний режим: моніторинг, діагностика та аналіз працюють без прав адміністратора. Натисніть, щоб підвищити права (UAC) за потреби.";

        if (eventBus is not null)
        {
            var dispatch = uiDispatch ?? (static a => a());
            _updateSubscription = eventBus.Subscribe<UpdateAvailableEvent>(e => dispatch(() =>
            {
                HasUpdateAvailable = e.Update.IsUpdateAvailable;
                UpdateBadgeText = $"Доступне оновлення v{e.Update.LatestVersion}";
            }));
            _settingsSubscription = eventBus.Subscribe<SettingsChangedEvent>(e => dispatch(() =>
            {
                int normalizedScale = e.Settings.Normalize().UiScalePercent;
                if (UiScalePercent != normalizedScale)
                {
                    UiScalePercent = normalizedScale;
                }
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
    public double UiScaleFactor => UiScalePercent / 100.0;
    public string UiScaleText => $"{UiScalePercent}%";

    public IReadOnlyList<NavigationGroupViewModel> NavigationGroups { get; }
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
        _network?.SetUiActive(_isWindowVisible && activeId == ArgusSectionId.Network);
        _processes?.SetUiActive(_isWindowVisible && activeId == ArgusSectionId.Processes);

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
            case ArgusSectionId.Network:
                _network?.EnsureInitialized();
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
            case ArgusSectionId.Processes:
                _processes?.EnsureInitialized();
                break;
            case ArgusSectionId.StartupAndServices:
                _startupAndServices?.EnsureInitialized();
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
    public void ZoomIn() => ApplyZoom(UiScalePercent + UiScaleStepPercent);

    [RelayCommand]
    public void ZoomOut() => ApplyZoom(UiScalePercent - UiScaleStepPercent);

    [RelayCommand]
    public void ResetZoom() => ApplyZoom(ArgusSettings.DefaultUiScalePercent);

    private void ApplyZoom(int targetPercent)
    {
        int clamped = Math.Clamp(targetPercent, ArgusSettings.MinUiScalePercent, ArgusSettings.MaxUiScalePercent);
        if (clamped == UiScalePercent)
        {
            return;
        }

        UiScalePercent = clamped;
        var updated = (_settingsStore.Current with { UiScalePercent = clamped }).Normalize();
        _eventBus?.Publish(new SettingsChangedEvent(updated));
        _ = PersistUiScaleAsync(updated);
    }

    private async Task PersistUiScaleAsync(ArgusSettings updated)
    {
        try
        {
            await _settingsStore.SaveAsync(updated).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort persistence on interactive zoom hotkeys
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
        _settingsSubscription?.Dispose();
        foreach (var section in _sections.Values)
        {
            if (section is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
