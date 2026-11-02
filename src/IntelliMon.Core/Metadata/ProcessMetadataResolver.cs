using System;
using System.Collections.Generic;
using IntelliMon.Core.Telemetry;

namespace IntelliMon.Core.Metadata;

public sealed class ProcessMetadataResolver
{
    private readonly ServiceResolver _serviceResolver;
    private readonly AuthenticodeVerifier _authenticodeVerifier;

    public ProcessMetadataResolver(ServiceResolver? serviceResolver = null, AuthenticodeVerifier? authenticodeVerifier = null)
    {
        _serviceResolver = serviceResolver ?? new ServiceResolver();
        _authenticodeVerifier = authenticodeVerifier ?? new AuthenticodeVerifier();
    }

    public ServiceResolver ServiceResolver => _serviceResolver;
    public AuthenticodeVerifier AuthenticodeVerifier => _authenticodeVerifier;

    /// <summary>
    /// Gathers all local Windows metadata for the specified process.
    /// Gracefully degrades if certain fields are inaccessible or if the process terminates.
    /// </summary>
    public ProcessIdentityInfo Resolve(
        int processId,
        string processName,
        long createTime,
        IReadOnlyDictionary<int, ProcessTelemetrySnapshot>? snapshotMap = null)
    {
        // 1. Resolve executable path
        string? exePath = ProcessPathResolver.GetExecutablePath(processId);

        // 2. Resolve file version metadata if path is available
        var versionInfo = exePath != null ? FileVersionMetadataProvider.GetMetadata(exePath) : null;

        // 3. Resolve Authenticode signature (cached by file path + mtime + size)
        var authInfo = exePath != null
            ? _authenticodeVerifier.Verify(exePath)
            : new AuthenticodeInfo(SignatureVerificationStatus.Inaccessible, null);

        // 4. Resolve command line (best effort)
        string? cmdLine = ProcessCommandLineResolver.GetCommandLine(processId);

        // 5. Resolve hosted Windows services (e.g. for svchost.exe)
        var services = _serviceResolver.GetServicesForProcess(processId);

        // 6. Resolve process ancestry from coherent snapshot
        var ancestry = snapshotMap != null
            ? ProcessAncestryBuilder.BuildAncestry(processId, snapshotMap)
            : Array.Empty<ProcessAncestryNode>();

        return new ProcessIdentityInfo(
            ProcessId: processId,
            CreateTime: createTime,
            ProcessName: processName,
            ExecutablePath: exePath,
            FileDescription: versionInfo?.FileDescription,
            ProductName: versionInfo?.ProductName,
            CompanyName: versionInfo?.CompanyName,
            FileVersion: versionInfo?.FileVersion,
            ProductVersion: versionInfo?.ProductVersion,
            OriginalFilename: versionInfo?.OriginalFilename,
            SignatureStatus: authInfo.Status,
            SignerName: authInfo.SignerName,
            CommandLine: cmdLine,
            HostedServices: services,
            Ancestry: ancestry
        );
    }
}
