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

    public TimeSpan ReverseDnsTimeout { get; init; } = TimeSpan.FromMilliseconds(400);

    public int MaxDegreeOfParallelism { get; init; } = 128;

    public async IAsyncEnumerable<DiscoveredHost> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var candidates = GetCandidates();
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
                    await Parallel.ForEachAsync(candidates, options, async (address, ct) =>
                    {
                        var open = await ProbeHostAsync(address, Ports, ct).ConfigureAwait(false);
                        if (open.Count > 0)
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
    public async Task<IReadOnlyList<int>> ProbeHostAsync(IPAddress address, IReadOnlyList<int> ports, CancellationToken cancellationToken)
    {
        var probes = new Task<bool>[ports.Count];
        for (var i = 0; i < probes.Length; i++)
        {
            probes[i] = IsPortOpenAsync(address, ports[i], cancellationToken);
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
    internal static List<IPAddress> GetCandidates()
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

        return ordered;
    }

    private async Task<bool> IsPortOpenAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task<string?> TryReverseLookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), cancellationToken)
                .WaitAsync(ReverseDnsTimeout, cancellationToken)
                .ConfigureAwait(false);
            var name = entry.HostName;
            if (string.IsNullOrEmpty(name) || IPAddress.TryParse(name, out _))
            {
                return null;
            }

            return name.ToLowerInvariant();
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
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
