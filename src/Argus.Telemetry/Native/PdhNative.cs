using System.Runtime.InteropServices;

namespace Argus.Telemetry.Native;

/// <summary>
/// Locale-independent Windows Performance Data Helper (PDH) wrapper using PdhAddEnglishCounterW.
/// Works identically across all Windows 10/11 display languages with zero third-party drivers.
/// </summary>
internal sealed partial class PdhQuerySession : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;

    private IntPtr _queryHandle;
    private readonly List<IntPtr> _counters = [];
    private bool _disposed;

    private PdhQuerySession(IntPtr queryHandle)
    {
        _queryHandle = queryHandle;
    }

    public static PdhQuerySession? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        uint status = PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query);
        return status == ErrorSuccess && query != IntPtr.Zero
            ? new PdhQuerySession(query)
            : null;
    }

    public IntPtr TryAddEnglishCounter(string counterPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(counterPath))
        {
            return IntPtr.Zero;
        }

        uint status = PdhAddEnglishCounterW(_queryHandle, counterPath, IntPtr.Zero, out IntPtr counter);
        if (status != ErrorSuccess || counter == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        _counters.Add(counter);
        return counter;
    }

    public bool Collect()
    {
        if (_disposed || _queryHandle == IntPtr.Zero)
        {
            return false;
        }

        return PdhCollectQueryData(_queryHandle) == ErrorSuccess;
    }

    public bool TryGetDouble(IntPtr counterHandle, out double value, bool allowOver100 = true)
    {
        value = 0;
        if (_disposed || counterHandle == IntPtr.Zero)
        {
            return false;
        }

        uint format = allowOver100 ? (PdhFmtDouble | PdhFmtNoCap100) : PdhFmtDouble;
        uint status = PdhGetFormattedCounterValue(counterHandle, format, out _, out var item);
        if (status != ErrorSuccess || item.CStatus != ErrorSuccess || double.IsNaN(item.DoubleValue) || double.IsInfinity(item.DoubleValue))
        {
            return false;
        }

        value = item.DoubleValue;
        return true;
    }

    public unsafe Dictionary<string, double> GetDoubleArray(IntPtr counterHandle, bool allowOver100 = true)
    {
        var results = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_disposed || counterHandle == IntPtr.Zero)
        {
            return results;
        }

        uint format = allowOver100 ? (PdhFmtDouble | PdhFmtNoCap100) : PdhFmtDouble;
        uint bufferSize = 0;
        uint itemCount = 0;
        uint status = PdhGetFormattedCounterArrayW(counterHandle, format, ref bufferSize, ref itemCount, IntPtr.Zero);
        if (status != PdhMoreData || bufferSize == 0 || itemCount == 0)
        {
            return results;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhGetFormattedCounterArrayW(counterHandle, format, ref bufferSize, ref itemCount, buffer);
            if (status != ErrorSuccess)
            {
                return results;
            }

            var items = (PdhFmtCounterValueItemW*)buffer;
            for (uint i = 0; i < itemCount; i++)
            {
                if (items[i].FmtValue.CStatus != ErrorSuccess)
                {
                    continue;
                }

                string? name = Marshal.PtrToStringUni(items[i].SzName);
                double val = items[i].FmtValue.DoubleValue;
                if (string.IsNullOrWhiteSpace(name) || double.IsNaN(val) || double.IsInfinity(val))
                {
                    continue;
                }

                results[name] = val;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return results;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _counters.Clear();
        if (_queryHandle != IntPtr.Zero)
        {
            _ = PdhCloseQuery(_queryHandle);
            _queryHandle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItemW
    {
        public IntPtr SzName;
        public PdhFmtCounterValue FmtValue;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(IntPtr hQuery);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PdhFmtCounterValue pValue);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr itemBuffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(IntPtr hQuery);
}
