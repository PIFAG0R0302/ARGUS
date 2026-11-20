using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Argus.Core;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Core.Extensions;
using Argus.Core.Journal;
using Argus.Maintenance.Cleanup;
using Argus.Telemetry.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed class ModuleStatusItem(ArgusModuleDescriptor descriptor)
{
    public string DisplayName { get; } = descriptor.DisplayName;
    public string Summary { get; } = descriptor.Summary;
    public ArgusModuleStage Stage { get; } = descriptor.Stage;
    public bool IsReady => Stage == ArgusModuleStage.Ready;
    public string StageLabel => Stage switch
    {
        ArgusModuleStage.Ready => "Готово",
        ArgusModuleStage.Scaffolded => "Каркас",
        ArgusModuleStage.OptionalDisabled => "Вимкнено",
        _ => Stage.ToString()
    };
}

public sealed class JournalRowItem(OperationJournalEntry entry)
{
    public string TimeText { get; } = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string ModuleId { get; } = entry.ModuleId;
    public string Action { get; } = entry.Action;
    public string Details { get; } = entry.Details;
    public OperationSeverity Severity { get; } = entry.Severity;
    public string SeverityLabel => Severity switch
    {
        OperationSeverity.Success => "OK",
        OperationSeverity.Warning => "Увага",
        OperationSeverity.Error => "Помилка",
        _ => "Інфо"
    };
}

public sealed class OverviewDiskSummaryItem
{
    public OverviewDiskSummaryItem(PhysicalDiskCardItem card)
    {
        Title = card.Title;
        MediaBadge = card.MediaBadge;
        IsSsdOrNvme = card.IsSsdOrNvme;
        DetailsText = card.BusAndCapacityText;
        HealthLabel = card.HealthLabel;
        IsHealthy = card.IsHealthy;
        IsCaution = card.IsCaution;
        IsCritical = card.IsCritical;
    }

    public string Title { get; }
    public string MediaBadge { get; }
    public bool IsSsdOrNvme { get; }
    public string DetailsText { get; }
    public string HealthLabel { get; }
    public bool IsHealthy { get; }
    public bool IsCaution { get; }
    public bool IsCritical { get; }
}

public sealed partial class OverviewViewModel : SectionViewModelBase, IDisposable
{
    private const int SparklineMaxPoints = 30;

    private readonly IArgusSettingsStore _settingsStore;
    private readonly ITelemetryService? _telemetryService;
    private readonly ISensorTierStatusProvider? _tierStatusProvider;
    private readonly DevicesViewModel? _devicesVm;
    private readonly CleanupViewModel? _cleanupVm;
    private readonly DisksViewModel? _disksVm;
    private readonly Action<Action> _uiDispatch;
    private readonly List<double> _fallbackCpuSparkline = [];
    private readonly List<double> _fallbackRamSparkline = [];
    private readonly IDisposable _telemetrySubscription;
    private readonly IDisposable _settingsSubscription;
    private Action<ArgusSectionId>? _navigateToSection;
    private volatile bool _isUiActive = true;

