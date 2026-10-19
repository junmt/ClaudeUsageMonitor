namespace ClaudeUsageMonitor.Core.Models;

/// <summary>/usage の1回分の取得結果。ratelimit.json にもこの形で保存する。</summary>
public sealed class RateLimitSnapshot
{
    public int Version { get; set; } = 1;
    public DateTimeOffset FetchedAt { get; set; }
    public string? CliVersion { get; set; }
    public AccountInfo? Account { get; set; }
    public List<UsageLimit> Limits { get; set; } = [];

    /// <summary>先頭行から判断した、サブスクリプションで利用中かどうか。</summary>
    public bool UsingSubscription { get; set; } = true;

    /// <summary>「What's contributing to your limits usage?」以降の利用傾向テキスト。</summary>
    public string? Insights { get; set; }

    public string RawResult { get; set; } = "";
}
