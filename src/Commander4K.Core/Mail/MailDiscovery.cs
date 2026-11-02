using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Mail;

public sealed record HttpAnswer(int Status, string? Body);

public sealed record SrvRecord(string Target, int Port, int Priority, int Weight);

public sealed record MxRecord(string Host, int Preference);

public enum ProbeOutcome
{
    Ok,
    Network,
    Tls,
    NoStartTls,
    CertificateMismatch,
}

/// <param name="CertificateNames">On <see cref="ProbeOutcome.CertificateMismatch"/>: the CN and DNS names the certificate is for.</param>
public sealed record ProbeAnswer(ProbeOutcome Outcome, IReadOnlyList<string> CertificateNames, string? Detail = null);

/// <summary>The network as <see cref="MailDiscovery"/> sees it; tests replace it.</summary>
public interface IMailDiscoveryNetwork
{
    /// <summary>GET (<paramref name="postXml"/> null) or an XML POST over HTTPS only: at most 3 HTTPS redirects, a body of at most 1 MB, no credentials ever.</summary>
    Task<HttpAnswer> HttpsAsync(Uri uri, string? postXml, CancellationToken ct);

    Task<IReadOnlyList<SrvRecord>> SrvAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<MxRecord>> MxAsync(string domain, CancellationToken ct);

    /// <summary>Connects with TLS (at once, or a required STARTTLS), reads the greeting and CAPABILITY/EHLO and leaves. Never logs in.</summary>
    Task<ProbeAnswer> ProbeAsync(string host, int port, bool smtp, bool ssl, CancellationToken ct);
}

/// <summary>
/// Finds the IMAP/SMTP servers of an address. Stages 1–5 (domain autoconfig, SRV, Thunderbird's ISPDB, MX, Autodiscover)
/// run together and are chosen in that order; the chosen servers must accept a TLS connection with a valid certificate.
/// Otherwise the usual host names are probed (6); a certificate made out to another host name (shared hosting) makes
/// that name a new candidate, never an accepted certificate. Nothing here logs in or sends a password.
/// </summary>
public sealed class MailDiscovery(IMailDiscoveryNetwork network, TimeSpan? attemptTimeout = null, TimeSpan? totalTimeout = null)
{
    public static MailDiscovery Default { get; } = new(new MailDiscoveryNetwork());

    private readonly IMailDiscoveryNetwork _network = network;
    private readonly TimeSpan _attempt = attemptTimeout ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _total = totalTimeout ?? TimeSpan.FromSeconds(20);

    private sealed record Server(string Host, int Port, bool Ssl, string Username);

    private sealed record Proposal(string Source, IReadOnlyList<Server> Imap, IReadOnlyList<Server> Smtp, IReadOnlyList<string> Auth);

