using System;
using System.Text;
using IntelliMon.Core.Native;

namespace IntelliMon.Core.Metadata;

public static class ProcessPathResolver
{
    /// <summary>
    /// Obtains the full executable path for the given process ID using QueryFullProcessImageNameW.
    /// Returns null if the process is inaccessible, protected, or has terminated.
    /// </summary>
    public static string? GetExecutablePath(int processId)
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
            var sb = new StringBuilder(1024);
            int capacity = sb.Capacity;
            if (Kernel32.QueryFullProcessImageNameW(hProcess, 0, sb, ref capacity))
            {
                return sb.ToString();
            }

            return null;
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
