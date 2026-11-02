using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using IntelliMon.Core.Native;

namespace IntelliMon.Core.Metadata;

public sealed record HostedServiceInfo(
    string ServiceName,
    string DisplayName
)
{
    public override string ToString() => $"{ServiceName} ({DisplayName})";
}

public sealed class ServiceResolver
{
    private readonly object _lock = new();
    private Dictionary<int, List<HostedServiceInfo>> _cachedMap = new();
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(5);

    public IReadOnlyList<HostedServiceInfo> GetServicesForProcess(int processId)
    {
        if (processId <= 0)
        {
            return Array.Empty<HostedServiceInfo>();
        }

        lock (_lock)
        {
            if (DateTime.UtcNow - _lastRefreshUtc > _cacheTtl)
            {
                RefreshCacheInternal();
            }

            if (_cachedMap.TryGetValue(processId, out var list))
            {
                return list;
            }

            return Array.Empty<HostedServiceInfo>();
        }
    }

    public void Refresh()
    {
        lock (_lock)
        {
            RefreshCacheInternal();
        }
    }

    private void RefreshCacheInternal()
    {
        var map = new Dictionary<int, List<HostedServiceInfo>>(64);

        IntPtr hSCM = Advapi32.OpenSCManager(null, null, Advapi32.SC_MANAGER_ENUMERATE_SERVICE);
        if (hSCM == IntPtr.Zero)
        {
            _cachedMap = map;
            _lastRefreshUtc = DateTime.UtcNow;
            return;
        }

        try
        {
            uint bytesNeeded = 0;
            uint servicesReturned = 0;
            uint resumeHandle = 0;

            // First call to determine buffer size
            Advapi32.EnumServicesStatusEx(
                hSCM,
                Advapi32.SC_ENUM_PROCESS_INFO,
                Advapi32.SERVICE_WIN32,
                Advapi32.SERVICE_STATE_ALL,
                IntPtr.Zero,
                0,
                out bytesNeeded,
                out servicesReturned,
                ref resumeHandle,
                null);

            if (bytesNeeded == 0)
            {
                _cachedMap = map;
                _lastRefreshUtc = DateTime.UtcNow;
                return;
            }

            IntPtr pBuffer = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                resumeHandle = 0;
                bool success = Advapi32.EnumServicesStatusEx(
                    hSCM,
                    Advapi32.SC_ENUM_PROCESS_INFO,
                    Advapi32.SERVICE_WIN32,
                    Advapi32.SERVICE_STATE_ALL,
                    pBuffer,
                    bytesNeeded,
                    out bytesNeeded,
                    out servicesReturned,
                    ref resumeHandle,
                    null);

                if (!success || servicesReturned == 0)
                {
                    _cachedMap = map;
                    _lastRefreshUtc = DateTime.UtcNow;
                    return;
                }

                int structSize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
                for (int i = 0; i < servicesReturned; i++)
                {
                    IntPtr itemPtr = new IntPtr(pBuffer.ToInt64() + (i * structSize));
                    var item = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(itemPtr);

                    int pid = (int)item.ServiceStatusProcess.dwProcessId;
                    if (pid > 0)
                    {
                        if (!map.TryGetValue(pid, out var list))
                        {
                            list = new List<HostedServiceInfo>();
                            map[pid] = list;
                        }

                        list.Add(new HostedServiceInfo(
                            ServiceName: item.lpServiceName ?? "Unknown",
                            DisplayName: item.lpDisplayName ?? item.lpServiceName ?? "Unknown"
                        ));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pBuffer);
            }
        }
        catch
        {
            // Gracefully ignore service query errors
        }
        finally
        {
            Advapi32.CloseServiceHandle(hSCM);
        }

        _cachedMap = map;
        _lastRefreshUtc = DateTime.UtcNow;
    }
}
