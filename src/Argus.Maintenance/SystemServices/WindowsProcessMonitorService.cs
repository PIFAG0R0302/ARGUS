using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Argus.Core.Contracts.System;
using Argus.Core.Journal;
using Microsoft.Extensions.Logging;

namespace Argus.Maintenance.SystemServices;

/// <summary>
/// Native Win32 process metrics monitor using OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION),
/// GetProcessTimes, GetProcessIoCounters, and QueryFullProcessImageNameW for real per-process
/// CPU %, RAM (Working Set), Disk Read/Write speed (B/s), and executable path without external packages.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsProcessMonitorService : IProcessMonitorService
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorAccessDenied = 5;

    private readonly record struct ProcessCounterSample(
        long TotalCpuTicks100Ns,
        ulong ReadTransferBytes,
        ulong WriteTransferBytes,
        long TimestampTicks);

    private readonly IPrivilegeService _privilegeService;
    private readonly IOperationJournal _journal;
    private readonly ILogger<WindowsProcessMonitorService> _logger;
    private readonly Lock _sync = new();
    private readonly Dictionary<int, ProcessCounterSample> _previousSamples = new();

    public WindowsProcessMonitorService(
        IPrivilegeService privilegeService,
        IOperationJournal journal,
        ILogger<WindowsProcessMonitorService> logger)
    {
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IReadOnlyList<ProcessMetricsItem>> GetProcessMetricsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CollectProcessMetrics(cancellationToken), cancellationToken);
    }

    public async Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default)
    {
        int currentPid = Environment.ProcessId;
        if (pid <= 0 || pid == 4 || pid == currentPid)
        {
            const string msg = "Завершення критичного системного процесу або поточного процесу ARGUS заблоковано.";
            await _journal.RecordAsync(
                "argus.processes",
                OperationSeverity.Warning,
                "CriticalProcessTerminateBlocked",
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
                string blockedMsg = $"Процес «{processName}» (PID {pid}) є критичним системним процесом Windows і жорстко захищений від завершення.";
                await _journal.RecordAsync(
                    "argus.processes",
                    OperationSeverity.Warning,
                    "CriticalProcessTerminateBlocked",
                    $"PID={pid} ({processName}): {blockedMsg}",
                    succeeded: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return new SystemActionResult(false, RequiresElevation: false, blockedMsg);
            }

            proc.Kill(entireProcessTree: false);
            try
            {
                proc.WaitForExit(1500);
            }
            catch
            {
                // Non-fatal wait timeout
            }

            string okMsg = $"Процес «{processName}» (PID {pid}) завершено.";
            await _journal.RecordAsync(
                "argus.processes",
                OperationSeverity.Success,
                "ProcessTerminated",
                okMsg,
                succeeded: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(true, RequiresElevation: false, okMsg);
        }
        catch (ArgumentException)
        {
            return new SystemActionResult(false, RequiresElevation: false, $"Процес із PID {pid} уже завершено.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied || !_privilegeService.IsAdministrator)
        {
            string uacMsg = $"Потрібні права адміністратора (UAC), щоб завершити процес «{processName}» (PID {pid}).";
            await _journal.RecordAsync(
                "argus.processes",
                OperationSeverity.Warning,
                "ProcessTerminateRequiresElevation",
                $"PID={pid} ({processName}): {uacMsg}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: !_privilegeService.IsAdministrator, uacMsg);
        }
        catch (UnauthorizedAccessException)
        {
            string uacMsg = $"Потрібні права адміністратора (UAC), щоб завершити процес «{processName}» (PID {pid}).";
            await _journal.RecordAsync(
                "argus.processes",
                OperationSeverity.Warning,
                "ProcessTerminateRequiresElevation",
                $"PID={pid} ({processName}): {uacMsg}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: !_privilegeService.IsAdministrator, uacMsg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to terminate process PID={Pid} ({Name}).", pid, processName);
            string failMsg = $"Не вдалося завершити «{processName}» (PID {pid}): {ex.Message}";
            await _journal.RecordAsync(
                "argus.processes",
                OperationSeverity.Error,
                "ProcessTerminateFailed",
                failMsg,
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: false, failMsg);
        }
    }

    public bool OpenProcessFolder(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{executablePath}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to open folder for '{ExecutablePath}'.", executablePath);
            return false;
        }
    }

    internal static double ComputeCpuPercent(long deltaCpuTicks100Ns, double elapsedSeconds, int logicalProcessors)
    {
        if (deltaCpuTicks100Ns <= 0 || elapsedSeconds <= 0.001 || logicalProcessors <= 0)
        {
            return 0.0;
        }

        double cpuSeconds = deltaCpuTicks100Ns / 10_000_000.0;
        double percent = (cpuSeconds / (elapsedSeconds * logicalProcessors)) * 100.0;
        return Math.Clamp(Math.Round(percent, 1), 0.0, 100.0);
    }

    internal static double ComputeRateBytesPerSec(ulong deltaBytes, double elapsedSeconds)
    {
        if (deltaBytes == 0 || elapsedSeconds <= 0.001)
        {
            return 0.0;
        }

        return Math.Max(0.0, deltaBytes / elapsedSeconds);
    }

    private IReadOnlyList<ProcessMetricsItem> CollectProcessMetrics(CancellationToken ct)
    {
        Process[] processes = Process.GetProcesses();
        int currentPid = Environment.ProcessId;
        int logicalCores = Math.Max(1, Environment.ProcessorCount);
        long nowTicks = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;

        var currentPids = new HashSet<int>(processes.Length);
        var results = new List<ProcessMetricsItem>(processes.Length);
        var nextSamples = new Dictionary<int, ProcessCounterSample>(processes.Length);

        Dictionary<int, ProcessCounterSample> prevSnapshot;
        lock (_sync)
        {
            prevSnapshot = new Dictionary<int, ProcessCounterSample>(_previousSamples);
        }

        try
        {
            foreach (var proc in processes)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    int pid = proc.Id;
                    string name = proc.ProcessName;
                    currentPids.Add(pid);

                    long workingSet = 0;
                    int threads = 0;
                    try { workingSet = proc.WorkingSet64; } catch { }
                    try { threads = proc.Threads.Count; } catch { }

                    bool isCritical = CriticalProcessProtection.IsCriticalProcess(pid, name, currentPid);
                    bool isProtectedAccess = pid is 0 or 4;

                    double cpuPercent = 0.0;
                    double diskReadBps = 0.0;
                    double diskWriteBps = 0.0;
                    string? exePath = null;

                    if (pid is not (0 or 4))
                    {
                        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
                        if (handle != IntPtr.Zero)
                        {
                            try
                            {
                                exePath = QueryExecutablePath(handle);

                                long cpuTicks = 0;
                                if (GetProcessTimes(handle, out _, out _, out var kernelTime, out var userTime))
                                {
                                    cpuTicks = FileTimeToTicks(kernelTime) + FileTimeToTicks(userTime);
                                }

                                ulong readBytes = 0;
                                ulong writeBytes = 0;
                                if (GetProcessIoCounters(handle, out var ioCounters))
                                {
                                    readBytes = ioCounters.ReadTransferCount;
                                    writeBytes = ioCounters.WriteTransferCount;
                                }

                                var sample = new ProcessCounterSample(cpuTicks, readBytes, writeBytes, nowTicks);
                                nextSamples[pid] = sample;

                                if (prevSnapshot.TryGetValue(pid, out var prev))
                                {
                                    double elapsedSec = (nowTicks - prev.TimestampTicks) / freq;
                                    if (elapsedSec > 0.05)
                                    {
                                        long deltaCpu = Math.Max(0, sample.TotalCpuTicks100Ns - prev.TotalCpuTicks100Ns);
                                        ulong deltaRead = sample.ReadTransferBytes >= prev.ReadTransferBytes
                                            ? sample.ReadTransferBytes - prev.ReadTransferBytes
                                            : 0;
                                        ulong deltaWrite = sample.WriteTransferBytes >= prev.WriteTransferBytes
                                            ? sample.WriteTransferBytes - prev.WriteTransferBytes
                                            : 0;

                                        cpuPercent = ComputeCpuPercent(deltaCpu, elapsedSec, logicalCores);
                                        diskReadBps = ComputeRateBytesPerSec(deltaRead, elapsedSec);
                                        diskWriteBps = ComputeRateBytesPerSec(deltaWrite, elapsedSec);
                                    }
                                }
                            }
                            finally
                            {
                                _ = CloseHandle(handle);
                            }
                        }
                        else
                        {
                            isProtectedAccess = true;
                        }
                    }

                    results.Add(new ProcessMetricsItem(
                        Pid: pid,
                        Name: name,
                        CpuPercent: cpuPercent,
                        WorkingSetBytes: Math.Max(0, workingSet),
                        DiskReadBytesPerSec: diskReadBps,
                        DiskWriteBytesPerSec: diskWriteBps,
                        ThreadCount: Math.Max(0, threads),
                        ExecutablePath: exePath,
                        IsCriticalSystem: isCritical,
                        IsProtectedAccess: isProtectedAccess));
                }
                catch
                {
                    // Skip transiently exited process
                }
            }
        }
        finally
        {
            foreach (var proc in processes)
            {
                try { proc.Dispose(); } catch { }
            }
        }

        lock (_sync)
        {
            _previousSamples.Clear();
            foreach (var kvp in nextSamples)
            {
                if (currentPids.Contains(kvp.Key))
                {
                    _previousSamples[kvp.Key] = kvp.Value;
                }
            }
        }

        return results;
    }

    private static string? QueryExecutablePath(IntPtr processHandle)
    {
        uint size = 1024;
        IntPtr buffer = Marshal.AllocHGlobal((int)(size * 2));
        try
        {
            if (QueryFullProcessImageNameW(processHandle, 0, buffer, ref size) && size > 0)
            {
                return Marshal.PtrToStringUni(buffer, (int)size);
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static long FileTimeToTicks(FileTime ft)
    {
        return ((long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
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
    private static partial bool GetProcessTimes(
        IntPtr hProcess,
        out FileTime lpCreationTime,
        out FileTime lpExitTime,
        out FileTime lpKernelTime,
        out FileTime lpUserTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(
        IntPtr hProcess,
        out IoCounters lpIoCounters);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        uint dwFlags,
        IntPtr lpExeName,
        ref uint lpdwSize);
}
