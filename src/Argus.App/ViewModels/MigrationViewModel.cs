using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Argus.App.Services;
using Argus.Core.Contracts.Migration;
using Argus.Core.Contracts.System;
using Argus.Core.Extensions;
using Argus.Maintenance.Cleanup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed partial class MigrationTargetDiskItem : ObservableObject
{
    public MigrationTargetDiskItem(
        MigrationTargetDisk disk,
        bool isSelected,
        Func<MigrationTargetDiskItem, Task> onSelect)
    {
        Disk = disk ?? throw new ArgumentNullException(nameof(disk));
        DiskNumber = disk.DiskNumber;
        Title = $"Диск {disk.DiskNumber} · {disk.FriendlyName}";

        string sizeText = WindowsCleanerService.FormatBytes(disk.SizeBytes);
        string lettersText = disk.DriveLetters.Count > 0
            ? string.Join(", ", disk.DriveLetters.Select(static l => $"{l}:"))
            : "без літер";

        MetaText = $"{disk.BusType} · {disk.MediaType} · {disk.PartitionStyle} · {sizeText} · Томи: {lettersText}";
        DropdownLabel = $"Диск {disk.DiskNumber} · {disk.FriendlyName} ({sizeText}, {lettersText})";

        HasExistingData = disk.UsedDataBytes > 512L * 1024 * 1024;
        ExistingDataText = HasExistingData
            ? $"Зайнято {WindowsCleanerService.FormatBytes(disk.UsedDataBytes)}"
            : "Порожній / < 0,5 ГіБ";

        _isSelected = isSelected;
        SelectCommand = new AsyncRelayCommand(() => onSelect(this));
    }

    public MigrationTargetDisk Disk { get; }
    public int DiskNumber { get; }
    public string Title { get; }
    public string MetaText { get; }
    public string DropdownLabel { get; }
    public string ExistingDataText { get; }
    public bool HasExistingData { get; }
    public ICommand SelectCommand { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class MigrationPhaseStepItem : ObservableObject
{
    public MigrationPhaseStepItem(string label, int stepOrder)
    {
        Label = label;
        StepOrder = stepOrder;
    }

    public string Label { get; }
    public int StepOrder { get; }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isDone;
}

public sealed partial class MigrationViewModel : SectionViewModelBase, IDisposable
{
    private readonly IMigrationEngine? _engine;
    private readonly IPrivilegeService? _privilegeService;
    private readonly Action<Action> _uiDispatch;
    private readonly List<double> _speedPoints = new(64);
    private CancellationTokenSource? _runCts;
    private int _refreshGate;
    private bool _suppressTargetReeval;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInternalSelected))]
    [NotifyPropertyChangedFor(nameof(IsExternalSelected))]
    [NotifyPropertyChangedFor(nameof(BusFilterSummary))]
    [NotifyPropertyChangedFor(nameof(BootDriversSummary))]
    [NotifyPropertyChangedFor(nameof(FolderPolicySummary))]
    [NotifyPropertyChangedFor(nameof(FirmwarePolicySummary))]
    [NotifyPropertyChangedFor(nameof(ProfileTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyTargetsMessage))]
    private MigrationProfile _selectedProfile = MigrationProfile.InternalMotherboardDisk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGptSelected))]
    [NotifyPropertyChangedFor(nameof(IsMbrSelected))]
    [NotifyPropertyChangedFor(nameof(PartitionLayoutSummary))]
    private PartitionStyleChoice _selectedPartitionStyle = PartitionStyleChoice.GptUefi;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoModeSelected))]
    [NotifyPropertyChangedFor(nameof(IsBlockModeSelected))]
    [NotifyPropertyChangedFor(nameof(IsDismModeSelected))]
    private CloneMode _selectedCloneMode = CloneMode.Auto;

    [ObservableProperty]
    private bool _registerUefiFirst;

    [ObservableProperty]
    private bool _copyRedirectedDesktop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMigration))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMigration))]
    [NotifyPropertyChangedFor(nameof(CanEditPlan))]
    private bool _isRunning;

    [ObservableProperty]
    private string _sourceOsTitle = "Опитування активної ОС…";

    [ObservableProperty]
    private string _sourceOsMetaText = "Очікування даних Storage WMI…";

    [ObservableProperty]
    private string _sourceFirmwareBadge = "UEFI";

    [ObservableProperty]
    private string _sourceBitLockerBadge = "BitLocker: н/д";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetDisks))]
    [NotifyPropertyChangedFor(nameof(NoTargetDisks))]
    private int _targetDiskCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpectedConfirmationToken))]
    [NotifyPropertyChangedFor(nameof(ConfirmationTokenExample))]
    [NotifyPropertyChangedFor(nameof(CanStartMigration))]
    [NotifyPropertyChangedFor(nameof(ConfirmationDialogTargetText))]
    [NotifyPropertyChangedFor(nameof(ConfirmationDialogWarningText))]
    private MigrationTargetDiskItem? _selectedTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMigration))]
    private bool _preflightCanProceed;

    [ObservableProperty]
    private string _resolvedModeBadge = "Режим: Авто";

    [ObservableProperty]
    private string _preflightSummaryText = "Оберіть цільовий накопичувач для перевірки плану міграції.";

    [ObservableProperty]
    private bool _isConfirmationDialogOpen;

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _liveStatusText = "Оберіть цільовий диск і натисніть «Почати клонування» — перед стартом з'явиться вікно підтвердження.";

    [ObservableProperty]
    private bool _liveStatusIsWarning;

    [ObservableProperty]
    private string _speedText = "0,0 МБ/с";

    [ObservableProperty]
    private string _etaText = "ETA: —";

    [ObservableProperty]
    private string _copiedBytesText = "0,00 / 0,00 ГіБ";

    [ObservableProperty]
    private string _lastLogPathText = string.Empty;

    [ObservableProperty]
    private bool _hasLastLogPath;

    [ObservableProperty]
    private IReadOnlyList<double> _speedHistory = [0.0, 0.0];

    [ObservableProperty]
    private double _speedChartMax = 250.0;

    public MigrationViewModel(
        IMigrationEngine? engine = null,
        IPrivilegeService? privilegeService = null,
        Action<Action>? uiDispatch = null,
        bool autoLoadOnInit = true,
        AppUiModeService? uiMode = null)
        : base(
            ArgusSectionId.Migration,
            "Міграція та клонування ОС",
            "Єдиний скомпільований C#-рушій CopyOS: NtfsVssCloner (1-в-1 через VSS Bitmap) та адаптивний VSS + DISM",
            uiMode,
            uiDispatch)
    {
        _engine = engine;
        _privilegeService = privilegeService;
        _uiDispatch = uiDispatch ?? (static action => action());

        TargetDisks = [];
        PreflightMessages = [];
        PhaseSteps =
        [
            new("VSS", 1),
            new("Розмітка", 2),
            new("Клонування", 3),
            new("CHKDSK", 4),
            new("Реєстр", 5),
            new("Завантажувач", 6)
        ];

        if (autoLoadOnInit && _engine is not null)
        {
            EnsureInitialized();
        }
    }

    private int _initialized;

    public void EnsureInitialized()
    {
        if (_engine is not null && Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            _ = RefreshEnvironmentAsync();
        }
    }

    public ObservableCollection<MigrationTargetDiskItem> TargetDisks { get; }
    public ObservableCollection<string> PreflightMessages { get; }
    public ObservableCollection<MigrationPhaseStepItem> PhaseSteps { get; }

    protected override bool FallbackIsAdministrator => _privilegeService?.IsAdministrator ?? false;
    public bool IsInternalSelected => SelectedProfile == MigrationProfile.InternalMotherboardDisk;
    public bool IsExternalSelected => SelectedProfile == MigrationProfile.ExternalUsbDrive;
    public bool IsGptSelected => SelectedPartitionStyle == PartitionStyleChoice.GptUefi;
    public bool IsMbrSelected => SelectedPartitionStyle == PartitionStyleChoice.MbrLegacy;
    public bool IsAutoModeSelected => SelectedCloneMode == CloneMode.Auto;
    public bool IsBlockModeSelected => SelectedCloneMode == CloneMode.BlockVssBitmap;
    public bool IsDismModeSelected => SelectedCloneMode == CloneMode.DismShrinkToFit;
    public bool HasTargetDisks => TargetDiskCount > 0;
    public bool NoTargetDisks => TargetDiskCount == 0;
    public bool CanEditPlan => !IsRunning;

    public string ExpectedConfirmationToken =>
        MigrationConfirmationPolicy.BuildExpectedToken(SelectedTarget?.DiskNumber ?? 1);

    public string ConfirmationTokenExample => ExpectedConfirmationToken;

    public bool CanStartMigration =>
        !IsRunning &&
        !IsLoading &&
        IsAdministrator &&
        SelectedTarget is not null &&
        PreflightCanProceed;

    public string ConfirmationDialogTargetText => SelectedTarget is not null
        ? $"{SelectedTarget.Title} ({WindowsCleanerService.FormatBytes(SelectedTarget.Disk.SizeBytes)})"
        : "Диск не обрано";

    public string ConfirmationDialogWarningText => SelectedTarget is { HasExistingData: true }
        ? $"Усі файли на цьому диску ({SelectedTarget.ExistingDataText}) будуть повністю знищені. Системний диск C: залишиться без змін."
        : "Усі розділи та дані на обраному цільовому диску будуть повністю стерті. Системний диск C: залишиться без змін.";

    partial void OnSelectedTargetChanged(MigrationTargetDiskItem? value)
    {
        if (value is null || _suppressTargetReeval)
        {
            return;
        }

        foreach (var d in TargetDisks)
        {
            d.IsSelected = ReferenceEquals(d, value);
        }

        _ = ReevaluateCurrentTargetAsync();
    }

    public string ProfileTitle => IsInternalSelected
        ? "Профіль «Внутрішній диск» (SATA / M.2 NVMe / RAID)"
        : "Профіль «Зовнішній носій» (Портативна Windows на USB SSD / HDD)";

    public string BusFilterSummary => IsInternalSelected
        ? "Шини: SATA, NVMe, RAID, ATA, SAS, SCSI (USB-диски приховано)"
        : "Шини: лише USB (внутрішні диски захищено від вибору)";

    public string BootDriversSummary => IsInternalSelected
        ? "Start=0 для stornvme, storahci, iaStor*, amd_sata, lsi_sas, partmgr, volmgr"
        : "Start=0 для USBXHCI, USBHUB3, UASPStor, USBSTOR, USB4* + PollBootPartitionTimeout=30000";

    public string FolderPolicySummary => IsInternalSelected
        ? "Перенесений Робочий стіл копіюється лише за вибором користувача"
        : "Автоматичне виправлення перенесених тек профілю (Desktop, Documents, Downloads тощо) для запуску на будь-якому ПК";

    public string FirmwarePolicySummary => IsInternalSelected
        ? "Опційний запис нового SSD першим у чергу завантаження UEFI (bcdboot)"
        : "Обов'язковий прапорець /nofirmwaresync — порядок завантаження основного ПК не змінюється";

    public string PartitionLayoutSummary => IsGptSelected
        ? "GPT / UEFI: 100 МБ FAT32 (ESP) + 16 МБ MSR + системний NTFS (рекомендовано для Windows 10 і 11)"
        : "MBR / Legacy BIOS: 100 МБ активний NTFS (Boot) + системний NTFS (для старих плат без UEFI)";

    public string EmptyTargetsMessage => IsInternalSelected
        ? "Інших внутрішніх накопичувачів (SATA / NVMe / RAID) не знайдено."
        : "Зовнішніх USB-накопичувачів не знайдено. Підключіть USB SSD/HDD і натисніть «Оновити диски».";

    [RelayCommand]
    public async Task RefreshEnvironmentAsync()
    {
        if (_engine is null || IsRunning || Interlocked.CompareExchange(ref _refreshGate, 1, 0) != 0)
        {
            return;
        }

        _uiDispatch(() => IsLoading = true);
        try
        {
            var source = await _engine.InspectLiveOsAsync().ConfigureAwait(false);
            _uiDispatch(() =>
            {
                string arch = source.Is64Bit ? "x64" : "x86";
                SourceOsTitle = $"{source.OsCaption} ({arch})";
                SourceOsMetaText =
                    $"Том {source.SystemDriveLetter}: · Диск {source.SourceDiskNumber} ({source.SourceDiskModel}, {source.SourceBusType}) · Зайнято {WindowsCleanerService.FormatBytes(source.UsedBytes)} із {WindowsCleanerService.FormatBytes(source.PartitionSizeBytes)} · Сектор {source.LogicalSectorSize} Б";
                SourceFirmwareBadge = source.IsUefiHost ? "UEFI" : "Legacy BIOS";
                SourceBitLockerBadge = IsAdministrator
                    ? $"BitLocker: {source.BitLockerStatus}"
                    : "BitLocker: перевірка з UAC";
            });

            var targets = await _engine.DiscoverTargetDisksAsync(SelectedProfile).ConfigureAwait(false);

            MigrationTargetDisk? chosen = SelectedTarget is not null
                ? targets.FirstOrDefault(d => d.DiskNumber == SelectedTarget.DiskNumber) ?? targets.FirstOrDefault()
                : targets.FirstOrDefault();

            MigrationPreflightResult? preflight = null;
            if (chosen is not null)
            {
                preflight = await _engine.EvaluatePlanAsync(BuildRequest(chosen.DiskNumber)).ConfigureAwait(false);
            }

            _uiDispatch(() =>
            {
                TargetDisks.Clear();
                MigrationTargetDiskItem? selectedItem = null;
                foreach (var disk in targets)
                {
                    bool isSel = chosen is not null && disk.DiskNumber == chosen.DiskNumber;
                    var item = new MigrationTargetDiskItem(disk, isSel, SelectTargetDiskAsync);
                    TargetDisks.Add(item);
                    if (isSel)
                    {
                        selectedItem = item;
                    }
                }

                TargetDiskCount = TargetDisks.Count;
                _suppressTargetReeval = true;
                try
                {
                    SelectedTarget = selectedItem;
                }
                finally
                {
                    _suppressTargetReeval = false;
                }

                ApplyPreflight(preflight, chosen);
            });
        }
        catch (Exception ex)
        {
            _uiDispatch(() =>
            {
                LiveStatusIsWarning = true;
                LiveStatusText = $"Помилка опитування дисків: {ex.Message}";
            });
        }
        finally
        {
            Interlocked.Exchange(ref _refreshGate, 0);
            _uiDispatch(() => IsLoading = false);
        }
    }

    [RelayCommand]
    private void SelectInternalProfile()
    {
        if (IsRunning || SelectedProfile == MigrationProfile.InternalMotherboardDisk)
        {
            return;
        }

        SelectedProfile = MigrationProfile.InternalMotherboardDisk;
        IsConfirmationDialogOpen = false;
        _ = RefreshEnvironmentAsync();
    }

    [RelayCommand]
    private void SelectExternalProfile()
    {
        if (IsRunning || SelectedProfile == MigrationProfile.ExternalUsbDrive)
        {
            return;
        }

        SelectedProfile = MigrationProfile.ExternalUsbDrive;
        RegisterUefiFirst = false;
        CopyRedirectedDesktop = false;
        IsConfirmationDialogOpen = false;
        _ = RefreshEnvironmentAsync();
    }

    [RelayCommand]
    private void SelectGpt()
    {
        if (IsRunning)
        {
            return;
        }

        SelectedPartitionStyle = PartitionStyleChoice.GptUefi;
        _ = ReevaluateCurrentTargetAsync();
    }

    [RelayCommand]
    private void SelectMbr()
    {
        if (IsRunning)
        {
            return;
        }

        SelectedPartitionStyle = PartitionStyleChoice.MbrLegacy;
        RegisterUefiFirst = false;
        _ = ReevaluateCurrentTargetAsync();
    }

    [RelayCommand]
    private void SelectCloneModeAuto()
    {
        if (IsRunning)
        {
            return;
        }

        SelectedCloneMode = CloneMode.Auto;
        _ = ReevaluateCurrentTargetAsync();
    }

    [RelayCommand]
    private void SelectCloneModeBlock()
    {
        if (IsRunning)
        {
            return;
        }

        SelectedCloneMode = CloneMode.BlockVssBitmap;
        _ = ReevaluateCurrentTargetAsync();
    }

    [RelayCommand]
    private void SelectCloneModeDism()
    {
        if (IsRunning)
        {
            return;
        }

        SelectedCloneMode = CloneMode.DismShrinkToFit;
        _ = ReevaluateCurrentTargetAsync();
    }

    public Task SelectTargetDiskAsync(MigrationTargetDiskItem item)
    {
        if (IsRunning || item is null)
        {
            return Task.CompletedTask;
        }

        _uiDispatch(() =>
        {
            SelectedTarget = item;
        });

        return Task.CompletedTask;
    }

    public async Task ReevaluateCurrentTargetAsync()
    {
        if (_engine is null || SelectedTarget is null || IsRunning)
        {
            return;
        }

        var chosen = SelectedTarget.Disk;
        var preflight = await _engine.EvaluatePlanAsync(BuildRequest(chosen.DiskNumber)).ConfigureAwait(false);
        _uiDispatch(() => ApplyPreflight(preflight, chosen));
    }

    [RelayCommand]
    public void RequestStartMigration()
    {
        if (!CanStartMigration)
        {
            return;
        }

        IsConfirmationDialogOpen = true;
    }

    [RelayCommand]
    public void CancelConfirmationDialog()
    {
        IsConfirmationDialogOpen = false;
    }

    [RelayCommand]
    public async Task ConfirmAndStartMigrationAsync()
    {
        IsConfirmationDialogOpen = false;
        await StartMigrationAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task StartMigrationAsync()
    {
        if (_engine is null || SelectedTarget is null || IsRunning)
        {
            return;
        }

        var request = BuildRequest(SelectedTarget.DiskNumber);
        string token = MigrationConfirmationPolicy.BuildExpectedToken(request.TargetDiskNumber);

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        _uiDispatch(() =>
        {
            IsRunning = true;
            ProgressPercent = 0;
            SpeedText = "0,0 МБ/с";
            EtaText = "ETA: обчислення…";
            CopiedBytesText = "0,00 / 0,00 ГіБ";
            LiveStatusIsWarning = false;
            LiveStatusText = $"Запуск міграції CopyOS на Диск {request.TargetDiskNumber}…";
            HasLastLogPath = false;
            LastLogPathText = string.Empty;
            _speedPoints.Clear();
            SpeedHistory = [0.0, 0.0];
            SpeedChartMax = 250.0;
            ResetPhaseSteps();
        });

        IProgress<MigrationProgress> progress = new DirectProgress<MigrationProgress>(p => _uiDispatch(() => ApplyProgress(p)));

        try
        {
            var result = await _engine.ExecuteAsync(request, token, progress, ct).ConfigureAwait(false);
            _uiDispatch(() =>
            {
                LiveStatusText = NormalizeElevationHint(result.Summary);
                LiveStatusIsWarning = !result.Succeeded;
                if (result.Succeeded)
                {
                    ProgressPercent = 100;
                    MarkAllPhasesDone();
                }

                if (!string.IsNullOrWhiteSpace(result.LogFilePath))
                {
                    LastLogPathText = $"Лог міграції: {result.LogFilePath}";
                    HasLastLogPath = true;
                }
            });
        }
        finally
        {
            _uiDispatch(() => IsRunning = false);
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    public void CancelMigration()
    {
        if (!IsRunning)
        {
            return;
        }

        LiveStatusIsWarning = true;
        LiveStatusText = "Скасування клонування та безпечне очищення VSS-знімка і реєстру…";
        try
        {
            _runCts?.Cancel();
        }
        catch
        {
            // ignore already disposed
        }
    }

    private MigrationPlanRequest BuildRequest(int targetDiskNumber) => new(
        Profile: SelectedProfile,
        TargetDiskNumber: targetDiskNumber,
        PartitionStyle: SelectedPartitionStyle,
        RequestedCloneMode: SelectedCloneMode,
        RegisterUefiBootPriorityFirst: IsInternalSelected && IsGptSelected && RegisterUefiFirst,
        CopyRedirectedDesktop: IsInternalSelected && CopyRedirectedDesktop);

    private void ApplyPreflight(MigrationPreflightResult? preflight, MigrationTargetDisk? target)
    {
        PreflightMessages.Clear();

        if (preflight is null || target is null)
        {
            PreflightCanProceed = false;
            ResolvedModeBadge = "Режим: очікування цілі";
            PreflightSummaryText = EmptyTargetsMessage;
            return;
        }

        PreflightCanProceed = preflight.CanProceed;
        ResolvedModeBadge = preflight.ResolvedCloneMode == CloneMode.BlockVssBitmap
            ? "Авторежим: BLOCK (VSS Bitmap 1-в-1)"
            : preflight.ScratchDriveLetter.HasValue
                ? $"Авторежим: VSS + DISM (буфер {preflight.ScratchDriveLetter}:)"
                : "Авторежим: VSS + DISM";

        PreflightSummaryText = preflight.CanProceed
            ? $"Ціль: Диск {target.DiskNumber} ({target.FriendlyName}, {WindowsCleanerService.FormatBytes(target.SizeBytes)}). План пройшов перевірку безпеки."
            : "План заблоковано перевіркою безпеки. Усуньте зазначені обмеження перед стартом.";

        foreach (string err in preflight.BlockingErrors)
        {
            PreflightMessages.Add($"[БЛОК] {NormalizeElevationHint(err)}");
        }

        foreach (string warn in preflight.Warnings)
        {
            PreflightMessages.Add($"[УВАГА] {NormalizeElevationHint(warn)}");
        }
    }

    private static string NormalizeElevationHint(string message) =>
        message.Replace(
            "запустіть кореневий Argus.exe або натисніть щит UAC",
            "натисніть кнопку «Підвищити права (UAC)» вище або щит UAC у верхній панелі",
            StringComparison.OrdinalIgnoreCase);

    private void ApplyProgress(MigrationProgress p)
    {
        ProgressPercent = Math.Clamp(p.PercentComplete, 0, 100);
        LiveStatusText = p.StatusMessage;
        LiveStatusIsWarning = p.Phase is MigrationPhase.Failed or MigrationPhase.Cancelled;

        if (p.TotalBytes > 0)
        {
            double copiedGiB = p.BytesCopied / (1024.0 * 1024.0 * 1024.0);
            double totalGiB = p.TotalBytes / (1024.0 * 1024.0 * 1024.0);
            CopiedBytesText = string.Format(CultureInfo.CurrentCulture, "{0:0.00} / {1:0.00} ГіБ", copiedGiB, totalGiB);
        }

        if (p.SpeedMegabytesPerSec > 0)
        {
            SpeedText = string.Format(CultureInfo.CurrentCulture, "{0:0.0} МБ/с", p.SpeedMegabytesPerSec);
            _speedPoints.Add(p.SpeedMegabytesPerSec);
            if (_speedPoints.Count > 60)
            {
                _speedPoints.RemoveAt(0);
            }

            SpeedChartMax = Math.Max(100.0, _speedPoints.Max() * 1.15);
            SpeedHistory = _speedPoints.Count == 1 ? [0.0, _speedPoints[0]] : [.. _speedPoints];
        }

        EtaText = p.EstimatedRemaining.HasValue
            ? $"ETA: {p.EstimatedRemaining.Value:hh\\:mm\\:ss}"
            : "ETA: —";

        UpdatePhaseSteps(p.Phase);
    }

    private void ResetPhaseSteps()
    {
        foreach (var step in PhaseSteps)
        {
            step.IsActive = false;
            step.IsDone = false;
        }
    }

    private void MarkAllPhasesDone()
    {
        foreach (var step in PhaseSteps)
        {
            step.IsActive = false;
            step.IsDone = true;
        }
    }

    private void UpdatePhaseSteps(MigrationPhase phase)
    {
        int activeOrder = phase switch
        {
            MigrationPhase.InspectingSource or MigrationPhase.ValidatingTarget or MigrationPhase.CreatingVssSnapshot => 1,
            MigrationPhase.PartitioningTarget => 2,
            MigrationPhase.CloningData => 3,
            MigrationPhase.CheckingAndExtendingVolume or MigrationPhase.FixingUserFolders => 4,
            MigrationPhase.UpdatingOfflineRegistry => 5,
            MigrationPhase.InstallingBootloader or MigrationPhase.CleaningUp => 6,
            MigrationPhase.Completed => 7,
            _ => 0
        };

        if (activeOrder == 0)
        {
            return;
        }

        foreach (var step in PhaseSteps)
        {
            step.IsDone = step.StepOrder < activeOrder;
            step.IsActive = step.StepOrder == activeOrder;
        }
    }

    public void Dispose()
    {
        DetachUiMode();
        _runCts?.Dispose();
    }

    private sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
