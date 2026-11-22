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

public sealed partial class MonitoringViewModel : SectionViewModelBase, IDisposable
{
    private readonly IArgusSettingsStore _settingsStore;
    private readonly ITelemetryService _telemetryService;
    private readonly ISensorTierStatusProvider _tierStatusProvider;
    private readonly ITelemetryReportExporter? _reportExporter;
    private readonly ISystemInspectorService? _systemInspector;
    private readonly Action<Action> _uiDispatch;
    private readonly IDisposable _telemetrySubscription;
    private readonly IDisposable _settingsSubscription;
    private readonly IDisposable _alertSubscription;
    private volatile bool _isUiActive = true;
    private int _inspectorBusyGate;
    private bool _inspectorLoadedOnce;
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
        ISystemInspectorService? systemInspector = null)
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

        CoreLoads = [];
        StorageItems = [];
        ExtraSensors = [];
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
        }
    }

    protected override void OnExpertModeChanged(bool isExpertMode)
    {
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
        }
    }

    public ObservableCollection<CoreLoadItem> CoreLoads { get; }
    public ObservableCollection<StorageRowItem> StorageItems { get; }
    public ObservableCollection<ExtraSensorItem> ExtraSensors { get; }
    public ObservableCollection<ProcessRowItem> FilteredProcesses { get; }
    public ObservableCollection<WindowsServiceRowItem> FilteredServices { get; }
    public ObservableCollection<LocalPortRowItem> FilteredLocalPorts { get; }

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
    }
}
