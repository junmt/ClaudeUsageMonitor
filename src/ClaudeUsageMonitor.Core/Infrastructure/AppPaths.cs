namespace ClaudeUsageMonitor.Core.Infrastructure;

public static class AppPaths
{
    private const string AppName = "ClaudeUsageMonitor";

    public static string RoamingDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string LocalDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");
    public static string StateFile => Path.Combine(LocalDir, "state.json");
    public static string DatabaseFile => Path.Combine(LocalDir, "usage.db");
    public static string RateLimitCacheFile => Path.Combine(LocalDir, "ratelimit.json");
    public static string LogsDir => Path.Combine(LocalDir, "logs");

    /// <summary>claude 実行時の作業ディレクトリ。</summary>
    public static string ProbeDir => Path.Combine(LocalDir, "probe");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RoamingDir);
        Directory.CreateDirectory(LocalDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ProbeDir);
    }
}
