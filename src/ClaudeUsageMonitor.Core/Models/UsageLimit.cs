namespace ClaudeUsageMonitor.Core.Models;

/// <summary>/usage が返す利用枠1件分。</summary>
public sealed record UsageLimit(
    string RawLabel,
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    string? ResetText);
