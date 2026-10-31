using Commander4K.Core.Security;

namespace Commander4K.Core.Tests;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "c4k-secrets-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Values_are_encrypted_on_disk()
    {
        new SecretStore(_path).Set("grafana", "tok-123-secret", "grafana.example.com");

        Assert.DoesNotContain("tok-123-secret", File.ReadAllText(_path), StringComparison.Ordinal);
        Assert.Equal("tok-123-secret", new SecretStore(_path).Get("grafana", "GRAFANA.example.com"));
        Assert.True(new SecretStore(_path).Exists("grafana"));
        Assert.False(new SecretStore(_path).Exists("other"));
    }

    [Fact]
    public void Expand_replaces_placeholders()
    {
        var store = new SecretStore(_path);
        store.Set("token", "abc", "api.example.com");
        store.Set("user", "kamil", "api.example.com");

        Assert.Equal("Bearer abc for kamil", store.Expand("Bearer {secret:token} for {secret:user}", "api.example.com"));
        Assert.Equal("no placeholders", store.Expand("no placeholders", "anything"));
    }

    [Fact]
    public void Secret_is_refused_for_another_host()
    {
        var store = new SecretStore(_path);
        store.Set("token", "abc", "api.example.com");

        Assert.Null(store.Get("token", "evil.example.net"));
        var refused = Assert.Throws<SecretHostMismatchException>(() => store.Expand("Bearer {secret:token}", "evil.example.net"));
        Assert.Equal("token", refused.Name);
        Assert.Equal("api.example.com", refused.BoundHost);
    }

    [Fact]
    public void Missing_secret_throws_with_its_name()
    {
        var missing = Assert.Throws<SecretMissingException>(() => new SecretStore(_path).Expand("{secret:grafana}", "x"));

        Assert.Equal("grafana", missing.Name);
    }

    [Fact]
    public void Setting_again_keeps_the_first_host()
    {
        var store = new SecretStore(_path);
        store.Set("token", "abc", "api.example.com");
        store.Set("token", "def", "other.example.com");

        Assert.Equal("def", store.Get("token", "api.example.com"));
        Assert.Null(store.Get("token", "other.example.com"));
    }
}
