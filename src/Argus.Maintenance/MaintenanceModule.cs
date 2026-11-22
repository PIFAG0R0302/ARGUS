using Argus.Core;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.System;
using Argus.Maintenance.Cleanup;
using Argus.Maintenance.Disks;
using Argus.Maintenance.SystemServices;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Maintenance;

public sealed class MaintenanceModule : IArgusModule
{
    public ArgusModuleDescriptor Descriptor { get; } = new(
        Id: "argus.maintenance",
        DisplayName: "Argus.Maintenance",
        Summary: "Очищення ОС на JSON-правилах, HDD-дефрагментація, SSD TRIM, S.M.A.R.T., UAC, автозапуск і System Restore",
        Stage: ArgusModuleStage.Ready);
}

public static class MaintenanceServiceCollectionExtensions
{
    public static IServiceCollection AddArgusMaintenance(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IArgusModule, MaintenanceModule>();
        services.AddSingleton<IPrivilegeService, WindowsPrivilegeService>();
        services.AddSingleton<ISystemInspectorService, WindowsSystemInspectorService>();
        services.AddSingleton<IProcessMonitorService, WindowsProcessMonitorService>();
        services.AddSingleton<IStartupAndServicesManager, WindowsStartupAndServicesManager>();
        services.AddSingleton<INetworkManagerService, WindowsNetworkManagerService>();
        services.AddSingleton<IProcessNetworkTrafficService, WindowsProcessNetworkTrafficService>();
        services.AddSingleton<IStartupRegistrationService>(_ => new WindowsStartupRegistrationService());
        services.AddSingleton<IRestorePointService, WindowsRestorePointService>();
        services.AddSingleton<IAppUpdateService>(sp => new GitHubAppUpdateService(
            sp.GetRequiredService<Argus.Core.Configuration.IArgusSettingsStore>(),
            sp.GetRequiredService<Argus.Core.Journal.IOperationJournal>(),
            sp.GetRequiredService<Argus.Core.Events.IArgusEventBus>(),
            privilegeService: sp.GetRequiredService<IPrivilegeService>()));
        services.AddSingleton<ICleanerService, WindowsCleanerService>();
        services.AddSingleton<IDiskOptimizer, WindowsDiskOptimizer>();
        services.AddSingleton<IDiskSpaceAnalyzer, WindowsDiskSpaceAnalyzer>();
        return services;
    }
}
