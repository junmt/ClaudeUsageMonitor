namespace ClaudeUsageMonitor.Core.Settings;

public enum SourceMode
{
    /// <summary>Windows → WSL の順に試す。</summary>
    Auto,
    WindowsOnly,
    WslOnly,
}

public enum ColorTheme
{
    System,
    Light,
    Dark,
}

public enum TopmostMode
{
    AlwaysOnTop,
    Normal,
    Bottom,
}

/// <summary>%APPDATA%\ClaudeUsageMonitor\settings.json に保存する設定。</summary>
public sealed class AppSettings
{
    // 一般
    public bool AutoStart { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
    public bool NotifyOnReset { get; set; }

    // 利用枠の取得
    public string? ClaudePath { get; set; }
    public SourceMode Source { get; set; } = SourceMode.Auto;
    public int RefreshIntervalMinutes { get; set; } = 3;

    // ログ集計
    public List<string> ExtraLogDirectories { get; set; } = [];

    /// <summary>WSL ディストリビューションごとのログ監視の有効/無効。未登録は有効扱い。</summary>
    public Dictionary<string, bool> WslDistros { get; set; } = [];

    public bool StartStoppedWslForLogs { get; set; }

    /// <summary>ディストリビューションごとの HOME のキャッシュ（停止中でもパスを組み立てられるように）。</summary>
    public Dictionary<string, string> WslHomes { get; set; } = [];

    // 表示
    public int WarningThreshold { get; set; } = 70;
    public int CriticalThreshold { get; set; } = 90;
    public bool ShowEmail { get; set; } = true;

    // システム情報（オプション）
    public bool ShowPcCpu { get; set; }
    public bool ShowPcMemory { get; set; }
    public bool ShowPcStorage { get; set; }

    /// <summary>表示するドライブ（例: "C:, D:"）。空なら NTFS / ReFS の固定ドライブすべて。</summary>
    public string? StorageDrives { get; set; }

    public bool ShowWslCpu { get; set; }
    public bool ShowWslMemory { get; set; }
    public bool ShowWslStorage { get; set; }

    public bool AnyPcSystemInfo => ShowPcCpu || ShowPcMemory || ShowPcStorage;
    public bool AnyWslSystemInfo => ShowWslCpu || ShowWslMemory || ShowWslStorage;
    public ColorTheme Theme { get; set; } = ColorTheme.System;
    public int OpacityPercent { get; set; } = 90;
    public TopmostMode Topmost { get; set; } = TopmostMode.AlwaysOnTop;
    public bool ClickThrough { get; set; }

    // ウィジェットの状態
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool DetailMode { get; set; }
    public bool WidgetVisible { get; set; } = true;

    public bool IsWslDistroEnabled(string distro) =>
        !WslDistros.TryGetValue(distro, out var enabled) || enabled;

    public void Normalize()
    {
        RefreshIntervalMinutes = Math.Clamp(RefreshIntervalMinutes, 1, 30);
        WarningThreshold = Math.Clamp(WarningThreshold, 1, 100);
        CriticalThreshold = Math.Clamp(CriticalThreshold, WarningThreshold, 100);
        OpacityPercent = Math.Clamp(OpacityPercent, 30, 100);
        ExtraLogDirectories = ExtraLogDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct().ToList();
    }
}
