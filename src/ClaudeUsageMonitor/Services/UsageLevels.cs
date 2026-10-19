using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;
using ClaudeUsageMonitor.ViewModels;

namespace ClaudeUsageMonitor.Services;

public static class UsageLevels
{
    public static UsageLevel Of(double percent, AppSettings settings) =>
        percent >= settings.CriticalThreshold ? UsageLevel.Critical
        : percent >= settings.WarningThreshold ? UsageLevel.Warning
        : UsageLevel.Normal;

    /// <summary>トレイアイコンに表示する枠（Session (5hr)、無ければ先頭）。</summary>
    public static UsageLimit? Primary(RateLimitSnapshot? snapshot) =>
        snapshot?.Limits.FirstOrDefault(l => l.RawLabel.Equals("Current session", StringComparison.OrdinalIgnoreCase))
        ?? snapshot?.Limits.FirstOrDefault();
}
