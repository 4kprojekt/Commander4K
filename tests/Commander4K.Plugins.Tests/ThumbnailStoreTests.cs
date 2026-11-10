using System.Collections.Concurrent;
using Commander4K.Core.Execution;
using Commander4K.Core.Folders;
using Commander4K.Core.Plugins;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;
using Commander4K.Plugins.BuiltIn.Previews;
using SkiaSharp;

namespace Commander4K.Plugins.Tests;

public sealed class ThumbnailStoreTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("c4k-thumbs-").FullName;
    private readonly AuditLog _audit = new(null);
    private readonly VfsRouter _router = new(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
    private readonly HttpClient _http = new();
    private readonly FolderPreviewPlugin _plugin = new();

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private string ThumbnailFolder => Path.Combine(_root, ThumbnailStore.FolderName);

    public async ValueTask DisposeAsync()
    {
        _plugin.Thumbnails.Dispose();
        await _audit.DisposeAsync();
        _http.Dispose();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(100, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Gallery_makes_small_thumbnails_in_a_hidden_folder()
    {
        Picture("a.png", 1600, 1200, SKColors.Red);
        Picture("b.jpg", 300, 900, SKColors.Green);
        Picture("c.png", 100, 50, SKColors.Blue);
        File.WriteAllBytes(Path.Combine(_root, "broken.png"), [137, 80, 78, 71, 1, 2, 3]);
        var reports = new ConcurrentQueue<TilesPreview>();

        var gallery = await LoadAsync(Host(), new SynchronousProgress<PreviewContent>(p => reports.Enqueue((TilesPreview)p)));

        Assert.Equal(["a.png", "b.jpg", "broken.png", "c.png"], gallery.Tiles.Select(t => t.Title));
        Assert.All(gallery.Tiles.Where(t => t.Title != "broken.png"), t => Assert.NotNull(t.Image));
        Assert.Null(gallery.Tiles[2].Image);
        Assert.All(gallery.Tiles.Where(t => t.Image is not null), t => Assert.InRange(t.Image!.Value.Length, 1, 20_000));
        Assert.NotEmpty(reports);
        Assert.All(reports.First().Tiles, t => Assert.Null(t.Image)); // tiles first, pictures later

        var directory = new DirectoryInfo(ThumbnailFolder);
        Assert.True(directory.Exists);
        Assert.True(directory.Attributes.HasFlag(FileAttributes.Hidden));
        var files = directory.GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(3, files.Length);
        Assert.All(files, f => Assert.EndsWith(".webp", f, StringComparison.Ordinal));
        Assert.Contains(files, f => f.StartsWith("a.png.", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.StartsWith("broken", StringComparison.Ordinal));

        var (width, height) = Dimensions(File.ReadAllBytes(Path.Combine(ThumbnailFolder, files.Single(f => f.StartsWith("a.png.", StringComparison.Ordinal)))));
        Assert.Equal((256, 192), (width, height));
        Assert.Equal((85, 256), Dimensions(File.ReadAllBytes(Path.Combine(ThumbnailFolder, files.Single(f => f.StartsWith("b.jpg.", StringComparison.Ordinal))))));
        Assert.Equal((100, 50), Dimensions(File.ReadAllBytes(Path.Combine(ThumbnailFolder, files.Single(f => f.StartsWith("c.png.", StringComparison.Ordinal))))));
        Assert.Equal(3, _audit.Recent(_plugin.Id).Count(e => e.Operation == "fs.write"));
    }

    [Fact]
    public async Task Second_visit_reuses_thumbnails_and_removes_stale_ones()
    {
        Picture("keep.png", 400, 400, SKColors.Red);
        Picture("gone.png", 400, 400, SKColors.Green);
        Picture("changed.png", 400, 400, SKColors.Blue);
        await LoadAsync(Host());
        var before = Directory.GetFiles(ThumbnailFolder).Select(f => Path.GetFileName(f)).ToArray();
        Assert.Equal(3, before.Length);
        var writtenBefore = _audit.Recent(_plugin.Id).Count(e => e.Operation == "fs.write");

        File.Delete(Path.Combine(_root, "gone.png"));
        Picture("changed.png", 800, 400, SKColors.Yellow);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "changed.png"), DateTime.UtcNow.AddMinutes(5));
        File.WriteAllBytes(Path.Combine(ThumbnailFolder, "stranger.webp"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(ThumbnailFolder, "notes.txt"), "not ours");

        // A fresh store: nothing is remembered in memory, everything comes from the folder.
        using var store = new ThumbnailStore();
        var host = Host();
        var gallery = await LoadAsync(host, store: store);
        Assert.All(gallery.Tiles, t => Assert.NotNull(t.Image));
        await WaitForDeletionsAsync();

        var after = Directory.GetFiles(ThumbnailFolder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();
        Assert.Contains(before.Single(f => f.StartsWith("keep.png.", StringComparison.Ordinal)), after);
        Assert.DoesNotContain(after, f => f.StartsWith("gone.png.", StringComparison.Ordinal));
        Assert.DoesNotContain(before.Single(f => f.StartsWith("changed.png.", StringComparison.Ordinal)), after);
        Assert.Contains(after, f => f.StartsWith("changed.png.", StringComparison.Ordinal));
        Assert.DoesNotContain("stranger.webp", after);
        Assert.Contains("notes.txt", after); // only .webp files are ours to delete
        var written = _audit.Recent(_plugin.Id).Where(e => e.Operation == "fs.write").Skip(writtenBefore).ToList();
        Assert.Contains("changed.png", Assert.Single(written).Detail, StringComparison.Ordinal); // keep.png was not made again
    }

    [Fact]
    public async Task Without_write_permission_pictures_stay_in_memory()
    {
        Picture("a.png", 300, 300, SKColors.Red);
        var readOnly = new PluginManifest(_plugin.Id, "Folder", "1.0.0", "a", "d", [PluginPermissions.FileRead]);

        var gallery = await LoadAsync(Host(readOnly));

        Assert.NotNull(Assert.Single(gallery.Tiles).Image);
        Assert.False(Directory.Exists(ThumbnailFolder));

        // The second request is served from memory: no read of the image, no write attempt.
        var reads = _audit.Recent(_plugin.Id).Count(e => e.Operation == "fs.read");
        var again = await LoadAsync(Host(readOnly));
        Assert.NotNull(Assert.Single(again.Tiles).Image);
        Assert.Equal(reads, _audit.Recent(_plugin.Id).Count(e => e.Operation == "fs.read"));
    }

    [Fact]
    public async Task Setting_off_writes_nothing()
    {
        Picture("a.png", 300, 300, SKColors.Red);
        _plugin.Thumbnails.WriteToFolders = false;

        var gallery = await LoadAsync(Host());

        Assert.NotNull(Assert.Single(gallery.Tiles).Image);
        Assert.False(Directory.Exists(ThumbnailFolder));
        Assert.DoesNotContain(_audit.Recent(_plugin.Id), e => e.Operation is "fs.write" or "fs.mkdir");
    }

    [Fact]
    public async Task Cancelled_view_does_not_stop_the_work()
    {
        for (var i = 0; i < 6; i++)
        {
            Picture($"p{i}.png", 600, 400, SKColors.Red);
        }

        using var cancel = new CancellationTokenSource();
        var host = Host();
        var images = (await host.ListAsync(_root, TestToken)).Where(FolderViews.IsImage).ToList();
        var pending = _plugin.Thumbnails.RequestAsync(host, _root, images, (_, _) => { }, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (!Directory.Exists(ThumbnailFolder) || Directory.GetFiles(ThumbnailFolder).Length < 6))
        {
            await Task.Delay(50, TestToken);
        }

        Assert.Equal(6, Directory.GetFiles(ThumbnailFolder).Length);
    }

    [Fact]
    public void Encode_fits_the_longest_edge_and_keeps_the_aspect()
    {
        Assert.Equal((256, 144), Dimensions(ThumbnailStore.Encode(Png(1920, 1080, SKColors.Red))!));
        Assert.Equal((144, 256), Dimensions(ThumbnailStore.Encode(Png(1080, 1920, SKColors.Red))!));
        Assert.Equal((64, 64), Dimensions(ThumbnailStore.Encode(Png(64, 64, SKColors.Red))!));
        Assert.Null(ThumbnailStore.Encode([1, 2, 3, 4]));
        Assert.Equal((256, 1), ThumbnailStore.Fit(10_000, 10));
    }

    [Fact]
    public void Thumbnail_names_carry_size_and_time_and_paths_follow_the_scheme()
    {
        var image = new VfsEntry("IMG_1.jpg", @"\\srv\share\IMG_1.jpg", VfsEntryKind.File, 12345, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        Assert.Equal("IMG_1.jpg.12345-1700000000.webp", ThumbnailStore.ThumbnailName(image));
        Assert.Equal(@"\\srv\share\photos\.thumbnail.c4k", ThumbnailStore.ThumbnailFolder(@"\\srv\share\photos"));
        Assert.Equal(@"C:\photos\.thumbnail.c4k", ThumbnailStore.ThumbnailFolder(@"C:\photos"));
        Assert.Equal("sftp://host/photos/.thumbnail.c4k", ThumbnailStore.ThumbnailFolder("sftp://host/photos"));
        Assert.Equal("sftp://host/.thumbnail.c4k", ThumbnailStore.ThumbnailFolder("sftp://host/"));
    }

    private async Task<TilesPreview> LoadAsync(IPluginHost host, IProgress<PreviewContent>? progress = null, ThumbnailStore? store = null)
    {
        var item = C4kItem.Parse(_root, "gallery.c4k", """{ "view": "gallery" }""");
        var folder = new VfsEntry(Path.GetFileName(_root), _root, VfsEntryKind.Directory);
        return Assert.IsType<TilesPreview>(await FolderViews.LoadAsync(item, folder, host, store ?? _plugin.Thumbnails, progress, TestToken));
    }

    /// <summary>Stale thumbnails are deleted after the pictures, so the gallery can be back before they are gone.</summary>
    private async Task WaitForDeletionsAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && Directory.GetFiles(ThumbnailFolder, "*.webp").Length > 3)
        {
            await Task.Delay(50, TestToken);
        }
    }

    private IPluginHost Host(PluginManifest? manifest = null) => new ScopedPluginHost(manifest ?? _plugin.Manifest, new NullPluginUi(), _router, _http, _audit);

    private void Picture(string name, int width, int height, SKColor colour) => File.WriteAllBytes(Path.Combine(_root, name), Png(width, height, colour));

    private static byte[] Png(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(colour);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 4f, paint);
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (int Width, int Height) Dimensions(byte[] encoded)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        return (codec.Info.Width, codec.Info.Height);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
