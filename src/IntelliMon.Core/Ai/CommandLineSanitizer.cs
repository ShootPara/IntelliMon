using System;
using System.Text.RegularExpressions;

namespace IntelliMon.Core.Ai;

/// <summary>
/// Scrubs privacy-sensitive data from process metadata before it is transmitted to an external AI provider.
/// Strips Windows username path components, personal folder names, and private command-line arguments.
/// </summary>
public static class CommandLineSanitizer
{
    // Matches C:\Users\<username>\ or %USERPROFILE%\ style prefixes
    private static readonly Regex UserPathRegex = new(
        @"(?:[A-Za-z]:\\Users\\)[^\\""'\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches %USERPROFILE% and %LOCALAPPDATA% environment variable expansions that may leak username
    private static readonly Regex UserEnvVarRegex = new(
        @"%(?:USERPROFILE|LOCALAPPDATA|APPDATA|HOMEDRIVE|HOMEPATH)%",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches common password/secret-style flags: --password=xxx, -p=xxx, /key:xxx
    private static readonly Regex SecretArgRegex = new(
        @"(?:--?(?:password|passwd|pwd|secret|token|key|apikey|api-key|auth|credential|cred)s?)[=:\s]+\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Sanitizes an executable path for AI transmission by replacing user-specific path components.
    /// </summary>
    public static string SanitizePath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return "<path unavailable>";
        }

        string sanitized = UserPathRegex.Replace(fullPath, @"C:\Users\<user>");
        sanitized = UserEnvVarRegex.Replace(sanitized, "%USERPROFILE%");
        return sanitized;
    }

    /// <summary>
    /// Sanitizes a process command line for AI transmission by removing user-specific paths and secret arguments.
    /// </summary>
    public static string SanitizeCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return "<command line unavailable>";
        }

        string sanitized = UserPathRegex.Replace(commandLine, @"C:\Users\<user>");
        sanitized = UserEnvVarRegex.Replace(sanitized, "%USERPROFILE%");
        sanitized = SecretArgRegex.Replace(sanitized, "<redacted>");
        return sanitized;
    }
}
