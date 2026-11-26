using System.Collections.ObjectModel;
using System.Globalization;
using Argus.App.Services;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Core.Extensions;
using Argus.Telemetry.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public enum ExpertInspectorTab
{
    Processes,
    Services,
    LocalPorts
}

public sealed class ProcessRowItem
{
    public ProcessRowItem(ProcessInspectItem item)
    {
        Pid = item.Pid;
        PidText = item.Pid.ToString(CultureInfo.InvariantCulture);
        Name = item.Name;
        WorkingSetBytes = item.WorkingSetBytes;
        double mb = item.WorkingSetBytes / (1024.0 * 1024.0);
        MemoryText = string.Format(CultureInfo.CurrentCulture, "{0:0.0} МБ", mb);
        ThreadCount = item.ThreadCount;
        ThreadsText = $"{item.ThreadCount} пот.";
        ExecutablePath = string.IsNullOrWhiteSpace(item.ExecutablePath)
            ? (item.IsSystemOrProtected ? "Системний / захищений процес" : "Шлях недоступний")
            : item.ExecutablePath;
        HasExecutablePath = !string.IsNullOrWhiteSpace(item.ExecutablePath);
        IsSystemOrProtected = item.IsSystemOrProtected;
    }

    public int Pid { get; }
    public string PidText { get; }
    public string Name { get; }
    public long WorkingSetBytes { get; }
    public string MemoryText { get; }
    public int ThreadCount { get; }
    public string ThreadsText { get; }
    public string ExecutablePath { get; }
    public bool HasExecutablePath { get; }
    public bool IsSystemOrProtected { get; }
}

public sealed class WindowsServiceRowItem
{
    public WindowsServiceRowItem(WindowsServiceInspectItem item)
    {
        ServiceName = item.ServiceName;
        DisplayName = item.DisplayName;
        Status = item.Status;
        StartType = item.StartType;
        CanStop = item.CanStop;
        IsRunning = item.Status.Contains("Працює", StringComparison.OrdinalIgnoreCase) ||
                    item.Status.Contains("Running", StringComparison.OrdinalIgnoreCase);
        IsStopped = item.Status.Contains("Зупинено", StringComparison.OrdinalIgnoreCase) ||
                    item.Status.Contains("Stopped", StringComparison.OrdinalIgnoreCase);
    }

    public string ServiceName { get; }
    public string DisplayName { get; }
    public string Status { get; }
    public string StartType { get; }
    public bool CanStop { get; }
    public bool IsRunning { get; }
    public bool IsStopped { get; }
}

public sealed class LocalPortRowItem
{
    public LocalPortRowItem(LocalPortInspectItem item)
    {
        Protocol = item.Protocol;
        EndpointText = $"{item.LocalAddress}:{item.LocalPort}";
        State = string.IsNullOrWhiteSpace(item.State) ? "—" : item.State;
        Pid = item.Pid;
        PidText = item.Pid.ToString(CultureInfo.InvariantCulture);
        ProcessName = item.ProcessName;
    }

    public string Protocol { get; }
    public string EndpointText { get; }
    public string State { get; }
    public int Pid { get; }
    public string PidText { get; }
    public string ProcessName { get; }
}

public sealed partial class CoreLoadItem : ObservableObject
{
    public CoreLoadItem(int index)
    {
        Index = index;
        Label = $"Потік {index}";
    }

    public int Index { get; }
    public string Label { get; }

    [ObservableProperty]
    private double _loadPercent;

    [ObservableProperty]
    private string _loadText = "0,0%";

    public void Update(double loadPercent)
    {
        LoadPercent = Math.Clamp(loadPercent, 0.0, 100.0);
        LoadText = string.Format(CultureInfo.CurrentCulture, "{0:0.0}%", LoadPercent);
    }
}

public sealed partial class StorageRowItem : ObservableObject
{
    public StorageRowItem(int diskNumber, string model)
    {
        DiskNumber = diskNumber;
        Title = $"Диск {diskNumber} · {model}";
    }

    public int DiskNumber { get; }
    public string Title { get; }

    [ObservableProperty]
    private string _temperatureText = "н/д";

    [ObservableProperty]
    private string _throughputText = "0,0 / 0,0 МБ/с";

    public void Update(StorageThermalSnapshot snap)
    {
        TemperatureText = snap.TemperatureCelsius.HasValue
            ? string.Format(CultureInfo.CurrentCulture, "{0:0} °C", snap.TemperatureCelsius.Value)
            : "н/д";

        double readMb = (snap.ReadBytesPerSec ?? 0.0) / (1024.0 * 1024.0);
        double writeMb = (snap.WriteBytesPerSec ?? 0.0) / (1024.0 * 1024.0);
        ThroughputText = string.Format(CultureInfo.CurrentCulture, "R {0:0.0} · W {1:0.0} МБ/с", readMb, writeMb);
    }
}

public sealed partial class ExtraSensorItem : ObservableObject
{
    public ExtraSensorItem(string sensorId, string name)
    {
        SensorId = sensorId;
        Name = name;
    }

    public string SensorId { get; }
    public string Name { get; }

    [ObservableProperty]
    private string _valueText = "—";

    public void Update(SensorReading reading)
    {
        ValueText = string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", reading.Value, reading.Unit);
    }
}

public sealed class GaugeConsumerItem
{
    public GaugeConsumerItem(
        int rank,
        int pid,
        string processName,
        string primaryMetricText,
        string secondaryMetricText,
        double barPercent,
        string? executablePath = null,
        bool isCriticalSystem = false,
        bool isProtectedAccess = false)
    {
        Rank = rank;
        RankText = $"#{rank}";
        Pid = pid;
        PidText = $"PID {pid}";
        ProcessName = processName;
        PrimaryMetricText = primaryMetricText;
        SecondaryMetricText = secondaryMetricText;
        BarPercent = Math.Clamp(barPercent, 0.0, 100.0);
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath;
        HasExecutablePath = !string.IsNullOrWhiteSpace(executablePath);
        IsCriticalSystem = isCriticalSystem || CriticalProcessProtection.IsCriticalProcess(pid, processName, Environment.ProcessId);
        IsProtectedAccess = isProtectedAccess;
        CanTerminate = !IsCriticalSystem;
    }

    public int Rank { get; }
    public string RankText { get; }
    public int Pid { get; }
    public string PidText { get; }
    public string ProcessName { get; }
    public string PrimaryMetricText { get; }
    public string SecondaryMetricText { get; }
    public double BarPercent { get; }
    public string? ExecutablePath { get; }
    public bool HasExecutablePath { get; }
    public bool IsCriticalSystem { get; }
    public bool IsProtectedAccess { get; }
    public bool CanTerminate { get; }
}

internal sealed record GaugeConsumerGroupSnapshot(
    string GroupKey,
    int Rank,
    string ProcessName,
    double BarPercent,
    string PrimaryMetricText,
    string SecondaryMetricText,
    string? ExecutablePath,
    bool IsCriticalSystem,
    IReadOnlyList<GaugeConsumerItem> Items);

public sealed partial class GaugeConsumerGroupViewModel : ObservableObject
{
    internal GaugeConsumerGroupViewModel(GaugeConsumerGroupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        GroupKey = snapshot.GroupKey;
        Items = [];
        UpdateFrom(snapshot);
    }

    public string GroupKey { get; }
    public ObservableCollection<GaugeConsumerItem> Items { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded;

    public string ExpandGlyph => IsExpanded ? "▼" : "▶";

    [ObservableProperty]
    private int _rank;

    [ObservableProperty]
    private string _rankText = string.Empty;

    [ObservableProperty]
    private string _processName = string.Empty;

    [ObservableProperty]
    private int _instanceCount;

    [ObservableProperty]
    private string _countBadgeText = string.Empty;

    [ObservableProperty]
    private bool _isMultiInstance;

    [ObservableProperty]
    private bool _isSingleInstance;

    [ObservableProperty]
    private string _pidOrCountText = string.Empty;

    [ObservableProperty]
    private double _barPercent;

    [ObservableProperty]
    private string _primaryMetricText = string.Empty;

    [ObservableProperty]
    private string _secondaryMetricText = string.Empty;

    [ObservableProperty]
    private string? _executablePath;

    [ObservableProperty]
    private bool _hasExecutablePath;

    [ObservableProperty]
    private bool _isCriticalSystem;

    [ObservableProperty]
    private bool _canTerminate;

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    internal void UpdateFrom(GaugeConsumerGroupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Rank = snapshot.Rank;
        RankText = $"#{snapshot.Rank}";
        ProcessName = snapshot.ProcessName;
        InstanceCount = snapshot.Items.Count;
        IsMultiInstance = InstanceCount > 1;
        IsSingleInstance = InstanceCount == 1;
        CountBadgeText = $"{InstanceCount} проц.";
        PidOrCountText = IsMultiInstance
            ? $"{InstanceCount} проц."
            : (snapshot.Items.Count == 1 ? $"PID {snapshot.Items[0].Pid}" : "0 проц.");
        BarPercent = Math.Clamp(snapshot.BarPercent, 0.0, 100.0);
        PrimaryMetricText = snapshot.PrimaryMetricText;
        SecondaryMetricText = snapshot.SecondaryMetricText;
        ExecutablePath = string.IsNullOrWhiteSpace(snapshot.ExecutablePath) ? null : snapshot.ExecutablePath;
        HasExecutablePath = !string.IsNullOrWhiteSpace(snapshot.ExecutablePath);
        IsCriticalSystem = snapshot.IsCriticalSystem;
        CanTerminate = snapshot.Items.Any(static i => i.CanTerminate);

        Items.Clear();
        foreach (var item in snapshot.Items)
        {
            Items.Add(item);
        }
    }
}

public sealed partial class MonitoringViewModel : SectionViewModelBase, IDisposable
{
    private static readonly TimeSpan GaugeConsumersRefreshInterval = TimeSpan.FromSeconds(2.5);

    private readonly IArgusSettingsStore _settingsStore;
    private readonly ITelemetryService _telemetryService;
    private readonly ISensorTierStatusProvider _tierStatusProvider;
    private readonly ITelemetryReportExporter? _reportExporter;
    private readonly ISystemInspectorService? _systemInspector;
    private readonly IProcessMonitorService? _processMonitor;
    private readonly IGpuProcessMetricsReader _gpuProcessMetricsReader;
    private readonly bool _ownsGpuMetricsReader;
    private readonly Action<Action> _uiDispatch;
    private readonly IDisposable _telemetrySubscription;
    private readonly IDisposable _settingsSubscription;
    private readonly IDisposable _alertSubscription;
    private volatile bool _isUiActive = true;
    private int _inspectorBusyGate;
    private int _gaugeConsumersBusyGate;
    private bool _inspectorLoadedOnce;
    private TelemetrySnapshot? _lastSnapshot;
    private DateTimeOffset _lastGaugeConsumersRefreshUtc = DateTimeOffset.MinValue;
    private IReadOnlyList<ProcessInspectItem> _allProcesses = [];
    private IReadOnlyList<WindowsServiceInspectItem> _allServices = [];
    private IReadOnlyList<LocalPortInspectItem> _allLocalPorts = [];

