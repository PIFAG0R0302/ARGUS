using System.Runtime.InteropServices;
using Argus.Core.Contracts.Devices;
using Argus.Core.Events;
using Argus.Core.Journal;
using Microsoft.Extensions.Logging;

namespace Argus.Devices.Monitoring;

/// <summary>
/// Live hardware plug-and-play topology monitor using native Win32 CM_Register_Notification (CfgMgr32.dll)
/// plus WM_DEVICECHANGE forwarding support, with 350 ms burst debouncing.
/// </summary>
public sealed partial class WindowsDeviceChangeMonitor : IDeviceChangeMonitor
{
    private const uint CmNotifyFilterFlagAllInterfaceClasses = 0x00000001;
    private const int CmNotifyFilterTypeDeviceInterface = 0;
    private const int CmNotifyActionDeviceInterfaceArrival = 0;
    private const int CmNotifyActionDeviceInterfaceRemoval = 1;
    private const int CrSuccess = 0;

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(350);

    private readonly IArgusEventBus _eventBus;
    private readonly IOperationJournal _journal;
    private readonly ILogger<WindowsDeviceChangeMonitor> _logger;
    private readonly Lock _sync = new();

    private CmNotifyCallback? _nativeCallbackDelegate;
    private IntPtr _notificationContext;
    private DateTimeOffset _lastEmittedUtc = DateTimeOffset.MinValue;
    private DeviceChangeKind _lastEmittedKind = DeviceChangeKind.ConfigChanged;
    private bool _disposed;

    public WindowsDeviceChangeMonitor(
        IArgusEventBus eventBus,
        IOperationJournal journal,
        ILogger<WindowsDeviceChangeMonitor> logger)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsMonitoring => !_disposed && _notificationContext != IntPtr.Zero;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_sync)
        {
            if (_notificationContext != IntPtr.Zero)
            {
                return;
            }

            var filter = new CmNotifyFilter
            {
                CbSize = (uint)Marshal.SizeOf<CmNotifyFilter>(),
                Flags = CmNotifyFilterFlagAllInterfaceClasses,
                FilterType = CmNotifyFilterTypeDeviceInterface
            };

            _nativeCallbackDelegate = OnCmNotification;
            IntPtr fnPtr = Marshal.GetFunctionPointerForDelegate(_nativeCallbackDelegate);

            int cr = CM_Register_Notification(ref filter, IntPtr.Zero, fnPtr, out _notificationContext);
            if (cr == CrSuccess && _notificationContext != IntPtr.Zero)
            {
                _logger.LogInformation("Live PnP device notification monitor registered (CfgMgr32).");
            }
            else
            {
                _notificationContext = IntPtr.Zero;
                _logger.LogWarning("CM_Register_Notification returned {Result}; relying on manual/window refresh.", cr);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_notificationContext != IntPtr.Zero)
            {
                _ = CM_Unregister_Notification(_notificationContext);
                _notificationContext = IntPtr.Zero;
            }

            _nativeCallbackDelegate = null;
        }
    }

    public void RaiseTopologyChange(DeviceChangeKind kind, string? deviceInstanceId = null)
    {
        if (_disposed)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (now - _lastEmittedUtc < DebounceWindow && _lastEmittedKind == kind)
            {
                return;
            }

            _lastEmittedUtc = now;
            _lastEmittedKind = kind;
        }

        var evt = new DeviceTopologyChangedEvent(kind, deviceInstanceId, now);
        _eventBus.Publish(evt);

        string actionText = kind switch
        {
            DeviceChangeKind.Arrived => "Підключено пристрій",
            DeviceChangeKind.Removed => "Відключено пристрій",
            _ => "Змінено конфігурацію пристроїв"
        };

        string detailText = string.IsNullOrWhiteSpace(deviceInstanceId)
            ? actionText
            : $"{actionText}: {deviceInstanceId}";

        _ = _journal.RecordAsync(
            moduleId: "argus.devices",
            severity: OperationSeverity.Info,
            action: "DeviceTopologyChanged",
            details: detailText);
    }

    private uint OnCmNotification(IntPtr hNotify, IntPtr context, int action, IntPtr eventData, uint eventDataSize)
    {
        try
        {
            DeviceChangeKind kind = action switch
            {
                CmNotifyActionDeviceInterfaceArrival => DeviceChangeKind.Arrived,
                CmNotifyActionDeviceInterfaceRemoval => DeviceChangeKind.Removed,
                _ => DeviceChangeKind.ConfigChanged
            };

            string? symbolicLink = null;
            if (eventData != IntPtr.Zero && eventDataSize >= 28)
            {
                // CM_NOTIFY_EVENT_DATA: FilterType (int, offset 0), Reserved (uint, offset 4),
                // ClassGuid (16 bytes, offset 8), SymbolicLink (WCHAR[1] starting at offset 24)
                symbolicLink = Marshal.PtrToStringUni(eventData + 24);
            }

            RaiseTopologyChange(kind, symbolicLink);
        }
        catch
        {
            // Never allow exceptions to unwind across native CfgMgr32 callback boundary
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CmNotifyCallback(
        IntPtr hNotify,
        IntPtr context,
        int action,
        IntPtr eventData,
        uint eventDataSize);

    // CM_NOTIFY_FILTER total size on x64/x86 is 416 bytes (24-byte header + 392-byte union).
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct CmNotifyFilter
    {
        [FieldOffset(0)]
        public uint CbSize;

        [FieldOffset(4)]
        public uint Flags;

        [FieldOffset(8)]
        public int FilterType;

        [FieldOffset(12)]
        public uint Reserved;
    }

    [LibraryImport("CfgMgr32.dll")]
    private static partial int CM_Register_Notification(
        ref CmNotifyFilter pFilter,
        IntPtr pContext,
        IntPtr pCallback,
        out IntPtr pNotifyContext);

    [LibraryImport("CfgMgr32.dll")]
    private static partial int CM_Unregister_Notification(IntPtr notifyContext);
}
