using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Events;
using Argus.Core.Extensions;
using Argus.Core.Journal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Argus.Core;

public sealed class CoreModule : IArgusModule
{
    public ArgusModuleDescriptor Descriptor { get; } = new(
        Id: "argus.core",
        DisplayName: "Argus.Core",
        Summary: "Доменні контракти, шина подій, кільцевий буфер, JSON-налаштування, журнал дій і розширення",
        Stage: ArgusModuleStage.Ready);
}

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddArgusCore(
        this IServiceCollection services,
        string? settingsFilePath = null,
        string? journalFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IArgusModule, CoreModule>();
        services.AddSingleton<IArgusEventBus, ArgusEventBus>();
        services.AddSingleton<ICleanerRuleLoader, JsonCleanerRuleLoader>();
        services.AddSingleton<IArgusExtensionDiscovery, JsonExtensionDiscovery>();

        services.AddSingleton<IArgusSettingsStore>(sp =>
            new JsonArgusSettingsStore(
                sp.GetRequiredService<ILogger<JsonArgusSettingsStore>>(),
                settingsFilePath));

        services.AddSingleton<IOperationJournal>(sp =>
            new FileBackedOperationJournal(
                sp.GetRequiredService<IArgusEventBus>(),
                sp.GetRequiredService<ILogger<FileBackedOperationJournal>>(),
                journalFilePath ?? ArgusPaths.GetDefaultJournalPath()));

        return services;
    }
}
