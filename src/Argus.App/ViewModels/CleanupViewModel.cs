using System.Collections.ObjectModel;
using Argus.App.Services;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Events;
using Argus.Core.Extensions;
using Argus.Maintenance.Cleanup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed partial class CleanupRuleRowItem : ObservableObject
{
    private readonly Action _onSelectionChanged;

    public CleanupRuleRowItem(CleanerRuleDefinition definition, Action onSelectionChanged)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _onSelectionChanged = onSelectionChanged ?? (static () => { });

        Id = definition.Id;
        DisplayName = definition.DisplayName;
        Description = definition.Description;
        RequiresElevation = definition.RequiresElevation;
        CategoryLabel = LocalizeCategory(definition.Category);
        ScopePreview = $"{string.Join(", ", definition.Paths.Take(2))} · {string.Join(", ", definition.FilePatterns)}";
        _isSelected = definition.EnabledByDefault;
    }

    public CleanerRuleDefinition Definition { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string CategoryLabel { get; }
    public string ScopePreview { get; }
    public bool RequiresElevation { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private int _scannedFileCount;

    [ObservableProperty]
    private long _scannedBytes;

    [ObservableProperty]
    private string _scannedSizeText = "—";

    [ObservableProperty]
    private string _scannedFilesText = "Очікує аналізу";

    [ObservableProperty]
    private string _samplePathsText = string.Empty;

    [ObservableProperty]
    private bool _hasSamplePaths;

    [ObservableProperty]
    private bool _hasFiles;

    partial void OnIsSelectedChanged(bool value)
    {
        _onSelectionChanged();
    }

    public void ApplyScanItem(CleanerScanItem? item)
    {
        if (item is null || item.FileCount <= 0)
        {
            ScannedFileCount = 0;
            ScannedBytes = 0;
            ScannedSizeText = "0 Б";
            ScannedFilesText = "Чисто";
            SamplePathsText = string.Empty;
            HasSamplePaths = false;
            HasFiles = false;
            return;
        }

        ScannedFileCount = item.FileCount;
        ScannedBytes = item.TotalBytes;
        ScannedSizeText = WindowsCleanerService.FormatBytes(item.TotalBytes);
        ScannedFilesText = $"{item.FileCount} файлів";
        HasFiles = true;

        if (item.SamplePaths.Count > 0)
        {
            SamplePathsText = "Приклади: " + string.Join(" · ", item.SamplePaths.Take(2).Select(Path.GetFileName));
            HasSamplePaths = true;
        }
        else
        {
            SamplePathsText = string.Empty;
            HasSamplePaths = false;
        }
    }

    public void ResetScan()
    {
        ScannedFileCount = 0;
        ScannedBytes = 0;
        ScannedSizeText = "—";
        ScannedFilesText = "Очікує аналізу";
        SamplePathsText = string.Empty;
        HasSamplePaths = false;
        HasFiles = false;
    }

    private static string LocalizeCategory(CleanerCategory category) => category switch
    {
        CleanerCategory.WindowsTemp => "Система Windows",
        CleanerCategory.UserTemp => "Профіль користувача",
        CleanerCategory.BrowserCache => "Браузери",
        CleanerCategory.SystemLogs => "Системні журнали",
        CleanerCategory.CrashDumps => "Дампи та WER",
        CleanerCategory.WindowsUpdateCache => "Windows Update",
        CleanerCategory.Thumbnails => "Провідник",
        CleanerCategory.RecycleBin => "Кошик",
        _ => "Користувацьке"
    };
}

public sealed partial class CleanupViewModel : SectionViewModelBase, IDisposable
{
    private readonly IArgusSettingsStore _settingsStore;
    private readonly ICleanerRuleLoader _ruleLoader;
    private readonly ICleanerService? _cleanerService;
    private readonly Action<Action> _uiDispatch;
    private readonly IDisposable? _settingsSubscription;
    private CleanerScanReport? _approvedScanReport;
    private int _busyGate;
    private int _initialized;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasScanReport;

    [ObservableProperty]
    private bool _createRestorePointBeforeCleanup;

    [ObservableProperty]
    private string _restorePointGuardText = string.Empty;

    [ObservableProperty]
    private string _totalFoundSummaryText = "Ще не проаналізовано";

    [ObservableProperty]
    private string _selectedSummaryText = "0 Б (0 файлів)";

    [ObservableProperty]
    private string _rulesCatalogSummaryText = string.Empty;

    [ObservableProperty]
    private string _statusBannerText = "Крок 1: натисніть «Аналізувати систему», щоб підрахувати точний обсяг без видалення.";

    [ObservableProperty]
    private bool _statusIsSuccess = true;

    [ObservableProperty]
    private string _newExclusionPath = string.Empty;

    [ObservableProperty]
    private int _operationProgressPercent;

    [ObservableProperty]
    private string _operationProgressText = "0 %";

    [ObservableProperty]
    private string _operationStageText = "Очікування запуску аналізу";

    public event Action? ScanStateChanged;

    public int LastScannedFileCount => _approvedScanReport?.TotalFiles ?? 0;
    public long LastScannedTotalBytes => _approvedScanReport?.TotalBytes ?? 0L;

    public CleanupViewModel(
        IArgusSettingsStore settingsStore,
        ICleanerRuleLoader? ruleLoader = null,
        ICleanerService? cleanerService = null,
        IArgusEventBus? eventBus = null,
        string? rulesDirectory = null,
        Action<Action>? uiDispatch = null,
        bool autoLoadOnInit = true,
        AppUiModeService? uiMode = null)
        : base(
            ArgusSectionId.Cleanup,
            "Очищення системи",
            "Безпечне двокрокове очищення на зовнішніх JSON-правилах із підтримкою System Restore",
            uiMode,
            uiDispatch)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _ruleLoader = ruleLoader ?? new JsonCleanerRuleLoader();
        _cleanerService = cleanerService;
        _uiDispatch = uiDispatch ?? (static action => action());

        string? settingsDir = Path.GetDirectoryName(_settingsStore.SettingsFilePath);
        RulesDirectoryPath = !string.IsNullOrWhiteSpace(rulesDirectory)
            ? rulesDirectory
            : (!string.IsNullOrWhiteSpace(settingsDir)
                ? Path.Combine(settingsDir, "rules")
                : ArgusPaths.GetDefaultCleanerRulesDirectory());

        Rules = [];
        Exclusions = [];

        SyncFromSettings();
        PopulateRules(DefaultCleanerRuleCatalog.GetBuiltInSystemRules()
            .Concat(DefaultCleanerRuleCatalog.GetBuiltInBrowserRules())
            .ToArray());

        if (eventBus is not null)
        {
            _settingsSubscription = eventBus.Subscribe<SettingsChangedEvent>(_ => _uiDispatch(SyncFromSettings));
        }

        if (autoLoadOnInit)
        {
            EnsureInitialized();
        }
    }

    public void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            _ = InitializeCatalogAndAnalyzeAsync();
        }
    }

    private async Task InitializeCatalogAndAnalyzeAsync()
    {
        await ReloadRulesAsync().ConfigureAwait(false);
        if (_cleanerService is not null)
        {
            _uiDispatch(() => _ = AnalyzeAsync());
        }
    }

    public string RulesDirectoryPath { get; }
    public ObservableCollection<CleanupRuleRowItem> Rules { get; }
    public ObservableCollection<string> Exclusions { get; }

    public bool CanClean => !IsBusy &&
                            HasScanReport &&
                            _approvedScanReport is not null &&
                            Rules.Any(static r => r.IsSelected && r.ScannedFileCount > 0);

    public override bool NeedsElevation =>
        !IsAdministrator &&
        (CreateRestorePointBeforeCleanup || Rules.Any(static r => r.IsSelected && r.RequiresElevation));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClean));
    }

    partial void OnHasScanReportChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClean));
    }

    partial void OnCreateRestorePointBeforeCleanupChanged(bool value)
    {
        RestorePointGuardText = value
            ? "Контрольна точка System Restore перед очищенням: увімкнено"
            : "Контрольна точка System Restore перед очищенням: вимкнено";
        OnPropertyChanged(nameof(NeedsElevation));
    }

    [RelayCommand]
    public async Task ReloadRulesAsync()
    {
        if (Interlocked.CompareExchange(ref _busyGate, 1, 0) != 0)
        {
            return;
        }

        _uiDispatch(() => IsBusy = true);
        try
        {
            var loadResult = await DefaultCleanerRuleCatalog.EnsureAndLoadRulesAsync(_ruleLoader, RulesDirectoryPath)
                .ConfigureAwait(false);

            _uiDispatch(() =>
            {
                if (loadResult.ValidRules.Count > 0)
                {
                    PopulateRules(loadResult.ValidRules);
                }

                _approvedScanReport = null;
                HasScanReport = false;

                string warnPart = loadResult.Warnings.Count > 0
                    ? $" · попереджень валідатора: {loadResult.Warnings.Count}"
                    : string.Empty;
                RulesCatalogSummaryText = $"Активних правил: {Rules.Count}{warnPart} · {RulesDirectoryPath}";
                RecalculateSelectionTotals();
            });
        }
        finally
        {
            Interlocked.Exchange(ref _busyGate, 0);
            _uiDispatch(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private void SelectAllRules()
    {
        foreach (var rule in Rules)
        {
            rule.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectDefaultRules()
    {
        foreach (var rule in Rules)
        {
            rule.IsSelected = rule.Definition.EnabledByDefault;
        }
    }

    [RelayCommand]
    public async Task AnalyzeAsync()
    {
        if (_cleanerService is null || Interlocked.CompareExchange(ref _busyGate, 1, 0) != 0)
        {
            return;
        }

        _uiDispatch(() =>
        {
            IsBusy = true;
            StatusIsSuccess = true;
            OperationProgressPercent = 2;
            OperationProgressText = "2 %";
            OperationStageText = "Аналіз: підготовка JSON-правил…";
            StatusBannerText = "Триває аналіз цільових тек за JSON-правилами…";
        });

        using var tickerCts = new CancellationTokenSource();
        var tickerTask = RunSmoothTickerAsync(maxAutoPercent: 93, intervalMs: 70, tickerCts.Token);

        try
        {
            var definitions = Rules.Select(static r => r.Definition).ToArray();
            var exclusions = Exclusions.ToArray();
            var progress = new Progress<CleanerProgress>(p => _uiDispatch(() =>
            {
                int pct = Math.Max(OperationProgressPercent, Math.Clamp(p.PercentComplete, 1, 98));
                OperationProgressPercent = pct;
                OperationProgressText = $"{pct} %";
                OperationStageText = $"{p.StageLabel}: {p.CurrentItem}";
            }));

            var report = await _cleanerService.AnalyzeAsync(definitions, exclusions, progress: progress).ConfigureAwait(false);

            _uiDispatch(() =>
            {
                _approvedScanReport = report;
                var byId = report.Items.ToDictionary(static i => i.RuleId, StringComparer.OrdinalIgnoreCase);
                foreach (var row in Rules)
                {
                    byId.TryGetValue(row.Id, out var scanItem);
                    row.ApplyScanItem(scanItem);
                }

                HasScanReport = true;
                OperationProgressPercent = 100;
                OperationProgressText = "100 %";
                OperationStageText = $"Аналіз завершено · {report.TotalFiles} файлів ({WindowsCleanerService.FormatBytes(report.TotalBytes)})";
                TotalFoundSummaryText = $"{WindowsCleanerService.FormatBytes(report.TotalBytes)} ({report.TotalFiles} файлів)";
                RecalculateSelectionTotals();

                StatusIsSuccess = true;
                StatusBannerText = report.TotalFiles > 0
                    ? $"Аналіз завершено: знайдено {report.TotalFiles} файлів ({WindowsCleanerService.FormatBytes(report.TotalBytes)}). Перевірте вибрані правила й натисніть «Очистити вибране»."
                    : "Аналіз завершено: система чиста, зайвих тимчасових файлів за правилами не виявлено.";
                ScanStateChanged?.Invoke();
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
    public async Task CleanSelectedAsync()
    {
        if (_cleanerService is null || _approvedScanReport is null || !CanClean)
        {
            return;
        }

        var selectedIds = Rules
            .Where(static r => r.IsSelected && r.ScannedFileCount > 0)
            .Select(static r => r.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selectedIds.Count == 0 || Interlocked.CompareExchange(ref _busyGate, 1, 0) != 0)
        {
            return;
        }

        _uiDispatch(() =>
        {
            IsBusy = true;
            StatusIsSuccess = true;
            OperationProgressPercent = 2;
            OperationProgressText = "2 %";
            OperationStageText = CreateRestorePointBeforeCleanup
                ? "Очищення: створення контрольної точки System Restore…"
                : "Очищення: видалення вибраних файлів…";
            StatusBannerText = CreateRestorePointBeforeCleanup
                ? "Створення контрольної точки System Restore та очищення вибраних категорій…"
                : "Виконується безпечне видалення підтверджених файлів…";
        });

        using var tickerCts = new CancellationTokenSource();
        var tickerTask = RunSmoothTickerAsync(maxAutoPercent: 94, intervalMs: 95, tickerCts.Token);

        try
        {
            var reportToClean = _approvedScanReport;
            var exclusions = Exclusions.ToArray();
            var progress = new Progress<CleanerProgress>(p => _uiDispatch(() =>
            {
                int pct = Math.Max(OperationProgressPercent, Math.Clamp(p.PercentComplete, 1, 98));
                OperationProgressPercent = pct;
                OperationProgressText = $"{pct} %";
                OperationStageText = $"{p.StageLabel}: {p.CurrentItem}";
            }));

            var result = await _cleanerService.CleanAsync(
                reportToClean,
                selectedIds,
                exclusions,
                CreateRestorePointBeforeCleanup,
                progress: progress).ConfigureAwait(false);

            _uiDispatch(() =>
            {
                _approvedScanReport = null;
                HasScanReport = false;
                OperationProgressPercent = 100;
                OperationProgressText = "100 %";
                OperationStageText = $"Очищення завершено · звільнено {WindowsCleanerService.FormatBytes(result.BytesFreed)}";
                foreach (var row in Rules)
                {
                    if (selectedIds.Contains(row.Id))
                    {
                        row.ApplyScanItem(null);
                    }
                }

                RecalculateSelectionTotals();

                string rpNote = !string.IsNullOrWhiteSpace(result.RestorePointId)
                    ? " · Точку відновлення створено"
                    : string.Empty;
                string skipNote = result.FilesSkipped > 0
                    ? $" (пропущено зайнятих процесами: {result.FilesSkipped})"
                    : string.Empty;

                StatusIsSuccess = result.Errors.Count == 0;
                StatusBannerText = result.Errors.Count == 0
                    ? $"Очищення виконано: видалено {result.FilesDeleted} файлів, звільнено {WindowsCleanerService.FormatBytes(result.BytesFreed)}{skipNote}{rpNote}."
                    : $"Звільнено {WindowsCleanerService.FormatBytes(result.BytesFreed)} (видалено {result.FilesDeleted}{skipNote}). Зауваження: {result.Errors[0]}";
                ScanStateChanged?.Invoke();
            });
        }
        finally
        {
            await StopTickerAsync(tickerCts, tickerTask).ConfigureAwait(false);
            Interlocked.Exchange(ref _busyGate, 0);
            _uiDispatch(() => IsBusy = false);
        }
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

    [RelayCommand]
    public async Task AddExclusionAsync()
    {
        string candidate = NewExclusionPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        if (!Exclusions.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            Exclusions.Add(candidate);
            await PersistExclusionsAsync().ConfigureAwait(false);
        }

        _uiDispatch(() =>
        {
            NewExclusionPath = string.Empty;
            InvalidateScanAfterExclusionChange();
        });
    }

    [RelayCommand]
    public async Task RemoveExclusionAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var existing = Exclusions.FirstOrDefault(e => e.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Exclusions.Remove(existing);
            await PersistExclusionsAsync().ConfigureAwait(false);
            _uiDispatch(InvalidateScanAfterExclusionChange);
        }
    }

    private async Task PersistExclusionsAsync()
    {
        var updated = _settingsStore.Current with
        {
            CleanerExclusions = Exclusions.ToArray()
        };
        await _settingsStore.SaveAsync(updated).ConfigureAwait(false);
    }

    private void InvalidateScanAfterExclusionChange()
    {
        _approvedScanReport = null;
        HasScanReport = false;
        foreach (var row in Rules)
        {
            row.ResetScan();
        }

        TotalFoundSummaryText = "Потрібен повторний аналіз";
        RecalculateSelectionTotals();
        StatusBannerText = "Білий список винятків оновлено. Натисніть «Аналізувати систему» для перерахунку.";
    }

    private void SyncFromSettings()
    {
        var cfg = _settingsStore.Current;
        CreateRestorePointBeforeCleanup = cfg.CreateRestorePointBeforeCleanup;
        RestorePointGuardText = cfg.CreateRestorePointBeforeCleanup
            ? "Контрольна точка System Restore перед очищенням: увімкнено"
            : "Контрольна точка System Restore перед очищенням: вимкнено";

        Exclusions.Clear();
        foreach (string item in cfg.CleanerExclusions)
        {
            Exclusions.Add(item);
        }
    }

    private void PopulateRules(IReadOnlyList<CleanerRuleDefinition> definitions)
    {
        var previousSelection = Rules.ToDictionary(static r => r.Id, static r => r.IsSelected, StringComparer.OrdinalIgnoreCase);
        Rules.Clear();
        foreach (var def in definitions)
        {
            var row = new CleanupRuleRowItem(def, RecalculateSelectionTotals);
            if (previousSelection.TryGetValue(def.Id, out bool prev))
            {
                row.IsSelected = prev;
            }

            Rules.Add(row);
        }

        RulesCatalogSummaryText = $"Зовнішні JSON-правила ({Rules.Count} активних) · {RulesDirectoryPath}";
        RecalculateSelectionTotals();
    }

    private void RecalculateSelectionTotals()
    {
        int selectedRules = Rules.Count(static r => r.IsSelected);
        int selectedFiles = Rules.Where(static r => r.IsSelected).Sum(static r => r.ScannedFileCount);
        long selectedBytes = Rules.Where(static r => r.IsSelected).Sum(static r => r.ScannedBytes);

        SelectedSummaryText = HasScanReport
            ? $"{WindowsCleanerService.FormatBytes(selectedBytes)} ({selectedFiles} файлів · правил: {selectedRules})"
            : $"Правил: {selectedRules} із {Rules.Count}";

        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(NeedsElevation));
    }

    public void Dispose()
    {
        DetachUiMode();
        _settingsSubscription?.Dispose();
    }
}
