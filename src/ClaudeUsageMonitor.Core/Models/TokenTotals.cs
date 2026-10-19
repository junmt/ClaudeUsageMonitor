namespace ClaudeUsageMonitor.Core.Models;

public sealed record TokenTotals(long Input, long Output, long CacheWrite, long CacheRead)
{
    public static readonly TokenTotals Zero = new(0, 0, 0, 0);

    public long Total => Input + Output + CacheWrite + CacheRead;
}