    public MonitoringViewModel(
        IArgusSettingsStore settingsStore,
        ITelemetryService telemetryService,
        ISensorTierStatusProvider tierStatusProvider,
        IArgusEventBus eventBus,
        Action<Action>? uiDispatch = null,
        ITelemetryReportExporter? reportExporter = null,
        AppUiModeService? uiMode = null,
        ISystemInspectorService? systemInspector = null,
        IProcessMonitorService? processMonitor = null,
        IGpuProcessMetricsReader? gpuProcessMetricsReader = null)
        : base(
            ArgusSectionId.Monitoring,
            "Моніторинг у реальному часі",
            "Жива телеметрія CPU, RAM, GPU, VRAM, векторні графіки Skia, сповіщення й експорт звітів",
            uiMode,
            uiDispatch)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _tierStatusProvider = tierStatusProvider ?? throw new ArgumentNullException(nameof(tierStatusProvider));
        ArgumentNullException.ThrowIfNull(eventBus);
        _uiDispatch = uiDispatch ?? (static action => action());
        _reportExporter = reportExporter;
        _systemInspector = systemInspector;
        _processMonitor = processMonitor;
        if (gpuProcessMetricsReader is not null)
        {
            _gpuProcessMetricsReader = gpuProcessMetricsReader;
            _ownsGpuMetricsReader = false;
        }
        else
        {
            _gpuProcessMetricsReader = new GpuProcessMetricsReader();
            _ownsGpuMetricsReader = true;
        }

        CoreLoads = [];
        StorageItems = [];
        ExtraSensors = [];
        GaugeConsumers = [];
        GaugeConsumerGroups = [];
        FilteredProcesses = [];
        FilteredServices = [];
        FilteredLocalPorts = [];

        RefreshSettingsLabels();
        RebuildChartSeries();

        if (_telemetryService.Latest is { } latest)
        {
            ApplySnapshot(latest);
        }

        _telemetrySubscription = eventBus.Subscribe<TelemetryUpdatedEvent>(e =>
        {
            if (!_isUiActive)
            {
                return;
            }

            _uiDispatch(() =>
            {
                if (_isUiActive)
                {
                    ApplySnapshot(e.Snapshot);
                }
            });
        });
        _settingsSubscription = eventBus.Subscribe<SettingsChangedEvent>(_ => _uiDispatch(RefreshSettingsLabels));
        _alertSubscription = eventBus.Subscribe<TelemetryAlertEvent>(e => _uiDispatch(() => ApplyAlert(e)));
    }

    public void SetUiActive(bool active)
    {
        bool wasActive = _isUiActive;
        _isUiActive = active;
        if (active && !wasActive)
        {
            if (_telemetryService.Latest is { } latest)
            {
                _uiDispatch(() => ApplySnapshot(latest));
            }

            if (IsExpertMode && !_inspectorLoadedOnce && _systemInspector is not null)
            {
                _ = RefreshInspectorAsync();
            }

            if (IsGaugeConsumersOpen)
            {
                _ = RefreshGaugeConsumersAsync();
            }
        }
    }

    protected override void OnExpertModeChanged(bool isExpertMode)
    {
        OnPropertyChanged(nameof(ShowGaugeConsumerUacCallout));
        OnPropertyChanged(nameof(ShowCpuThreadsPanel));
        OnPropertyChanged(nameof(ShowGpuDetailsPanel));
        if (isExpertMode)
        {
            if (_isUiActive && _systemInspector is not null)
            {
                _ = RefreshInspectorAsync();
            }
        }
        else
        {
            PendingTerminateProcess = null;
            ShowInspectorElevationCallout = false;
            PendingTerminateConsumer = null;
            PendingTerminateConsumerGroup = null;
            ShowGaugeConsumerElevationCallout = false;
        }
    }

    public ObservableCollection<CoreLoadItem> CoreLoads { get; }
    public ObservableCollection<StorageRowItem> StorageItems { get; }
    public ObservableCollection<ExtraSensorItem> ExtraSensors { get; }
    public ObservableCollection<GaugeConsumerItem> GaugeConsumers { get; }
    public ObservableCollection<GaugeConsumerGroupViewModel> GaugeConsumerGroups { get; }
    public ObservableCollection<ProcessRowItem> FilteredProcesses { get; }
    public ObservableCollection<WindowsServiceRowItem> FilteredServices { get; }
    public ObservableCollection<LocalPortRowItem> FilteredLocalPorts { get; }

