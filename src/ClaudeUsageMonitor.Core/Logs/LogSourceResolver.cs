using ClaudeUsageMonitor.Core.Cli;
using ClaudeUsageMonitor.Core.Infrastructure;
using ClaudeUsageMonitor.Core.Settings;

namespace ClaudeUsageMonitor.Core.Logs;

/// <summary>集計対象のログディレクトリ（Windows ＋ WSL ＋ 追加ディレクトリ）を決める。</summary>
public static class LogSourceResolver
{
    /// <param name="settings">WSL の HOME をキャッシュするため WslHomes を更新することがある。</param>
    /// <returns>ログディレクトリと、settings を更新したかどうか。</returns>
    public static async Task<(IReadOnlyList<LogSource> Sources, bool SettingsChanged)> ResolveAsync(AppSettings settings, CancellationToken ct)
    {
        var sources = new List<LogSource>();
        var changed = false;

        // Windows（Claude Code が無ければ存在しないので、存在チェックは LogCollector に任せる）
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDir))
            configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        sources.Add(new LogSource(Path.Combine(configDir, "projects"), IsRemote: false));

        foreach (var dir in settings.ExtraLogDirectories)
        {
            var projects = Path.Combine(dir, "projects");
            var target = Directory.Exists(projects) ? projects : dir;
            sources.Add(new LogSource(target, IsRemote: target.StartsWith(@"\\", StringComparison.Ordinal)));
        }

        // WSL
        if (Wsl.IsAvailable)
        {
            var distros = Wsl.ListDistros().Where(d => settings.IsWslDistroEnabled(d.Name)).ToList();
            if (distros.Count > 0)
            {
                var running = await Wsl.ListRunningAsync(ct).ConfigureAwait(false);
                foreach (var distro in distros)
                {
                    // ログ読み込みのためだけに停止中のディストリビューションは起動しない（UNC パスへのアクセスでも起動するため）
                    if (!running.Contains(distro.Name) && !settings.StartStoppedWslForLogs)
                        continue;

                    if (!settings.WslHomes.TryGetValue(distro.Name, out var home))
                    {
                        try
                        {
                            home = await Wsl.GetHomeAsync(distro.Name, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            AppLog.Warn($"WSL ({distro.Name}) の HOME を取得できませんでした: {ex.Message}");
                        }
                        if (home is null)
                            continue;
                        settings.WslHomes[distro.Name] = home;
                        changed = true;
                    }

                    sources.Add(new LogSource(Wsl.ToUncPath(distro.Name, home.TrimEnd('/') + "/.claude/projects"), IsRemote: true));
                }
            }
        }

        var unique = sources
            .GroupBy(s => s.Directory.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        return (unique, changed);
    }
}
