using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Commander4K.Plugins.BuiltIn.Network;

internal sealed record AdvertisedNetworkService(
    string Instance,
    string ServiceType,
    string HostName,
    int Port,
    IPAddress? Address,
    IReadOnlyDictionary<string, string> Text);

internal interface IAdvertisedServiceBrowser
{
    Task<IReadOnlyList<AdvertisedNetworkService>> BrowseAsync(CancellationToken cancellationToken);
}

/// <summary>Browses a fixed set of DNS-SD service types over mDNS; it does not probe host ports.</summary>
internal sealed class MdnsServiceBrowser : IAdvertisedServiceBrowser
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndpoint = new(MulticastAddress, 5353);
    private static readonly TimeSpan BrowseTimeout = TimeSpan.FromMilliseconds(1200);
    private const string ServiceTypeEnumeration = "_services._dns-sd._udp.local.";
    private const int MaxAdditionalServiceTypes = 32;
    private static readonly string[] ServiceTypes =
    [
        "_http._tcp.local.",
        "_https._tcp.local.",
        "_api._tcp.local.",
        "_smb._tcp.local.",
        "_ssh._tcp.local.",
        "_ftp._tcp.local.",
        "_ipp._tcp.local.",
        "_printer._tcp.local.",
        "_airplay._tcp.local.",
        "_googlecast._tcp.local.",
        "_raop._tcp.local.",
    ];

    public async Task<IReadOnlyList<AdvertisedNetworkService>> BrowseAsync(CancellationToken cancellationToken)
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .Distinct()
            .ToArray();
        if (interfaces.Length == 0)
        {
            return [];
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = false,
        };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, MulticastEndpoint.Port));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);

        var joined = 0;
        foreach (var address in interfaces)
        {
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(MulticastAddress, address));
                joined++;
            }
            catch (SocketException)
            {
                // A disconnected or non-multicast interface should not prevent browsing on the others.
            }
        }

        if (joined == 0)
        {
            return [];
        }

        var records = new DnsSdRecords();
        var buffer = new byte[9000];
        var initialTypes = ServiceTypes.Append(ServiceTypeEnumeration).ToArray();
        await SendQueriesAsync(socket, interfaces, initialTypes, cancellationToken).ConfigureAwait(false);
        await ReceiveResponsesAsync(socket, buffer, records, cancellationToken).ConfigureAwait(false);

        var additionalTypes = records.EnumeratedServiceTypes()
            .Except(initialTypes, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAdditionalServiceTypes)
            .ToArray();
        if (additionalTypes.Length > 0)
        {
            await SendQueriesAsync(socket, interfaces, additionalTypes, cancellationToken).ConfigureAwait(false);
            await ReceiveResponsesAsync(socket, buffer, records, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return records.ToServices(ServiceTypes.Concat(additionalTypes));
    }

    private static async Task SendQueriesAsync(Socket socket, IReadOnlyList<IPAddress> interfaces, IReadOnlyList<string> serviceTypes, CancellationToken cancellationToken)
    {
        var query = CreateQuery(serviceTypes);
        foreach (var address in interfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    BitConverter.ToInt32(address.GetAddressBytes()));
                await socket.SendToAsync(query, SocketFlags.None, MulticastEndpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // Try the remaining active interfaces.
            }
        }
    }

    private static async Task ReceiveResponsesAsync(Socket socket, byte[] buffer, DnsSdRecords records, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(BrowseTimeout);
        try
        {
            while (true)
            {
                var result = await socket.ReceiveFromAsync(
                    buffer.AsMemory(),
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    deadline.Token).ConfigureAwait(false);
                try
                {
                    records.Add(buffer.AsSpan(0, result.ReceivedBytes));
                }
                catch (InvalidDataException ex)
                {
                    System.Diagnostics.Trace.TraceWarning($"Ignoring malformed mDNS response: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The bounded browse window elapsed.
        }
    }

    internal static IReadOnlyList<AdvertisedNetworkService> ParseResponses(IEnumerable<byte[]> packets, IEnumerable<string>? serviceTypes = null)
    {
        var records = new DnsSdRecords();
        foreach (var packet in packets)
        {
            records.Add(packet);
        }

        return records.ToServices(serviceTypes ?? ServiceTypes.Concat(records.EnumeratedServiceTypes()));
    }

    internal static string? WebUrlFor(AdvertisedNetworkService service)
    {
        var type = service.ServiceType;
        var isApi = type.Equals("_api._tcp.local.", StringComparison.OrdinalIgnoreCase);
        var isHttps = type.Equals("_https._tcp.local.", StringComparison.OrdinalIgnoreCase)
            || (isApi && service.Text.TryGetValue("tls", out var tls)
                && (tls.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || tls.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || tls.Equals("yes", StringComparison.OrdinalIgnoreCase)));
        if (!isApi && !type.Equals("_http._tcp.local.", StringComparison.OrdinalIgnoreCase)
            && !type.Equals("_https._tcp.local.", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var host = service.Address?.ToString() ?? service.HostName.TrimEnd('.');
        var path = service.Text.GetValueOrDefault("path") ?? service.Text.GetValueOrDefault("api") ?? "/";
        if (string.IsNullOrWhiteSpace(path))
        {
            path = "/";
        }
        else if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return new UriBuilder(isHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, host, service.Port)
        {
            Path = path,
        }.Uri.AbsoluteUri;
    }

    internal static string LabelFor(string serviceType) => serviceType.ToLowerInvariant() switch
    {
        "_http._tcp.local." => "HTTP",
        "_https._tcp.local." => "HTTPS",
        "_api._tcp.local." => "API",
        "_smb._tcp.local." => "SMB",
        "_ssh._tcp.local." => "SSH",
        "_ftp._tcp.local." => "FTP",
        "_ipp._tcp.local." => "IPP",
        "_printer._tcp.local." => "Printer",
        "_airplay._tcp.local." => "AirPlay",
        "_googlecast._tcp.local." => "Google Cast",
        "_raop._tcp.local." => "AirPlay Audio",
        _ => serviceType,
    };

    private static byte[] CreateQuery(IReadOnlyList<string> serviceTypes)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)serviceTypes.Count);
        stream.Write(header);

        Span<byte> question = stackalloc byte[4];
        foreach (var serviceType in serviceTypes)
        {
            foreach (var label in serviceType.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                if (bytes.Length is 0 or > 63)
                {
                    throw new InvalidDataException("Invalid DNS-SD service label.");
                }

                stream.WriteByte((byte)bytes.Length);
                stream.Write(bytes);
            }
            stream.WriteByte(0);
            stream.WriteByte(0);
            BinaryPrimitives.WriteUInt16BigEndian(question, DnsSdRecords.PtrType);
            BinaryPrimitives.WriteUInt16BigEndian(question[2..], 1);
            stream.Write(question);
        }

        return stream.ToArray();
    }

    private sealed class DnsSdRecords
    {
        public const ushort PtrType = 12;
        private const ushort AType = 1;
        private const ushort TxtType = 16;
        private const ushort SrvType = 33;

        private readonly Dictionary<string, List<string>> _pointers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Host, int Port)> _servers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _text = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IPAddress> _addresses = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> EnumeratedServiceTypes() =>
            _pointers.TryGetValue(ServiceTypeEnumeration, out var types)
                ? types.Where(IsServiceType).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()
                : [];

        public void Add(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 12 || (BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) & 0x8000) == 0)
            {
                return;
            }

            var questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
            var records = BinaryPrimitives.ReadUInt16BigEndian(packet[6..])
                + BinaryPrimitives.ReadUInt16BigEndian(packet[8..])
                + BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
            var offset = 12;
            for (var i = 0; i < questions; i++)
            {
                _ = ReadName(packet, ref offset);
                EnsureAvailable(packet, offset, 4);
                offset += 4;
            }

            for (var i = 0; i < records; i++)
            {
                var name = ReadName(packet, ref offset);
                EnsureAvailable(packet, offset, 10);
                var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
                offset += 2;
                offset += 2;
                offset += 4;
                var length = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
                offset += 2;
                EnsureAvailable(packet, offset, length);
                var end = offset + length;
                switch (type)
                {
                    case PtrType:
                        var ptrOffset = offset;
                        var target = ReadName(packet, ref ptrOffset);
                        if (ptrOffset != end)
                        {
                            throw new InvalidDataException("Invalid DNS-SD PTR record length.");
                        }

                        if (!_pointers.TryGetValue(name, out var targets))
                        {
                            targets = [];
                            _pointers.Add(name, targets);
                        }

                        if (!targets.Contains(target, StringComparer.OrdinalIgnoreCase))
                        {
                            targets.Add(target);
                        }

                        break;
                    case SrvType when length >= 7:
                        var port = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 4)..]);
                        var srvOffset = offset + 6;
                        var server = ReadName(packet, ref srvOffset);
                        if (srvOffset != end)
                        {
                            throw new InvalidDataException("Invalid DNS-SD SRV record length.");
                        }

                        _servers[name] = (server, port);
                        break;
                    case TxtType:
                        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        while (offset < end)
                        {
                            var itemLength = packet[offset++];
                            if (offset + itemLength > end)
                            {
                                throw new InvalidDataException("Truncated DNS-SD TXT record.");
                            }

                            var item = Encoding.UTF8.GetString(packet.Slice(offset, itemLength));
                            offset += itemLength;
                            var separator = item.IndexOf('=');
                            values[separator < 0 ? item : item[..separator]] = separator < 0 ? string.Empty : item[(separator + 1)..];
                        }

                        _text[name] = values;
                        break;
                    case AType when length == 4:
                        _addresses[name] = new IPAddress(packet.Slice(offset, 4));
                        break;
                }

                offset = end;
            }
        }

        public IReadOnlyList<AdvertisedNetworkService> ToServices(IEnumerable<string> serviceTypes)
        {
            var types = serviceTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var services = new List<AdvertisedNetworkService>();
            foreach (var (type, targets) in _pointers)
            {
                if (!types.Contains(type))
                {
                    continue;
                }

                foreach (var instance in targets)
                {
                    if (!_servers.TryGetValue(instance, out var server) || server.Port is < 1 or > 65535)
                    {
                        continue;
                    }

                    var serviceType = type;
                    var suffix = serviceType;
                    var instanceName = instance.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        ? instance[..^suffix.Length].TrimEnd('.')
                        : instance;
                    var text = _text.GetValueOrDefault(instance) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    services.Add(new AdvertisedNetworkService(
                        instanceName,
                        serviceType,
                        server.Host.TrimEnd('.'),
                        server.Port,
                        _addresses.GetValueOrDefault(server.Host),
                        text));
                }
            }

            return services
                .DistinctBy(s => $"{s.ServiceType}|{s.Instance}|{s.HostName}", StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s.HostName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.ServiceType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Instance, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string ReadName(ReadOnlySpan<byte> packet, ref int offset)
        {
            var labels = new List<string>();
            var cursor = offset;
            var jumped = false;
            var jumps = 0;
            while (true)
            {
                EnsureAvailable(packet, cursor, 1);
                var length = packet[cursor++];
                if (length == 0)
                {
                    if (!jumped)
                    {
                        offset = cursor;
                    }

                    break;
                }

                if ((length & 0xC0) == 0xC0)
                {
                    EnsureAvailable(packet, cursor, 1);
                    var pointer = ((length & 0x3F) << 8) | packet[cursor++];
                    if (pointer >= packet.Length || ++jumps > 32)
                    {
                        throw new InvalidDataException("Invalid DNS compression pointer.");
                    }

                    if (!jumped)
                    {
                        offset = cursor;
                        jumped = true;
                    }

                    cursor = pointer;
                    continue;
                }

                if ((length & 0xC0) != 0 || length > 63)
                {
                    throw new InvalidDataException("Invalid DNS label.");
                }

                EnsureAvailable(packet, cursor, length);
                labels.Add(Encoding.UTF8.GetString(packet.Slice(cursor, length)));
                cursor += length;
            }

            return string.Join('.', labels).ToLowerInvariant() + ".";
        }

        private static bool IsServiceType(string name)
        {
            var labels = name.TrimEnd('.').Split('.');
            return labels.Length == 3
                && labels[0].StartsWith('_')
                && labels[1] is "_tcp" or "_udp"
                && labels[2].Equals("local", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureAvailable(ReadOnlySpan<byte> packet, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset > packet.Length - length)
            {
                throw new InvalidDataException("Truncated DNS-SD response.");
            }
        }
    }
}
