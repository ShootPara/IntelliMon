using System;
using System.Linq;
using System.Threading;
using IntelliMon.Core.Telemetry;
using Xunit;

namespace IntelliMon.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void SystemMemoryMonitor_ReturnsPlausiblePhysicalMemory()
    {
        var monitor = new SystemMemoryMonitor();
        Assert.True(monitor.TotalPhysicalBytes > 1024UL * 1024 * 1024, "Total physical RAM should be greater than 1 GB");
        Assert.True(monitor.TotalPhysicalGigabytes > 1.0, "Total physical GB should be greater than 1.0");

        var status = monitor.GetCurrentMemoryStatus();
        Assert.Equal(monitor.TotalPhysicalBytes, status.TotalBytes);
        Assert.True(status.AvailableBytes > 0, "Available physical RAM should be greater than 0");
        Assert.InRange(status.MemoryLoadPercent, 0.0, 100.0);
    }

    [Fact]
    public void DiskPerformanceMonitor_ReturnsPlausibleActiveTime()
    {
        using var monitor = new DiskPerformanceMonitor();
        // Give PDH counter a moment
        Thread.Sleep(200);
        double activeTime = monitor.GetSystemDiskActiveTimePercent();

        Assert.True(double.IsFinite(activeTime), "Disk active time must be a finite number");
        Assert.InRange(activeTime, 0.0, 100.0);
    }

    [Fact]
    public void ProcessTelemetryEngine_SamplesProcessesAccuratelyWithoutCrash()
    {
        using var engine = new ProcessTelemetryEngine();

        // Sample 1: Baseline
        var initial = engine.SampleProcesses(out double diskActive1, out double totalIo1);
        Assert.NotEmpty(initial);
        Assert.True(double.IsFinite(diskActive1));
        Assert.InRange(diskActive1, 0.0, 100.0);
        Assert.True(totalIo1 >= 0.0);

        // Sleep to establish meaningful delta
        Thread.Sleep(500);

        // Sample 2: Deltas
        var deltaSamples = engine.SampleProcesses(out double diskActive2, out double totalIo2);
        Assert.NotEmpty(deltaSamples);
        Assert.True(double.IsFinite(diskActive2));
        Assert.InRange(diskActive2, 0.0, 100.0);
        Assert.True(totalIo2 >= 0.0);

        foreach (var sample in deltaSamples)
        {
            Assert.True(sample.ProcessId >= 0, "PID must be non-negative");
            Assert.False(string.IsNullOrWhiteSpace(sample.ProcessName), "Process name must not be empty");

            Assert.True(double.IsFinite(sample.CpuPercent), $"CPU % must be finite for PID {sample.ProcessId}");
            Assert.InRange(sample.CpuPercent, 0.0, 100.0);

            Assert.True(double.IsFinite(sample.MemoryWorkingSetPercent), $"Memory % must be finite for PID {sample.ProcessId}");
            Assert.InRange(sample.MemoryWorkingSetPercent, 0.0, 100.0);

            Assert.True(double.IsFinite(sample.DiskPercent), $"Disk % must be finite for PID {sample.ProcessId}");
            Assert.InRange(sample.DiskPercent, 0.0, 100.0);

            Assert.True(double.IsFinite(sample.DiskIoBytesPerSec), $"Disk bytes/sec must be finite for PID {sample.ProcessId}");
            Assert.True(sample.DiskIoBytesPerSec >= 0.0, $"Disk bytes/sec must be non-negative for PID {sample.ProcessId}");
        }

        // Verify known system processes are present in the snapshot
        var pids = deltaSamples.Select(s => s.ProcessId).ToHashSet();
        Assert.Contains(0, pids); // System Idle Process
        Assert.Contains(4, pids); // System
    }

    [Fact]
    public void ProcessTelemetryEngine_HandlesRapidConsecutiveSamplesWithoutError()
    {
        using var engine = new ProcessTelemetryEngine();

        for (int i = 0; i < 5; i++)
        {
            var samples = engine.SampleProcesses(out double active, out double totalIo);
            Assert.NotEmpty(samples);
            Assert.InRange(active, 0.0, 100.0);
            Assert.True(totalIo >= 0.0);
            Thread.Sleep(50);
        }
    }
}
