using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using IntelliMon.Core.Native;

namespace IntelliMon.Core.Telemetry;

public sealed class ProcessTelemetryEngine : IDisposable
{
    private readonly SystemMemoryMonitor _memoryMonitor;
    private readonly DiskPerformanceMonitor _diskMonitor;
    private readonly int _processorCount;
    private readonly ulong _totalPhysicalMemory;

    private readonly Dictionary<int, ProcessTelemetrySnapshot> _previousSnapshots = new(512);

    private IntPtr _unmanagedBuffer = IntPtr.Zero;
    private uint _bufferSize = 512 * 1024; // Start at 512 KB
    private bool _disposed;

    public ProcessTelemetryEngine(SystemMemoryMonitor? memoryMonitor = null, DiskPerformanceMonitor? diskMonitor = null)
    {
        _memoryMonitor = memoryMonitor ?? new SystemMemoryMonitor();
        _diskMonitor = diskMonitor ?? new DiskPerformanceMonitor();
        _processorCount = Environment.ProcessorCount;
        _totalPhysicalMemory = _memoryMonitor.TotalPhysicalBytes;

        _unmanagedBuffer = Marshal.AllocHGlobal((int)_bufferSize);
    }

    public SystemMemoryMonitor MemoryMonitor => _memoryMonitor;
    public DiskPerformanceMonitor DiskMonitor => _diskMonitor;
    public ulong TotalPhysicalMemory => _totalPhysicalMemory;

    /// <summary>
    /// Returns the snapshot map from the most recent telemetry sample, useful for ancestry resolution.
    /// </summary>
    public IReadOnlyDictionary<int, ProcessTelemetrySnapshot> GetLatestSnapshots() => _previousSnapshots;

    /// <summary>
    /// Collects a telemetry sample for all running processes and computes CPU %, Working Set %, and Disk %.
    /// </summary>
    public IReadOnlyList<ProcessResourceSample> SampleProcesses(out double systemDiskActiveTimePercent, out double totalDiskBytesPerSec)
    {
        systemDiskActiveTimePercent = _diskMonitor.GetSystemDiskActiveTimePercent();

        var currentSnapshots = CaptureProcessSnapshots();
        var now = DateTime.UtcNow;

        var rawSamples = new List<(ProcessTelemetrySnapshot Snapshot, double CpuPercent, double DiskBytesPerSec, double MemoryPercent)>(currentSnapshots.Count);
        double sumDiskBytesPerSec = 0.0;

        foreach (var current in currentSnapshots)
        {
            double cpuPercent = 0.0;
            double diskBytesPerSec = 0.0;

            if (_previousSnapshots.TryGetValue(current.ProcessId, out var prev) && prev.CreateTime == current.CreateTime)
            {
                double deltaTimeSec = (current.TimestampUtc - prev.TimestampUtc).TotalSeconds;
                if (deltaTimeSec > 0.001)
                {
                    long deltaCpuTicks = (current.UserTime - prev.UserTime) + (current.KernelTime - prev.KernelTime);
                    if (deltaCpuTicks > 0)
                    {
                        double capacity = deltaTimeSec * _processorCount * 10_000_000.0;
                        cpuPercent = (deltaCpuTicks / capacity) * 100.0;
                    }

                    long deltaRead = Math.Max(0, current.ReadTransferCount - prev.ReadTransferCount);
                    long deltaWrite = Math.Max(0, current.WriteTransferCount - prev.WriteTransferCount);
                    diskBytesPerSec = (deltaRead + deltaWrite) / deltaTimeSec;
                }
            }

            if (!double.IsFinite(cpuPercent) || cpuPercent < 0.0) cpuPercent = 0.0;
            cpuPercent = Math.Clamp(cpuPercent, 0.0, 100.0);

            if (!double.IsFinite(diskBytesPerSec) || diskBytesPerSec < 0.0) diskBytesPerSec = 0.0;

            double memoryPercent = _totalPhysicalMemory > 0
                ? ((double)current.WorkingSetSize / _totalPhysicalMemory) * 100.0
                : 0.0;

            if (!double.IsFinite(memoryPercent) || memoryPercent < 0.0) memoryPercent = 0.0;
            memoryPercent = Math.Clamp(memoryPercent, 0.0, 100.0);

            sumDiskBytesPerSec += diskBytesPerSec;
            rawSamples.Add((current, cpuPercent, diskBytesPerSec, memoryPercent));
        }

        totalDiskBytesPerSec = sumDiskBytesPerSec;

        // Calculate attributed disk percentage for each process
        var results = new List<ProcessResourceSample>(rawSamples.Count);
        foreach (var item in rawSamples)
        {
            double processDiskPercent = 0.0;
            if (sumDiskBytesPerSec > 0.0 && systemDiskActiveTimePercent > 0.0 && item.DiskBytesPerSec > 0.0)
            {
                processDiskPercent = (item.DiskBytesPerSec / sumDiskBytesPerSec) * systemDiskActiveTimePercent;
            }

            if (!double.IsFinite(processDiskPercent) || processDiskPercent < 0.0) processDiskPercent = 0.0;
            processDiskPercent = Math.Clamp(processDiskPercent, 0.0, 100.0);

            results.Add(new ProcessResourceSample(
                ProcessId: item.Snapshot.ProcessId,
                ParentProcessId: item.Snapshot.ParentProcessId,
                ProcessName: item.Snapshot.ProcessName,
                CpuPercent: item.CpuPercent,
                MemoryWorkingSetPercent: item.MemoryPercent,
                WorkingSetBytes: item.Snapshot.WorkingSetSize,
                DiskIoBytesPerSec: item.DiskBytesPerSec,
                DiskPercent: processDiskPercent
            ));
        }

        // Update previous snapshots map and prune terminated PIDs
        _previousSnapshots.Clear();
        foreach (var snapshot in currentSnapshots)
        {
            _previousSnapshots[snapshot.ProcessId] = snapshot;
        }

        return results;
    }

