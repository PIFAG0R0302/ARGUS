namespace Argus.Core;

public enum ArgusModuleStage
{
    Scaffolded,
    Ready,
    OptionalDisabled
}

public sealed record ArgusModuleDescriptor(
    string Id,
    string DisplayName,
    string Summary,
    ArgusModuleStage Stage);

public interface IArgusModule
{
    ArgusModuleDescriptor Descriptor { get; }
}
