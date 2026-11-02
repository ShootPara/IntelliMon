using System;

namespace IntelliMon.Core.Knowledge;

public enum KnowledgeSource
{
    BuiltIn,
    AiLearned,
    UserDefined
}

public sealed record ProcessKnowledgeEntry(
    int Id,
    string BinaryName,
    string? Publisher,
    string? ServiceName,
    string? PathPattern,
    string DisplayName,
    string? ProductName,
    string Summary,
    string Category,
    KnowledgeSource Source,
    double Confidence,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc
)
{
    public override string ToString() => $"{DisplayName} [{Category}] - {Summary} (Source: {Source})";
}
