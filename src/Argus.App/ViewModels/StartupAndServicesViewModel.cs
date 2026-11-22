using System.Collections.ObjectModel;
using System.Windows.Input;
using Argus.App.Services;
using Argus.Core.Contracts.System;
using Argus.Core.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed partial class StartupEntryRowViewModel : ObservableObject
{
    private readonly Func<StartupEntryItem, bool, Task> _onToggle;

    public StartupEntryRowViewModel(
        StartupEntryItem model,
        Func<StartupEntryItem, bool, Task> onToggle)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _onToggle = onToggle ?? throw new ArgumentNullException(nameof(onToggle));

        Name = model.Name;
        CommandText = string.IsNullOrWhiteSpace(model.Command) ? "—" : model.Command;
        LocationPath = model.LocationPath;
        PublisherOrAuthor = model.PublisherOrAuthor;
        RequiresElevation = model.RequiresElevation;
        IsSystemOrMicrosoft = model.IsSystemOrMicrosoft;
        SourceBadge = model.Source switch
        {
            StartupSourceKind.RegistryCurrentUserRun => "Реєстр HKCU",
            StartupSourceKind.RegistryLocalMachineRun => "Реєстр HKLM",
            StartupSourceKind.RegistryLocalMachineWow6432Run => "Реєстр WOW64",
            StartupSourceKind.StartupFolderUser => "Тека Startup",
            StartupSourceKind.StartupFolderCommon => "Спільна Startup",
            StartupSourceKind.TaskScheduler => "Планувальник",
            _ => "Автозапуск"
        };

        _isEnabled = model.IsEnabled;
        ToggleCommand = new AsyncRelayCommand(() => _onToggle(Model, !IsEnabled));
    }

    public StartupEntryItem Model { get; }
    public string Name { get; }
    public string CommandText { get; }
    public string LocationPath { get; }
    public string PublisherOrAuthor { get; }
    public string SourceBadge { get; }
    public bool RequiresElevation { get; }
    public bool IsSystemOrMicrosoft { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(ToggleButtonLabel))]
    private bool _isEnabled;

    public string StateText => IsEnabled ? "Увімкнено" : "Вимкнено";
    public string ToggleButtonLabel => IsEnabled ? "Вимкнути" : "Увімкнути";

    public ICommand ToggleCommand { get; }
}

public sealed partial class ManagedServiceRowViewModel : ObservableObject
{
    private static readonly string[] StartModeChoices = ["Автоматично", "Вручну", "Вимкнено"];

    private readonly Func<string, ServiceControlAction, Task> _onControl;
    private readonly Func<string, ServiceStartModeOption, Task> _onChangeStartMode;
    private bool _suppressModeCallback;

    public ManagedServiceRowViewModel(
        ManagedServiceItem model,
        Func<string, ServiceControlAction, Task> onControl,
        Func<string, ServiceStartModeOption, Task> onChangeStartMode)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _onControl = onControl ?? throw new ArgumentNullException(nameof(onControl));
        _onChangeStartMode = onChangeStartMode ?? throw new ArgumentNullException(nameof(onChangeStartMode));

        ServiceName = model.ServiceName;
        DisplayName = model.DisplayName;
        Status = model.Status;
        StartModeLabel = model.StartModeLabel;
        IsCriticalProtected = model.IsCriticalProtected;
        BinaryPath = string.IsNullOrWhiteSpace(model.BinaryPath) ? "—" : model.BinaryPath;
        Description = string.IsNullOrWhiteSpace(model.Description) ? string.Empty : model.Description;
        HasDescription = !string.IsNullOrWhiteSpace(Description);

        IsRunning = string.Equals(model.Status, "Працює", StringComparison.Ordinal);
        CanStart = !IsRunning;
        CanStop = model.CanStop && !IsCriticalProtected;
        CanRestart = IsRunning && !IsCriticalProtected;
        CanChangeStartMode = !IsCriticalProtected;

        _selectedStartModeIndex = model.StartMode switch
        {
            ServiceStartModeOption.Automatic => 0,
            ServiceStartModeOption.Manual => 1,
            ServiceStartModeOption.Disabled => 2,
            _ => 1
        };

