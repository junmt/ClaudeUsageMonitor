using System.Runtime.InteropServices;

namespace ClaudeUsageMonitor.Core.SystemInfo;

/// <summary>PC の CPU・メモリ・ストレージを読む。CPU 使用率は前回の呼び出しからの差分で求める。</summary>
public sealed class PcMonitor
{
    private ulong _lastIdle;
    private ulong _lastTotal;

    public PcSystemUsage Read(bool cpu, bool memory, bool storage, string? driveFilter) => new(
        CpuPercent: cpu ? SampleCpu() : null,
        ProcessorCount: Environment.ProcessorCount,
        Memory: memory ? ReadMemory() : null,
        Drives: storage ? ReadDrives(driveFilter) : []);

    /// <summary>前回の呼び出しから今までの CPU 使用率（%）。初回は null。</summary>
    public double? SampleCpu()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            return null;

        // カーネル時間にはアイドル時間が含まれる
        var idle = idleTime.ToUInt64();
        var total = kernelTime.ToUInt64() + userTime.ToUInt64();
        var hadPrevious = _lastTotal != 0;
        var idleDelta = idle - _lastIdle;
        var totalDelta = total - _lastTotal;
        _lastIdle = idle;
        _lastTotal = total;

        if (!hadPrevious || totalDelta == 0)
            return null;
        return Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }

    public static CapacityUsage? ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return null;
        var total = (long)status.TotalPhys;
        return new CapacityUsage(total, total - (long)status.AvailPhys);
    }

    /// <summary>
    /// ドライブの使用状況。filter（例: "C:, D:"）が空の場合は NTFS / ReFS の固定ドライブを対象にする
    /// （Google ドライブなどの仮想ドライブは FAT32 等として見えるため除外される）。
    /// </summary>
    public static IReadOnlyList<DriveUsage> ReadDrives(string? filter)
    {
        var wanted = ParseDriveFilter(filter);
        var result = new List<DriveUsage>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var letter = drive.Name.TrimEnd('\\').ToUpperInvariant();
                if (wanted.Count > 0 ? !wanted.Contains(letter) : !IsDefaultTarget(drive))
                    continue;
                if (!drive.IsReady)
                    continue;

                var usage = new CapacityUsage(drive.TotalSize, drive.TotalSize - drive.TotalFreeSpace);
                result.Add(new DriveUsage(letter, string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel, usage));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 取り外されたドライブなどは飛ばす
            }
        }
        return result;
    }

    internal static HashSet<string> ParseDriveFilter(string? filter) =>
        (filter ?? "")
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.TrimEnd('\\', ':').ToUpperInvariant() + ":")
            .Where(d => d.Length == 2 && char.IsAsciiLetter(d[0]))
            .ToHashSet();

    private static bool IsDefaultTarget(DriveInfo drive) =>
        drive.DriveType == DriveType.Fixed
        && drive.IsReady
        && drive.DriveFormat is "NTFS" or "ReFS";

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);
}
