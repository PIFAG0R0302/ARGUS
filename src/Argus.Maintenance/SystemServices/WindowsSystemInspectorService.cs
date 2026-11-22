using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Argus.Core.Contracts.System;
using Argus.Core.Journal;
using Microsoft.Extensions.Logging;

namespace Argus.Maintenance.SystemServices;

/// <summary>
/// Native Windows Expert Mode inspector providing Defender/HVCI-safe read-only enumeration of
/// processes, Windows services (advapi32.dll), and local TCP/UDP ports (iphlpapi.dll) without
/// requiring administrator privileges, plus UAC-aware process termination and service control.
/// </summary>
public sealed partial class WindowsSystemInspectorService : ISystemInspectorService
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceWin32 = 0x00000030;
    private const uint ServiceStateAll = 0x00000003;
    private const uint ScEnumProcessInfo = 0;

    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceAcceptStop = 0x00000001;

    private const uint ServiceStopped = 1;
    private const uint ServiceStartPending = 2;
    private const uint ServiceStopPending = 3;
    private const uint ServiceRunning = 4;
    private const uint ServiceContinuePending = 5;
    private const uint ServicePausePending = 6;
    private const uint ServicePaused = 7;

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    private static readonly HashSet<string> CriticalSystemProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle",
        "System",
        "Registry",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "winlogon",
        "Memory Compression"
    };

    private readonly IPrivilegeService _privilegeService;
    private readonly IOperationJournal _journal;
    private readonly ILogger<WindowsSystemInspectorService> _logger;

    public WindowsSystemInspectorService(
        IPrivilegeService privilegeService,
        IOperationJournal journal,
        ILogger<WindowsSystemInspectorService> logger)
    {
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IReadOnlyList<ProcessInspectItem>> GetProcessesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CollectProcesses(cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<WindowsServiceInspectItem>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CollectServices(cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<LocalPortInspectItem>> GetLocalPortsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CollectLocalPorts(cancellationToken), cancellationToken);
    }

    public async Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default)
    {
        int currentPid = Environment.ProcessId;
        if (pid <= 0 || pid == 4 || pid == currentPid)
        {
            const string msg = "Завершення критичного системного процесу або поточного процесу ARGUS заборонено.";
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "TerminateProcessBlocked",
                $"PID={pid}: {msg}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: false, msg);
        }

        string processName = $"PID {pid}";
        try
        {
            using var proc = Process.GetProcessById(pid);
            processName = proc.ProcessName;

            if (CriticalProcessProtection.IsCriticalProcess(pid, processName, currentPid))
            {
                string protectedMsg = $"Процес «{processName}» (PID {pid}) є критичним системним процесом і захищений від завершення.";
                await _journal.RecordAsync(
                    "argus.maintenance",
                    OperationSeverity.Warning,
                    "TerminateProcessBlocked",
                    protectedMsg,
                    succeeded: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return new SystemActionResult(false, RequiresElevation: false, protectedMsg);
            }

            proc.Kill(entireProcessTree: false);
            proc.WaitForExit(1500);

            string okMsg = $"Процес «{processName}» (PID {pid}) успішно завершено.";
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Success,
                "TerminateProcess",
                okMsg,
                succeeded: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(true, RequiresElevation: false, okMsg);
        }
        catch (ArgumentException)
        {
            string exitedMsg = $"Процес (PID {pid}) уже завершився.";
            return new SystemActionResult(true, RequiresElevation: false, exitedMsg);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied || !_privilegeService.IsAdministrator)
        {
            bool needsUac = !_privilegeService.IsAdministrator || ex.NativeErrorCode == ErrorAccessDenied;
            string deniedMsg = !_privilegeService.IsAdministrator
                ? $"Потрібні права адміністратора (UAC) для завершення процесу «{processName}» (PID {pid})."
                : $"Windows заборонила завершення захищеного процесу «{processName}» (PID {pid}).";

            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "TerminateProcessDenied",
                deniedMsg,
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: needsUac, deniedMsg);
        }
        catch (UnauthorizedAccessException)
        {
            string deniedMsg = $"Потрібні права адміністратора (UAC) для завершення процесу «{processName}» (PID {pid}).";
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "TerminateProcessDenied",
                deniedMsg,
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: true, deniedMsg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string errMsg = $"Не вдалося завершити процес «{processName}» (PID {pid}): {ex.Message}";
            _logger.LogWarning(ex, "Failed to terminate process {Pid}.", pid);
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Error,
                "TerminateProcessFailed",
                errMsg,
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: false, errMsg);
        }
    }

    public async Task<SystemActionResult> ControlServiceAsync(
        string serviceName,
        ServiceControlAction action,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new SystemActionResult(false, RequiresElevation: false, "Не вказано назву служби Windows.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new SystemActionResult(false, RequiresElevation: false, "Керування службами доступне лише у Windows.");
        }

        string trimmedName = serviceName.Trim();
        if (action is ServiceControlAction.Stop or ServiceControlAction.Restart &&
            CriticalServiceProtection.IsCriticalService(trimmedName))
        {
            string blockedMsg = $"Служба «{trimmedName}» є критичною для Windows і захищена від зупинки або перезапуску.";
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "CriticalServiceBlocked",
                $"Служба={trimmedName}, Дія={action}: {blockedMsg}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: false, blockedMsg);
        }

        if (!_privilegeService.IsAdministrator)
        {
            string msg = $"Потрібні права адміністратора (UAC) для керування службою «{trimmedName}».";
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "ControlServiceRequiresElevation",
                $"Служба={trimmedName}, Дія={action}: {msg}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: true, msg);
        }

        var result = await Task.Run(
            () => ExecuteServiceControl(trimmedName, action, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        await _journal.RecordAsync(
            "argus.maintenance",
            result.Succeeded ? OperationSeverity.Success : OperationSeverity.Warning,
            "ControlService",
            $"Служба={trimmedName}, Дія={action}: {result.Message}",
            succeeded: result.Succeeded,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }

    public bool OpenProcessFolder(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{executablePath}\"",
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to open Explorer for '{ExecutablePath}'.", executablePath);
            return false;
        }
    }

    private static IReadOnlyList<ProcessInspectItem> CollectProcesses(CancellationToken ct)
    {
        Process[] processes = Process.GetProcesses();
        int currentPid = Environment.ProcessId;
        var items = new List<ProcessInspectItem>(processes.Length);

        try
        {
            foreach (var proc in processes)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    int pid = proc.Id;
                    string name = proc.ProcessName;
                    long workingSet = 0;
                    int threadCount = 0;
                    string? exePath = null;
                    bool isProtected = pid is 0 or 4 ||
                                       pid == currentPid ||
                                       CriticalSystemProcessNames.Contains(name);

                    try
                    {
                        workingSet = proc.WorkingSet64;
                    }
                    catch
                    {
                        // Ignore transiently inaccessible working set
                    }

                    try
                    {
                        threadCount = proc.Threads.Count;
                    }
                    catch
                    {
                        // Ignore inaccessible thread collection
                    }

                    if (pid is not (0 or 4))
                    {
                        try
                        {
                            exePath = proc.MainModule?.FileName;
                        }
                        catch (Win32Exception)
                        {
                            isProtected = true;
                        }
                        catch (InvalidOperationException)
                        {
                            // Process exited while inspecting
                            continue;
                        }
                        catch
                        {
                            // Keep process item without path
                        }
                    }

                    items.Add(new ProcessInspectItem(
                        Pid: pid,
                        Name: name,
                        WorkingSetBytes: Math.Max(0, workingSet),
                        ThreadCount: Math.Max(0, threadCount),
                        ExecutablePath: exePath,
                        IsSystemOrProtected: isProtected));
                }
                catch
                {
                    // Skip processes that exited mid-enumeration
                }
            }
        }
        finally
        {
            foreach (var proc in processes)
            {
                try
                {
                    proc.Dispose();
                }
                catch
                {
                    // Best-effort dispose
                }
            }
        }

        items.Sort(static (a, b) =>
        {
            int byMem = b.WorkingSetBytes.CompareTo(a.WorkingSetBytes);
            return byMem != 0 ? byMem : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        return items;
    }

    private IReadOnlyList<WindowsServiceInspectItem> CollectServices(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        IntPtr scm = OpenSCManagerW(null, null, ScManagerEnumerateService);
        if (scm == IntPtr.Zero)
        {
            _logger.LogWarning("OpenSCManagerW failed with Win32 error {Error}.", Marshal.GetLastWin32Error());
            return [];
        }

        var items = new List<WindowsServiceInspectItem>(256);
        IntPtr enumBuffer = IntPtr.Zero;
        IntPtr configBuffer = IntPtr.Zero;
        uint configBufferSize = 8192;

        try
        {
            uint bytesNeeded = 0;
            uint servicesReturned = 0;
            uint resumeHandle = 0;

            _ = EnumServicesStatusExW(
                scm,
                ScEnumProcessInfo,
                ServiceWin32,
                ServiceStateAll,
                IntPtr.Zero,
                0,
                out bytesNeeded,
                out servicesReturned,
                ref resumeHandle,
                null);

            if (bytesNeeded == 0)
            {
                return items;
            }

            uint allocSize = bytesNeeded + 4096;
            enumBuffer = Marshal.AllocHGlobal((int)allocSize);
            configBuffer = Marshal.AllocHGlobal((int)configBufferSize);
            resumeHandle = 0;

            int entrySize = Marshal.SizeOf<EnumServiceStatusProcess>();

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                bool ok = EnumServicesStatusExW(
                    scm,
                    ScEnumProcessInfo,
                    ServiceWin32,
                    ServiceStateAll,
                    enumBuffer,
                    allocSize,
                    out bytesNeeded,
                    out servicesReturned,
                    ref resumeHandle,
                    null);

                int err = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && err != ErrorMoreData)
                {
                    break;
                }

                for (int i = 0; i < servicesReturned; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    IntPtr ptr = IntPtr.Add(enumBuffer, i * entrySize);
                    var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(ptr);

                    string serviceName = Marshal.PtrToStringUni(entry.lpServiceName) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(serviceName))
                    {
                        continue;
                    }

                    string displayName = Marshal.PtrToStringUni(entry.lpDisplayName) ?? serviceName;
                    string status = FormatServiceState(entry.ServiceStatusProcess.dwCurrentState);
                    bool canStop = (entry.ServiceStatusProcess.dwControlsAccepted & ServiceAcceptStop) != 0 &&
                                   entry.ServiceStatusProcess.dwCurrentState is ServiceRunning or ServicePaused;
                    string startType = QueryServiceStartType(scm, serviceName, ref configBuffer, ref configBufferSize);

                    items.Add(new WindowsServiceInspectItem(
                        ServiceName: serviceName,
                        DisplayName: displayName,
                        Status: status,
                        StartType: startType,
                        CanStop: canStop));
                }

                if (ok)
                {
                    break;
                }
            }
        }
        finally
        {
            if (configBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(configBuffer);
            }

            if (enumBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(enumBuffer);
            }

            _ = CloseServiceHandle(scm);
        }

        items.Sort(static (a, b) =>
        {
            int aRank = a.Status == "Працює" ? 0 : 1;
            int bRank = b.Status == "Працює" ? 0 : 1;
            int byRun = aRank.CompareTo(bRank);
            return byRun != 0 ? byRun : StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayName, b.DisplayName);
        });

        return items;
    }

    private static string QueryServiceStartType(
        IntPtr scm,
        string serviceName,
        ref IntPtr buffer,
        ref uint bufferSize)
    {
        IntPtr svc = OpenServiceW(scm, serviceName, ServiceQueryConfig);
        if (svc == IntPtr.Zero)
        {
            return "—";
        }

        try
        {
            bool ok = QueryServiceConfigW(svc, buffer, bufferSize, out uint bytesNeeded);
            if (!ok && Marshal.GetLastWin32Error() == ErrorInsufficientBuffer && bytesNeeded > bufferSize)
            {
                Marshal.FreeHGlobal(buffer);
                bufferSize = bytesNeeded + 256;
                buffer = Marshal.AllocHGlobal((int)bufferSize);
                ok = QueryServiceConfigW(svc, buffer, bufferSize, out _);
            }

            if (!ok)
            {
                return "—";
            }

            var cfg = Marshal.PtrToStructure<QueryServiceConfig>(buffer);
            return FormatServiceStartType(cfg.dwStartType);
        }
        finally
        {
            _ = CloseServiceHandle(svc);
        }
    }

    private static SystemActionResult ExecuteServiceControl(
        string serviceName,
        ServiceControlAction action,
        CancellationToken ct)
    {
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorAccessDenied)
            {
                return new SystemActionResult(false, RequiresElevation: true, "Потрібні права адміністратора (UAC) для відкриття диспетчера служб.");
            }

            return new SystemActionResult(false, RequiresElevation: false, $"Не вдалося відкрити диспетчер служб (код {err}).");
        }

        try
        {
            uint desiredAccess = action switch
            {
                ServiceControlAction.Start => ServiceStart | ServiceQueryStatus,
                ServiceControlAction.Stop => ServiceStop | ServiceQueryStatus,
                _ => ServiceStart | ServiceStop | ServiceQueryStatus
            };

            IntPtr svc = OpenServiceW(scm, serviceName, desiredAccess);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ErrorAccessDenied)
                {
                    return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для керування службою «{serviceName}».");
                }

                return new SystemActionResult(false, RequiresElevation: false, $"Не вдалося відкрити службу «{serviceName}» (код {err}).");
            }

            try
            {
                return action switch
                {
                    ServiceControlAction.Start => StartServiceCore(svc, serviceName, ct),
                    ServiceControlAction.Stop => StopServiceCore(svc, serviceName, ct),
                    ServiceControlAction.Restart => RestartServiceCore(svc, serviceName, ct),
                    _ => new SystemActionResult(false, RequiresElevation: false, "Невідома дія керування службою.")
                };
            }
            finally
            {
                _ = CloseServiceHandle(svc);
            }
        }
        finally
        {
            _ = CloseServiceHandle(scm);
        }
    }

    private static SystemActionResult StartServiceCore(IntPtr svc, string serviceName, CancellationToken ct)
    {
        if (!StartServiceW(svc, 0, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorServiceAlreadyRunning)
            {
                return new SystemActionResult(true, RequiresElevation: false, $"Служба «{serviceName}» уже працює.");
            }

            if (err == ErrorAccessDenied)
            {
                return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для запуску служби «{serviceName}».");
            }

            return new SystemActionResult(false, RequiresElevation: false, $"Не вдалося запустити службу «{serviceName}» (код {err}).");
        }

        bool reached = WaitForServiceState(svc, ServiceRunning, TimeSpan.FromSeconds(10), ct);
        return reached
            ? new SystemActionResult(true, RequiresElevation: false, $"Службу «{serviceName}» успішно запущено.")
            : new SystemActionResult(true, RequiresElevation: false, $"Надіслано команду запуску служби «{serviceName}» (перехід у стан роботи триває).");
    }

    private static SystemActionResult StopServiceCore(IntPtr svc, string serviceName, CancellationToken ct)
    {
        if (!ControlService(svc, ServiceControlStop, out _))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorServiceNotActive)
            {
                return new SystemActionResult(true, RequiresElevation: false, $"Служба «{serviceName}» уже зупинена.");
            }

            if (err == ErrorAccessDenied)
            {
                return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для зупинки служби «{serviceName}».");
            }

            return new SystemActionResult(false, RequiresElevation: false, $"Не вдалося зупинити службу «{serviceName}» (код {err}).");
        }

        bool reached = WaitForServiceState(svc, ServiceStopped, TimeSpan.FromSeconds(10), ct);
        return reached
            ? new SystemActionResult(true, RequiresElevation: false, $"Службу «{serviceName}» успішно зупинено.")
            : new SystemActionResult(true, RequiresElevation: false, $"Надіслано команду зупинки служби «{serviceName}» (зупинка триває).");
    }

    private static SystemActionResult RestartServiceCore(IntPtr svc, string serviceName, CancellationToken ct)
    {
        var stopResult = StopServiceCore(svc, serviceName, ct);
        if (!stopResult.Succeeded)
        {
            return stopResult;
        }

        var startResult = StartServiceCore(svc, serviceName, ct);
        if (!startResult.Succeeded)
        {
            return startResult;
        }

        return new SystemActionResult(true, RequiresElevation: false, $"Службу «{serviceName}» успішно перезапущено.");
    }

    private static bool WaitForServiceState(IntPtr svc, uint desiredState, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (QueryServiceStatus(svc, out var status) && status.dwCurrentState == desiredState)
            {
                return true;
            }

            Thread.Sleep(150);
        }

        return QueryServiceStatus(svc, out var finalStatus) && finalStatus.dwCurrentState == desiredState;
    }

    private static IReadOnlyList<LocalPortInspectItem> CollectLocalPorts(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var pidMap = BuildPidNameMap();
        var items = new List<LocalPortInspectItem>(128);

        CollectTcpV4Ports(items, pidMap, ct);
        CollectTcpV6Ports(items, pidMap, ct);
        CollectUdpV4Ports(items, pidMap, ct);
        CollectUdpV6Ports(items, pidMap, ct);

        items.Sort(static (a, b) =>
        {
            int byProto = StringComparer.Ordinal.Compare(a.Protocol, b.Protocol);
            if (byProto != 0)
            {
                return byProto;
            }

            int byPort = a.LocalPort.CompareTo(b.LocalPort);
            return byPort != 0 ? byPort : StringComparer.Ordinal.Compare(a.LocalAddress, b.LocalAddress);
        });

        return items;
    }

    private static Dictionary<int, string> BuildPidNameMap()
    {
        var map = new Dictionary<int, string>
        {
            [0] = "System Idle Process",
            [4] = "System"
        };

        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (var proc in processes)
            {
                try
                {
                    map[proc.Id] = proc.ProcessName;
                }
                catch
                {
                    // Skip exited process
                }
            }
        }
        finally
        {
            foreach (var proc in processes)
            {
                try
                {
                    proc.Dispose();
                }
                catch
                {
                    // Best-effort dispose
                }
            }
        }

        return map;
    }

    private static unsafe void CollectTcpV4Ports(List<LocalPortInspectItem> target, Dictionary<int, string> pidMap, CancellationToken ct)
    {
        int size = 0;
        uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, order: false, AfInet, TcpTableOwnerPidAll, 0);
        if (rc != ErrorInsufficientBuffer || size <= 4)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedTcpTable(buffer, ref size, order: false, AfInet, TcpTableOwnerPidAll, 0);
            if (rc != 0)
            {
                return;
            }

            uint count = *(uint*)buffer;
            byte* rowPtr = (byte*)buffer + sizeof(uint);
            int rowSize = sizeof(MibTcpRowOwnerPid);

            for (uint i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = *(MibTcpRowOwnerPid*)(rowPtr + (i * rowSize));
                uint addr = row.dwLocalAddr;
                string ip = new IPAddress(new ReadOnlySpan<byte>(&addr, 4)).ToString();
                int port = DecodeNetworkPort(row.dwLocalPort);
                int pid = unchecked((int)row.dwOwningPid);
                string procName = pidMap.TryGetValue(pid, out string? name) ? name : $"PID {pid}";

                target.Add(new LocalPortInspectItem(
                    Protocol: "TCP",
                    LocalAddress: ip,
                    LocalPort: port,
                    State: FormatTcpState(row.dwState),
                    Pid: pid,
                    ProcessName: procName));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe void CollectTcpV6Ports(List<LocalPortInspectItem> target, Dictionary<int, string> pidMap, CancellationToken ct)
    {
        int size = 0;
        uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, order: false, AfInet6, TcpTableOwnerPidAll, 0);
        if (rc != ErrorInsufficientBuffer || size <= 4)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedTcpTable(buffer, ref size, order: false, AfInet6, TcpTableOwnerPidAll, 0);
            if (rc != 0)
            {
                return;
            }

            uint count = *(uint*)buffer;
            byte* rowPtr = (byte*)buffer + sizeof(uint);
            int rowSize = sizeof(MibTcp6RowOwnerPid);

            for (uint i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                MibTcp6RowOwnerPid* row = (MibTcp6RowOwnerPid*)(rowPtr + (i * rowSize));
                string ip = new IPAddress(new ReadOnlySpan<byte>(row->ucLocalAddr, 16)).ToString();
                int port = DecodeNetworkPort(row->dwLocalPort);
                int pid = unchecked((int)row->dwOwningPid);
                string procName = pidMap.TryGetValue(pid, out string? name) ? name : $"PID {pid}";

                target.Add(new LocalPortInspectItem(
                    Protocol: "TCPv6",
                    LocalAddress: ip,
                    LocalPort: port,
                    State: FormatTcpState(row->dwState),
                    Pid: pid,
                    ProcessName: procName));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe void CollectUdpV4Ports(List<LocalPortInspectItem> target, Dictionary<int, string> pidMap, CancellationToken ct)
    {
        int size = 0;
        uint rc = GetExtendedUdpTable(IntPtr.Zero, ref size, order: false, AfInet, UdpTableOwnerPid, 0);
        if (rc != ErrorInsufficientBuffer || size <= 4)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedUdpTable(buffer, ref size, order: false, AfInet, UdpTableOwnerPid, 0);
            if (rc != 0)
            {
                return;
            }

            uint count = *(uint*)buffer;
            byte* rowPtr = (byte*)buffer + sizeof(uint);
            int rowSize = sizeof(MibUdpRowOwnerPid);

            for (uint i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = *(MibUdpRowOwnerPid*)(rowPtr + (i * rowSize));
                uint addr = row.dwLocalAddr;
                string ip = new IPAddress(new ReadOnlySpan<byte>(&addr, 4)).ToString();
                int port = DecodeNetworkPort(row.dwLocalPort);
                int pid = unchecked((int)row.dwOwningPid);
                string procName = pidMap.TryGetValue(pid, out string? name) ? name : $"PID {pid}";

                target.Add(new LocalPortInspectItem(
                    Protocol: "UDP",
                    LocalAddress: ip,
                    LocalPort: port,
                    State: "—",
                    Pid: pid,
                    ProcessName: procName));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe void CollectUdpV6Ports(List<LocalPortInspectItem> target, Dictionary<int, string> pidMap, CancellationToken ct)
    {
        int size = 0;
        uint rc = GetExtendedUdpTable(IntPtr.Zero, ref size, order: false, AfInet6, UdpTableOwnerPid, 0);
        if (rc != ErrorInsufficientBuffer || size <= 4)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedUdpTable(buffer, ref size, order: false, AfInet6, UdpTableOwnerPid, 0);
            if (rc != 0)
            {
                return;
            }

            uint count = *(uint*)buffer;
            byte* rowPtr = (byte*)buffer + sizeof(uint);
            int rowSize = sizeof(MibUdp6RowOwnerPid);

            for (uint i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                MibUdp6RowOwnerPid* row = (MibUdp6RowOwnerPid*)(rowPtr + (i * rowSize));
                string ip = new IPAddress(new ReadOnlySpan<byte>(row->ucLocalAddr, 16)).ToString();
                int port = DecodeNetworkPort(row->dwLocalPort);
                int pid = unchecked((int)row->dwOwningPid);
                string procName = pidMap.TryGetValue(pid, out string? name) ? name : $"PID {pid}";

                target.Add(new LocalPortInspectItem(
                    Protocol: "UDPv6",
                    LocalAddress: ip,
                    LocalPort: port,
                    State: "—",
                    Pid: pid,
                    ProcessName: procName));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static int DecodeNetworkPort(uint rawPort) =>
        (int)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));

    internal static string FormatServiceState(uint state) => state switch
    {
        ServiceStopped => "Зупинено",
        ServiceStartPending => "Запускається",
        ServiceStopPending => "Зупиняється",
        ServiceRunning => "Працює",
        ServiceContinuePending => "Відновлюється",
        ServicePausePending => "Призупиняється",
        ServicePaused => "Призупинено",
        _ => "Невідомо"
    };

    internal static string FormatServiceStartType(uint startType) => startType switch
    {
        0 => "Завантаження",
        1 => "Система",
        2 => "Автоматично",
        3 => "Вручну",
        4 => "Вимкнено",
        _ => "—"
    };

    internal static string FormatTcpState(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTEN",
        3 => "SYN_SENT",
        4 => "SYN_RCVD",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => "UNKNOWN"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public ServiceStatusProcess ServiceStatusProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfig
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MibTcp6RowOwnerPid
    {
        public fixed byte ucLocalAddr[16];
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public fixed byte ucRemoteAddr[16];
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MibUdp6RowOwnerPid
    {
        public fixed byte ucLocalAddr[16];
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusExW(
        IntPtr hSCManager,
        uint infoLevel,
        uint dwServiceType,
        uint dwServiceState,
        IntPtr lpServices,
        uint cbBufSize,
        out uint pcbBytesNeeded,
        out uint lpServicesReturned,
        ref uint lpResumeHandle,
        string? pszGroupName);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfigW(
        IntPtr hService,
        IntPtr lpServiceConfig,
        uint cbBufSize,
        out uint pcbBytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(IntPtr hService, out ServiceStatus lpServiceStatus);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr hService, uint dwControl, out ServiceStatus lpServiceStatus);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr hSCObject);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int ulAf,
        int tableClass,
        uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int ulAf,
        int tableClass,
        uint reserved);
}