    public async Task<MailDiscoveryResult> DiscoverAsync(string email, CancellationToken ct)
    {
        var (address, local, domain) = ParseAddress(email);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(_total);
        var result = await new Run(this, address, local, domain, total.Token).ExecuteAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>The address with its domain in ASCII (punycode), its local part and the domain; throws on anything else.</summary>
    public static (string Address, string Local, string Domain) ParseAddress(string email)
    {
        var text = email?.Trim() ?? string.Empty;
        var at = text.LastIndexOf('@');
        if (at is <= 0 or > 64 || at == text.Length - 1 || text.IndexOf('@') != at || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException(Loc.T("„{0}” nie jest adresem e-mail.", text));
        }

        string domain;
        try
        {
            domain = new IdnMapping().GetAscii(text[(at + 1)..].TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            throw new ArgumentException(Loc.T("„{0}” nie jest adresem e-mail.", text));
        }

        if (!IsHostName(domain) || !domain.Contains('.'))
        {
            throw new ArgumentException(Loc.T("„{0}” nie jest adresem e-mail.", text));
        }

        var local = text[..at];
        return ($"{local}@{domain}", local, domain);
    }

    public static bool IsHostName(string host) =>
        host.Length is > 0 and <= 253
        && host.Split('.').All(label => label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    /// <summary>The host itself or a name under the address's domain.</summary>
    public static bool InDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>The registered domain of a host name, for an ISPDB lookup of an MX host.</summary>
    // shortcut: a short list instead of the Public Suffix List; upgrade if MX hosts under other two-level suffixes matter.
    public static string BaseDomain(string host)
    {
        var labels = host.TrimEnd('.').Split('.');
        var take = labels.Length >= 3 && labels[^1].Length == 2 && labels[^2] is "com" or "net" or "org" or "co" or "gov" or "edu" or "ac" ? 3 : 2;
        return string.Join('.', labels.TakeLast(Math.Min(take, labels.Length)));
    }

    /// <summary>The XML of Mozilla autoconfig / ISPDB: <c>emailProvider</c> with incoming and outgoing servers.</summary>
    private static (List<Server> Imap, List<Server> Smtp, List<string> Auth) ParseAutoconfig(string xml, string address, string local, string domain, Action<string> log)
    {
        var root = LoadXml(xml);
        var imap = new List<Server>();
        var smtp = new List<Server>();
        var auth = new List<string>();
        foreach (var server in root.Descendants().Where(e => e.Name.LocalName is "incomingServer" or "outgoingServer"))
        {
            var type = (string?)server.Attribute("type");
            var outgoing = server.Name.LocalName == "outgoingServer";
            if (outgoing ? type != "smtp" : type != "imap")
            {
                continue;
            }

            string Child(string name) => Fill(server.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? string.Empty, address, local, domain);
            var host = Child("hostname").ToLowerInvariant();
            var socket = Child("socketType").ToUpperInvariant();
            if (!int.TryParse(Child("port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || socket is not ("SSL" or "STARTTLS"))
            {
                log($"  {type} {host}: socket type '{socket}' or port refused (no plain text)");
                continue;
            }

            foreach (var method in server.Elements().Where(e => e.Name.LocalName == "authentication").Select(e => e.Value.Trim()))
            {
                var name = method.Equals("OAuth2", StringComparison.OrdinalIgnoreCase) ? "oauth2" : method.StartsWith("password", StringComparison.OrdinalIgnoreCase) ? "password" : null;
                if (name is not null && !auth.Contains(name))
                {
                    auth.Add(name);
                }
            }

            var username = Child("username");
            if (Acceptable(host, port, socket == "SSL", log))
            {
                (outgoing ? smtp : imap).Add(new Server(host, port, socket == "SSL", username.Length > 0 ? username : address));
            }
        }

        return (imap, smtp, auth);
    }

    /// <summary>The POX answer of Exchange Autodiscover: <c>Protocol</c> elements of type IMAP and SMTP.</summary>
    private static (List<Server> Imap, List<Server> Smtp) ParseAutodiscover(string xml, string address, Action<string> log)
    {
        var imap = new List<Server>();
        var smtp = new List<Server>();
        foreach (var protocol in LoadXml(xml).Descendants().Where(e => e.Name.LocalName == "Protocol"))
        {
            string Child(string name) => protocol.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? string.Empty;
            var type = Child("Type").ToUpperInvariant();
            if (type is not ("IMAP" or "SMTP") || !int.TryParse(Child("Port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                continue;
            }

            // Only implicit TLS on 993/465; any other port is used with a required STARTTLS, which the probe enforces.
            var host = Child("Server").ToLowerInvariant();
            var ssl = port is 993 or 465;
            var login = Child("LoginName");
            if (Acceptable(host, port, ssl, log))
            {
                (type == "IMAP" ? imap : smtp).Add(new Server(host, port, ssl, login.Length > 0 ? login : address));
            }
        }

        return (imap, smtp);
    }

    private static string Fill(string text, string address, string local, string domain) => text
        .Replace("%EMAILADDRESS%", address, StringComparison.Ordinal)
        .Replace("%EMAILLOCALPART%", local, StringComparison.Ordinal)
        .Replace("%EMAILDOMAIN%", domain, StringComparison.Ordinal);

    /// <summary>A server c4k.dir can name: a host name, and TLS on 993/465 only, since <see cref="MailEndpoints"/> derives TLS from the port.</summary>
    private static bool Acceptable(string host, int port, bool ssl, Action<string> log)
    {
        if (!IsHostName(host) || port is < 1 or > 65535)
        {
            log($"  invalid server '{host}:{port}'");
            return false;
        }

        if (ssl != port is 993 or 465)
        {
            log($"  {host}:{port} {(ssl ? "ssl" : "starttls")} skipped: c4k.dir uses implicit TLS on 993/465 only");
            return false;
        }

        return true;
    }

    /// <summary>No DTD, no external entities, at most 1 MB.</summary>
    private static XElement LoadXml(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XElement.Load(reader);
    }

    private sealed class Run(MailDiscovery owner, string address, string local, string domain, CancellationToken total)
    {
        private readonly ConcurrentQueue<string> _log = new();
        private readonly List<string> _mx = [];

        private void Log(string line) => _log.Enqueue(line);

        public async Task<MailDiscoveryResult> ExecuteAsync()
        {
            var proposals = await Task.WhenAll(AutoconfigAsync(), SrvAsync(), IspdbAsync(domain, "ispdb"), MxAsync(), AutodiscoverAsync()).ConfigureAwait(false);
            foreach (var proposal in proposals.OfType<Proposal>())
            {
                Log($"verify {proposal.Source}");
                var imap = await FirstWorkingAsync(proposal.Imap, smtp: false).ConfigureAwait(false);
                if (imap is null)
                {
                    continue;
                }

                var smtp = await FirstWorkingAsync(proposal.Smtp, smtp: true).ConfigureAwait(false)
                    ?? await ProbeHostsAsync(Candidates(imap.Host), smtp: true).ConfigureAwait(false);
                return Result(proposal.Source, imap, smtp, proposal.Auth, verified: true);
            }

            Log("probe usual host names");
            var probedImap = await ProbeHostsAsync(Candidates(null), smtp: false).ConfigureAwait(false);
            if (probedImap is not null)
            {
                var probedSmtp = await ProbeHostsAsync(Candidates(probedImap.Host), smtp: true).ConfigureAwait(false);
                return Result("probe", probedImap, probedSmtp, ["password"], verified: true);
            }

            // Nothing answered: the manual form starts from the best guess, marked unverified.
            var guess = proposals.OfType<Proposal>().FirstOrDefault();
            return Result(guess?.Source ?? "none", guess?.Imap.FirstOrDefault(), guess?.Smtp.FirstOrDefault(), guess?.Auth ?? ["password"], verified: false);
        }

        private MailDiscoveryResult Result(string source, Server? imap, Server? smtp, IReadOnlyList<string> auth, bool verified)
        {
            var reasons = new List<string>();
            var foreign = new[] { imap, smtp }.OfType<Server>().Select(s => s.Host).Where(h => !InDomain(h, domain)).Distinct().ToList();
            if (foreign.Count > 0)
            {
                reasons.Add(Loc.T("Serwer {0} nie należy do domeny {1}. Sprawdź, czy to Twój dostawca, zanim podasz hasło.", string.Join(", ", foreign), domain));
            }

            if (!verified && imap is not null)
            {
                reasons.Add(Loc.T("Nie udało się połączyć z serwerem; to tylko propozycja."));
            }

            if (auth.Contains("oauth2") && !auth.Contains("password"))
            {
                reasons.Add(Loc.T("Ten dostawca zwykle wymaga logowania OAuth2 albo hasła aplikacji; zwykłe hasło może nie zadziałać."));
            }

            var confidence = !verified ? "low" : source is "autoconfig" or "srv" or "ispdb" ? "high" : "medium";
            Log($"result {source}, {confidence}: imap {Describe(imap)}, smtp {Describe(smtp)}");
            return new MailDiscoveryResult(Settings(imap), Settings(smtp), auth, imap is null ? "none" : source, confidence,
                reasons.Count > 0, reasons.Count > 0 ? string.Join(" ", reasons) : null, [.. _log]);
        }

        private static MailServerSettings? Settings(Server? s) => s is null ? null : new(s.Host, s.Port, s.Ssl ? "ssl" : "starttls", s.Username);

        private static string Describe(Server? s) => s is null ? "-" : $"{s.Host}:{s.Port} {(s.Ssl ? "ssl" : "starttls")}";

        /// <summary>MX hosts, then the usual names; <paramref name="first"/> (the IMAP server found) leads for SMTP.</summary>
        private List<string> Candidates(string? first)
        {
            var hosts = new List<string>();
            if (first is not null)
            {
                hosts.Add(first);
            }

            lock (_mx)
            {
                hosts.AddRange(_mx);
            }

            hosts.AddRange([$"mail.{domain}", $"imap.{domain}", $"smtp.{domain}", domain]);
            return [.. hosts.Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        // Stage 1: the domain's own autoconfig, HTTPS only.
        private async Task<Proposal?> AutoconfigAsync()
        {
            var bodies = await Task.WhenAll(
                FetchAsync("autoconfig", $"https://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(address)}"),
                FetchAsync("autoconfig", $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml")).ConfigureAwait(false);
            return bodies.Select(body => FromAutoconfig(body, "autoconfig")).FirstOrDefault(p => p is not null);
        }

        // Stage 3 (and 4 for an MX host's domain): Thunderbird's database.
        private async Task<Proposal?> IspdbAsync(string name, string source) =>
            FromAutoconfig(await FetchAsync(source, $"https://autoconfig.thunderbird.net/v1.1/{name}").ConfigureAwait(false), source);

        private Proposal? FromAutoconfig(string? xml, string source)
        {
            if (xml is null)
            {
                return null;
            }

            try
            {
                var (imap, smtp, auth) = ParseAutoconfig(xml, address, local, domain, Log);
                if (imap.Count > 0)
                {
                    return new Proposal(source, imap, smtp, auth.Count > 0 ? auth : ["password"]);
                }

                Log($"{source}: no usable IMAP server");
            }
            catch (XmlException ex)
            {
                Log($"{source}: invalid XML ({ex.Message})");
            }

            return null;
        }

        // Stage 2: RFC 6186 / RFC 8314 SRV records; implicit TLS first, a "." target means "no such service".
        private async Task<Proposal?> SrvAsync()
        {
            var lists = await Task.WhenAll(
                SrvAsync("_imaps._tcp", ssl: true), SrvAsync("_imap._tcp", ssl: false),
                SrvAsync("_submissions._tcp", ssl: true), SrvAsync("_submission._tcp", ssl: false)).ConfigureAwait(false);
            var imap = lists[0].Concat(lists[1]).ToList();
            return imap.Count > 0 ? new Proposal("srv", imap, [.. lists[2], .. lists[3]], ["password"]) : null;
        }

        private async Task<List<Server>> SrvAsync(string service, bool ssl)
        {
            var name = $"{service}.{domain}";
            var records = await AttemptAsync($"srv {name}", ct => owner._network.SrvAsync(name, ct)).ConfigureAwait(false) ?? [];
            var servers = new List<Server>();
            foreach (var record in records.OrderBy(r => r.Priority).ThenByDescending(r => r.Weight))
            {
                var target = record.Target.TrimEnd('.').ToLowerInvariant();
                if (target.Length == 0)
                {
                    Log($"  {name}: target '.', service not offered");
                    continue;
                }

                if (Acceptable(target, record.Port, ssl, Log))
                {
                    servers.Add(new Server(target, record.Port, ssl, address));
                }
            }

            Log($"  {name}: {servers.Count} server(s)");
            return servers;
        }

        // Stage 4: MX. Google and Microsoft 365 are known (OAuth2); another provider's domain is looked up in the ISPDB.
        private async Task<Proposal?> MxAsync()
        {
            var records = await AttemptAsync($"mx {domain}", ct => owner._network.MxAsync(domain, ct)).ConfigureAwait(false) ?? [];
            var hosts = records.OrderBy(r => r.Preference).Select(r => r.Host.TrimEnd('.').ToLowerInvariant()).Where(IsHostName).ToList();
            lock (_mx)
            {
                _mx.AddRange(hosts);
            }

            Log($"  mx: {(hosts.Count > 0 ? string.Join(", ", hosts) : "none")}");
            if (hosts.Count == 0)
            {
                return null;
            }

            if (hosts.Any(h => h.EndsWith(".google.com", StringComparison.Ordinal) || h.EndsWith(".googlemail.com", StringComparison.Ordinal)))
            {
                return new Proposal("mx", [new("imap.gmail.com", 993, true, address)], [new("smtp.gmail.com", 465, true, address)], ["oauth2"]);
            }

            if (hosts.Any(h => h.EndsWith(".mail.protection.outlook.com", StringComparison.Ordinal)))
            {
                return new Proposal("mx", [new("outlook.office365.com", 993, true, address)], [new("smtp.office365.com", 587, false, address)], ["oauth2"]);
            }

            var provider = BaseDomain(hosts[0]);
            return provider.Equals(domain, StringComparison.OrdinalIgnoreCase) ? null : await IspdbAsync(provider, "mx").ConfigureAwait(false);
        }

        // Stage 5: Exchange Autodiscover with the address only. A 401 is "no answer": nothing is sent with credentials.
        private async Task<Proposal?> AutodiscoverAsync()
        {
            var request = new XElement(XName.Get("Autodiscover", "http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006"),
                new XElement(XName.Get("Request", "http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006"),
                    new XElement(XName.Get("EMailAddress", "http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006"), address),
                    new XElement(XName.Get("AcceptableResponseSchema", "http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006"),
                        "http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a"))).ToString(SaveOptions.DisableFormatting);
            var bodies = await Task.WhenAll(
                FetchAsync("autodiscover", $"https://autodiscover.{domain}/autodiscover/autodiscover.xml", request),
                FetchAsync("autodiscover", $"https://{domain}/autodiscover/autodiscover.xml", request)).ConfigureAwait(false);
            foreach (var body in bodies.OfType<string>())
            {
                try
                {
                    var (imap, smtp) = ParseAutodiscover(body, address, Log);
                    if (imap.Count > 0)
                    {
                        return new Proposal("autodiscover", imap, smtp, ["password"]);
                    }
                }
                catch (XmlException ex)
                {
                    Log($"autodiscover: invalid XML ({ex.Message})");
                }
            }

            return null;
        }

        /// <summary>The body of a 200 answer, or null. The log names the host and path only, never the address.</summary>
        private async Task<string?> FetchAsync(string stage, string url, string? post = null)
        {
            var uri = new Uri(url);
            var shown = $"{stage} {(post is null ? "GET" : "POST")} {uri.Host}{uri.AbsolutePath}";
            var answer = await AttemptAsync(shown, ct => owner._network.HttpsAsync(uri, post, ct)).ConfigureAwait(false);
            if (answer is null)
            {
                return null;
            }

            Log($"  {shown}: {answer.Status}{(answer.Status == 401 ? " (needs a login: skipped, no credentials are sent)" : string.Empty)}");
            return answer.Status == 200 ? answer.Body : null;
        }

        private async Task<Server?> FirstWorkingAsync(IReadOnlyList<Server> servers, bool smtp)
        {
            var answers = await Task.WhenAll(servers.Select(s => ProbeAsync(s, smtp))).ConfigureAwait(false);
            var index = Array.FindIndex(answers, a => a.Outcome == ProbeOutcome.Ok);
            return index >= 0 ? servers[index] : null;
        }

        /// <summary>
        /// Stage 6: implicit TLS, then STARTTLS on each candidate. A certificate for another name (shared hosting) makes
        /// that name a candidate; the mismatched certificate itself is never accepted.
        /// </summary>
        private async Task<Server?> ProbeHostsAsync(List<string> hosts, bool smtp)
        {
            (int Port, bool Ssl)[] ports = smtp ? [(465, true), (587, false)] : [(993, true), (143, false)];
            var tried = new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);
            for (var round = 0; round < 2 && hosts.Count > 0; round++)
            {
                var servers = hosts.SelectMany(h => ports.Select(p => new Server(h, p.Port, p.Ssl, address))).ToList();
                var answers = await Task.WhenAll(servers.Select(s => ProbeAsync(s, smtp))).ConfigureAwait(false);
                var index = Array.FindIndex(answers, a => a.Outcome == ProbeOutcome.Ok);
                if (index >= 0)
                {
                    return servers[index];
                }

                hosts = [.. answers.Where(a => a.Outcome == ProbeOutcome.CertificateMismatch)
                    .SelectMany(a => a.CertificateNames)
                    .Select(n => n.TrimEnd('.').ToLowerInvariant())
                    .Where(n => IsHostName(n) && tried.Add(n))];
                if (hosts.Count > 0)
                {
                    Log($"  certificate names to try: {string.Join(", ", hosts)}");
                }
            }

            return null;
        }

        private async Task<ProbeAnswer> ProbeAsync(Server server, bool smtp)
        {
            var shown = $"probe {(smtp ? "smtp" : "imap")} {Describe(server)}";
            var answer = await AttemptAsync(shown, ct => owner._network.ProbeAsync(server.Host, server.Port, smtp, server.Ssl, ct)).ConfigureAwait(false);
            if (answer is null)
            {
                return new ProbeAnswer(ProbeOutcome.Network, [], "timeout"); // AttemptAsync logged it
            }

            Log($"  {shown}: {answer.Outcome switch
            {
                ProbeOutcome.Ok => "ok",
                ProbeOutcome.NoStartTls => "no STARTTLS, refused",
                ProbeOutcome.CertificateMismatch => $"certificate is for {string.Join(", ", answer.CertificateNames)}, refused",
                var other => $"{other.ToString().ToLowerInvariant()} {answer.Detail}".TrimEnd(),
            }}");
            return answer;
        }

        /// <summary>One network step with its own time limit inside the total one; failures become null and a log line.</summary>
        private async Task<T?> AttemptAsync<T>(string shown, Func<CancellationToken, Task<T>> step) where T : class
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(total);
            attempt.CancelAfter(owner._attempt);
            try
            {
                return await step(attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log($"  {shown}: timeout");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException or InvalidDataException or TimeoutException or XmlException)
            {
                Log($"  {shown}: {ex.GetType().Name} {ex.Message}");
            }

            return null;
        }
    }
}