    // Collapsible Hardware Cards (CPU Threads & GPU Adapter) — collapsed by default, gated to Expert mode in UI
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuThreadsChevron))]
    [NotifyPropertyChangedFor(nameof(ShowCpuThreadsPanel))]
    private bool _isCpuThreadsExpanded;

    public string CpuThreadsChevron => IsCpuThreadsExpanded ? "▼" : "▶";
    public bool ShowCpuThreadsPanel => IsExpertMode && IsCpuThreadsExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuDetailsChevron))]
    [NotifyPropertyChangedFor(nameof(ShowGpuDetailsPanel))]
    private bool _isGpuDetailsExpanded;

    public string GpuDetailsChevron => IsGpuDetailsExpanded ? "▼" : "▶";
    public bool ShowGpuDetailsPanel => IsExpertMode && IsGpuDetailsExpanded;

    // Interactive Gauge Top-Consumers Panel ("CPU", "RAM", "GPU", "TEMP", or null)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGaugeConsumersOpen))]
    [NotifyPropertyChangedFor(nameof(IsCpuGaugeSelected))]
    [NotifyPropertyChangedFor(nameof(IsRamGaugeSelected))]
    [NotifyPropertyChangedFor(nameof(IsGpuGaugeSelected))]
    [NotifyPropertyChangedFor(nameof(IsTempGaugeSelected))]
    private string? _activeGaugeConsumerKind;

    public bool IsGaugeConsumersOpen => !string.IsNullOrEmpty(ActiveGaugeConsumerKind);
    public bool IsCpuGaugeSelected => string.Equals(ActiveGaugeConsumerKind, "CPU", StringComparison.OrdinalIgnoreCase);
    public bool IsRamGaugeSelected => string.Equals(ActiveGaugeConsumerKind, "RAM", StringComparison.OrdinalIgnoreCase);
    public bool IsGpuGaugeSelected => string.Equals(ActiveGaugeConsumerKind, "GPU", StringComparison.OrdinalIgnoreCase);
    public bool IsTempGaugeSelected => string.Equals(ActiveGaugeConsumerKind, "TEMP", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private string _gaugeConsumersTitle = string.Empty;

    [ObservableProperty]
    private string _gaugeConsumersSubtitle = string.Empty;

    [ObservableProperty]
    private bool _isGaugeConsumersLoading;

    [ObservableProperty]
    private bool _hasGaugeConsumers;

    [ObservableProperty]
    private string _gaugeConsumersEmptyText = "Немає активних процесів для відображення";

    // Expert process actions inside Top-Consumers Panel
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingGaugeConsumerTerminate))]
    [NotifyPropertyChangedFor(nameof(PendingGaugeConsumerTerminatePrompt))]
    private GaugeConsumerItem? _pendingTerminateConsumer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingGaugeConsumerTerminate))]
    [NotifyPropertyChangedFor(nameof(PendingGaugeConsumerTerminatePrompt))]
    private GaugeConsumerGroupViewModel? _pendingTerminateConsumerGroup;

    public bool HasPendingGaugeConsumerTerminate => PendingTerminateConsumer is not null || PendingTerminateConsumerGroup is not null;

    public string PendingGaugeConsumerTerminatePrompt
    {
        get
        {
            if (PendingTerminateConsumer is { } p)
            {
                return $"Завершити процес «{p.ProcessName}» (PID {p.Pid})?";
            }

            if (PendingTerminateConsumerGroup is { } g)
            {
                int killable = g.Items.Count(static i => i.CanTerminate && !CriticalProcessProtection.IsCriticalProcess(i.Pid, i.ProcessName, Environment.ProcessId));
                int skipped = g.Items.Count - killable;
                return skipped > 0
                    ? $"Завершити {killable} некритичних процесів «{g.ProcessName}» ({skipped} системних буде пропущено)?"
                    : $"Завершити усі {killable} процесів «{g.ProcessName}»?";
            }

            return string.Empty;
        }
    }

    [ObservableProperty]
    private bool _hasGaugeConsumerFeedback;

    [ObservableProperty]
    private string _gaugeConsumerFeedbackText = string.Empty;

    [ObservableProperty]
    private bool _gaugeConsumerFeedbackIsSuccess = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGaugeConsumerUacCallout))]
    private bool _showGaugeConsumerElevationCallout;

    [ObservableProperty]
    private string _gaugeConsumerElevationReason = "Для завершення захищених процесів потрібне підвищення прав (UAC).";

    public bool ShowGaugeConsumerUacCallout => IsExpertMode && !IsAdministrator && ShowGaugeConsumerElevationCallout;

    // Expert Diagnostics State (loaded lazily only when IsExpertMode is ON)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProcessesTab))]
    [NotifyPropertyChangedFor(nameof(IsServicesTab))]
    [NotifyPropertyChangedFor(nameof(IsLocalPortsTab))]
    [NotifyPropertyChangedFor(nameof(ShowContextualInspectorUac))]
    private ExpertInspectorTab _activeInspectorTab = ExpertInspectorTab.Processes;

    public bool IsProcessesTab => ActiveInspectorTab == ExpertInspectorTab.Processes;
    public bool IsServicesTab => ActiveInspectorTab == ExpertInspectorTab.Services;
    public bool IsLocalPortsTab => ActiveInspectorTab == ExpertInspectorTab.LocalPorts;

    [ObservableProperty]
    private string _inspectorSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _isInspectorLoading;

    [ObservableProperty]
    private string _processesTabLabel = "Процеси (PID)";

    [ObservableProperty]
    private string _servicesTabLabel = "Служби Windows";

    [ObservableProperty]
    private string _localPortsTabLabel = "Локальні порти";

    [ObservableProperty]
    private string _inspectorSummaryText = "Натисніть «Оновити», щоб прочитати процеси, служби та локальні порти";

    [ObservableProperty]
    private bool _hasInspectorFeedback;

    [ObservableProperty]
    private string _inspectorFeedbackText = string.Empty;

    [ObservableProperty]
    private bool _inspectorFeedbackIsSuccess = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContextualInspectorUac))]
    private bool _showInspectorElevationCallout;

    [ObservableProperty]
    private string _inspectorElevationReason = "Перегляд процесів, служб і портів працює без прав адміністратора. Для керування службами Windows або завершення захищених процесів потрібне підвищення прав (UAC).";

    public bool ShowContextualInspectorUac => !IsAdministrator && (IsServicesTab || ShowInspectorElevationCallout);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingTerminate))]
    [NotifyPropertyChangedFor(nameof(PendingTerminatePrompt))]
    private ProcessRowItem? _pendingTerminateProcess;

    public bool HasPendingTerminate => PendingTerminateProcess is not null;

    public string PendingTerminatePrompt => PendingTerminateProcess is { } p
        ? $"Завершити процес «{p.Name}» (PID {p.Pid})?"
        : string.Empty;

    partial void OnInspectorSearchQueryChanged(string value)
    {
        ApplyInspectorFilter();
    }

    [ObservableProperty]
    private string _samplingSummary = string.Empty;

    [ObservableProperty]
    private string _historySummary = string.Empty;

    [ObservableProperty]
    private string _sensorModeSummary = string.Empty;

    [ObservableProperty]
    private bool _hasLiveSample;

    [ObservableProperty]
    private string _lastUpdatedText = "Очікування першого зразка…";

    // Live Skia Chart Series & Time Window
    [ObservableProperty]
    private int _selectedWindowSeconds = 60;

    public bool IsWindow30Selected => SelectedWindowSeconds == 30;
    public bool IsWindow60Selected => SelectedWindowSeconds == 60;
    public bool IsWindow180Selected => SelectedWindowSeconds == 180;
    public bool IsWindow300Selected => SelectedWindowSeconds == 300;

    [ObservableProperty]
    private IReadOnlyList<double> _cpuSeries = [];

    [ObservableProperty]
    private IReadOnlyList<double> _ramSeries = [];

    [ObservableProperty]
    private IReadOnlyList<double> _gpuSeries = [];

    [ObservableProperty]
    private IReadOnlyList<double> _tempSeries = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreAllSeriesVisible))]
    private bool _showCpuSeries = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreAllSeriesVisible))]
    private bool _showRamSeries = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreAllSeriesVisible))]
    private bool _showGpuSeries = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreAllSeriesVisible))]
    private bool _showTempSeries = true;

    public bool AreAllSeriesVisible => ShowCpuSeries && ShowRamSeries && ShowGpuSeries && ShowTempSeries;

    [ObservableProperty]
    private double? _activeAlertThreshold = 85.0;

    // Alert Banner & Report Export State
    [ObservableProperty]
    private bool _hasActiveAlert;

    [ObservableProperty]
    private string _activeAlertText = string.Empty;

    [ObservableProperty]
    private bool _hasExportFeedback;

    [ObservableProperty]
    private string _exportFeedback = string.Empty;

    [ObservableProperty]
    private bool _exportSucceeded = true;

    [ObservableProperty]
    private bool _isExportingReport;

    // CPU Gauge & Header
    [ObservableProperty]
    private bool _cpuStandby = true;

    [ObservableProperty]
    private double _cpuLoadValue;

    [ObservableProperty]
    private string _cpuDisplayValue = "—";

    [ObservableProperty]
    private string _cpuSubLabel = $"{Environment.ProcessorCount} потоків";

    [ObservableProperty]
    private string _cpuModelName = "Визначення процесора…";

    [ObservableProperty]
    private string _cpuClockAndTopologyText = $"{Environment.ProcessorCount} логічних потоків";

    // RAM Gauge
    [ObservableProperty]
    private bool _ramStandby = true;

    [ObservableProperty]
    private double _ramLoadValue;

    [ObservableProperty]
    private string _ramDisplayValue = "—";

    [ObservableProperty]
    private string _ramSubLabel = "Ініціалізація Win32…";

    // GPU Gauge & Detail
    [ObservableProperty]
    private bool _gpuStandby = true;

    [ObservableProperty]
    private double _gpuLoadValue;

    [ObservableProperty]
    private string _gpuDisplayValue = "—";

    [ObservableProperty]
    private string _gpuUnit = "%";

    [ObservableProperty]
    private string _gpuSubLabel = "Опитування NVML / WDDM…";

    [ObservableProperty]
    private string _gpuNameText = "Відеоадаптер не визначено";

    [ObservableProperty]
    private string _gpuVramDetailText = "VRAM: н/д";

    [ObservableProperty]
    private string _gpuClockAndFanText = "Частота: н/д";

    // Peak Thermal Gauge
    [ObservableProperty]
    private bool _tempStandby = true;

    [ObservableProperty]
    private double _tempValue;

    [ObservableProperty]
    private string _tempDisplayValue = "—";

    [ObservableProperty]
    private string _tempSubLabel = "GPU / накопичувачі / ACPI";

    [ObservableProperty]
    private bool _hasExtraSensors;

    [RelayCommand]
    private void ToggleCpuThreads()
    {
        IsCpuThreadsExpanded = !IsCpuThreadsExpanded;
    }

    [RelayCommand]
    private void ToggleGpuDetails()
    {
        IsGpuDetailsExpanded = !IsGpuDetailsExpanded;
    }

    [RelayCommand]
    private void SelectGaugeConsumerKind(string? kind)
    {
        string normalized = NormalizeGaugeKind(kind);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        if (string.Equals(ActiveGaugeConsumerKind, normalized, StringComparison.Ordinal))
        {
            CloseGaugeConsumers();
            return;
        }

        PendingTerminateConsumer = null;
        PendingTerminateConsumerGroup = null;
        GaugeConsumers.Clear();
        GaugeConsumerGroups.Clear();
        ActiveGaugeConsumerKind = normalized;
        ApplyGaugeConsumersHeaderDefaults(normalized);
        _lastGaugeConsumersRefreshUtc = DateTimeOffset.MinValue;
        _ = RefreshGaugeConsumersAsync();
    }

    [RelayCommand]
    private void CloseGaugeConsumers()
    {
        ActiveGaugeConsumerKind = null;
        GaugeConsumers.Clear();
        GaugeConsumerGroups.Clear();
        HasGaugeConsumers = false;
        IsGaugeConsumersLoading = false;
        PendingTerminateConsumer = null;
        PendingTerminateConsumerGroup = null;
        ShowGaugeConsumerElevationCallout = false;
        HasGaugeConsumerFeedback = false;
        GaugeConsumerFeedbackText = string.Empty;
    }

    [RelayCommand]
    private void OpenGaugeConsumerFolder(GaugeConsumerItem? item)
    {
        if (!IsExpertMode || item is null || !item.HasExecutablePath)
        {
            return;
        }

        OpenGaugeConsumerFolderCore(item.ProcessName, item.ExecutablePath);
    }

    [RelayCommand]
    private void OpenGaugeConsumerGroupFolder(GaugeConsumerGroupViewModel? group)
    {
        if (!IsExpertMode || group is null || !group.HasExecutablePath)
        {
            return;
        }

        OpenGaugeConsumerFolderCore(group.ProcessName, group.ExecutablePath);
    }

    private void OpenGaugeConsumerFolderCore(string processName, string? executablePath)
    {
        bool opened;
        if (_systemInspector is not null)
        {
            opened = _systemInspector.OpenProcessFolder(executablePath);
        }
        else if (_processMonitor is not null)
        {
            opened = _processMonitor.OpenProcessFolder(executablePath);
        }
        else
        {
            return;
        }

        GaugeConsumerFeedbackIsSuccess = opened;
        GaugeConsumerFeedbackText = opened
            ? $"Відкрито папку процесу «{processName}»."
            : $"Не вдалося відкрити папку для «{processName}».";
        HasGaugeConsumerFeedback = true;
    }

    [RelayCommand]
    private void RequestTerminateGaugeConsumer(GaugeConsumerItem? item)
    {
        if (!IsExpertMode || item is null)
        {
            return;
        }

        if (!item.CanTerminate || CriticalProcessProtection.IsCriticalProcess(item.Pid, item.ProcessName, Environment.ProcessId))
        {
            PendingTerminateConsumer = null;
            PendingTerminateConsumerGroup = null;
            GaugeConsumerFeedbackIsSuccess = false;
            GaugeConsumerFeedbackText = $"Процес «{item.ProcessName}» (PID {item.Pid}) є критичним системним процесом і захищений від завершення.";
            HasGaugeConsumerFeedback = true;
            return;
        }

        PendingTerminateConsumerGroup = null;
        PendingTerminateConsumer = item;
    }

    [RelayCommand]
    private void RequestTerminateGaugeConsumerGroup(GaugeConsumerGroupViewModel? group)
    {
        if (!IsExpertMode || group is null)
        {
            return;
        }

        if (group.IsSingleInstance && group.Items.Count == 1)
        {
            RequestTerminateGaugeConsumer(group.Items[0]);
            return;
        }

        bool anyKillable = group.Items.Any(static i =>
            i.CanTerminate && !CriticalProcessProtection.IsCriticalProcess(i.Pid, i.ProcessName, Environment.ProcessId));
        if (!anyKillable)
        {
            PendingTerminateConsumer = null;
            PendingTerminateConsumerGroup = null;
            GaugeConsumerFeedbackIsSuccess = false;
            GaugeConsumerFeedbackText = $"Усі процеси у групі «{group.ProcessName}» є критичними системними і захищені від завершення.";
            HasGaugeConsumerFeedback = true;
            return;
        }

        PendingTerminateConsumer = null;
        PendingTerminateConsumerGroup = group;
    }

    [RelayCommand]
    private void CancelTerminateGaugeConsumer()
    {
        PendingTerminateConsumer = null;
        PendingTerminateConsumerGroup = null;
    }

    [RelayCommand]
    private async Task ConfirmTerminateGaugeConsumerAsync()
    {
        var target = PendingTerminateConsumer;
        var groupTarget = PendingTerminateConsumerGroup;
        PendingTerminateConsumer = null;
        PendingTerminateConsumerGroup = null;

        if (!IsExpertMode || (target is null && groupTarget is null))
        {
            return;
        }

        if (target is not null)
        {
            if (!target.CanTerminate || CriticalProcessProtection.IsCriticalProcess(target.Pid, target.ProcessName, Environment.ProcessId))
            {
                _uiDispatch(() =>
                {
                    GaugeConsumerFeedbackIsSuccess = false;
                    GaugeConsumerFeedbackText = $"Процес «{target.ProcessName}» (PID {target.Pid}) є критичним системним процесом і захищений від завершення.";
                    HasGaugeConsumerFeedback = true;
                });
                return;
            }

            var result = await TerminatePidCoreAsync(target.Pid).ConfigureAwait(false);
            if (result is null)
            {
                return;
            }

            _uiDispatch(() =>
            {
                GaugeConsumerFeedbackIsSuccess = result.Succeeded;
                GaugeConsumerFeedbackText = result.Message;
                HasGaugeConsumerFeedback = true;

                if (result.RequiresElevation)
                {
                    ShowGaugeConsumerElevationCallout = true;
                    GaugeConsumerElevationReason = result.Message;
                }
                else if (result.Succeeded)
                {
                    ShowGaugeConsumerElevationCallout = false;
                }
            });

            if (result.Succeeded)
            {
                await RefreshGaugeConsumersAsync().ConfigureAwait(false);
            }

            return;
        }

        if (groupTarget is not null)
        {
            var killable = groupTarget.Items
                .Where(static i => i.CanTerminate && !CriticalProcessProtection.IsCriticalProcess(i.Pid, i.ProcessName, Environment.ProcessId))
                .ToList();
            int skippedCritical = groupTarget.Items.Count - killable.Count;

            if (killable.Count == 0)
            {
                _uiDispatch(() =>
                {
                    GaugeConsumerFeedbackIsSuccess = false;
                    GaugeConsumerFeedbackText = $"Усі процеси у групі «{groupTarget.ProcessName}» є критичними системними і захищені від завершення.";
                    HasGaugeConsumerFeedback = true;
                });
                return;
            }

            if (_systemInspector is null && _processMonitor is null)
            {
                return;
            }

            int succeededCount = 0;
            int failedCount = 0;
            bool requiresElevation = false;
            string? lastError = null;

            foreach (var item in killable)
            {
                var result = await TerminatePidCoreAsync(item.Pid).ConfigureAwait(false);
                if (result is null)
                {
                    return;
                }

                if (result.Succeeded)
                {
                    succeededCount++;
                }
                else
                {
                    failedCount++;
                    lastError = result.Message;
                    if (result.RequiresElevation)
                    {
                        requiresElevation = true;
                    }
                }
            }

            string feedback = (failedCount == 0, succeededCount > 0, skippedCritical > 0) switch
            {
                (true, true, false) => $"Завершено {succeededCount} процесів «{groupTarget.ProcessName}».",
                (true, true, true) => $"Завершено {succeededCount} процесів «{groupTarget.ProcessName}» (пропущено критичних: {skippedCritical}).",
                (false, true, _) => $"Завершено {succeededCount} із {killable.Count} процесів «{groupTarget.ProcessName}» (помилок: {failedCount}).",
                _ => string.IsNullOrWhiteSpace(lastError)
                    ? $"Не вдалося завершити процеси «{groupTarget.ProcessName}»."
                    : lastError
            };

            _uiDispatch(() =>
            {
                GaugeConsumerFeedbackIsSuccess = failedCount == 0 && succeededCount > 0;
                GaugeConsumerFeedbackText = feedback;
                HasGaugeConsumerFeedback = true;

                if (requiresElevation)
                {
                    ShowGaugeConsumerElevationCallout = true;
                    GaugeConsumerElevationReason = lastError ?? "Для завершення захищених процесів потрібне підвищення прав (UAC).";
                }
                else if (failedCount == 0 && succeededCount > 0)
                {
                    ShowGaugeConsumerElevationCallout = false;
                }
            });

            if (succeededCount > 0)
            {
                await RefreshGaugeConsumersAsync().ConfigureAwait(false);
            }
        }
    }

    private Task<SystemActionResult?> TerminatePidCoreAsync(int pid)
    {
        if (_systemInspector is not null)
        {
            return WrapResultAsync(_systemInspector.TerminateProcessAsync(pid));
        }

        if (_processMonitor is not null)
        {
            return WrapResultAsync(_processMonitor.TerminateProcessAsync(pid));
        }

        return Task.FromResult<SystemActionResult?>(null);

        static async Task<SystemActionResult?> WrapResultAsync(Task<SystemActionResult> task) =>
            await task.ConfigureAwait(false);
    }

    public async Task RefreshGaugeConsumersAsync(CancellationToken cancellationToken = default)
    {
        string? requestedKind = ActiveGaugeConsumerKind;
        if (string.IsNullOrEmpty(requestedKind))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _gaugeConsumersBusyGate, 1, 0) != 0)
        {
            return;
        }

        _uiDispatch(() => IsGaugeConsumersLoading = true);
        try
        {
            var snapshot = _lastSnapshot;
            if (_processMonitor is null)
            {
                _uiDispatch(() =>
                {
                    if (!string.Equals(ActiveGaugeConsumerKind, requestedKind, StringComparison.Ordinal))
                    {
                        return;
                    }

                    ApplyGaugeConsumersHeaderDefaults(requestedKind);
                    GaugeConsumers.Clear();
                    GaugeConsumerGroups.Clear();
                    HasGaugeConsumers = false;
                    GaugeConsumersEmptyText = "Служба моніторингу процесів недоступна.";
                });
                return;
            }

            var processes = await _processMonitor.GetProcessMetricsAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<int, double>? gpuUsage = null;
            if (string.Equals(requestedKind, "GPU", StringComparison.Ordinal) ||
                string.Equals(requestedKind, "TEMP", StringComparison.Ordinal))
            {
                gpuUsage = await Task.Run(
                    () => _gpuProcessMetricsReader.SamplePerProcessGpuUtilization(),
                    cancellationToken).ConfigureAwait(false);
            }

            var (title, subtitle, items, groups) = BuildGaugeConsumersAndGroups(requestedKind, processes, gpuUsage, snapshot);
            _lastGaugeConsumersRefreshUtc = DateTimeOffset.UtcNow;

            _uiDispatch(() =>
            {
                if (!string.Equals(ActiveGaugeConsumerKind, requestedKind, StringComparison.Ordinal))
                {
                    return;
                }

                GaugeConsumersTitle = title;
                GaugeConsumersSubtitle = subtitle;
                GaugeConsumers.Clear();
                foreach (var item in items)
                {
                    GaugeConsumers.Add(item);
                }

                CollectionSyncHelper.SyncByKey(
                    GaugeConsumerGroups,
                    groups,
                    static g => g.GroupKey,
                    static vm => vm.GroupKey,
                    static g => new GaugeConsumerGroupViewModel(g),
                    static (vm, g) => vm.UpdateFrom(g),
                    currentSelection: null,
                    setSelection: static _ => { },
                    StringComparer.OrdinalIgnoreCase);

                HasGaugeConsumers = GaugeConsumerGroups.Count > 0 || GaugeConsumers.Count > 0;
                if (!HasGaugeConsumers)
                {
                    GaugeConsumersEmptyText = "Немає активних процесів для відображення.";
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation.
        }
        catch
        {
            _uiDispatch(() =>
            {
                if (!string.Equals(ActiveGaugeConsumerKind, requestedKind, StringComparison.Ordinal))
                {
                    return;
                }

                GaugeConsumers.Clear();
                GaugeConsumerGroups.Clear();
                HasGaugeConsumers = false;
                GaugeConsumersEmptyText = "Не вдалося отримати список процесів.";
            });
        }
        finally
        {
            Interlocked.Exchange(ref _gaugeConsumersBusyGate, 0);
            _uiDispatch(() => IsGaugeConsumersLoading = false);

            // If the user switched to another gauge while this refresh was running, refresh for the new kind.
            string? currentKind = ActiveGaugeConsumerKind;
            if (!string.IsNullOrEmpty(currentKind) &&
                !string.Equals(currentKind, requestedKind, StringComparison.Ordinal))
            {
                _ = RefreshGaugeConsumersAsync();
            }
        }
    }

    private static string NormalizeGaugeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return string.Empty;
        }

        return kind.Trim().ToUpperInvariant() switch
        {
            "CPU" => "CPU",
            "RAM" => "RAM",
            "GPU" => "GPU",
            "TEMP" => "TEMP",
            _ => string.Empty
        };
    }

    private void ApplyGaugeConsumersHeaderDefaults(string kind)
    {
        switch (kind)
        {
            case "CPU":
                GaugeConsumersTitle = "Топ-споживачі CPU";
                GaugeConsumersSubtitle = "Процеси з найбільшим навантаженням на процесор";
                break;
            case "RAM":
                GaugeConsumersTitle = "Топ-споживачі оперативної пам'яті";
                GaugeConsumersSubtitle = "Процеси з найбільшим споживанням ОЗП (Working Set)";
                break;
            case "GPU":
                GaugeConsumersTitle = "Топ-споживачі GPU";
                GaugeConsumersSubtitle = "Опитування лічильників GPU Engine по процесах…";
                break;
            case "TEMP":
                GaugeConsumersTitle = "Джерела нагріву системи (Температура)";
                GaugeConsumersSubtitle = $"{BuildThermalSummaryText(_lastSnapshot)} · Топ процесів за навантаженням CPU / GPU";
                break;
        }
    }

    internal static string NormalizeConsumerGroupKey(string? name)
    {
        string trimmed = string.IsNullOrWhiteSpace(name) ? "(невідомо)" : name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 4)
        {
            trimmed = trimmed[..^4];
        }

        return trimmed.ToLowerInvariant();
    }

    private static (string Title, string Subtitle, List<GaugeConsumerItem> Items, List<GaugeConsumerGroupSnapshot> Groups) BuildGaugeConsumersAndGroups(
        string kind,
        IReadOnlyList<ProcessMetricsItem> processes,
        IReadOnlyDictionary<int, double>? gpuUsage,
        TelemetrySnapshot? snapshot)
    {
        const int maxItems = 6;
        var items = new List<GaugeConsumerItem>(maxItems);
        var groups = new List<GaugeConsumerGroupSnapshot>(maxItems);

        switch (kind)
        {
            case "CPU":
            {
                var ordered = processes
                    .Where(p => p.Pid > 0)
                    .OrderByDescending(p => p.CpuPercent)
                    .ThenByDescending(p => p.WorkingSetBytes)
                    .ThenBy(p => p.Pid)
                    .ToList();

                int rank = 1;
                foreach (var p in ordered.Take(maxItems))
                {
                    items.Add(CreateCpuConsumerItem(rank++, p));
                }

                var grouped = ordered
                    .GroupBy(static p => NormalizeConsumerGroupKey(p.Name), StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var instances = g.ToList();
                        double totalCpu = Math.Clamp(instances.Sum(static p => Math.Max(0.0, p.CpuPercent)), 0.0, 100.0);
                        long totalRam = instances.Sum(static p => Math.Max(0L, p.WorkingSetBytes));
                        int totalThreads = instances.Sum(static p => Math.Max(0, p.ThreadCount));
                        return (Key: g.Key, Instances: instances, TotalCpu: totalCpu, TotalRam: totalRam, TotalThreads: totalThreads);
                    })
                    .OrderByDescending(x => x.TotalCpu)
                    .ThenByDescending(x => x.TotalRam)
                    .ThenBy(x => x.Instances[0].Name, StringComparer.OrdinalIgnoreCase)
                    .Take(maxItems);

                int groupRank = 1;
                foreach (var entry in grouped)
                {
                    var childItems = entry.Instances
                        .Select((p, idx) => CreateCpuConsumerItem(idx + 1, p))
                        .ToList();
                    groups.Add(CreateGroupSnapshot(
                        entry.Key,
                        groupRank++,
                        entry.Instances,
                        entry.TotalCpu,
                        string.Format(CultureInfo.CurrentCulture, "{0:0.0}%", entry.TotalCpu),
                        $"{FormatMemorySize(entry.TotalRam)} · {entry.TotalThreads} пот.",
                        childItems));
                }

                return ("Топ-споживачі CPU", "Процеси з найбільшим навантаженням на процесор", items, groups);
            }

            case "RAM":
            {
                var ordered = processes
                    .Where(p => p.Pid > 0)
                    .OrderByDescending(p => p.WorkingSetBytes)
                    .ThenByDescending(p => p.CpuPercent)
                    .ThenBy(p => p.Pid)
                    .ToList();

                var top = ordered.Take(maxItems).ToList();
                long totalRamBytes = snapshot?.Memory.TotalBytes ?? 0;
                long peakWorkingSet = top.Count > 0 ? Math.Max(1L, top[0].WorkingSetBytes) : 1L;

                int rank = 1;
                foreach (var p in top)
                {
                    items.Add(CreateRamConsumerItem(rank++, p, totalRamBytes, peakWorkingSet));
                }

                var grouped = ordered
                    .GroupBy(static p => NormalizeConsumerGroupKey(p.Name), StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var instances = g.ToList();
                        long totalRam = instances.Sum(static p => Math.Max(0L, p.WorkingSetBytes));
                        double totalCpu = Math.Clamp(instances.Sum(static p => Math.Max(0.0, p.CpuPercent)), 0.0, 100.0);
                        int totalThreads = instances.Sum(static p => Math.Max(0, p.ThreadCount));
                        return (Key: g.Key, Instances: instances, TotalRam: totalRam, TotalCpu: totalCpu, TotalThreads: totalThreads);
                    })
                    .OrderByDescending(x => x.TotalRam)
                    .ThenByDescending(x => x.TotalCpu)
                    .ThenBy(x => x.Instances[0].Name, StringComparer.OrdinalIgnoreCase)
                    .Take(maxItems)
                    .ToList();

                long peakGroupRam = grouped.Count > 0 ? Math.Max(1L, grouped[0].TotalRam) : 1L;
                int groupRank = 1;
                foreach (var entry in grouped)
                {
                    double barPct = totalRamBytes > 0
                        ? Math.Clamp((double)entry.TotalRam / totalRamBytes * 100.0, 0.0, 100.0)
                        : Math.Clamp((double)entry.TotalRam / peakGroupRam * 100.0, 0.0, 100.0);
                    long childPeakRam = Math.Max(1L, entry.Instances[0].WorkingSetBytes);
                    var childItems = entry.Instances
                        .Select((p, idx) => CreateRamConsumerItem(idx + 1, p, totalRamBytes, childPeakRam))
                        .ToList();
                    groups.Add(CreateGroupSnapshot(
                        entry.Key,
                        groupRank++,
                        entry.Instances,
                        barPct,
                        FormatMemorySize(entry.TotalRam),
                        string.Format(CultureInfo.CurrentCulture, "CPU {0:0.0}% · {1} пот.", entry.TotalCpu, entry.TotalThreads),
                        childItems));
                }

                return ("Топ-споживачі оперативної пам'яті", "Процеси з найбільшим споживанням ОЗП (Working Set)", items, groups);
            }

            case "GPU":
            {
                var withGpu = new List<(ProcessMetricsItem Process, double GpuPercent)>();
                if (gpuUsage is { Count: > 0 })
                {
                    foreach (var p in processes)
                    {
                        if (p.Pid > 0 && gpuUsage.TryGetValue(p.Pid, out double gpuPct) && gpuPct > 0.05)
                        {
                            withGpu.Add((p, Math.Clamp(gpuPct, 0.0, 100.0)));
                        }
                    }
                }

                if (withGpu.Count > 0)
                {
                    var orderedGpu = withGpu
                        .OrderByDescending(x => x.GpuPercent)
                        .ThenByDescending(x => x.Process.WorkingSetBytes)
                        .ThenBy(x => x.Process.Pid)
                        .ToList();

                    int rank = 1;
                    foreach (var entry in orderedGpu.Take(maxItems))
                    {
                        items.Add(CreateGpuConsumerItem(rank++, entry.Process, entry.GpuPercent));
                    }

                    var groupedGpu = orderedGpu
                        .GroupBy(static x => NormalizeConsumerGroupKey(x.Process.Name), StringComparer.OrdinalIgnoreCase)
                        .Select(g =>
                        {
                            var entries = g.ToList();
                            double totalGpu = Math.Clamp(entries.Sum(static x => x.GpuPercent), 0.0, 100.0);
                            double totalCpu = Math.Clamp(entries.Sum(static x => Math.Max(0.0, x.Process.CpuPercent)), 0.0, 100.0);
                            long totalRam = entries.Sum(static x => Math.Max(0L, x.Process.WorkingSetBytes));
                            return (Key: g.Key, Entries: entries, TotalGpu: totalGpu, TotalCpu: totalCpu, TotalRam: totalRam);
                        })
                        .OrderByDescending(x => x.TotalGpu)
                        .ThenByDescending(x => x.TotalRam)
                        .ThenBy(x => x.Entries[0].Process.Name, StringComparer.OrdinalIgnoreCase)
                        .Take(maxItems);

                    int groupRank = 1;
                    foreach (var entry in groupedGpu)
                    {
                        var instances = entry.Entries.Select(static x => x.Process).ToList();
                        var childItems = entry.Entries
                            .Select((x, idx) => CreateGpuConsumerItem(idx + 1, x.Process, x.GpuPercent))
                            .ToList();
                        groups.Add(CreateGroupSnapshot(
                            entry.Key,
                            groupRank++,
                            instances,
                            entry.TotalGpu,
                            string.Format(CultureInfo.CurrentCulture, "GPU {0:0.0}%", entry.TotalGpu),
                            string.Format(CultureInfo.CurrentCulture, "CPU {0:0.0}% · {1}", entry.TotalCpu, FormatMemorySize(entry.TotalRam)),
                            childItems));
                    }

                    return ("Топ-споживачі GPU", "Навантаження GPU по процесах (Windows WDDM GPU Engine)", items, groups);
                }

                // Graceful fallback when per-PID GPU counters are unavailable or all 0%
                var orderedFallback = processes
                    .Where(p => p.Pid > 0)
                    .OrderByDescending(p => p.CpuPercent)
                    .ThenByDescending(p => p.WorkingSetBytes)
                    .ThenBy(p => p.Pid)
                    .ToList();

                var fallback = orderedFallback.Take(maxItems).ToList();
                long maxMem = fallback.Count > 0 ? Math.Max(1L, fallback.Max(p => p.WorkingSetBytes)) : 1L;
                int fbRank = 1;
                foreach (var p in fallback)
                {
                    items.Add(CreateGpuFallbackConsumerItem(fbRank++, p, maxMem));
                }

                var groupedFallback = orderedFallback
                    .GroupBy(static p => NormalizeConsumerGroupKey(p.Name), StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var instances = g.ToList();
                        double totalCpu = Math.Clamp(instances.Sum(static p => Math.Max(0.0, p.CpuPercent)), 0.0, 100.0);
                        long totalRam = instances.Sum(static p => Math.Max(0L, p.WorkingSetBytes));
                        return (Key: g.Key, Instances: instances, TotalCpu: totalCpu, TotalRam: totalRam);
                    })
                    .OrderByDescending(x => x.TotalCpu)
                    .ThenByDescending(x => x.TotalRam)
                    .ThenBy(x => x.Instances[0].Name, StringComparer.OrdinalIgnoreCase)
                    .Take(maxItems)
                    .ToList();

                long maxGroupMem = groupedFallback.Count > 0 ? Math.Max(1L, groupedFallback.Max(g => g.TotalRam)) : 1L;
                int fbGroupRank = 1;
                foreach (var entry in groupedFallback)
                {
                    double barPct = entry.TotalCpu > 0.1
                        ? Math.Clamp(entry.TotalCpu, 0.0, 100.0)
                        : Math.Clamp((double)entry.TotalRam / maxGroupMem * 100.0, 0.0, 100.0);
                    long childMaxMem = Math.Max(1L, entry.Instances.Max(p => p.WorkingSetBytes));
                    var childItems = entry.Instances
                        .Select((p, idx) => CreateGpuFallbackConsumerItem(idx + 1, p, childMaxMem))
                        .ToList();
                    groups.Add(CreateGroupSnapshot(
                        entry.Key,
                        fbGroupRank++,
                        entry.Instances,
                        barPct,
                        string.Format(CultureInfo.CurrentCulture, "CPU {0:0.0}%", entry.TotalCpu),
                        $"RAM {FormatMemorySize(entry.TotalRam)}",
                        childItems));
                }

                return (
                    "Топ-споживачі GPU",
                    "Лічильники GPU по процесах наразі 0% або недоступні — показано найактивніші процеси за CPU/RAM",
                    items,
                    groups);
            }

            default: // "TEMP"
            {
                string thermalSummary = BuildThermalSummaryText(snapshot);
                var ordered = processes
                    .Where(p => p.Pid > 0)
                    .Select(p =>
                    {
                        double gpuPct = gpuUsage is not null && gpuUsage.TryGetValue(p.Pid, out double g)
                            ? Math.Clamp(g, 0.0, 100.0)
                            : 0.0;
                        double heatScore = Math.Clamp(p.CpuPercent, 0.0, 100.0) + gpuPct;
                        return (Process: p, GpuPercent: gpuPct, HeatScore: heatScore);
                    })
                    .OrderByDescending(x => x.HeatScore)
                    .ThenByDescending(x => x.Process.WorkingSetBytes)
                    .ThenBy(x => x.Process.Pid)
                    .ToList();

                int rank = 1;
                foreach (var entry in ordered.Take(maxItems))
                {
                    items.Add(CreateTempConsumerItem(rank++, entry.Process, entry.GpuPercent, entry.HeatScore));
                }

                var grouped = ordered
                    .GroupBy(static x => NormalizeConsumerGroupKey(x.Process.Name), StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var entries = g.ToList();
                        double totalCpu = Math.Clamp(entries.Sum(static x => Math.Max(0.0, x.Process.CpuPercent)), 0.0, 100.0);
                        double totalGpu = Math.Clamp(entries.Sum(static x => x.GpuPercent), 0.0, 100.0);
                        double totalHeat = entries.Sum(static x => x.HeatScore);
                        long totalRam = entries.Sum(static x => Math.Max(0L, x.Process.WorkingSetBytes));
                        int totalThreads = entries.Sum(static x => Math.Max(0, x.Process.ThreadCount));
                        return (Key: g.Key, Entries: entries, TotalCpu: totalCpu, TotalGpu: totalGpu, TotalHeat: totalHeat, TotalRam: totalRam, TotalThreads: totalThreads);
                    })
                    .OrderByDescending(x => x.TotalHeat)
                    .ThenByDescending(x => x.TotalRam)
                    .ThenBy(x => x.Entries[0].Process.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(maxItems);

                int groupRank = 1;
                foreach (var entry in grouped)
                {
                    var instances = entry.Entries.Select(static x => x.Process).ToList();
                    var childItems = entry.Entries
                        .Select((x, idx) => CreateTempConsumerItem(idx + 1, x.Process, x.GpuPercent, x.HeatScore))
                        .ToList();
                    string primary = entry.TotalGpu > 0.05
                        ? string.Format(CultureInfo.CurrentCulture, "CPU {0:0.0}% · GPU {1:0.0}%", entry.TotalCpu, entry.TotalGpu)
                        : string.Format(CultureInfo.CurrentCulture, "CPU {0:0.0}%", entry.TotalCpu);
                    groups.Add(CreateGroupSnapshot(
                        entry.Key,
                        groupRank++,
                        instances,
                        Math.Clamp(entry.TotalHeat, 0.0, 100.0),
                        primary,
                        $"RAM {FormatMemorySize(entry.TotalRam)} · {entry.TotalThreads} пот.",
                        childItems));
                }

                return (
                    "Джерела нагріву системи (Температура)",
                    $"{thermalSummary} · Топ процесів за тепловим навантаженням (CPU / GPU)",
                    items,
                    groups);
            }
        }
    }

    private static GaugeConsumerItem CreateCpuConsumerItem(int rank, ProcessMetricsItem p)
    {
        double cpu = Math.Clamp(p.CpuPercent, 0.0, 100.0);
        return new GaugeConsumerItem(
            rank,
            p.Pid,
            p.Name,
            string.Format(CultureInfo.CurrentCulture, "{0:0.0}%", cpu),
            $"{FormatMemorySize(p.WorkingSetBytes)} · {p.ThreadCount} пот.",
            cpu,
            p.ExecutablePath,
            p.IsCriticalSystem,
            p.IsProtectedAccess);
    }

    private static GaugeConsumerItem CreateRamConsumerItem(int rank, ProcessMetricsItem p, long totalRamBytes, long peakWorkingSet)
    {
        double barPct = totalRamBytes > 0
            ? Math.Clamp((double)p.WorkingSetBytes / totalRamBytes * 100.0, 0.0, 100.0)
            : Math.Clamp((double)p.WorkingSetBytes / peakWorkingSet * 100.0, 0.0, 100.0);
        return new GaugeConsumerItem(
            rank,
            p.Pid,
            p.Name,
            FormatMemorySize(p.WorkingSetBytes),
            string.Format(CultureInfo.CurrentCulture, "CPU {0:0.0}% · {1} пот.", p.CpuPercent, p.ThreadCount),
            barPct,
            p.ExecutablePath,
            p.IsCriticalSystem,
            p.IsProtectedAccess);
    }

    private static GaugeConsumerItem CreateGpuConsumerItem(int rank, ProcessMetricsItem p, double gpuPercent)
    {
        return new GaugeConsumerItem(
            rank,
            p.Pid,
            p.Name,
            string.Format(CultureInfo.CurrentCulture, "GPU {0:0.0}%", gpuPercent),
            string.Format(CultureInfo.CurrentCulture, "CPU {0:0.0}% · {1}", p.CpuPercent, FormatMemorySize(p.WorkingSetBytes)),
            gpuPercent,
            p.ExecutablePath,
            p.IsCriticalSystem,
            p.IsProtectedAccess);
    }

    private static GaugeConsumerItem CreateGpuFallbackConsumerItem(int rank, ProcessMetricsItem p, long maxMem)
    {
        double barPct = p.CpuPercent > 0.1
            ? Math.Clamp(p.CpuPercent, 0.0, 100.0)
            : Math.Clamp((double)p.WorkingSetBytes / maxMem * 100.0, 0.0, 100.0);
        return new GaugeConsumerItem(
            rank,
            p.Pid,
            p.Name,
            string.Format(CultureInfo.CurrentCulture, "CPU {0:0.0}%", p.CpuPercent),
            $"RAM {FormatMemorySize(p.WorkingSetBytes)}",
            barPct,
            p.ExecutablePath,
            p.IsCriticalSystem,
            p.IsProtectedAccess);
    }

    private static GaugeConsumerItem CreateTempConsumerItem(int rank, ProcessMetricsItem p, double gpuPercent, double heatScore)
    {
        string primary = gpuPercent > 0.05
            ? string.Format(CultureInfo.CurrentCulture, "CPU {0:0.0}% · GPU {1:0.0}%", p.CpuPercent, gpuPercent)
            : string.Format(CultureInfo.CurrentCulture, "CPU {0:0.0}%", p.CpuPercent);
        return new GaugeConsumerItem(
            rank,
            p.Pid,
            p.Name,
            primary,
            $"RAM {FormatMemorySize(p.WorkingSetBytes)} · {p.ThreadCount} пот.",
            Math.Clamp(heatScore, 0.0, 100.0),
            p.ExecutablePath,
            p.IsCriticalSystem,
            p.IsProtectedAccess);
    }

    private static GaugeConsumerGroupSnapshot CreateGroupSnapshot(
        string groupKey,
        int rank,
        IReadOnlyList<ProcessMetricsItem> instances,
        double barPercent,
        string primaryMetricText,
        string secondaryMetricText,
        IReadOnlyList<GaugeConsumerItem> childItems)
    {
        string displayName = instances
            .Select(static p => p.Name?.Trim())
            .FirstOrDefault(static n => !string.IsNullOrEmpty(n))
            ?? groupKey;

        string? executablePath = instances
            .Select(static p => p.ExecutablePath)
            .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path));

        bool isCritical = childItems.All(static c => c.IsCriticalSystem);

        return new GaugeConsumerGroupSnapshot(
            groupKey,
            rank,
            displayName,
            barPercent,
            primaryMetricText,
            secondaryMetricText,
            executablePath,
            isCritical,
            childItems);
    }

    private static string BuildThermalSummaryText(TelemetrySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "CPU: н/д · GPU: н/д";
        }

        string cpuTemp = snapshot.Cpu.PackageTemperatureCelsius.HasValue
            ? string.Format(CultureInfo.CurrentCulture, "{0:0} °C", snapshot.Cpu.PackageTemperatureCelsius.Value)
            : "н/д";

        var primaryGpu = snapshot.Gpus.FirstOrDefault();
        string gpuTemp = primaryGpu?.TemperatureCelsius is { } gTemp
            ? string.Format(CultureInfo.CurrentCulture, "{0:0} °C", gTemp)
            : "н/д";

        return $"CPU: {cpuTemp} · GPU: {gpuTemp}";
    }

    private static string FormatMemorySize(long bytes)
    {
        const double mib = 1024.0 * 1024.0;
        const double gib = 1024.0 * 1024.0 * 1024.0;
        if (bytes >= gib)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.00} ГіБ", bytes / gib);
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.0} МБ", bytes / mib);
    }

    [RelayCommand]
    private void SelectProcessesTab()
    {
        ActiveInspectorTab = ExpertInspectorTab.Processes;
        PendingTerminateProcess = null;
        ApplyInspectorFilter();
        if (IsExpertMode && _allProcesses.Count == 0 && _systemInspector is not null)
        {
            _ = RefreshInspectorAsync();
        }
    }

    [RelayCommand]
    private void SelectServicesTab()
    {
        ActiveInspectorTab = ExpertInspectorTab.Services;
        PendingTerminateProcess = null;
        ApplyInspectorFilter();
        if (IsExpertMode && _allServices.Count == 0 && _systemInspector is not null)
        {
            _ = RefreshInspectorAsync();
        }
    }

    [RelayCommand]
    private void SelectLocalPortsTab()
    {
        ActiveInspectorTab = ExpertInspectorTab.LocalPorts;
        PendingTerminateProcess = null;
        ApplyInspectorFilter();
        if (IsExpertMode && _allLocalPorts.Count == 0 && _systemInspector is not null)
        {
            _ = RefreshInspectorAsync();
        }
    }

    [RelayCommand]
    public async Task RefreshInspectorAsync()
    {
        if (_systemInspector is null || !IsExpertMode || Interlocked.CompareExchange(ref _inspectorBusyGate, 1, 0) != 0)
        {
            return;
        }

        _uiDispatch(() => IsInspectorLoading = true);
        try
        {
            var processesTask = _systemInspector.GetProcessesAsync();
            var servicesTask = _systemInspector.GetServicesAsync();
            var portsTask = _systemInspector.GetLocalPortsAsync();
            await Task.WhenAll(processesTask, servicesTask, portsTask).ConfigureAwait(false);

            var processes = await processesTask.ConfigureAwait(false);
            var services = await servicesTask.ConfigureAwait(false);
            var ports = await portsTask.ConfigureAwait(false);

            _uiDispatch(() =>
            {
                _inspectorLoadedOnce = true;
                _allProcesses = processes;
                _allServices = services;
                _allLocalPorts = ports;

                ProcessesTabLabel = $"Процеси ({processes.Count})";
                ServicesTabLabel = $"Служби ({services.Count})";
                LocalPortsTabLabel = $"Порти ({ports.Count})";

                ApplyInspectorFilter();
            });
        }
        catch (Exception ex)
        {
            _uiDispatch(() =>
            {
                InspectorFeedbackIsSuccess = false;
                InspectorFeedbackText = $"Не вдалося оновити діагностику: {ex.Message}";
                HasInspectorFeedback = true;
            });
        }
        finally
        {
            Interlocked.Exchange(ref _inspectorBusyGate, 0);
            _uiDispatch(() => IsInspectorLoading = false);
        }
    }

    [RelayCommand]
    private void OpenProcessFolder(ProcessRowItem? row)
    {
        if (_systemInspector is null || row is null || !row.HasExecutablePath)
        {
            return;
        }

        bool opened = _systemInspector.OpenProcessFolder(row.ExecutablePath);
        InspectorFeedbackIsSuccess = opened;
        InspectorFeedbackText = opened
            ? $"Відкрито теку процесу «{row.Name}»."
            : $"Не вдалося відкрити теку для «{row.Name}».";
        HasInspectorFeedback = true;
    }

    [RelayCommand]
    private void RequestTerminateProcess(ProcessRowItem? row)
    {
        if (row is null)
        {
            return;
        }

        PendingTerminateProcess = row;
    }

    [RelayCommand]
    private void CancelTerminateProcess()
    {
        PendingTerminateProcess = null;
    }

    [RelayCommand]
    private async Task ConfirmTerminateProcessAsync()
    {
        var target = PendingTerminateProcess;
        PendingTerminateProcess = null;

        if (_systemInspector is null || target is null)
        {
            return;
        }

        var result = await _systemInspector.TerminateProcessAsync(target.Pid).ConfigureAwait(false);
        _uiDispatch(() =>
        {
            InspectorFeedbackIsSuccess = result.Succeeded;
            InspectorFeedbackText = result.Message;
            HasInspectorFeedback = true;

            if (result.RequiresElevation)
            {
                ShowInspectorElevationCallout = true;
                InspectorElevationReason = result.Message;
            }
        });

        if (result.Succeeded)
        {
            await RefreshInspectorAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private Task StartServiceAsync(WindowsServiceRowItem? row) =>
        ControlServiceCoreAsync(row, ServiceControlAction.Start);

    [RelayCommand]
    private Task StopServiceAsync(WindowsServiceRowItem? row) =>
        ControlServiceCoreAsync(row, ServiceControlAction.Stop);

    [RelayCommand]
    private Task RestartServiceAsync(WindowsServiceRowItem? row) =>
        ControlServiceCoreAsync(row, ServiceControlAction.Restart);

    private async Task ControlServiceCoreAsync(WindowsServiceRowItem? row, ServiceControlAction action)
    {
        if (_systemInspector is null || row is null)
        {
            return;
        }

        var result = await _systemInspector.ControlServiceAsync(row.ServiceName, action).ConfigureAwait(false);
        _uiDispatch(() =>
        {
            InspectorFeedbackIsSuccess = result.Succeeded;
            InspectorFeedbackText = result.Message;
            HasInspectorFeedback = true;

            if (result.RequiresElevation)
            {
                ShowInspectorElevationCallout = true;
                InspectorElevationReason = result.Message;
            }
        });

        if (result.Succeeded)
        {
            await RefreshInspectorAsync().ConfigureAwait(false);
        }
    }

    private void ApplyInspectorFilter()
    {
        string query = InspectorSearchQuery?.Trim() ?? string.Empty;
        const int maxVisible = 120;

        if (IsProcessesTab)
        {
            FilteredProcesses.Clear();
            int matched = 0;
            foreach (var p in _allProcesses)
            {
                if (!string.IsNullOrEmpty(query) &&
                    !p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !p.Pid.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !(p.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    continue;
                }

                matched++;
                if (FilteredProcesses.Count < maxVisible)
                {
                    FilteredProcesses.Add(new ProcessRowItem(p));
                }
            }

            InspectorSummaryText = $"Показано {FilteredProcesses.Count} із {matched} процесів (усього {_allProcesses.Count})";
        }
        else if (IsServicesTab)
        {
            FilteredServices.Clear();
            int matched = 0;
            foreach (var s in _allServices)
            {
                if (!string.IsNullOrEmpty(query) &&
                    !s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !s.ServiceName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !s.Status.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matched++;
                if (FilteredServices.Count < maxVisible)
                {
                    FilteredServices.Add(new WindowsServiceRowItem(s));
                }
            }

            InspectorSummaryText = $"Показано {FilteredServices.Count} із {matched} служб (усього {_allServices.Count})";
        }
        else
        {
            FilteredLocalPorts.Clear();
            int matched = 0;
            foreach (var port in _allLocalPorts)
            {
                if (!string.IsNullOrEmpty(query) &&
                    !port.Protocol.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !port.LocalAddress.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !port.LocalPort.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !port.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !port.Pid.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matched++;
                if (FilteredLocalPorts.Count < maxVisible)
                {
                    FilteredLocalPorts.Add(new LocalPortRowItem(port));
                }
            }

            InspectorSummaryText = $"Показано {FilteredLocalPorts.Count} із {matched} локальних портів (усього {_allLocalPorts.Count})";
        }
    }

    private Action<ArgusSectionId>? _navigateToSection;

    public void AttachNavigator(Action<ArgusSectionId> navigate)
    {
        _navigateToSection = navigate;
    }

    [RelayCommand]
    private void OpenFullProcesses()
    {
        _navigateToSection?.Invoke(ArgusSectionId.Processes);
    }

    [RelayCommand]
    private void SelectWindow30() => SetTimeWindow(30);

    [RelayCommand]
    private void SelectWindow60() => SetTimeWindow(60);

    [RelayCommand]
    private void SelectWindow180() => SetTimeWindow(180);

    [RelayCommand]
    private void SelectWindow300() => SetTimeWindow(300);

    [RelayCommand]
    private void ToggleCpuSeries()
    {
        ShowCpuSeries = !ShowCpuSeries;
        RebuildChartSeries();
    }

    [RelayCommand]
    private void ToggleRamSeries()
    {
        ShowRamSeries = !ShowRamSeries;
        RebuildChartSeries();
    }

    [RelayCommand]
    private void ToggleGpuSeries()
    {
        ShowGpuSeries = !ShowGpuSeries;
        RebuildChartSeries();
    }

    [RelayCommand]
    private void ToggleTempSeries()
    {
        ShowTempSeries = !ShowTempSeries;
        RebuildChartSeries();
    }

    [RelayCommand]
    private void ShowAllSeries()
    {
        ShowCpuSeries = true;
        ShowRamSeries = true;
        ShowGpuSeries = true;
        ShowTempSeries = true;
        RebuildChartSeries();
    }

    [RelayCommand]
    private void DismissAlert()
    {
        HasActiveAlert = false;
        ActiveAlertText = string.Empty;
    }

    [RelayCommand]
    private Task ExportHtmlReportAsync() => ExportReportCoreAsync(TelemetryReportFormat.Html);

    [RelayCommand]
    private Task ExportJsonReportAsync() => ExportReportCoreAsync(TelemetryReportFormat.Json);

    [RelayCommand]
    private Task ExportCsvReportAsync() => ExportReportCoreAsync(TelemetryReportFormat.Csv);

    public void SetTimeWindow(int seconds)
    {
        SelectedWindowSeconds = Math.Clamp(seconds, 30, 3600);
        OnPropertyChanged(nameof(IsWindow30Selected));
        OnPropertyChanged(nameof(IsWindow60Selected));
        OnPropertyChanged(nameof(IsWindow180Selected));
        OnPropertyChanged(nameof(IsWindow300Selected));
        RebuildChartSeries();
    }

    private async Task ExportReportCoreAsync(TelemetryReportFormat format)
    {
        if (_reportExporter is null || IsExportingReport)
        {
            return;
        }

        IsExportingReport = true;
        try
        {
            var result = await _reportExporter.ExportAsync(
                format,
                maxSamples: ComputeWindowSampleCount(SelectedWindowSeconds)).ConfigureAwait(false);

            _uiDispatch(() =>
            {
                ExportSucceeded = result.Succeeded;
                ExportFeedback = result.Message;
                HasExportFeedback = true;
            });
        }
        finally
        {
            _uiDispatch(() => IsExportingReport = false);
        }
    }

    private void ApplyAlert(TelemetryAlertEvent alert)
    {
        ActiveAlertText = $"{alert.Timestamp.ToLocalTime():HH:mm:ss} · {alert.Message}";
        HasActiveAlert = true;
    }

    private void RefreshSettingsLabels()
    {
        var cfg = _settingsStore.Current;
        SamplingSummary = $"Інтервал: {cfg.TelemetryIntervalMs} мс";
        SensorModeSummary = _tierStatusProvider.GetStatusSummary();
        ActiveAlertThreshold = Math.Min(cfg.GpuTemperatureAlertCelsius, cfg.RamUsageAlertPercent);

        int samples = _telemetryService.GetHistory(cfg.HistoryWindowSeconds).Count;
        HistorySummary = $"Буфер: {samples} / {cfg.HistoryWindowSeconds}";
        RebuildChartSeries();
    }

    private int ComputeWindowSampleCount(int seconds)
    {
        int intervalMs = Math.Clamp(_settingsStore.Current.TelemetryIntervalMs, 250, 10000);
        return Math.Clamp((int)Math.Ceiling(seconds * 1000.0 / intervalMs), 1, 3600);
    }

    private void RebuildChartSeries()
    {
        int windowSamples = ComputeWindowSampleCount(SelectedWindowSeconds);
        var history = _telemetryService.GetHistory(windowSamples);
        RebuildChartSeriesFromHistory(history, windowSamples);
    }

    private void RebuildChartSeriesFromHistory(IReadOnlyList<TelemetrySnapshot> history, int windowSamples)
    {
        if (history.Count == 0)
        {
            if (_telemetryService.Latest is { } latest)
            {
                history = [latest];
            }
            else
            {
                return;
            }
        }

        int startOffset = history.Count > windowSamples ? history.Count - windowSamples : 0;
        int count = history.Count - startOffset;

        var cpu = new double[count];
        var ram = new double[count];
        var gpu = new double[count];
        var temp = new double[count];
        bool hasAnyGpuLoad = false;
        bool hasAnyTemp = false;

        for (int i = 0; i < count; i++)
        {
            var item = history[startOffset + i];
            cpu[i] = Math.Clamp(item.Cpu.TotalLoadPercent, 0.0, 100.0);
            ram[i] = Math.Clamp(item.Memory.LoadPercent, 0.0, 100.0);

            var primaryGpu = item.Gpus.FirstOrDefault();
            if (primaryGpu?.CoreLoadPercent is { } gLoad)
            {
                gpu[i] = Math.Clamp(gLoad, 0.0, 100.0);
                hasAnyGpuLoad = true;
            }

            var (peakTemp, _) = ResolvePeakTemperature(item);
            if (peakTemp.HasValue)
            {
                temp[i] = Math.Clamp(peakTemp.Value, 0.0, 100.0);
                hasAnyTemp = true;
            }
        }

        CpuSeries = ShowCpuSeries ? cpu : [];
        RamSeries = ShowRamSeries ? ram : [];
        GpuSeries = (ShowGpuSeries && hasAnyGpuLoad) ? gpu : [];
        TempSeries = (ShowTempSeries && hasAnyTemp) ? temp : [];
    }

    private void ApplySnapshot(TelemetrySnapshot snap)
    {
        _lastSnapshot = snap;
        HasLiveSample = true;
        LastUpdatedText = $"Оновлено о {snap.Timestamp.ToLocalTime():HH:mm:ss}";

        var cfg = _settingsStore.Current;
        int windowSamples = ComputeWindowSampleCount(SelectedWindowSeconds);
        var history = _telemetryService.GetHistory(Math.Max(windowSamples, cfg.HistoryWindowSeconds));
        HistorySummary = $"Буфер: {Math.Min(history.Count, cfg.HistoryWindowSeconds)} / {cfg.HistoryWindowSeconds}";

        // 1. CPU
        CpuStandby = false;
        CpuLoadValue = Math.Clamp(snap.Cpu.TotalLoadPercent, 0.0, 100.0);
        CpuDisplayValue = string.Format(CultureInfo.CurrentCulture, "{0:0.0}", CpuLoadValue);
        CpuModelName = snap.Cpu.Name;

        string clockPart = snap.Cpu.CurrentClockMHz.HasValue
            ? string.Format(CultureInfo.CurrentCulture, "{0:0} МГц", snap.Cpu.CurrentClockMHz.Value)
            : "н/д";

        CpuSubLabel = $"{clockPart} · {snap.Cpu.PhysicalCores}Я / {snap.Cpu.LogicalProcessors}П";
        CpuClockAndTopologyText = $"{clockPart} · {snap.Cpu.PhysicalCores} фіз. ядер / {snap.Cpu.LogicalProcessors} потоків";

        SyncPerCoreItems(snap.Cpu.PerCoreLoadPercent);

        // 2. RAM
        if (snap.Memory.TotalBytes > 0)
        {
            const double gib = 1024.0 * 1024.0 * 1024.0;
            double usedGiB = snap.Memory.UsedBytes / gib;
            double totalGiB = snap.Memory.TotalBytes / gib;
            RamStandby = false;
            RamLoadValue = Math.Clamp(snap.Memory.LoadPercent, 0.0, 100.0);
            RamDisplayValue = string.Format(CultureInfo.CurrentCulture, "{0:0.0}", usedGiB);
            RamSubLabel = string.Format(CultureInfo.CurrentCulture, "з {0:0.0} ГіБ ({1:0.0}%)", totalGiB, RamLoadValue);
        }

        // 3. GPU
        var primaryGpu = snap.Gpus.FirstOrDefault();
        if (primaryGpu is not null)
        {
            GpuNameText = primaryGpu.Name;
            string shortGpu = ShortenGpuName(primaryGpu.Name);

            string vramSummary = FormatVram(primaryGpu.VramUsedBytes, primaryGpu.VramTotalBytes);
            GpuVramDetailText = $"VRAM: {vramSummary}";

            string clockText = primaryGpu.CoreClockMHz.HasValue
                ? string.Format(CultureInfo.CurrentCulture, "{0:0} МГц", primaryGpu.CoreClockMHz.Value)
                : "н/д";
            string tempPart = primaryGpu.TemperatureCelsius.HasValue
                ? string.Format(CultureInfo.CurrentCulture, "{0:0} °C", primaryGpu.TemperatureCelsius.Value)
                : "н/д";
            GpuClockAndFanText = $"Ядро: {clockText} · Темп.: {tempPart}";

            if (primaryGpu.CoreLoadPercent.HasValue)
            {
                GpuStandby = false;
                GpuLoadValue = Math.Clamp(primaryGpu.CoreLoadPercent.Value, 0.0, 100.0);
                GpuDisplayValue = string.Format(CultureInfo.CurrentCulture, "{0:0}", GpuLoadValue);
                GpuUnit = "%";
                GpuSubLabel = $"{shortGpu} · {vramSummary}";
            }
            else if (primaryGpu.VramUsedBytes.HasValue && primaryGpu.VramTotalBytes is > 0)
            {
                double vramPct = Math.Clamp((double)primaryGpu.VramUsedBytes.Value / primaryGpu.VramTotalBytes.Value * 100.0, 0.0, 100.0);
                GpuStandby = false;
                GpuLoadValue = vramPct;
                GpuDisplayValue = string.Format(CultureInfo.CurrentCulture, "{0:0.0}", primaryGpu.VramUsedBytes.Value / (1024.0 * 1024.0 * 1024.0));
                GpuUnit = "ГіБ";
                GpuSubLabel = shortGpu;
            }
            else
            {
                GpuStandby = true;
                GpuDisplayValue = "—";
                GpuSubLabel = shortGpu;
            }
        }
        else
        {
            GpuStandby = true;
            GpuDisplayValue = "—";
            GpuNameText = "Відеоадаптер не знайдено";
            GpuSubLabel = "Немає активного адаптера";
        }

        // 4. Peak real temperature across all verified hardware sources
        var (peakTemp, peakSource) = ResolvePeakTemperature(snap);
        if (peakTemp.HasValue)
        {
            TempStandby = false;
            TempValue = Math.Clamp(peakTemp.Value, 0.0, 110.0);
            TempDisplayValue = string.Format(CultureInfo.CurrentCulture, "{0:0}", peakTemp.Value);
            TempSubLabel = peakSource;
        }
        else
        {
            TempStandby = true;
            TempValue = 0;
            TempDisplayValue = "—";
            TempSubLabel = "Датчики температури недоступні";
        }

        // 5. Storage & extra sensors & live chart curves
        SyncStorageItems(snap.Storage);
        SyncExtraSensors(snap.ExtraSensors);
        RebuildChartSeriesFromHistory(history, windowSamples);

        // 6. Periodic refresh of open interactive gauge consumers panel
        if (IsGaugeConsumersOpen &&
            _isUiActive &&
            (DateTimeOffset.UtcNow - _lastGaugeConsumersRefreshUtc) >= GaugeConsumersRefreshInterval)
        {
            _ = RefreshGaugeConsumersAsync();
        }
    }

    internal static (double? PeakCelsius, string SourceLabel) ResolvePeakTemperature(TelemetrySnapshot snap)
    {
        double? bestTemp = null;
        string bestLabel = string.Empty;

        void Consider(double? temp, string label)
        {
            if (temp.HasValue && (!bestTemp.HasValue || temp.Value > bestTemp.Value))
            {
                bestTemp = temp.Value;
                bestLabel = label;
            }
        }

        Consider(snap.Cpu.PackageTemperatureCelsius, "CPU (PawnIO)");

        foreach (var gpu in snap.Gpus)
        {
            Consider(gpu.TemperatureCelsius, $"GPU · {ShortenGpuName(gpu.Name)}");
        }

        foreach (var disk in snap.Storage)
        {
            Consider(disk.TemperatureCelsius, $"Диск {disk.DiskNumber}");
        }

        if (!bestTemp.HasValue)
        {
            foreach (var sensor in snap.ExtraSensors)
            {
                if (sensor.Kind == SensorKind.TemperatureCelsius)
                {
                    Consider(sensor.Value, sensor.Name);
                }
            }
        }

        return (bestTemp, bestLabel);
    }

    private void SyncPerCoreItems(IReadOnlyList<double> loads)
    {
        if (CoreLoads.Count != loads.Count)
        {
            CoreLoads.Clear();
            for (int i = 0; i < loads.Count; i++)
            {
                var item = new CoreLoadItem(i);
                item.Update(loads[i]);
                CoreLoads.Add(item);
            }

            return;
        }

        for (int i = 0; i < loads.Count; i++)
        {
            CoreLoads[i].Update(loads[i]);
        }
    }

    private void SyncStorageItems(IReadOnlyList<StorageThermalSnapshot> storage)
    {
        bool shapeChanged = StorageItems.Count != storage.Count;
        if (!shapeChanged)
        {
            for (int i = 0; i < storage.Count; i++)
            {
                if (StorageItems[i].DiskNumber != storage[i].DiskNumber)
                {
                    shapeChanged = true;
                    break;
                }
            }
        }

        if (shapeChanged)
        {
            StorageItems.Clear();
            foreach (var disk in storage)
            {
                var row = new StorageRowItem(disk.DiskNumber, disk.Model);
                row.Update(disk);
                StorageItems.Add(row);
            }

            return;
        }

        for (int i = 0; i < storage.Count; i++)
        {
            StorageItems[i].Update(storage[i]);
        }
    }

    private void SyncExtraSensors(IReadOnlyList<SensorReading> sensors)
    {
        HasExtraSensors = sensors.Count > 0;
        bool shapeChanged = ExtraSensors.Count != sensors.Count;
        if (!shapeChanged)
        {
            for (int i = 0; i < sensors.Count; i++)
            {
                if (!string.Equals(ExtraSensors[i].SensorId, sensors[i].SensorId, StringComparison.Ordinal))
                {
                    shapeChanged = true;
                    break;
                }
            }
        }

        if (shapeChanged)
        {
            ExtraSensors.Clear();
            foreach (var reading in sensors)
            {
                var item = new ExtraSensorItem(reading.SensorId, reading.Name);
                item.Update(reading);
                ExtraSensors.Add(item);
            }

            return;
        }

        for (int i = 0; i < sensors.Count; i++)
        {
            ExtraSensors[i].Update(sensors[i]);
        }
    }

    private static string FormatVram(long? usedBytes, long? totalBytes)
    {
        const double gib = 1024.0 * 1024.0 * 1024.0;
        if (usedBytes.HasValue && totalBytes is > 0)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.0} / {1:0.0} ГіБ", usedBytes.Value / gib, totalBytes.Value / gib);
        }

        if (totalBytes is > 0)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.0} ГіБ", totalBytes.Value / gib);
        }

        return "н/д";
    }

    private static string ShortenGpuName(string name)
    {
        return name
            .Replace("NVIDIA ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("AMD ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Intel(R) ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    public void Dispose()
    {
        DetachUiMode();
        _telemetrySubscription.Dispose();
        _settingsSubscription.Dispose();
        _alertSubscription.Dispose();
        if (_ownsGpuMetricsReader)
        {
            _gpuProcessMetricsReader.Dispose();
        }
    }
}
