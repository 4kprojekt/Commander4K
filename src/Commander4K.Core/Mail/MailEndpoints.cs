using System.Globalization;
using MailKit.Security;

namespace Commander4K.Core.Mail;

public sealed record MailEndpoint(string Host, int Port, SecureSocketOptions Security);

/// <summary>Turns <c>host[:port]</c> into a connection target. TLS is always required, never "when available".</summary>
public static class MailEndpoints
{
    public static MailEndpoint Parse(string hostPort, int defaultPort)
    {
        ArgumentNullException.ThrowIfNull(hostPort);
        var text = hostPort.Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace) || text.Contains("://", StringComparison.Ordinal) || text.Contains('/'))
        {
            throw new FormatException("Mail server must be host or host:port.");
        }

        var host = text;
        var port = defaultPort;
        var colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                throw new FormatException("Invalid mail server port.");
            }
        }

        if (host.Length == 0 || host.Contains(':'))
        {
            throw new FormatException("Mail server host is empty or invalid.");
        }

        return new MailEndpoint(host, port, port is 993 or 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
    }
}
