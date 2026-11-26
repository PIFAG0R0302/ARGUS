using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Argus.App.Services;
using Argus.Core.Contracts.System;
using Argus.Core.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed partial class ProcessRowViewModel : ObservableObject
{
    private readonly RelayCommand _openFolderCommand;

    public ProcessRowViewModel(
        ProcessMetricsItem model,
        Action<ProcessMetricsItem> onRequestTerminate,
        Action<string?> onOpenFolder)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));

        Pid = model.Pid;
        PidText = model.Pid.ToString(CultureInfo.InvariantCulture);
        IsCriticalSystem = ProcessesViewModel.IsProtectedProcess(model);
        CanTerminate = !IsCriticalSystem;

        _name = model.Name;
        _cpuText = string.Format(CultureInfo.CurrentCulture, "{0:0.0}%", model.CpuPercent);
        _memoryText = FormatMemory(model.WorkingSetBytes);
        _diskIoText = FormatDiskIo(model.DiskReadBytesPerSec, model.DiskWriteBytesPerSec);
        _threadsText = $"{model.ThreadCount} пот.";
        _executablePath = FormatExecutablePath(model);
        _hasExecutablePath = !string.IsNullOrWhiteSpace(model.ExecutablePath);
        _isHighCpu = model.CpuPercent >= 15.0;
        _hasDiskActivity = model.TotalDiskBytesPerSec >= 1024.0;

        RequestTerminateCommand = new RelayCommand(() => onRequestTerminate(Model), () => CanTerminate);
        _openFolderCommand = new RelayCommand(() => onOpenFolder(Model.ExecutablePath), () => HasExecutablePath);
    }

    public ProcessMetricsItem Model { get; private set; }
    public int Pid { get; }
    public string PidText { get; }
    public bool IsCriticalSystem { get; }
    public bool CanTerminate { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _cpuText;

    [ObservableProperty]
    private string _memoryText;

    [ObservableProperty]
    private string _diskIoText;

    [ObservableProperty]
    private string _threadsText;

    [ObservableProperty]
    private string _executablePath;

    [ObservableProperty]
    private bool _hasExecutablePath;

    [ObservableProperty]
    private bool _isHighCpu;

    [ObservableProperty]
    private bool _hasDiskActivity;

    public ICommand RequestTerminateCommand { get; }
    public ICommand OpenFolderCommand => _openFolderCommand;

    public void UpdateFrom(ProcessMetricsItem model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;

        Name = model.Name;
        CpuText = string.Format(CultureInfo.CurrentCulture, "{0:0.0}%", model.CpuPercent);
        MemoryText = FormatMemory(model.WorkingSetBytes);
        DiskIoText = FormatDiskIo(model.DiskReadBytesPerSec, model.DiskWriteBytesPerSec);
        ThreadsText = $"{model.ThreadCount} пот.";
        ExecutablePath = FormatExecutablePath(model);
        HasExecutablePath = !string.IsNullOrWhiteSpace(model.ExecutablePath);
        IsHighCpu = model.CpuPercent >= 15.0;
        HasDiskActivity = model.TotalDiskBytesPerSec >= 1024.0;

        _openFolderCommand.NotifyCanExecuteChanged();
    }

    private static string FormatExecutablePath(ProcessMetricsItem model) =>
        string.IsNullOrWhiteSpace(model.ExecutablePath)
            ? (model.IsProtectedAccess ? "Системний / захищений доступ" : "Шлях недоступний")
            : model.ExecutablePath;

    internal static string FormatMemory(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024.0
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.00} ГБ", mb / 1024.0)
            : string.Format(CultureInfo.CurrentCulture, "{0:0.0} МБ", mb);
    }

    internal static string FormatDiskIo(double readBps, double writeBps)
    {
        if (readBps < 1024.0 && writeBps < 1024.0)
        {
            return "0,0 КБ/с";
        }

        return $"R {FormatRate(readBps)} · W {FormatRate(writeBps)}";
    }

    private static string FormatRate(double bps)
    {
        double kb = bps / 1024.0;
        return kb >= 1024.0
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} МБ/с", kb / 1024.0)
            : string.Format(CultureInfo.CurrentCulture, "{0:0.0} КБ/с", kb);
    }
}

