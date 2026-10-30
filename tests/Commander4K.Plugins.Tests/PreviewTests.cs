using System.Collections.Concurrent;
using Commander4K.Core.Execution;
using Commander4K.Core.Plugins;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;
using Commander4K.Plugins.BuiltIn.Previews;

namespace Commander4K.Plugins.Tests;

public sealed class PreviewTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("c4k-preview-").FullName;
    private readonly AuditLog _audit = new(null);
    private readonly VfsRouter _router = new(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
    private readonly HttpClient _http = new();

    public PreviewTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "big", "deep"));
        Directory.CreateDirectory(Path.Combine(_root, "small"));
        File.WriteAllBytes(Path.Combine(_root, "big", "a.bin"), new byte[300_000]);
        File.WriteAllBytes(Path.Combine(_root, "big", "deep", "b.bin"), new byte[200_000]);
        File.WriteAllBytes(Path.Combine(_root, "small", "c.bin"), new byte[1_000]);
        File.WriteAllText(Path.Combine(_root, "readme.md"), "# Tytuł\n\n**zażółć**");
        File.WriteAllBytes(Path.Combine(_root, "data.bin"), [1, 0, 2, 0, 3]);
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _audit.DisposeAsync();
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Folder_preview_lists_immediately_then_fills_recursive_sizes()
    {
        var plugin = new FolderPreviewPlugin();
        var updates = new ConcurrentQueue<FolderPreview>();
        var progress = new SynchronousProgress<PreviewContent>(p => updates.Enqueue((FolderPreview)p));

        var result = (FolderPreview)await plugin.LoadAsync(Dir(_root), Host(plugin.Manifest), progress, TestToken);

        Assert.True(result.Complete);
        Assert.Equal(500_000, result.Items.Single(i => i.Name == "big").Size);
        Assert.Equal(1_000, result.Items.Single(i => i.Name == "small").Size);
        Assert.Contains(updates, u => !u.Complete && u.Items.Any(i => i.IsDirectory && i.Size is null));
        Assert.Contains(_audit.Recent(plugin.Id), e => e.Operation == "fs.size");
    }

    [Fact]
    public async Task Folder_preview_is_cancelled_with_the_token()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FolderPreviewPlugin().LoadAsync(Dir(_root), Host(new FolderPreviewPlugin().Manifest), null, cts.Token));
    }

    [Fact]
    public async Task Markdown_and_text_previews_read_through_the_host()
    {
        var markdown = new MarkdownPreviewPlugin();
        var text = new TextPreviewPlugin();
        var readme = FileEntry(Path.Combine(_root, "readme.md"));

        Assert.True(markdown.CanPreview(readme));
        var md = Assert.IsType<MarkdownPreview>(await markdown.LoadAsync(readme, Host(markdown.Manifest), null, TestToken));
        Assert.Contains("**zażółć**", md.Markdown, StringComparison.Ordinal);
        Assert.True(md.CanEdit);

        Assert.IsType<HexPreview>(await text.LoadAsync(FileEntry(Path.Combine(_root, "data.bin")), Host(text.Manifest), null, TestToken));
        Assert.Contains(_audit.Recent(markdown.Id), e => e.Operation == "fs.read");
    }

    [Fact]
    public async Task Saving_needs_fs_write_in_the_previews_manifest()
    {
        var image = new ImagePreviewPlugin();
        var markdown = new MarkdownPreviewPlugin();
        var target = Path.Combine(_root, "readme.md");

        await Assert.ThrowsAsync<PluginPermissionException>(() => Host(image.Manifest).WriteTextAsync(target, "x", TestToken));
        await Host(markdown.Manifest).WriteTextAsync(target, "# nowy", TestToken);

        Assert.Equal("# nowy", await File.ReadAllTextAsync(target, TestToken));
        Assert.Contains(_audit.Recent(image.Id), e => e.Operation == "fs.write" && e.Outcome == AuditOutcome.Denied);
    }

    private ScopedPluginHost Host(PluginManifest manifest) => new(manifest, new NullUi(), _router, _http, _audit);

    private static VfsEntry Dir(string path) => new(Path.GetFileName(path), path, VfsEntryKind.Directory);

    private static VfsEntry FileEntry(string path) => new(Path.GetFileName(path), path, VfsEntryKind.File, new FileInfo(path).Length);

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class NullUi : IPluginUi
    {
        public string? ActivePath => null;

        public string? OtherPath => null;

        public IReadOnlyList<VfsEntry> SelectedEntries => [];

        public Task NavigateAsync(string path, string? focusName) => Task.CompletedTask;

        public void Notify(string message, bool isError)
        {
        }

        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<string?> PromptAsync(string title, string message, string initial) => Task.FromResult<string?>(null);

        public Task EditFileAsync(string path) => Task.CompletedTask;

        public Task OpenPanelAsync(string panelPluginId, string? argument) => Task.CompletedTask;

        public Task OpenUrlAsync(string url) => Task.CompletedTask;

        public Task SearchWebAsync(string query, string? engine) => Task.CompletedTask;
    }
}
