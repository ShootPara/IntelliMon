using System;
using System.Collections.Generic;
using System.Linq;

namespace IntelliMon.Core.Metadata;

public sealed record ProcessIdentityInfo(
    int ProcessId,
    long CreateTime,
    string ProcessName,
    string? ExecutablePath,
    string? FileDescription,
    string? ProductName,
    string? CompanyName,
    string? FileVersion,
    string? ProductVersion,
    string? OriginalFilename,
    SignatureVerificationStatus SignatureStatus,
    string? SignerName,
    string? CommandLine,
    IReadOnlyList<HostedServiceInfo> HostedServices,
    IReadOnlyList<ProcessAncestryNode> Ancestry
)
{
    public string FormattedAncestry =>
        Ancestry.Count > 0
            ? string.Join(" -> ", Ancestry.Select(a => $"{a.ProcessName} ({a.ProcessId})"))
            : $"{ProcessName} ({ProcessId})";

    public string PrimaryDescription
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FileDescription))
            {
                return FileDescription;
            }

            if (HostedServices.Count > 0)
            {
                return string.Join(", ", HostedServices.Select(s => s.DisplayName));
            }

            if (!string.IsNullOrWhiteSpace(ProductName))
            {
                return ProductName;
            }

            return ProcessName;
        }
    }
}
