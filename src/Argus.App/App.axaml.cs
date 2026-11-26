using Argus.App.Services;
using Argus.App.Themes;
using Argus.App.ViewModels;
using Argus.Core;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Devices;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.Migration;
using Argus.Core.Contracts.System;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Core.Extensions;
using Argus.Core.Journal;
using Argus.Core.Logging;
using Argus.Devices;
using Argus.Devices.Monitoring;
using Argus.Maintenance;
using Argus.Migration;
using Argus.Telemetry;
using Argus.Telemetry.Providers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Argus.App;

public sealed class App : Application
{
    private ServiceProvider? _serviceProvider;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private IDisposable? _settingsChangedSubscription;
    private IDisposable? _telemetryAlertSubscription;
    private IDisposable? _trayTooltipTelemetrySubscription;
    private DateTimeOffset _lastTrayTooltipUpdate = DateTimeOffset.MinValue;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        bool startMinimized = ApplicationLifetime is IClassicDesktopStyleApplicationLifetime earlyDesktop &&
            (earlyDesktop.Args?.Any(static a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) ?? false);

        var services = new ServiceCollection();
        ConfigureServices(services, startMinimized);
        _serviceProvider = services.BuildServiceProvider();

        var logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        var settingsStore = _serviceProvider.GetRequiredService<IArgusSettingsStore>();
        settingsStore.LoadAsync().GetAwaiter().GetResult();
        ArgusThemeManager.ApplyTheme(settingsStore.Current.ThemeId);

        var privilegeService = _serviceProvider.GetRequiredService<IPrivilegeService>();
        logger.LogInformation(
            "Argus starting. IsAdministrator={IsAdmin}, Settings={SettingsPath}",
            privilegeService.IsAdministrator,
            settingsStore.SettingsFilePath);

        var journal = _serviceProvider.GetRequiredService<IOperationJournal>();
        journal.RecordAsync(
            moduleId: "argus.app",
            severity: OperationSeverity.Info,
            action: "AppStarted",
            details: $"Права={(privilegeService.IsAdministrator ? "Адміністратор" : "Стандартні")}, Settings={settingsStore.SettingsFilePath}")
            .GetAwaiter()
            .GetResult();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainViewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();
            if (startMinimized)
            {
                mainViewModel.SetWindowVisible(false);
            }
            else if (TryParseInitialSection(desktop.Args, out var initialSection))
            {
                mainViewModel.SelectSection(initialSection);
            }

            var telemetryService = _serviceProvider.GetRequiredService<ITelemetryService>();
            _ = telemetryService.StartAsync();

            var deviceChangeMonitor = _serviceProvider.GetRequiredService<WindowsDeviceChangeMonitor>();
            deviceChangeMonitor.Start();

            _mainWindow = new MainWindow(
                settingsStore,
                journal,
                kind => deviceChangeMonitor.RaiseTopologyChange(kind))
            {
                DataContext = mainViewModel
            };

            _mainWindow.Closed += (_, _) =>
            {
                if (!(settingsStore.Current.EnableSystemTray && settingsStore.Current.MinimizeToTrayOnClose))
                {
                    desktop.Shutdown(0);
                }
            };

            var eventBus = _serviceProvider.GetRequiredService<IArgusEventBus>();
            var trayNotifications = _serviceProvider.GetRequiredService<WindowsTrayNotificationService>();

            SyncTrayIconState(desktop, mainViewModel, settingsStore.Current.EnableSystemTray || startMinimized, trayNotifications);

            _settingsChangedSubscription = eventBus.Subscribe<SettingsChangedEvent>(evt =>
                Dispatcher.UIThread.Post(() =>
                    SyncTrayIconState(desktop, mainViewModel, evt.Settings.EnableSystemTray, trayNotifications)));

            _telemetryAlertSubscription = eventBus.Subscribe<TelemetryAlertEvent>(alert =>
            {
                if (!settingsStore.Current.EnableSystemTray ||
                    !TelemetryAlertPolicy.IsHardwareOrDiskTrayAlert(alert.Kind))
                {
                    return;
                }

                var (title, isError) = FormatHardwareAlertNotification(alert);
                trayNotifications.ShowNotification(title, alert.Message, isError);
            });

