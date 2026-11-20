using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Extensions;
using Argus.Maintenance.Cleanup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed class PhysicalDiskCardItem
{
    public PhysicalDiskCardItem(
        PhysicalDiskInfo info,
        Func<char, Task> onOptimize,
        Func<char, Task> onAnalyzeSpace)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Title = $"Диск {info.DiskNumber} · {info.Model}";

        MediaBadge = info.MediaKind switch
        {
            StorageMediaKind.NvmeSsd => "NVMe SSD",
            StorageMediaKind.SataSsd => "SATA SSD",
            StorageMediaKind.Hdd => "HDD",
            StorageMediaKind.RemovableUsb => "USB",
            _ => "Накопичувач"
        };

        IsSsdOrNvme = info.MediaKind is StorageMediaKind.NvmeSsd or StorageMediaKind.SataSsd;

        string sizeText = WindowsCleanerService.FormatBytes(info.SizeBytes);
        string volumesText = info.DriveLetters.Count > 0
            ? string.Join(", ", info.DriveLetters.Select(static l => $"{l}:"))
            : "без літери";

        BusAndCapacityText = $"{info.BusType} · {info.PartitionStyle} · {sizeText} · Томи: {volumesText}";
        SerialText = $"S/N: {info.SerialNumber}";

        IsHealthy = info.Smart.Health == SmartHealthStatus.Healthy;
        IsCaution = info.Smart.Health == SmartHealthStatus.Caution;
        IsCritical = info.Smart.Health == SmartHealthStatus.Critical;
        HealthLabel = info.Smart.Health switch
        {
            SmartHealthStatus.Healthy => "Здоровий",
            SmartHealthStatus.Caution => "Увага",
            SmartHealthStatus.Critical => "Критичний",
            _ => "Невідомо"
        };

        HasTemperature = info.Smart.TemperatureCelsius.HasValue;
        TemperatureText = info.Smart.TemperatureCelsius.HasValue
            ? string.Format(CultureInfo.CurrentCulture, "{0:0} °C", info.Smart.TemperatureCelsius.Value)
            : string.Empty;

        var smartParts = new List<string>();
        if (info.Smart.WearLevelPercent.HasValue)
        {
            smartParts.Add($"Знос: {info.Smart.WearLevelPercent.Value}%");
        }

        if (info.Smart.PowerOnHours.HasValue)
        {
            smartParts.Add($"Напрацювання: {info.Smart.PowerOnHours.Value} год");
        }

        if (info.Smart.MediaErrorCount.HasValue)
        {
            smartParts.Add($"Помилки: {info.Smart.MediaErrorCount.Value}");
        }

        SmartDetailsText = smartParts.Count > 0
            ? $"S.M.A.R.T.: {string.Join(" · ", smartParts)}"
            : "S.M.A.R.T.: критичних помилок не виявлено";

        PrimaryDriveLetter = info.DriveLetters.FirstOrDefault();
        HasDriveLetter = PrimaryDriveLetter is >= 'A' and <= 'Z';

        var policyAction = DiskOptimizationPolicy.ResolveSafeAction(info.MediaKind);
        string letterBadge = HasDriveLetter ? $" ({PrimaryDriveLetter}:)" : string.Empty;
        OptimizeButtonLabel = policyAction switch
        {
            DiskOptimizationAction.TrimSsd => $"Оптимізація SSD{letterBadge}",
            DiskOptimizationAction.DefragmentHdd => $"Дефрагментація HDD{letterBadge}",
            _ => $"Аналіз диска{letterBadge}"
        };
        OptimizeTooltip = policyAction switch
        {
            DiskOptimizationAction.TrimSsd => "Безпечна оптимізація SSD (TRIM): очищає невикористані блоки без дефрагментації та зносу комірок.",
            DiskOptimizationAction.DefragmentHdd => "Штатна дефрагментація жорсткого диска Windows (defrag.exe /O /U).",
            _ => "Безпечний аналіз стану й фрагментації тому."
        };
        SpaceMapButtonLabel = $"Карта{letterBadge}";

        OptimizeCommand = new AsyncRelayCommand(() => onOptimize(PrimaryDriveLetter), () => HasDriveLetter);
        SpaceMapCommand = new AsyncRelayCommand(() => onAnalyzeSpace(PrimaryDriveLetter), () => HasDriveLetter);
    }

    public PhysicalDiskInfo Info { get; }
    public string Title { get; }
    public string MediaBadge { get; }
    public bool IsSsdOrNvme { get; }
    public string BusAndCapacityText { get; }
    public string SerialText { get; }
    public string HealthLabel { get; }
    public bool IsHealthy { get; }
    public bool IsCaution { get; }
    public bool IsCritical { get; }
    public bool HasTemperature { get; }
    public string TemperatureText { get; }
    public string SmartDetailsText { get; }
    public char PrimaryDriveLetter { get; }
    public bool HasDriveLetter { get; }
    public string OptimizeButtonLabel { get; }
    public string OptimizeTooltip { get; }
    public string SpaceMapButtonLabel { get; }

    public ICommand OptimizeCommand { get; }
    public ICommand SpaceMapCommand { get; }
}

