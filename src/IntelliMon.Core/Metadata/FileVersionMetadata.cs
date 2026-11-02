using System;
using System.Diagnostics;
using System.IO;

namespace IntelliMon.Core.Metadata;

public sealed record FileVersionMetadata(
    string? FileDescription,
    string? ProductName,
    string? CompanyName,
    string? FileVersion,
    string? ProductVersion,
    string? OriginalFilename
);

public static class FileVersionMetadataProvider
{
    public static FileVersionMetadata? GetMetadata(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(filePath);
            return new FileVersionMetadata(
                FileDescription: Clean(info.FileDescription),
                ProductName: Clean(info.ProductName),
                CompanyName: Clean(info.CompanyName),
                FileVersion: Clean(info.FileVersion),
                ProductVersion: Clean(info.ProductVersion),
                OriginalFilename: Clean(info.OriginalFilename)
            );
        }
        catch
        {
            return null;
        }
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }
}
