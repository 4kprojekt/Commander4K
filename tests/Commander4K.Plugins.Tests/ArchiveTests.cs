using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Commander4K.Core.Execution;
using Commander4K.Core.Jobs;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Archives;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.Tests;

public sealed class ArchiveTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("c4k-arc-").FullName;
    private readonly ArchiveVfsPlugin _archives = new();
    private readonly VfsRouter _router;

    public ArchiveTests()
    {
        _router = new VfsRouter(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin(), _archives]);
        _archives.Router = _router;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Zip_lists_nested_and_implied_folders_and_reads_entries()
    {
        var zip = Path.Combine(_folder, "dane.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "readme.md", "# Witaj");
            Write(archive, "docs/a.txt", new string('x', 5000)); // "docs" has no entry of its own.
            Write(archive, "docs/deep/b.txt", "b");
            archive.CreateEntry("empty/");
        }

        var root = ArchiveVfsPlugin.RootPath(zip);
        Assert.Equal("zip://" + zip, root);

        var top = await List(root);
        Assert.Equal(["docs", "empty", "readme.md"], top.Select(e => e.Name));
        Assert.Equal(VfsEntryKind.Directory, top[0].Kind);
        Assert.Equal(root + "::/docs", top[0].Path);

        var docs = await List(top[0].Path);
        Assert.Equal(["deep", "a.txt"], docs.Select(e => e.Name));
        Assert.Equal(5000, docs[1].Size);
        Assert.Contains("% po kompresji", docs[1].Description, StringComparison.Ordinal);

        Assert.Equal("# Witaj", await ReadText(top[2].Path));
        Assert.Equal("b", await ReadText(root + "::/docs/deep/b.txt"));

        // ".." chain: inner folder → archive root → the folder holding the archive.
        Assert.Equal(root + "::/docs", _router.GetParentPath(root + "::/docs/deep"));
        Assert.Equal(root, _router.GetParentPath(root + "::/docs"));
        Assert.Equal(_folder, _router.GetParentPath(root)?.TrimEnd('\\'));
        Assert.Equal(VfsEntryKind.Archive, (await _router.GetEntryAsync(root, Token))?.Kind);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => List(root + "::/nope"));
    }

    [Fact]
    public async Task Tar_gz_is_browsable_and_copy_out_works_through_the_router()
    {
        var tgz = Path.Combine(_folder, "kopia.tar.gz");
        await using (var file = File.Create(tgz))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "./src/main.cs") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("class A {}")) };
            await tar.WriteEntryAsync(entry, Token);
        }

        var root = ArchiveVfsPlugin.RootPath(tgz);
        Assert.StartsWith("tar://", root, StringComparison.Ordinal);
        var src = Assert.Single(await List(root)); // .NET writes PAX headers; they must not show up as folders.
        Assert.Equal("src", src.Name);

        var target = Path.Combine(_folder, "out");
        Directory.CreateDirectory(target);
        await new VfsTransferJob(_router, [src], target, TransferMode.Copy, OverwritePolicy.Overwrite).ExecuteAsync(new NoProgress(), Token);
        Assert.Equal("class A {}", await File.ReadAllTextAsync(Path.Combine(target, "src", "main.cs"), Token));
    }

    [Theory]
    [InlineData("paczka.zip")]
    [InlineData("paczka.tar.gz")]
    public async Task Pack_job_writes_an_archive_that_reads_back(string name)
    {
        var source = Path.Combine(_folder, "projekt");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "alfa", Token);
        await File.WriteAllTextAsync(Path.Combine(source, "sub", "b.txt"), "beta", Token);
        var output = Path.Combine(_folder, name);
        var entry = (await _router.GetEntryAsync(source, Token))!.Value;

        await new VfsPackJob(_router, (o, i, p, ct) => _archives.PackAsync(o, i, p, ct, name), [entry], output).ExecuteAsync(new NoProgress(), Token);

        var root = ArchiveVfsPlugin.RootPath(output);
        Assert.Equal("projekt", Assert.Single(await List(root)).Name);
        Assert.Equal("beta", await ReadText(root + "::/projekt/sub/b.txt"));
    }

    [Fact]
    public async Task Cancelled_pack_removes_the_partial_archive()
    {
        var source = Path.Combine(_folder, "big.bin");
        await File.WriteAllBytesAsync(source, new byte[4_000_000], Token);
        var output = Path.Combine(_folder, "przerwane.zip");
        using var cts = new CancellationTokenSource();
        var entry = (await _router.GetEntryAsync(source, Token))!.Value;
        var job = new VfsPackJob(_router, async (o, i, p, ct) =>
        {
            await cts.CancelAsync();
            await _archives.PackAsync(o, i, p, ct, "x.zip");
        }, [entry], output);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.ExecuteAsync(new NoProgress(), cts.Token));
        Assert.False(File.Exists(output));
    }

    private async Task<List<VfsEntry>> List(string path)
    {
        var entries = new List<VfsEntry>();
        await foreach (var entry in _router.EnumerateAsync(path, Token))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private async Task<string> ReadText(string path)
    {
        await using var stream = await _router.OpenReadAsync(path, Token);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(Token);
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private sealed class NoProgress : IProgress<JobMetrics>
    {
        public void Report(JobMetrics value)
        {
        }
    }
}
