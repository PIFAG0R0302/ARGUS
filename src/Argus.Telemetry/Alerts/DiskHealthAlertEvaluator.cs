using System.Globalization;
using System.Management;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Core.Journal;

namespace Argus.Telemetry.Alerts;

public sealed record DriveSpaceSample(
    string Name,
    string VolumeLabel,
    long TotalBytes,
    long FreeBytes)
{
    public double FreePercent => TotalBytes > 0
        ? Math.Clamp((double)FreeBytes / TotalBytes * 100.0, 0.0, 100.0)
        : 100.0;
}

public sealed record DiskSmartSample(
    int DiskNumber,
    string Model,
    SmartHealthStatus Health);

/// <summary>
/// Periodically evaluates fixed logical volumes for critically low free space and physical disks
/// for S.M.A.R.T. health degradation (Caution/Critical), emitting cooldown-protected
/// <see cref="TelemetryAlertEvent"/> notifications and operation journal warnings.
/// </summary>
public sealed class DiskHealthAlertEvaluator
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(15);

    private readonly IArgusSettingsStore _settingsStore;
    private readonly IArgusEventBus _eventBus;
    private readonly IOperationJournal _journal;
    private readonly Func<IReadOnlyList<DriveSpaceSample>> _driveSpaceReader;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DiskSmartSample>>> _smartHealthReader;
    private readonly TimeSpan _cooldown;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _lastTriggeredByKey = new(StringComparer.OrdinalIgnoreCase);

    public DiskHealthAlertEvaluator(
        IArgusSettingsStore settingsStore,
        IArgusEventBus eventBus,
        IOperationJournal journal,
        Func<IReadOnlyList<DriveSpaceSample>>? driveSpaceReader = null,
        Func<CancellationToken, Task<IReadOnlyList<DiskSmartSample>>>? smartHealthReader = null,
        TimeSpan? cooldown = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _driveSpaceReader = driveSpaceReader ?? ReadFixedDrives;
        _smartHealthReader = smartHealthReader ?? ReadPhysicalDiskSmartHealthAsync;
        _cooldown = cooldown ?? DefaultCooldown;
    }

    public async Task<IReadOnlyList<TelemetryAlertEvent>> EvaluateAsync(
        DateTimeOffset? timestampOverride = null,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timestampOverride ?? DateTimeOffset.UtcNow;
        var cfg = _settingsStore.Current;
        var candidates = new List<(string Key, TelemetryAlertEvent Alert)>();

        IReadOnlyList<DriveSpaceSample> drives;
        try
        {
            drives = _driveSpaceReader();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            drives = [];
        }

        foreach (var drive in drives)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (drive.TotalBytes <= 0)
            {
                continue;
            }

            double freePercent = drive.FreePercent;
            if (freePercent <= cfg.DiskFreeSpaceAlertPercent)
            {
                double freeGb = Math.Max(0.0, drive.FreeBytes / (1024.0 * 1024.0 * 1024.0));
                string labelPart = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? string.Empty : $" ({drive.VolumeLabel})";
                string msg = string.Format(
                    CultureInfo.CurrentCulture,
                    "Критично мало місця на диску {0}{1}: вільно {2:0.0} ГБ ({3:0.#}%, поріг {4:0.#}%)",
                    drive.Name,
                    labelPart,
                    freeGb,
                    freePercent,
                    cfg.DiskFreeSpaceAlertPercent);

                candidates.Add((
                    $"disk.space.{drive.Name}",
                    new TelemetryAlertEvent(
                        Kind: TelemetryAlertKind.DiskSpaceLow,
                        Component: drive.Name,
                        MeasuredValue: Math.Round(freePercent, 1),
                        ThresholdValue: cfg.DiskFreeSpaceAlertPercent,
                        Unit: "%",
                        Message: msg,
                        Timestamp: now)));
            }
        }

        if (cfg.EnableSmartDegradationAlert)
        {
            IReadOnlyList<DiskSmartSample> smartDisks;
            try
            {
                smartDisks = await _smartHealthReader(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                smartDisks = [];
            }

            foreach (var disk in smartDisks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (disk.Health is not (SmartHealthStatus.Caution or SmartHealthStatus.Critical))
                {
                    continue;
                }

                string severityText = disk.Health == SmartHealthStatus.Critical
                    ? "Критичний стан S.M.A.R.T."
                    : "Погіршення стану S.M.A.R.T.";
                string model = string.IsNullOrWhiteSpace(disk.Model) ? $"Диск {disk.DiskNumber}" : disk.Model;
                string msg = $"{severityText} накопичувача «{model}» (Диск {disk.DiskNumber}). Рекомендується створити резервну копію важливих даних.";

                candidates.Add((
                    $"disk.smart.{disk.DiskNumber}",
                    new TelemetryAlertEvent(
                        Kind: TelemetryAlertKind.DiskSmartDegraded,
                        Component: model,
                        MeasuredValue: disk.Health == SmartHealthStatus.Critical ? 2.0 : 1.0,
                        ThresholdValue: 1.0,
                        Unit: "S.M.A.R.T.",
                        Message: msg,
                        Timestamp: now)));
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var emitted = new List<TelemetryAlertEvent>();
        lock (_sync)
        {
            foreach (var (key, alert) in candidates)
            {
                if (_lastTriggeredByKey.TryGetValue(key, out DateTimeOffset lastTime) &&
                    now - lastTime < _cooldown)
                {
                    continue;
                }

                _lastTriggeredByKey[key] = now;
                emitted.Add(alert);
            }
        }

        foreach (var alert in emitted)
        {
            _eventBus.Publish(alert);
            await _journal.RecordAsync(
                moduleId: "argus.telemetry",
                severity: OperationSeverity.Warning,
                action: alert.Kind == TelemetryAlertKind.DiskSmartDegraded ? "DiskSmartAlert" : "DiskSpaceAlert",
                details: alert.Message,
                succeeded: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    private static IReadOnlyList<DriveSpaceSample> ReadFixedDrives()
    {
        var result = new List<DriveSpaceSample>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return result;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                string name = drive.Name.TrimEnd('\\');
                string label = string.Empty;
                try
                {
                    label = drive.VolumeLabel ?? string.Empty;
                }
                catch
                {
                    // Optional label
                }

                result.Add(new DriveSpaceSample(
                    Name: name,
                    VolumeLabel: label,
                    TotalBytes: drive.TotalSize,
                    FreeBytes: drive.AvailableFreeSpace));
            }
            catch
            {
                // Skip transiently unavailable drive
            }
        }

        return result;
    }

    private static Task<IReadOnlyList<DiskSmartSample>> ReadPhysicalDiskSmartHealthAsync(CancellationToken ct)
    {
        return Task.Run(() => ReadPhysicalDiskSmartHealthCore(ct), ct);
    }

    private static IReadOnlyList<DiskSmartSample> ReadPhysicalDiskSmartHealthCore(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var result = new List<DiskSmartSample>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT DeviceId, FriendlyName, HealthStatus FROM MSFT_PhysicalDisk"));
            using var collection = searcher.Get();

            foreach (ManagementBaseObject obj in collection)
            {
                using (obj)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!int.TryParse(obj["DeviceId"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int diskNumber) ||
                        diskNumber < 0)
                    {
                        continue;
                    }

                    string model = (obj["FriendlyName"] as string)?.Trim() ?? $"Диск {diskNumber}";
                    ushort rawHealth = TryConvertUInt16(obj["HealthStatus"], fallback: 5);
                    var health = rawHealth switch
                    {
                        0 => SmartHealthStatus.Healthy,
                        1 => SmartHealthStatus.Caution,
                        2 => SmartHealthStatus.Critical,
                        _ => SmartHealthStatus.Unknown
                    };

                    result.Add(new DiskSmartSample(diskNumber, model, health));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Storage WMI namespace unavailable or access denied; return empty safely.
        }

        return result;
    }

    private static ushort TryConvertUInt16(object? value, ushort fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        try
        {
            return Convert.ToUInt16(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }
}