            _trayTooltipTelemetrySubscription = eventBus.Subscribe<TelemetryUpdatedEvent>(evt =>
            {
                if (_trayIcon is null || !settingsStore.Current.EnableSystemTray)
                {
                    return;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now - _lastTrayTooltipUpdate < TimeSpan.FromSeconds(5))
                {
                    return;
                }

                _lastTrayTooltipUpdate = now;
                var snap = evt.Snapshot;
                string cpuPart = snap.Cpu.PackageTemperatureCelsius.HasValue
                    ? $"CPU {snap.Cpu.TotalLoadPercent:0}% ({snap.Cpu.PackageTemperatureCelsius.Value:0} °C)"
                    : $"CPU {snap.Cpu.TotalLoadPercent:0}%";
                var firstGpu = snap.Gpus.FirstOrDefault(static g => g.TemperatureCelsius.HasValue);
                string gpuPart = firstGpu is not null
                    ? $" · GPU {firstGpu.TemperatureCelsius!.Value:0} °C"
                    : string.Empty;
                string tooltip = $"ARGUS · {cpuPart}{gpuPart}";

                Dispatcher.UIThread.Post(() =>
                {
                    if (_trayIcon is not null)
                    {
                        _trayIcon.ToolTipText = tooltip;
                    }
                });
            });

