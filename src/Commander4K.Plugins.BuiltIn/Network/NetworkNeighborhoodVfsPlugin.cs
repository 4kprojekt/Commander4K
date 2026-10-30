using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Commander4K.Core.Execution;
using Commander4K.Core.Indexing;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Interop;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.BuiltIn.Network;

/// <param name="Path">Target opened on Enter, e.g. <c>\\nas\media</c> or <c>sftp://me@host</c>.</param>
public sealed record SavedConnection(string Name, string Path, string? Description = null);

/// <param name="Display">Label shown in the panel.</param>
/// <param name="HostName">Name used in the host's path; must resolve (e.g. the full DNS name).</param>
internal delegate void HostSink(string Display, string HostName, IPAddress? Address, IReadOnlyList<int> Ports, VfsEntryFlags Flags);

/// <summary>
/// Network neighbourhood (<c>net://</c>) and SMB shares (UNC / <c>smb://</c>). The root streams saved connections,
/// cached hosts and live discoveries concurrently; host level lists shares and SFTP/FTP endpoints; share level
/// streams files and keeps an offline copy of each listing in the SQLite cache.
/// </summary>
public sealed class NetworkNeighborhoodVfsPlugin : IVfsProviderPlugin, IVfsChangeNotifier
{
    /// <summary>Listings larger than this are not cached for offline browsing.</summary>
    private const int MaxCachedListing = 20_000;

    private static readonly TimeSpan BrowserEnumerationTimeout = TimeSpan.FromSeconds(3);
    private static readonly ResilientAsyncExecutor BlockingCalls = new();

    private readonly SqliteIndexService? _index;
    private readonly LanScanner _scanner;

    public NetworkNeighborhoodVfsPlugin(SqliteIndexService? index = null, LanScanner? scanner = null)
    {
        _index = index;
        _scanner = scanner ?? new LanScanner();
    }

    public string Id => "builtin.network";

    public string DisplayName => "Network";

    public IReadOnlyList<string> Schemes { get; } = ["net", VfsPath.SmbScheme];

    public VfsCapabilities Capabilities => VfsCapabilities.Read | VfsCapabilities.Write | VfsCapabilities.CreateDirectory
        | VfsCapabilities.Delete | VfsCapabilities.Move | VfsCapabilities.Watch;

    /// <summary>Favourite servers and shares shown at the top of <c>net://</c>.</summary>
    public IReadOnlyList<SavedConnection> SavedConnections { get; set; } = [];