    public OverviewViewModel(
        IEnumerable<IArgusModule> modules,
        IArgusSettingsStore settingsStore,
        IOperationJournal journal,
        IArgusEventBus eventBus,
        ITelemetryService? telemetryService = null,
        ISensorTierStatusProvider? tierStatusProvider = null,
        Action<Action>? uiDispatch = null,
        DevicesViewModel? devicesViewModel = null,
        CleanupViewModel? cleanupViewModel = null,
        DisksViewModel? disksViewModel = null)
        : base(
            ArgusSectionId.Overview,
            "Огляд системи",
            "Загальний стан ПК: основні датчики, архітектура заліза, швидкий аналіз очищення та підключені диски")
    {
        ArgumentNullException.ThrowIfNull(modules);
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(eventBus);

        _telemetryService = telemetryService;
        _tierStatusProvider = tierStatusProvider;
        _devicesVm = devicesViewModel;
        _cleanupVm = cleanupViewModel;
        _disksVm = disksViewModel;
        _uiDispatch = uiDispatch ?? (static action => action());

        LogicalProcessorsText = $"{Environment.ProcessorCount}";
        long totalRamBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        TotalRamText = totalRamBytes > 0
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.#} ГіБ", totalRamBytes / (1024.0 * 1024.0 * 1024.0))
            : "—";

        RuntimeText = RuntimeInformation.FrameworkDescription;
        OsText = FormatWindowsName();
        RefreshSettingsDisplay();
        RefreshSystemDiskGauge();

        ConnectedDisks = [];

        if (telemetryService?.Latest is { } latest)
        {
            ApplyTelemetry(latest);
        }

        if (_devicesVm is not null)
        {
            _devicesVm.PropertyChanged += OnDevicesPropertyChanged;
            SyncHardwareSummaryFromDevices();
        }

        if (_cleanupVm is not null)
        {
            _cleanupVm.PropertyChanged += OnCleanupPropertyChanged;
            _cleanupVm.ScanStateChanged += OnCleanupScanChanged;
            SyncCleanupSummary();
        }

        if (_disksVm is not null)
        {
            _disksVm.DisksChanged += OnDisksChanged;
            SyncConnectedDisks();
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
                    ApplyTelemetry(e.Snapshot);
                }
            });
        });
        _settingsSubscription = eventBus.Subscribe<SettingsChangedEvent>(_ => _uiDispatch(RefreshSettingsDisplay));
    }

    public void SetUiActive(bool active)
    {
        bool wasActive = _isUiActive;
        _isUiActive = active;
        if (active && !wasActive && _telemetryService?.Latest is { } latest)
        {
            _uiDispatch(() => ApplyTelemetry(latest));
        }
    }

    public void AttachNavigator(Action<ArgusSectionId> navigateToSection)
    {
        _navigateToSection = navigateToSection;
    }

    public ObservableCollection<OverviewDiskSummaryItem> ConnectedDisks { get; }

    public string LogicalProcessorsText { get; }
    public string TotalRamText { get; }
    public string RuntimeText { get; }
    public string OsText { get; }

    // 4 Top Radial Percentage Gauges (CPU %, RAM %, GPU %, System Disk C: %)
    [ObservableProperty]
    private bool _cpuStandby = true;

    [ObservableProperty]
    private double _cpuGaugeValue;

    [ObservableProperty]
    private string _cpuGaugeDisplay = "—";

    [ObservableProperty]
    private string _cpuGaugeSubLabel = $"{Environment.ProcessorCount} потоків";

    [ObservableProperty]
    private bool _ramStandby = true;

    [ObservableProperty]
    private double _ramGaugeValue;

    [ObservableProperty]
    private string _ramGaugeDisplay = "—";

    [ObservableProperty]
    private string _ramGaugeSubLabel = "Ініціалізація…";

    [ObservableProperty]
    private bool _gpuStandby = true;

    [ObservableProperty]
    private double _gpuGaugeValue;

    [ObservableProperty]
    private string _gpuGaugeDisplay = "—";

    [ObservableProperty]
    private string _gpuGaugeSubLabel = "Опитування GPU…";

    [ObservableProperty]
    private double _systemDiskUsedPercent;

    [ObservableProperty]
    private string _systemDiskDisplay = "—";

    [ObservableProperty]
    private string _systemDiskSubLabel = "Системний том C:";

    // Node & Hardware Architecture Summary (from Devices + Telemetry)
    [ObservableProperty]
    private string _cpuArchitectureText = "Визначення процесора…";

    [ObservableProperty]
    private string _cpuCacheDetailText = $"{Environment.ProcessorCount} потоків";

    [ObservableProperty]
    private string _ramArchitectureText = "Опитування модулів ОЗП…";

    [ObservableProperty]
    private string _gpuArchitectureText = "Опитування відеоадаптера…";

    [ObservableProperty]
    private string _motherboardArchitectureText = "Опитування системної плати…";

    [ObservableProperty]
    private string _biosArchitectureText = "BIOS / UEFI";

    [ObservableProperty]
    private string _sensorTierText = "Рівень 1: без сторонніх драйверів";

    [ObservableProperty]
    private string _telemetryIntervalText = "1000 мс";

    [ObservableProperty]
    private string _liveCpuSummaryText = "—";

    [ObservableProperty]
    private string _liveRamSummaryText = "—";

    [ObservableProperty]
    private string _liveGpuSummaryText = "Опитування…";

    [ObservableProperty]
    private IReadOnlyList<double> _cpuSparklineValues = [];

    [ObservableProperty]
    private IReadOnlyList<double> _ramSparklineValues = [];

    // Cleanup Quick-Analyze Widget (Synced with CleanupViewModel)
    [ObservableProperty]
    private string _cleanupFoundSummaryText = "Ще не проаналізовано";

    [ObservableProperty]
    private string _cleanupStatusText = "Натисніть «Аналізувати», щоб перевірити тимчасові файли й кеш.";

    [ObservableProperty]
    private int _cleanupProgressPercent;

    [ObservableProperty]
    private string _cleanupProgressText = "0 %";

    [ObservableProperty]
    private bool _isCleanupBusy;

    // Connected Disks Widget (Synced with DisksViewModel)
    [ObservableProperty]
    private string _disksFleetSummaryText = "Опитування накопичувачів…";

    [RelayCommand]
    private Task AnalyzeCleanupFromOverviewAsync()
    {
        return _cleanupVm is not null
            ? _cleanupVm.AnalyzeAsync()
            : Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenMonitoring() => _navigateToSection?.Invoke(ArgusSectionId.Monitoring);

    [RelayCommand]
    private void OpenDevices() => _navigateToSection?.Invoke(ArgusSectionId.Devices);

    [RelayCommand]
    private void OpenCleanup() => _navigateToSection?.Invoke(ArgusSectionId.Cleanup);

    [RelayCommand]
    private void OpenDisks() => _navigateToSection?.Invoke(ArgusSectionId.Disks);

    private void OnDevicesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _uiDispatch(SyncHardwareSummaryFromDevices);
    }

    private void SyncHardwareSummaryFromDevices()
    {
        if (_devicesVm is null)
        {
            return;
        }

        CpuArchitectureText = _devicesVm.CpuTitle;
        CpuCacheDetailText = $"{_devicesVm.CpuTopologyAndClockText} · {_devicesVm.CpuCacheAndArchText}";
        MotherboardArchitectureText = _devicesVm.BoardTitle;
        BiosArchitectureText = _devicesVm.BiosSummaryText;
        GpuArchitectureText = _devicesVm.PrimaryGpuTitle;

        if (_devicesVm.RamSlots.Count > 0)
        {
            string slots = string.Join(" · ", _devicesVm.RamSlots.Take(4).Select(static s => $"{s.SlotLocator}: {s.CapacityAndSpeed}"));
            RamArchitectureText = $"{_devicesVm.RamSummaryTitle} ({slots})";
        }
        else
        {
            RamArchitectureText = _devicesVm.RamSummaryTitle;
        }
    }

    private void OnCleanupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanupViewModel.IsBusy)
            or nameof(CleanupViewModel.OperationProgressPercent)
            or nameof(CleanupViewModel.OperationProgressText)
            or nameof(CleanupViewModel.OperationStageText)
            or nameof(CleanupViewModel.TotalFoundSummaryText)
            or nameof(CleanupViewModel.StatusBannerText))
        {
            _uiDispatch(SyncCleanupSummary);
        }
    }

    private void OnCleanupScanChanged() => _uiDispatch(SyncCleanupSummary);

    private void SyncCleanupSummary()
    {
        if (_cleanupVm is null)
        {
            return;
        }

        IsCleanupBusy = _cleanupVm.IsBusy;
        CleanupProgressPercent = _cleanupVm.OperationProgressPercent;
        CleanupProgressText = _cleanupVm.OperationProgressText;
        CleanupFoundSummaryText = _cleanupVm.TotalFoundSummaryText;
        CleanupStatusText = _cleanupVm.IsBusy
            ? _cleanupVm.OperationStageText
            : _cleanupVm.StatusBannerText;
    }

    private void OnDisksChanged() => _uiDispatch(SyncConnectedDisks);

    private void SyncConnectedDisks()
    {
        if (_disksVm is null)
        {
            return;
        }

        DisksFleetSummaryText = _disksVm.FleetSummaryText;
        ConnectedDisks.Clear();
        foreach (var disk in _disksVm.Disks)
        {
            ConnectedDisks.Add(new OverviewDiskSummaryItem(disk));
        }

        RefreshSystemDiskGauge();
    }

    private void RefreshSystemDiskGauge()
    {
        try
        {
            string sysRoot = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\') + "\\";
            var di = new DriveInfo(sysRoot);
            if (di.IsReady && di.TotalSize > 0)
            {
                long used = Math.Max(0, di.TotalSize - di.AvailableFreeSpace);
                double pct = Math.Clamp((double)used / di.TotalSize * 100.0, 0.0, 100.0);
                SystemDiskUsedPercent = pct;
                SystemDiskDisplay = string.Format(CultureInfo.CurrentCulture, "{0:0}", pct);
                SystemDiskSubLabel = $"Том {sysRoot[0]}: · вільно {WindowsCleanerService.FormatBytes(di.AvailableFreeSpace)}";
            }
        }
        catch
        {
            // ignore
        }
    }

    private void RefreshSettingsDisplay()
    {
        TelemetryIntervalText = $"{_settingsStore.Current.TelemetryIntervalMs} мс";
        SensorTierText = _tierStatusProvider is not null
            ? (_tierStatusProvider.GetPawnIoState() == PawnIoAvailabilityState.Ready
                ? "Рівень 2: PawnIO активний"
                : "Рівень 1: без сторонніх драйверів")
            : (_settingsStore.Current.EnablePawnIoExtendedSensors
                ? "Рівень 2: PawnIO (увімкнено)"
                : "Рівень 1: без сторонніх драйверів");
    }

    private void ApplyTelemetry(TelemetrySnapshot snap)
    {
        // CPU Gauge
        CpuStandby = false;
        CpuGaugeValue = Math.Clamp(snap.Cpu.TotalLoadPercent, 0.0, 100.0);
        CpuGaugeDisplay = string.Format(CultureInfo.CurrentCulture, "{0:0.0}", CpuGaugeValue);
        LiveCpuSummaryText = $"{CpuGaugeDisplay}%";

        string clockPart = snap.Cpu.CurrentClockMHz.HasValue
            ? string.Format(CultureInfo.CurrentCulture, "{0:0} МГц", snap.Cpu.CurrentClockMHz.Value)
            : "н/д";
        CpuGaugeSubLabel = $"{clockPart} · {snap.Cpu.PhysicalCores}Я / {snap.Cpu.LogicalProcessors}П";

        // RAM Gauge
        if (snap.Memory.TotalBytes > 0)
        {
            const double gib = 1024.0 * 1024.0 * 1024.0;
            RamStandby = false;
            RamGaugeValue = Math.Clamp(snap.Memory.LoadPercent, 0.0, 100.0);
            RamGaugeDisplay = string.Format(CultureInfo.CurrentCulture, "{0:0}", RamGaugeValue);
            LiveRamSummaryText = string.Format(
                CultureInfo.CurrentCulture,
                "{0:0.0} / {1:0.0} ГіБ",
                snap.Memory.UsedBytes / gib,
                snap.Memory.TotalBytes / gib);
            RamGaugeSubLabel = LiveRamSummaryText;
        }

        // GPU Gauge
        var gpu = snap.Gpus.FirstOrDefault();
        if (gpu is not null)
        {
            string shortName = gpu.Name
                .Replace("NVIDIA ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("AMD ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();

            LiveGpuSummaryText = gpu.TemperatureCelsius.HasValue
                ? string.Format(CultureInfo.CurrentCulture, "{0} · {1:0} °C", shortName, gpu.TemperatureCelsius.Value)
                : shortName;

            if (gpu.CoreLoadPercent.HasValue)
            {
                GpuStandby = false;
                GpuGaugeValue = Math.Clamp(gpu.CoreLoadPercent.Value, 0.0, 100.0);
                GpuGaugeDisplay = string.Format(CultureInfo.CurrentCulture, "{0:0}", GpuGaugeValue);
                GpuGaugeSubLabel = LiveGpuSummaryText;
            }
            else if (gpu.VramUsedBytes.HasValue && gpu.VramTotalBytes is > 0)
            {
                double vramPct = Math.Clamp((double)gpu.VramUsedBytes.Value / gpu.VramTotalBytes.Value * 100.0, 0.0, 100.0);
                GpuStandby = false;
                GpuGaugeValue = vramPct;
                GpuGaugeDisplay = string.Format(CultureInfo.CurrentCulture, "{0:0}", vramPct);
                GpuGaugeSubLabel = $"VRAM · {LiveGpuSummaryText}";
            }
            else
            {
                GpuStandby = true;
                GpuGaugeDisplay = "—";
                GpuGaugeSubLabel = LiveGpuSummaryText;
            }
        }
        else
        {
            GpuStandby = true;
            GpuGaugeDisplay = "—";
            LiveGpuSummaryText = "н/д";
            GpuGaugeSubLabel = "Відеоадаптер не знайдено";
        }

        UpdateSparklines(snap);
    }

    private void UpdateSparklines(TelemetrySnapshot latestSnap)
    {
        var history = _telemetryService?.GetHistory(SparklineMaxPoints);
        if (history is { Count: > 0 })
        {
            CpuSparklineValues = history.Select(static s => Math.Clamp(s.Cpu.TotalLoadPercent, 0.0, 100.0)).ToArray();
            RamSparklineValues = history.Select(static s => Math.Clamp(s.Memory.LoadPercent, 0.0, 100.0)).ToArray();
            return;
        }

        _fallbackCpuSparkline.Add(Math.Clamp(latestSnap.Cpu.TotalLoadPercent, 0.0, 100.0));
        _fallbackRamSparkline.Add(Math.Clamp(latestSnap.Memory.LoadPercent, 0.0, 100.0));
        if (_fallbackCpuSparkline.Count > SparklineMaxPoints)
        {
            _fallbackCpuSparkline.RemoveAt(0);
        }

        if (_fallbackRamSparkline.Count > SparklineMaxPoints)
        {
            _fallbackRamSparkline.RemoveAt(0);
        }

        CpuSparklineValues = [.. _fallbackCpuSparkline];
        RamSparklineValues = [.. _fallbackRamSparkline];
    }

    private static string FormatWindowsName()
    {
        var ver = Environment.OSVersion.Version;
        if (OperatingSystem.IsWindows() && ver.Major == 10 && ver.Build >= 22000)
        {
            return $"Windows 11 (збірка {ver.Build})";
        }

        if (OperatingSystem.IsWindows() && ver.Major == 10)
        {
            return $"Windows 10 (збірка {ver.Build})";
        }

        return RuntimeInformation.OSDescription;
    }

    public void Dispose()
    {
        if (_devicesVm is not null)
        {
            _devicesVm.PropertyChanged -= OnDevicesPropertyChanged;
        }

        if (_cleanupVm is not null)
        {
            _cleanupVm.PropertyChanged -= OnCleanupPropertyChanged;
            _cleanupVm.ScanStateChanged -= OnCleanupScanChanged;
        }

        if (_disksVm is not null)
        {
            _disksVm.DisksChanged -= OnDisksChanged;
        }

        _telemetrySubscription.Dispose();
        _settingsSubscription.Dispose();
    }
}