        StartCommand = new AsyncRelayCommand(() => _onControl(ServiceName, ServiceControlAction.Start), () => CanStart);
        StopCommand = new AsyncRelayCommand(() => _onControl(ServiceName, ServiceControlAction.Stop), () => CanStop);
        RestartCommand = new AsyncRelayCommand(() => _onControl(ServiceName, ServiceControlAction.Restart), () => CanRestart);
    }

    public ManagedServiceItem Model { get; }
    public string ServiceName { get; }
    public string DisplayName { get; }
    public string Status { get; }
    public string StartModeLabel { get; }
    public bool IsCriticalProtected { get; }
    public bool IsRunning { get; }
    public bool CanStart { get; }
    public bool CanStop { get; }
    public bool CanRestart { get; }
    public bool CanChangeStartMode { get; }
    public string BinaryPath { get; }
    public string Description { get; }
    public bool HasDescription { get; }
    public IReadOnlyList<string> StartModeOptions => StartModeChoices;

    [ObservableProperty]
    private int _selectedStartModeIndex;

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }

    partial void OnSelectedStartModeIndexChanged(int value)
    {
        if (_suppressModeCallback || IsCriticalProtected)
        {
            return;
        }

        ServiceStartModeOption targetMode = value switch
        {
            0 => ServiceStartModeOption.Automatic,
            1 => ServiceStartModeOption.Manual,
            2 => ServiceStartModeOption.Disabled,
            _ => ServiceStartModeOption.Manual
        };

        if (targetMode == Model.StartMode)
        {
            return;
        }

        _ = _onChangeStartMode(ServiceName, targetMode);
    }

    public void RevertStartModeSelection()
    {
        _suppressModeCallback = true;
        try
        {
            SelectedStartModeIndex = Model.StartMode switch
            {
                ServiceStartModeOption.Automatic => 0,
                ServiceStartModeOption.Manual => 1,
                ServiceStartModeOption.Disabled => 2,
                _ => 1
            };
        }
        finally
        {
            _suppressModeCallback = false;
        }
    }
}

public sealed partial class StartupAndServicesViewModel : SectionViewModelBase, IDisposable
{
    private readonly IStartupAndServicesManager _manager;
    private readonly Action<Action> _uiDispatch;
    private IReadOnlyList<StartupEntryItem> _allStartupEntries = [];
    private IReadOnlyList<ManagedServiceItem> _allServices = [];
    private bool _initialized;

    public StartupAndServicesViewModel(
        IStartupAndServicesManager manager,
        Action<Action>? uiDispatch = null,
        AppUiModeService? uiMode = null,
        bool autoLoadOnInit = false)
        : base(
            ArgusSectionId.StartupAndServices,
            "Автозапуск та служби Windows",
            "Точки автозапуску (Registry, Startup, Task Scheduler) і керування службами із жорстким захистом критичних компонентів ОС",
            uiMode,
            uiDispatch)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _uiDispatch = uiDispatch ?? (static action => action());

        StartupEntries = [];
        Services = [];

