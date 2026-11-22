namespace Argus.Core.Contracts.System;

public enum StartupSourceKind
{
    RegistryCurrentUserRun,
    RegistryLocalMachineRun,
    RegistryLocalMachineWow6432Run,
    StartupFolderUser,
    StartupFolderCommon,
    TaskScheduler
}

public sealed record StartupEntryItem(
    string Id,
    string Name,
    string Command,
    StartupSourceKind Source,
    string LocationPath,
    bool IsEnabled,
    bool RequiresElevation,
    bool IsSystemOrMicrosoft,
    string PublisherOrAuthor);

public enum ServiceStartModeOption
{
    Automatic,
    Manual,
    Disabled
}

public sealed record ManagedServiceItem(
    string ServiceName,
    string DisplayName,
    string Status,
    ServiceStartModeOption StartMode,
    string StartModeLabel,
    bool CanStop,
    bool IsCriticalProtected,
    string? BinaryPath,
    string? Description);

/// <summary>
/// Hard protection guard preventing stop, restart, or disabling of core Windows services
/// essential for boot, RPC/SCM, security, networking, and user sessions.
/// </summary>
public static class CriticalServiceProtection
{
    private static readonly HashSet<string> ProtectedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs",
        "RpcEptMapper",
        "DcomLaunch",
        "LSM",
        "SamSs",
        "EventLog",
        "PlugPlay",
        "Power",
        "ProfSvc",
        "UserManager",
        "BFE",
        "mpssvc",
        "WinDefend",
        "SecurityHealthService",
        "WdNisSvc",
        "Sense",
        "TrustedInstaller",
        "CryptSvc",
        "BrokerInfrastructure",
        "SystemEventsBroker",
        "CoreMessagingRegistrar",
        "StateRepository",
        "Appinfo",
        "Schedule",
        "gpsvc",
        "Winmgmt",
        "nsi",
        "Dnscache",
        "Dhcp",
        "VaultSvc",
        "KeyIso",
        "NgcSvc",
        "NgcCtnrSvc",
        "DeviceInstall",
        "vds",
        "VSS"
    };

    public static bool IsCriticalService(string? serviceName) =>
        !string.IsNullOrWhiteSpace(serviceName) && ProtectedServices.Contains(serviceName.Trim());
}

public interface IStartupAndServicesManager
{
    Task<IReadOnlyList<StartupEntryItem>> GetStartupEntriesAsync(
        bool includeSystemTasks = false,
        CancellationToken cancellationToken = default);

    Task<SystemActionResult> SetStartupEntryEnabledAsync(
        StartupEntryItem entry,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManagedServiceItem>> GetManagedServicesAsync(
        CancellationToken cancellationToken = default);

    Task<SystemActionResult> ControlServiceAsync(
        string serviceName,
        ServiceControlAction action,
        CancellationToken cancellationToken = default);

    Task<SystemActionResult> SetServiceStartModeAsync(
        string serviceName,
        ServiceStartModeOption startMode,
        CancellationToken cancellationToken = default);
}
