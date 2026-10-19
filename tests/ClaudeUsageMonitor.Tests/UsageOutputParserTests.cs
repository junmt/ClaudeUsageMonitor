using ClaudeUsageMonitor.Core.Cli;

namespace ClaudeUsageMonitor.Tests;

public class UsageOutputParserTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    // 2026-10-19 17:42 JST
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 17, 42, 0, TimeSpan.FromHours(9));

    // 2026-10-19 に Windows の Claude Code 2.1.287 から実際に得た出力
    private const string WindowsOutput =
        """{"is_error":false,"duration_api_ms":0,"num_turns":0,"session_id":"00000000-0000-0000-0000-000000000000","total_cost_usd":0,"subtype":"success","result":"You are currently using your subscription to power your Claude Code usage\n\nCurrent session: 24% used · resets Oct 19, 8pm (Asia/Tokyo)\nCurrent week (all models): 6% used · resets Oct 26, 8am (Asia/Tokyo)\nCurrent week (Fable): 0% used · resets Oct 26, 8am (Asia/Tokyo)","local_command":"usage","type":"result"}""";

    [Fact]
    public void ParsesLimitsFromCliJson()
    {
        var snapshot = UsageOutputParser.ParseCliOutput(WindowsOutput, Now);

        Assert.True(snapshot.UsingSubscription);
        Assert.Null(snapshot.Insights);
        Assert.Collection(snapshot.Limits,
            l =>
            {
                Assert.Equal("Current session", l.RawLabel);
                Assert.Equal("Session (5hr)", l.Label);
                Assert.Equal(24, l.UsedPercent);
                Assert.Equal(new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.FromHours(9)), l.ResetsAt);
            },
            l =>
            {
                Assert.Equal("Weekly (7 day)", l.Label);
                Assert.Equal(6, l.UsedPercent);
                Assert.Equal(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.FromHours(9)), l.ResetsAt);
            },
            l =>
            {
                Assert.Equal("Fable limit", l.Label);
                Assert.Equal(0, l.UsedPercent);
            });
    }

    [Fact]
    public void IgnoresWslWarningBeforeJson()
    {
        var stdout = "wsl: Failed to translate 'G:\\マイドライブ\\workspace'\r\n" + WindowsOutput + "\n";
        var snapshot = UsageOutputParser.ParseCliOutput(stdout, Now);
        Assert.Equal(3, snapshot.Limits.Count);
    }

    [Fact]
    public void SeparatesInsightsSection()
    {
        // 2026-10-19 に WSL (Ubuntu) の Claude Code から実際に得た result
        const string result = """
            You are currently using your subscription to power your Claude Code usage

            Current session: 25% used · resets Oct 19, 7:59pm (Asia/Tokyo)
            Current week (all models): 6% used · resets Oct 26, 7:59am (Asia/Tokyo)
            Current week (Fable): 0% used · resets Oct 26, 8am (Asia/Tokyo)

            What's contributing to your limits usage?
            Approximate, based on local sessions on this machine — does not include other devices or claude.ai.

            Last 24h · 516 requests · 6 sessions
              91% of your usage was at >150k context
              Top skills: /review 4%, /pptx 3%, /xlsx 3%
            """;

        var snapshot = UsageOutputParser.ParseResultText(result, Now);

        Assert.Equal(3, snapshot.Limits.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 19, 59, 0, TimeSpan.FromHours(9)), snapshot.Limits[0].ResetsAt);
        Assert.NotNull(snapshot.Insights);
        Assert.StartsWith("What's contributing", snapshot.Insights);
        Assert.Contains("516 requests", snapshot.Insights);
    }

    [Fact]
    public void ThrowsWhenCliReportsError()
    {
        const string stdout = """{"is_error":true,"result":"Not logged in","type":"result"}""";
        var ex = Assert.Throws<CliOutputException>(() => UsageOutputParser.ParseCliOutput(stdout, Now));
        Assert.Contains("Not logged in", ex.Message);
    }

    [Fact]
    public void ThrowsWhenOutputIsNotJson()
    {
        Assert.Throws<CliOutputException>(() => UsageOutputParser.ParseCliOutput("command not found", Now));
    }

    [Fact]
    public void KeepsRawTextWhenFormatIsUnknown()
    {
        var snapshot = UsageOutputParser.ParseResultText("Usage information is temporarily unavailable.", Now);
        Assert.Empty(snapshot.Limits);
        Assert.Equal("Usage information is temporarily unavailable.", snapshot.RawResult);
    }

    [Theory]
    [InlineData("Current session", "Session (5hr)")]
    [InlineData("Current week (all models)", "Weekly (7 day)")]
    [InlineData("Current week (Opus)", "Opus limit")]
    [InlineData("Something new", "Something new")]
    public void MapsLabels(string raw, string expected) => Assert.Equal(expected, UsageOutputParser.MapLabel(raw));

    [Fact]
    public void ResetDateRollsOverToNextYear()
    {
        var newYearsEve = new DateTimeOffset(2026, 12, 30, 12, 0, 0, TimeSpan.FromHours(9));
        var reset = UsageOutputParser.ParseResetTime("Jan 2, 8am", "Asia/Tokyo", newYearsEve);
        Assert.Equal(new DateTimeOffset(2027, 1, 2, 8, 0, 0, TimeSpan.FromHours(9)), reset);
    }

    [Fact]
    public void RecentlyPassedResetStaysInThePast()
    {
        var reset = UsageOutputParser.ParseResetTime("Oct 19, 5pm", "Asia/Tokyo", Now);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 17, 0, 0, TimeSpan.FromHours(9)), reset);
    }

    [Fact]
    public void ResetWithoutDateIsToday()
    {
        var reset = UsageOutputParser.ParseResetTime("8pm", "Asia/Tokyo", Now);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.FromHours(9)), reset);
    }

    [Fact]
    public void ResetUsesGivenTimeZone()
    {
        var reset = UsageOutputParser.ParseResetTime("Oct 19, 8am", "America/New_York", Now);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.FromHours(-4)), reset);
        Assert.NotEqual(Tokyo.BaseUtcOffset, reset!.Value.Offset);
    }

    [Fact]
    public void ParsesAuthStatus()
    {
        const string stdout = """
            {
              "loggedIn": true,
              "authMethod": "claude.ai",
              "email": "user@example.co.jp",
              "orgName": "Example Inc",
              "subscriptionType": "team"
            }
            """;
        var account = UsageOutputParser.ParseAuthStatus(stdout);
        Assert.True(account.LoggedIn);
        Assert.Equal("user@example.co.jp", account.Email);
        Assert.Equal("Example Inc", account.OrgName);
        Assert.Equal("team", account.SubscriptionType);
    }
}
