using Argus.Core.Collections;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Events;
using Argus.Telemetry.Alerts;
using Argus.Telemetry.Providers;
using Microsoft.Extensions.Logging;

namespace Argus.Telemetry;

/// <summary>
/// Background real-time telemetry collector feeding RingBuffer&lt;TelemetrySnapshot&gt; and IArgusEventBus.
/// Respects TelemetryIntervalMs dynamically and isolates each sensor provider so a single failure never interrupts sampling.
/// </summary>
public sealed class ArgusTelemetryService : ITelemetryService, IDisposable
{
    private const int MaxBufferCapacity = 3600;

    private readonly ICpuTelemetryProvider _cpuProvider;
    private readonly IMemoryTelemetryProvider _memoryProvider;
    private readonly IGpuTelemetryProvider _gpuProvider;
    private readonly IStorageTelemetryProvider _storageProvider;
    private readonly IReadOnlyList<ISensorProvider> _extraProviders;
    private readonly IArgusSettingsStore _settingsStore;
    private readonly IArgusEventBus _eventBus;
    private readonly ILogger<ArgusTelemetryService> _logger;
    private readonly TelemetryAlertEvaluator? _alertEvaluator;
    private readonly DiskHealthAlertEvaluator? _diskHealthEvaluator;

    private readonly RingBuffer<TelemetrySnapshot> _history = new(MaxBufferCapacity);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private Task? _diskHealthLoopTask;
    private volatile TelemetrySnapshot? _latest;
    private volatile bool _isRunning;
    private bool _disposed;

    public ArgusTelemetryService(
        ICpuTelemetryProvider cpuProvider,
        IMemoryTelemetryProvider memoryProvider,
        IGpuTelemetryProvider gpuProvider,
        IStorageTelemetryProvider storageProvider,
        IEnumerable<ISensorProvider> extraProviders,
        IArgusSettingsStore settingsStore,
        IArgusEventBus eventBus,
        ILogger<ArgusTelemetryService> logger,
        TelemetryAlertEvaluator? alertEvaluator = null,
        DiskHealthAlertEvaluator? diskHealthEvaluator = null)
    {
        _cpuProvider = cpuProvider ?? throw new ArgumentNullException(nameof(cpuProvider));
        _memoryProvider = memoryProvider ?? throw new ArgumentNullException(nameof(memoryProvider));
        _gpuProvider = gpuProvider ?? throw new ArgumentNullException(nameof(gpuProvider));
        _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
        _extraProviders = (extraProviders ?? throw new ArgumentNullException(nameof(extraProviders))).ToArray();
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _alertEvaluator = alertEvaluator;
        _diskHealthEvaluator = diskHealthEvaluator;
    }

    public bool IsRunning => _isRunning;

    public TelemetrySnapshot? Latest => _latest;

    public IReadOnlyList<TelemetrySnapshot> GetHistory(int maxSamples = 180)
    {
        return _history.SnapshotTail(maxSamples);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isRunning)
            {
                return;
            }

            _loopCts = new CancellationTokenSource();
            _isRunning = true;
            _loopTask = Task.Run(() => RunSamplingLoopAsync(_loopCts.Token), CancellationToken.None);
            if (_diskHealthEvaluator is not null)
            {
                _diskHealthLoopTask = Task.Run(() => RunDiskHealthLoopAsync(_loopCts.Token), CancellationToken.None);
            }

            _logger.LogInformation("Telemetry polling started (interval={IntervalMs}ms).", _settingsStore.Current.TelemetryIntervalMs);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            if (_loopCts is not null)
            {
                await _loopCts.CancelAsync().ConfigureAwait(false);
            }

            if (_loopTask is not null)
            {
                try
                {
                    await _loopTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected on shutdown
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Telemetry polling loop did not stop within timeout.");
                }
            }

