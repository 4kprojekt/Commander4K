using Commander4K.Plugins.BuiltIn.Remote;

namespace Commander4K.Plugins.Tests;

public sealed class SshMonitorTests
{
    private const string Linux = """
        @@uname
        Linux
        @@loadavg
        0.52 0.41 0.30 2/412 98765
        @@stat
        cpu  1000 50 500 8000 200 0 30 0 0 0
        @@meminfo
        MemTotal:       16303916 kB
        MemAvailable:    9123456 kB
        @@df
        Filesystem     1024-blocks      Used Available Capacity Mounted on
        /dev/sda1        102400000  51200000  51200000      50% /
        /dev/sdb1        204800000 184320000  20480000      90% /mnt/big data
        /dev/sdc1          1024000    102400    921600      10% /boot
        /dev/sdd1           512000     51200    460800      10% /boot/efi
        @@uptime
        93784.12 350000.00
        """;

    [Fact]
    public void Parses_linux_sample()
    {
        var stats = SshStatsParser.Parse(Linux);

        Assert.Equal("Linux", stats.System);
        Assert.Equal([0.52, 0.41, 0.30], stats.LoadAverage);
        Assert.Equal([1000L, 50, 500, 8000, 200, 0, 30, 0, 0, 0], stats.CpuTicks!);
        Assert.Equal(16303916, stats.MemTotalKb);
        Assert.Equal(9123456, stats.MemAvailableKb);
        Assert.Equal(4, stats.Disks.Count);
        Assert.Equal(("/mnt/big data", 204800000L, 184320000L), stats.Disks[1]);
        Assert.Equal(TimeSpan.FromSeconds(93784.12), stats.Uptime);
    }

    [Fact]
    public void Non_linux_sample_gives_system_only()
    {
        var stats = SshStatsParser.Parse("@@uname\nFreeBSD\n@@loadavg\ncat: /proc/loadavg: No such file or directory\n@@stat\n@@meminfo\n@@df\n@@uptime\n");

        Assert.Equal("FreeBSD", stats.System);
        Assert.False(stats.IsLinux);
        Assert.Empty(stats.LoadAverage);
        Assert.Null(stats.CpuTicks);
        Assert.Empty(stats.Disks);
    }

    [Fact]
    public void Missing_proc_sections_do_not_throw()
    {
        var stats = SshStatsParser.Parse("garbage\n@@uname\nLinux\n@@df\nnot a table\n");

        Assert.Equal("Linux", stats.System);
        Assert.Equal(0, stats.MemTotalKb);
        Assert.Empty(stats.Disks);
        Assert.Equal(TimeSpan.Zero, stats.Uptime);
    }

    /// <summary>Against <c>C4K_TEST_SFTP</c> (see <see cref="RemoteServerTests"/>); a server that refuses exec still gives a card, never an exception.</summary>
    [Fact]
    public async Task Ssh_monitor_reads_a_real_server()
    {
        var server = Environment.GetEnvironmentVariable("C4K_TEST_SFTP");
        Assert.SkipWhen(string.IsNullOrEmpty(server), "C4K_TEST_SFTP is not set");
        var url = new Uri(server!);
        var user = url.UserInfo.Split(':', 2);
        var remote = new RemoteTransferVfsPlugin();
        remote.SetCredentials($"sftp://{user[0]}@{url.Host}:{url.Port}", new System.Net.NetworkCredential(user[0], Uri.UnescapeDataString(user[1])));
        await using var audit = new Commander4K.Core.Plugins.AuditLog(null);
        await using var monitor = new SshMonitor(remote, audit);

        var card = await monitor.LoadCardAsync($"sftp://{user[0]}@{url.Host}:{url.Port}/", null, null, TestContext.Current.CancellationToken);

        Assert.NotEqual(Commander4K.PluginSdk.CardStatus.None, card.Status);
        Assert.Contains(card.Actions, a => a.Target == Commander4K.PluginSdk.CardActions.Refresh);
    }

    private static SshStats Reading(params long[] ticks) =>
        SshStatsParser.Parse($"@@uname\nLinux\n@@loadavg\n0.10 0.20 0.30 1/1 1\n@@stat\ncpu  {string.Join(' ', ticks)}\n");

    [Fact]
    public async Task Cpu_is_measured_against_the_same_cards_previous_reading()
    {
        await using var audit = new Commander4K.Core.Plugins.AuditLog(null);
        await using var monitor = new SshMonitor(new RemoteTransferVfsPlugin(), audit);
        long[] t0 = [100, 0, 100, 800, 0], t1 = [500, 0, 400, 900, 0], t2 = [600, 0, 200, 1000, 0];

        var first = monitor.Describe(Reading(t0), null, []);
        monitor.Describe(Reading(t1), null, []); // another dashboard's card for the same server
        var second = monitor.Describe(Reading(t2), first, []);

        Assert.DoesNotContain(first.Items.OfType<Commander4K.PluginSdk.CardValue>(), v => v.Label == "CPU");
        var expected = SshStatsParser.CpuPercent(t0, t2)!.Value;
        Assert.Equal($"{expected:0} %", second.Items.OfType<Commander4K.PluginSdk.CardValue>().Single(v => v.Label == "CPU").Value);
    }

    [Fact]
    public async Task Release_keeps_the_session_while_another_dashboard_holds_it()
    {
        await using var audit = new Commander4K.Core.Plugins.AuditLog(null);
        await using var monitor = new SshMonitor(new RemoteTransferVfsPlugin(), audit);
        const string Path = "sftp://kamil@vps/";

        monitor.Retain(Path);
        monitor.Retain(Path);
        await monitor.ReleaseAsync(Path);
        Assert.Equal(1, monitor.Holders(Path));

        await monitor.ReleaseAsync(Path);
        Assert.Equal(0, monitor.Holders(Path));
    }

    [Fact]
    public void Cpu_percent_from_two_samples()
    {
        long[] before = [1000, 50, 500, 8000, 200, 0, 30, 0];
        long[] after = [1300, 50, 600, 8500, 200, 0, 30, 0];

        // busy grew by 400 of 900 ticks; idle and iowait are not busy.
        Assert.Equal(400.0 / 900 * 100, SshStatsParser.CpuPercent(before, after)!.Value, 3);
        Assert.Null(SshStatsParser.CpuPercent(after, after));
    }
}
