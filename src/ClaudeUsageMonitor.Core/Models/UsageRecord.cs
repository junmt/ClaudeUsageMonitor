namespace ClaudeUsageMonitor.Core.Models;

/// <summary>セッションログ中の assistant 応答1件分の usage。</summary>
public sealed record UsageRecord(
    string MessageId,
    string RequestId,
    DateTimeOffset Timestamp,
    string? SessionId,
    string? ProjectPath,
    string? Model,
    bool IsSidechain,
    long InputTokens,
    long OutputTokens,
    long CacheWrite5mTokens,
    long CacheWrite1hTokens,
    long CacheReadTokens,
    long WebSearchRequests)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheWrite5mTokens + CacheWrite1hTokens + CacheReadTokens;
}