public sealed partial class VolumeTabItem : ObservableObject
{
    private readonly Func<char, Task> _onSelect;

    public VolumeTabItem(char driveLetter, bool isSelected, Func<char, Task> onSelect)
    {
        DriveLetter = char.ToUpperInvariant(driveLetter);
        Label = $"{DriveLetter}:";
        _isSelected = isSelected;
        _onSelect = onSelect;
        SelectCommand = new AsyncRelayCommand(() => _onSelect(DriveLetter));
    }

    public char DriveLetter { get; }
    public string Label { get; }
    public ICommand SelectCommand { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class FolderSpaceRowItem
{
    public FolderSpaceRowItem(FolderSpaceNode node)
    {
        Name = node.Name;
        FullPath = node.FullPath;
        SizeText = WindowsCleanerService.FormatBytes(node.SizeBytes);
        DetailText = string.Format(
            CultureInfo.CurrentCulture,
            "{0:0.0}% зайнятого · файлів: {1}",
            node.ShareOfVolumeUsedPercent,
            node.FileCount);
        BarPercent = Math.Clamp(node.ShareOfTopListPercent, 1.0, 100.0);
    }

    public string Name { get; }
    public string FullPath { get; }
    public string SizeText { get; }
    public string DetailText { get; }
    public double BarPercent { get; }
}

public sealed partial class DisksViewModel : SectionViewModelBase
{
    private readonly IDiskOptimizer? _diskOptimizer;
    private readonly IDiskSpaceAnalyzer? _spaceAnalyzer;
    private readonly Action<Action> _uiDispatch;
    private int _busyGate;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _fleetSummaryText = "Опитування фізичних накопичувачів…";

    [ObservableProperty]
    private string _statusBannerText = "Політика безпеки активна: для SSD/NVMe — безпечна оптимізація (очищення вільних блоків без дефрагментації), для HDD — дефрагментація.";

    [ObservableProperty]
    private bool _statusIsSuccess = true;

    [ObservableProperty]
    private string _selectedVolumeSummaryText = "Виберіть том для карти найбільших тек";

    [ObservableProperty]
    private char _selectedVolumeLetter = 'C';

    [ObservableProperty]
    private int _operationProgressPercent;

    [ObservableProperty]
    private string _operationProgressText = "0 %";

    [ObservableProperty]
    private string _operationStageText = "Очікування опитування накопичувачів…";

    public event Action? DisksChanged;

    [GeneratedRegex(@"\b(?<pct>\d{1,3})\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex DefragPercentRegex();

    public DisksViewModel(
        IDiskOptimizer? diskOptimizer = null,
        IDiskSpaceAnalyzer? spaceAnalyzer = null,
        Action<Action>? uiDispatch = null,
        bool autoLoadOnInit = true)
        : base(
            ArgusSectionId.Disks,
            "Диски й діагностика",
            "Розпізнавання HDD / SSD / NVMe, S.M.A.R.T., безпечна оптимізація та карта найбільших тек")
    {
        _diskOptimizer = diskOptimizer;
        _spaceAnalyzer = spaceAnalyzer;
        _uiDispatch = uiDispatch ?? (static action => action());

        Disks = [];
        VolumeTabs = [];
        LargestFolders = [];

        if (autoLoadOnInit && _diskOptimizer is not null)
        {
            EnsureInitialized();
        }
    }

    private int _initialized;

    public void EnsureInitialized()
    {
        if (_diskOptimizer is not null && Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            _ = RefreshDisksAsync();
        }
    }

    public ObservableCollection<PhysicalDiskCardItem> Disks { get; }
    public ObservableCollection<VolumeTabItem> VolumeTabs { get; }
    public ObservableCollection<FolderSpaceRowItem> LargestFolders { get; }

    [RelayCommand]
    public async Task RefreshDisksAsync()
    {
        if (_diskOptimizer is null || Interlocked.CompareExchange(ref _busyGate, 1, 0) != 0)
        {
            return;
        }

        _uiDispatch(() =>
        {
            IsBusy = true;
            SetProgress(3, "Опитування фізичних накопичувачів і S.M.A.R.T.…");
        });
        using var tickerCts = new CancellationTokenSource();
        var tickerTask = RunSmoothTickerAsync(maxAutoPercent: 92, intervalMs: 90, tickerCts.Token);
        try
        {
            var physicalDisks = await _diskOptimizer.GetDisksAsync().ConfigureAwait(false);
            var availableLetters = physicalDisks
                .SelectMany(static d => d.DriveLetters)
                .Distinct()
                .OrderBy(static c => c)
                .ToArray();

            char initialLetter = availableLetters.Contains(SelectedVolumeLetter)
                ? SelectedVolumeLetter
                : (availableLetters.FirstOrDefault() is var first && first >= 'A' ? first : 'C');

            _uiDispatch(() =>
            {
                Disks.Clear();
                foreach (var disk in physicalDisks)
                {
                    Disks.Add(new PhysicalDiskCardItem(disk, OptimizeVolumeAsync, SelectVolumeForSpaceMapAsync));
                }

                int ssdCount = physicalDisks.Count(static d => d.MediaKind is StorageMediaKind.NvmeSsd or StorageMediaKind.SataSsd);
                int hddCount = physicalDisks.Count(static d => d.MediaKind == StorageMediaKind.Hdd);
                FleetSummaryText = $"Фізичних дисків: {physicalDisks.Count} (SSD/NVMe: {ssdCount} · HDD: {hddCount})";

                VolumeTabs.Clear();
                foreach (char letter in availableLetters)
                {
                    VolumeTabs.Add(new VolumeTabItem(letter, letter == initialLetter, SelectVolumeForSpaceMapAsync));
                }

                SelectedVolumeLetter = initialLetter;
                if (availableLetters.Length > 0 && _spaceAnalyzer is not null)
                {
                    SelectedVolumeSummaryText = $"Сканування найбільших тек на {initialLetter}:\\…";
                    RaiseProgressFloor(35, $"Аналіз карти найбільших тек на {initialLetter}:\\…");
                }
                else
                {
                    SetProgress(100, $"Опитування завершено · дисків: {physicalDisks.Count}");
                }

                DisksChanged?.Invoke();
            });

            if (_spaceAnalyzer is not null && availableLetters.Length > 0)
            {
                var scanProgress = new Progress<(int Percent, string FolderName)>(p =>
                {
                    int mapped = 35 + (int)(Math.Clamp(p.Percent, 0, 100) * 0.62);
                    _uiDispatch(() => RaiseProgressFloor(mapped, $"Аналіз {initialLetter}:\\{p.FolderName}…"));
                });

                var spaceReport = await _spaceAnalyzer.AnalyzeVolumeSpaceAsync(initialLetter, topFolderCount: 8, progress: scanProgress)
                    .ConfigureAwait(false);

                _uiDispatch(() =>
                {
                    ApplySpaceReport(spaceReport);
                    SetProgress(100, $"Діагностика й аналіз тому {initialLetter}: завершено");
                });
            }
        }
        finally
        {
            await StopTickerAsync(tickerCts, tickerTask).ConfigureAwait(false);
            Interlocked.Exchange(ref _busyGate, 0);
            _uiDispatch(() => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task OptimizeVolumeAsync(char driveLetter)
    {
        if (_diskOptimizer is null || driveLetter is < 'A' or > 'z')
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _busyGate, 1, 0) != 0)
        {
            return;
        }

        char letter = char.ToUpperInvariant(driveLetter);
        _uiDispatch(() =>
        {
            IsBusy = true;
            StatusIsSuccess = true;
            SetProgress(2, $"Підготовка до оптимізації тома {letter}:…");
            StatusBannerText = $"Запуск штатної оптимізації тома {letter}:…";
        });

        using var tickerCts = new CancellationTokenSource();
        var tickerTask = RunSmoothTickerAsync(maxAutoPercent: 94, intervalMs: 140, tickerCts.Token);

        try
        {
            var progress = new Progress<string>(line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _uiDispatch(() =>
                    {
                        StatusBannerText = $"{letter}: · {line}";
                        var m = DefragPercentRegex().Match(line);
                        if (m.Success && int.TryParse(m.Groups["pct"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct))
                        {
                            int clamped = Math.Clamp(pct, 1, 99);
                            RaiseProgressFloor(clamped, $"Оптимізація тома {letter}: · {clamped} %");
                        }
                    });
                }
            });

            var result = await _diskOptimizer.OptimizeVolumeAsync(letter, progress).ConfigureAwait(false);
            _uiDispatch(() =>
            {
                StatusIsSuccess = result.Succeeded;
                StatusBannerText = result.Summary;
                if (result.Succeeded)
                {
                    SetProgress(100, $"Оптимізація тома {letter}: завершена");
                }
                else
                {
                    SetProgress(0, $"Оптимізація тома {letter}: не виконана");
                }
            });
        }
        finally
        {
            await StopTickerAsync(tickerCts, tickerTask).ConfigureAwait(false);
            Interlocked.Exchange(ref _busyGate, 0);
            _uiDispatch(() => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task SelectVolumeForSpaceMapAsync(char driveLetter)
    {
        if (_spaceAnalyzer is null || driveLetter is < 'A' or > 'z')
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _busyGate, 1, 0) != 0)
        {
            return;
        }

        char letter = char.ToUpperInvariant(driveLetter);
        _uiDispatch(() =>
        {
            IsBusy = true;
            SelectedVolumeLetter = letter;
            foreach (var tab in VolumeTabs)
            {
                tab.IsSelected = tab.DriveLetter == letter;
            }

            SelectedVolumeSummaryText = $"Сканування найбільших тек на {letter}:\\…";
            SetProgress(5, $"Аналіз карти найбільших тек на {letter}:\\…");
        });

        using var tickerCts = new CancellationTokenSource();
        var tickerTask = RunSmoothTickerAsync(maxAutoPercent: 93, intervalMs: 75, tickerCts.Token);

        try
        {
            var scanProgress = new Progress<(int Percent, string FolderName)>(p =>
            {
                _uiDispatch(() => RaiseProgressFloor(Math.Clamp(p.Percent, 5, 98), $"Аналіз {letter}:\\{p.FolderName}…"));
            });

            var report = await _spaceAnalyzer.AnalyzeVolumeSpaceAsync(letter, topFolderCount: 8, progress: scanProgress).ConfigureAwait(false);
            _uiDispatch(() =>
            {
                ApplySpaceReport(report);
                SetProgress(100, $"Аналіз тому {letter}: завершено");
            });
        }
        finally
        {
            await StopTickerAsync(tickerCts, tickerTask).ConfigureAwait(false);
            Interlocked.Exchange(ref _busyGate, 0);
            _uiDispatch(() => IsBusy = false);
        }
    }

    private void SetProgress(int percent, string stage)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        OperationProgressPercent = clamped;
        OperationProgressText = $"{clamped} %";
        OperationStageText = stage;
    }

    private void RaiseProgressFloor(int minPercent, string stage)
    {
        int next = Math.Max(OperationProgressPercent, Math.Clamp(minPercent, 0, 99));
        OperationProgressPercent = next;
        OperationProgressText = $"{next} %";
        OperationStageText = stage;
    }

    private async Task RunSmoothTickerAsync(int maxAutoPercent, int intervalMs, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                _uiDispatch(() =>
                {
                    if (OperationProgressPercent < maxAutoPercent)
                    {
                        int step = OperationProgressPercent < 40 ? 3 : OperationProgressPercent < 75 ? 2 : 1;
                        int next = Math.Min(maxAutoPercent, OperationProgressPercent + step);
                        OperationProgressPercent = next;
                        OperationProgressText = $"{next} %";
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // expected when operation finishes
        }
    }

    private static async Task StopTickerAsync(CancellationTokenSource cts, Task tickerTask)
    {
        try
        {
            cts.Cancel();
            await tickerTask.ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private void ApplySpaceReport(VolumeSpaceReport report)
    {
        SelectedVolumeLetter = report.DriveLetter;
        string labelPart = string.IsNullOrWhiteSpace(report.VolumeLabel) ? string.Empty : $" «{report.VolumeLabel}»";
        SelectedVolumeSummaryText =
            $"Том {report.DriveLetter}:{labelPart} ({report.FileSystem}) · Зайнято {WindowsCleanerService.FormatBytes(report.UsedBytes)} із {WindowsCleanerService.FormatBytes(report.TotalSizeBytes)} · Вільно {WindowsCleanerService.FormatBytes(report.FreeBytes)}";

        LargestFolders.Clear();
        foreach (var node in report.LargestFolders)
        {
            LargestFolders.Add(new FolderSpaceRowItem(node));
        }
    }
}
