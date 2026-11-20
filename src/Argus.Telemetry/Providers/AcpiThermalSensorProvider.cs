using Argus.Core.Contracts.Telemetry;
using Argus.Telemetry.Native;

namespace Argus.Telemetry.Providers;

/// <summary>
/// Tier 1 (zero-driver) ACPI thermal zone reader using locale-independent Windows PDH
/// (\Thermal Zone Information(*)\High Precision Temperature and Temperature).
/// Surfaces genuine motherboard/ACPI zones transparently without fabricating CPU die values.
/// </summary>
public sealed class AcpiThermalSensorProvider : ISensorProvider, IDisposable
{
    private readonly PdhQuerySession? _pdh;
    private readonly IntPtr _highPrecisionTempCounter;
    private readonly IntPtr _tempCounter;
    private bool _disposed;

    public string ProviderId => "argus.telemetry.acpi_thermal";
    public string DisplayName => "ACPI Thermal Zones (Windows PDH)";
    public SensorTier Tier => SensorTier.NativeZeroDriver;
    public bool IsAvailable => !_disposed && _pdh is not null && (_highPrecisionTempCounter != IntPtr.Zero || _tempCounter != IntPtr.Zero);

    public AcpiThermalSensorProvider()
    {
        _pdh = PdhQuerySession.TryCreate();
        if (_pdh is not null)
        {
            _highPrecisionTempCounter = _pdh.TryAddEnglishCounter(@"\Thermal Zone Information(*)\High Precision Temperature");
            _tempCounter = _pdh.TryAddEnglishCounter(@"\Thermal Zone Information(*)\Temperature");
            if (_highPrecisionTempCounter != IntPtr.Zero || _tempCounter != IntPtr.Zero)
            {
                _ = _pdh.Collect();
            }
        }
    }

    public Task<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || _pdh is null || !_pdh.Collect())
        {
            return Task.FromResult<IReadOnlyList<SensorReading>>([]);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var results = new List<SensorReading>();

        var highPrec = _highPrecisionTempCounter != IntPtr.Zero
            ? _pdh.GetDoubleArray(_highPrecisionTempCounter, allowOver100: true)
            : [];

        if (highPrec.Count > 0)
        {
            foreach (var kv in highPrec)
            {
                if (TryConvertDeciKelvinToCelsius(kv.Value, out double tempC))
                {
                    results.Add(CreateReading(kv.Key, tempC, now));
                }
            }
        }
        else if (_tempCounter != IntPtr.Zero)
        {
            var raw = _pdh.GetDoubleArray(_tempCounter, allowOver100: true);
            foreach (var kv in raw)
            {
                if (TryConvertKelvinToCelsius(kv.Value, out double tempC))
                {
                    results.Add(CreateReading(kv.Key, tempC, now));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<SensorReading>>(results);
    }

    internal static bool TryConvertDeciKelvinToCelsius(double deciKelvin, out double celsius)
    {
        double c = Math.Round((deciKelvin / 10.0) - 273.15, 1);
        if (c is >= 5.0 and <= 125.0)
        {
            celsius = c;
            return true;
        }

        celsius = 0;
        return false;
    }

    internal static bool TryConvertKelvinToCelsius(double kelvin, out double celsius)
    {
        double c = Math.Round(kelvin - 273.15, 1);
        if (c is >= 5.0 and <= 125.0)
        {
            celsius = c;
            return true;
        }

        celsius = 0;
        return false;
    }

    private static SensorReading CreateReading(string instanceName, double celsius, DateTimeOffset timestamp)
    {
        string cleanName = instanceName.Trim();
        return new SensorReading(
            SensorId: $"acpi.thermal.{cleanName}",
            Name: $"ACPI зона {cleanName}",
            Component: "Системна плата (ACPI)",
            Kind: SensorKind.TemperatureCelsius,
            Value: celsius,
            Unit: "°C",
            Tier: SensorTier.NativeZeroDriver,
            Timestamp: timestamp);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pdh?.Dispose();
    }
}
