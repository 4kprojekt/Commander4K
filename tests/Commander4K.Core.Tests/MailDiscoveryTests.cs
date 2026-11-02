using System.Collections.Concurrent;
using Commander4K.Core.Mail;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class MailDiscoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Autoconfig = """
        <clientConfig version="1.1"><emailProvider id="example.com">
          <incomingServer type="imap"><hostname>imap.example.com</hostname><port>993</port><socketType>SSL</socketType>
            <username>%EMAILLOCALPART%</username><authentication>password-cleartext</authentication></incomingServer>
          <incomingServer type="pop3"><hostname>pop.example.com</hostname><port>995</port><socketType>SSL</socketType></incomingServer>
          <outgoingServer type="smtp"><hostname>smtp.%EMAILDOMAIN%</hostname><port>587</port><socketType>STARTTLS</socketType>
            <username>%EMAILADDRESS%</username><authentication>OAuth2</authentication></outgoingServer>
        </emailProvider></clientConfig>
        """;

    private static Task<MailDiscoveryResult> Discover(FakeNetwork net, string email = "jan@example.com") =>
        new MailDiscovery(net, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5)).DiscoverAsync(email, Token);

    [Fact]
    public async Task Autoconfig_wins_with_placeholders_filled()
    {
        var net = new FakeNetwork().Http("https://autoconfig.example.com/mail/config-v1.1.xml?emailaddress=jan%40example.com", Autoconfig).Ok("imap.example.com", 993).Ok("smtp.example.com", 587);
        net.Ispdb("example.com", Autoconfig.Replace("imap.example.com", "other.example.com", StringComparison.Ordinal));

        var result = await Discover(net);

        Assert.Equal("autoconfig", result.Source);
        Assert.Equal("high", result.Confidence);
        Assert.Equal(new MailServerSettings("imap.example.com", 993, "ssl", "jan"), result.Imap);
        Assert.Equal(new MailServerSettings("smtp.example.com", 587, "starttls", "jan@example.com"), result.Smtp);
        Assert.Equal(["password", "oauth2"], result.AuthMethods);
        Assert.False(result.RequiresUserConfirmation);
    }

    [Fact]
    public async Task Srv_comes_before_ispdb_and_a_dot_target_is_skipped()
    {
        var net = new FakeNetwork()
            .Srv("_imaps._tcp.example.com", new SrvRecord(".", 0, 0, 0))
            .Srv("_imap._tcp.example.com", new SrvRecord("b.example.com.", 143, 20, 0), new SrvRecord("a.example.com.", 143, 10, 0))
            .Srv("_submission._tcp.example.com", new SrvRecord("out.example.com.", 587, 0, 0))
            .Ok("a.example.com", 143).Ok("b.example.com", 143).Ok("out.example.com", 587).Ok("imap.example.com", 993).Ok("smtp.example.com", 587);
        net.Ispdb("example.com", Autoconfig);

        var result = await Discover(net);

        Assert.Equal("srv", result.Source);
        Assert.Equal(new MailServerSettings("a.example.com", 143, "starttls", "jan@example.com"), result.Imap);
        Assert.Equal("out.example.com", result.Smtp?.Host);
        Assert.Contains(result.Attempts, a => a.Contains("target '.'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ispdb_is_used_when_the_domain_has_nothing()
    {
        var net = new FakeNetwork().Ok("imap.example.com", 993).Ok("smtp.example.com", 587);
        net.Ispdb("example.com", Autoconfig);

        var result = await Discover(net);

        Assert.Equal("ispdb", result.Source);
        Assert.Equal("imap.example.com", result.Imap?.Host);
    }

    [Fact]
    public async Task Google_mx_means_gmail_servers_and_oauth2()
    {
        var net = new FakeNetwork().Mx("example.com", "aspmx.l.google.com.").Ok("imap.gmail.com", 993).Ok("smtp.gmail.com", 465);

        var result = await Discover(net);

        Assert.Equal("mx", result.Source);
        Assert.Equal("imap.gmail.com", result.Imap?.Host);
        Assert.Equal(["oauth2"], result.AuthMethods);
        Assert.True(result.RequiresUserConfirmation); // gmail.com is not example.com, and the password may not work
    }

    [Fact]
    public async Task Mx_provider_domain_is_looked_up_in_ispdb()
    {
        var net = new FakeNetwork().Mx("example.com", "mx1.provider.co.uk.").Ok("imap.provider.co.uk", 993).Ok("smtp.provider.co.uk", 465);
        net.Ispdb("provider.co.uk", Autoconfig.Replace("imap.example.com", "imap.provider.co.uk", StringComparison.Ordinal)
            .Replace("smtp.%EMAILDOMAIN%</hostname><port>587</port><socketType>STARTTLS", "smtp.provider.co.uk</hostname><port>465</port><socketType>SSL", StringComparison.Ordinal));

        var result = await Discover(net);

        Assert.Equal("mx", result.Source);
        Assert.Equal("imap.provider.co.uk", result.Imap?.Host);
        Assert.True(result.RequiresUserConfirmation);
        Assert.Contains("imap.provider.co.uk", result.ConfirmationReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Autodiscover_sends_the_address_only_and_401_is_no_answer()
    {
        var net = new FakeNetwork().Status("https://autodiscover.example.com/autodiscover/autodiscover.xml", 401)
            .Http("https://example.com/autodiscover/autodiscover.xml", """
                <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006"><Response xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a">
                <Account><Protocol><Type>IMAP</Type><Server>mail.example.com</Server><Port>993</Port><SSL>on</SSL></Protocol>
                <Protocol><Type>SMTP</Type><Server>mail.example.com</Server><Port>587</Port><Encryption>TLS</Encryption></Protocol></Account></Response></Autodiscover>
                """).Ok("mail.example.com", 993).Ok("mail.example.com", 587);

        var result = await Discover(net);

        Assert.Equal("autodiscover", result.Source);
        Assert.Equal(new MailServerSettings("mail.example.com", 587, "starttls", "jan@example.com"), result.Smtp);
        Assert.All(net.Posts, body => Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Attempts, a => a.Contains("401", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shared_hosting_certificate_name_becomes_the_server()
    {
        var net = new FakeNetwork().Mx("example.com", "mail.example.com.")
            .Mismatch("mail.example.com", 993, "h107.hoster.net").Mismatch("mail.example.com", 465, "h107.hoster.net")
            .Ok("h107.hoster.net", 993).Ok("h107.hoster.net", 465);

        var result = await Discover(net);

        Assert.Equal("probe", result.Source);
        Assert.Equal(new MailServerSettings("h107.hoster.net", 993, "ssl", "jan@example.com"), result.Imap);
        Assert.Equal(new MailServerSettings("h107.hoster.net", 465, "ssl", "jan@example.com"), result.Smtp);
        Assert.True(result.RequiresUserConfirmation);
        Assert.DoesNotContain(net.Probed, p => p.StartsWith("mail.example.com", StringComparison.Ordinal) && p.EndsWith("accepted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Server_without_starttls_is_refused()
    {
        var net = new FakeNetwork().NoStartTls("mail.example.com", 143).NoStartTls("mail.example.com", 587);

        var result = await Discover(net);

        Assert.Null(result.Imap);
        Assert.Equal("none", result.Source);
        Assert.Equal("low", result.Confidence);
        Assert.Contains(result.Attempts, a => a.Contains("no STARTTLS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_xml_and_dtd_are_refused_and_timeouts_do_not_stop_the_rest()
    {
        var net = new FakeNetwork()
            .Http("https://autoconfig.example.com/mail/config-v1.1.xml?emailaddress=jan%40example.com", "<clientConfig><emailProvider>")
            .Http("https://example.com/.well-known/autoconfig/mail/config-v1.1.xml", """<!DOCTYPE x [<!ENTITY e SYSTEM "file:///c:/windows/win.ini">]><clientConfig>&e;</clientConfig>""")
            .Hang("_imaps._tcp.example.com")
            .Ok("imap.example.com", 993).Ok("smtp.example.com", 465);

        var result = await Discover(net);

        Assert.Equal("probe", result.Source);
        Assert.Equal("imap.example.com", result.Imap?.Host);
        Assert.Contains(result.Attempts, a => a.Contains("invalid XML", StringComparison.Ordinal));
        Assert.Contains(result.Attempts, a => a.Contains("timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plain_text_and_mismatched_ports_from_a_config_are_refused()
    {
        var net = new FakeNetwork().Ok("imap.example.com", 143).Ok("imap.example.com", 993);
        net.Ispdb("example.com", Autoconfig.Replace("<port>993</port><socketType>SSL", "<port>143</port><socketType>plain", StringComparison.Ordinal));

        var result = await Discover(net);

        Assert.NotEqual("ispdb", result.Source);
        Assert.Contains(result.Attempts, a => a.Contains("no plain text", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("jan@zażółć.pl", "xn--za-6ja4f8n1l.pl")]
    [InlineData(" Jan@Example.COM ", "example.com")]
    public void Address_domain_becomes_punycode(string email, string domain) =>
        Assert.Equal(domain, MailDiscovery.ParseAddress(email).Domain);

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("a@b@example.com")]
    [InlineData("jan@exa mple.com")]
    [InlineData("jan@localhost")]
    [InlineData("jan@example.com/../x")]
    public void Bad_addresses_are_refused(string email) =>
        Assert.Throws<ArgumentException>(() => MailDiscovery.ParseAddress(email));

    [Fact]
    public void Dns_name_pointers_cannot_loop()
    {
        var packet = new byte[] { 0xC0, 0x00 };
        var offset = 0;
        Assert.Throws<IOException>(() => DnsWire.ReadName(packet, ref offset));
    }

    /// <summary>
    /// Manual: <c>C4K_TEST_DISCOVER=dev@4kprojekt.pl</c>. Prints every attempt; no password exists anywhere in discovery.
    /// </summary>
    [Fact]
    public async Task Real_domain()
    {
        var email = Environment.GetEnvironmentVariable("C4K_TEST_DISCOVER");
        Assert.SkipWhen(string.IsNullOrEmpty(email), "C4K_TEST_DISCOVER is not set");
        var result = await MailDiscovery.Default.DiscoverAsync(email, Token);
        foreach (var line in result.Attempts)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{result.Source} {result.Confidence} imap={result.Imap} smtp={result.Smtp} confirm={result.RequiresUserConfirmation}: {result.ConfirmationReason}");
        Assert.NotNull(result.Imap);
    }

    private sealed class FakeNetwork : IMailDiscoveryNetwork
    {
        private readonly Dictionary<string, HttpAnswer> _http = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SrvRecord[]> _srv = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MxRecord[]> _mx = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ProbeAnswer> _probes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hang = new(StringComparer.Ordinal);

        public ConcurrentBag<string> Posts { get; } = [];

        public ConcurrentBag<string> Probed { get; } = [];

        public FakeNetwork Http(string url, string body)
        {
            _http[url] = new HttpAnswer(200, body);
            return this;
        }

        public FakeNetwork Status(string url, int status)
        {
            _http[url] = new HttpAnswer(status, null);
            return this;
        }

        public FakeNetwork Ispdb(string domain, string body) => Http($"https://autoconfig.thunderbird.net/v1.1/{domain}", body);

        public FakeNetwork Srv(string name, params SrvRecord[] records)
        {
            _srv[name] = records;
            return this;
        }

        public FakeNetwork Mx(string domain, string host)
        {
            _mx[domain] = [new MxRecord(host, 10)];
            return this;
        }

        public FakeNetwork Hang(string name)
        {
            _hang.Add(name);
            return this;
        }

        public FakeNetwork Ok(string host, int port) => Probe(host, port, new ProbeAnswer(ProbeOutcome.Ok, []));

        public FakeNetwork NoStartTls(string host, int port) => Probe(host, port, new ProbeAnswer(ProbeOutcome.NoStartTls, []));

        public FakeNetwork Mismatch(string host, int port, string name) => Probe(host, port, new ProbeAnswer(ProbeOutcome.CertificateMismatch, [name]));

        private FakeNetwork Probe(string host, int port, ProbeAnswer answer)
        {
            _probes[$"{host}:{port}"] = answer;
            return this;
        }

        public Task<HttpAnswer> HttpsAsync(Uri uri, string? postXml, CancellationToken ct)
        {
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            if (postXml is not null)
            {
                Posts.Add(postXml);
            }

            return Task.FromResult(_http.GetValueOrDefault(uri.OriginalString) ?? new HttpAnswer(404, null));
        }

        public async Task<IReadOnlyList<SrvRecord>> SrvAsync(string name, CancellationToken ct)
        {
            if (_hang.Contains(name))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return _srv.GetValueOrDefault(name) ?? [];
        }

        public Task<IReadOnlyList<MxRecord>> MxAsync(string domain, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MxRecord>>(_mx.GetValueOrDefault(domain) ?? []);

        public Task<ProbeAnswer> ProbeAsync(string host, int port, bool smtp, bool ssl, CancellationToken ct)
        {
            var answer = _probes.GetValueOrDefault($"{host}:{port}") ?? new ProbeAnswer(ProbeOutcome.Network, [], "refused");
            Probed.Add($"{host}:{port} {(answer.Outcome == ProbeOutcome.Ok ? "accepted" : answer.Outcome.ToString())}");
            return Task.FromResult(answer);
        }
    }
}
