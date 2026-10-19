using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Logs;

public static class UsageStatsCalculator
{
    public static readonly TimeSpan BlockLength = TimeSpan.FromHours(5);
    private static readonly TimeSpan BurnRateWindow = TimeSpan.FromMinutes(60);

    public static UsageStats Compute(UsageRepository repository, DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var todayStart = new DateTimeOffset(local.Date, local.Offset);
        var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
        var weekStart = todayStart.AddDays(-daysSinceMonday);
        var monthStart = new DateTimeOffset(new DateTime(local.Year, local.Month, 1), local.Offset);
        var end = now.AddMinutes(1);

        // 現在のブロックの開始を正しく求めるため、ブロック長より十分前から読む
        var timeline = repository.Timeline(now - TimeSpan.FromHours(24), end);

        return new UsageStats(
            Today: repository.SumTokens(todayStart, end),
            Week: repository.SumTokens(weekStart, end),
            Month: repository.SumTokens(monthStart, end),
            CurrentBlock: ComputeCurrentBlock(repository, timeline, now),
            ModelsThisWeek: repository.TotalsBy("model", weekStart, end, 10),
            ProjectsThisWeek: repository.TotalsBy("project_path", weekStart, end, 5),
            ComputedAt: now);
    }

    /// <summary>
    /// 5時間ブロック：ブロック外で最初のメッセージの時刻を時単位で切り捨てた時刻から5時間。
    /// 現在がどのブロックにも含まれない場合は null。
    /// </summary>
    public static DateTimeOffset? FindCurrentBlockStart(IReadOnlyList<TimelinePoint> timeline, DateTimeOffset now)
    {
        DateTimeOffset? blockStart = null;
        foreach (var point in timeline)
        {
            if (point.Timestamp > now)
                break;
            if (blockStart is null || point.Timestamp >= blockStart.Value + BlockLength)
                blockStart = FloorToHour(point.Timestamp);
        }

        return blockStart is { } start && now < start + BlockLength ? start : null;
    }

    private static UsageBlock? ComputeCurrentBlock(UsageRepository repository, IReadOnlyList<TimelinePoint> timeline, DateTimeOffset now)
    {
        if (FindCurrentBlockStart(timeline, now) is not { } start)
            return null;

        var blockEnd = start + BlockLength;
        var tokens = repository.SumTokens(start, now.AddMinutes(1));
        var recent = timeline.Where(p => p.Timestamp > now - BurnRateWindow && p.Timestamp <= now).Sum(p => p.Tokens);
        var burnPerMinute = recent / BurnRateWindow.TotalMinutes;
        var projected = tokens.Total + (long)(burnPerMinute * Math.Max(0, (blockEnd - now).TotalMinutes));
        return new UsageBlock(start, blockEnd, tokens, burnPerMinute, projected);
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}