            if (Program.InstanceGuard is not null)
            {
                Program.InstanceGuard.StartActivationListener(sectionArg =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(sectionArg) &&
                            Enum.TryParse(sectionArg, ignoreCase: true, out ArgusSectionId targetSection))
                        {
                            mainViewModel.SelectSection(targetSection);
                        }

                        RestoreMainWindow(desktop);
                    }));
            }

            if (startMinimized)
            {
                logger.LogInformation("Argus started minimized in system tray.");
            }
            else
            {
                desktop.MainWindow = _mainWindow;
            }

            if (settingsStore.Current.CheckUpdatesOnStartup)
            {
                var settingsVm = _serviceProvider.GetRequiredService<SettingsViewModel>();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3500).ConfigureAwait(false);
                    await settingsVm.CheckForUpdatesQuietlyAsync().ConfigureAwait(false);
                });
            }

            desktop.Exit += (_, _) =>
            {
                logger.LogInformation("Argus shutting down cleanly.");
                _settingsChangedSubscription?.Dispose();
                _settingsChangedSubscription = null;
                _telemetryAlertSubscription?.Dispose();
                _telemetryAlertSubscription = null;
                _trayTooltipTelemetrySubscription?.Dispose();
                _trayTooltipTelemetrySubscription = null;

                deviceChangeMonitor.Stop();
                telemetryService.StopAsync().GetAwaiter().GetResult();

                DisposeTrayIcon();
                trayNotifications.Dispose();

                _serviceProvider.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void RestoreMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_mainWindow is null)
        {
            return;
        }

        desktop.MainWindow ??= _mainWindow;
        _mainWindow.RestoreFromTray();
    }

    private void SyncTrayIconState(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindowViewModel mainViewModel,
        bool shouldShowTray,
        WindowsTrayNotificationService trayNotifications)
    {
        if (shouldShowTray)
        {
            if (_trayIcon is null)
            {
                InitializeTrayIcon(desktop, mainViewModel);
            }
        }
        else
        {
            DisposeTrayIcon();
            trayNotifications.ClearNotificationIcon();
        }
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.IsVisible = false;
        TrayIcon.SetIcons(this, []);
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private static (string Title, bool IsError) FormatHardwareAlertNotification(TelemetryAlertEvent alert) => alert.Kind switch
    {
        TelemetryAlertKind.CpuTemperatureHigh => ("ARGUS · Перегрів процесора", true),
        TelemetryAlertKind.GpuTemperatureHigh => ("ARGUS · Перегрів відеокарти", true),
        TelemetryAlertKind.DiskSmartDegraded => ("ARGUS · Увага до S.M.A.R.T. диска", true),
        TelemetryAlertKind.DiskSpaceLow => ("ARGUS · Критично мало місця на диску", alert.MeasuredValue <= 5.0),
        _ => ("ARGUS · Попередження системи", false)
    };

    private void InitializeTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindowViewModel mainViewModel)
    {
        var openItem = new NativeMenuItem("Відкрити ARGUS");
        openItem.Click += (_, _) => RestoreMainWindow(desktop);

        var monitoringItem = new NativeMenuItem("Моніторинг");
        monitoringItem.Click += (_, _) =>
        {
            mainViewModel.SelectSection(ArgusSectionId.Monitoring);
            RestoreMainWindow(desktop);
        };

        var networkItem = new NativeMenuItem("Мережа та Firewall");
        networkItem.Click += (_, _) =>
        {
            mainViewModel.SelectSection(ArgusSectionId.Network);
            RestoreMainWindow(desktop);
        };

        var processesItem = new NativeMenuItem("Процеси");
        processesItem.Click += (_, _) =>
        {
            mainViewModel.SelectSection(ArgusSectionId.Processes);
            RestoreMainWindow(desktop);
        };

        var startupItem = new NativeMenuItem("Автозапуск і служби");
        startupItem.Click += (_, _) =>
        {
            mainViewModel.SelectSection(ArgusSectionId.StartupAndServices);
            RestoreMainWindow(desktop);
        };

        var journalItem = new NativeMenuItem("Журнал записів");
        journalItem.Click += (_, _) =>
        {
            mainViewModel.SelectSection(ArgusSectionId.Journal);
            RestoreMainWindow(desktop);
        };

        var settingsItem = new NativeMenuItem("Налаштування");
        settingsItem.Click += (_, _) =>
        {
            mainViewModel.SelectSection(ArgusSectionId.Settings);
            RestoreMainWindow(desktop);
        };

        var exitItem = new NativeMenuItem("Вийти повністю");
        exitItem.Click += (_, _) => ShutdownCompletely(desktop);

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(monitoringItem);
        menu.Items.Add(networkItem);
        menu.Items.Add(processesItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(journalItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        using var iconStream = AssetLoader.Open(new Uri("avares://Argus/Assets/argus.ico"));
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "ARGUS — моніторинг, оптимізація й міграція Windows",
            Menu = menu,
            IsVisible = true
        };

        _trayIcon.Clicked += (_, _) => RestoreMainWindow(desktop);
        TrayIcon.SetIcons(this, [_trayIcon]);
    }

    private void ShutdownCompletely(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _mainWindow?.ForceClose();
        desktop.Shutdown(0);
    }

    private void ConfigureServices(IServiceCollection services, bool startMinimized)
    {
        Action<Action> uiDispatch = static action =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Post(action);
            }
        };

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddArgusFileLogging(ArgusPaths.GetDefaultLogsDirectory());
        });

        services.AddArgusCore();
        services.AddArgusTelemetry();
        services.AddArgusDevices();
        services.AddArgusMaintenance();
        services.AddArgusMigration();

        Action requestAppExit = () =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            {
                ShutdownCompletely(d);
            }
        };

        services.AddSingleton<WindowsTrayNotificationService>();
        services.AddSingleton(sp => new AppUiModeService(
            sp.GetRequiredService<IArgusSettingsStore>(),
            sp.GetRequiredService<IPrivilegeService>(),
            sp.GetRequiredService<IArgusEventBus>(),
            requestAppExit));

        services.AddSingleton(sp => new OverviewViewModel(
            sp.GetServices<IArgusModule>(),
            sp.GetRequiredService<IArgusSettingsStore>(),
            sp.GetRequiredService<IOperationJournal>(),
            sp.GetRequiredService<IArgusEventBus>(),
            sp.GetRequiredService<ITelemetryService>(),
            sp.GetRequiredService<ISensorTierStatusProvider>(),
            uiDispatch,
            sp.GetRequiredService<DevicesViewModel>(),
            sp.GetRequiredService<CleanupViewModel>(),
            sp.GetRequiredService<DisksViewModel>(),
            uiMode: sp.GetRequiredService<AppUiModeService>()));

        services.AddSingleton(sp => new MonitoringViewModel(
            sp.GetRequiredService<IArgusSettingsStore>(),
            sp.GetRequiredService<ITelemetryService>(),
            sp.GetRequiredService<ISensorTierStatusProvider>(),
            sp.GetRequiredService<IArgusEventBus>(),
            uiDispatch,
            sp.GetRequiredService<ITelemetryReportExporter>(),
            sp.GetRequiredService<AppUiModeService>(),
            sp.GetRequiredService<ISystemInspectorService>(),
            sp.GetRequiredService<IProcessMonitorService>()));
        services.AddSingleton(sp => new DevicesViewModel(
            sp.GetRequiredService<IDeviceProvider>(),
            sp.GetRequiredService<IArgusEventBus>(),
            uiDispatch,
            autoLoadOnInit: !startMinimized,
            uiMode: sp.GetRequiredService<AppUiModeService>()));
        services.AddSingleton(sp => new CleanupViewModel(
            sp.GetRequiredService<IArgusSettingsStore>(),
            sp.GetRequiredService<ICleanerRuleLoader>(),
            sp.GetRequiredService<ICleanerService>(),
            sp.GetRequiredService<IArgusEventBus>(),
            uiDispatch: uiDispatch,
            autoLoadOnInit: !startMinimized,
            uiMode: sp.GetRequiredService<AppUiModeService>()));
        services.AddSingleton(sp => new DisksViewModel(
            sp.GetRequiredService<IDiskOptimizer>(),
            sp.GetRequiredService<IDiskSpaceAnalyzer>(),
            uiDispatch,
            autoLoadOnInit: !startMinimized,
            uiMode: sp.GetRequiredService<AppUiModeService>()));
        services.AddSingleton(sp => new MigrationViewModel(
            sp.GetRequiredService<IMigrationEngine>(),
            sp.GetRequiredService<IPrivilegeService>(),
            uiDispatch,
            autoLoadOnInit: false,
            uiMode: sp.GetRequiredService<AppUiModeService>()));
        services.AddSingleton(sp => new NetworkViewModel(
            sp.GetRequiredService<INetworkManagerService>(),
            uiDispatch,
            uiMode: sp.GetRequiredService<AppUiModeService>(),
            autoLoadOnInit: false,
            trafficService: sp.GetRequiredService<IProcessNetworkTrafficService>(),
            processMonitorService: sp.GetRequiredService<IProcessMonitorService>()));
        services.AddSingleton(sp => new ProcessesViewModel(
            sp.GetRequiredService<IProcessMonitorService>(),
            uiDispatch,
            uiMode: sp.GetRequiredService<AppUiModeService>(),
            autoLoadOnInit: false));
        services.AddSingleton(sp => new StartupAndServicesViewModel(
            sp.GetRequiredService<IStartupAndServicesManager>(),
            uiDispatch,
            uiMode: sp.GetRequiredService<AppUiModeService>(),
            autoLoadOnInit: false));
        services.AddSingleton(sp => new JournalViewModel(
            sp.GetRequiredService<IOperationJournal>(),
            sp.GetRequiredService<IArgusEventBus>(),
            uiDispatch));

        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<IArgusSettingsStore>(),
            sp.GetRequiredService<IOperationJournal>(),
            sp.GetRequiredService<IArgusEventBus>(),
            sp.GetRequiredService<IStartupRegistrationService>(),
            sp.GetRequiredService<IRestorePointService>(),
            uiDispatch,
            sp.GetRequiredService<IAppUpdateService>(),
            requestAppExit: requestAppExit,
            uiMode: sp.GetRequiredService<AppUiModeService>()));

        services.AddSingleton(sp => new MainWindowViewModel(
            sp.GetRequiredService<OverviewViewModel>(),
            sp.GetRequiredService<MonitoringViewModel>(),
            sp.GetRequiredService<DevicesViewModel>(),
            sp.GetRequiredService<CleanupViewModel>(),
            sp.GetRequiredService<DisksViewModel>(),
            sp.GetRequiredService<MigrationViewModel>(),
            sp.GetRequiredService<JournalViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<IArgusSettingsStore>(),
            sp.GetRequiredService<IPrivilegeService>(),
            requestAppExit: requestAppExit,
            startVisible: !startMinimized,
            eventBus: sp.GetRequiredService<IArgusEventBus>(),
            uiDispatch: uiDispatch,
            network: sp.GetRequiredService<NetworkViewModel>(),
            startupAndServices: sp.GetRequiredService<StartupAndServicesViewModel>(),
            processes: sp.GetRequiredService<ProcessesViewModel>(),
            uiMode: sp.GetRequiredService<AppUiModeService>()));
    }

    private static bool TryParseInitialSection(string[]? args, out ArgusSectionId section)
    {
        section = ArgusSectionId.Overview;
        if (args is null || args.Length < 2)
        {
            return false;
        }

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--section", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse(args[i + 1], ignoreCase: true, out ArgusSectionId parsed))
            {
                section = parsed;
                return true;
            }
        }

        return false;
    }
}
