namespace Argus.Core.Contracts.Devices;

public sealed record OsVersionInfo(
    string Edition,
    string DisplayVersion,
    string BuildNumber,
    string Architecture,
    bool IsUefiFirmware);

public sealed record CpuSpec(
    string Name,
    string Manufacturer,
    string Architecture,
    string Socket,
    int PhysicalCores,
    int LogicalProcessors,
    double MaxClockMHz,
    long L2CacheBytes,
    long L3CacheBytes);

public sealed record RamModuleSpec(
    string SlotLocator,
    string Manufacturer,
    string PartNumber,
    long CapacityBytes,
    int SpeedMHz,
    string MemoryType);

public sealed record MotherboardSpec(
    string Manufacturer,
    string Product,
    string BiosVendor,
    string BiosVersion,
    string BiosReleaseDate);

public sealed record GpuSpec(
    string Name,
    string Vendor,
    string DriverVersion,
    long DedicatedVideoMemoryBytes,
    string PciDeviceId);

public sealed record NetworkAdapterSpec(
    string Name,
    string MacAddress,
    long LinkSpeedBitsPerSec,
    bool IsConnected,
    string AdapterType);

public sealed record HardwareInventory(
    DateTimeOffset CollectedAt,
    OsVersionInfo Os,
    CpuSpec Cpu,
    IReadOnlyList<RamModuleSpec> RamModules,
    MotherboardSpec Motherboard,
    IReadOnlyList<GpuSpec> Gpus,
    IReadOnlyList<NetworkAdapterSpec> NetworkAdapters);

public enum DeviceStatus
{
    Ok,
    Warning,
    Error,
    Disabled,
    Unknown
}

public enum DeviceChangeKind
{
    Arrived,
    Removed,
    ConfigChanged
}

public sealed record DeviceNode(
    string InstanceId,
    string FriendlyName,
    string Category,
    string BusType,
    string Manufacturer,
    string DriverVersion,
    string? DriverDate,
    DeviceStatus Status,
    int ProblemCode,
    bool IsPresent,
    string? ParentInstanceId);

public sealed record DeviceTopologyChangedEvent(
    DeviceChangeKind Kind,
    string? DeviceInstanceId,
    DateTimeOffset Timestamp);

public interface IDeviceProvider
{
    Task<HardwareInventory> GetInventoryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceNode>> GetDevicesAsync(CancellationToken cancellationToken = default);
}

public interface IDeviceChangeMonitor : IDisposable
{
    bool IsMonitoring { get; }
    void Start();
    void Stop();
}
