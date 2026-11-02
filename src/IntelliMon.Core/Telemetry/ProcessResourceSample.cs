namespace IntelliMon.Core.Telemetry;

public sealed record ProcessResourceSample(
    int ProcessId,
    int ParentProcessId,
    string ProcessName,
    double CpuPercent,
    double MemoryWorkingSetPercent,
    ulong WorkingSetBytes,
    double DiskIoBytesPerSec,
    double DiskPercent
)
{
    public override string ToString() =>
        $"[PID {ProcessId,6}] {ProcessName,-25} | CPU: {CpuPercent,5:F1}% | RAM: {MemoryWorkingSetPercent,5:F1}% ({WorkingSetBytes / (1024.0 * 1024.0),7:F1} MB) | Disk: {DiskPercent,5:F1}% ({DiskIoBytesPerSec / (1024.0 * 1024.0),6:F2} MB/s)";
}
