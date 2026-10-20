namespace ClaudeUsageMonitor.Core.SystemInfo;

/// <summary>メモリやディスクの「使用量 / 総量」。</summary>
public sealed record CapacityUsage(long TotalBytes, long UsedBytes)
{
    public double Percent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
}

public sealed record DriveUsage(string Name, string? Label, CapacityUsage Usage);

/// <summary>PC のシステム情報。設定で無効にした項目は null（ドライブは空）。</summary>
public sealed record PcSystemUsage(
    double? CpuPercent,
    int ProcessorCount,
    CapacityUsage? Memory,
    IReadOnlyList<DriveUsage> Drives);

/// <summary>
/// WSL2 のシステム情報。WSL2 はすべてのディストリビューションで1つの VM を共有するため1つ分を読む。
/// </summary>
/// <param name="CpuPercent">前回の取得からの VM 内の CPU 使用率（初回は null）。</param>
/// <param name="Memory">VM 内（/proc/meminfo）の使用量と上限。</param>
/// <param name="Disk">ルートファイルシステム（df /）の使用量と容量。</param>
/// <param name="HostMemoryBytes">Windows 上で VM（vmmemWSL）が占めている物理メモリ。</param>
public sealed record WslSystemUsage(
    bool Running,
    string? Distro,
    double? CpuPercent,
    int? ProcessorCount,
    CapacityUsage? Memory,
    CapacityUsage? Disk,
    long? HostMemoryBytes);
