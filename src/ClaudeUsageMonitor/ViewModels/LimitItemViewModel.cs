namespace ClaudeUsageMonitor.ViewModels;

public enum UsageLevel
{
    Normal,
    Warning,
    Critical,
    Stale,
}

/// <summary>ウィジェットの利用枠1行分。</summary>
public sealed record LimitItemViewModel(
    string Label,
    double Percent,
    string PercentText,
    string ResetText,
    string? ResetTooltip,
    UsageLevel Level);

/// <summary>詳細モードの内訳（モデル別・プロジェクト別）1行分。</summary>
public sealed record BarItemViewModel(string Name, string ValueText, double Ratio, string? Tooltip);
