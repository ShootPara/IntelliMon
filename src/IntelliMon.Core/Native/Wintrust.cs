using System;
using System.Runtime.InteropServices;

namespace IntelliMon.Core.Native;

internal static class Wintrust
{
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    public static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public const uint WTD_UI_NONE = 2;
    public const uint WTD_REVOKE_NONE = 0;
    public const uint WTD_CHOICE_FILE = 1;
    public const uint WTD_STATEACTION_IGNORE = 0;
    public const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    public const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    // Common WinVerifyTrust return codes
    public const int ERROR_SUCCESS = 0;
    public const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    public const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
    public const int TRUST_E_SUBJECT_NOT_TRUSTED = unchecked((int)0x800B0004);
    public const int CRYPT_E_SECURITY_SETTINGS = unchecked((int)0x80092026);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    public static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
        ref WINTRUST_DATA pWVTData);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WINTRUST_FILE_INFO
{
    public uint cbStruct;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string pcwszFilePath;
    public IntPtr hFile;
    public IntPtr pgKnownSubject;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WINTRUST_DATA
{
    public uint cbStruct;
    public IntPtr pPolicyCallbackData;
    public IntPtr pSIPClientData;
    public uint dwUIChoice;
    public uint fdwRevocationChecks;
    public uint dwUnionChoice;
    public IntPtr pFile;
    public uint dwStateAction;
    public IntPtr hWVTStateData;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? pwszURLReference;
    public uint dwProvFlags;
    public uint dwUIContext;
    public IntPtr pSignatureSettings;
}
