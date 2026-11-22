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
    public ProcessRowViewModel(
        ProcessMetricsItem model,
        Action<ProcessMetricsItem> onRequestTerminate,
        Action<string?> onOpenFolder)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));

        Pid = model.Pid;
        PidText = model.Pid.ToString(CultureInfo.InvariantCulture);
        IsCriticalSystem = model.IsCriticalSystem;
        CanTerminate = !model.IsCriticalSystem;

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
        OpenFolderCommand = new RelayCommand(() => onOpenFolder(Model.ExecutablePath), () => HasExecutablePath);
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
    public ICommand OpenFolderCommand { get; }

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

public sealed partial class ProcessesViewModel : SectionViewModelBase, IDisposable
{
    private readonly IProcessMonitorService _processService;
    private readonly Action<Action> _uiDispatch;
    private readonly Timer _pollTimer;
    private IReadOnlyList<ProcessMetricsItem> _allProcesses = [];
    private ProcessMetricsItem? _pendingTerminateTarget;
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
        _pollTimer = new Timer(OnPollTick, null, Timeout.Infinite, Timeout.Infinite);

        if (autoLoadOnInit)
        {
            EnsureInitialized();
        }
    }

    public ObservableCollection<ProcessRowViewModel> Processes { get; }

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
                HiddenCriticalCount = items.Count(static p => p.IsCriticalSystem);
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
        var target = _pendingTerminateTarget;
        _pendingTerminateTarget = null;
        IsTerminateConfirmationOpen = false;
        TerminatePromptText = string.Empty;

        if (target is null)
        {
            return;
        }

        var result = await _processService.TerminateProcessAsync(target.Pid).ConfigureAwait(false);
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

    [RelayCommand]
    private void CancelTerminate()
    {
        _pendingTerminateTarget = null;
        IsTerminateConfirmationOpen = false;
        TerminatePromptText = string.Empty;
    }

    private void RequestTerminate(ProcessMetricsItem item)
    {
        if (item.IsCriticalSystem)
        {
            ActionFeedback = $"Процес «{item.Name}» (PID {item.Pid}) є критичним для Windows і захищений від завершення.";
            HasActionFeedback = true;
            return;
        }

        _pendingTerminateTarget = item;
        TerminatePromptText = $"Примусово завершити процес «{item.Name}» (PID {item.Pid})? Незбережені дані цієї програми буде втрачено.";
        IsTerminateConfirmationOpen = true;
    }

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
            query = query.Where(static p => !p.IsCriticalSystem);
        }

        if (!string.IsNullOrEmpty(q))
        {
            query = query.Where(p =>
                p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (p.ExecutablePath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        query = (SortBy, SortDescending) switch
        {
            ("CPU", true) => query
                .OrderByDescending(static p => p.CpuPercent)
                .ThenByDescending(static p => p.WorkingSetBytes)
                .ThenBy(static p => p.Pid),
            ("CPU", false) => query
                .OrderBy(static p => p.CpuPercent)
                .ThenBy(static p => p.WorkingSetBytes)
                .ThenBy(static p => p.Pid),
            ("DISK", true) => query
                .OrderByDescending(static p => p.TotalDiskBytesPerSec)
                .ThenByDescending(static p => p.WorkingSetBytes)
                .ThenBy(static p => p.Pid),
            ("DISK", false) => query
                .OrderBy(static p => p.TotalDiskBytesPerSec)
                .ThenBy(static p => p.WorkingSetBytes)
                .ThenBy(static p => p.Pid),
            ("NAME", true) => query
                .OrderByDescending(static p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static p => p.Pid),
            ("NAME", false) => query
                .OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static p => p.Pid),
            ("PID", true) => query.OrderByDescending(static p => p.Pid),
            ("PID", false) => query.OrderBy(static p => p.Pid),
            (_, false) => query
                .OrderBy(static p => p.WorkingSetBytes)
                .ThenBy(static p => p.CpuPercent)
                .ThenBy(static p => p.Pid),
            _ => query
                .OrderByDescending(static p => p.WorkingSetBytes)
                .ThenByDescending(static p => p.CpuPercent)
                .ThenBy(static p => p.Pid)
        };

        var visible = query.Take(160).ToList();
        CollectionSyncHelper.SyncByKey(
            Processes,
            visible,
            static m => m.Pid,
            static r => r.Pid,
            m => new ProcessRowViewModel(m, RequestTerminate, OpenFolder),
            static (r, m) => r.UpdateFrom(m),
            SelectedProcess,
            row => SelectedProcess = row);

        string hiddenSuffix = showCritical
            ? $" · Експертний режим (системні: {HiddenCriticalCount})"
            : $" · Сховано критичних системних: {HiddenCriticalCount} (увімкніть «Експертний режим» для показу)";

        SummaryText = $"Показано {Processes.Count} із {_allProcesses.Count} процесів{hiddenSuffix}";
    }

    public void Dispose()
    {
        _pollTimer.Dispose();
        DetachUiMode();
    }
}
