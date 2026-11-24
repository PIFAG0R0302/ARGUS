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

    public StartupEntryItem Model { get; private set; }
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

    public void UpdateFrom(StartupEntryItem model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        IsEnabled = model.IsEnabled;
    }
}

public sealed partial class ManagedServiceRowViewModel : ObservableObject
{
    private static readonly string[] StartModeChoices = ["Автоматично", "Вручну", "Вимкнено"];

    private readonly Func<string, ServiceControlAction, Task> _onControl;
    private readonly Func<string, ServiceStartModeOption, Task> _onChangeStartMode;
    private readonly AsyncRelayCommand _startCommand;
    private readonly AsyncRelayCommand _stopCommand;
    private readonly AsyncRelayCommand _restartCommand;
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
        IsCriticalProtected = model.IsCriticalProtected;
        BinaryPath = string.IsNullOrWhiteSpace(model.BinaryPath) ? "—" : model.BinaryPath;
        Description = string.IsNullOrWhiteSpace(model.Description) ? string.Empty : model.Description;
        HasDescription = !string.IsNullOrWhiteSpace(Description);

        _status = model.Status;
        _startModeLabel = model.StartModeLabel;
        _isRunning = string.Equals(model.Status, "Працює", StringComparison.Ordinal);
        _canStart = !_isRunning;
        _canStop = model.CanStop && !IsCriticalProtected;
        _canRestart = _isRunning && !IsCriticalProtected;
        _canChangeStartMode = !IsCriticalProtected;

        _selectedStartModeIndex = MapStartModeToIndex(model.StartMode);

