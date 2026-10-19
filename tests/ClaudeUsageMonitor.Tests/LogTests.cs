using System.Text;
using ClaudeUsageMonitor.Core.Logs;

namespace ClaudeUsageMonitor.Tests;

public sealed class LogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cum-tests-" + Guid.NewGuid().ToString("N"));

    public LogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Line(string messageId, string requestId, string timestamp, long input, long output,
        long cacheRead = 0, long cache1h = 0, string model = "claude-opus-5-5", string cwd = @"G:\work\app") =>
        """{"parentUuid":null,"isSidechain":false,"message":{"id":"MID","model":"MODEL","role":"assistant","content":[{"type":"text","text":"本文は読まない"}],"usage":{"input_tokens":IN,"cache_creation_input_tokens":CW1H,"cache_read_input_tokens":CR,"output_tokens":OUT,"server_tool_use":{"web_search_requests":0},"cache_creation":{"ephemeral_1h_input_tokens":CW1H,"ephemeral_5m_input_tokens":0}}},"requestId":"RID","type":"assistant","timestamp":"TS","cwd":"CWD","sessionId":"s1"}"""
            .Replace("MID", messageId).Replace("MODEL", model).Replace("RID", requestId).Replace("TS", timestamp)
            .Replace("CWD", cwd.Replace("\\", "\\\\")).Replace("CW1H", cache1h.ToString()).Replace("CR", cacheRead.ToString())
            .Replace("IN", input.ToString()).Replace("OUT", output.ToString());

    [Fact]
    public void ParsesUsageLine()
    {
        Assert.True(LogLineParser.TryParse(Line("m1", "r1", "2026-10-19T08:00:00.000Z", 2, 513, 29587, 11489), out var r));
        Assert.Equal("m1", r.MessageId);
        Assert.Equal("claude-opus-5-5", r.Model);
        Assert.Equal(2, r.InputTokens);
        Assert.Equal(513, r.OutputTokens);
        Assert.Equal(11489, r.CacheWrite1hTokens);
        Assert.Equal(0, r.CacheWrite5mTokens);
        Assert.Equal(29587, r.CacheReadTokens);
        Assert.Equal(@"G:\work\app", r.ProjectPath);
    }

    [Theory]
    [InlineData("""{"type":"user","message":{"role":"user","content":"hi"}}""")]
    [InlineData("""{"type":"assistant","message":{"id":"x","model":"<synthetic>","usage":{"input_tokens":0,"output_tokens":0}},"timestamp":"2026-10-19T08:00:00Z"}""")]
    [InlineData("""{"broken json""")]
    public void SkipsNonUsageLines(string line) => Assert.False(LogLineParser.TryParse(line, out _));

    [Fact]
    public async Task CollectorReadsIncrementallyAndDeduplicates()
    {
        var projects = Path.Combine(_dir, "projects", "proj");
        Directory.CreateDirectory(projects);
        var log = Path.Combine(projects, "session.jsonl");

        // 同じ応答がストリーミングで2行記録され、最後の行の値が最終値
        await File.WriteAllTextAsync(log,
            Line("m1", "r1", "2026-10-19T08:00:00.000Z", 10, 5) + "\n"
            + Line("m1", "r1", "2026-10-19T08:00:00.000Z", 10, 50) + "\n", new UTF8Encoding(false));

        using var repo = new UsageRepository(Path.Combine(_dir, "usage.db"));
        using var collector = new LogCollector(repo, _ => Task.FromResult<IReadOnlyList<LogSource>>([new LogSource(Path.Combine(_dir, "projects"), true)]));

        await collector.ScanAsync();
        var from = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
        var to = DateTimeOffset.Parse("2026-10-20T00:00:00Z");
        Assert.Equal(60, repo.SumTokens(from, to).Total);

        // 追記分（改行で終わらない書き込み途中の行を含む）
        await File.AppendAllTextAsync(log,
            Line("m2", "r2", "2026-10-19T09:00:00.000Z", 100, 0) + "\n"
            + Line("m3", "r3", "2026-10-19T09:30:00.000Z", 1000, 0)[..40]);
        await collector.ScanAsync();
        Assert.Equal(160, repo.SumTokens(from, to).Total);

        // 書き込み途中だった行が完成したら取り込まれる
        await File.WriteAllTextAsync(log,
            File.ReadAllText(log)[..^40] + Line("m3", "r3", "2026-10-19T09:30:00.000Z", 1000, 0) + "\n");
        await collector.ScanAsync();
        Assert.Equal(1160, repo.SumTokens(from, to).Total);

        var models = repo.TotalsBy("model", from, to, 10);
        Assert.Single(models);
        Assert.Equal(1160, models[0].Tokens);
    }

    [Fact]
    public void FindsCurrentFiveHourBlock()
    {
        var now = DateTimeOffset.Parse("2026-10-19T10:30:00Z");
        var timeline = new List<TimelinePoint>
        {
            new(DateTimeOffset.Parse("2026-10-19T01:10:00Z"), 1), // ブロック 01:00-06:00
            new(DateTimeOffset.Parse("2026-10-19T05:50:00Z"), 1),
            new(DateTimeOffset.Parse("2026-10-19T07:45:00Z"), 1), // ブロック 07:00-12:00
            new(DateTimeOffset.Parse("2026-10-19T10:00:00Z"), 1),
        };

        Assert.Equal(DateTimeOffset.Parse("2026-10-19T07:00:00Z"), UsageStatsCalculator.FindCurrentBlockStart(timeline, now));
        Assert.Null(UsageStatsCalculator.FindCurrentBlockStart(timeline, DateTimeOffset.Parse("2026-10-19T12:30:00Z")));
    }
}
