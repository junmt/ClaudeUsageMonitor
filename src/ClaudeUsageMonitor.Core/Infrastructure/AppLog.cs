namespace ClaudeUsageMonitor.Core.Infrastructure;

/// <summary>%LOCALAPPDATA%\ClaudeUsageMonitor\logs への簡易ファイルログ（7日ローテーション）。</summary>
public static class AppLog
{
    private const int RetentionDays = 7;
    private static readonly Lock Gate = new();
    private static bool _cleaned;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                if (!_cleaned)
                {
                    _cleaned = true;
                    DeleteOldFiles();
                }

                var file = Path.Combine(AppPaths.LogsDir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ログ出力の失敗でアプリを止めない
        }
    }

    private static void DeleteOldFiles()
    {
        var limit = DateTime.Now.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(AppPaths.LogsDir, "app-*.log"))
        {
            if (File.GetLastWriteTime(file) < limit)
                File.Delete(file);
        }
    }
}
