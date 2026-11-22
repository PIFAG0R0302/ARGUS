using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Devices;
using Argus.Core.Extensions;
using Argus.Core.Journal;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Argus.App;

public sealed partial class MainWindow : Window
{
    private const uint WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;

    private readonly IArgusSettingsStore? _settingsStore;
    private readonly IOperationJournal? _journal;
    private readonly Action<DeviceChangeKind>? _onWindowDeviceChange;
    private bool _allowClose;
    private bool _wndProcHooked;

    public MainWindow()
    {
        InitializeComponent();
        WireWindowEvents();
    }

    public MainWindow(
        IArgusSettingsStore settingsStore,
        IOperationJournal journal,
        Action<DeviceChangeKind>? onWindowDeviceChange = null)
    {
        _settingsStore = settingsStore;
        _journal = journal;
        _onWindowDeviceChange = onWindowDeviceChange;
        InitializeComponent();
        WireWindowEvents();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            vm.SetWindowVisible(true);
        }

        Activate();
    }

    private void WireWindowEvents()
    {
        KeyDown += OnWindowKeyDown;
        Closing += OnWindowClosing;
        PropertyChanged += OnWindowPropertyChanged;
        Opened += (_, _) =>
        {
            if (OperatingSystem.IsWindows() && !_wndProcHooked)
            {
                _wndProcHooked = true;
                Win32Properties.AddWndProcHookCallback(this, OnWndProc);
            }

            if (DataContext is MainWindowViewModel vm)
            {
                vm.SetWindowVisible(true);
            }
        };
    }

    private IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange && _onWindowDeviceChange is not null)
        {
            int code = unchecked((int)wParam.ToInt64());
            if (code == DbtDeviceArrival)
            {
                _onWindowDeviceChange(DeviceChangeKind.Arrived);
            }
            else if (code == DbtDeviceRemoveComplete)
            {
                _onWindowDeviceChange(DeviceChangeKind.Removed);
            }
            else if (code == DbtDevNodesChanged)
            {
                _onWindowDeviceChange(DeviceChangeKind.ConfigChanged);
            }
        }

        return IntPtr.Zero;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            bool maximized = WindowState == WindowState.Maximized;
            if (DataContext is MainWindowViewModel vm)
            {
                vm.IsMaximized = maximized;
                vm.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
            }

            if (RootShell is not null)
            {
                RootShell.Margin = maximized ? OffScreenMargin : new Thickness(0);
            }
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_allowClose &&
            _settingsStore is not null &&
            _settingsStore.Current.EnableSystemTray &&
            _settingsStore.Current.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SetWindowVisible(false);
            }

            _ = _journal?.RecordAsync(
                "argus.app",
                OperationSeverity.Info,
                "MinimizedToTray",
                "Вікно згорнуто в системний трей");
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.Handled &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            !IsInsideButton(e.Source))
        {
            BeginMoveDrag(e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!IsInsideButton(e.Source))
        {
            ToggleMaximizeRestore();
        }
    }

    private static bool IsInsideButton(object? source) =>
        source is Visual visual && (visual is Button || visual.FindAncestorOfType<Button>() is not null);

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestoreClicked(object? sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        ArgusSectionId? target = e.Key switch
        {
            Key.D1 or Key.NumPad1 => ArgusSectionId.Overview,
            Key.D2 or Key.NumPad2 => ArgusSectionId.Monitoring,
            Key.D3 or Key.NumPad3 => ArgusSectionId.Network,
            Key.D4 or Key.NumPad4 => ArgusSectionId.Devices,
            Key.D5 or Key.NumPad5 => ArgusSectionId.Cleanup,
            Key.D6 or Key.NumPad6 => ArgusSectionId.Disks,
            Key.D7 or Key.NumPad7 => ArgusSectionId.Migration,
            Key.D8 or Key.NumPad8 => ArgusSectionId.Processes,
            Key.D9 or Key.NumPad9 => ArgusSectionId.StartupAndServices,
            Key.D0 or Key.NumPad0 => ArgusSectionId.Journal,
            Key.OemComma => ArgusSectionId.Settings,
            _ => null
        };

        if (target is not null)
        {
            vm.SelectSection(target.Value);
            e.Handled = true;
        }
    }
}
