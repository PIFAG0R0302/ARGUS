using System.Globalization;
using System.Runtime.InteropServices;

namespace Argus.App.Services;

public interface IGpuProcessMetricsReader : IDisposable
{
    IReadOnlyDictionary<int, double> SamplePerProcessGpuUtilization();
}

/// <summary>
/// Lightweight, locale-independent Windows PDH reader for per-process GPU engine utilization
/// (<c>\GPU Engine(*)\Utilization Percentage</c>). Parses <c>pid_&lt;N&gt;_</c> tokens from
/// instance names and degrades gracefully to an empty dictionary if counters are unavailable.
/// </summary>
public sealed partial class GpuProcessMetricsReader : IGpuProcessMetricsReader
{
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;

    private readonly Lock _sync = new();
    private IntPtr _queryHandle;
    private IntPtr _gpuEngineCounter;
    private bool _initialized;
    private bool _primed;
    private bool _disposed;

    public IReadOnlyDictionary<int, double> SamplePerProcessGpuUtilization()
    {
        if (!OperatingSystem.IsWindows())
        {
            return EmptyResult;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return EmptyResult;
            }

            try
            {
                EnsureInitialized();
                if (_queryHandle == IntPtr.Zero || _gpuEngineCounter == IntPtr.Zero)
                {
                    return EmptyResult;
                }

                if (!_primed)
                {
                    if (PdhCollectQueryData(_queryHandle) != ErrorSuccess)
                    {
                        return EmptyResult;
                    }

                    _primed = true;
                    Thread.Sleep(120);
                }

                if (PdhCollectQueryData(_queryHandle) != ErrorSuccess)
                {
                    return EmptyResult;
                }

                var rawSamples = ReadCounterArray(_gpuEngineCounter);
                return AggregatePerPidUtilization(rawSamples);
            }
            catch
            {
                return EmptyResult;
            }
        }
    }

    public static bool TryParsePidFromInstanceName(string? instanceName, out int pid)
    {
        pid = 0;
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return false;
        }

        int markerIndex = instanceName.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        int digitsStart = markerIndex + 4;
        if (digitsStart >= instanceName.Length)
        {
            return false;
        }

        int digitsEnd = digitsStart;
        while (digitsEnd < instanceName.Length && char.IsAsciiDigit(instanceName[digitsEnd]))
        {
            digitsEnd++;
        }

        if (digitsEnd == digitsStart)
        {
            return false;
        }

        return int.TryParse(
            instanceName.AsSpan(digitsStart, digitsEnd - digitsStart),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out pid) && pid > 0;
    }

    public static IReadOnlyDictionary<int, double> AggregatePerPidUtilization(
        IReadOnlyList<KeyValuePair<string, double>> engineSamples)
    {
        if (engineSamples.Count == 0)
        {
            return EmptyResult;
        }

        var totals = new Dictionary<int, double>();
        foreach (var sample in engineSamples)
        {
            if (double.IsNaN(sample.Value) || double.IsInfinity(sample.Value) || sample.Value <= 0.0)
            {
                continue;
            }

            if (!TryParsePidFromInstanceName(sample.Key, out int pid))
            {
                continue;
            }

            totals.TryGetValue(pid, out double current);
            totals[pid] = Math.Clamp(current + sample.Value, 0.0, 100.0);
        }

        return totals;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        uint openStatus = PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query);
        if (openStatus != ErrorSuccess || query == IntPtr.Zero)
        {
            return;
        }

        uint addStatus = PdhAddEnglishCounterW(
            query,
            @"\GPU Engine(*)\Utilization Percentage",
            IntPtr.Zero,
            out IntPtr counter);

        if (addStatus != ErrorSuccess || counter == IntPtr.Zero)
        {
            _ = PdhCloseQuery(query);
            return;
        }

        _queryHandle = query;
        _gpuEngineCounter = counter;
    }

    private static unsafe List<KeyValuePair<string, double>> ReadCounterArray(IntPtr counterHandle)
    {
        var samples = new List<KeyValuePair<string, double>>();
        uint bufferSize = 0;
        uint itemCount = 0;
        uint status = PdhGetFormattedCounterArrayW(
            counterHandle,
            PdhFmtDouble,
            ref bufferSize,
            ref itemCount,
            IntPtr.Zero);

        if (status != PdhMoreData || bufferSize == 0 || itemCount == 0)
        {
            return samples;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhGetFormattedCounterArrayW(
                counterHandle,
                PdhFmtDouble,
                ref bufferSize,
                ref itemCount,
                buffer);

            if (status != ErrorSuccess)
            {
                return samples;
            }

            var items = (PdhFmtCounterValueItemW*)buffer;
            for (uint i = 0; i < itemCount; i++)
            {
                if (items[i].FmtValue.CStatus != ErrorSuccess)
                {
                    continue;
                }

                string? name = Marshal.PtrToStringUni(items[i].SzName);
                double value = items[i].FmtValue.DoubleValue;
                if (string.IsNullOrWhiteSpace(name) || double.IsNaN(value) || double.IsInfinity(value))
                {
                    continue;
                }

                samples.Add(new KeyValuePair<string, double>(name, value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return samples;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _gpuEngineCounter = IntPtr.Zero;
            if (_queryHandle != IntPtr.Zero)
            {
                _ = PdhCloseQuery(_queryHandle);
                _queryHandle = IntPtr.Zero;
            }
        }
    }

    private static readonly IReadOnlyDictionary<int, double> EmptyResult =
        new Dictionary<int, double>();

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
    private static partial uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr itemBuffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(IntPtr hQuery);
}
