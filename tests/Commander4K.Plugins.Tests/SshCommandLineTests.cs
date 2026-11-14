using Commander4K.Plugins.BuiltIn.Remote;

namespace Commander4K.Plugins.Tests;

public sealed class SshCommandLineTests
{
    [Fact]
    public void Saved_login_gives_key_user_and_folder()
    {
        var args = SshCommandLine.For("sftp://v8.example:2222/var/it's here", new RemoteLogin("ubuntu", PrivateKeyFile: @"C:\keys\id"));

        Assert.Equal(
            ["-t", "-p", "2222", "-i", @"C:\keys\id", "-l", "ubuntu", "--", "v8.example", @"cd -- '/var/it'\''s here' || cd && exec $SHELL -l"],
            args);
    }

    [Fact]
    public void User_in_the_path_wins_and_a_password_is_never_passed()
    {
        var args = SshCommandLine.For("sftp://me:secret@host/", new RemoteLogin("other", "pw"));

        Assert.Equal(["-t", "-p", "22", "-l", "me", "--", "host"], args);
    }

    [Theory]
    [InlineData("sftp://host/a;rm -rf x")] // Windows Terminal would split the command there
    [InlineData("sftp://host/a\"b")]
    public void Folder_that_cannot_be_quoted_starts_at_home(string path) =>
        Assert.Equal("host", SshCommandLine.For(path, null)![^1]);

    [Theory]
    [InlineData("ftp://host/")]
    [InlineData(@"C:\Windows")]
    [InlineData("sftp://-oProxyCommand=calc/")]
    [InlineData("sftp://a;b@host/")]
    public void Not_an_ssh_target(string path) => Assert.Null(SshCommandLine.For(path, null));
}
