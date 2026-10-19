using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Cli;

public sealed class CliOutputException(string message) : Exception(message);

/// <summary>
/// claude -p "/usage" --output-format json の出力を解析する。
/// </summary>
public static partial class UsageOutputParser
{
    private const string InsightsHeading = "What's contributing";

    // 例: "Current session: 24% used · resets Oct 19, 8pm (Asia/Tokyo)"
    [GeneratedRegex(@"^(?<label>[^:]+?):\s*(?<pct>\d+(?:\.\d+)?)%\s*used(?:\s*[·•\-]\s*resets\s+(?<reset>.+?)(?:\s*\((?<tz>[^)]+)\))?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LimitLineRegex();

    // 例: "Oct 19, 8pm" / "Oct 19, 7:59pm" / "8pm"
    [GeneratedRegex(@"^(?:(?<mon>[A-Za-z]{3,9})\.?\s+(?<day>\d{1,2})(?:,\s*|\s+at\s+|\s+))?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResetTimeRegex();

    [GeneratedRegex(@"^Current week \((?<model>.+)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeeklyModelRegex();

    /// <summary>CLI の標準出力全体からスナップショットを作る。</summary>
    public static RateLimitSnapshot ParseCliOutput(string stdout, DateTimeOffset now)
    {
        using var doc = ParseJson(stdout);
        var root = doc.RootElement;

        if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            var message = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : "不明なエラー";
            throw new CliOutputException($"Claude Code がエラーを返しました: {message}");
        }

        if (!root.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.String)
            throw new CliOutputException("Claude Code の出力に result がありません。");

        var snapshot = ParseResultText(resultElement.GetString() ?? "", now);
        if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
            snapshot.CliVersion = version.GetString();
        return snapshot;
    }

    /// <summary>/usage の result テキストを解析する。</summary>
    public static RateLimitSnapshot ParseResultText(string result, DateTimeOffset now)
    {
        var snapshot = new RateLimitSnapshot { FetchedAt = now, RawResult = result };
        var lines = result.Replace("\r\n", "\n").Split('\n');

        var insightsStart = Array.FindIndex(lines, l => l.TrimStart().StartsWith(InsightsHeading, StringComparison.OrdinalIgnoreCase));
        var limitLines = insightsStart >= 0 ? lines[..insightsStart] : lines;
        if (insightsStart >= 0)
            snapshot.Insights = string.Join(Environment.NewLine, lines[insightsStart..]).Trim();

        foreach (var line in limitLines)
        {
            var match = LimitLineRegex().Match(line.Trim());
            if (!match.Success)
                continue;

            var rawLabel = match.Groups["label"].Value.Trim();
            var percent = double.Parse(match.Groups["pct"].Value, CultureInfo.InvariantCulture);
            var resetText = match.Groups["reset"].Success ? match.Groups["reset"].Value.Trim() : null;
            var tz = match.Groups["tz"].Success ? match.Groups["tz"].Value.Trim() : null;
            var resetsAt = resetText is null ? null : ParseResetTime(resetText, tz, now);

            snapshot.Limits.Add(new UsageLimit(rawLabel, MapLabel(rawLabel), percent, resetsAt, resetText));
        }

        var firstLine = limitLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        snapshot.UsingSubscription = firstLine.Contains("subscription", StringComparison.OrdinalIgnoreCase)
                                     || snapshot.Limits.Count > 0;
        return snapshot;
    }

    /// <summary>claude auth status の出力を解析する。</summary>
    public static AccountInfo ParseAuthStatus(string stdout)
    {
        using var doc = ParseJson(stdout);
        var root = doc.RootElement;
        return new AccountInfo(
            LoggedIn: root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True,
            Email: GetString(root, "email"),
            OrgName: GetString(root, "orgName"),
            SubscriptionType: GetString(root, "subscriptionType"));
    }

    public static string MapLabel(string rawLabel)
    {
        if (rawLabel.Equals("Current session", StringComparison.OrdinalIgnoreCase))
            return "Session (5hr)";
        if (rawLabel.Equals("Current week (all models)", StringComparison.OrdinalIgnoreCase))
            return "Weekly (7 day)";

        var model = WeeklyModelRegex().Match(rawLabel);
        return model.Success ? $"{model.Groups["model"].Value} limit" : rawLabel;
    }

    /// <summary>
    /// "Oct 19, 8pm" のような年のないリセット時刻を、指定タイムゾーンで解釈する。
    /// 年は「現在以降で最も近い日時」になるよう補う（直前に過ぎたリセットは過去のまま残す）。
    /// </summary>
    public static DateTimeOffset? ParseResetTime(string text, string? timeZoneId, DateTimeOffset now)
    {
        var match = ResetTimeRegex().Match(text.Trim());
        if (!match.Success)
            return null;

        var zone = FindTimeZone(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) % 12;
        if (match.Groups["ap"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase))
            hour += 12;
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;

        DateTime candidate;
        if (match.Groups["mon"].Success)
        {
            var month = ParseMonth(match.Groups["mon"].Value);
            var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
            if (month is null || day > 31)
                return null;

            candidate = SafeDate(localNow.Year, month.Value, day, hour, minute);
            // 2日以上過去なら翌年の日付とみなす（年末年始をまたぐケース）
            if (candidate < localNow.DateTime.AddDays(-2))
                candidate = SafeDate(localNow.Year + 1, month.Value, day, hour, minute);
        }
        else
        {
            candidate = localNow.Date.AddHours(hour).AddMinutes(minute);
            if (candidate < localNow.DateTime.AddHours(-1))
                candidate = candidate.AddDays(1);
        }

        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
    }

    private static DateTime SafeDate(int year, int month, int day, int hour, int minute)
    {
        day = Math.Min(day, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
    }

    private static int? ParseMonth(string name)
    {
        var abbreviations = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames;
        var key = name[..3];
        for (var i = 0; i < 12; i++)
        {
            if (abbreviations[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        return null;
    }

    private static TimeZoneInfo FindTimeZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // 不明なタイムゾーンはローカル時刻として扱う
            }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>標準出力の最終の JSON 行を解析する（wsl.exe の警告などが混ざる場合に備える）。</summary>
    private static JsonDocument ParseJson(string stdout)
    {
        var lines = stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('{'))
                continue;
            try
            {
                return JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // 複数行にわたる JSON の可能性があるため、下の全体解析に回す
            }
        }

        var start = stdout.IndexOf('{');
        var end = stdout.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                return JsonDocument.Parse(stdout[start..(end + 1)]);
            }
            catch (JsonException)
            {
            }
        }

        throw new CliOutputException("Claude Code の出力を JSON として解析できませんでした。");
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
