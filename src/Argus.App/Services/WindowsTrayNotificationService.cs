using System.Runtime.InteropServices;

namespace Argus.App.Services;

public sealed class WindowsTrayNotificationService : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;

    private const uint NiifWarning = 0x00000002;
    private const uint NiifError = 0x00000003;
    private const uint NiifRespectQuietTime = 0x00000080;

    private const uint NotifyIconId = 0xA265;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly IntPtr IdiWarning = new(32515);

    private readonly object _sync = new();
    private IntPtr _messageHwnd;
    private IntPtr _hIcon;
    private bool _ownsIcon;
    private bool _iconActive;
    private Timer? _cleanupTimer;
    private bool _disposed;

    public bool ShowNotification(string title, string message, bool isError = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            EnsureNativeResources();
            if (_messageHwnd == IntPtr.Zero)
            {
                return false;
            }

            var data = new NotifyIconDataW
            {
                cbSize = (uint)Marshal.SizeOf<NotifyIconDataW>(),
                hWnd = _messageHwnd,
                uID = NotifyIconId,
                uFlags = NifIcon | NifTip | NifInfo,
                hIcon = _hIcon,
                szTip = "ARGUS — системні сповіщення",
                szInfoTitle = Truncate(title, 63),
                szInfo = Truncate(message, 255),
                uTimeoutOrVersion = 10_000,
                dwInfoFlags = (isError ? NiifError : NiifWarning) | NiifRespectQuietTime
            };

            bool ok = Shell_NotifyIconW(_iconActive ? NimModify : NimAdd, ref data);
            if (!ok && _iconActive)
            {
                ok = Shell_NotifyIconW(NimAdd, ref data);
            }

            if (ok)
            {
                _iconActive = true;
                ScheduleIconCleanup();
            }

            return ok;
        }
    }

    public void ClearNotificationIcon()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_sync)
        {
            RemoveIconLocked();
        }
    }

    private void ScheduleIconCleanup()
    {
        _cleanupTimer?.Dispose();
        _cleanupTimer = new Timer(_ => ClearNotificationIcon(), null, TimeSpan.FromSeconds(14), Timeout.InfiniteTimeSpan);
    }

    private void EnsureNativeResources()
    {
        if (_messageHwnd == IntPtr.Zero)
        {
            _messageHwnd = CreateWindowExW(
                0,
                "STATIC",
                "ArgusTrayNotifyWindow",
                0,
                0,
                0,
                0,
                0,
                HwndMessage,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
        }

        if (_hIcon == IntPtr.Zero)
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                ushort iconIndex = 0;
                IntPtr extracted = ExtractAssociatedIconW(IntPtr.Zero, exePath, ref iconIndex);
                if (extracted != IntPtr.Zero)
                {
                    _hIcon = extracted;
                    _ownsIcon = true;
                    return;
                }
            }

            _hIcon = LoadIconW(IntPtr.Zero, IdiWarning);
            _ownsIcon = false;
        }
    }

    private void RemoveIconLocked()
    {
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;

        if (!_iconActive || _messageHwnd == IntPtr.Zero)
        {
            return;
        }

        var data = new NotifyIconDataW
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconDataW>(),
            hWnd = _messageHwnd,
            uID = NotifyIconId
        };

        Shell_NotifyIconW(NimDelete, ref data);
        _iconActive = false;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..(maxLength - 1)] + "…";
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (OperatingSystem.IsWindows())
            {
                RemoveIconLocked();

                if (_ownsIcon && _hIcon != IntPtr.Zero)
                {
                    DestroyIcon(_hIcon);
                }

                _hIcon = IntPtr.Zero;
                _ownsIcon = false;

                if (_messageHwnd != IntPtr.Zero)
                {
                    DestroyWindow(_messageHwnd);
                    _messageHwnd = IntPtr.Zero;
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconDataW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconDataW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ExtractAssociatedIconW(IntPtr hInst, string pszIconPath, ref ushort piIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
