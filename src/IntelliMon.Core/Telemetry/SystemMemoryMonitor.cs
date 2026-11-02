using System;
using IntelliMon.Core.Native;

namespace IntelliMon.Core.Telemetry;

public sealed class SystemMemoryMonitor
{
    private readonly ulong _totalPhysicalBytes;

    public SystemMemoryMonitor()
    {
        var memStatus = MEMORYSTATUSEX.Create();
        if (Kernel32.GlobalMemoryStatusEx(ref memStatus))
        {
            _totalPhysicalBytes = memStatus.ullTotalPhys;
        }
        else
        {
            // Fallback: estimate 8GB if Win32 call fails unexpectedly
            _totalPhysicalBytes = 8UL * 1024 * 1024 * 1024;
        }
    }

    public ulong TotalPhysicalBytes => _totalPhysicalBytes;

    public double TotalPhysicalGigabytes => _totalPhysicalBytes / (1024.0 * 1024.0 * 1024.0);

    public (ulong TotalBytes, ulong AvailableBytes, double MemoryLoadPercent) GetCurrentMemoryStatus()
    {
        var memStatus = MEMORYSTATUSEX.Create();
        if (Kernel32.GlobalMemoryStatusEx(ref memStatus))
        {
            return (memStatus.ullTotalPhys, memStatus.ullAvailPhys, memStatus.dwMemoryLoad);
        }

        return (_totalPhysicalBytes, 0, 0);
    }
}
