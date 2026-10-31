using Commander4K.Core.Execution;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.Tests;

public sealed class LocalFileSystemPluginTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("c4k-local-").FullName;
    private readonly LocalFileSystemPlugin _plugin = new();

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Lists_files_directories_and_hidden_entries()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[42]);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        var hidden = Path.Combine(_root, "secret.txt");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var entries = await ListAsync(_root);

        Assert.Equal(42, entries.Single(e => e.Name == "a.bin").Size);
        Assert.Equal(VfsEntryKind.Directory, entries.Single(e => e.Name == "sub").Kind);
        Assert.True(entries.Single(e => e.Name == "secret.txt").Flags.HasFlag(VfsEntryFlags.Hidden));
    }

    [Fact]
    public async Task Root_lists_drives()
    {
        var drives = await ListAsync(LocalFileSystemPlugin.RootPath);

        Assert.Contains(drives, d => d.Kind == VfsEntryKind.Drive && d.Path.Equals(@"C:\", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32", @"C:\Windows")]
    [InlineData(@"C:\Windows\", @"C:\")]
    [InlineData(@"C:\", LocalFileSystemPlugin.RootPath)]
    [InlineData("C:", LocalFileSystemPlugin.RootPath)]
    [InlineData(LocalFileSystemPlugin.RootPath, null)]
    public void Parent_paths_walk_up_to_drive_list(string path, string? expected) =>
        Assert.Equal(expected, _plugin.GetParentPath(path));

    [Fact]
    public void File_uri_is_translated() =>
        Assert.Equal(@"C:\Users\x", LocalFileSystemPlugin.ToLocalPath("file:///C:/Users/x"));

    [Fact]
    public async Task Missing_directory_surfaces_error_instead_of_empty_listing() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ListAsync(Path.Combine(_root, "nope")));

    [Fact]
    public async Task Watch_reports_debounced_changes()
    {
        var changes = 0;
        using var signal = new SemaphoreSlim(0);
        using var watch = _plugin.Watch(_root, () =>
        {
            Interlocked.Increment(ref changes);
            signal.Release();
        });

        for (var i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"burst{i}.txt"), "x");
        }

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), TestToken));
        await Task.Delay(400, TestToken);
        Assert.InRange(changes, 1, 3);
    }

    [Fact]
    public async Task Directory_size_through_router()
    {
        Directory.CreateDirectory(Path.Combine(_root, "x", "y"));
        File.WriteAllBytes(Path.Combine(_root, "x", "1.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_root, "x", "y", "2.bin"), new byte[234]);
        var router = new VfsRouter(new ResilientAsyncExecutor(), [_plugin]);

        var size = await new DirectorySizeCalculator(router).CalculateAsync(_root, cancellationToken: TestToken);

        Assert.Equal(1234, size.Bytes);
        Assert.Equal(2, size.Files);
        Assert.Equal(2, size.Directories);
    }

    [Fact]
    public async Task Directory_size_counts_unreadable_folders_without_throwing()
    {
        // Under a debugger every thrown exception suspends the whole process; C:\Windows has thousands of these.
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllBytes(Path.Combine(locked, "hidden.bin"), new byte[500]);
        File.WriteAllBytes(Path.Combine(_root, "open.bin"), new byte[10]);
        var deny = new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        var directory = new DirectoryInfo(locked);
        var security = directory.GetAccessControl();
        security.AddAccessRule(deny);
        directory.SetAccessControl(security);

        var thrown = 0;
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception.Message.Contains(locked, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref thrown);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            var router = new VfsRouter(new ResilientAsyncExecutor(), [_plugin]);
            var size = await new DirectorySizeCalculator(router).CalculateAsync(_root, cancellationToken: TestToken);

            Assert.Equal(10, size.Bytes);
            Assert.Equal(1, size.InaccessibleDirectories);
            Assert.Equal(0, thrown);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
            security.RemoveAccessRule(deny);
            directory.SetAccessControl(security); // Dispose deletes the folder.
        }
    }

    private async Task<List<VfsEntry>> ListAsync(string path)
    {
        var result = new List<VfsEntry>();
        await foreach (var entry in _plugin.EnumerateAsync(path, TestToken))
        {
            result.Add(entry);
        }

        return result;
    }
}
