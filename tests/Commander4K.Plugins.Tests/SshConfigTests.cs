using Commander4K.Plugins.BuiltIn.Remote;

namespace Commander4K.Plugins.Tests;

public sealed class SshConfigTests
{
    private const string Config = """
        # comment
        Host v8 *.v8.example !old.v8.example
            User deploy
            IdentityFile ~/.ssh/v8/v8_deploy

        Host=other
            User nobody
            IdentityFile ~/.ssh/other

        Host *
            User fallback
            IdentityFile "%d/.ssh/common key"
        """;

    private static readonly string Ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    [Fact]
    public void Matching_blocks_add_keys_and_first_user_wins()
    {
        var config = SshConfig.Parse(Config, "build.v8.example");

        Assert.Equal("deploy", config.User);
        Assert.Equal([Path.Combine(Ssh, "v8", "v8_deploy"), Path.Combine(Ssh, "common key")], config.IdentityFiles);
    }

    [Fact]
    public void Negated_and_unmatched_hosts_get_only_the_wildcard_block()
    {
        foreach (var host in new[] { "old.v8.example", "elsewhere" })
        {
            var config = SshConfig.Parse(Config, host);

            Assert.Equal("fallback", config.User);
            Assert.Equal([Path.Combine(Ssh, "common key")], config.IdentityFiles);
        }
    }
}