        if (autoLoadOnInit)
        {
            EnsureInitialized();
        }
    }

    public ObservableCollection<StartupEntryRowViewModel> StartupEntries { get; }
    public ObservableCollection<ManagedServiceRowViewModel> Services { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartupTab))]
    [NotifyPropertyChangedFor(nameof(IsServicesTab))]
    private int _activeSubTab;

    public bool IsStartupTab => ActiveSubTab == 0;
    public bool IsServicesTab => ActiveSubTab == 1;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _startupSourceFilter = "Усі";

    [ObservableProperty]
    private string _serviceStateFilter = "Усі";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _summaryText = "Натисніть «Оновити» або відкрийте вкладку для сканування автозапуску й служб.";

    [ObservableProperty]
    private string _actionFeedback = string.Empty;

    [ObservableProperty]
    private bool _hasActionFeedback;

    [ObservableProperty]
    private bool _actionFeedbackIsError;

    [ObservableProperty]
    private int _startupCount;

    [ObservableProperty]
    private int _serviceCount;

    [ObservableProperty]
    private int _protectedServiceCount;

    public string StartupTabLabel => $"Автозапуск ({StartupCount})";
    public string ServicesTabLabel => $"Служби Windows ({ServiceCount})";

    partial void OnStartupCountChanged(int value) => OnPropertyChanged(nameof(StartupTabLabel));
    partial void OnServiceCountChanged(int value) => OnPropertyChanged(nameof(ServicesTabLabel));
    partial void OnSearchQueryChanged(string value) => ApplyFilters();
    partial void OnStartupSourceFilterChanged(string value) => ApplyFilters();
    partial void OnServiceStateFilterChanged(string value) => ApplyFilters();

    public void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _ = RefreshAsync();
    }

    protected override void OnExpertModeChanged(bool isExpertMode)
    {
        if (_initialized)
        {
            _ = RefreshAsync();
        }
    }

    [RelayCommand]
    private void SelectSubTab(string indexText)
    {
        if (int.TryParse(indexText, out int idx) && idx is 0 or 1)
        {
            ActiveSubTab = idx;
            ApplyFilters();
        }
    }

    [RelayCommand]
    private void SetStartupFilter(string filter)
    {
        StartupSourceFilter = string.IsNullOrWhiteSpace(filter) ? "Усі" : filter;
    }

    [RelayCommand]
    private void SetServiceFilter(string filter)
    {
        ServiceStateFilter = string.IsNullOrWhiteSpace(filter) ? "Усі" : filter;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        _uiDispatch(() => IsLoading = true);

        try
        {
            bool includeSystemTasks = IsExpertMode;
            var startupTask = _manager.GetStartupEntriesAsync(includeSystemTasks);
            var servicesTask = _manager.GetManagedServicesAsync();

            await Task.WhenAll(startupTask, servicesTask).ConfigureAwait(false);

            _allStartupEntries = await startupTask.ConfigureAwait(false);
            _allServices = await servicesTask.ConfigureAwait(false);

            _uiDispatch(() =>
            {
                StartupCount = _allStartupEntries.Count;
                ServiceCount = _allServices.Count;
                ProtectedServiceCount = _allServices.Count(static s => s.IsCriticalProtected);
                ApplyFilters();
            });
        }
        catch (Exception ex)
        {
            _uiDispatch(() =>
            {
                ActionFeedback = $"Помилка читання автозапуску та служб: {ex.Message}";
                HasActionFeedback = true;
                ActionFeedbackIsError = true;
            });
        }
        finally
        {
            _uiDispatch(() => IsLoading = false);
        }
    }

    private async Task HandleStartupToggleAsync(StartupEntryItem entry, bool enabled)
    {
        var result = await _manager.SetStartupEntryEnabledAsync(entry, enabled).ConfigureAwait(false);
        _uiDispatch(() =>
        {
            ActionFeedback = result.Message;
            HasActionFeedback = true;
            ActionFeedbackIsError = !result.Succeeded;
        });

        if (result.Succeeded)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleServiceControlAsync(string serviceName, ServiceControlAction action)
    {
        var result = await _manager.ControlServiceAsync(serviceName, action).ConfigureAwait(false);
        _uiDispatch(() =>
        {
            ActionFeedback = result.Message;
            HasActionFeedback = true;
            ActionFeedbackIsError = !result.Succeeded;
        });

        if (result.Succeeded)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleServiceStartModeAsync(string serviceName, ServiceStartModeOption startMode)
    {
        var result = await _manager.SetServiceStartModeAsync(serviceName, startMode).ConfigureAwait(false);
        _uiDispatch(() =>
        {
            ActionFeedback = result.Message;
            HasActionFeedback = true;
            ActionFeedbackIsError = !result.Succeeded;

            if (!result.Succeeded)
            {
                var row = Services.FirstOrDefault(r => string.Equals(r.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
                row?.RevertStartModeSelection();
            }
        });

        if (result.Succeeded)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private void ApplyFilters()
    {
        string q = (SearchQuery ?? string.Empty).Trim();

        // Filter Startup entries
        var filteredStartup = _allStartupEntries.Where(item =>
        {
            bool sourceMatch = StartupSourceFilter switch
            {
                "Реєстр" => item.Source is StartupSourceKind.RegistryCurrentUserRun
                    or StartupSourceKind.RegistryLocalMachineRun
                    or StartupSourceKind.RegistryLocalMachineWow6432Run,
                "Startup" => item.Source is StartupSourceKind.StartupFolderUser or StartupSourceKind.StartupFolderCommon,
                "Планувальник" => item.Source == StartupSourceKind.TaskScheduler,
                "Вимкнені" => !item.IsEnabled,
                _ => true
            };

            if (!sourceMatch)
            {
                return false;
            }

            if (string.IsNullOrEmpty(q))
            {
                return true;
            }

            return item.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   item.Command.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   item.LocationPath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   item.PublisherOrAuthor.Contains(q, StringComparison.OrdinalIgnoreCase);
        }).Take(200).ToList();

        StartupEntries.Clear();
        foreach (var item in filteredStartup)
        {
            StartupEntries.Add(new StartupEntryRowViewModel(item, HandleStartupToggleAsync));
        }

        // Filter Services
        var filteredServices = _allServices.Where(svc =>
        {
            bool stateMatch = ServiceStateFilter switch
            {
                "Працюють" => string.Equals(svc.Status, "Працює", StringComparison.Ordinal),
                "Зупинені" => string.Equals(svc.Status, "Зупинено", StringComparison.Ordinal),
                "Захищені" => svc.IsCriticalProtected,
                _ => true
            };

            if (!stateMatch)
            {
                return false;
            }

            if (string.IsNullOrEmpty(q))
            {
                return true;
            }

            return svc.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   svc.ServiceName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   (svc.BinaryPath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        }).Take(150).ToList();

        Services.Clear();
        foreach (var svc in filteredServices)
        {
            Services.Add(new ManagedServiceRowViewModel(svc, HandleServiceControlAsync, HandleServiceStartModeAsync));
        }

        SummaryText = IsStartupTab
            ? $"Показано {StartupEntries.Count} із {_allStartupEntries.Count} точок автозапуску (Registry, Startup, Task Scheduler)"
            : $"Показано {Services.Count} із {_allServices.Count} служб Windows · Захищених критичних: {ProtectedServiceCount}";
    }

    public void Dispose()
    {
        DetachUiMode();
    }
}
