namespace ClaudeUsageMonitor.Core.Models;

/// <summary>claude auth status から得たアカウント情報。</summary>
public sealed record AccountInfo(
    bool LoggedIn,
    string? Email,
    string? OrgName,
    string? SubscriptionType);
