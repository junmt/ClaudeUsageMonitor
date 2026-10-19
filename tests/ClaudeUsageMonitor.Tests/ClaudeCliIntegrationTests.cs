using ClaudeUsageMonitor.Core.Cli;
using ClaudeUsageMonitor.Core.Settings;

namespace ClaudeUsageMonitor.Tests;

/// <summary>
/// インストール済みの Claude Code を実際に実行するテスト。
/// 環境変数 CUM_INTEGRATION=1 のときだけ実行する（例: $env:CUM_INTEGRATION=1; dotnet test）。
/// </summary>
public class ClaudeCliIntegrationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("CUM_INTEGRATION") == "1";

    [Theory]
    [InlineData(SourceMode.Auto)]
    [InlineData(SourceMode.WindowsOnly)]
    [InlineData(SourceMode.WslOnly)]
    public async Task FetchesUsageFromInstalledClaudeCode(SourceMode mode)
    {
        if (!Enabled)
            return;

        var service = new ClaudeCliService(() => new AppSettings { Source = mode });
        var snapshot = await service.FetchUsageAsync(CancellationToken.None);

        Assert.NotEmpty(snapshot.Limits);
        Assert.Contains(snapshot.Limits, l => l.RawLabel == "Current session");
        Assert.All(snapshot.Limits, l => Assert.NotNull(l.ResetsAt));
        Assert.Equal(mode == SourceMode.WslOnly ? EndpointKind.Wsl : EndpointKind.Windows, service.LastEndpoint!.Kind);

        var account = await service.FetchAccountAsync(CancellationToken.None);
        Assert.True(account.LoggedIn);
        Assert.False(string.IsNullOrEmpty(account.Email));
    }
}
