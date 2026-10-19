using System.Runtime.CompilerServices;
using ClaudeUsageMonitor.Core.Infrastructure;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;

namespace ClaudeUsageMonitor.Core.Cli;

public enum EndpointKind
{
    Windows,
    Wsl,
}

/// <summary>実行対象の Claude Code。Windows なら実行ファイルのパス、WSL ならディストリビューション名。</summary>
public sealed record ClaudeEndpoint(EndpointKind Kind, string Target)
{
    public override string ToString() => Kind == EndpointKind.Windows ? $"Windows ({Target})" : $"WSL: {Target}";
}

public sealed class ClaudeNotFoundException()
    : Exception("Claude Code が見つかりません（Windows・WSL のどちらにもインストールされていません）。");

/// <summary>
/// インストール済みの Claude Code CLI を実行して利用枠・アカウント情報を取得する。
/// Windows 側に無い（または失敗した）場合は WSL 側の Claude Code を使う。
/// </summary>
public sealed class ClaudeCliService(Func<AppSettings> settings)
{
    // claude に渡すのはこの2種類のみ（モデル呼び出しを起こさないため）
    private static readonly string[] UsageArgs = ["-p", "/usage", "--output-format", "json", "--no-session-persistence"];
    private static readonly string[] AuthStatusArgs = ["auth", "status"];

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WslDetectionTtl = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeEndpoint? _lastSuccess;
    private IReadOnlyList<ClaudeEndpoint>? _wslEndpoints;
    private DateTimeOffset _wslDetectedAt;

    /// <summary>最後に取得に成功した取得元。</summary>
    public ClaudeEndpoint? LastEndpoint => _lastSuccess;

    public async Task<RateLimitSnapshot> FetchUsageAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunOnFirstAvailableAsync(UsageArgs,
                stdout => UsageOutputParser.ParseCliOutput(stdout, DateTimeOffset.Now),
                accept: null, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AccountInfo> FetchAccountAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // ログアウト中の取得元より、ログイン済みの取得元を優先する
            return await RunOnFirstAvailableAsync(AuthStatusArgs, UsageOutputParser.ParseAuthStatus,
                accept: a => a.LoggedIn, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>次回の取得時に WSL 側を検出し直す。</summary>
    public void InvalidateDetection() => _wslEndpoints = null;

    private async Task<T> RunOnFirstAvailableAsync<T>(string[] args, Func<string, T> parse, Func<T, bool>? accept, CancellationToken ct)
    {
        var errors = new List<string>();
        var tried = new HashSet<ClaudeEndpoint>();
        var fallback = default(T);
        var hasFallback = false;

        await foreach (var endpoint in EnumerateCandidatesAsync(ct).ConfigureAwait(false))
        {
            if (!tried.Add(endpoint))
                continue;

            try
            {
                var result = await RunAsync(endpoint, args, ct).ConfigureAwait(false);
                if (result.TimedOut)
                {
                    errors.Add($"{endpoint}: タイムアウトしました");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(result.StdOut))
                {
                    errors.Add($"{endpoint}: 出力がありません（終了コード {result.ExitCode}）{FirstLine(result.StdErr)}");
                    continue;
                }

                var value = parse(result.StdOut);
                if (accept is not null && !accept(value))
                {
                    fallback = value;
                    hasFallback = true;
                    errors.Add($"{endpoint}: ログインしていません");
                    continue;
                }

                _lastSuccess = endpoint;
                return value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{endpoint}: {ex.Message}");
            }
        }

        // すべて失敗したら次回は検出からやり直す
        _lastSuccess = null;
        InvalidateDetection();

        if (hasFallback)
            return fallback!;
        if (tried.Count == 0)
            throw new ClaudeNotFoundException();

        var message = string.Join(Environment.NewLine, errors);
        AppLog.Warn($"Claude Code の実行に失敗しました:{Environment.NewLine}{message}");
        throw new CliOutputException(message);
    }

    private async IAsyncEnumerable<ClaudeEndpoint> EnumerateCandidatesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var mode = settings().Source;

        if (_lastSuccess is { } last && IsAllowed(last, mode))
            yield return last;

        if (mode != SourceMode.WslOnly && FindWindowsClaude(settings().ClaudePath) is { } exe)
            yield return new ClaudeEndpoint(EndpointKind.Windows, exe);

        if (mode == SourceMode.WindowsOnly)
            yield break;

        // WSL の検出はディストリビューションを起動するため、Windows 側で取得できなかった場合にのみ行う
        foreach (var endpoint in await DetectWslAsync(ct).ConfigureAwait(false))
            yield return endpoint;
    }

    private static bool IsAllowed(ClaudeEndpoint endpoint, SourceMode mode) => mode switch
    {
        SourceMode.WindowsOnly => endpoint.Kind == EndpointKind.Windows,
        SourceMode.WslOnly => endpoint.Kind == EndpointKind.Wsl,
        _ => true,
    };

    /// <summary>Windows 側の claude を探す。設定値 → PATH → 既定のインストール先の順。</summary>
    public static string? FindWindowsClaude(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in pathDirs)
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // PATH に不正な文字を含むエントリがあっても無視する
                }
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(appData, "npm", "claude.cmd"),
        }.FirstOrDefault(File.Exists);
    }

    private async Task<IReadOnlyList<ClaudeEndpoint>> DetectWslAsync(CancellationToken ct)
    {
        if (_wslEndpoints is not null && DateTimeOffset.Now - _wslDetectedAt < WslDetectionTtl)
            return _wslEndpoints;

        var found = new List<ClaudeEndpoint>();
        if (Wsl.IsAvailable)
        {
            foreach (var distro in Wsl.ListDistros())
            {
                try
                {
                    var result = await Wsl.RunBashAsync(distro.Name,
                        "command -v claude || { test -x \"$HOME/.local/bin/claude\" && echo \"$HOME/.local/bin/claude\"; }",
                        CommandTimeout, ct).ConfigureAwait(false);
                    if (result.Succeeded && !string.IsNullOrWhiteSpace(result.StdOut))
                        found.Add(new ClaudeEndpoint(EndpointKind.Wsl, distro.Name));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Warn($"WSL ({distro.Name}) での Claude Code の検出に失敗しました: {ex.Message}");
                }
            }
        }

        AppLog.Info(found.Count == 0
            ? "WSL 側に Claude Code は見つかりませんでした。"
            : $"WSL 側の Claude Code: {string.Join(", ", found.Select(e => e.Target))}");
        _wslEndpoints = found;
        _wslDetectedAt = DateTimeOffset.Now;
        return found;
    }

    private static Task<ProcessResult> RunAsync(ClaudeEndpoint endpoint, string[] args, CancellationToken ct)
    {
        if (endpoint.Kind == EndpointKind.Windows)
            return ProcessRunner.RunAsync(endpoint.Target, args, CommandTimeout, ct, AppPaths.ProbeDir);

        var quotedArgs = string.Join(' ', args.Select(BashQuote));
        var script = $"c=$(command -v claude 2>/dev/null || echo \"$HOME/.local/bin/claude\"); exec \"$c\" {quotedArgs}";
        return Wsl.RunBashAsync(endpoint.Target, script, CommandTimeout, ct);
    }

    private static string BashQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "" : $" - {line}";
    }
}
