using System;

namespace IntelliMon.Core.Telemetry;

public readonly record struct ProcessTelemetrySnapshot(
    int ProcessId,
    int ParentProcessId,
    string ProcessName,
    long CreateTime,
    long UserTime,
    long KernelTime,
    ulong WorkingSetSize,
    ulong PrivatePageCount,
    long ReadTransferCount,
    long WriteTransferCount,
    DateTime TimestampUtc
);