internal sealed record ProcessGroupSnapshot(
    string GroupKey,
    string DisplayName,
    int LowestPid,
    double TotalCpuPercent,
    long TotalWorkingSetBytes,
    double TotalDiskReadBytesPerSec,
    double TotalDiskWriteBytesPerSec,
    int TotalThreadCount,
    bool HasCriticalSystem,
    string? RepresentativeRawPath,
    string RepresentativeDisplayPath,
    IReadOnlyList<ProcessMetricsItem> Instances)
{
    public double TotalDiskBytesPerSec => TotalDiskReadBytesPerSec + TotalDiskWriteBytesPerSec;
}

public sealed partial class ProcessGroupViewModel : ObservableObject
{
    private readonly Action<ProcessMetricsItem> _onRequestTerminate;
    private readonly Action<ProcessGroupViewModel>? _onRequestTerminateGroup;
    private readonly Action<string?> _onOpenFolder;
    private readonly RelayCommand _openFolderCommand;
    private readonly RelayCommand _requestTerminateSingleCommand;
    private readonly RelayCommand _requestTerminateGroupCommand;
    private string? _representativeRawPath;
    private ProcessMetricsItem? _singleInstanceModel;

    internal ProcessGroupViewModel(
        ProcessGroupSnapshot snapshot,
        Action<ProcessMetricsItem> onRequestTerminate,
        Action<string?> onOpenFolder,
        Action<ProcessGroupViewModel>? onRequestTerminateGroup = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _onRequestTerminate = onRequestTerminate ?? throw new ArgumentNullException(nameof(onRequestTerminate));
        _onOpenFolder = onOpenFolder ?? throw new ArgumentNullException(nameof(onOpenFolder));
        _onRequestTerminateGroup = onRequestTerminateGroup;

        GroupKey = snapshot.GroupKey;
        Instances = [];

        _openFolderCommand = new RelayCommand(
            () => _onOpenFolder(_representativeRawPath),
            () => HasExecutablePath);

        _requestTerminateSingleCommand = new RelayCommand(
            () =>
            {
                if (_singleInstanceModel is not null)
                {
                    _onRequestTerminate(_singleInstanceModel);
                }
            },
            () => CanTerminateSingle && _singleInstanceModel is not null);

        _requestTerminateGroupCommand = new RelayCommand(
            () =>
            {
                if (IsSingleInstance && _singleInstanceModel is not null)
                {
                    _onRequestTerminate(_singleInstanceModel);
                    return;
                }

                _onRequestTerminateGroup?.Invoke(this);
            },
            () => CanTerminateGroup);

        UpdateFrom(snapshot);
    }

    public string GroupKey { get; }
    public ObservableCollection<ProcessRowViewModel> Instances { get; }

    [ObservableProperty]
    private ProcessRowViewModel? _selectedInstance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded;

    public string ExpandGlyph => IsExpanded ? "▼" : "▶";

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _instanceCount;

    [ObservableProperty]
    private bool _isMultiInstance;

    [ObservableProperty]
    private bool _isSingleInstance;

    [ObservableProperty]
    private int _representativePid;

    [ObservableProperty]
    private string _pidOrCountText = string.Empty;

    [ObservableProperty]
    private double _totalCpuPercent;

