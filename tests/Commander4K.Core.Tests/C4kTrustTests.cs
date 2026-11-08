using Commander4K.Core.Folders;

namespace Commander4K.Core.Tests;

public sealed class C4kTrustTests : IDisposable
{
    private const string Run = """{ "run": { "command": "build.ps1", "args": ["-Release"] } }""";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "c4k-trust-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "trusted.json");

    private static C4kItem Item(string json, string file = "a.c4k") => C4kItem.Parse(@"C:\Items", file, json);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void Resize_does_not_revoke_trust()
    {
        var store = new C4kTrustStore(StorePath);
        store.Trust(Item(Run));

        Assert.True(store.IsTrusted(Item("""{ "title": "Other", "size": "wide", "run": { "command": "build.ps1", "args": ["-Release"] } }""")));
    }

    [Fact]
    public void Changing_run_revokes_trust()
    {
        var store = new C4kTrustStore(StorePath);
        store.Trust(Item(Run));

        Assert.False(store.IsTrusted(Item("""{ "run": { "command": "format.ps1" } }""")));
    }

    [Fact]
    public void Renamed_file_is_not_trusted()
    {
        var store = new C4kTrustStore(StorePath);
        store.Trust(Item(Run));

        Assert.False(store.IsTrusted(Item(Run, "b.c4k")));
    }

    [Fact]
    public void Trust_survives_reload()
    {
        new C4kTrustStore(StorePath).Trust(Item(Run));

        Assert.True(new C4kTrustStore(StorePath).IsTrusted(Item(Run)));
    }

    [Fact]
    public void Remote_card_needs_trust_and_lists_server()
    {
        var item = Item("""{ "open": "sftp://kamil@vps.example.com/home", "card": true }""");

        Assert.True(item.NeedsTrust);
        Assert.Equal(["SFTP sftp://kamil@vps.example.com/home"], item.TrustSummary());
        Assert.False(Item("""{ "open": "C:\x", "card": true }""").NeedsTrust);
        Assert.False(Item("""{ "open": "sftp://kamil@vps.example.com/home" }""").NeedsTrust);
    }

    [Fact]
    public void Changing_remote_card_open_revokes_trust()
    {
        var store = new C4kTrustStore(StorePath);
        store.Trust(Item("""{ "open": "sftp://kamil@vps.example.com/home", "card": true }"""));

        Assert.True(store.IsTrusted(Item("""{ "title": "T", "size": "wide", "open": "sftp://kamil@vps.example.com/home", "card": true }""")));
        Assert.False(store.IsTrusted(Item("""{ "open": "sftp://kamil@evil.example.com/home", "card": true }""")));
    }

    [Fact]
    public void Summary_lists_run_post_and_ssh()
    {
        var run = Item(Run);
        var post = Item("""{ "card": "api", "api": { "method": "POST", "url": "https://x.test/hook", "body": "{}" } }""");
        var ssh = Item("""{ "card": "ssh", "open": "sftp://root@host/", "user": "root" }""");

        Assert.Equal(["run build.ps1 -Release"], run.TrustSummary());
        Assert.Equal(["POST https://x.test/hook"], post.TrustSummary());
        Assert.Single(ssh.TrustSummary());
        Assert.StartsWith("SSH ", ssh.TrustSummary()[0]);
        Assert.All(new[] { run, post, ssh }, i => Assert.True(i.NeedsTrust));

        var store = new C4kTrustStore(StorePath);
        store.Trust(post);
        Assert.False(store.IsTrusted(Item("""{ "card": "api", "api": { "method": "POST", "url": "https://x.test/other" } }""")));
        store.Trust(ssh);
        Assert.False(store.IsTrusted(Item("""{ "card": "ssh", "open": "sftp://root@host/", "user": "admin" }""")));
    }

    [Fact]
    public void Plain_view_needs_no_trust() => Assert.False(Item("""{ "view": "gallery" }""").NeedsTrust);
}
