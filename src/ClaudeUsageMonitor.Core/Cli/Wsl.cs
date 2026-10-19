using ClaudeUsageMonitor.Core.Infrastructure;
using Microsoft.Win32;

namespace ClaudeUsageMonitor.Core.Cli;

public sealed record WslDistro(string Name, bool IsDefault);

/// <summary>WSL ディストリビューションの列挙と、ディストリビューション内でのコマンド実行。</summary>
public static class Wsl
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    // wsl.exe 自身のメッセージを UTF-8 で出力させる（未対応の古い WSL では UTF-16 のまま）
    private static readonly Dictionary<string, string> Environment = new() { ["WSL_UTF8"] = "1" };

    public static string WslExe { get; } =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "wsl.exe");

    public static bool IsAvailable => File.Exists(WslExe);

    /// <summary>
    /// インストール済みのディストリビューションをレジストリから列挙する（ディストリビューションを起動しない）。
    /// docker-desktop 系は除外し、既定のディストリビューションを先頭にする。
    /// </summary>
    public static IReadOnlyList<WslDistro> ListDistros()
    {
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(LxssKey);
            if (lxss is null)
                return [];

            var defaultId = lxss.GetValue("DefaultDistribution") as string;
            var result = new List<WslDistro>();
            foreach (var id in lxss.GetSubKeyNames())
            {
                using var key = lxss.OpenSubKey(id);
                if (key?.GetValue("DistributionName") is not string name || string.IsNullOrWhiteSpace(name))
                    continue;
                if (name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(new WslDistro(name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
            }

            return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"WSL ディストリビューションの列挙に失敗しました: {ex.Message}");
            return [];
        }
    }

    /// <summary>実行中のディストリビューション名の一覧。</summary>
    public static async Task<IReadOnlySet<string>> ListRunningAsync(CancellationToken ct)
    {
        if (!IsAvailable)
            return new HashSet<string>();
        try
        {
            var result = await ProcessRunner.RunAsync(WslExe, ["--list", "--running", "--quiet"], TimeSpan.FromSeconds(15), ct,
                environment: Environment).ConfigureAwait(false);
            return SplitLines(result.StdOut).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"実行中の WSL ディストリビューションの取得に失敗しました: {ex.Message}");
            return new HashSet<string>();
        }
    }

    /// <summary>
    /// ディストリビューション内で bash のログインシェルとしてスクリプトを実行する（停止中なら起動する）。
    /// --cd ~ は、作業ディレクトリが WSL から参照できないパス（Google ドライブ等）でも警告を出さないため。
    /// </summary>
    public static Task<ProcessResult> RunBashAsync(string distro, string script, TimeSpan timeout, CancellationToken ct) =>
        ProcessRunner.RunAsync(WslExe, ["-d", distro, "--cd", "~", "--exec", "bash", "-lc", script], timeout, ct,
            workingDirectory: AppPaths.ProbeDir, environment: Environment);

    /// <summary>ディストリビューションの既定ユーザーの HOME を取得する。</summary>
    public static async Task<string?> GetHomeAsync(string distro, CancellationToken ct)
    {
        var result = await RunBashAsync(distro, "printf '%s' \"$HOME\"", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        var home = CleanOutput(result.StdOut).Trim();
        return result.Succeeded && home.StartsWith('/') ? home : null;
    }

    /// <summary>Linux パスを Windows から参照できる UNC パスに変換する。</summary>
    public static string ToUncPath(string distro, string linuxPath) =>
        $@"\\wsl.localhost\{distro}{linuxPath.Replace('/', '\\')}";

    private static IEnumerable<string> SplitLines(string output) =>
        CleanOutput(output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // UTF-16 出力を UTF-8 として読んだ場合に混ざる NUL を取り除く
    private static string CleanOutput(string output) => output.Replace("\0", "").Replace("\uFEFF", "");
}
