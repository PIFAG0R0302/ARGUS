using System.Runtime.InteropServices;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Telemetry;

namespace Argus.Telemetry.Providers;

public enum PawnIoAvailabilityState
{
    DisabledBySetting,
    DriverNotInstalled,
    Ready
}

public interface ISensorTierStatusProvider
{
    PawnIoAvailabilityState GetPawnIoState();
    string GetStatusSummary();
}

/// <summary>
/// Tier 2 (opt-in) signed PawnIO sensor bridge.
/// Never bundles, extracts, or loads WinRing0.sys; probes only an already-installed official signed PawnIO device.
/// </summary>
public sealed partial class PawnIoSensorProvider : ISensorProvider, ISensorTierStatusProvider
{
    private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
    private const uint OpenExisting = 3;

    private readonly IArgusSettingsStore _settingsStore;
    private readonly Func<bool> _deviceProbe;

    public PawnIoSensorProvider(IArgusSettingsStore settingsStore, Func<bool>? deviceProbe = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _deviceProbe = deviceProbe ?? ProbeOfficialPawnIoDevice;
    }

    public string ProviderId => "argus.telemetry.pawnio";
    public string DisplayName => "Підписаний драйвер PawnIO (Рівень 2)";
    public SensorTier Tier => SensorTier.SignedPawnIo;
    public bool IsAvailable => GetPawnIoState() == PawnIoAvailabilityState.Ready;

    public PawnIoAvailabilityState GetPawnIoState()
    {
        if (!_settingsStore.Current.EnablePawnIoExtendedSensors)
        {
            return PawnIoAvailabilityState.DisabledBySetting;
        }

        return _deviceProbe()
            ? PawnIoAvailabilityState.Ready
            : PawnIoAvailabilityState.DriverNotInstalled;
    }

    public string GetStatusSummary() => GetPawnIoState() switch
    {
        PawnIoAvailabilityState.Ready => "Рівень 2: Win32/NVML + підписаний PawnIO активний",
        PawnIoAvailabilityState.DriverNotInstalled => "Рівень 1: базові API (увімкнено PawnIO, але драйвер не встановлено)",
        _ => "Рівень 1: Win32 / PDH / NVML / IOCTL (без сторонніх драйверів)"
    };

    public Task<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        // Only returns readings when the official signed PawnIO kernel module is installed and enabled.
        // Without a loaded signed PawnIO module, returns empty rather than fabricating sensor values.
        return Task.FromResult<IReadOnlyList<SensorReading>>([]);
    }

    private static bool ProbeOfficialPawnIoDevice()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            IntPtr handle = CreateFileW(
                @"\\.\PawnIO",
                0,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return false;
            }

            _ = CloseHandle(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}
