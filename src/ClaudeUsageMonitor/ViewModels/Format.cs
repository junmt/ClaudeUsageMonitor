using System.Globalization;

namespace ClaudeUsageMonitor.ViewModels;

public static class Format
{
    public static string Tokens(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    public static string Percent(double value) =>
        value.ToString(value % 1 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>"Resets in 2h" 形式。過ぎていれば null。</summary>
    public static string? ResetsIn(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var remaining = resetsAt - now;
        if (remaining <= TimeSpan.Zero)
            return null;
        if (remaining.TotalHours < 1)
            return $"Resets in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
        if (remaining.TotalDays < 1)
            return $"Resets in {(int)remaining.TotalHours}h";
        return $"Resets in {(int)remaining.TotalDays}d";
    }

    public static string Ago(TimeSpan elapsed) => elapsed.TotalMinutes switch
    {
        < 1 => "たった今",
        < 60 => $"{(int)elapsed.TotalMinutes}分前",
        < 60 * 24 => $"{(int)elapsed.TotalHours}時間前",
        _ => $"{(int)elapsed.TotalDays}日前",
    };

    public static string DateTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("M/d (ddd) HH:mm", CultureInfo.GetCultureInfo("ja-JP"));

    /// <summary>プロジェクトのパスから表示名（末尾のフォルダ名）を取り出す。Windows・WSL の両方の区切りに対応。</summary>
    public static string ProjectName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        return index >= 0 && index < trimmed.Length - 1 ? trimmed[(index + 1)..] : trimmed;
    }
}
