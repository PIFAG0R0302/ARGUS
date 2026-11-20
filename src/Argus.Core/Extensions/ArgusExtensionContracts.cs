namespace Argus.Core.Extensions;

public enum ArgusSectionId
{
    Overview,
    Monitoring,
    Devices,
    Cleanup,
    Disks,
    Migration,
    Journal,
    Settings
}

public enum ArgusExtensionKind
{
    ManagedPlugin,
    NativeSensorLibrary,
    CleanerRulePack
}

public sealed record ArgusExtensionManifest(
    string Id,
    string Name,
    string Version,
    ArgusExtensionKind Kind,
    string EntryPoint,
    string MinimumCoreVersion,
    bool RequiresElevation);

public interface IArgusExtensionDiscovery
{
    Task<IReadOnlyList<ArgusExtensionManifest>> DiscoverAsync(
        string extensionsDirectory,
        CancellationToken cancellationToken = default);
}
