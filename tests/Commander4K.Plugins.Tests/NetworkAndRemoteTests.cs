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
    [InlineData("net://", null)]
    public void Network_parents(string path, string? expected) =>
        Assert.Equal(expected, new NetworkNeighborhoodVfsPlugin().GetParentPath(path));

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
}
