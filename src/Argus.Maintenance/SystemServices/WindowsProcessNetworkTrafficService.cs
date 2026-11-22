using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Argus.Core.Contracts.System;
using Microsoft.Extensions.Logging;

namespace Argus.Maintenance.SystemServices;

/// <summary>
/// Hybrid native per-process network traffic tracker (zero external NuGet dependencies):
/// - When elevated (Administrator): opens a live native Win32 ETW session via advapi32.dll
///   (Microsoft-Windows-Kernel-Network) capturing exact per-PID TCP/UDP RX/TX bytes & KB/s.
/// - When running non-elevated (asInvoker): falls back cleanly to active TCP/UDP socket correlation
///   plus GetProcessIoCounters (OtherTransferCount) delta estimation without UAC errors.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsProcessNetworkTrafficService : IProcessNetworkTrafficService
{
    private const string EtwSessionName = "ARGUS-KernelNet-ETW";
    private static readonly Guid KernelNetworkProviderGuid = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint EventTraceControlStop = 1;
    private const uint EventControlCodeEnableProvider = 1;
    private const byte TraceLevelInformation = 4;
    private const ulong KernelNetworkKeywords = 0x30; // KERNEL_NETWORK_KEYWORD_IPV4 (0x10) | IPV6 (0x20)
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const ulong InvalidTraceHandle = ulong.MaxValue;

    private sealed class PidTrafficAccumulator
    {
        public long TotalReceivedBytes;
        public long TotalSentBytes;
    }

    private readonly record struct SnapshotBaseline(long ReceivedBytes, long SentBytes, long TimestampTicks);
    private readonly record struct FallbackIoSample(ulong OtherTransferBytes, long TimestampTicks);

    private readonly IPrivilegeService _privilegeService;
    private readonly INetworkManagerService _networkManager;
    private readonly ILogger<WindowsProcessNetworkTrafficService> _logger;

    private readonly ConcurrentDictionary<int, PidTrafficAccumulator> _etwTotals = new();
    private readonly Dictionary<int, SnapshotBaseline> _lastSnapshotBaselines = new();
    private readonly Dictionary<int, FallbackIoSample> _fallbackIoBaselines = new();
    private readonly Dictionary<int, long> _fallbackAccumulatedBytes = new();
    private readonly Lock _stateSync = new();

    private ulong _sessionHandle;
    private ulong _traceHandle = InvalidTraceHandle;
    private Thread? _etwThread;
    private EventRecordCallbackDelegate? _pinnedCallback;
    private volatile bool _isStarted;
    private volatile bool _isEtwActive;
    private bool _disposed;

    public WindowsProcessNetworkTrafficService(
        IPrivilegeService privilegeService,
        INetworkManagerService networkManager,
        ILogger<WindowsProcessNetworkTrafficService> logger)
    {
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsEtwActive => _isEtwActive;

    public void Start()
    {
        lock (_stateSync)
        {
            if (_disposed || _isStarted)
            {
                return;
            }

            _isStarted = true;
            if (_privilegeService.IsAdministrator)
            {
                TryStartEtwSessionLocked();
            }
        }
    }

    public void Stop()
    {
        ulong traceToClose;
        Thread? threadToJoin;

        lock (_stateSync)
        {
            if (!_isStarted)
            {
                return;
            }

            _isStarted = false;
            _isEtwActive = false;
            StopEtwControlLocked();
            traceToClose = _traceHandle;
            _traceHandle = InvalidTraceHandle;
            threadToJoin = _etwThread;
            _etwThread = null;
        }

        if (traceToClose != InvalidTraceHandle && traceToClose != 0)
        {
            try { _ = CloseTrace(traceToClose); } catch { }
        }

        if (threadToJoin is not null && threadToJoin.IsAlive && threadToJoin != Thread.CurrentThread)
        {
            try { threadToJoin.Join(1200); } catch { }
        }
    }

    public void ResetTotals()
    {
        _etwTotals.Clear();
        lock (_stateSync)
        {
            _lastSnapshotBaselines.Clear();
            _fallbackIoBaselines.Clear();
            _fallbackAccumulatedBytes.Clear();
        }
    }

    public async Task<NetworkTrafficSnapshot> GetSnapshotAsync(
        IReadOnlyList<ActiveConnectionItem>? currentConnections = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ActiveConnectionItem> connections = currentConnections
            ?? await _networkManager.GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);

        return await Task.Run(
            () => BuildSnapshot(connections, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    internal static bool TryParseKernelNetworkEvent(
        ushort eventId,
        ReadOnlySpan<byte> userData,
        int headerPid,
        out int pid,
        out uint bytes,
        out bool isSend)
    {
        // Microsoft-Windows-Kernel-Network event IDs:
        // Send: 10 (TCP v4), 26 (TCP v6), 42 (UDP v4), 58 (UDP v6)
        // Recv: 11 (TCP v4), 27 (TCP v6), 43 (UDP v4), 59 (UDP v6)
        bool? sendFlag = eventId switch
        {
            10 or 26 or 42 or 58 => true,
            11 or 27 or 43 or 59 => false,
            _ => null
        };

        if (sendFlag is null || userData.Length < 8)
        {
            pid = 0;
            bytes = 0;
            isSend = false;
            return false;
        }

        // Standard kernel network payload layout starts with: uint32 PID (offset 0), uint32 size (offset 4)
        uint payloadPid = MemoryMarshal.Read<uint>(userData[..4]);
        uint size = MemoryMarshal.Read<uint>(userData.Slice(4, 4));

        if (size == 0 || size > 64 * 1024 * 1024)
        {
            pid = 0;
            bytes = 0;
            isSend = false;
            return false;
        }

        pid = payloadPid > 0 ? unchecked((int)payloadPid) : headerPid;
        bytes = size;
        isSend = sendFlag.Value;
        return pid > 0;
    }

    private NetworkTrafficSnapshot BuildSnapshot(
        IReadOnlyList<ActiveConnectionItem> connections,
        CancellationToken ct)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        var connInfoByPid = new Dictionary<int, (string Name, string? Path, int Count)>();

        foreach (var conn in connections)
        {
            ct.ThrowIfCancellationRequested();
            if (conn.Pid <= 0)
            {
                continue;
            }

            if (connInfoByPid.TryGetValue(conn.Pid, out var existing))
            {
                connInfoByPid[conn.Pid] = (
                    existing.Name,
                    existing.Path ?? conn.ExecutablePath,
                    existing.Count + 1);
            }
            else
            {
                connInfoByPid[conn.Pid] = (conn.ProcessName, conn.ExecutablePath, 1);
            }
        }

        if (_isEtwActive)
        {
            return BuildEtwSnapshot(connInfoByPid, nowTicks, freq, ct);
        }

        return BuildFallbackSnapshot(connInfoByPid, nowTicks, freq, ct);
    }

    private NetworkTrafficSnapshot BuildEtwSnapshot(
        Dictionary<int, (string Name, string? Path, int Count)> connInfoByPid,
        long nowTicks,
        double freq,
        CancellationToken ct)
    {
        var allPids = new HashSet<int>(connInfoByPid.Keys);
        foreach (int pid in _etwTotals.Keys)
        {
            allPids.Add(pid);
        }

        var procNames = BuildMissingProcessNames(allPids, connInfoByPid);
        var items = new List<ProcessNetworkTrafficItem>(allPids.Count);

        lock (_stateSync)
        {
            foreach (int pid in allPids)
            {
                ct.ThrowIfCancellationRequested();

                long totalRx = 0;
                long totalTx = 0;
                if (_etwTotals.TryGetValue(pid, out var acc))
                {
                    totalRx = Volatile.Read(ref acc.TotalReceivedBytes);
                    totalTx = Volatile.Read(ref acc.TotalSentBytes);
                }

                double rxBps = 0.0;
                double txBps = 0.0;
                if (_lastSnapshotBaselines.TryGetValue(pid, out var prev))
                {
                    double dt = (nowTicks - prev.TimestampTicks) / freq;
                    if (dt > 0.05)
                    {
                        rxBps = Math.Max(0.0, (totalRx - prev.ReceivedBytes) / dt);
                        txBps = Math.Max(0.0, (totalTx - prev.SentBytes) / dt);
                    }
                }

                _lastSnapshotBaselines[pid] = new SnapshotBaseline(totalRx, totalTx, nowTicks);

                (string name, string? path, int sockCount) = connInfoByPid.TryGetValue(pid, out var ci)
                    ? ci
                    : (procNames.TryGetValue(pid, out var pn) ? pn.Name : $"PID {pid}",
                       procNames.TryGetValue(pid, out var pp) ? pp.Path : null,
                       0);

                if (sockCount == 0 && totalRx == 0 && totalTx == 0)
                {
                    continue;
                }

                items.Add(new ProcessNetworkTrafficItem(
                    Pid: pid,
                    ProcessName: name,
                    ExecutablePath: path,
                    DownloadBytesPerSec: rxBps,
                    UploadBytesPerSec: txBps,
                    TotalReceivedBytes: totalRx,
                    TotalSentBytes: totalTx,
                    ActiveConnectionCount: sockCount,
                    SourceKind: NetworkTrafficSourceKind.NativeEtwKernel));
            }
        }

        SortTrafficItems(items);

        return new NetworkTrafficSnapshot(
            ActiveSourceKind: NetworkTrafficSourceKind.NativeEtwKernel,
            IsEtwActive: true,
            SourceSummary: "Нативний Win32 ETW Kernel (точний облік TCP/UDP RX/TX по PID)",
            Processes: items,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private NetworkTrafficSnapshot BuildFallbackSnapshot(
        Dictionary<int, (string Name, string? Path, int Count)> connInfoByPid,
        long nowTicks,
        double freq,
        CancellationToken ct)
    {
        var items = new List<ProcessNetworkTrafficItem>(connInfoByPid.Count);

        lock (_stateSync)
        {
            foreach (var (pid, info) in connInfoByPid)
            {
                ct.ThrowIfCancellationRequested();

                double estBps = 0.0;
                long accBytes = _fallbackAccumulatedBytes.TryGetValue(pid, out long existingAcc) ? existingAcc : 0;

                if (TryReadOtherIoBytes(pid, out ulong otherBytes))
                {
                    if (_fallbackIoBaselines.TryGetValue(pid, out var prev))
                    {
                        double dt = (nowTicks - prev.TimestampTicks) / freq;
                        if (dt > 0.05 && otherBytes >= prev.OtherTransferBytes)
                        {
                            ulong delta = otherBytes - prev.OtherTransferBytes;
                            estBps = delta / dt;
                            accBytes += (long)Math.Min(delta, (ulong)long.MaxValue - (ulong)Math.Max(0, accBytes));
                            _fallbackAccumulatedBytes[pid] = accBytes;
                        }
                    }

                    _fallbackIoBaselines[pid] = new FallbackIoSample(otherBytes, nowTicks);
                }

                items.Add(new ProcessNetworkTrafficItem(
                    Pid: pid,
                    ProcessName: info.Name,
                    ExecutablePath: info.Path,
                    DownloadBytesPerSec: estBps,
                    UploadBytesPerSec: 0.0,
                    TotalReceivedBytes: accBytes,
                    TotalSentBytes: 0,
                    ActiveConnectionCount: info.Count,
                    SourceKind: NetworkTrafficSourceKind.SocketAndIoEstimate));
            }
        }

        SortTrafficItems(items);

        string summary = _privilegeService.IsAdministrator
            ? "Резервний режим оцінки сокетів та I/O (сесія ETW недоступна)"
            : "Режим без UAC: сокети + оцінка I/O · для точних TCP/UDP КБ/с (ETW) підвищте права (UAC)";

        return new NetworkTrafficSnapshot(
            ActiveSourceKind: NetworkTrafficSourceKind.SocketAndIoEstimate,
            IsEtwActive: false,
            SourceSummary: summary,
            Processes: items,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static void SortTrafficItems(List<ProcessNetworkTrafficItem> items)
    {
        items.Sort(static (a, b) =>
        {
            int bySpeed = b.TotalBytesPerSec.CompareTo(a.TotalBytesPerSec);
            if (bySpeed != 0)
            {
                return bySpeed;
            }

            int byTotal = b.TotalTransferredBytes.CompareTo(a.TotalTransferredBytes);
            if (byTotal != 0)
            {
                return byTotal;
            }

            int byConns = b.ActiveConnectionCount.CompareTo(a.ActiveConnectionCount);
            return byConns != 0 ? byConns : StringComparer.OrdinalIgnoreCase.Compare(a.ProcessName, b.ProcessName);
        });
    }

    private static Dictionary<int, (string Name, string? Path)> BuildMissingProcessNames(
        HashSet<int> allPids,
        Dictionary<int, (string Name, string? Path, int Count)> existing)
    {
        var missing = new Dictionary<int, (string, string?)>();
        foreach (int pid in allPids)
        {
            if (existing.ContainsKey(pid) || pid <= 0)
            {
                continue;
            }

            try
            {
                using var proc = Process.GetProcessById(pid);
                string? path = null;
                try { path = proc.MainModule?.FileName; } catch { }
                missing[pid] = (proc.ProcessName, path);
            }
            catch
            {
                missing[pid] = ($"PID {pid}", null);
            }
        }

        return missing;
    }

    private static bool TryReadOtherIoBytes(int pid, out ulong otherTransferCount)
    {
        otherTransferCount = 0;
        if (pid is 0 or 4)
        {
            return false;
        }

        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (GetProcessIoCounters(h, out var counters))
            {
                otherTransferCount = counters.OtherTransferCount;
                return true;
            }

            return false;
        }
        finally
        {
            _ = CloseHandle(h);
        }
    }

    private void TryStartEtwSessionLocked()
    {
        try
        {
            // Clean up any leftover session with the same name first
            StopEtwControlLocked();

            int propsSize = Marshal.SizeOf<EventTraceProperties>();
            int nameBytes = (EtwSessionName.Length + 1) * 2;
            int totalBytes = propsSize + nameBytes + 64;
            IntPtr propsPtr = Marshal.AllocHGlobal(totalBytes);

            try
            {
                RtlZeroMemory(propsPtr, (nuint)totalBytes);
                var props = new EventTraceProperties
                {
                    Wnode = new WnodeHeader
                    {
                        BufferSize = (uint)totalBytes,
                        Flags = WnodeFlagTracedGuid,
                        ClientContext = 1 // QPC clock
                    },
                    BufferSize = 64,
                    MinimumBuffers = 2,
                    MaximumBuffers = 16,
                    LogFileMode = EventTraceRealTimeMode,
                    FlushTimer = 1,
                    LoggerNameOffset = (uint)propsSize
                };

                Marshal.StructureToPtr(props, propsPtr, false);

                uint startStatus = StartTraceW(out ulong sessionHandle, EtwSessionName, propsPtr);
                if (startStatus != 0)
                {
                    _logger.LogDebug("StartTraceW returned {Status}; using fallback traffic estimator.", startStatus);
                    return;
                }

                _sessionHandle = sessionHandle;
                Guid provider = KernelNetworkProviderGuid;
                uint enableStatus = EnableTraceEx2(
                    sessionHandle,
                    in provider,
                    EventControlCodeEnableProvider,
                    TraceLevelInformation,
                    KernelNetworkKeywords,
                    0,
                    0,
                    IntPtr.Zero);

                if (enableStatus != 0)
                {
                    _logger.LogDebug("EnableTraceEx2 returned {Status}; stopping ETW session.", enableStatus);
                    StopEtwControlLocked();
                    return;
                }

                _pinnedCallback = OnEtwEventRecord;
                var logFile = new EventTraceLogfile
                {
                    LoggerName = EtwSessionName,
                    ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord,
                    EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_pinnedCallback)
                };

                ulong traceHandle = OpenTraceW(ref logFile);
                if (traceHandle == InvalidTraceHandle || traceHandle == 0)
                {
                    _logger.LogDebug("OpenTraceW failed with Win32 error {Err}.", Marshal.GetLastWin32Error());
                    StopEtwControlLocked();
                    return;
                }

                _traceHandle = traceHandle;
                _isEtwActive = true;

                _etwThread = new Thread(() => RunEtwTraceLoop(traceHandle))
                {
                    IsBackground = true,
                    Name = "Argus.EtwKernelNetwork"
                };
                _etwThread.Start();
            }
            finally
            {
                Marshal.FreeHGlobal(propsPtr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to initialize native Win32 ETW kernel network session.");
            _isEtwActive = false;
            StopEtwControlLocked();
        }
    }

    private void RunEtwTraceLoop(ulong traceHandle)
    {
        try
        {
            ulong[] handles = [traceHandle];
            _ = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Expected during shutdown
        }
        finally
        {
            _isEtwActive = false;
        }
    }

    private void OnEtwEventRecord(IntPtr eventRecordPtr)
    {
        if (eventRecordPtr == IntPtr.Zero || !_isEtwActive)
        {
            return;
        }

        try
        {
            var record = Marshal.PtrToStructure<EventRecord>(eventRecordPtr);
            if (record.UserData == IntPtr.Zero || record.UserDataLength < 8 || record.UserDataLength > 512)
            {
                return;
            }

            unsafe
            {
                var rawSpan = new ReadOnlySpan<byte>(record.UserData.ToPointer(), record.UserDataLength);
                if (!TryParseKernelNetworkEvent(
                        record.EventHeader.EventDescriptor.Id,
                        rawSpan,
                        unchecked((int)record.EventHeader.ProcessId),
                        out int pid,
                        out uint byteCount,
                        out bool isSend))
                {
                    return;
                }

                var acc = _etwTotals.GetOrAdd(pid, static _ => new PidTrafficAccumulator());
                if (isSend)
                {
                    Interlocked.Add(ref acc.TotalSentBytes, byteCount);
                }
                else
                {
                    Interlocked.Add(ref acc.TotalReceivedBytes, byteCount);
                }
            }
        }
        catch
        {
            // Never throw across native ETW callback boundary
        }
    }

    private void StopEtwControlLocked()
    {
        int propsSize = Marshal.SizeOf<EventTraceProperties>();
        int totalBytes = propsSize + (EtwSessionName.Length + 1) * 2 + 64;
        IntPtr propsPtr = Marshal.AllocHGlobal(totalBytes);
        try
        {
            RtlZeroMemory(propsPtr, (nuint)totalBytes);
            var props = new EventTraceProperties
            {
                Wnode = new WnodeHeader
                {
                    BufferSize = (uint)totalBytes,
                    Flags = WnodeFlagTracedGuid
                },
                LoggerNameOffset = (uint)propsSize
            };
            Marshal.StructureToPtr(props, propsPtr, false);
            _ = ControlTraceW(_sessionHandle, EtwSessionName, propsPtr, EventTraceControlStop);
            _sessionHandle = 0;
        }
        catch
        {
            // Ignore when session does not exist
        }
        finally
        {
            Marshal.FreeHGlobal(propsPtr);
        }
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
    private delegate void EventRecordCallbackDelegate(IntPtr eventRecord);

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventDescriptor
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHeader
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EventDescriptor EventDescriptor;
        public uint KernelTime;
        public uint UserTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EtwBufferContext
    {
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventRecord
    {
        public EventHeader EventHeader;
        public EtwBufferContext BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TimeZoneInformation
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public short[] StandardName;
        public SystemTime StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public short[] DaylightName;
        public SystemTime DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceLogfileHeader
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TimeZoneInformation TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceHeader
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public uint KernelTime;
        public uint UserTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTrace
    {
        public EventTraceHeader Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EventTraceLogfile
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LogFileName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EventTrace CurrentEvent;
        public TraceLogfileHeader LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [LibraryImport("kernel32.dll")]
    private static partial void RtlZeroMemory(IntPtr dest, nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(
        IntPtr hProcess,
        out IoCounters lpIoCounters);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint StartTraceW(
        out ulong sessionHandle,
        string sessionName,
        IntPtr properties);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ControlTraceW(
        ulong sessionHandle,
        string sessionName,
        IntPtr properties,
        uint controlCode);

    [LibraryImport("advapi32.dll")]
    private static partial uint EnableTraceEx2(
        ulong traceHandle,
        in Guid providerId,
        uint controlCode,
        byte level,
        ulong matchAnyKeyword,
        ulong matchAllKeyword,
        uint timeout,
        IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EventTraceLogfile logFile);

    [LibraryImport("advapi32.dll")]
    private static partial uint ProcessTrace(
        [In] ulong[] handleArray,
        uint handleCount,
        IntPtr startTime,
        IntPtr endTime);

    [LibraryImport("advapi32.dll")]
    private static partial uint CloseTrace(ulong traceHandle);
}
