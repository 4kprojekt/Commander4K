using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Commander4K.Plugins.BuiltIn.Interop;

namespace Commander4K.Plugins.BuiltIn.Network;

/// <param name="DnsName">Reverse-DNS name exactly as resolved (e.g. <c>ha.lan</c>); the bare label may not resolve forward.</param>
public readonly record struct DiscoveredHost(IPAddress Address, string? DnsName, IReadOnlyList<int> OpenPorts)
{
    /// <summary>Short upper-case label for display, e.g. <c>HA</c>; the address when there is no DNS name.</summary>
    public string DisplayName => DnsName is null ? Address.ToString() : ShortName(DnsName);

    /// <summary>Name to connect with: the full DNS name, or the address.</summary>
    public string HostName => DnsName ?? Address.ToString();

    internal static string ShortName(string dnsName)
    {
        var dot = dnsName.IndexOf('.');
        return (dot > 0 ? dnsName[..dot] : dnsName).ToUpperInvariant();
    }
}

/// <summary>
/// Fast LAN discovery without NetBIOS: sweeps the local IPv4 subnets (ARP neighbours first) with short TCP connect
/// probes on SMB, SSH and FTP, streaming each responding host as soon as it answers.
/// </summary>
public sealed class LanScanner
{
    public const int SmbPort = 445;
    public const int SshPort = 22;
    public const int FtpPort = 21;

    /// <summary>Subnets wider than /24 are clamped to the /24 around our own address.</summary>
    private const int NarrowestScannedPrefix = 24;

    public IReadOnlyList<int> Ports { get; init; } = [SmbPort, SshPort, FtpPort];

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>ARP neighbours are known to be alive: they get longer, so a busy host still answers.</summary>
    public TimeSpan NeighbourConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Reverse lookups at the same time. Windows has no asynchronous reverse lookup: each one holds a thread-pool
    /// thread, for 5–10 s when the address has no name, so they are capped to keep the pool free for the UI's work.
    /// </summary>
    private const int MaxConcurrentReverseLookups = 4;

    private readonly SemaphoreSlim _lookups = new(MaxConcurrentReverseLookups);

    public TimeSpan ReverseDnsTimeout { get; init; } = TimeSpan.FromMilliseconds(400);

    public int MaxDegreeOfParallelism { get; init; } = 128;

    public async IAsyncEnumerable<DiscoveredHost> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (candidates, neighbours) = GetCandidatesWithNeighbours();
        var found = Channel.CreateUnbounded<DiscoveredHost>(new UnboundedChannelOptions { SingleReader = true });

