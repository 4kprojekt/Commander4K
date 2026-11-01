using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Commander4K.Core.Configuration;
using Commander4K.Core.Execution;
using Commander4K.Core.Indexing;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Interop;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.BuiltIn.Network;

/// <param name="Path">Target opened on Enter, e.g. <c>\\nas\media</c> or <c>sftp://me@host</c>.</param>
public sealed record SavedConnection(string Name, string Path, string? Description = null);

/// <param name="Display">Label shown in the panel.</param>
/// <param name="HostName">Name used in the host's path; must resolve (e.g. the full DNS name).</param>
internal delegate void HostSink(string Display, string HostName, IPAddress? Address, IReadOnlyList<int> Ports, VfsEntryFlags Flags, string? ServiceDescription = null);

/// <summary>
/// Network neighbourhood (<c>net://</c>) and SMB shares (UNC / <c>smb://</c>). The root separates LAN devices from
/// saved Remote connections; the LAN listing streams cached hosts and live discoveries, while host level lists shares
/// and service endpoints and share level streams files with an offline SQLite cache.
/// </summary>
public sealed class NetworkNeighborhoodVfsPlugin : IVfsProviderPlugin, IVfsChangeNotifier
{
    public const string RootPath = NetworkPath.RootPath;
    public const string LanPath = "net://?lan";
    public const string RemotePath = "net://?remote";
    public const string AddLanHostPath = "net://?add-host";
    public const string ScanLanPath = "net://?scan-lan";

    public static IReadOnlyList<int> CommonServicePorts { get; } =
        [21, 22, 23, 53, 80, 81, 139, 443, 445, 554, 1883, 3000, 3306, 3389, 5000, 5001, 8000, 8080, 8081, 8291, 8443, 8728, 8729, 9000, 9090, 9100, 32400];

    /// <summary>Listings larger than this are not cached for offline browsing.</summary>
    private const int MaxCachedListing = 20_000;

    private static readonly TimeSpan BrowserEnumerationTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The longest the LAN listing stays "loading": discovery that has not finished by then is abandoned, so a stuck
    /// source can never keep the panel busy. Hosts found by then are cached and appear at once on the next visit.
    /// </summary>
    public TimeSpan DiscoveryBudget { get; init; } = TimeSpan.FromSeconds(6);
    private static readonly ResilientAsyncExecutor BlockingCalls = new();

    private readonly SqliteIndexService? _index;
    private readonly LanScanner _scanner;
    private readonly IAdvertisedServiceBrowser _serviceBrowser;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, AdvertisedNetworkService>> _advertisedServices = new(StringComparer.OrdinalIgnoreCase);

    public NetworkNeighborhoodVfsPlugin(SqliteIndexService? index = null, LanScanner? scanner = null)
        : this(index, scanner, new MdnsServiceBrowser())
    {
    }

    internal NetworkNeighborhoodVfsPlugin(SqliteIndexService? index, LanScanner? scanner, IAdvertisedServiceBrowser serviceBrowser)
    {
        _index = index;
        _scanner = scanner ?? new LanScanner();
        _serviceBrowser = serviceBrowser;
    }

    public string Id => "builtin.network";

    public string DisplayName => "Network";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.network", Loc.T("Sieć"), Loc.T("Hosty LAN, zapisane połączenia Remote i usługi sieciowe."), PluginPermissions.FileRead, PluginPermissions.FileWrite, PluginPermissions.Network);

    public IReadOnlyList<string> Schemes { get; } = ["net", VfsPath.SmbScheme];

    public VfsCapabilities Capabilities => VfsCapabilities.Read | VfsCapabilities.Write | VfsCapabilities.CreateDirectory
        | VfsCapabilities.Delete | VfsCapabilities.Move | VfsCapabilities.Watch;

    /// <summary>When off, the LAN group shows saved and cached hosts without probing or browsing mDNS.</summary>
    public bool LanDiscoveryEnabled { get; set; } = true;

    /// <summary>Favourite servers and shares shown in the Remote group.</summary>
    public IReadOnlyList<SavedConnection> SavedConnections { get; set; } = [];

