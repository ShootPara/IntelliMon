using System;
using IntelliMon.Core.Native;

namespace IntelliMon.Core.Telemetry;

public sealed class DiskPerformanceMonitor : IDisposable
{
    private const string TotalDiskTimeCounterPath = @"\PhysicalDisk(_Total)\% Disk Time";

    private IntPtr _queryHandle = IntPtr.Zero;
    private IntPtr _counterHandle = IntPtr.Zero;
    private bool _initialized;
    private bool _disposed;

    public DiskPerformanceMonitor()
    {
        Initialize();
    }

    private void Initialize()
    {
        int status = Pdh.PdhOpenQuery(null, IntPtr.Zero, out _queryHandle);
        if (status != Pdh.ERROR_SUCCESS)
        {
            _queryHandle = IntPtr.Zero;
            return;
        }

        status = Pdh.PdhAddEnglishCounterW(_queryHandle, TotalDiskTimeCounterPath, IntPtr.Zero, out _counterHandle);
        if (status != Pdh.ERROR_SUCCESS)
        {
            Pdh.PdhCloseQuery(_queryHandle);
            _queryHandle = IntPtr.Zero;
            _counterHandle = IntPtr.Zero;
            return;
        }

        // Prime the counter (PDH rate/percentage counters need an initial sample)
        Pdh.PdhCollectQueryData(_queryHandle);
        _initialized = true;
    }

    /// <summary>
    /// Collects query data and returns the current system-wide disk active time percentage (0.0 to 100.0).
    /// </summary>
    public double GetSystemDiskActiveTimePercent()
    {
        if (_disposed || !_initialized || _queryHandle == IntPtr.Zero || _counterHandle == IntPtr.Zero)
        {
            return 0.0;
        }

        int status = Pdh.PdhCollectQueryData(_queryHandle);
        if (status != Pdh.ERROR_SUCCESS)
        {
            return 0.0;
        }

        status = Pdh.PdhGetFormattedCounterValue(_counterHandle, Pdh.PDH_FMT_DOUBLE, out _, out var counterVal);
        if (status != Pdh.ERROR_SUCCESS || counterVal.CStatus != Pdh.ERROR_SUCCESS)
        {
            return 0.0;
        }

        double val = counterVal.doubleValue;
        if (!double.IsFinite(val) || val < 0.0)
        {
            return 0.0;
        }

        return Math.Clamp(val, 0.0, 100.0);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_queryHandle != IntPtr.Zero)
            {
                Pdh.PdhCloseQuery(_queryHandle);
                _queryHandle = IntPtr.Zero;
                _counterHandle = IntPtr.Zero;
            }
        }
    }
}