        var sweep = Task.Run(
            async () =>
            {
                try
                {
                    var options = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                        CancellationToken = cancellationToken,
                    };
                    // The body never throws on cancellation (probes return "closed"): an exception leaving this
                    // lambda into Parallel's code would stop a debugger as "user-unhandled" on every navigation away
                    // from "This PC". Parallel.ForEachAsync itself stops on the token and reports it below.
                    await Parallel.ForEachAsync(candidates, options, async (address, ct) =>
                    {
                        var open = await ProbeHostAsync(address, Ports, neighbours.Contains(address) ? NeighbourConnectTimeout : ConnectTimeout, ct).ConfigureAwait(false);
                        if (open.Count > 0 && !ct.IsCancellationRequested)
                        {
                            var name = await TryReverseLookupAsync(address, ct).ConfigureAwait(false);
                            found.Writer.TryWrite(new DiscoveredHost(address, name, open));
                        }
                    }).ConfigureAwait(false);
                    found.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    found.Writer.TryComplete(ex);
                }
            },
            CancellationToken.None);

        await foreach (var host in found.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return host;
        }

        await sweep.ConfigureAwait(false);
    }

    /// <summary>Returns the subset of <paramref name="ports"/> accepting TCP connections on <paramref name="address"/>.</summary>
    public Task<IReadOnlyList<int>> ProbeHostAsync(IPAddress address, IReadOnlyList<int> ports, CancellationToken cancellationToken) =>
        ProbeHostAsync(address, ports, ConnectTimeout, cancellationToken);

    private static async Task<IReadOnlyList<int>> ProbeHostAsync(IPAddress address, IReadOnlyList<int> ports, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var probes = new Task<bool>[ports.Count];
        for (var i = 0; i < probes.Length; i++)
        {
            probes[i] = IsPortOpenAsync(address, ports[i], timeout, cancellationToken);
        }

        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        var open = new List<int>(ports.Count);
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i])
            {
                open.Add(ports[i]);
            }
        }

        return open;
    }

    /// <summary>Hosts to probe: ARP neighbours first (most likely alive), then the rest of each local subnet.</summary>
    internal static List<IPAddress> GetCandidates() => GetCandidatesWithNeighbours().Candidates;

    private static (List<IPAddress> Candidates, HashSet<IPAddress> Neighbours) GetCandidatesWithNeighbours()
    {
        var subnets = GetLocalSubnets();
        var ordered = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();

        foreach (var neighbour in Win32Network.GetArpNeighbours())
        {
            if (subnets.Any(s => s.Contains(neighbour) && !s.IsSelfOrEdge(neighbour)) && seen.Add(neighbour))
            {
                ordered.Add(neighbour);
            }
        }

        var neighbours = new HashSet<IPAddress>(ordered);

        foreach (var subnet in subnets)
        {
            foreach (var address in subnet.Hosts())
            {
                if (seen.Add(address))
                {
                    ordered.Add(address);
                }
            }
        }

        return (ordered, neighbours);
    }

    /// <summary>
    /// A connect that is guaranteed to end: the wait is bounded by <paramref name="timeout"/> itself and closing the
    /// socket aborts the connect, instead of trusting <c>ConnectAsync</c> to observe its token — a probe that never
    /// completed kept the whole listing "loading" for good.
    /// </summary>
    /// <remarks>
    /// No exception is thrown anywhere on this path: the outcome is read from <see cref="SocketAsyncEventArgs.SocketError"/>
    /// and the timeout completes the same source. A sweep is ~3000 probes, nearly all of them timeouts, and a throwing
    /// probe (<c>WaitAsync</c> → <c>TimeoutException</c>) made a debugger suspend the whole app 3000 times: under F5
    /// the window froze and <c>net://</c> stayed "loading".
    /// </remarks>
    private static async Task<bool> IsPortOpenAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connect = new SocketAsyncEventArgs { RemoteEndPoint = new IPEndPoint(address, port) };
        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connect.Completed += (_, e) => outcome.TrySetResult(e.SocketError == SocketError.Success);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (!socket.ConnectAsync(connect))
            {
                return connect.SocketError == SocketError.Success; // Completed synchronously (e.g. refused at once).
            }

            deadline.CancelAfter(timeout);
            using (deadline.Token.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false), outcome))
            {
                return await outcome.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            deadline.Dispose();
            socket.Dispose(); // Aborts a connect still in progress; Completed then reports OperationAborted, ignored.
            connect.Dispose(); // Deferred by the runtime until a pending operation completes.
        }
    }

    private async Task<string?> TryReverseLookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            // A lookup still running from an earlier host keeps its slot until Windows gives up, so a slow DNS server
            // cannot take more than a few pool threads; when none is free in time the host is shown by its address.
            if (!await _lookups.WaitAsync(ReverseDnsTimeout, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var lookup = Dns.GetHostEntryAsync(address);
            _ = lookup.ContinueWith(t =>
            {
                _ = t.Exception;
                _lookups.Release();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // WhenAny instead of WaitAsync: a slow or failed lookup is the common case and must not throw (see IsPortOpenAsync).
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAny(lookup, Task.Delay(ReverseDnsTimeout, deadline.Token)).ConfigureAwait(false);
            deadline.Cancel(); // Releases the delay's timer.
            if (!lookup.IsCompletedSuccessfully)
            {
                return null;
            }

            var name = lookup.Result.HostName;
            if (string.IsNullOrEmpty(name) || IPAddress.TryParse(name, out _))
            {
                return null;
            }

            return name.ToLowerInvariant();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null; // The scan was cancelled while waiting for a lookup slot.
        }
    }

    private static List<Ipv4Subnet> GetLocalSubnets()
    {
        var subnets = new List<Ipv4Subnet>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IsLinkLocal(address))
                {
                    continue;
                }

                var prefix = Math.Max(unicast.PrefixLength, NarrowestScannedPrefix);
                if (prefix < 31)
                {
                    subnets.Add(new Ipv4Subnet(address, prefix));
                }
            }
        }

        return subnets;
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private readonly record struct Ipv4Subnet(IPAddress Self, int PrefixLength)
    {
        private uint SelfValue => ToUInt32(Self);

        private uint Mask => PrefixLength == 0 ? 0 : uint.MaxValue << (32 - PrefixLength);

        private uint Network => SelfValue & Mask;

        private uint Broadcast => Network | ~Mask;

        public bool Contains(IPAddress address) =>
            address.AddressFamily == AddressFamily.InterNetwork && (ToUInt32(address) & Mask) == Network;

        public bool IsSelfOrEdge(IPAddress address)
        {
            var value = ToUInt32(address);
            return value == SelfValue || value == Network || value == Broadcast;
        }

        public IEnumerable<IPAddress> Hosts()
        {
            for (var value = Network + 1; value < Broadcast; value++)
            {
                if (value != SelfValue)
                {
                    yield return FromUInt32(value);
                }
            }
        }

        private static uint ToUInt32(IPAddress address)
        {
            Span<byte> bytes = stackalloc byte[4];
            address.TryWriteBytes(bytes, out _);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        private static IPAddress FromUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return new IPAddress(bytes);
        }
    }
}
