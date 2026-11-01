using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
    [InlineData("net://NAS", "net://?lan")]
    [InlineData("net://?lan", "net://")]
    [InlineData("net://?remote", "net://")]
    [InlineData("net://", "local://")]
    public void Network_parents(string path, string? expected) =>
        Assert.Equal(expected, new NetworkNeighborhoodVfsPlugin().GetParentPath(path));

    [Theory]
    [InlineData("net://")]
    [InlineData("net://?lan")]
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
        var plugin = new NetworkNeighborhoodVfsPlugin(
            null,
            new LanScanner { Ports = [], ConnectTimeout = TimeSpan.FromSeconds(30), NeighbourConnectTimeout = TimeSpan.FromSeconds(30) },
            EmptyServiceBrowser.Instance)
        {
            DiscoveryBudget = TimeSpan.FromMilliseconds(800),
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var entries = new List<PluginSdk.VfsEntry>();
        await foreach (var entry in plugin.EnumerateAsync(NetworkNeighborhoodVfsPlugin.LanPath, TestToken))
        {
            entries.Add(entry);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Contains(entries, e => e.Path == NetworkNeighborhoodVfsPlugin.AddLanHostPath);
    }

    [Fact]
    public async Task Root_separates_lan_and_remote_groups()
    {
        var plugin = new NetworkNeighborhoodVfsPlugin();

        var entries = new List<PluginSdk.VfsEntry>();
        await foreach (var entry in plugin.EnumerateAsync(NetworkNeighborhoodVfsPlugin.RootPath, TestToken))
        {
            entries.Add(entry);
        }

        Assert.Equal([NetworkNeighborhoodVfsPlugin.LanPath, NetworkNeighborhoodVfsPlugin.RemotePath], entries.Select(e => e.Path));
        Assert.Equal("net://", plugin.GetParentPath(NetworkNeighborhoodVfsPlugin.LanPath));
        Assert.Equal("net://", plugin.GetParentPath(NetworkNeighborhoodVfsPlugin.RemotePath));
    }

    [Fact]
    public async Task Remote_group_lists_saved_connections_and_not_lan_hosts()
    {
        var plugin = new NetworkNeighborhoodVfsPlugin
        {
            SavedConnections = [new SavedConnection("Home SSH", "sftp://user@home.lan/")],
            LanHosts = [new Commander4K.Core.Configuration.LanHostSetting { Name = "MikroTik", Host = "192.168.1.1" }],
        };

        var entries = new List<PluginSdk.VfsEntry>();
        await foreach (var entry in plugin.EnumerateAsync(NetworkNeighborhoodVfsPlugin.RemotePath, TestToken))
        {
            entries.Add(entry);
        }

        Assert.Contains(entries, e => e.Name == "Home SSH" && e.Path == "sftp://user@home.lan/");
        Assert.DoesNotContain(entries, e => e.Name == "MikroTik");
    }

    [Fact]
    public async Task Lan_lists_manually_added_hosts_even_when_discovery_is_disabled()
    {
        var plugin = new NetworkNeighborhoodVfsPlugin
        {
            LanDiscoveryEnabled = false,
            LanHosts = [new Commander4K.Core.Configuration.LanHostSetting { Name = "MikroTik", Host = "192.168.1.1" }],
        };

        var entries = new List<PluginSdk.VfsEntry>();
        await foreach (var entry in plugin.EnumerateAsync(NetworkNeighborhoodVfsPlugin.LanPath, TestToken))
        {
            entries.Add(entry);
        }

        var host = Assert.Single(entries, e => e.Name == "MikroTik");
        Assert.Equal("net://192.168.1.1", host.Path);
        Assert.Contains("wyłączone", host.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(NetworkNeighborhoodVfsPlugin.AddLanHostPath, entries.Select(e => e.Path));
        Assert.Contains(NetworkNeighborhoodVfsPlugin.ScanLanPath, entries.Select(e => e.Path));
    }

    [Fact]
    public async Task Host_listing_includes_a_manually_added_web_service()
    {
        var plugin = new NetworkNeighborhoodVfsPlugin
        {
            LanDiscoveryEnabled = false,
            LanHosts =
            [
                new Commander4K.Core.Configuration.LanHostSetting
                {
                    Name = "Router",
                    Host = "192.168.1.1",
                    Services = [new Commander4K.Core.Configuration.LanHostServiceSetting { Name = "WWW", Port = 8080, Scheme = "http" }],
                },
            ],
        };

        var entries = new List<PluginSdk.VfsEntry>();
        await foreach (var entry in plugin.EnumerateAsync("net://192.168.1.1", TestToken))
        {
            entries.Add(entry);
        }

        var service = Assert.Single(entries, e => e.Name == "WWW");
        Assert.Equal("http://192.168.1.1:8080/", service.Path);
    }

    [Theory]
    [InlineData(80, "HTTP", "http://router.lan/")]
    [InlineData(443, "HTTPS", "https://router.lan/")]
    [InlineData(8080, "HTTP :8080", "http://router.lan:8080/")]
    public void Detected_web_ports_are_openable_endpoints(int port, string name, string path)
    {
        var entry = NetworkNeighborhoodVfsPlugin.ServiceEntry("router.lan", port);

        Assert.Equal(name, entry.Name);
        Assert.Equal(path, entry.Path);
        Assert.Equal(PluginSdk.VfsEntryKind.File, entry.Kind);
    }

    [Fact]
    public void Detected_non_web_ports_are_informational_service_entries()
    {
        var entry = NetworkNeighborhoodVfsPlugin.ServiceEntry("router.lan", 8291);

        Assert.Equal("MikroTik Winbox", entry.Name);
        Assert.Equal("service://router.lan:8291", entry.Path);
        Assert.Equal(PluginSdk.VfsEntryKind.Service, entry.Kind);
    }

    [Fact]
    public async Task Host_listing_rechecks_and_shows_all_ports_from_the_discovery_scan()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var plugin = new NetworkNeighborhoodVfsPlugin(
            null,
            new LanScanner { Ports = [port] },
            EmptyServiceBrowser.Instance);

        var entries = new List<PluginSdk.VfsEntry>();
        await foreach (var entry in plugin.EnumerateAsync("net://127.0.0.1", TestToken))
        {
            entries.Add(entry);
        }

        var service = Assert.Single(entries, entry => entry.Kind == PluginSdk.VfsEntryKind.Service);
        Assert.Equal(port.ToString(), service.Name);
        Assert.Equal($"service://127.0.0.1:{port}", service.Path);
    }

    [Fact]
    public void Common_service_ports_include_mikrotik_winbox_and_api()
    {
        Assert.Contains(80, NetworkNeighborhoodVfsPlugin.CommonServicePorts);
        Assert.Contains(443, NetworkNeighborhoodVfsPlugin.CommonServicePorts);
        Assert.Contains(8291, NetworkNeighborhoodVfsPlugin.CommonServicePorts);
        Assert.Contains(8728, NetworkNeighborhoodVfsPlugin.CommonServicePorts);
        Assert.Contains(8729, NetworkNeighborhoodVfsPlugin.CommonServicePorts);
    }

    [Fact]
    public async Task Lan_lists_advertised_services_and_host_opens_advertised_web_endpoint()
    {
        var service = new AdvertisedNetworkService(
            "panel",
            "_http._tcp.local.",
            "server.local",
            8080,
            IPAddress.Parse("192.168.1.50"),
            new Dictionary<string, string> { ["path"] = "/api" });
        var plugin = new NetworkNeighborhoodVfsPlugin(
            null,
            new LanScanner { Ports = [] },
            new FixedServiceBrowser([service]))
        {
            DiscoveryBudget = TimeSpan.FromSeconds(1),
        };

        var root = new List<PluginSdk.VfsEntry>();
        await foreach (var entry in plugin.EnumerateAsync(NetworkNeighborhoodVfsPlugin.LanPath, TestToken))
        {
            root.Add(entry);
        }

        var host = Assert.Single(root, e => e.Path == "net://192.168.1.50");
        Assert.Contains("HTTP :8080", host.Description, StringComparison.Ordinal);

        var endpoint = new List<PluginSdk.VfsEntry>();
        await foreach (var entry in plugin.EnumerateAsync(host.Path, TestToken))
        {
            endpoint.Add(entry);
            break;
        }

        var web = Assert.Single(endpoint);
        Assert.Equal("http://192.168.1.50:8080/api", web.Path);
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

    [Fact]
    public void Dns_sd_response_exposes_web_service_and_advertised_api_path()
    {
        var services = MdnsServiceBrowser.ParseResponses([HttpServiceResponse()], ["_http._tcp.local."]);

        var service = Assert.Single(services);
        Assert.Equal("panel", service.Instance);
        Assert.Equal("server.local", service.HostName);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), service.Address);
        Assert.Equal(8080, service.Port);
        Assert.Equal("/api", service.Text["path"]);
        Assert.Equal("http://192.168.1.50:8080/api", MdnsServiceBrowser.WebUrlFor(service));
    }

    [Fact]
    public void Non_web_dns_sd_services_are_not_opened_as_web_pages()
    {
        var service = new AdvertisedNetworkService(
            "share",
            "_smb._tcp.local.",
            "nas.local",
            445,
            IPAddress.Parse("192.168.1.20"),
            new Dictionary<string, string>());

        Assert.Null(MdnsServiceBrowser.WebUrlFor(service));
    }

    [Fact]
    public void Api_service_uses_its_advertised_tls_setting_and_path()
    {
        var service = new AdvertisedNetworkService(
            "api",
            "_api._tcp.local.",
            "api.local",
            8443,
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tls"] = "TRUE",
                ["api"] = "/v1/health",
            });

        Assert.Equal("https://api.local:8443/v1/health", MdnsServiceBrowser.WebUrlFor(service));
    }

    [Fact]
    public void Dns_sd_meta_query_discovers_other_announced_service_types()
    {
        var services = MdnsServiceBrowser.ParseResponses([CustomServiceResponse()]);

        var service = Assert.Single(services);
        Assert.Equal("_custom._tcp.local.", service.ServiceType);
        Assert.Equal("custom", service.Instance);
        Assert.Equal(1234, service.Port);
    }

    private static byte[] HttpServiceResponse()
    {
        using var packet = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], 4);
        packet.Write(header);

        var instance = "panel._http._tcp.local.";
        WriteRecord(packet, "_http._tcp.local.", 12, EncodeName(instance));

        using var srv = new MemoryStream();
        WriteUInt16(srv, 0);
        WriteUInt16(srv, 0);
        WriteUInt16(srv, 8080);
        srv.Write(EncodeName("server.local."));
        WriteRecord(packet, instance, 33, srv.ToArray());

        var txt = Encoding.UTF8.GetBytes("path=/api");
        WriteRecord(packet, instance, 16, [(byte)txt.Length, .. txt]);
        WriteRecord(packet, "server.local.", 1, [192, 168, 1, 50]);
        return packet.ToArray();
    }

    private static byte[] CustomServiceResponse()
    {
        using var packet = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], 4);
        packet.Write(header);

        WriteRecord(packet, "_services._dns-sd._udp.local.", 12, EncodeName("_custom._tcp.local."));
        WriteRecord(packet, "_custom._tcp.local.", 12, EncodeName("custom._custom._tcp.local."));

        using var srv = new MemoryStream();
        WriteUInt16(srv, 0);
        WriteUInt16(srv, 0);
        WriteUInt16(srv, 1234);
        srv.Write(EncodeName("server.local."));
        WriteRecord(packet, "custom._custom._tcp.local.", 33, srv.ToArray());
        WriteRecord(packet, "server.local.", 1, [192, 168, 1, 51]);
        return packet.ToArray();
    }

    private static void WriteRecord(Stream packet, string name, ushort type, byte[] data)
    {
        packet.Write(EncodeName(name));
        WriteUInt16(packet, type);
        WriteUInt16(packet, 1);
        WriteUInt32(packet, 120);
        WriteUInt16(packet, (ushort)data.Length);
        packet.Write(data);
    }

    private static byte[] EncodeName(string name)
    {
        using var encoded = new MemoryStream();
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            encoded.WriteByte((byte)bytes.Length);
            encoded.Write(bytes);
        }

        encoded.WriteByte(0);
        return encoded.ToArray();
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed class FixedServiceBrowser(IReadOnlyList<AdvertisedNetworkService> services) : IAdvertisedServiceBrowser
    {
        public Task<IReadOnlyList<AdvertisedNetworkService>> BrowseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(services);
    }

    private sealed class EmptyServiceBrowser : IAdvertisedServiceBrowser
    {
        public static EmptyServiceBrowser Instance { get; } = new();

        public Task<IReadOnlyList<AdvertisedNetworkService>> BrowseAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AdvertisedNetworkService>>([]);
    }
}
