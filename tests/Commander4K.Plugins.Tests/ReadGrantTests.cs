using Commander4K.Core.Execution;
using Commander4K.Core.Plugins;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.Tests;

public sealed class ReadGrantTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("c4k-grant-").FullName;
    private readonly AuditLog _audit = new(null);
    private readonly ScopedPluginHost _host;

    public ReadGrantTests()
    {
        var router = new VfsRouter(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
        _host = new ScopedPluginHost(new PluginManifest("tests.grant", "Grant", "1.0.0", "a", "d", []), new NullUi(), router, new HttpClient(), _audit);
        Directory.CreateDirectory(Path.Combine(_root, "X", "sub"));
        Directory.CreateDirectory(Path.Combine(_root, "Xy"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "a.txt.bak"), "bak");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "s");
        File.WriteAllText(Path.Combine(_root, "X", "sub", "c.txt"), "c");
        File.WriteAllText(Path.Combine(_root, "Xy", "d.txt"), "d");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private static TransferItem File_(string path) => new(path, Path.GetFileName(path), TransferItemKind.File, 1);

    private static TransferItem Folder(string path) => new(path, Path.GetFileName(path), TransferItemKind.Folder, 0);

    private async Task AssertReadableAsync(string path)
    {
        await using var stream = await _host.OpenReadAsync(path, Token);
        Assert.True(stream.CanRead);
    }

    private Task AssertDeniedAsync(string path) =>
        Assert.ThrowsAsync<PluginPermissionException>(() => _host.OpenReadAsync(path, Token));

    [Fact]
    public async Task Granted_file_is_readable_neighbour_is_not()
    {
        using var grant = _host.GrantRead([File_(P("a.txt"))]);

        await AssertReadableAsync(P("a.txt"));
        await AssertDeniedAsync(P("b.txt"));
    }

    [Fact]
    public async Task Granted_folder_covers_descendants()
    {
        using var grant = _host.GrantRead([Folder(P("X"))]);

        Assert.NotEmpty(await _host.ListAsync(P("X"), Token));
        await AssertReadableAsync(P("X", "sub", "c.txt"));
    }

    [Fact]
    public async Task Grant_matches_normalized_paths_not_name_prefixes()
    {
        using var grant = _host.GrantRead([Folder(P("X")), File_(P("a.txt"))]);

        await AssertDeniedAsync(P("Xy", "d.txt"));
        await AssertDeniedAsync(P("a.txt.bak"));
        await AssertDeniedAsync(P("X") + @"\..\secret.txt");
        await AssertDeniedAsync(P("X") + "/../secret.txt");
        await AssertDeniedAsync(P("X", "..", "b.txt"));
        await AssertDeniedAsync("relative.txt");
        await AssertDeniedAsync("sftp://host/" + P("a.txt"));

        // Separators and case do not matter for local paths.
        await AssertReadableAsync(P("X", "sub", "c.txt").Replace('\\', '/').ToUpperInvariant());
        await AssertReadableAsync(P("X", "sub", "..", "sub", "c.txt"));
        await AssertReadableAsync(P("a.txt").ToUpperInvariant());
    }

    [Fact]
    public async Task Grant_ends_when_scope_is_disposed_after_failure()
    {
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var grant = _host.GrantRead([File_(P("a.txt"))]);
            throw new InvalidOperationException("boom");
        }));

        await AssertDeniedAsync(P("a.txt"));
    }

    [Fact]
    public async Task Overlapping_grants_count_and_release_once()
    {
        var first = _host.GrantRead([File_(P("a.txt"))]);
        var second = _host.GrantRead([File_(P("a.txt"))]);

        first.Dispose();
        first.Dispose(); // A second dispose must not release the other holder's share.
        await AssertReadableAsync(P("a.txt"));

        second.Dispose();
        await AssertDeniedAsync(P("a.txt"));
    }

    [Fact]
    public async Task Grants_are_audited()
    {
        using (_host.GrantRead([File_(P("a.txt")), Folder(P("X")), File_("relative.txt")]))
        {
            await AssertReadableAsync(P("a.txt"));
        }

        // Each granted path is named; a path that was not granted is logged as refused, not counted.
        var entries = _audit.Recent("tests.grant");
        Assert.Contains(entries, e => e.Operation == "fs.grant" && e.Detail == "file " + P("a.txt") && e.Outcome == AuditOutcome.Allowed);
        Assert.Contains(entries, e => e.Operation == "fs.grant" && e.Detail == "folder " + P("X") && e.Outcome == AuditOutcome.Allowed);
        Assert.Contains(entries, e => e.Operation == "fs.grant" && e.Detail.EndsWith("relative.txt", StringComparison.Ordinal) && e.Outcome == AuditOutcome.Denied);
        Assert.Equal(3, entries.Count(e => e.Operation == "fs.grant"));
        Assert.Contains(entries, e => e.Operation == "fs.grant.end" && e.Detail == "2 paths");
        Assert.Contains(entries, e => e.Operation == "fs.read" && e.Outcome == AuditOutcome.Allowed && e.Detail.EndsWith("(granted)", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Operation == "fs.grant.end");
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class NullUi : IPluginUi
    {
        public string? ActivePath => null;

        public string? OtherPath => null;

        public IReadOnlyList<VfsEntry> SelectedEntries => [];

        public Task NavigateAsync(string path, string? focusName) => Task.CompletedTask;

        public void Notify(string message, bool isError, string? target)
        {
        }

        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<string?> PromptAsync(string title, string message, string initial) => Task.FromResult<string?>(null);

        public Task EditFileAsync(string path) => Task.CompletedTask;

        public Task OpenPanelAsync(string panelPluginId, string? argument) => Task.CompletedTask;

        public Task OpenUrlAsync(string url) => Task.CompletedTask;

        public Task SearchWebAsync(string query, string? engine) => Task.CompletedTask;

        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }
}
