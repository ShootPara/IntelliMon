using System;
using System.Runtime.InteropServices;

namespace IntelliMon.Core.Native;

internal static unsafe class NtDll
{
    public const uint STATUS_SUCCESS = 0x00000000;
    public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    public const int SystemProcessInformation = 5;
    public const int ProcessCommandLineInformation = 60;

    [DllImport("ntdll.dll")]
    public static extern uint NtQuerySystemInformation(
        int systemInformationClass,
        byte* systemInformation,
        uint systemInformationLength,
        out uint returnLength);

    [DllImport("ntdll.dll")]
    public static extern uint NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        uint processInformationLength,
        out uint returnLength);
}

[StructLayout(LayoutKind.Sequential)]
internal struct UNICODE_STRING
{
    public ushort Length;
    public ushort MaximumLength;
    private uint _padding;
    public IntPtr Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_PROCESS_INFORMATION
{
    public uint NextEntryOffset;              // 0x00
    public uint NumberOfThreads;              // 0x04
    public long WorkingSetPrivateSize;        // 0x08
    public uint HardFaultCount;               // 0x10
    public uint NumberOfThreadsHighWatermark; // 0x14
    public ulong CycleTime;                   // 0x18
    public long CreateTime;                   // 0x20
    public long UserTime;                     // 0x28
    public long KernelTime;                   // 0x30
    public UNICODE_STRING ImageName;          // 0x38 (16 bytes)
    public int BasePriority;                  // 0x48
    private uint _padding1;                   // 0x4C
    public IntPtr UniqueProcessId;            // 0x50
    public IntPtr InheritedFromUniqueProcessId; // 0x58
    public uint HandleCount;                  // 0x60
    public uint SessionId;                    // 0x64
    public UIntPtr UniqueProcessKey;          // 0x68
    public UIntPtr PeakVirtualSize;           // 0x70
    public UIntPtr VirtualSize;               // 0x78
    public uint PageFaultCount;               // 0x80
    private uint _padding2;                   // 0x84
    public UIntPtr PeakWorkingSetSize;         // 0x88
    public UIntPtr WorkingSetSize;             // 0x90
    public UIntPtr QuotaPeakPagedPoolUsage;    // 0x98
    public UIntPtr QuotaPagedPoolUsage;        // 0xA0
    public UIntPtr QuotaPeakNonPagedPoolUsage; // 0xA8
    public UIntPtr QuotaNonPagedPoolUsage;     // 0xB0
    public UIntPtr PagefileUsage;              // 0xB8
    public UIntPtr PeakPagefileUsage;          // 0xC0
    public UIntPtr PrivatePageCount;           // 0xC8
    public long ReadOperationCount;           // 0xD0
    public long WriteOperationCount;          // 0xD8
    public long OtherOperationCount;          // 0xE0
    public long ReadTransferCount;            // 0xE8
    public long WriteTransferCount;           // 0xF0
    public long OtherTransferCount;           // 0xF8
}
