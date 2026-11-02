using System;
using System.Runtime.InteropServices;
using IntelliMon.Core.Native;

namespace IntelliMon.Core.Metadata;

public static class ProcessCommandLineResolver
{
    /// <summary>
    /// Obtains the full command line for the given process ID using NtQueryInformationProcess(ProcessCommandLineInformation).
    /// Returns null if access is denied, the process has terminated, or command line is unavailable.
    /// </summary>
    public static string? GetCommandLine(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        IntPtr hProcess = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            uint returnLength;
            uint status = NtDll.NtQueryInformationProcess(
                hProcess,
                NtDll.ProcessCommandLineInformation,
                IntPtr.Zero,
                0,
                out returnLength);

            if (returnLength == 0)
            {
                return null;
            }

            IntPtr pBuffer = Marshal.AllocHGlobal((int)returnLength);
            try
            {
                status = NtDll.NtQueryInformationProcess(
                    hProcess,
                    NtDll.ProcessCommandLineInformation,
                    pBuffer,
                    returnLength,
                    out returnLength);

                if (status != NtDll.STATUS_SUCCESS)
                {
                    return null;
                }

                var uString = Marshal.PtrToStructure<UNICODE_STRING>(pBuffer);
                if (uString.Buffer == IntPtr.Zero || uString.Length == 0)
                {
                    return null;
                }

                string cmdLine = Marshal.PtrToStringUni(uString.Buffer, uString.Length / 2) ?? string.Empty;
                string trimmed = cmdLine.Trim();
                return trimmed.Length > 0 ? trimmed : null;
            }
            finally
            {
                Marshal.FreeHGlobal(pBuffer);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Kernel32.CloseHandle(hProcess);
        }
    }
}