        _startCommand = new AsyncRelayCommand(() => _onControl(ServiceName, ServiceControlAction.Start), () => CanStart);
        _stopCommand = new AsyncRelayCommand(() => _onControl(ServiceName, ServiceControlAction.Stop), () => CanStop);
        _restartCommand = new AsyncRelayCommand(() => _onControl(ServiceName, ServiceControlAction.Restart), () => CanRestart);
    }

    public ManagedServiceItem Model { get; private set; }
    public string ServiceName { get; }
    public string DisplayName { get; }
    public bool IsCriticalProtected { get; }
    public string BinaryPath { get; }
    public string Description { get; }
    public bool HasDescription { get; }
    public IReadOnlyList<string> StartModeOptions => StartModeChoices;

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private string _startModeLabel;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _canStart;

    [ObservableProperty]
    private bool _canStop;

    [ObservableProperty]
    private bool _canRestart;

    [ObservableProperty]
    private bool _canChangeStartMode;

    [ObservableProperty]
    private int _selectedStartModeIndex;

    public ICommand StartCommand => _startCommand;
    public ICommand StopCommand => _stopCommand;
    public ICommand RestartCommand => _restartCommand;

    public void UpdateFrom(ManagedServiceItem model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Status = model.Status;
        StartModeLabel = model.StartModeLabel;
        IsRunning = string.Equals(model.Status, "Працює", StringComparison.Ordinal);
        CanStart = !IsRunning;
        CanStop = model.CanStop && !IsCriticalProtected;
        CanRestart = IsRunning && !IsCriticalProtected;
        CanChangeStartMode = !IsCriticalProtected;

        _suppressModeCallback = true;
        try
        {
            SelectedStartModeIndex = MapStartModeToIndex(model.StartMode);
        }
        finally
        {
            _suppressModeCallback = false;
        }

        _startCommand.NotifyCanExecuteChanged();
        _stopCommand.NotifyCanExecuteChanged();
        _restartCommand.NotifyCanExecuteChanged();
    }

    private static int MapStartModeToIndex(ServiceStartModeOption mode) => mode switch
    {
        ServiceStartModeOption.Automatic => 0,
        ServiceStartModeOption.Manual => 1,
        ServiceStartModeOption.Disabled => 2,
        _ => 1
    };

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
    [NotifyPropertyChangedFor(nameof(IsStartupSourceAll))]
    [NotifyPropertyChangedFor(nameof(IsStartupSourceRegistry))]
    [NotifyPropertyChangedFor(nameof(IsStartupSourceStartup))]
    [NotifyPropertyChangedFor(nameof(IsStartupSourceTaskScheduler))]
    private string _startupSourceFilter = "Усі";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartupStateAll))]
    [NotifyPropertyChangedFor(nameof(IsStartupStateEnabled))]
    [NotifyPropertyChangedFor(nameof(IsStartupStateDisabled))]
    private string _startupStateFilter = "Усі стани";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServiceStateAll))]
    [NotifyPropertyChangedFor(nameof(IsServiceStateRunning))]
    [NotifyPropertyChangedFor(nameof(IsServiceStateStopped))]
    private string _serviceStateFilter = "Усі";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServiceStartModeAll))]
    [NotifyPropertyChangedFor(nameof(IsServiceStartModeAutomatic))]
    [NotifyPropertyChangedFor(nameof(IsServiceStartModeManual))]
    [NotifyPropertyChangedFor(nameof(IsServiceStartModeDisabled))]
    private string _serviceStartModeFilter = "Усі типи";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartupSortByName))]
    [NotifyPropertyChangedFor(nameof(IsStartupSortBySource))]
    [NotifyPropertyChangedFor(nameof(IsStartupSortByState))]
    [NotifyPropertyChangedFor(nameof(StartupSortNameHeader))]
    [NotifyPropertyChangedFor(nameof(StartupSortSourceHeader))]
    [NotifyPropertyChangedFor(nameof(StartupSortStateHeader))]
    private string _startupSortColumn = "NAME";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartupSortNameHeader))]
    [NotifyPropertyChangedFor(nameof(StartupSortSourceHeader))]
    [NotifyPropertyChangedFor(nameof(StartupSortStateHeader))]
    private bool _startupSortDescending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServiceSortByName))]
    [NotifyPropertyChangedFor(nameof(IsServiceSortByState))]
    [NotifyPropertyChangedFor(nameof(IsServiceSortByStartMode))]
    [NotifyPropertyChangedFor(nameof(ServiceSortNameHeader))]
    [NotifyPropertyChangedFor(nameof(ServiceSortStateHeader))]
    [NotifyPropertyChangedFor(nameof(ServiceSortStartModeHeader))]
    private string _serviceSortColumn = "NAME";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServiceSortNameHeader))]
    [NotifyPropertyChangedFor(nameof(ServiceSortStateHeader))]
    [NotifyPropertyChangedFor(nameof(ServiceSortStartModeHeader))]
    private bool _serviceSortDescending;

    public bool IsStartupSourceAll => string.Equals(StartupSourceFilter, "Усі", StringComparison.Ordinal);
    public bool IsStartupSourceRegistry => StartupSourceFilter is "Registry" or "Реєстр";
    public bool IsStartupSourceStartup => string.Equals(StartupSourceFilter, "Startup", StringComparison.Ordinal);
    public bool IsStartupSourceTaskScheduler => StartupSourceFilter is "Task Scheduler" or "Планувальник";

    public bool IsStartupStateAll => string.Equals(StartupStateFilter, "Усі стани", StringComparison.Ordinal);
    public bool IsStartupStateEnabled => string.Equals(StartupStateFilter, "Увімкнено", StringComparison.Ordinal);
    public bool IsStartupStateDisabled => string.Equals(StartupStateFilter, "Вимкнено", StringComparison.Ordinal);

    public bool IsServiceStateAll => string.Equals(ServiceStateFilter, "Усі", StringComparison.Ordinal);
    public bool IsServiceStateRunning => ServiceStateFilter is "Працює" or "Працюють";
    public bool IsServiceStateStopped => ServiceStateFilter is "Зупинено" or "Зупинені";

    public bool IsServiceStartModeAll => string.Equals(ServiceStartModeFilter, "Усі типи", StringComparison.Ordinal);
    public bool IsServiceStartModeAutomatic => string.Equals(ServiceStartModeFilter, "Автоматично", StringComparison.Ordinal);
    public bool IsServiceStartModeManual => string.Equals(ServiceStartModeFilter, "Вручну", StringComparison.Ordinal);
    public bool IsServiceStartModeDisabled => string.Equals(ServiceStartModeFilter, "Вимкнено", StringComparison.Ordinal);

    public bool IsStartupSortByName => string.Equals(StartupSortColumn, "NAME", StringComparison.OrdinalIgnoreCase);
    public bool IsStartupSortBySource => string.Equals(StartupSortColumn, "SOURCE", StringComparison.OrdinalIgnoreCase);
    public bool IsStartupSortByState => string.Equals(StartupSortColumn, "STATE", StringComparison.OrdinalIgnoreCase);

    public string StartupSortNameHeader => CollectionSyncHelper.FormatSortHeader("НАЗВА", "NAME", StartupSortColumn, StartupSortDescending);
    public string StartupSortSourceHeader => CollectionSyncHelper.FormatSortHeader("ДЖЕРЕЛО", "SOURCE", StartupSortColumn, StartupSortDescending);
    public string StartupSortStateHeader => CollectionSyncHelper.FormatSortHeader("СТАН", "STATE", StartupSortColumn, StartupSortDescending);

    public bool IsServiceSortByName => string.Equals(ServiceSortColumn, "NAME", StringComparison.OrdinalIgnoreCase);
    public bool IsServiceSortByState => string.Equals(ServiceSortColumn, "STATE", StringComparison.OrdinalIgnoreCase);
    public bool IsServiceSortByStartMode => string.Equals(ServiceSortColumn, "STARTMODE", StringComparison.OrdinalIgnoreCase);

    public string ServiceSortNameHeader => CollectionSyncHelper.FormatSortHeader("НАЗВА СЛУЖБИ", "NAME", ServiceSortColumn, ServiceSortDescending);
    public string ServiceSortStateHeader => CollectionSyncHelper.FormatSortHeader("СТАН", "STATE", ServiceSortColumn, ServiceSortDescending);
    public string ServiceSortStartModeHeader => CollectionSyncHelper.FormatSortHeader("ТИП ЗАПУСКУ", "STARTMODE", ServiceSortColumn, ServiceSortDescending);

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
    partial void OnStartupStateFilterChanged(string value) => ApplyFilters();
    partial void OnServiceStateFilterChanged(string value) => ApplyFilters();
    partial void OnServiceStartModeFilterChanged(string value) => ApplyFilters();

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
        StartupSourceFilter = string.IsNullOrWhiteSpace(filter) ? "Усі" : filter.Trim();
    }

    [RelayCommand]
    private void SetStartupStateFilter(string filter)
    {
        StartupStateFilter = string.IsNullOrWhiteSpace(filter) ? "Усі стани" : filter.Trim();
    }

    [RelayCommand]
    private void SetServiceFilter(string filter)
    {
        ServiceStateFilter = string.IsNullOrWhiteSpace(filter) ? "Усі" : filter;
    }

    [RelayCommand]
    private void SetServiceStartModeFilter(string filter)
    {
        ServiceStartModeFilter = string.IsNullOrWhiteSpace(filter) ? "Усі типи" : filter;
    }

    [RelayCommand]
    private void SetStartupSort(string column)
    {
        string normalized = string.IsNullOrWhiteSpace(column) ? "NAME" : column.Trim().ToUpperInvariant();
        if (string.Equals(StartupSortColumn, normalized, StringComparison.OrdinalIgnoreCase))
        {
            StartupSortDescending = !StartupSortDescending;
        }
        else
        {
            StartupSortColumn = normalized;
            StartupSortDescending = string.Equals(normalized, "STATE", StringComparison.OrdinalIgnoreCase);
        }

        ApplyFilters();
    }

    [RelayCommand]
    private void SetServiceSort(string column)
    {
        string normalized = string.IsNullOrWhiteSpace(column) ? "NAME" : column.Trim().ToUpperInvariant();
        if (string.Equals(ServiceSortColumn, normalized, StringComparison.OrdinalIgnoreCase))
        {
            ServiceSortDescending = !ServiceSortDescending;
        }
        else
        {
            ServiceSortColumn = normalized;
            ServiceSortDescending = string.Equals(normalized, "STATE", StringComparison.OrdinalIgnoreCase);
        }

        ApplyFilters();
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
        var startupFiltered = _allStartupEntries.Where(item =>
        {
            bool sourceMatch = StartupSourceFilter switch
            {
                "Registry" or "Реєстр" => item.Source is StartupSourceKind.RegistryCurrentUserRun
                    or StartupSourceKind.RegistryLocalMachineRun
                    or StartupSourceKind.RegistryLocalMachineWow6432Run,
                "Startup" => item.Source is StartupSourceKind.StartupFolderUser or StartupSourceKind.StartupFolderCommon,
                "Task Scheduler" or "Планувальник" => item.Source == StartupSourceKind.TaskScheduler,
                "Вимкнені" => !item.IsEnabled,
                _ => true
            };

            if (!sourceMatch)
            {
                return false;
            }

            bool stateMatch = StartupStateFilter switch
            {
                "Увімкнено" => item.IsEnabled,
                "Вимкнено" => !item.IsEnabled,
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

            return item.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   item.Command.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   item.LocationPath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   item.PublisherOrAuthor.Contains(q, StringComparison.OrdinalIgnoreCase);
        });

        IEnumerable<StartupEntryItem> sortedStartup = (StartupSortColumn.ToUpperInvariant(), StartupSortDescending) switch
        {
            ("SOURCE", false) => startupFiltered
                .OrderBy(static i => GetStartupSourceOrder(i.Source))
                .ThenBy(static i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static i => i.Id, StringComparer.OrdinalIgnoreCase),
            ("SOURCE", true) => startupFiltered
                .OrderByDescending(static i => GetStartupSourceOrder(i.Source))
                .ThenBy(static i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static i => i.Id, StringComparer.OrdinalIgnoreCase),
            ("STATE", false) => startupFiltered
                .OrderBy(static i => i.IsEnabled)
                .ThenBy(static i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static i => i.Id, StringComparer.OrdinalIgnoreCase),
            ("STATE", true) => startupFiltered
                .OrderByDescending(static i => i.IsEnabled)
                .ThenBy(static i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static i => i.Id, StringComparer.OrdinalIgnoreCase),
            (_, true) => startupFiltered
                .OrderByDescending(static i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static i => i.Id, StringComparer.OrdinalIgnoreCase),
            _ => startupFiltered
                .OrderBy(static i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static i => i.Id, StringComparer.OrdinalIgnoreCase)
        };

        var desiredStartup = sortedStartup.Take(200).ToList();

        CollectionSyncHelper.SyncByKey(
            StartupEntries,
            desiredStartup,
            static m => m.Id,
            static r => r.Model.Id,
            m => new StartupEntryRowViewModel(m, HandleStartupToggleAsync),
            static (r, m) => r.UpdateFrom(m),
            currentSelection: null,
            static _ => { },
            StringComparer.OrdinalIgnoreCase);

        // Filter Services
        var servicesFiltered = _allServices.Where(svc =>
        {
            bool stateMatch = ServiceStateFilter switch
            {
                "Працює" or "Працюють" => string.Equals(svc.Status, "Працює", StringComparison.Ordinal),
                "Зупинено" or "Зупинені" => string.Equals(svc.Status, "Зупинено", StringComparison.Ordinal),
                "Захищені" => svc.IsCriticalProtected,
                _ => true
            };

            if (!stateMatch)
            {
                return false;
            }

            bool startModeMatch = ServiceStartModeFilter switch
            {
                "Автоматично" => svc.StartMode == ServiceStartModeOption.Automatic,
                "Вручну" => svc.StartMode == ServiceStartModeOption.Manual,
                "Вимкнено" => svc.StartMode == ServiceStartModeOption.Disabled,
                _ => true
            };

            if (!startModeMatch)
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
        });

        IEnumerable<ManagedServiceItem> sortedServices = (ServiceSortColumn.ToUpperInvariant(), ServiceSortDescending) switch
        {
            ("STATE", false) => servicesFiltered
                .OrderBy(static s => string.Equals(s.Status, "Працює", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(static s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static s => s.ServiceName, StringComparer.OrdinalIgnoreCase),
            ("STATE", true) => servicesFiltered
                .OrderByDescending(static s => string.Equals(s.Status, "Працює", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(static s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static s => s.ServiceName, StringComparer.OrdinalIgnoreCase),
            ("STARTMODE", false) => servicesFiltered
                .OrderBy(static s => GetServiceStartModeOrder(s.StartMode))
                .ThenBy(static s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static s => s.ServiceName, StringComparer.OrdinalIgnoreCase),
            ("STARTMODE", true) => servicesFiltered
                .OrderByDescending(static s => GetServiceStartModeOrder(s.StartMode))
                .ThenBy(static s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static s => s.ServiceName, StringComparer.OrdinalIgnoreCase),
            (_, true) => servicesFiltered
                .OrderByDescending(static s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static s => s.ServiceName, StringComparer.OrdinalIgnoreCase),
            _ => servicesFiltered
                .OrderBy(static s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static s => s.ServiceName, StringComparer.OrdinalIgnoreCase)
        };

        var desiredServices = sortedServices.Take(150).ToList();

        CollectionSyncHelper.SyncByKey(
            Services,
            desiredServices,
            static m => m.ServiceName,
            static r => r.ServiceName,
            m => new ManagedServiceRowViewModel(m, HandleServiceControlAsync, HandleServiceStartModeAsync),
            static (r, m) => r.UpdateFrom(m),
            currentSelection: null,
            static _ => { },
            StringComparer.OrdinalIgnoreCase);

        SummaryText = IsStartupTab
            ? $"Показано {StartupEntries.Count} із {_allStartupEntries.Count} точок автозапуску (Registry, Startup, Task Scheduler)"
            : $"Показано {Services.Count} із {_allServices.Count} служб Windows · Захищених критичних: {ProtectedServiceCount}";
    }

    private static int GetStartupSourceOrder(StartupSourceKind source) => source switch
    {
        StartupSourceKind.RegistryCurrentUserRun => 0,
        StartupSourceKind.RegistryLocalMachineRun => 1,
        StartupSourceKind.RegistryLocalMachineWow6432Run => 2,
        StartupSourceKind.StartupFolderUser => 3,
        StartupSourceKind.StartupFolderCommon => 4,
        StartupSourceKind.TaskScheduler => 5,
        _ => 6
    };

    private static int GetServiceStartModeOrder(ServiceStartModeOption mode) => mode switch
    {
        ServiceStartModeOption.Automatic => 0,
        ServiceStartModeOption.Manual => 1,
        ServiceStartModeOption.Disabled => 2,
        _ => 3
    };

    public void Dispose()
    {
        DetachUiMode();
    }
}