    public IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, CancellationToken cancellationToken)
    {
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
        var parsed = NetworkPath.Parse(path);
        switch (parsed.Level)
        {
            case NetworkPathLevel.Root:
                return null;
            case NetworkPathLevel.Host:
                return NetworkPath.RootPath;
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

    public IDisposable Watch(string directoryPath, Action changed) =>
        FileSystemListing.Watch(RequireShare(directoryPath), changed);

    private async IAsyncEnumerable<VfsEntry> EnumerateRootAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new VfsEntry("+ Dodaj połączenie sieciowe", NetworkPath.AddConnectionPath, VfsEntryKind.Action);
        foreach (var saved in SavedConnections)
        {
            yield return new VfsEntry(saved.Name, saved.Path, VfsEntryKind.Host, Description: saved.Description ?? saved.Path);
        }

        var output = Channel.CreateUnbounded<VfsEntry>(new UnboundedChannelOptions { SingleReader = true });
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        void Emit(string display, string hostName, IPAddress? address, IReadOnlyList<int> ports, VfsEntryFlags flags)
        {
            // A host is reported once, whether it was found by name, by address, or both.
            var nameIsNew = seen.TryAdd(display, 0);
            var addressIsNew = address is null || seen.TryAdd(address.ToString(), 0);
            if (!nameIsNew || !addressIsNew)
            {
                return;
            }

            var description = DescribePorts(ports, address);
            output.Writer.TryWrite(new VfsEntry(display, NetworkPath.HostPath(hostName), VfsEntryKind.Host, Flags: flags, Description: description));
        }

        var sources = Task.WhenAll(
            Task.Run(() => EmitCachedHostsAsync(Emit, cancellationToken), CancellationToken.None),
            Task.Run(() => EmitBrowserServersAsync(Emit, cancellationToken), CancellationToken.None),
            Task.Run(() => EmitScanAsync(Emit, cancellationToken), CancellationToken.None));
        _ = sources.ContinueWith(_ => output.Writer.TryComplete(), TaskScheduler.Default);

        await foreach (var entry in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
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
            foreach (var host in await _index.GetHostsAsync(cancellationToken).ConfigureAwait(false))
            {
                var address = host.Address is not null && IPAddress.TryParse(host.Address, out var parsed) ? parsed : null;
                var display = IPAddress.TryParse(host.Host, out _) ? host.Host : DiscoveredHost.ShortName(host.Host);
                emit(display, host.Host, address, host.OpenPorts, VfsEntryFlags.Cached);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The cache is an accelerator; live discovery still runs.
        }
    }

    private static async Task EmitBrowserServersAsync(HostSink emit, CancellationToken cancellationToken)
    {
        List<string> names;
        try
        {
            // Without a Computer Browser on the network NetServerEnum blocks for 30 s or more, which would keep
            // the whole listing "loading"; give up early and let the hung call finish in the background.
            names = await BlockingCalls.RunAsync(_ => Win32Network.EnumerateBrowserServers(), cancellationToken, BrowserEnumerationTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return;
        }

        foreach (var name in names)
        {
            emit(name.ToUpperInvariant(), name, null, [], VfsEntryFlags.None);
        }
    }

    private async Task EmitScanAsync(HostSink emit, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var host in _scanner.ScanAsync(cancellationToken).ConfigureAwait(false))
            {
                emit(host.DisplayName, host.HostName, host.Address, host.OpenPorts, VfsEntryFlags.None);
                _index?.UpsertHost(new NetworkHostRecord(
                    host.HostName,
                    host.Address.ToString(),
                    host.OpenPorts,
                    DateTimeOffset.UtcNow));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning($"LAN scan failed: {ex.Message}");
        }
    }

    private async IAsyncEnumerable<VfsEntry> EnumerateHostAsync(string host, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Probing takes ~150 ms, while NetShareEnum against a host without SMB waits ~10 s for the OS to give up,
        // so probe first, show SFTP/FTP immediately and only ask for shares when port 445 answers.
        var open = await ResolveAndProbeAsync(host, cancellationToken).ConfigureAwait(false);
        if (open is not null && open.Contains(LanScanner.SshPort))
        {
            yield return new VfsEntry("SFTP", $"sftp://{host}/", VfsEntryKind.Host, Description: $"SSH na porcie {LanScanner.SshPort}");
        }

        if (open is not null && open.Contains(LanScanner.FtpPort))
        {
            yield return new VfsEntry("FTP", $"ftp://{host}/", VfsEntryKind.Host, Description: $"FTP na porcie {LanScanner.FtpPort}");
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

    /// <returns>Open service ports, or <c>null</c> when the name does not resolve through DNS.</returns>
    private async Task<IReadOnlyList<int>?> ResolveAndProbeAsync(string host, CancellationToken cancellationToken)
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
            : await _scanner.ProbeHostAsync(addresses[0], [LanScanner.SmbPort, LanScanner.SshPort, LanScanner.FtpPort], cancellationToken).ConfigureAwait(false);
    }

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
            services.Add(port switch
            {
                LanScanner.SmbPort => "SMB",
                LanScanner.SshPort => "SSH",
                LanScanner.FtpPort => "FTP",
                _ => port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        return services.Count == 0 ? null : string.Join(" · ", services);
    }
}
