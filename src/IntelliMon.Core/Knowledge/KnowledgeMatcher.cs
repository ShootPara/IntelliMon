using System;
using System.Collections.Generic;
using System.Linq;
using IntelliMon.Core.Metadata;

namespace IntelliMon.Core.Knowledge;

public sealed record KnowledgeMatchResult(
    ProcessKnowledgeEntry Entry,
    int MatchScore,
    string MatchReason
);

public static class KnowledgeMatcher
{
    private static readonly HashSet<string> GenericRuntimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell.exe",
        "cmd.exe",
        "python.exe",
        "python3.exe",
        "node.exe",
        "java.exe",
        "javaw.exe",
        "dotnet.exe",
        "wscript.exe",
        "cscript.exe",
        "bash.exe"
    };

    public static bool IsGenericRuntime(string? processName)
    {
        return !string.IsNullOrEmpty(processName) && GenericRuntimes.Contains(processName);
    }

    /// <summary>
    /// Evaluates candidate knowledge entries against resolved ProcessIdentityInfo.
    /// Returns the best matching knowledge entry with its score and reason, or null if evidence is insufficient.
    /// </summary>
    public static KnowledgeMatchResult? Match(
        ProcessIdentityInfo identity,
        IEnumerable<ProcessKnowledgeEntry> candidateEntries)
    {
        if (identity == null || candidateEntries == null)
        {
            return null;
        }

        KnowledgeMatchResult? bestMatch = null;
        string normalizedExePath = PathNormalizer.NormalizePath(identity.ExecutablePath);

        foreach (var entry in candidateEntries)
        {
            // BinaryName must match (case-insensitive)
            if (!entry.BinaryName.Equals(identity.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int score = 0;
            string reason = string.Empty;

            // 1. Service-Hosted Match (Highest Precedence - Score 100)
            if (!string.IsNullOrWhiteSpace(entry.ServiceName) && identity.HostedServices.Count > 0)
            {
                bool serviceMatched = identity.HostedServices.Any(s =>
                    s.ServiceName.Equals(entry.ServiceName, StringComparison.OrdinalIgnoreCase));

                if (serviceMatched)
                {
                    score = 100;
                    reason = $"Hosted Service Match ('{entry.ServiceName}')";
                }
            }

            // 2. Publisher / Signer Match (Score 80)
            if (score == 0 && !string.IsNullOrWhiteSpace(entry.Publisher))
            {
                bool publisherMatched =
                    (!string.IsNullOrWhiteSpace(identity.SignerName) &&
                     identity.SignerName.Contains(entry.Publisher, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(identity.CompanyName) &&
                     identity.CompanyName.Contains(entry.Publisher, StringComparison.OrdinalIgnoreCase));

                if (publisherMatched)
                {
                    score = 80;
                    reason = $"Publisher / Signer Match ('{entry.Publisher}')";
                }
            }

            // 3. Path Pattern Match (Score 60)
            if (score == 0 && !string.IsNullOrWhiteSpace(entry.PathPattern) && !string.IsNullOrWhiteSpace(normalizedExePath))
            {
                if (normalizedExePath.Equals(entry.PathPattern, StringComparison.OrdinalIgnoreCase) ||
                    normalizedExePath.Contains(entry.PathPattern, StringComparison.OrdinalIgnoreCase))
                {
                    score = 60;
                    reason = $"Path Pattern Match ('{entry.PathPattern}')";
                }
            }

            // 4. Exact BinaryName Match for Non-Generic Executables (Score 40)
            if (score == 0)
            {
                if (!IsGenericRuntime(identity.ProcessName) &&
                    string.IsNullOrWhiteSpace(entry.ServiceName) &&
                    string.IsNullOrWhiteSpace(entry.PathPattern))
                {
                    score = 40;
                    reason = $"Binary Name Match ('{identity.ProcessName}')";
                }
            }

            if (score > 0 && (bestMatch == null || score > bestMatch.MatchScore))
            {
                bestMatch = new KnowledgeMatchResult(entry, score, reason);
            }
        }

        return bestMatch;
    }
}
