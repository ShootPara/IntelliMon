using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using IntelliMon.Core.Native;

namespace IntelliMon.Core.Metadata;

public enum SignatureVerificationStatus
{
    SignedAndVerified,
    SignedUntrusted,
    Unsigned,
    Inaccessible
}

public sealed record AuthenticodeInfo(
    SignatureVerificationStatus Status,
    string? SignerName
);

internal readonly record struct FileSignatureCacheKey(
    string NormalizedPath,
    long FileSize,
    DateTime LastWriteTimeUtc
);

public sealed class AuthenticodeVerifier
{
    private readonly ConcurrentDictionary<FileSignatureCacheKey, AuthenticodeInfo> _cache = new();

    public AuthenticodeInfo Verify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new AuthenticodeInfo(SignatureVerificationStatus.Inaccessible, null);
        }

        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                return new AuthenticodeInfo(SignatureVerificationStatus.Inaccessible, null);
            }
        }
        catch
        {
            return new AuthenticodeInfo(SignatureVerificationStatus.Inaccessible, null);
        }

        var key = new FileSignatureCacheKey(
            NormalizedPath: Path.GetFullPath(filePath).ToLowerInvariant(),
            FileSize: fileInfo.Length,
            LastWriteTimeUtc: fileInfo.LastWriteTimeUtc
        );

        return _cache.GetOrAdd(key, _ => PerformVerification(filePath));
    }

    private static AuthenticodeInfo PerformVerification(string filePath)
    {
        int trustResult = VerifyTrustInternal(filePath);

        // Attempt certificate extraction to obtain publisher / subject name
        string? signerName = ExtractSignerName(filePath);

        if (trustResult == Wintrust.ERROR_SUCCESS)
        {
            return new AuthenticodeInfo(
                Status: SignatureVerificationStatus.SignedAndVerified,
                SignerName: signerName ?? "Verified Signer"
            );
        }

        if (trustResult == Wintrust.TRUST_E_NOSIGNATURE)
        {
            // No embedded signature
            return new AuthenticodeInfo(
                Status: SignatureVerificationStatus.Unsigned,
                SignerName: null
            );
        }

        // Signed, but trust validation failed (untrusted root, expired, or catalog/policy issue)
        if (!string.IsNullOrEmpty(signerName))
        {
            return new AuthenticodeInfo(
                Status: SignatureVerificationStatus.SignedUntrusted,
                SignerName: signerName
            );
        }

        return new AuthenticodeInfo(
            Status: SignatureVerificationStatus.Unsigned,
            SignerName: null
        );
    }

    private static int VerifyTrustInternal(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        IntPtr pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFileInfo, false);
            var trustData = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = Wintrust.WTD_UI_NONE,
                fdwRevocationChecks = Wintrust.WTD_REVOKE_NONE,
                dwUnionChoice = Wintrust.WTD_CHOICE_FILE,
                pFile = pFileInfo,
                dwStateAction = Wintrust.WTD_STATEACTION_IGNORE,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = null,
                dwProvFlags = Wintrust.WTD_REVOCATION_CHECK_NONE | Wintrust.WTD_CACHE_ONLY_URL_RETRIEVAL,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero
            };

            return Wintrust.WinVerifyTrust(
                Wintrust.INVALID_HANDLE_VALUE,
                Wintrust.WINTRUST_ACTION_GENERIC_VERIFY_V2,
                ref trustData);
        }
        catch
        {
            return Wintrust.TRUST_E_NOSIGNATURE;
        }
        finally
        {
            Marshal.FreeHGlobal(pFileInfo);
        }
    }

    private static string? ExtractSignerName(string filePath)
    {
        try
        {
            using var rawCert = X509Certificate.CreateFromSignedFile(filePath);
            using var cert2 = new X509Certificate2(rawCert);
            string? name = cert2.GetNameInfo(X509NameType.SimpleName, false);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }

            return cert2.Subject;
        }
        catch
        {
            return null;
        }
    }

    public void ClearCache() => _cache.Clear();

    public int CacheCount => _cache.Count;
}
