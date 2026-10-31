using System.Collections.Concurrent;
using Commander4K.Core.Execution;
using Commander4K.Core.Folders;
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

    [Fact]
    public async Task Obsidian_vault_preview_shows_links_backlinks_tags_and_a_graph()
    {
        var vault = Path.Combine(_root, "Sejf");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
        Directory.CreateDirectory(Path.Combine(vault, "Projekty"));
        File.WriteAllText(Path.Combine(vault, "Start.md"), "---\ntags: [indeks, plan]\n---\n# Start\n\nWitaj w sejfie, zobacz [[Projekt Ćma]] oraz [[Pomysły|moje pomysły]].\n![[schemat.png]]\n#projekt");
        File.WriteAllText(Path.Combine(vault, "Pomysły.md"), "Lista pomysłów. Wraca do [[Start#Start]] i do [[Brakująca notatka]].\n```\n[[w kodzie się nie liczy]]\n```");
        File.WriteAllText(Path.Combine(vault, "Samotna.md"), "Notatka bez linków.");
        File.WriteAllText(Path.Combine(vault, "Projekty", "Projekt Ćma.md"), "Opis projektu, link [md](../Pomysły.md). #projekt/ćma");
        var plugin = new FolderPreviewPlugin();

        var overview = Assert.IsType<MarkdownPreview>(await plugin.LoadAsync(Dir(vault), Host(plugin.Manifest), null, TestToken)).Markdown;

        Assert.Contains("sejf Obsidian", overview, StringComparison.Ordinal);
        Assert.Contains("4 notatki · 4 powiązania", overview, StringComparison.Ordinal);
        Assert.Contains("```mermaid\nflowchart LR", overview.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("[\"Projekt Ćma\"]", overview, StringComparison.Ordinal);
        Assert.Contains("`#projekt` 1", overview, StringComparison.Ordinal);
        Assert.Contains("`#projekt/ćma` 1", overview, StringComparison.Ordinal); // Nested tags stay separate, as in Obsidian.
        Assert.Contains("4 tagi", overview, StringComparison.Ordinal);
        Assert.Contains("`#indeks` 1", overview, StringComparison.Ordinal);
        Assert.Contains("## Bez powiązań\n\nSamotna", overview.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("`Brakująca notatka` ← Pomysły", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("w kodzie", overview, StringComparison.Ordinal);
        Assert.Contains("> Witaj w sejfie, zobacz Projekt Ćma oraz moje pomysły.", overview, StringComparison.Ordinal);

        // A subfolder of the vault is recognised through the .obsidian folder above it.
        var sub = Assert.IsType<MarkdownPreview>(await plugin.LoadAsync(Dir(Path.Combine(vault, "Projekty")), Host(plugin.Manifest), null, TestToken)).Markdown;
        Assert.Contains("folder w sejfie Obsidian „Sejf”", sub, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folders_without_notes_keep_the_size_view()
    {
        File.WriteAllText(Path.Combine(_root, "small", "notes.md"), "# tylko jedna");

        Assert.IsType<FolderPreview>(await new FolderPreviewPlugin().LoadAsync(Dir(Path.Combine(_root, "small")), Host(new FolderPreviewPlugin().Manifest), null, TestToken));
    }

    [Fact]
    public void Canvas_becomes_a_diagram_with_cards()
    {
        var markdown = CanvasPreviewPlugin.Describe("Plan", """
            { "nodes": [
                { "id": "a", "type": "text", "text": "# Cel\nWydać wersję" },
                { "id": "b", "type": "file", "file": "Projekty/Projekt Ćma.md" },
                { "id": "c", "type": "link", "url": "https://example.com" },
                { "id": "g", "type": "group", "label": "Etap 1" } ],
              "edges": [ { "id": "e1", "fromNode": "a", "toNode": "b", "label": "opis \"x\"" }, { "id": "e2", "fromNode": "b", "toNode": "c" } ] }
            """).ReplaceLineEndings("\n");

        Assert.Contains("4 elementy · 2 połączenia", markdown, StringComparison.Ordinal);
        Assert.Contains("c0[\"Cel\"]", markdown, StringComparison.Ordinal);
        Assert.Contains("c1[\"📄 Projekt Ćma\"]", markdown, StringComparison.Ordinal);
        Assert.Contains("c3[/\"▢ Etap 1\"/]", markdown, StringComparison.Ordinal);
        Assert.Contains("c0 -->|\"opis 'x'\"| c1", markdown, StringComparison.Ordinal);
        Assert.Contains("### Cel\n\n# Cel\nWydać wersję", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task This_pc_lists_drives_known_folders_then_network_entries()
    {
        var local = new LocalFileSystemPlugin { NetworkEntries = Network };
        var entries = new List<VfsEntry>();
        await foreach (var entry in local.EnumerateAsync(LocalFileSystemPlugin.RootPath, TestToken))
        {
            entries.Add(entry);
        }

        Assert.Contains(entries, e => e.Kind == VfsEntryKind.Drive);
        Assert.Contains(entries, e => e.Kind == VfsEntryKind.Directory && e.Path == Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Assert.Contains(entries, e => e.Path == "net://");
        Assert.Equal("NAS", entries[^1].Name); // Streamed after the local part.
        Assert.True(entries.FindIndex(e => e.Kind == VfsEntryKind.Drive) < entries.FindIndex(e => e.Kind == VfsEntryKind.Directory));

        static async IAsyncEnumerable<VfsEntry> Network([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
            yield return new VfsEntry("NAS", "net://nas.lan", VfsEntryKind.Host);
        }
    }

    [Theory]
    [InlineData("sftp://jan@serwer:2222/home/jan", "sftp://jan@serwer:2222")]
    [InlineData("sftp://serwer/", "sftp://@serwer:22")]
    [InlineData("ftp://anna@ftp.example.com/pub", "ftp://anna@ftp.example.com:21")]
    [InlineData("sftp://jan@serwer:22", "sftp://jan@serwer:22")]
    public void Saved_logins_are_keyed_like_the_sessions(string path, string realm) =>
        Assert.Equal(realm, Commander4K.Plugins.BuiltIn.Remote.RemoteTransferVfsPlugin.RealmFor(path));

    [Fact]
    public async Task Folder_card_counts_top_level_and_fills_size_later()
    {
        var plugin = new FolderPreviewPlugin();
        var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        plugin.Sizes.Completed += path => completed.TrySetResult(path);
        var host = Host(plugin.Manifest);

        var first = await plugin.LoadCardAsync(new PreviewCardRequest(Dir(_root), null, null), host, TestToken);

        Assert.Equal("2", Value(first, "Pliki"));
        Assert.Equal("2", Value(first, "Foldery"));
        Assert.Equal("liczę…", Value(first, "Rozmiar"));
        Assert.Contains(first.Actions, a => a.Target == _root);

        Assert.Equal(_root, await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestToken));
        var second = await plugin.LoadCardAsync(new PreviewCardRequest(Dir(_root), null, first), host, TestToken);
        var total = 300_000 + 200_000 + 1_000 + new FileInfo(Path.Combine(_root, "readme.md")).Length + 5;
        Assert.Equal(FolderCards.FormatBytes(total), Value(second, "Rozmiar"));
    }

    [Fact]
    public async Task Folder_size_is_recomputed_only_when_folder_changes()
    {
        var plugin = new FolderPreviewPlugin();
        var host = Host(plugin.Manifest);
        var folder = Dir(_root) with { LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var done = new SemaphoreSlim(0);
        plugin.Sizes.Completed += _ => done.Release();

        Assert.Null(plugin.Sizes.GetOrStart(host, folder));
        Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(10), TestToken));
        Assert.NotNull(plugin.Sizes.GetOrStart(host, folder));
        Assert.Single(_audit.Recent(plugin.Id), e => e.Operation == "fs.size");

        Assert.Null(plugin.Sizes.GetOrStart(host, folder with { LastWriteTime = folder.LastWriteTime.AddMinutes(1) }));
        Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(10), TestToken));
        Assert.Equal(2, _audit.Recent(plugin.Id).Count(e => e.Operation == "fs.size"));
    }

    [Fact]
    public async Task Gallery_card_reads_only_four_images()
    {
        var gallery = Path.Combine(_root, "photos");
        Directory.CreateDirectory(gallery);
        for (var i = 0; i < 6; i++)
        {
            File.WriteAllBytes(Path.Combine(gallery, $"p{i}.png"), [137, 80, 78, 71, (byte)i]);
        }

        var plugin = new FolderPreviewPlugin();
        var card = await FolderCards.LoadAsync(FolderViewIds.Gallery, Dir(gallery), null, Host(plugin.Manifest),
            new PreviewCardRequest(Dir(gallery), null, null), plugin.Sizes, TestToken);

        Assert.Equal(4, Assert.Single(card.Items.OfType<CardThumbnails>()).Images.Count);
        Assert.Equal("6", Value(card, "Obrazy"));
        Assert.Equal(4, _audit.Recent(plugin.Id).Count(e => e.Operation == "fs.read"));
        Assert.Contains(card.Actions, a => a.Target == gallery);
    }

    [Fact]
    public async Task Dashboard_card_lists_first_three_tiles_without_loading_them()
    {
        var config = Commander4K.Core.Folders.FolderConfig.Parse(_root, """
            { "title": "Serwery", "description": "Moje maszyny", "preview": { "view": "dashboard", "tiles": [
              { "title": "A", "open": "nowhere-a", "card": true },
              { "title": "B", "open": "nowhere-b" },
              { "title": "C", "url": "https://example.com" },
              { "title": "D", "open": "nowhere-d" }
            ] } }
            """);
        var plugin = new FolderPreviewPlugin();

        var card = await FolderCards.LoadAsync(FolderViewIds.Dashboard, Dir(_root), config, Host(plugin.Manifest),
            new PreviewCardRequest(Dir(_root), null, null), plugin.Sizes, TestToken);

        Assert.Equal("Serwery", card.Title);
        Assert.Equal("Moje maszyny", card.Subtitle);
        Assert.Equal("4", Value(card, "Kafelki"));
        Assert.Equal("A · B · C", Assert.Single(card.Items.OfType<CardText>()).Text);
        Assert.DoesNotContain(_audit.Recent(plugin.Id), e => e.Detail.Contains("nowhere", StringComparison.Ordinal));
    }

    private static string? Value(PreviewCard card, string label) => card.Items.OfType<CardValue>().FirstOrDefault(v => v.Label == label)?.Value;

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

        public void Notify(string message, bool isError, string? target)
        {
        }

        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<string?> PromptAsync(string title, string message, string initial) => Task.FromResult<string?>(null);

        public Task EditFileAsync(string path) => Task.CompletedTask;

        public Task OpenPanelAsync(string panelPluginId, string? argument) => Task.CompletedTask;

        public Task OpenUrlAsync(string url) => Task.CompletedTask;

        public Task SearchWebAsync(string query, string? engine) => Task.CompletedTask;

        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }
}