    /// <summary>Hosts explicitly added to the LAN list.</summary>
    public IReadOnlyList<LanHostSetting> LanHosts { get; set; } = [];

    /// <summary>Whether to check the extended common TCP port list during LAN discovery.</summary>
    public bool ScanPopularPorts { get; set; }

    public IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, CancellationToken cancellationToken)
    {
        if (string.Equals(path, LanPath, StringComparison.OrdinalIgnoreCase))
        {
            return EnumerateLanAsync(cancellationToken);
        }

        if (string.Equals(path, RemotePath, StringComparison.OrdinalIgnoreCase))
        {
            return EnumerateRemoteAsync(cancellationToken);
        }

        var parsed = NetworkPath.Parse(path);
        return parsed.Level switch
        {
            NetworkPathLevel.Root => EnumerateRootAsync(cancellationToken),
            NetworkPathLevel.Host => EnumerateHostAsync(parsed.Host, cancellationToken),
            _ => EnumerateShareAsync(parsed, cancellationToken),
        };
    }

    public ValueTask<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        if (string.Equals(path, LanPath, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<VfsEntry?>(new VfsEntry(Loc.T("LAN"), LanPath, VfsEntryKind.Directory,
                Description: Loc.T("Wykryte i ręcznie dodane urządzenia w sieci lokalnej.")));
        }

        if (string.Equals(path, RemotePath, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<VfsEntry?>(new VfsEntry(Loc.T("Remote"), RemotePath, VfsEntryKind.Directory,
                Description: Loc.T("Zapisane połączenia i lokalizacje sieciowe Windows.")));
        }

        var parsed = NetworkPath.Parse(path);
        VfsEntry? entry = parsed.Level switch
        {
            NetworkPathLevel.Root => new VfsEntry(DisplayName, NetworkPath.RootPath, VfsEntryKind.Directory),
            NetworkPathLevel.Host => new VfsEntry(parsed.Host, NetworkPath.HostPath(parsed.Host), VfsEntryKind.Host),
            _ => GetShareEntry(parsed.Unc),
        };
        return ValueTask.FromResult(entry);
    }

    public string? GetParentPath(string path)
    {
        if (string.Equals(path, LanPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, RemotePath, StringComparison.OrdinalIgnoreCase))
        {
            return RootPath;
        }

        var parsed = NetworkPath.Parse(path);
        switch (parsed.Level)
        {
            case NetworkPathLevel.Root:
                return LocalFileSystemPlugin.RootPath; // "This PC", which lists net:// next to the drives.
            case NetworkPathLevel.Host:
                return LanPath;
        }

        return string.Equals(parsed.ShareRoot, parsed.Unc, StringComparison.OrdinalIgnoreCase)
            ? NetworkPath.HostPath(parsed.Host)
            : Path.GetDirectoryName(parsed.Unc);
    }

    public string Combine(string directoryPath, string name)
    {
        var parsed = NetworkPath.Parse(directoryPath);
        return parsed.Level switch
        {
            NetworkPathLevel.Root => NetworkPath.HostPath(name),
            NetworkPathLevel.Host => $@"\\{parsed.Host}\{name}",
            _ => Path.Join(parsed.Unc, name),
        };
    }

    /// <summary>
    /// Grants this logon session access to a share (the login dialog's "OK"). Blocking: call through the
    /// resilient executor.
    /// </summary>
    /// <exception cref="Win32Exception">Wrong credentials or unreachable server.</exception>
    public void Connect(string sharePath, string? userName, string? password) =>
        Win32Network.Connect(NetworkPath.Parse(sharePath).ShareRoot, userName, password);

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new FileStream(RequireShare(path), new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        }));

    public ValueTask<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new FileStream(RequireShare(path), new FileStreamOptions
        {
            Mode = overwrite ? FileMode.Create : FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        }));

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RequireShare(path));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var unc = RequireShare(path);
        if (Directory.Exists(unc))
        {
            Directory.Delete(unc, recursive);
        }
        else
        {
            File.Delete(unc);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        var source = RequireShare(sourcePath);
        var destination = RequireShare(destinationPath);
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Only shares have files to watch; network groups and host levels are virtual.</summary>
    public IDisposable Watch(string directoryPath, Action changed)
    {
        var parsed = NetworkPath.Parse(directoryPath);
        return parsed.Level == NetworkPathLevel.Share
            ? FileSystemListing.Watch(parsed.Unc, changed)
            : LocalFileSystemPlugin.NoWatch.Instance;
    }

    private async IAsyncEnumerable<VfsEntry> EnumerateRootAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new VfsEntry(Loc.T("LAN"), LanPath, VfsEntryKind.Directory,
            Description: Loc.T("Wykryte i ręcznie dodane urządzenia w sieci lokalnej."));
        yield return new VfsEntry(Loc.T("Remote"), RemotePath, VfsEntryKind.Directory,
            Description: Loc.T("Zapisane połączenia i lokalizacje sieciowe Windows."));
    }

    private async IAsyncEnumerable<VfsEntry> EnumerateRemoteAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new VfsEntry(Loc.T("+ Dodaj połączenie sieciowe"), NetworkPath.AddConnectionPath, VfsEntryKind.Action);
        foreach (var saved in SavedConnections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VfsEntry(saved.Name, saved.Path, VfsEntryKind.Host, Description: saved.Description ?? saved.Path);
        }

        foreach (var location in NetworkLocations.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VfsEntry(location.Name, location.Path, VfsEntryKind.Share, Description: Loc.T("Lokalizacja sieciowa Windows · {0}", location.Path));
        }
    }

    private async IAsyncEnumerable<VfsEntry> EnumerateLanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new VfsEntry(Loc.T("Skanuj adresy IP"), ScanLanPath, VfsEntryKind.Action);
        yield return new VfsEntry(Loc.T("+ Dodaj host LAN"), AddLanHostPath, VfsEntryKind.Action);

        var output = Channel.CreateUnbounded<VfsEntry>(new UnboundedChannelOptions { SingleReader = true });
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAddresses = new HashSet<IPAddress>();
        var seenGate = new Lock();

        void Emit(string display, string hostName, IPAddress? address, IReadOnlyList<int> ports, VfsEntryFlags flags, string? serviceDescription = null)
        {
            // A host is reported once, whether it was found by name, by address, or both. Both keys are checked before
            // either is claimed: claiming one and then rejecting on the other dropped hosts (a host shown by its IP
            // claimed the address under its name, so the address check then failed for itself and for its DNS name).
            lock (seenGate)
            {
                if (seenNames.Contains(display) || (address is not null && seenAddresses.Contains(address)))
                {
                    return;
                }

                seenNames.Add(display);
                if (address is not null)
                {
                    seenAddresses.Add(address);
                }
            }

            var description = DescribePorts(ports, address);
            if (!string.IsNullOrWhiteSpace(serviceDescription))
            {
                description = string.IsNullOrWhiteSpace(description) ? serviceDescription : $"{description} · {serviceDescription}";
            }

            output.Writer.TryWrite(new VfsEntry(display, NetworkPath.HostPath(hostName), VfsEntryKind.Host, Flags: flags, Description: description));
        }

        // Each source swallows its own cancellation: an exception escaping a Task.Run lambda stops a debugger
        // ("user-unhandled") every time the user leaves "This PC" or net:// before discovery finishes.
        var discovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discovery.CancelAfter(DiscoveryBudget);
        var token = discovery.Token;
        var sources = Task.WhenAll(
            Task.Run(() => EmitManualHostsAsync(Emit, token), CancellationToken.None),
            Task.Run(() => EmitCachedHostsAsync(Emit, token), CancellationToken.None),
            Task.Run(() => EmitBrowserServersAsync(Emit, token), CancellationToken.None),
            Task.Run(() => EmitScanAsync(Emit, token), CancellationToken.None),
            Task.Run(() => EmitAdvertisedServicesAsync(Emit, token), CancellationToken.None));
        _ = sources.ContinueWith(_ => output.Writer.TryComplete(), TaskScheduler.Default);

        try
        {
            await foreach (var entry in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }
        }
        finally
        {
            // Leaving early stops the sources too; they end quietly on their own threads. Not disposed: a source may
            // still be linking to the token, and cancelling already released the budget timer.
            discovery.Cancel();
        }
    }

    private async Task EmitCachedHostsAsync(HostSink emit, CancellationToken cancellationToken)
    {
        if (_index is null)
        {
            return;
        }

        try
        {
            // Hosts with a DNS name first: the cache may also hold the same address from a visit when it had no name,
            // and the first one emitted wins.
            var hosts = await _index.GetHostsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var host in hosts.OrderBy(h => IPAddress.TryParse(h.Host, out _)))
            {
                var address = host.Address is not null && IPAddress.TryParse(host.Address, out var parsed) ? parsed : null;
                var display = IPAddress.TryParse(host.Host, out _) ? host.Host : DiscoveredHost.ShortName(host.Host);
                emit(display, host.Host, address, host.OpenPorts, VfsEntryFlags.Cached);
            }
        }
        catch (Exception)
        {
            // The cache is an accelerator; live discovery still runs. Cancellation also ends here quietly: these
            // sources run inside Task.Run, and the listing reports the cancellation itself.
        }
    }

    private async Task EmitBrowserServersAsync(HostSink emit, CancellationToken cancellationToken)
    {
        if (!LanDiscoveryEnabled)
        {
            return;
        }

        List<string> names;
        try
        {
            // Without a Computer Browser on the network NetServerEnum blocks for 30 s or more, which would keep
            // the whole listing "loading"; give up early and let the hung call finish in the background.
            names = await BlockingCalls.RunAsync(_ => Win32Network.EnumerateBrowserServers(), cancellationToken, BrowserEnumerationTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return; // Runs inside Task.Run: cancellation must end quietly here (see EnumerateLanAsync).
        }

        foreach (var name in names)
        {
            emit(name.ToUpperInvariant(), name, null, [], VfsEntryFlags.None);
        }
    }

    private async Task EmitManualHostsAsync(HostSink emit, CancellationToken cancellationToken)
    {
        try
        {
            await Parallel.ForEachAsync(
                LanHosts.ToArray(),
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                async (saved, token) =>
                {
                    var addresses = LanDiscoveryEnabled
                        ? await ResolveLocalAddressesAsync(saved.Host, token).ConfigureAwait(false)
                        : [];
                    var scanPorts = ScanPopularPorts ? CommonServicePorts : _scanner.Ports;
                    var openPorts = addresses.Length == 0
                        ? []
                        : (await Task.WhenAll(addresses.Select(address =>
                            _scanner.ProbeHostAsync(address, scanPorts, _scanner.NeighbourConnectTimeout, token))).ConfigureAwait(false))
                            .SelectMany(ports => ports)
                            .Distinct()
                            .Order()
                            .ToArray();
                    var address = addresses.FirstOrDefault();
                    var description = !LanDiscoveryEnabled
                        ? Loc.T("Host dodany ręcznie · wykrywanie usług jest wyłączone.")
                        : addresses.Length == 0
                            ? Loc.T("Nie znaleziono lokalnego adresu IPv4; host nie był skanowany.")
                        : openPorts.Length == 0
                            ? Loc.T("Host dodany ręcznie · nie wykryto typowych usług TCP.")
                            : Loc.T("Otwarte porty TCP: {0}", string.Join(", ", openPorts.Select(port => $"{port} ({DescribePort(port)})")));
                    emit(saved.Name, saved.Host, address, [], VfsEntryFlags.None, description);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Navigation away and the discovery budget both end the user-added host probes quietly.
        }
    }

    private static async Task<IPAddress[]> ResolveLocalAddressesAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, System.Net.Sockets.AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
            return addresses.Where(IsLocalAddress).Distinct().ToArray();
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] is 10 or 127
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }

    private async Task EmitScanAsync(HostSink emit, CancellationToken cancellationToken)
    {
        if (!LanDiscoveryEnabled)
        {
            return;
        }

        try
        {
            await foreach (var host in _scanner.ScanAsync(
                cancellationToken,
                ScanPopularPorts ? CommonServicePorts : null).ConfigureAwait(false))
            {
                emit(host.DisplayName, host.HostName, host.Address, host.OpenPorts, VfsEntryFlags.None);
                _index?.UpsertHost(new NetworkHostRecord(
                    host.HostName,
                    host.Address.ToString(),
                    host.OpenPorts,
                    DateTimeOffset.UtcNow));
            }
        }
        catch (OperationCanceledException)
        {
            // Navigated away: the listing reports the cancellation itself.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"LAN scan failed: {ex.Message}");
        }
    }

    private async Task EmitAdvertisedServicesAsync(HostSink emit, CancellationToken cancellationToken)
    {
        if (!LanDiscoveryEnabled)
        {
            return;
        }

        try
        {
            _advertisedServices.Clear();
            var services = await _serviceBrowser.BrowseAsync(cancellationToken).ConfigureAwait(false);
            foreach (var service in services)
            {
                RememberService(service);
            }

            foreach (var group in services.GroupBy(s => s.Address?.ToString() ?? s.HostName, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                var host = first.Address?.ToString() ?? first.HostName;
                var display = first.Address is null ? DiscoveredHost.ShortName(first.HostName) : first.Address.ToString();
                emit(display, host, first.Address, [], VfsEntryFlags.None, DescribeServices(group));
            }
        }
        catch (OperationCanceledException)
        {
            // Leaving the listing cancels the bounded DNS-SD browse.
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException)
        {
            System.Diagnostics.Trace.TraceWarning($"mDNS service discovery failed: {ex.Message}");
        }
    }

    private async IAsyncEnumerable<VfsEntry> EnumerateHostAsync(string host, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var saved in LanHosts.Where(saved => string.Equals(saved.Host, host, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var service in saved.Services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (service.Port is < 1 or > 65535 || service.Scheme is not ("http" or "https"))
                {
                    continue;
                }

                var url = new UriBuilder(service.Scheme, host, service.Port).Uri.AbsoluteUri;
                yield return new VfsEntry(service.Name, url, VfsEntryKind.File,
                    Description: Loc.T("Usługa ręczna · {0}:{1}", service.Scheme, service.Port));
            }
        }

        foreach (var service in ServicesForHost(host))
        {
            if (MdnsServiceBrowser.WebUrlFor(service) is { } url)
            {
                var label = MdnsServiceBrowser.LabelFor(service.ServiceType);
                var name = string.IsNullOrWhiteSpace(service.Instance) ? label : $"{label} · {service.Instance}";
                yield return new VfsEntry(name, url, VfsEntryKind.File, Description: DescribeAdvertisedService(service));
            }
            else if (service.ServiceType.Equals("_ssh._tcp.local.", StringComparison.OrdinalIgnoreCase)
                && service.Port != LanScanner.SshPort)
            {
                yield return new VfsEntry("SFTP", $"sftp://{host}:{service.Port}/", VfsEntryKind.Host,
                    Description: Loc.T("Usługa SSH ogłoszona przez DNS-SD · port {0}", service.Port));
            }
            else if (service.ServiceType.Equals("_ftp._tcp.local.", StringComparison.OrdinalIgnoreCase)
                && service.Port != LanScanner.FtpPort)
            {
                yield return new VfsEntry("FTP", $"ftp://{host}:{service.Port}/", VfsEntryKind.Host,
                    Description: Loc.T("Usługa FTP ogłoszona przez DNS-SD · port {0}", service.Port));
            }
        }

        // Probing takes ~150 ms, while NetShareEnum against a host without SMB waits ~10 s for the OS to give up.
        // Recheck the same port set used by discovery so every detected service appears in the host view.
        var portsToProbe = ScanPopularPorts ? CommonServicePorts : _scanner.Ports;
        var open = LanDiscoveryEnabled
            ? await ResolveAndProbeAsync(host, portsToProbe, cancellationToken).ConfigureAwait(false)
            : null;
        if (open is not null && open.Contains(LanScanner.SshPort))
        {
            yield return new VfsEntry("SFTP", $"sftp://{host}/", VfsEntryKind.Host, Description: Loc.T("SSH na porcie {0}", LanScanner.SshPort));
        }

        if (open is not null && open.Contains(LanScanner.FtpPort))
        {
            yield return new VfsEntry("FTP", $"ftp://{host}/", VfsEntryKind.Host, Description: Loc.T("FTP na porcie {0}", LanScanner.FtpPort));
        }

        if (open is not null)
        {
            foreach (var port in open.Where(port => port is not (LanScanner.SshPort or LanScanner.FtpPort or LanScanner.SmbPort)))
            {
                yield return ServiceEntry(host, port);
            }
        }

        // Unresolvable by DNS (null) may still be reachable by NetBIOS name, so try SMB in that case too.
        if (open is not null && !open.Contains(LanScanner.SmbPort))
        {
            yield break;
        }

        List<ShareInfo> shares;
        try
        {
            shares = Win32Network.EnumerateShares(host);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is NativeMethods.ErrorAccessDenied or NativeMethods.ErrorLogonFailure)
        {
            throw new VfsAuthenticationRequiredException(NetworkPath.HostPath(host), @"\\" + host, ex);
        }
        catch (Win32Exception)
        {
            shares = [];
        }

        foreach (var share in shares)
        {
            yield return new VfsEntry(share.Name, $@"\\{host}\{share.Name}", VfsEntryKind.Share, Description: share.Remark);
        }
    }

    private void RememberService(AdvertisedNetworkService service)
    {
        var key = $"{service.ServiceType}|{service.Instance}|{service.HostName}";
        Add(service.HostName);
        if (service.Address is not null)
        {
            Add(service.Address.ToString());
        }

        void Add(string host) =>
            _advertisedServices.GetOrAdd(host.TrimEnd('.'), _ => new ConcurrentDictionary<string, AdvertisedNetworkService>(StringComparer.OrdinalIgnoreCase))
                .TryAdd(key, service);
    }

    private IReadOnlyList<AdvertisedNetworkService> ServicesForHost(string host) =>
        _advertisedServices.TryGetValue(host.TrimEnd('.'), out var services)
            ? services.Values.OrderBy(s => s.ServiceType, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Instance, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    private static string DescribeServices(IEnumerable<AdvertisedNetworkService> services)
    {
        var summary = string.Join(" · ", services
            .Select(s => $"{MdnsServiceBrowser.LabelFor(s.ServiceType)} :{s.Port}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));
        return Loc.T("Ogłaszane usługi: {0}", summary);
    }

    private static string DescribeAdvertisedService(AdvertisedNetworkService service)
    {
        var details = service.Text
            .Where(item => item.Key.Equals("path", StringComparison.OrdinalIgnoreCase) || item.Key.Equals("api", StringComparison.OrdinalIgnoreCase))
            .Select(item => $"{item.Key}={item.Value}");
        var suffix = string.Join(" · ", details);
        return suffix.Length == 0
            ? Loc.T("Usługa ogłoszona przez DNS-SD · port {0}", service.Port)
            : Loc.T("Usługa ogłoszona przez DNS-SD · port {0} · {1}", service.Port, suffix);
    }

    /// <returns>Open service ports, or <c>null</c> when the name does not resolve through DNS.</returns>
    private async Task<IReadOnlyList<int>?> ResolveAndProbeAsync(string host, IReadOnlyList<int> ports, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, System.Net.Sockets.AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }

        return addresses.Length == 0
            ? null
            : await _scanner.ProbeHostAsync(addresses[0], ports, _scanner.NeighbourConnectTimeout, cancellationToken).ConfigureAwait(false);
    }

    internal static VfsEntry ServiceEntry(string host, int port)
    {
        var scheme = IsHttpsPort(port) ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        if (IsHttpPort(port))
        {
            var url = new UriBuilder(scheme, host, port).Uri.AbsoluteUri;
            return new VfsEntry(DescribePort(port), url, VfsEntryKind.File,
                Description: Loc.T("Otwarta usługa TCP · port {0}", port));
        }

        return new VfsEntry(DescribePort(port), $"service://{host}:{port}", VfsEntryKind.Service,
            Description: Loc.T("Otwarta usługa TCP · port {0}", port));
    }

    private static bool IsHttpPort(int port) => port is 80 or 81 or 443 or 3000 or 5000 or 5001 or 8000 or 8080 or 8081 or 8443 or 9000 or 9090;

    private static bool IsHttpsPort(int port) => port is 443 or 5001 or 8443;

    private async IAsyncEnumerable<VfsEntry> EnumerateShareAsync(NetworkPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IEnumerator<VfsEntry>? enumerator = null;
        Exception? openError = null;
        try
        {
            enumerator = FileSystemListing.Enumerate(path.Unc).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            openError = ex;
        }

        if (openError is not null)
        {
            if (FileSystemListing.IsAuthenticationError(openError))
            {
                throw new VfsAuthenticationRequiredException(path.Unc, path.ShareRoot, openError);
            }

            // Unreachable server: fall back to the last listing we saw, flagged as cached.
            IReadOnlyList<VfsEntry> cached = _index is null
                ? []
                : await _index.GetCachedListingAsync(path.Unc, cancellationToken).ConfigureAwait(false);
            if (cached.Count == 0)
            {
                ExceptionDispatchInfo.Throw(openError);
            }

            foreach (var entry in cached)
            {
                yield return entry;
            }

            yield break;
        }

        var snapshot = _index is null ? null : new List<VfsEntry>();
        using (enumerator)
        {
            var count = 0;
            while (enumerator!.MoveNext())
            {
                var entry = enumerator.Current;
                if (snapshot is not null && snapshot.Count < MaxCachedListing)
                {
                    snapshot.Add(entry);
                }

                if (++count % 1024 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                yield return entry;
            }
        }

        if (snapshot is not null && snapshot.Count < MaxCachedListing)
        {
            _index!.CacheListing(path.Unc, snapshot);
        }
    }

    private static VfsEntry? GetShareEntry(string unc)
    {
        FileSystemInfo info = Directory.Exists(unc) ? new DirectoryInfo(unc) : new FileInfo(unc);
        return info.Exists ? FileSystemListing.ToEntry(info) : null;
    }

    private static string RequireShare(string path)
    {
        var parsed = NetworkPath.Parse(path);
        return parsed.Level == NetworkPathLevel.Share
            ? parsed.Unc
            : throw new NotSupportedException("Only paths inside a share can be read or modified.");
    }

    private static string? DescribePorts(IReadOnlyList<int> ports, IPAddress? address)
    {
        var services = new List<string>(4);
        if (address is not null)
        {
            services.Add(address.ToString());
        }

        foreach (var port in ports)
        {
            services.Add(DescribePort(port));
        }

        return services.Count == 0 ? null : string.Join(" · ", services);
    }

    private static string DescribePort(int port) => port switch
    {
        21 => "FTP",
        22 => "SSH",
        23 => "Telnet",
        53 => "DNS",
        80 => "HTTP",
        81 => "HTTP-alt",
        139 => "NetBIOS",
        443 => "HTTPS",
        445 => "SMB",
        554 => "RTSP",
        1883 => "MQTT",
        3000 => "HTTP :3000",
        3306 => "MySQL",
        3389 => "RDP",
        5000 => "HTTP :5000",
        5001 => "HTTPS :5001",
        8000 => "HTTP :8000",
        8080 => "HTTP :8080",
        8081 => "HTTP :8081",
        8291 => "MikroTik Winbox",
        8443 => "HTTPS :8443",
        8728 => "MikroTik API",
        8729 => "MikroTik API-SSL",
        9000 => "HTTP :9000",
        9090 => "HTTP :9090",
        9100 => "IPP",
        32400 => "Plex",
        _ => port.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
