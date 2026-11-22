namespace Argus.Core.Contracts.System;

public enum ServiceControlAction
{
    Start,
    Stop,
    Restart
}

public sealed record ProcessInspectItem(
    int Pid,
    string Name,
    long WorkingSetBytes,
    int ThreadCount,
    string? ExecutablePath,
    bool IsSystemOrProtected);

public sealed record WindowsServiceInspectItem(
    string ServiceName,
    string DisplayName,
    string Status,
    string StartType,
    bool CanStop);

public sealed record LocalPortInspectItem(
    string Protocol,
    string LocalAddress,
    int LocalPort,
    string State,
    int Pid,
    string ProcessName);

public sealed record SystemActionResult(
    bool Succeeded,
    bool RequiresElevation,
    string Message);

public interface ISystemInspectorService
{
    Task<IReadOnlyList<ProcessInspectItem>> GetProcessesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WindowsServiceInspectItem>> GetServicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalPortInspectItem>> GetLocalPortsAsync(CancellationToken cancellationToken = default);
    Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default);
    Task<SystemActionResult> ControlServiceAsync(string serviceName, ServiceControlAction action, CancellationToken cancellationToken = default);
    bool OpenProcessFolder(string? executablePath);
}