    private unsafe List<ProcessTelemetrySnapshot> CaptureProcessSnapshots()
    {
        var list = new List<ProcessTelemetrySnapshot>(350);
        var timestamp = DateTime.UtcNow;

        while (true)
        {
            uint returnLength;
            uint status = NtDll.NtQuerySystemInformation(
                NtDll.SystemProcessInformation,
                (byte*)_unmanagedBuffer.ToPointer(),
                _bufferSize,
                out returnLength);

            if (status == NtDll.STATUS_INFO_LENGTH_MISMATCH)
            {
                // Buffer too small; allocate returnLength + 64KB safety margin
                _bufferSize = Math.Max(returnLength + (64 * 1024), _bufferSize * 2);
                Marshal.FreeHGlobal(_unmanagedBuffer);
                _unmanagedBuffer = Marshal.AllocHGlobal((int)_bufferSize);
                continue;
            }

            if (status != NtDll.STATUS_SUCCESS)
            {
                return list;
            }

            byte* pCurrent = (byte*)_unmanagedBuffer.ToPointer();
            while (true)
            {
                var pInfo = (SYSTEM_PROCESS_INFORMATION*)pCurrent;
                int pid = pInfo->UniqueProcessId.ToInt32();
                int ppid = pInfo->InheritedFromUniqueProcessId.ToInt32();

                string name;
                if (pInfo->ImageName.Buffer != IntPtr.Zero && pInfo->ImageName.Length > 0)
                {
                    name = Marshal.PtrToStringUni(pInfo->ImageName.Buffer, pInfo->ImageName.Length / 2) ?? "Unknown";
                }
                else if (pid == 0)
                {
                    name = "System Idle Process";
                }
                else if (pid == 4)
                {
                    name = "System";
                }
                else
                {
                    name = "Unknown";
                }

                list.Add(new ProcessTelemetrySnapshot(
                    ProcessId: pid,
                    ParentProcessId: ppid,
                    ProcessName: name,
                    CreateTime: pInfo->CreateTime,
                    UserTime: pInfo->UserTime,
                    KernelTime: pInfo->KernelTime,
                    WorkingSetSize: pInfo->WorkingSetSize.ToUInt64(),
                    PrivatePageCount: pInfo->PrivatePageCount.ToUInt64(),
                    ReadTransferCount: pInfo->ReadTransferCount,
                    WriteTransferCount: pInfo->WriteTransferCount,
                    TimestampUtc: timestamp
                ));

                if (pInfo->NextEntryOffset == 0)
                {
                    break;
                }

                pCurrent += pInfo->NextEntryOffset;
            }

            break;
        }

        return list;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _diskMonitor.Dispose();
            if (_unmanagedBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_unmanagedBuffer);
                _unmanagedBuffer = IntPtr.Zero;
            }
        }
    }
}
