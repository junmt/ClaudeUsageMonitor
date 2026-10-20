using ClaudeUsageMonitor.Core.Infrastructure;
using Microsoft.Win32;

namespace ClaudeUsageMonitor.Services;

/// <summary>HKCU の Run キーによる Windows 起動時の自動起動。</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeUsageMonitor";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\"");
            else if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName);
        }
        catch (Exception ex)
        {
            AppLog.Error("自動起動の設定に失敗しました", ex);
        }
    }
}
