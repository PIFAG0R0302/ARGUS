using Argus.Core;
using Argus.Core.Contracts.Migration;
using Argus.Migration.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Migration;

public sealed class MigrationModule : IArgusModule
{
    public ArgusModuleDescriptor Descriptor { get; } = new(
        Id: "argus.migration",
        DisplayName: "Argus.Migration",
        Summary: "Скомпільований C#-рушій CopyOS (NtfsVssCloner, VSS+DISM, профілі SATA/NVMe і USB)",
        Stage: ArgusModuleStage.Ready);
}

public static class MigrationServiceCollectionExtensions
{
    public static IServiceCollection AddArgusMigration(this IServiceCollection services)
    {
        services.AddSingleton<IArgusModule, MigrationModule>();
        services.AddSingleton<IMigrationHostOperations, WindowsMigrationHostOperations>();
        services.AddSingleton<IMigrationEngine, WindowsMigrationEngine>();
        return services;
    }
}
