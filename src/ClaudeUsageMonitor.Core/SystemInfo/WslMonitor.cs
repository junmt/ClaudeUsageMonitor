using System.Diagnostics;
using System.Globalization;
using ClaudeUsageMonitor.Core.Cli;
using ClaudeUsageMonitor.Core.Infrastructure;

namespace ClaudeUsageMonitor.Core.SystemInfo;

/// <summary>
/// WSL2 の CPU・メモリ・ディスクを1回の wsl.exe 呼び出しで読む。
/// 実行中のディストリビューションが無ければ起動せずに停止中として返す。
/// </summary>
public sealed class WslMonitor
{
    private const string WslVmProcessName = "vmmemWSL";
    private const string Separator = "@@CUM@@";

    // /proc/meminfo、/proc/stat の1行目、nproc、df / をまとめて出力する
    private static readonly string Script =
        $"cat /proc/meminfo; echo {Separator}; head -n 1 /proc/stat; echo {Separator}; nproc; echo {Separator}; df -B1 -P /";

    private CpuTimes? _lastCpu;

    public async Task<WslSystemUsage> ReadAsync(CancellationToken ct)
    {
        var host = ReadVmHostMemory();
        if (!Wsl.IsAvailable)
            return Stopped(host);

        var running = await Wsl.ListRunningAsync(ct).ConfigureAwait(false);
        if (running.Count == 0)
        {
            _lastCpu = null;
            return Stopped(host);
        }

        // 既定のディストリビューションが動いていればそれを、無ければ動いているものを使う（VM は共通なので値は同じ）
        var distro = Wsl.ListDistros().Select(d => d.Name).FirstOrDefault(running.Contains) ?? running.First();
        try
        {
            var result = await ProcessRunner.RunAsync(Wsl.WslExe, ["-d", distro, "--exec", "sh", "-c", Script],
                TimeSpan.FromSeconds(15), ct, AppPaths.ProbeDir).ConfigureAwait(false);
            if (!result.Succeeded)
                return new WslSystemUsage(true, distro, null, null, null, null, host);

            var sections = result.StdOut.Replace("\r\n", "\n").Split(Separator);
            var memory = sections.Length > 0 ? ParseMeminfo(sections[0]) : null;
            var cpu = sections.Length > 1 ? ParseProcStat(sections[1]) : null;
            int? processors = sections.Length > 2 && int.TryParse(sections[2].Trim(), CultureInfo.InvariantCulture, out var n) ? n : null;
            var disk = sections.Length > 3 ? ParseDf(sections[3]) : null;

            double? cpuPercent = null;
            if (cpu is { } now && _lastCpu is { } prev && now.Total > prev.Total)
                cpuPercent = Math.Clamp(100.0 * (1 - (double)(now.Idle - prev.Idle) / (now.Total - prev.Total)), 0, 100);
            _lastCpu = cpu;

            return new WslSystemUsage(true, distro, cpuPercent, processors, memory, disk, host);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"WSL ({distro}) のシステム情報を取得できませんでした: {ex.Message}");
            return new WslSystemUsage(true, distro, null, null, null, null, host);
        }
    }

    private static WslSystemUsage Stopped(long? host) => new(false, null, null, null, null, null, host);

    /// <summary>/proc/meminfo の MemTotal と MemAvailable から使用量を求める。</summary>
    public static CapacityUsage? ParseMeminfo(string text)
    {
        long? total = null, available = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                continue;
            if (parts[0] == "MemTotal:")
                total = kb * 1024;
            else if (parts[0] == "MemAvailable:")
                available = kb * 1024;
        }
        return total is > 0 && available is not null ? new CapacityUsage(total.Value, total.Value - available.Value) : null;
    }

    public readonly record struct CpuTimes(ulong Idle, ulong Total);

    /// <summary>/proc/stat の "cpu" 行から累積のアイドル時間と合計時間を求める（idle + iowait をアイドルとする）。</summary>
    public static CpuTimes? ParseProcStat(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
            return null;

        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(v => ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0)
            .ToArray();
        if (values.Length < 4)
            return null;

        // user nice system idle iowait irq softirq steal（guest は user に含まれるため除く）
        var total = values.Take(8).Aggregate(0UL, (sum, v) => sum + v);
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return new CpuTimes(idle, total);
    }

    /// <summary>df -B1 -P の出力（ヘッダー行あり／なし）から容量と使用量を求める。</summary>
    public static CapacityUsage? ParseDf(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used)
                && size > 0)
                return new CapacityUsage(size, used);
        }
        return null;
    }

    private static long? ReadVmHostMemory()
    {
        var processes = Process.GetProcessesByName(WslVmProcessName);
        try
        {
            if (processes.Length == 0)
                return null;
            long sum = 0;
            foreach (var process in processes)
                sum += process.WorkingSet64;
            return sum;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}
