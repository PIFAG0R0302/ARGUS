using Argus.Core;
using Argus.Core.Contracts.Telemetry;
using Argus.Telemetry.Alerts;
using Argus.Telemetry.Providers;
using Argus.Telemetry.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Telemetry;

public sealed class TelemetryModule : IArgusModule
{
    public ArgusModuleDescriptor Descriptor { get; } = new(
        Id: "argus.telemetry",
        DisplayName: "Argus.Telemetry",
        Summary: "Дворівнева телеметрія реального часу (Win32/PDH, NVML/WDDM, IOCTL, опційний PawnIO)",
        Stage: ArgusModuleStage.Ready);
}

public static class TelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddArgusTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<IArgusModule, TelemetryModule>();
        services.AddSingleton<ICpuTelemetryProvider, WindowsCpuTelemetryProvider>();
        services.AddSingleton<IMemoryTelemetryProvider, WindowsMemoryTelemetryProvider>();
        services.AddSingleton<IGpuTelemetryProvider, CompositeGpuTelemetryProvider>();
        services.AddSingleton<IStorageTelemetryProvider, WindowsStorageTelemetryProvider>();
        services.AddSingleton<AcpiThermalSensorProvider>();
        services.AddSingleton<ISensorProvider>(sp => sp.GetRequiredService<AcpiThermalSensorProvider>());
        services.AddSingleton<PawnIoSensorProvider>();
        services.AddSingleton<ISensorProvider>(sp => sp.GetRequiredService<PawnIoSensorProvider>());
        services.AddSingleton<ISensorTierStatusProvider>(sp => sp.GetRequiredService<PawnIoSensorProvider>());
        services.AddSingleton<TelemetryAlertEvaluator>();
        services.AddSingleton<ArgusTelemetryService>();
        services.AddSingleton<ITelemetryService>(sp => sp.GetRequiredService<ArgusTelemetryService>());
        services.AddSingleton<ITelemetryReportExporter, TelemetryReportExporter>();
        return services;
    }
}
