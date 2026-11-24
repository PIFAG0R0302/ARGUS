using System.Collections.ObjectModel;
using Argus.App.Services;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Argus.Core.Extensions;
using Argus.Core.Journal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed partial class SettingsViewModel : SectionViewModelBase, IDisposable
{
    private readonly IArgusSettingsStore _settingsStore;
    private readonly IOperationJournal _journal;
    private readonly IArgusEventBus _eventBus;
    private readonly IStartupRegistrationService _startupService;
    private readonly IRestorePointService _restorePointService;
    private readonly IAppUpdateService? _updateService;
    private readonly AppUiModeService? _uiMode;
    private readonly Action? _requestAppExit;
    private readonly Action<Action> _uiDispatch;
    private readonly IDisposable _settingsChangedSubscription;
    private AppUpdateCheckResult? _latestUpdateResult;
    private int _updateBusyGate;
    private bool _suppressExpertSync;
    private bool _suppressScaleSync;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiScaleText))]
    [NotifyPropertyChangedFor(nameof(IsUiScale80))]
    [NotifyPropertyChangedFor(nameof(IsUiScale90))]
    [NotifyPropertyChangedFor(nameof(IsUiScale100))]
    [NotifyPropertyChangedFor(nameof(IsUiScale110))]
    [NotifyPropertyChangedFor(nameof(IsUiScale125))]
    [NotifyPropertyChangedFor(nameof(IsUiScale150))]
    private double _uiScalePercent = ArgusSettings.DefaultUiScalePercent;

    [ObservableProperty]
    private decimal? _telemetryIntervalMs;

    [ObservableProperty]
    private decimal? _historyWindowSeconds;

    [ObservableProperty]
    private decimal? _cpuTemperatureAlertCelsius;

    [ObservableProperty]
    private decimal? _gpuTemperatureAlertCelsius;

    [ObservableProperty]
    private decimal? _ramUsageAlertPercent;

    [ObservableProperty]
    private decimal? _diskFreeSpaceAlertPercent;

    [ObservableProperty]
    private bool _enableSmartDegradationAlert = true;

    [ObservableProperty]
    private bool _expertModeEnabled;

    [ObservableProperty]
    private bool _enableSystemTray;

    [ObservableProperty]
    private bool _createRestorePointBeforeCleanup;

    [ObservableProperty]
    private bool _enablePawnIoExtendedSensors;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _checkUpdatesOnStartup = true;

    [ObservableProperty]
    private string _gitHubUpdateRepository = ArgusSettings.DefaultGitHubRepository;

    [ObservableProperty]
    private string _saveFeedback = string.Empty;

    [ObservableProperty]
    private bool _hasSaveFeedback;

    [ObservableProperty]
    private string _restoreFeedback = string.Empty;

    [ObservableProperty]
    private bool _hasRestoreFeedback;

    [ObservableProperty]
    private bool _restoreFeedbackSuccess;

    [ObservableProperty]
    private bool _isCreatingRestorePoint;

    // GitHub Auto-Updater & Authenticode State
    [ObservableProperty]
    private string _currentVersionText = "v1.0.0";

    [ObservableProperty]
    private string _signerSummaryText = "Цифровий підпис Authenticode";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdates))]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdates))]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private bool _isUpdateConfirmationOpen;

    [ObservableProperty]
    private string _updateStatusText = "Натисніть «Перевірити оновлення», щоб звірити версію з GitHub Releases.";

    [ObservableProperty]
    private bool _updateStatusIsSuccess = true;

    [ObservableProperty]
    private string _availableUpdateTitle = string.Empty;

    [ObservableProperty]
    private string _availableUpdateNotes = string.Empty;

    [ObservableProperty]
    private int _updateDownloadPercent;

    [ObservableProperty]
    private string _updateDownloadPercentText = "0 %";

    [ObservableProperty]
    private string _updateDownloadStageText = string.Empty;

    public SettingsViewModel(
        IArgusSettingsStore settingsStore,
        IOperationJournal journal,
        IArgusEventBus eventBus,
        IStartupRegistrationService startupService,
        IRestorePointService restorePointService,
        Action<Action>? uiDispatch = null,
        IAppUpdateService? updateService = null,
        Action? requestAppExit = null,
        AppUiModeService? uiMode = null)
        : base(
            ArgusSectionId.Settings,
            "Налаштування",
            "Конфігурація Argus, оновлення з GitHub, системний трей, автозапуск, пороги сповіщень і System Restore",
            uiMode,
            uiDispatch)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(startupService);
        ArgumentNullException.ThrowIfNull(restorePointService);

        _settingsStore = settingsStore;
        _journal = journal;
        _eventBus = eventBus;
        _startupService = startupService;
        _restorePointService = restorePointService;
        _updateService = updateService;
        _uiMode = uiMode;
        _requestAppExit = requestAppExit;
        _uiDispatch = uiDispatch ?? (static action => action());

        SettingsFilePath = settingsStore.SettingsFilePath;
        LogsDirectoryPath = Path.Combine(Path.GetDirectoryName(settingsStore.SettingsFilePath) ?? ArgusPaths.GetDataDirectory(), "logs");

        if (_updateService is not null)
        {
            CurrentVersionText = $"v{_updateService.CurrentVersion}";
            SignerSummaryText = _updateService.CurrentExecutableSignerSummary;
        }
        else
        {
            var asmVer = typeof(SettingsViewModel).Assembly.GetName().Version;
            CurrentVersionText = asmVer is not null ? $"v{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}" : "v1.0.1";
        }

        ApplyFromSettings(settingsStore.Current);
        StartWithWindows = settingsStore.Current.StartWithWindows || TryReadStartupEnabled();

        _settingsChangedSubscription = _eventBus.Subscribe<SettingsChangedEvent>(e => _uiDispatch(() =>
        {
            int incoming = e.Settings.Normalize().UiScalePercent;
            if (RoundedUiScalePercent == incoming)
            {
                return;
            }

            _suppressScaleSync = true;
            try
            {
                UiScalePercent = incoming;
            }
            finally
            {
                _suppressScaleSync = false;
            }
        }));
    }

    public string SettingsFilePath { get; }
    public string LogsDirectoryPath { get; }

    private int RoundedUiScalePercent =>
        Math.Clamp((int)Math.Round(UiScalePercent), ArgusSettings.MinUiScalePercent, ArgusSettings.MaxUiScalePercent);

    public string UiScaleText => $"{RoundedUiScalePercent}%";
    public bool IsUiScale80 => RoundedUiScalePercent == 80;
    public bool IsUiScale90 => RoundedUiScalePercent == 90;
    public bool IsUiScale100 => RoundedUiScalePercent == 100;
    public bool IsUiScale110 => RoundedUiScalePercent == 110;
    public bool IsUiScale125 => RoundedUiScalePercent == 125;
    public bool IsUiScale150 => RoundedUiScalePercent == 150;

    public bool CanCheckForUpdates => !IsCheckingUpdates && !IsDownloadingUpdate;
    public bool CanDownloadUpdate => IsUpdateAvailable && !IsCheckingUpdates && !IsDownloadingUpdate;

    partial void OnUiScalePercentChanged(double value)
    {
        if (_suppressScaleSync)
        {
            return;
        }

        int clamped = Math.Clamp((int)Math.Round(value), ArgusSettings.MinUiScalePercent, ArgusSettings.MaxUiScalePercent);
        _eventBus.Publish(new SettingsChangedEvent(_settingsStore.Current with { UiScalePercent = clamped }));
    }

    [RelayCommand]
    private void SetUiScalePreset(string percentText)
    {
        if (int.TryParse(percentText, out int percent))
        {
            UiScalePercent = Math.Clamp(percent, ArgusSettings.MinUiScalePercent, ArgusSettings.MaxUiScalePercent);
        }
    }

    partial void OnExpertModeEnabledChanged(bool value)
    {
        if (!_suppressExpertSync && _uiMode is not null && _uiMode.IsExpertMode != value)
        {
            _uiMode.SetExpertMode(value, persist: false);
        }
    }

    protected override void OnExpertModeChanged(bool isExpertMode)
    {
        if (ExpertModeEnabled == isExpertMode)
        {
            return;
        }

        _suppressExpertSync = true;
        try
        {
            ExpertModeEnabled = isExpertMode;
        }
        finally
        {
            _suppressExpertSync = false;
        }
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        await CheckForUpdatesCoreAsync(isSilentStartup: false).ConfigureAwait(false);
    }

    public Task CheckForUpdatesQuietlyAsync() => CheckForUpdatesCoreAsync(isSilentStartup: true);

    private async Task CheckForUpdatesCoreAsync(bool isSilentStartup)
    {
        if (_updateService is null || Interlocked.CompareExchange(ref _updateBusyGate, 1, 0) != 0)
        {
            return;
        }

        // Persist any edited repository name before checking
        if (!string.IsNullOrWhiteSpace(GitHubUpdateRepository) &&
            !string.Equals(GitHubUpdateRepository.Trim(), _settingsStore.Current.GitHubUpdateRepository, StringComparison.Ordinal))
        {
            var updatedCfg = _settingsStore.Current with { GitHubUpdateRepository = GitHubUpdateRepository.Trim() };
            await _settingsStore.SaveAsync(updatedCfg).ConfigureAwait(false);
        }

        _uiDispatch(() =>
        {
            IsCheckingUpdates = true;
            IsUpdateConfirmationOpen = false;
            if (!isSilentStartup)
            {
                UpdateStatusIsSuccess = true;
                UpdateStatusText = "Перевірка останнього релізу на GitHub…";
            }
        });

        try
        {
            var result = await _updateService.CheckForUpdateAsync().ConfigureAwait(false);
            _latestUpdateResult = result;

            _uiDispatch(() =>
            {
                IsUpdateAvailable = result.IsUpdateAvailable;
                UpdateStatusIsSuccess = result.Succeeded;
                if (!isSilentStartup || result.IsUpdateAvailable)
                {
                    UpdateStatusText = result.StatusMessage;
                }

                if (result.IsUpdateAvailable)
                {
                    AvailableUpdateTitle = string.IsNullOrWhiteSpace(result.ReleaseTitle)
                        ? $"Доступна версія v{result.LatestVersion}"
                        : $"{result.ReleaseTitle} (v{result.LatestVersion})";
                    AvailableUpdateNotes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                        ? "Нова збірка інсталятора доступна в GitHub Releases."
                        : result.ReleaseNotes.Trim();
                    UpdateDownloadPercent = 0;
                    UpdateDownloadPercentText = "0 %";
                    UpdateDownloadStageText = "Готово до завантаження";
                }
            });
        }
        finally
        {
            Interlocked.Exchange(ref _updateBusyGate, 0);
            _uiDispatch(() => IsCheckingUpdates = false);
        }
    }

    [RelayCommand]
    private void RequestInstallUpdate()
    {
        if (!CanDownloadUpdate || _latestUpdateResult is null)
        {
            return;
        }

        IsUpdateConfirmationOpen = true;
    }

    [RelayCommand]
    private void CancelUpdateConfirmation()
    {
        IsUpdateConfirmationOpen = false;
    }

    [RelayCommand]
    public async Task ConfirmAndInstallUpdateAsync()
    {
        IsUpdateConfirmationOpen = false;
        if (_updateService is null || _latestUpdateResult is null || Interlocked.CompareExchange(ref _updateBusyGate, 1, 0) != 0)
        {
            return;
        }

        _uiDispatch(() =>
        {
            IsDownloadingUpdate = true;
            UpdateStatusIsSuccess = true;
            UpdateDownloadPercent = 3;
            UpdateDownloadPercentText = "3 %";
            UpdateDownloadStageText = "Підключення до GitHub Releases…";
            UpdateStatusText = $"Завантаження інсталятора v{_latestUpdateResult.LatestVersion}…";
        });

        try
        {
            var progress = new DirectUpdateProgress(p => _uiDispatch(() =>
            {
                UpdateDownloadPercent = Math.Clamp(p.Percent, 0, 100);
                UpdateDownloadPercentText = $"{UpdateDownloadPercent} %";
                UpdateDownloadStageText = p.Stage;
            }));

            var download = await _updateService
                .DownloadAndVerifyUpdateAsync(_latestUpdateResult, progress)
                .ConfigureAwait(false);

            if (!download.Succeeded || string.IsNullOrWhiteSpace(download.LocalInstallerPath))
            {
                _uiDispatch(() =>
                {
                    UpdateStatusIsSuccess = false;
                    UpdateStatusText = download.StatusMessage;
                    UpdateDownloadStageText = "Перевірку не пройдено";
                });
                return;
            }

            _uiDispatch(() =>
            {
                UpdateStatusIsSuccess = true;
                UpdateDownloadPercent = 100;
                UpdateDownloadPercentText = "100 %";
                UpdateStatusText = download.StatusMessage;
            });

            if (_updateService.LaunchInstaller(download.LocalInstallerPath))
            {
                _uiDispatch(() => _requestAppExit?.Invoke());
            }
        }
        finally
        {
            Interlocked.Exchange(ref _updateBusyGate, 0);
            _uiDispatch(() => IsDownloadingUpdate = false);
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var updated = new ArgusSettings
        {
            TelemetryIntervalMs = (int)Math.Round(TelemetryIntervalMs ?? 1000m),
            HistoryWindowSeconds = (int)Math.Round(HistoryWindowSeconds ?? 180m),
            UiScalePercent = RoundedUiScalePercent,
            CpuTemperatureAlertCelsius = (double)(CpuTemperatureAlertCelsius ?? 85m),
            GpuTemperatureAlertCelsius = (double)(GpuTemperatureAlertCelsius ?? 85m),
            RamUsageAlertPercent = (double)(RamUsageAlertPercent ?? 90m),
            DiskFreeSpaceAlertPercent = (double)(DiskFreeSpaceAlertPercent ?? 10m),
            EnableSmartDegradationAlert = EnableSmartDegradationAlert,
            ExpertModeEnabled = ExpertModeEnabled,
            EnableSystemTray = EnableSystemTray,
            CreateRestorePointBeforeCleanup = CreateRestorePointBeforeCleanup,
            EnablePawnIoExtendedSensors = EnablePawnIoExtendedSensors,
            MinimizeToTrayOnClose = MinimizeToTrayOnClose,
            StartWithWindows = StartWithWindows,
            CheckUpdatesOnStartup = CheckUpdatesOnStartup,
            GitHubUpdateRepository = string.IsNullOrWhiteSpace(GitHubUpdateRepository)
                ? ArgusSettings.DefaultGitHubRepository
                : GitHubUpdateRepository.Trim(),
            TrustedSignerThumbprint = _settingsStore.Current.TrustedSignerThumbprint,
            Language = _settingsStore.Current.Language,
            CleanerExclusions = _settingsStore.Current.CleanerExclusions
        };

        await _settingsStore.SaveAsync(updated).ConfigureAwait(false);
        var normalized = _settingsStore.Current;
        _eventBus.Publish(new SettingsChangedEvent(normalized));

        string startupNote = ApplyStartupSetting(normalized.StartWithWindows);

        await _journal.RecordAsync(
            moduleId: "argus.settings",
            severity: OperationSeverity.Success,
            action: "SettingsSaved",
            details: $"Інтервал={normalized.TelemetryIntervalMs} мс, масштаб={normalized.UiScalePercent}%, системний трей={normalized.EnableSystemTray}, згортання={normalized.MinimizeToTrayOnClose}, експертний режим={normalized.ExpertModeEnabled}, поріг диска={normalized.DiskFreeSpaceAlertPercent:0.#}%, S.M.A.R.T.={normalized.EnableSmartDegradationAlert}, автозапуск={normalized.StartWithWindows}, автооновлення={normalized.CheckUpdatesOnStartup}{startupNote}")
            .ConfigureAwait(false);

        _uiDispatch(() =>
        {
            ApplyFromSettings(normalized);
            SaveFeedback = "Зміни збережено у settings.json";
            HasSaveFeedback = true;
        });
    }

    [RelayCommand]
    private async Task ResetDefaultsAsync()
    {
        var defaults = new ArgusSettings
        {
            GitHubUpdateRepository = _settingsStore.Current.GitHubUpdateRepository,
            TrustedSignerThumbprint = _settingsStore.Current.TrustedSignerThumbprint
        }.Normalize();

        await _settingsStore.SaveAsync(defaults).ConfigureAwait(false);
        _eventBus.Publish(new SettingsChangedEvent(defaults));
        ApplyStartupSetting(defaults.StartWithWindows);

        await _journal.RecordAsync(
            moduleId: "argus.settings",
            severity: OperationSeverity.Info,
            action: "SettingsReset",
            details: "Відновлено стандартні параметри Argus")
            .ConfigureAwait(false);

        _uiDispatch(() =>
        {
            ApplyFromSettings(defaults);
            SaveFeedback = "Відновлено стандартні значення";
            HasSaveFeedback = true;
        });
    }

    [RelayCommand]
    private async Task CreateRestorePointNowAsync()
    {
        if (IsCreatingRestorePoint)
        {
            return;
        }

        _uiDispatch(() =>
        {
            IsCreatingRestorePoint = true;
            HasRestoreFeedback = true;
            RestoreFeedbackSuccess = false;
            RestoreFeedback = "Створення контрольної точки відновлення Windows...";
        });

        try
        {
            string stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var result = await _restorePointService
                .CreateRestorePointAsync($"Argus Manual Checkpoint ({stamp})")
                .ConfigureAwait(false);

            _uiDispatch(() =>
            {
                RestoreFeedbackSuccess = result.Succeeded;
                RestoreFeedback = result.Message ?? (result.Succeeded ? "Контрольну точку створено." : "Не вдалося створити точку.");
                HasRestoreFeedback = true;
            });
        }
        finally
        {
            _uiDispatch(() => IsCreatingRestorePoint = false);
        }
    }

    private string ApplyStartupSetting(bool enabled)
    {
        try
        {
            string exePath = Environment.ProcessPath ?? string.Empty;
            if (!enabled)
            {
                _startupService.SetEnabled(false, exePath);
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(exePath))
            {
                _startupService.SetEnabled(true, exePath);
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            return $" (помилка автозапуску: {ex.Message})";
        }
    }

    private bool TryReadStartupEnabled()
    {
        try
        {
            return _startupService.IsEnabled();
        }
        catch
        {
            return false;
        }
    }

    private void ApplyFromSettings(ArgusSettings settings)
    {
        TelemetryIntervalMs = settings.TelemetryIntervalMs;
        HistoryWindowSeconds = settings.HistoryWindowSeconds;
        _suppressScaleSync = true;
        try
        {
            UiScalePercent = settings.Normalize().UiScalePercent;
        }
        finally
        {
            _suppressScaleSync = false;
        }

        CpuTemperatureAlertCelsius = (decimal)settings.CpuTemperatureAlertCelsius;
        GpuTemperatureAlertCelsius = (decimal)settings.GpuTemperatureAlertCelsius;
        RamUsageAlertPercent = (decimal)settings.RamUsageAlertPercent;
        DiskFreeSpaceAlertPercent = (decimal)settings.DiskFreeSpaceAlertPercent;
        EnableSmartDegradationAlert = settings.EnableSmartDegradationAlert;
        _suppressExpertSync = true;
        try
        {
            ExpertModeEnabled = settings.ExpertModeEnabled;
        }
        finally
        {
            _suppressExpertSync = false;
        }

        EnableSystemTray = settings.EnableSystemTray;
        CreateRestorePointBeforeCleanup = settings.CreateRestorePointBeforeCleanup;
        EnablePawnIoExtendedSensors = settings.EnablePawnIoExtendedSensors;
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        StartWithWindows = settings.StartWithWindows;
        CheckUpdatesOnStartup = settings.CheckUpdatesOnStartup;
        GitHubUpdateRepository = settings.GitHubUpdateRepository;
    }

    public void Dispose()
    {
        _settingsChangedSubscription.Dispose();
        DetachUiMode();
    }

    private sealed class DirectUpdateProgress(Action<(int Percent, string Stage)> callback) : IProgress<(int Percent, string Stage)>
    {
        public void Report((int Percent, string Stage) value) => callback(value);
    }
}
