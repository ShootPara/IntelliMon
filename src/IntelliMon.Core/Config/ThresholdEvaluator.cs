using System;
using IntelliMon.Core.Telemetry;

namespace IntelliMon.Core.Config;

/// <summary>
/// Evaluates whether a process resource sample crosses any configured threshold.
/// </summary>
public sealed class ThresholdEvaluator
{
    private readonly AppSettings _settings;

    public ThresholdEvaluator(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Returns true if the sample exceeds the CPU, memory, or disk threshold.
    /// System Idle Process (PID 0) and System (PID 4) are excluded.
    /// </summary>
    public bool IsQualifying(ProcessResourceSample sample)
    {
        // Exclude kernel and idle pseudo-processes
        if (sample.ProcessId <= 4) return false;

        return sample.CpuPercent >= _settings.CpuThresholdPercent
            || sample.MemoryWorkingSetPercent >= _settings.MemoryThresholdPercent
            || sample.DiskPercent >= _settings.DiskThresholdPercent;
    }

    /// <summary>
    /// Returns a human-readable description of which thresholds this sample exceeds.
    /// </summary>
    public string GetTriggerReason(ProcessResourceSample sample)
    {
        var parts = new System.Collections.Generic.List<string>(3);

        if (sample.CpuPercent >= _settings.CpuThresholdPercent)
            parts.Add($"CPU {sample.CpuPercent:F1}%");
        if (sample.MemoryWorkingSetPercent >= _settings.MemoryThresholdPercent)
            parts.Add($"RAM {sample.MemoryWorkingSetPercent:F1}%");
        if (sample.DiskPercent >= _settings.DiskThresholdPercent)
            parts.Add($"Disk {sample.DiskPercent:F1}%");

        return parts.Count > 0 ? string.Join(" | ", parts) : "None";
    }
}
