using System.Text;
using Commander4K.Core.Execution;
using Commander4K.Core.Jobs;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;
using Commander4K.Plugins.BuiltIn.Remote;

namespace Commander4K.Plugins.Tests;

/// <summary>
/// Runs against real servers when <c>C4K_TEST_SFTP</c> / <c>C4K_TEST_FTP</c> hold a writable folder URL with
/// credentials, e.g. <c>sftp://foo:pass@127.0.0.1:2222/upload</c> (docker: atmoz/sftp) or
/// <c>ftp://user:pass@127.0.0.1:2121/</c> (docker: delfer/alpine-ftp-server). Skipped otherwise.
/// </summary>
public sealed class RemoteServerTests : IAsyncDisposable
{
    private readonly string _local = Directory.CreateTempSubdirectory("c4k-remote-").FullName;
    private readonly RemoteTransferVfsPlugin _remote = new();
    private readonly VfsRouter _router;

    public RemoteServerTests()
    {
        _router = new VfsRouter(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin(), _remote]);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _remote.DisposeAsync();
        Directory.Delete(_local, recursive: true);
    }

    /// <summary>
    /// The terminal's shell, against a server that gives one (atmoz/sftp does not): <c>C4K_TEST_SSH=sftp://foo:pass@127.0.0.1:2223/</c>
    /// with docker <c>linuxserver/openssh-server -e PASSWORD_ACCESS=true -e USER_NAME=foo -e USER_PASSWORD=pass -p 2223:2222</c>.
    /// </summary>
    [Fact]
    public async Task Shell_runs_a_command_and_ends_on_exit()
    {
        var server = Environment.GetEnvironmentVariable("C4K_TEST_SSH");
        Assert.SkipWhen(string.IsNullOrEmpty(server), "C4K_TEST_SSH is not set");

        await using var shell = await _remote.OpenShellAsync(server!, 100, 30, Token);
        shell.Resize(120, 40);
        shell.Write(Encoding.UTF8.GetBytes("echo c4k-$((6*7)); stty size; exit\n"));

        // The read blocks, so it gets its own thread, as in the app; 0 = the shell ended.
        var output = await Task.Factory.StartNew(() =>
        {
            var text = new StringBuilder();
            var buffer = new byte[4096];
            for (int read; (read = shell.Read(buffer)) > 0;)
            {
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            return text.ToString();
        }, Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Contains("c4k-42", output);
        Assert.Contains("40 120", output);
    }

    [Theory]
    [InlineData("C4K_TEST_SFTP")]
    [InlineData("C4K_TEST_FTP")]
    public async Task Upload_stat_move_and_recursive_delete(string variable)
    {
        var server = Environment.GetEnvironmentVariable(variable);
        Assert.SkipWhen(string.IsNullOrEmpty(server), $"{variable} is not set");

        // Paths built by Combine carry no password (as in the app), so register it like the login dialog does.
        var url = new Uri(server!);
        var user = url.UserInfo.Split(':', 2);
        _remote.SetCredentials($"{url.Scheme}://{user[0]}@{url.Host}:{url.Port}", new System.Net.NetworkCredential(user[0], Uri.UnescapeDataString(user[1])));

        var source = Path.Combine(_local, "src");
        Directory.CreateDirectory(Path.Combine(source, "sub", "deeper"));
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "alfa", Token);
        var big = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Commander4K ", 20_000)));
        await File.WriteAllBytesAsync(Path.Combine(source, "sub", "b.bin"), big, Token);
        await File.WriteAllTextAsync(Path.Combine(source, "sub", "deeper", "c.txt"), "gamma", Token);

        var root = _router.Combine(server!, "c4k-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Null(await _router.GetEntryAsync(root, Token));
        await _router.CreateDirectoryAsync(root, Token);
        try
        {
            Assert.Equal(VfsEntryKind.Directory, (await _router.GetEntryAsync(root, Token))?.Kind);

            // Upload a tree: the job creates missing remote folders because stat now reports "not found".
            var sourceEntry = (await _router.GetEntryAsync(source, Token))!.Value;
            await new VfsTransferJob(_router, [sourceEntry], root, TransferMode.Copy, OverwritePolicy.Overwrite).ExecuteAsync(new NoProgress(), Token);

            var remoteSrc = _router.Combine(root, "src");
            var names = new List<string>();
            await foreach (var entry in _router.EnumerateAsync(remoteSrc, Token))
            {
                names.Add(entry.Name);
            }

            Assert.Equal(["a.txt", "sub"], names.Order());
            var remoteBig = _router.Combine(_router.Combine(remoteSrc, "sub"), "b.bin");
            Assert.Equal(big.Length, (await _router.GetEntryAsync(remoteBig, Token))?.Size);
            await using (var read = await _router.OpenReadAsync(remoteBig, Token))
            using (var copy = new MemoryStream())
            {
                await read.CopyToAsync(copy, Token);
                Assert.Equal(big, copy.ToArray());
            }

            // "Skip existing" must really see the existing file now.
            var skip = new VfsTransferJob(_router, [sourceEntry], root, TransferMode.Copy, OverwritePolicy.Skip);
            await skip.ExecuteAsync(new NoProgress(), Token);
            Assert.Equal(3, skip.SkippedFiles);

            // Server-side rename.
            var renamed = _router.Combine(remoteSrc, "a-renamed.txt");
            await _router.MoveAsync(_router.Combine(remoteSrc, "a.txt"), renamed, overwrite: false, Token);
            Assert.Null(await _router.GetEntryAsync(_router.Combine(remoteSrc, "a.txt"), Token));
            Assert.Equal(VfsEntryKind.File, (await _router.GetEntryAsync(renamed, Token))?.Kind);

            await Assert.ThrowsAsync<IOException>(() => _router.DeleteAsync(remoteSrc, recursive: false, Token));
        }
        finally
        {
            await _router.DeleteAsync(root, recursive: true, CancellationToken.None);
        }

        Assert.Null(await _router.GetEntryAsync(root, Token));
    }

    private sealed class NoProgress : IProgress<JobMetrics>
    {
        public void Report(JobMetrics value)
        {
        }
    }
}
