using System;
using System.Runtime.InteropServices;

namespace IntelliMon.Core.Native;

internal static class Pdh
{
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const int ERROR_SUCCESS = 0;

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int PdhOpenQuery(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll", SetLastError = true)]
    public static extern int PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll", SetLastError = true)]
    public static extern int PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll", SetLastError = true)]
    public static extern int PdhCloseQuery(IntPtr hQuery);
}

[StructLayout(LayoutKind.Explicit)]
internal struct PDH_FMT_COUNTERVALUE
{
    [FieldOffset(0)]
    public uint CStatus;

    [FieldOffset(8)]
    public long longValue;

    [FieldOffset(8)]
    public double doubleValue;

    [FieldOffset(8)]
    public IntPtr ptrValue;
}
