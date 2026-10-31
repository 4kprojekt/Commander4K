using System.Net;
using System.Net.Sockets;
using Commander4K.Plugins.BuiltIn.Network;
using Commander4K.Plugins.BuiltIn.Remote;

namespace Commander4K.Plugins.Tests;

public sealed class NetworkAndRemoteTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("net://", "Root", "", "")]
    [InlineData("net://NAS", "Host", "NAS", @"\\NAS")]
    [InlineData(@"\\NAS", "Host", "NAS", @"\\NAS")]
    [InlineData("net://NAS/media/movies", "Share", "NAS", @"\\NAS\media\movies")]
    [InlineData(@"\\?\UNC\NAS\media", "Share", "NAS", @"\\NAS\media")]
    [InlineData("smb://192.168.1.10/share", "Share", "192.168.1.10", @"\\192.168.1.10\share")]
    public void Parses_network_paths(string path, string level, string host, string unc) =>
        Assert.Equal(new NetworkPath(Enum.Parse<NetworkPathLevel>(level), host, unc), NetworkPath.Parse(path));

    [Theory]
    [InlineData(@"\\NAS\media\movies\2026", @"\\NAS\media\movies")]
    [InlineData(@"\\NAS\media", "net://NAS")]
    [InlineData("net://NAS", "net://")]
    [InlineData("net://", "local://")]
    public void Network_parents(string path, string? expected) =>
        Assert.Equal(expected, new NetworkNeighborhoodVfsPlugin().GetParentPath(path));

    [Theory]
    [InlineData("net://")]
    [InlineData("net://NAS")]
    public void Virtual_network_levels_are_watched_without_throwing(string path)
    {
        using var watch = new NetworkNeighborhoodVfsPlugin().Watch(path, () => { });
        Assert.NotNull(watch);
    }

    [Fact]
    public void Share_root_is_server_and_share() =>
        Assert.Equal(@"\\NAS\media", NetworkPath.Parse(@"\\NAS\media\a\b").ShareRoot);

    [Fact]
    public void Remote_endpoint_keeps_special_characters_in_paths()
    {
        var endpoint = RemoteEndpoint.Parse("sftp://kamil:p%40ss@host.lan:2222/home/my dir/#notes?.txt");

        Assert.Equal(new RemoteEndpoint("sftp", "kamil", "p@ss", "host.lan", 2222, "/home/my dir/#notes?.txt"), endpoint);
        Assert.Equal("sftp://kamil@host.lan:2222/x", endpoint.ToPath("/x"));
    }

    [Theory]
    [InlineData("ftp://host", "net://host")]
    [InlineData("ftp://host/", "net://host")]
    [InlineData("sftp://me@host/home/me", "sftp://me@host/home")]
    [InlineData("sftp://me@host/home", "sftp://me@host/")]
    public void Remote_parents(string path, string expected) =>
        Assert.Equal(expected, new RemoteTransferVfsPlugin().GetParentPath(path));

    [Theory]
    [InlineData("sftp://demo@127.0.0.1:2222/config", "demo")]
    [InlineData("ftp://user:pass@host/ftp", "user")]
    [InlineData("sftp://host/srv", null)]
    [InlineData(@"C:\Users", null)]
    public void User_names_come_from_remote_paths(string path, string? expected) =>
        Assert.Equal(expected, RemoteTransferVfsPlugin.UserNameOf(path));

    [Fact]
    public async Task Probe_detects_open_and_closed_ports()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var open = ((IPEndPoint)listener.LocalEndpoint).Port;

        int closed;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var result = await new LanScanner().ProbeHostAsync(IPAddress.Loopback, [open, closed], TestToken);

        Assert.Equal([open], result);
    }

    [Fact]
    public async Task Cancelled_scan_probes_report_closed_instead_of_throwing()
    {
        // Leaving "This PC" cancels discovery; probes must not throw out of Parallel.ForEachAsync's body (a debugger
        // would stop there as "user-unhandled" on every navigation).
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await new LanScanner().ProbeHostAsync(IPAddress.Parse("192.0.2.1"), [445, 22], cancelled.Token);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Root_listing_ends_within_the_discovery_budget_however_slow_the_probes_are()
    {
        // A probe that never answered once kept net:// (and "This PC") loading for good; the budget ends it.
        var plugin = new NetworkNeighborhoodVfsPlugin(scanner: new LanScanner { ConnectTimeout = TimeSpan.FromSeconds(30), NeighbourConnectTimeout = TimeSpan.FromSeconds(30) })
        {
            DiscoveryBudget = TimeSpan.FromMilliseconds(800),
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var entries = new List<PluginSdk.VfsEntry>();
        await foreach (var entry in plugin.EnumerateAsync(NetworkNeighborhoodVfsPlugin.RootPath, TestToken))
        {
            entries.Add(entry);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Contains(entries, e => e.Kind == PluginSdk.VfsEntryKind.Action);
    }

    [Fact]
    public async Task Cancelled_scan_ends_with_cancellation_at_the_consumer_only()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var scanner = new LanScanner { ConnectTimeout = TimeSpan.FromSeconds(2) };

        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var host in scanner.ScanAsync(cts.Token))
            {
                _ = host;
            }
        });

        Assert.True(error is null or OperationCanceledException, error?.ToString());
    }
}
