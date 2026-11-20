using Argus.Core;
using Argus.Core.Contracts.Devices;
using Argus.Devices.Monitoring;
using Argus.Devices.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Devices;

public sealed class DevicesModule : IArgusModule
{
    public ArgusModuleDescriptor Descriptor { get; } = new(
        Id: "argus.devices",
        DisplayName: "Argus.Devices",
        Summary: "Інвентаризація заліза, дерево пристроїв SetupAPI/WMI й події CfgMgr32/WM_DEVICECHANGE",
        Stage: ArgusModuleStage.Ready);
}

public static class DevicesServiceCollectionExtensions
{
    public static IServiceCollection AddArgusDevices(this IServiceCollection services)
    {
        services.AddSingleton<IArgusModule, DevicesModule>();
        services.AddSingleton<IDeviceProvider, WindowsDeviceProvider>();
        services.AddSingleton<WindowsDeviceChangeMonitor>();
        services.AddSingleton<IDeviceChangeMonitor>(sp => sp.GetRequiredService<WindowsDeviceChangeMonitor>());
        return services;
    }
}
