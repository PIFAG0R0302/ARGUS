namespace Argus.Core.Contracts.System;

public sealed record ProcessMetricsItem(
    int Pid,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    double DiskReadBytesPerSec,
    double DiskWriteBytesPerSec,
    int ThreadCount,
    string? ExecutablePath,
    bool IsCriticalSystem,
    bool IsProtectedAccess)
{
    public double TotalDiskBytesPerSec => DiskReadBytesPerSec + DiskWriteBytesPerSec;
}

/// <summary>
/// Hard protection list for critical Windows system processes (System, smss, csrss, wininit,
/// services, lsass, explorer, etc.) preventing accidental termination even when elevated.
/// </summary>
public static class CriticalProcessProtection
{
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle",
        "System",
        "Registry",
        "Secure System",
        "Memory Compression",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "LsaIso",
        "winlogon",
        "explorer",
        "dwm",
        "fontdrvhost",
        "sihost",
        "svchost",
        "MsMpEng",
        "NisSrv",
        "SecurityHealthService"
    };

    public static bool IsCriticalProcess(int pid, string? processName, int currentProcessId)
    {
        if (pid <= 0 || pid == 4 || pid == currentProcessId)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        string trimmed = processName.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return CriticalNames.Contains(trimmed);
    }
}

public interface IProcessMonitorService
{
    Task<IReadOnlyList<ProcessMetricsItem>> GetProcessMetricsAsync(CancellationToken cancellationToken = default);
    Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default);
    bool OpenProcessFolder(string? executablePath);
}
