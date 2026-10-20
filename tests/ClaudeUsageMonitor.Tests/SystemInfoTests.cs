using ClaudeUsageMonitor.Core.SystemInfo;

namespace ClaudeUsageMonitor.Tests;

public class SystemInfoTests
{
    [Fact]
    public void ParsesProcMeminfo()
    {
        // 2026-10-20 に WSL2 (Ubuntu) で得た /proc/meminfo の一部
        const string meminfo = """
            MemTotal:       32865936 kB
            MemFree:         1234567 kB
            MemAvailable:   13664572 kB
            SwapTotal:      16777216 kB
            """;

        var usage = WslMonitor.ParseMeminfo(meminfo);

        Assert.NotNull(usage);
        Assert.Equal(32865936L * 1024, usage.TotalBytes);
        Assert.Equal((32865936L - 13664572L) * 1024, usage.UsedBytes);
        Assert.InRange(usage.Percent, 58.4, 58.5);
    }

    [Fact]
    public void ReturnsNullWhenMemAvailableIsMissing() =>
        Assert.Null(WslMonitor.ParseMeminfo("MemTotal: 1000 kB\n"));

    [Fact]
    public void ParsesProcStat()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice
        var times = WslMonitor.ParseProcStat("cpu  20438101 2239 10170557 882212138 1163080 0 2712563 0 0 0\n");

        Assert.NotNull(times);
        Assert.Equal(882212138UL + 1163080UL, times.Value.Idle);
        Assert.Equal(20438101UL + 2239 + 10170557 + 882212138 + 1163080 + 0 + 2712563 + 0, times.Value.Total);
    }

    [Fact]
    public void ParsesDfOutput()
    {
        // 2026-10-20 に WSL2 (Ubuntu) で得た df -B1 -P / の出力
        const string df = """
            Filesystem        1-blocks         Used    Available Capacity Mounted on
            /dev/sdd       1081101176832 286934966272 739173855232      28% /
            """;

        var usage = WslMonitor.ParseDf(df);

        Assert.NotNull(usage);
        Assert.Equal(1081101176832, usage.TotalBytes);
        Assert.Equal(286934966272, usage.UsedBytes);
    }

    [Theory]
    [InlineData("C:, D:", new[] { "C:", "D:" })]
    [InlineData("c d", new[] { "C:", "D:" })]
    [InlineData(@"C:\;E", new[] { "C:", "E:" })]
    [InlineData("", new string[0])]
    public void ParsesDriveFilter(string filter, string[] expected) =>
        Assert.Equal(expected.ToHashSet(), PcMonitor.ParseDriveFilter(filter));

    [Fact]
    public void ReadsPcMemoryAndCpu()
    {
        var memory = PcMonitor.ReadMemory();
        Assert.NotNull(memory);
        Assert.InRange(memory.UsedBytes, 1, memory.TotalBytes);

        var monitor = new PcMonitor();
        Assert.Null(monitor.SampleCpu()); // 初回は差分が無い
        Thread.Sleep(200);
        Assert.InRange(monitor.SampleCpu() ?? 0, 0, 100);
    }

    [Fact]
    public void ReadsSystemDrive()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        var drives = PcMonitor.ReadDrives(system);
        var drive = Assert.Single(drives);
        Assert.Equal(system.ToUpperInvariant(), drive.Name);
        Assert.True(drive.Usage.TotalBytes > 0);
    }
}
