namespace Argus.Core.Contracts.Telemetry;

public enum SensorTier
{
    NativeZeroDriver,
    SignedPawnIo
}

public enum SensorKind
{
    LoadPercent,
    TemperatureCelsius,
    ClockMHz,
    MemoryBytes,
    FanRpm,
    PowerWatts,
    VoltageVolts,
    ThroughputBytesPerSec
}

public sealed record SensorReading(
    string SensorId,
    string Name,
    string Component,
    SensorKind Kind,
    double Value,
    string Unit,
    SensorTier Tier,
    DateTimeOffset Timestamp);

public sealed record CpuSnapshot(
    string Name,
    int PhysicalCores,
    int LogicalProcessors,
    double TotalLoadPercent,
    IReadOnlyList<double> PerCoreLoadPercent,
    double? CurrentClockMHz,
    double? PackageTemperatureCelsius,
    double? PackagePowerWatts);

public sealed record MemorySnapshot(
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes,
    double LoadPercent);

public sealed record GpuSnapshot(
    string Name,
    string Vendor,
    double? CoreLoadPercent,
    long? VramTotalBytes,
    long? VramUsedBytes,
    double? TemperatureCelsius,
    double? CoreClockMHz,
    int? FanRpm);

public sealed record StorageThermalSnapshot(
    int DiskNumber,
    string Model,
    double? TemperatureCelsius,
    double? ReadBytesPerSec,
    double? WriteBytesPerSec);

public sealed record TelemetrySnapshot(
    DateTimeOffset Timestamp,
    SensorTier ActiveHighestTier,
    CpuSnapshot Cpu,
    MemorySnapshot Memory,
    IReadOnlyList<GpuSnapshot> Gpus,
    IReadOnlyList<StorageThermalSnapshot> Storage,
    IReadOnlyList<SensorReading> ExtraSensors);

public sealed record TelemetryUpdatedEvent(TelemetrySnapshot Snapshot);

public interface ISensorProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    SensorTier Tier { get; }
    bool IsAvailable { get; }
    Task<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default);
}

public interface ITelemetryService
{
    bool IsRunning { get; }
    TelemetrySnapshot? Latest { get; }
    IReadOnlyList<TelemetrySnapshot> GetHistory(int maxSamples = 180);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public enum TelemetryAlertKind
{
    CpuTemperatureHigh,
    GpuTemperatureHigh,
    RamUsageHigh
}

public sealed record TelemetryAlertEvent(
    TelemetryAlertKind Kind,
    string Component,
    double MeasuredValue,
    double ThresholdValue,
    string Unit,
    string Message,
    DateTimeOffset Timestamp);

public enum TelemetryReportFormat
{
    Html,
    Json,
    Csv
}

public sealed record TelemetryReportExportResult(
    bool Succeeded,
    TelemetryReportFormat Format,
    string FilePath,
    int SampleCount,
    string Message);

public interface ITelemetryReportExporter
{
    Task<TelemetryReportExportResult> ExportAsync(
        TelemetryReportFormat format,
        string? outputDirectory = null,
        int maxSamples = 360,
        CancellationToken cancellationToken = default);
}
