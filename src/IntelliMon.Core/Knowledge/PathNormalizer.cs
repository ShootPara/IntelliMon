using System;
using System.IO;
using System.Text.RegularExpressions;

namespace IntelliMon.Core.Knowledge;

public static class PathNormalizer
{
    private static readonly Lazy<string?> UserProfilePath = new(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private static readonly Lazy<string?> LocalAppDataPath = new(() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private static readonly Lazy<string?> AppDataPath = new(() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    private static readonly Lazy<string?> SystemRootPath = new(() => Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly Lazy<string?> ProgramFilesPath = new(() => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    private static readonly Lazy<string?> ProgramFilesX86Path = new(() => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

    // Regex to match dynamic version directory segments like "app-1.0.9256", "123.0.6312.59", "v2.4.1"
    private static readonly Regex VersionSegmentRegex = new(@"\\(app-\d+[\w\.\-]+|\d+\.\d+[\w\.\-]*|v\d+[\w\.\-]*)\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Normalizes a full Windows file path into a machine-agnostic pattern by replacing
    /// user profiles, system folders, and dynamic version segments.
    /// </summary>
    public static string NormalizePath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return string.Empty;
        }

        string path = Path.GetFullPath(fullPath);

        // Replace known root paths (ordered from most specific to least specific)
        if (!string.IsNullOrEmpty(LocalAppDataPath.Value) && path.StartsWith(LocalAppDataPath.Value, StringComparison.OrdinalIgnoreCase))
        {
            path = "%LOCALAPPDATA%" + path.Substring(LocalAppDataPath.Value.Length);
        }
        else if (!string.IsNullOrEmpty(AppDataPath.Value) && path.StartsWith(AppDataPath.Value, StringComparison.OrdinalIgnoreCase))
        {
            path = "%APPDATA%" + path.Substring(AppDataPath.Value.Length);
        }
        else if (!string.IsNullOrEmpty(UserProfilePath.Value) && path.StartsWith(UserProfilePath.Value, StringComparison.OrdinalIgnoreCase))
        {
            path = "%USERPROFILE%" + path.Substring(UserProfilePath.Value.Length);
        }
        else if (!string.IsNullOrEmpty(ProgramFilesX86Path.Value) && path.StartsWith(ProgramFilesX86Path.Value, StringComparison.OrdinalIgnoreCase))
        {
            path = "%PROGRAMFILES(X86)%" + path.Substring(ProgramFilesX86Path.Value.Length);
        }
        else if (!string.IsNullOrEmpty(ProgramFilesPath.Value) && path.StartsWith(ProgramFilesPath.Value, StringComparison.OrdinalIgnoreCase))
        {
            path = "%PROGRAMFILES%" + path.Substring(ProgramFilesPath.Value.Length);
        }
        else if (!string.IsNullOrEmpty(SystemRootPath.Value) && path.StartsWith(SystemRootPath.Value, StringComparison.OrdinalIgnoreCase))
        {
            path = "%SYSTEMROOT%" + path.Substring(SystemRootPath.Value.Length);
        }

        // Replace dynamic version directories with wildcards
        path = VersionSegmentRegex.Replace(path, @"\...\");

        return path;
    }
}
