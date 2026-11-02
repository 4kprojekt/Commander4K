using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailKitService = MailKit.MailService;

namespace Commander4K.Core.Mail;

/// <summary>The real network for <see cref="MailDiscovery"/>: HTTPS, DNS over UDP to the system's servers, and MailKit probes.</summary>
public sealed class MailDiscoveryNetwork : IMailDiscoveryNetwork
{
    private const int MaxBody = 1024 * 1024;
    private const int MaxRedirects = 3;

    // No cookies, no automatic redirects, no credentials (not even Windows ones): a 401 stays a 401.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        PreAuthenticate = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<HttpAnswer> HttpsAsync(Uri uri, string? postXml, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new HttpRequestException($"Refused a non-HTTPS address: {uri.Scheme}://{uri.Host}");
            }

            using var request = new HttpRequestMessage(postXml is null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (postXml is not null)
            {
                request.Content = new StringContent(postXml, Encoding.UTF8, "text/xml");
            }

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                if (hop == MaxRedirects)
                {
                    throw new HttpRequestException("Too many redirects.");
                }

                uri = new Uri(uri, location);
                continue;
            }

            if (status != 200)
            {
                return new HttpAnswer(status, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[MaxBody + 1];
            var length = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false)) > 0)
            {
                length += read;
                if (length > MaxBody)
                {
                    throw new InvalidDataException("The answer is larger than 1 MB.");
                }
            }

            return new HttpAnswer(status, Encoding.UTF8.GetString(buffer, 0, length));
        }
    }

    public async Task<IReadOnlyList<SrvRecord>> SrvAsync(string name, CancellationToken ct)
    {
        var (packet, answers) = await QueryAsync(name, 33, ct).ConfigureAwait(false);
        return [.. answers.Select(offset =>
        {
            var priority = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset));
            var weight = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 2));
            var port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 4));
            var at = offset + 6;
            return new SrvRecord(DnsWire.ReadName(packet, ref at), port, priority, weight);
        })];
    }

    public async Task<IReadOnlyList<MxRecord>> MxAsync(string domain, CancellationToken ct)
    {
        var (packet, answers) = await QueryAsync(domain, 15, ct).ConfigureAwait(false);
        return [.. answers.Select(offset =>
        {
            var preference = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset));
            var at = offset + 2;
            return new MxRecord(DnsWire.ReadName(packet, ref at), preference);
        })];
    }

    public async Task<ProbeAnswer> ProbeAsync(string host, int port, bool smtp, bool ssl, CancellationToken ct)
    {
        using MailKitService client = smtp ? new SmtpClient() : new ImapClient();
        var names = new List<string>();
        var mismatch = false;
        client.ServerCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            // Discovery never accepts a bad certificate; a valid one made out to another name is remembered as a candidate.
            if (errors == SslPolicyErrors.RemoteCertificateNameMismatch && certificate is not null)
            {
                mismatch = true;
                names.AddRange(CertificateNames(certificate));
            }

            return false;
        };

        try
        {
            // Greeting plus CAPABILITY (IMAP) or EHLO (SMTP), and a required STARTTLS on the other ports. No login.
            await client.ConnectAsync(host, port, ssl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct).ConfigureAwait(false);
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            return new ProbeAnswer(ProbeOutcome.Ok, []);
        }
        catch (SslHandshakeException ex)
        {
            return mismatch ? new ProbeAnswer(ProbeOutcome.CertificateMismatch, names) : new ProbeAnswer(ProbeOutcome.Tls, [], ex.Message);
        }
        catch (NotSupportedException) when (!ssl)
        {
            return new ProbeAnswer(ProbeOutcome.NoStartTls, []);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ProtocolException or ServiceNotConnectedException or System.Security.Authentication.AuthenticationException)
        {
            return new ProbeAnswer(ProbeOutcome.Network, [], ex.Message);
        }
    }

    private static IEnumerable<string> CertificateNames(X509Certificate certificate)
    {
        using var cert = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var names = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
        names.Add(cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        return names.Where(n => n.Length > 0 && !n.Contains('*')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One question to the system's DNS servers in turn; returns the packet and the RDATA offsets of the answers of that type.</summary>
    private static async Task<(byte[] Packet, List<int> Answers)> QueryAsync(string name, ushort type, CancellationToken ct)
    {
        var servers = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => !a.IsIPv6SiteLocal)
            .Distinct()
            .Take(3)
            .ToList();
        if (servers.Count == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue);
        var query = DnsWire.Query(id, name, type);
        Exception? last = null;
        foreach (var server in servers)
        {
            using var udp = new UdpClient(server.AddressFamily);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await udp.SendAsync(query, new IPEndPoint(server, 53), wait.Token).ConfigureAwait(false);
                while (true)
                {
                    var reply = await udp.ReceiveAsync(wait.Token).ConfigureAwait(false);
                    // shortcut: a truncated (TC) answer is used as far as it goes; add TCP if mail SRV/MX sets ever overflow 512 bytes.
                    if (DnsWire.Answers(reply.Buffer, id, type) is { } answers)
                    {
                        return (reply.Buffer, answers);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException($"DNS server {server} did not answer.");
            }
            catch (SocketException ex)
            {
                last = ex;
            }
        }

        throw last ?? new TimeoutException("No DNS answer.");
    }
}

/// <summary>The few parts of the DNS wire format (RFC 1035) that SRV and MX lookups need.</summary>
internal static class DnsWire
{
    public static byte[] Query(ushort id, string name, ushort type)
    {
        var packet = new List<byte>(64) { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }; // RD, one question
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }

        packet.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]); // root, type, class IN
        return [.. packet];
    }

    /// <summary>RDATA offsets of the answers of <paramref name="type"/>; empty on NXDOMAIN; null when the packet is not our answer.</summary>
    public static List<int>? Answers(byte[] packet, ushort id, ushort type)
    {
        if (packet.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(packet) != id || (packet[2] & 0x80) == 0)
        {
            return null;
        }

        var rcode = packet[3] & 0x0F;
        if (rcode == 3)
        {
            return [];
        }

        if (rcode != 0)
        {
            throw new IOException($"DNS error {rcode}.");
        }

        var questions = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6));
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            _ = ReadName(packet, ref offset);
            offset += 4;
        }

        var result = new List<int>();
        for (var i = 0; i < count && offset + 10 <= packet.Length; i++)
        {
            _ = ReadName(packet, ref offset);
            var recordType = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 8));
            offset += 10;
            if (offset + length > packet.Length)
            {
                break;
            }

            if (recordType == type)
            {
                result.Add(offset);
            }

            offset += length;
        }

        return result;
    }

    /// <summary>A possibly compressed name; pointers are followed at most 32 times so a crafted packet cannot loop.</summary>
    public static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var jumps = 0; ;)
        {
            if (position >= packet.Length)
            {
                throw new IOException("Truncated DNS name.");
            }

            var length = packet[position];
            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length || ++jumps > 32)
                {
                    throw new IOException("Invalid DNS name pointer.");
                }

                if (!jumped)
                {
                    offset = position + 2;
                    jumped = true;
                }

                position = ((length & 0x3F) << 8) | packet[position + 1];
                continue;
            }

            if (position + 1 + length > packet.Length)
            {
                throw new IOException("Truncated DNS label.");
            }

            labels.Add(Encoding.ASCII.GetString(packet, position + 1, length));
            position += 1 + length;
        }

        if (!jumped)
        {
            offset = position;
        }

        return labels.Count == 0 ? "." : string.Join('.', labels);
    }
}