            if (_diskHealthLoopTask is not null)
            {
                try
                {
                    await _diskHealthLoopTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected on shutdown
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Disk health monitoring loop did not stop within timeout.");
                }
            }

            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
            _diskHealthLoopTask = null;
            _logger.LogInformation("Telemetry polling stopped.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<TelemetrySnapshot> SampleOnceAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var extraSensors = new List<SensorReading>();
        SensorTier highestTier = SensorTier.NativeZeroDriver;

        double? cpuPackageTemp = null;
        double? cpuPackagePower = null;

        foreach (var provider in _extraProviders)
        {
            if (!provider.IsAvailable)
            {
                continue;
            }

            try
            {
                var readings = await provider.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (readings.Count > 0)
                {
                    if (provider.Tier == SensorTier.SignedPawnIo)
                    {
                        highestTier = SensorTier.SignedPawnIo;
                    }

                    foreach (var reading in readings)
                    {
                        extraSensors.Add(reading);
                        if (reading.Tier == SensorTier.SignedPawnIo &&
                            reading.Component.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                        {
                            if (reading.Kind == SensorKind.TemperatureCelsius && cpuPackageTemp is null)
                            {
                                cpuPackageTemp = reading.Value;
                            }
                            else if (reading.Kind == SensorKind.PowerWatts && cpuPackagePower is null)
                            {
                                cpuPackagePower = reading.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Extra sensor provider '{ProviderId}' failed during sampling.", provider.ProviderId);
            }
        }

        CpuSnapshot cpu;
        try
        {
            cpu = _cpuProvider.Sample(cpuPackageTemp, cpuPackagePower);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CPU telemetry sample failed.");
            cpu = new CpuSnapshot("CPU", Environment.ProcessorCount, Environment.ProcessorCount, 0, [], null, null, null);
        }

        MemorySnapshot memory;
        try
        {
            memory = _memoryProvider.Sample();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory telemetry sample failed.");
            memory = new MemorySnapshot(0, 0, 0, 0);
        }

        IReadOnlyList<GpuSnapshot> gpus;
        try
        {
            var gpuResult = _gpuProvider.Sample(now);
            gpus = gpuResult.Gpus;
            if (gpuResult.ExtraSensors.Count > 0)
            {
                extraSensors.AddRange(gpuResult.ExtraSensors);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPU telemetry sample failed.");
            gpus = [];
        }

        IReadOnlyList<StorageThermalSnapshot> storage;
        try
        {
            storage = _storageProvider.Sample();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Storage telemetry sample failed.");
            storage = [];
        }

        var snapshot = new TelemetrySnapshot(
            Timestamp: now,
            ActiveHighestTier: highestTier,
            Cpu: cpu,
            Memory: memory,
            Gpus: gpus,
            Storage: storage,
            ExtraSensors: extraSensors);

        _history.Add(snapshot);
        _latest = snapshot;
        _eventBus.Publish(new TelemetryUpdatedEvent(snapshot));

        if (_alertEvaluator is not null)
        {
            try
            {
                await _alertEvaluator.EvaluateAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Telemetry threshold alert evaluation failed.");
            }
        }

        return snapshot;
    }

    private async Task RunSamplingLoopAsync(CancellationToken ct)
    {
        try
        {
            // Small initial pause so CPU delta between constructor prime and sample #1 reflects real workload.
            await Task.Delay(150, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                await SampleOnceAsync(ct).ConfigureAwait(false);

                int intervalMs = Math.Clamp(_settingsStore.Current.TelemetryIntervalMs, 250, 10000);
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Clean stop requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in telemetry polling loop.");
        }
        finally
        {
            _isRunning = false;
        }
    }

    private async Task RunDiskHealthLoopAsync(CancellationToken ct)
    {
        if (_diskHealthEvaluator is null)
        {
            return;
        }

        try
        {
            // Initial 5s delay so application startup remains instant and low-overhead.
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _diskHealthEvaluator.EvaluateAsync(cancellationToken: ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Periodic disk health alert evaluation failed.");
                }

                await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Clean stop requested
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore shutdown races during disposal
        }

        _lifecycleLock.Dispose();
    }
}