    [ObservableProperty]
    private long _totalWorkingSetBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalDiskBytesPerSec))]
    private double _totalDiskReadBytesPerSec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalDiskBytesPerSec))]
    private double _totalDiskWriteBytesPerSec;

    public double TotalDiskBytesPerSec => TotalDiskReadBytesPerSec + TotalDiskWriteBytesPerSec;

    [ObservableProperty]
    private int _totalThreadCount;

    [ObservableProperty]
    private string _threadsText = string.Empty;

    [ObservableProperty]
    private string _cpuText = string.Empty;

    [ObservableProperty]
    private string _memoryText = string.Empty;

    [ObservableProperty]
    private string _diskIoText = string.Empty;

    [ObservableProperty]
    private bool _hasCriticalSystem;

    [ObservableProperty]
    private string _executablePath = string.Empty;

    [ObservableProperty]
    private bool _hasExecutablePath;

    [ObservableProperty]
    private bool _canTerminateSingle;

    [ObservableProperty]
    private bool _canTerminateGroup;

    public ICommand OpenFolderCommand => _openFolderCommand;
    public ICommand RequestTerminateSingleCommand => _requestTerminateSingleCommand;
    public ICommand RequestTerminateGroupCommand => _requestTerminateGroupCommand;

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    internal void UpdateFrom(ProcessGroupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Name = snapshot.DisplayName;
        InstanceCount = snapshot.Instances.Count;
        IsMultiInstance = InstanceCount > 1;
        IsSingleInstance = InstanceCount == 1;
        RepresentativePid = snapshot.LowestPid;
        PidOrCountText = IsMultiInstance
            ? $"{InstanceCount} проц."
            : snapshot.LowestPid.ToString(CultureInfo.InvariantCulture);

        TotalCpuPercent = snapshot.TotalCpuPercent;
        TotalWorkingSetBytes = snapshot.TotalWorkingSetBytes;
        TotalDiskReadBytesPerSec = snapshot.TotalDiskReadBytesPerSec;
        TotalDiskWriteBytesPerSec = snapshot.TotalDiskWriteBytesPerSec;
        TotalThreadCount = snapshot.TotalThreadCount;

        ThreadsText = $"{snapshot.TotalThreadCount} пот.";
        CpuText = string.Format(CultureInfo.CurrentCulture, "{0:0.0}%", snapshot.TotalCpuPercent);
        MemoryText = ProcessRowViewModel.FormatMemory(snapshot.TotalWorkingSetBytes);
        DiskIoText = ProcessRowViewModel.FormatDiskIo(snapshot.TotalDiskReadBytesPerSec, snapshot.TotalDiskWriteBytesPerSec);
        HasCriticalSystem = snapshot.HasCriticalSystem;

        _representativeRawPath = snapshot.RepresentativeRawPath;
        ExecutablePath = snapshot.RepresentativeDisplayPath;
        HasExecutablePath = !string.IsNullOrWhiteSpace(snapshot.RepresentativeRawPath);

        _singleInstanceModel = snapshot.Instances.Count == 1 ? snapshot.Instances[0] : null;
        CanTerminateSingle = _singleInstanceModel is not null && !ProcessesViewModel.IsProtectedProcess(_singleInstanceModel);
        CanTerminateGroup = snapshot.Instances.Any(static p => !ProcessesViewModel.IsProtectedProcess(p));

        CollectionSyncHelper.SyncByKey(
            Instances,
            snapshot.Instances,
            static m => m.Pid,
            static r => r.Pid,
            m => new ProcessRowViewModel(m, _onRequestTerminate, _onOpenFolder),
            static (r, m) => r.UpdateFrom(m),
            SelectedInstance,
            row => SelectedInstance = row);

        _openFolderCommand.NotifyCanExecuteChanged();
        _requestTerminateSingleCommand.NotifyCanExecuteChanged();
        _requestTerminateGroupCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class ProcessesViewModel : SectionViewModelBase, IDisposable
{
    private sealed record PendingGroupTerminate(
        string GroupName,
        int TotalCount,
        int SkippedCriticalCount,
        IReadOnlyList<ProcessMetricsItem> TerminableInstances);

    private readonly IProcessMonitorService _processService;
    private readonly Action<Action> _uiDispatch;
    private readonly Timer _pollTimer;
    private IReadOnlyList<ProcessMetricsItem> _allProcesses = [];
    private ProcessMetricsItem? _pendingTerminateTarget;
    private PendingGroupTerminate? _pendingTerminateGroup;
    private int _refreshGate;
    private volatile bool _isUiActive;
    private bool _initialized;

    public ProcessesViewModel(
        IProcessMonitorService processService,
        Action<Action>? uiDispatch = null,
        AppUiModeService? uiMode = null,
        bool autoLoadOnInit = false)
        : base(
            ArgusSectionId.Processes,
            "Процеси системи",
            "Живий моніторинг PID, CPU %, ОЗП, швидкості Disk I/O та безпечне завершення процесів із захистом критичних компонентів ОС",
            uiMode,
            uiDispatch)
    {
        _processService = processService ?? throw new ArgumentNullException(nameof(processService));
        _uiDispatch = uiDispatch ?? (static action => action());

        Processes = [];
        ProcessGroups = [];
        _pollTimer = new Timer(OnPollTick, null, Timeout.Infinite, Timeout.Infinite);

        if (autoLoadOnInit)
        {
            EnsureInitialized();
        }
    }

    public ObservableCollection<ProcessRowViewModel> Processes { get; }
    public ObservableCollection<ProcessGroupViewModel> ProcessGroups { get; }

    [ObservableProperty]
    private ProcessGroupViewModel? _selectedGroup;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortByRam))]
    [NotifyPropertyChangedFor(nameof(IsSortByCpu))]
    [NotifyPropertyChangedFor(nameof(IsSortByDisk))]
    [NotifyPropertyChangedFor(nameof(IsSortByName))]
    [NotifyPropertyChangedFor(nameof(IsSortByPid))]
    [NotifyPropertyChangedFor(nameof(SortPidHeader))]
    [NotifyPropertyChangedFor(nameof(SortNameHeader))]
    [NotifyPropertyChangedFor(nameof(SortCpuHeader))]
    [NotifyPropertyChangedFor(nameof(SortRamHeader))]
    [NotifyPropertyChangedFor(nameof(SortDiskHeader))]
    private string _sortBy = "RAM";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortPidHeader))]
    [NotifyPropertyChangedFor(nameof(SortNameHeader))]
    [NotifyPropertyChangedFor(nameof(SortCpuHeader))]
    [NotifyPropertyChangedFor(nameof(SortRamHeader))]
    [NotifyPropertyChangedFor(nameof(SortDiskHeader))]
    private bool _sortDescending = true;

    [ObservableProperty]
    private ProcessRowViewModel? _selectedProcess;

    public bool IsSortByRam => SortBy == "RAM";
    public bool IsSortByCpu => SortBy == "CPU";
    public bool IsSortByDisk => SortBy == "DISK";
    public bool IsSortByName => SortBy == "NAME";
    public bool IsSortByPid => SortBy == "PID";

    public string SortPidHeader => CollectionSyncHelper.FormatSortHeader("PID", "PID", SortBy, SortDescending);
    public string SortNameHeader => CollectionSyncHelper.FormatSortHeader("ПРОЦЕС ТА ШЛЯХ .EXE", "NAME", SortBy, SortDescending);
    public string SortCpuHeader => CollectionSyncHelper.FormatSortHeader("CPU %", "CPU", SortBy, SortDescending);
    public string SortRamHeader => CollectionSyncHelper.FormatSortHeader("RAM", "RAM", SortBy, SortDescending);
    public string SortDiskHeader => CollectionSyncHelper.FormatSortHeader("DISK I/O (R / W)", "DISK", SortBy, SortDescending);

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _summaryText = "Відкрийте вкладку або натисніть «Оновити» для перегляду активних процесів.";

    [ObservableProperty]
    private string _actionFeedback = string.Empty;

    [ObservableProperty]
    private bool _hasActionFeedback;

    [ObservableProperty]
    private int _totalProcessCount;

    [ObservableProperty]
    private int _hiddenCriticalCount;

    [ObservableProperty]
    private bool _isTerminateConfirmationOpen;

    [ObservableProperty]
    private string _terminateConfirmationTitle = "Підтвердження завершення процесу";

    [ObservableProperty]
    private string _terminatePromptText = string.Empty;

    partial void OnSearchQueryChanged(string value) => ApplyFiltersAndSort();
    partial void OnSortByChanged(string value) => ApplyFiltersAndSort();
    partial void OnSortDescendingChanged(bool value) => ApplyFiltersAndSort();

    public void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _ = RefreshAsync();
    }

    public void SetUiActive(bool active)
    {
        if (_isUiActive == active)
        {
            return;
        }

        _isUiActive = active;
        if (active)
        {
            EnsureInitialized();
            _pollTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
        else
        {
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    protected override void OnExpertModeChanged(bool isExpertMode)
    {
        ApplyFiltersAndSort();
    }

    [RelayCommand]
    private void SetSort(string sortKey)
    {
        if (string.IsNullOrWhiteSpace(sortKey))
        {
            return;
        }

        string normalized = sortKey.Trim().ToUpperInvariant();
        if (string.Equals(SortBy, normalized, StringComparison.Ordinal))
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortDescending = normalized is not ("NAME" or "PID");
            SortBy = normalized;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshGate, 1) == 1)
        {
            return;
        }

        _uiDispatch(() => IsLoading = true);
        try
        {
            var items = await _processService.GetProcessMetricsAsync().ConfigureAwait(false);
            _allProcesses = items;

            _uiDispatch(() =>
            {
                TotalProcessCount = items.Count;
                HiddenCriticalCount = items.Count(static p => IsProtectedProcess(p));
                ApplyFiltersAndSort();
            });
        }
        catch (Exception ex)
        {
            _uiDispatch(() =>
            {
                ActionFeedback = $"Помилка опитування процесів: {ex.Message}";
                HasActionFeedback = true;
            });
        }
        finally
        {
            _uiDispatch(() => IsLoading = false);
            Volatile.Write(ref _refreshGate, 0);
        }
    }

    [RelayCommand]
    public async Task ConfirmTerminateAsync()
    {
        var singleTarget = _pendingTerminateTarget;
        var groupTarget = _pendingTerminateGroup;
        _pendingTerminateTarget = null;
        _pendingTerminateGroup = null;
        IsTerminateConfirmationOpen = false;
        TerminateConfirmationTitle = "Підтвердження завершення процесу";
        TerminatePromptText = string.Empty;

        if (groupTarget is not null)
        {
            await ExecuteGroupTerminateAsync(groupTarget).ConfigureAwait(false);
            return;
        }

        if (singleTarget is null)
        {
            return;
        }

        if (IsProtectedProcess(singleTarget))
        {
            _uiDispatch(() =>
            {
                ActionFeedback = $"Процес «{singleTarget.Name}» (PID {singleTarget.Pid}) є критичним для Windows і захищений від завершення.";
                HasActionFeedback = true;
            });
            return;
        }

        var result = await _processService.TerminateProcessAsync(singleTarget.Pid).ConfigureAwait(false);
        _uiDispatch(() =>
        {
            ActionFeedback = result.Message;
            HasActionFeedback = true;
        });

        if (result.Succeeded)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private async Task ExecuteGroupTerminateAsync(PendingGroupTerminate groupTarget)
    {
        int succeededCount = 0;
        int failedCount = 0;
        int skippedCritical = groupTarget.SkippedCriticalCount;
        bool requiresElevation = false;
        string? firstFailureMessage = null;

        foreach (var instance in groupTarget.TerminableInstances)
        {
            if (IsProtectedProcess(instance))
            {
                skippedCritical++;
                continue;
            }

            var result = await _processService.TerminateProcessAsync(instance.Pid).ConfigureAwait(false);
            if (result.Succeeded)
            {
                succeededCount++;
            }
            else
            {
                failedCount++;
                requiresElevation |= result.RequiresElevation;
                firstFailureMessage ??= result.Message;
            }
        }

        string feedback = FormatGroupTerminateFeedback(
            groupTarget.GroupName,
            groupTarget.TotalCount,
            groupTarget.TerminableInstances.Count,
            succeededCount,
            failedCount,
            skippedCritical,
            requiresElevation,
            firstFailureMessage);

        _uiDispatch(() =>
        {
            ActionFeedback = feedback;
            HasActionFeedback = true;
        });

        if (succeededCount > 0)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private static string FormatGroupTerminateFeedback(
        string groupName,
        int totalCount,
        int attemptedCount,
        int succeededCount,
        int failedCount,
        int skippedCritical,
        bool requiresElevation,
        string? firstFailureMessage)
    {
        if (succeededCount > 0 && failedCount == 0)
        {
            return skippedCritical > 0
                ? $"Завершено {succeededCount} процесів групи «{groupName}» (пропущено критичних ОС: {skippedCritical})."
                : $"Завершено {succeededCount} із {totalCount} процесів групи «{groupName}».";
        }

        if (succeededCount > 0 && failedCount > 0)
        {
            var msg = $"Завершено {succeededCount} із {attemptedCount} процесів групи «{groupName}», не вдалося завершити: {failedCount}.";
            if (skippedCritical > 0)
            {
                msg += $" Пропущено критичних ОС: {skippedCritical}.";
            }

            if (requiresElevation)
            {
                msg += " Для решти потрібні права адміністратора (UAC).";
            }

            return msg;
        }

        if (failedCount > 0)
        {
            var msg = $"Не вдалося завершити процеси групи «{groupName}» ({failedCount} помилок).";
            if (requiresElevation)
            {
                msg += " Потрібні права адміністратора (UAC).";
            }
            else if (!string.IsNullOrWhiteSpace(firstFailureMessage))
            {
                msg += $" {firstFailureMessage}";
            }

            return msg;
        }

        return $"У групі «{groupName}» немає доступних для завершення процесів.";
    }

    [RelayCommand]
    private void CancelTerminate()
    {
        _pendingTerminateTarget = null;
        _pendingTerminateGroup = null;
        IsTerminateConfirmationOpen = false;
        TerminateConfirmationTitle = "Підтвердження завершення процесу";
        TerminatePromptText = string.Empty;
    }

    private void RequestTerminate(ProcessMetricsItem item)
    {
        if (IsProtectedProcess(item))
        {
            ActionFeedback = $"Процес «{item.Name}» (PID {item.Pid}) є критичним для Windows і захищений від завершення.";
            HasActionFeedback = true;
            return;
        }

        _pendingTerminateGroup = null;
        _pendingTerminateTarget = item;
        TerminateConfirmationTitle = "Підтвердження завершення процесу";
        TerminatePromptText = $"Примусово завершити процес «{item.Name}» (PID {item.Pid})? Незбережені дані цієї програми буде втрачено.";
        IsTerminateConfirmationOpen = true;
    }

    private void RequestTerminateGroup(ProcessGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var instances = group.Instances.Select(static r => r.Model).ToList();
        var terminable = instances.Where(static p => !IsProtectedProcess(p)).ToList();
        int criticalCount = instances.Count - terminable.Count;

        if (terminable.Count == 0)
        {
            ActionFeedback = $"Усі процеси групи «{group.Name}» є критичними системними процесами Windows і захищені від завершення.";
            HasActionFeedback = true;
            return;
        }

        if (instances.Count == 1)
        {
            RequestTerminate(terminable[0]);
            return;
        }

        _pendingTerminateTarget = null;
        _pendingTerminateGroup = new PendingGroupTerminate(
            group.Name,
            instances.Count,
            criticalCount,
            terminable);

        TerminateConfirmationTitle = "Підтвердження завершення групи процесів";
        TerminatePromptText = criticalCount > 0
            ? $"Примусово завершити {terminable.Count} із {instances.Count} процесів групи «{group.Name}»? Критичні системні процеси ОС ({criticalCount}) буде пропущено."
            : $"Примусово завершити всі процеси групи «{group.Name}» (інстансів: {instances.Count})? Незбережені дані цієї програми буде втрачено.";
        IsTerminateConfirmationOpen = true;
    }

    internal static bool IsProtectedProcess(ProcessMetricsItem item) =>
        item.IsCriticalSystem ||
        CriticalProcessProtection.IsCriticalProcess(item.Pid, item.Name, currentProcessId: -1);

    private void OpenFolder(string? path)
    {
        if (!_processService.OpenProcessFolder(path))
        {
            ActionFeedback = "Не вдалося відкрити теку виконуваного файлу процесу.";
            HasActionFeedback = true;
        }
    }

    private void OnPollTick(object? state)
    {
        if (_isUiActive)
        {
            _ = RefreshAsync();
        }
    }

    private void ApplyFiltersAndSort()
    {
        string q = (SearchQuery ?? string.Empty).Trim();
        bool showCritical = IsExpertMode;

        IEnumerable<ProcessMetricsItem> query = _allProcesses;
        if (!showCritical)
        {
            query = query.Where(static p => !IsProtectedProcess(p));
        }

        if (!string.IsNullOrEmpty(q))
        {
            query = query.Where(p =>
                p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (p.ExecutablePath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var filtered = query.ToList();

        var visibleFlat = SortItems(filtered, SortBy, SortDescending).Take(160).ToList();
        CollectionSyncHelper.SyncByKey(
            Processes,
            visibleFlat,
            static m => m.Pid,
            static r => r.Pid,
            m => new ProcessRowViewModel(m, RequestTerminate, OpenFolder),
            static (r, m) => r.UpdateFrom(m),
            SelectedProcess,
            row => SelectedProcess = row);

        var groupSnapshots = filtered
            .GroupBy(static p => NormalizeGroupKey(p.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => CreateGroupSnapshot(group.Key, group, SortBy, SortDescending));

        var sortedGroups = SortGroups(groupSnapshots, SortBy, SortDescending).Take(160).ToList();
        CollectionSyncHelper.SyncByKey(
            ProcessGroups,
            sortedGroups,
            static g => g.GroupKey,
            static vm => vm.GroupKey,
            g => new ProcessGroupViewModel(g, RequestTerminate, OpenFolder, RequestTerminateGroup),
            static (vm, g) => vm.UpdateFrom(g),
            SelectedGroup,
            group => SelectedGroup = group,
            StringComparer.OrdinalIgnoreCase);

        int visibleProcessCount = sortedGroups.Sum(static g => g.Instances.Count);
        string hiddenSuffix = showCritical
            ? $" · Експертний режим (системні: {HiddenCriticalCount})"
            : $" · Сховано критичних системних: {HiddenCriticalCount} (увімкніть «Експертний режим» для показу)";

        SummaryText = $"Програм: {ProcessGroups.Count} · Процесів: {visibleProcessCount} із {_allProcesses.Count}{hiddenSuffix}";
    }

    private static string NormalizeGroupKey(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "(невідомо)" : name.Trim();

    private static ProcessGroupSnapshot CreateGroupSnapshot(
        string groupKey,
        IEnumerable<ProcessMetricsItem> items,
        string sortBy,
        bool sortDescending)
    {
        var sortedInstances = SortItems(items, sortBy, sortDescending).ToList();

        string displayName = sortedInstances
            .Select(static p => p.Name?.Trim())
            .FirstOrDefault(static n => !string.IsNullOrEmpty(n))
            ?? groupKey;

        int lowestPid = sortedInstances.Min(static p => p.Pid);
        double totalCpu = sortedInstances.Sum(static p => p.CpuPercent);
        long totalRam = sortedInstances.Sum(static p => p.WorkingSetBytes);
        double totalRead = sortedInstances.Sum(static p => p.DiskReadBytesPerSec);
        double totalWrite = sortedInstances.Sum(static p => p.DiskWriteBytesPerSec);
        int totalThreads = sortedInstances.Sum(static p => p.ThreadCount);
        bool hasCritical = sortedInstances.Any(static p => IsProtectedProcess(p));

        string? rawPath = sortedInstances
            .Select(static p => p.ExecutablePath)
            .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path));

        string displayPath = !string.IsNullOrWhiteSpace(rawPath)
            ? rawPath
            : (sortedInstances.Any(static p => p.IsProtectedAccess)
                ? "Системний / захищений доступ"
                : "Шлях недоступний");

        return new ProcessGroupSnapshot(
            groupKey,
            displayName,
            lowestPid,
            totalCpu,
            totalRam,
            totalRead,
            totalWrite,
            totalThreads,
            hasCritical,
            rawPath,
            displayPath,
            sortedInstances);
    }

    private static IEnumerable<ProcessMetricsItem> SortItems(
        IEnumerable<ProcessMetricsItem> source,
        string sortBy,
        bool sortDescending) =>
        (sortBy, sortDescending) switch
        {
            ("CPU", true) => source
                .OrderByDescending(static p => p.CpuPercent)
                .ThenByDescending(static p => p.WorkingSetBytes)
                .ThenBy(static p => p.Pid),
            ("CPU", false) => source
                .OrderBy(static p => p.CpuPercent)
                .ThenBy(static p => p.WorkingSetBytes)
                .ThenBy(static p => p.Pid),
            ("DISK", true) => source
                .OrderByDescending(static p => p.TotalDiskBytesPerSec)
                .ThenByDescending(static p => p.WorkingSetBytes)
                .ThenBy(static p => p.Pid),
            ("DISK", false) => source
                .OrderBy(static p => p.TotalDiskBytesPerSec)
                .ThenBy(static p => p.WorkingSetBytes)
                .ThenBy(static p => p.Pid),
            ("NAME", true) => source
                .OrderByDescending(static p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static p => p.Pid),
            ("NAME", false) => source
                .OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static p => p.Pid),
            ("PID", true) => source.OrderByDescending(static p => p.Pid),
            ("PID", false) => source.OrderBy(static p => p.Pid),
            (_, false) => source
                .OrderBy(static p => p.WorkingSetBytes)
                .ThenBy(static p => p.CpuPercent)
                .ThenBy(static p => p.Pid),
            _ => source
                .OrderByDescending(static p => p.WorkingSetBytes)
                .ThenByDescending(static p => p.CpuPercent)
                .ThenBy(static p => p.Pid)
        };

    private static IEnumerable<ProcessGroupSnapshot> SortGroups(
        IEnumerable<ProcessGroupSnapshot> source,
        string sortBy,
        bool sortDescending) =>
        (sortBy, sortDescending) switch
        {
            ("CPU", true) => source
                .OrderByDescending(static g => g.TotalCpuPercent)
                .ThenByDescending(static g => g.TotalWorkingSetBytes)
                .ThenBy(static g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static g => g.LowestPid),
            ("CPU", false) => source
                .OrderBy(static g => g.TotalCpuPercent)
                .ThenBy(static g => g.TotalWorkingSetBytes)
                .ThenBy(static g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static g => g.LowestPid),
            ("DISK", true) => source
                .OrderByDescending(static g => g.TotalDiskBytesPerSec)
                .ThenByDescending(static g => g.TotalWorkingSetBytes)
                .ThenBy(static g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static g => g.LowestPid),
            ("DISK", false) => source
                .OrderBy(static g => g.TotalDiskBytesPerSec)
                .ThenBy(static g => g.TotalWorkingSetBytes)
                .ThenBy(static g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static g => g.LowestPid),
            ("NAME", true) => source
                .OrderByDescending(static g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static g => g.LowestPid),
            ("NAME", false) => source
                .OrderBy(static g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static g => g.LowestPid),
            ("PID", true) => source
                .OrderByDescending(static g => g.LowestPid)
                .ThenBy(static g => g.DisplayName, StringComparer.OrdinalIgnoreCase),
            ("PID", false) => source
                .OrderBy(static g => g.LowestPid)
                .ThenBy(static g => g.DisplayName, StringComparer.OrdinalIgnoreCase),
            (_, false) => source
                .OrderBy(static g => g.TotalWorkingSetBytes)
                .ThenBy(static g => g.TotalCpuPercent)
                .ThenBy(static g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static g => g.LowestPid),
            _ => source
                .OrderByDescending(static g => g.TotalWorkingSetBytes)
                .ThenByDescending(static g => g.TotalCpuPercent)
                .ThenBy(static g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static g => g.LowestPid)
        };

    public void Dispose()
    {
        _pollTimer.Dispose();
        DetachUiMode();
    }
}
