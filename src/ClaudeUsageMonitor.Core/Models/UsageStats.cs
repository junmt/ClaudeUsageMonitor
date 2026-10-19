namespace ClaudeUsageMonitor.Core.Models;

public sealed record NamedTotal(string Name, long Tokens);

/// <summary>ローカルログから算出した5時間ブロック。</summary>
public sealed record UsageBlock(
    DateTimeOffset Start,
    DateTimeOffset End,
    TokenTotals Tokens,
    double BurnRatePerMinute,
    long ProjectedTotal);

public sealed record UsageStats(
    TokenTotals Today,
    TokenTotals Week,
    TokenTotals Month,
    UsageBlock? CurrentBlock,
    IReadOnlyList<NamedTotal> ModelsThisWeek,
    IReadOnlyList<NamedTotal> ProjectsThisWeek,
    DateTimeOffset ComputedAt);
