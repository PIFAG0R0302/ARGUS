using System.Globalization;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Core.Journal;

namespace Argus.Telemetry.Alerts;

/// <summary>
/// Evaluates incoming telemetry snapshots against configured thermal and memory thresholds,
/// publishing cooldown-protected TelemetryAlertEvent notifications and journal warnings.
/// </summary>
public sealed class TelemetryAlertEvaluator
{
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(60);

    private readonly IArgusSettingsStore _settingsStore;
    private readonly IArgusEventBus _eventBus;
    private readonly IOperationJournal _journal;
    private readonly TimeSpan _cooldown;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _lastTriggeredByKey = new(StringComparer.OrdinalIgnoreCase);

    public TelemetryAlertEvaluator(
        IArgusSettingsStore settingsStore,
        IArgusEventBus eventBus,
        IOperationJournal journal,
        TimeSpan? cooldown = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _cooldown = cooldown ?? DefaultCooldown;
    }

    public async Task<IReadOnlyList<TelemetryAlertEvent>> EvaluateAsync(
        TelemetrySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var cfg = _settingsStore.Current;
        var candidates = new List<(string Key, TelemetryAlertEvent Alert)>();

        if (snapshot.Cpu.PackageTemperatureCelsius is { } cpuTemp &&
            cpuTemp >= cfg.CpuTemperatureAlertCelsius)
        {
            string msg = string.Format(
                CultureInfo.CurrentCulture,
                "Температура CPU ({0:0.#} °C) перевищила поріг {1:0.#} °C",
                cpuTemp,
                cfg.CpuTemperatureAlertCelsius);

            candidates.Add((
                "cpu.temp",
                new TelemetryAlertEvent(
                    TelemetryAlertKind.CpuTemperatureHigh,
                    snapshot.Cpu.Name,
                    cpuTemp,
                    cfg.CpuTemperatureAlertCelsius,
                    "°C",
                    msg,
                    snapshot.Timestamp)));
        }

        foreach (var gpu in snapshot.Gpus)
        {
            if (gpu.TemperatureCelsius is { } gpuTemp &&
                gpuTemp >= cfg.GpuTemperatureAlertCelsius)
            {
                string msg = string.Format(
                    CultureInfo.CurrentCulture,
                    "Температура GPU {0} ({1:0.#} °C) перевищила поріг {2:0.#} °C",
                    gpu.Name,
                    gpuTemp,
                    cfg.GpuTemperatureAlertCelsius);

                candidates.Add((
                    $"gpu.temp.{gpu.Name}",
                    new TelemetryAlertEvent(
                        TelemetryAlertKind.GpuTemperatureHigh,
                        gpu.Name,
                        gpuTemp,
                        cfg.GpuTemperatureAlertCelsius,
                        "°C",
                        msg,
                        snapshot.Timestamp)));
            }
        }

        if (snapshot.Memory.LoadPercent >= cfg.RamUsageAlertPercent)
        {
            string msg = string.Format(
                CultureInfo.CurrentCulture,
                "Заповнення ОЗП ({0:0.#}%) перевищило поріг {1:0.#}%",
                snapshot.Memory.LoadPercent,
                cfg.RamUsageAlertPercent);

            candidates.Add((
                "ram.load",
                new TelemetryAlertEvent(
                    TelemetryAlertKind.RamUsageHigh,
                    "Оперативна пам'ять",
                    snapshot.Memory.LoadPercent,
                    cfg.RamUsageAlertPercent,
                    "%",
                    msg,
                    snapshot.Timestamp)));
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
                    snapshot.Timestamp - lastTime < _cooldown)
                {
                    continue;
                }

                _lastTriggeredByKey[key] = snapshot.Timestamp;
                emitted.Add(alert);
            }
        }

        foreach (var alert in emitted)
        {
            _eventBus.Publish(alert);
            await _journal.RecordAsync(
                moduleId: "argus.telemetry",
                severity: OperationSeverity.Warning,
                action: "TelemetryAlert",
                details: alert.Message,
                succeeded: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }
}
