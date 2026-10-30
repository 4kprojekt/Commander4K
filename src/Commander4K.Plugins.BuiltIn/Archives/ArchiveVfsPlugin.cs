using System.Runtime.CompilerServices;
using Commander4K.Core.Localization;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;

namespace Commander4K.Plugins.BuiltIn.Archives;

/// <summary>
/// Browses archives as folders: <c>zip://C:\data\a.zip::/docs/readme.md</c> (also <c>7z://</c>, <c>tar://</c>,
/// <c>rar://</c>, <c>gz://</c>). The archive itself may live on any VFS (<c>zip://sftp://host/a.zip</c>) — it is read
/// through the <see cref="Router"/>. Windows file names cannot contain <c>::</c>, so the first <c>::</c> separates the
/// archive from the path inside it. A listing reads the archive's directory once and caches it (keyed by the archive's
/// size and time stamp); reading an entry extracts just that entry. Read-only; packing (zip, tar, tar.gz) goes through
/// <see cref="PackAsync(Stream, IReadOnlyList{ArchiveInput}, IProgress{long}?, CancellationToken, string)"/>.
/// </summary>
public sealed class ArchiveVfsPlugin : IVfsProviderPlugin, IArchivePlugin
{
    public const string InnerSeparator = "::";
    private const int CachedArchives = 8;
    private const long InMemoryLimit = 32L * 1024 * 1024;

    /// <summary>Extension → scheme; longest first so <c>.tar.gz</c> wins over <c>.gz</c>.</summary>
    private static readonly (string Extension, string Scheme)[] Formats =
    [
        (".tar.gz", "tar"), (".tar.bz2", "tar"), (".tar.xz", "tar"), (".tgz", "tar"), (".tbz2", "tar"), (".txz", "tar"), (".tar", "tar"),
        (".zip", "zip"), (".jar", "zip"), (".nupkg", "zip"), (".vsix", "zip"), (".whl", "zip"),
        (".7z", "7z"), (".rar", "rar"), (".gz", "gz"),
    ];

    private readonly LinkedList<ArchiveIndex> _cache = [];

    public string Id => "builtin.archives";

    public string DisplayName => "Archives";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.archives", Loc.T("Archiwa"),
        Loc.T("Przeglądanie archiwów ZIP, 7z, TAR (gz/bz2/xz), RAR i GZ jak folderów; pakowanie do ZIP i TAR."), PluginPermissions.FileRead, PluginPermissions.FileWrite);

    public IReadOnlyList<string> Schemes { get; } = ["zip", "7z", "tar", "rar", "gz"];

    public IReadOnlyList<string> Extensions { get; } = [.. Formats.Select(f => f.Extension)];

    public VfsCapabilities Capabilities => VfsCapabilities.Read;

    /// <summary>Opens the archive files themselves; set by the host once the router exists (it contains this plugin).</summary>
    public VfsRouter? Router { get; set; }

    /// <summary>Whether Enter on <paramref name="name"/> should open it as a folder.</summary>
    public static bool IsArchiveName(string name) => SchemeFor(name) is not null;

    /// <summary>The browsable root of an archive file, e.g. <c>C:\a.zip</c> → <c>zip://C:\a.zip</c>.</summary>
    public static string RootPath(string archivePath) =>
        $"{SchemeFor(archivePath) ?? "zip"}{VfsPath.SchemeSeparator}{archivePath}";

    /// <summary>Splits <c>zip://C:\a.zip::/x/y</c> into <c>C:\a.zip</c> and <c>x/y</c> (empty at the archive root).</summary>
    public static (string Scheme, string Archive, string Inner) Parse(string path)
    {
        var scheme = VfsPath.GetScheme(path) ?? "zip";
        var rest = VfsPath.StripScheme(path);
        var separator = rest.IndexOf(InnerSeparator, StringComparison.Ordinal);
        return separator < 0 ? (scheme, rest, string.Empty) : (scheme, rest[..separator], NormalizeKey(rest[(separator + InnerSeparator.Length)..]));
    }

    public static string MakePath(string scheme, string archive, string inner) =>
        inner.Length == 0 ? $"{scheme}{VfsPath.SchemeSeparator}{archive}" : $"{scheme}{VfsPath.SchemeSeparator}{archive}{InnerSeparator}/{inner}";

    public async IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (scheme, archive, inner) = Parse(path);
        var index = await GetIndexAsync(archive, cancellationToken).ConfigureAwait(false);
        if (!index.Children.TryGetValue(inner, out var children))
        {
            throw index.Items.ContainsKey(inner)
                ? new IOException(Loc.T("„{0}” jest plikiem, nie folderem.", inner))
                : new DirectoryNotFoundException(Loc.T("W archiwum nie ma folderu „{0}”.", inner));
        }

        foreach (var key in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToEntry(scheme, archive, index.Items[key]);
        }
    }

    public async ValueTask<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        var (scheme, archive, inner) = Parse(path);
        if (inner.Length == 0)
        {
            return Router is not null && await Router.GetEntryAsync(archive, cancellationToken).ConfigureAwait(false) is { } file
                ? file with { Path = path, Kind = VfsEntryKind.Archive }
                : null;
        }

        var index = await GetIndexAsync(archive, cancellationToken).ConfigureAwait(false);
        return index.Items.TryGetValue(inner, out var item) ? ToEntry(scheme, archive, item) : null;
    }

    /// <summary>Inside the archive: one level up; at its root: the folder that contains the archive file.</summary>
    public string? GetParentPath(string path)
    {
        var (scheme, archive, inner) = Parse(path);
        if (inner.Length == 0)
        {
            return Router?.GetParentPath(archive);
        }

        var cut = inner.LastIndexOf('/');
        return MakePath(scheme, archive, cut < 0 ? string.Empty : inner[..cut]);
    }

    public string Combine(string directoryPath, string name)
    {
        var (scheme, archive, inner) = Parse(directoryPath);
        return MakePath(scheme, archive, inner.Length == 0 ? NormalizeKey(name) : inner + "/" + NormalizeKey(name));
    }

    public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var (_, archive, inner) = Parse(path);
        var index = await GetIndexAsync(archive, cancellationToken).ConfigureAwait(false);
        if (!index.Items.TryGetValue(inner, out var item) || item.IsDirectory)
        {
            throw new FileNotFoundException(Loc.T("W archiwum nie ma pliku „{0}”.", inner), path);
        }

        await using var source = await OpenArchiveFileAsync(archive, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Extract(source, archive, item.EntryKey, item.Size, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    // ---- IArchivePlugin (stream based; used by tools and packing) ----

    public async IAsyncEnumerable<ArchiveEntryInfo> ListAsync(Stream archive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var items = await Task.Run(() => Read(archive, null, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var item in items.Items.Values.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            yield return new ArchiveEntryInfo(item.Key, item.IsDirectory, item.Size, item.CompressedSize, item.Modified);
        }
    }

    public ValueTask<Stream> OpenEntryAsync(Stream archive, string entryPath, CancellationToken cancellationToken) =>
        new(Task.Run(() =>
        {
            var index = Read(archive, null, cancellationToken);
            return index.Items.TryGetValue(NormalizeKey(entryPath), out var item) && !item.IsDirectory
                ? Extract(archive, null, item.EntryKey, item.Size, cancellationToken)
                : throw new FileNotFoundException(Loc.T("W archiwum nie ma pliku „{0}”.", entryPath));
        }, cancellationToken));

    public ValueTask PackAsync(Stream output, IReadOnlyList<ArchiveInput> inputs, IProgress<long>? progress, CancellationToken cancellationToken) =>
        PackAsync(output, inputs, progress, cancellationToken, "archive.zip");

    /// <summary>Format follows <paramref name="fileName"/>: <c>.tar</c>, <c>.tar.gz</c>/<c>.tgz</c>, otherwise ZIP (deflate).</summary>
    public ValueTask PackAsync(Stream output, IReadOnlyList<ArchiveInput> inputs, IProgress<long>? progress, CancellationToken cancellationToken, string fileName) =>
        new(Task.Run(async () =>
        {
            var lower = fileName.ToLowerInvariant();
            var (type, compression) = lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal)
                ? (ArchiveType.Tar, CompressionType.GZip)
                : lower.EndsWith(".tar", StringComparison.Ordinal) ? (ArchiveType.Tar, CompressionType.None) : (ArchiveType.Zip, CompressionType.Deflate);
            using var writer = WriterFactory.Open(output, type, new WriterOptions(compression) { LeaveStreamOpen = true });
            long done = 0;
            foreach (var input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var opened = await input.OpenAsync(cancellationToken).ConfigureAwait(false);
                var key = NormalizeKey(input.EntryPath);
                var date = input.LastWriteTime?.UtcDateTime ?? DateTime.UtcNow;
                if (writer is SharpCompress.Writers.Tar.TarWriter tar)
                {
                    // Tar headers carry the size up front: remote (non-seekable) inputs are spooled first.
                    await using var sized = opened.CanSeek ? null : TempFile();
                    if (sized is not null)
                    {
                        await opened.CopyToAsync(sized, cancellationToken).ConfigureAwait(false);
                        sized.Position = 0;
                    }

                    var source = sized ?? opened;
                    using var counting = new CountingStream(source, read => progress?.Report(done += read), cancellationToken);
                    tar.Write(key, counting, date, source.Length - source.Position);
                }
                else
                {
                    using var counting = new CountingStream(opened, read => progress?.Report(done += read), cancellationToken);
                    writer.Write(key, counting, date);
                }
            }
        }, cancellationToken));

    public static string? SchemeFor(string name)
    {
        foreach (var (extension, scheme) in Formats)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return scheme;
            }
        }

        return null;
    }

    private static VfsEntry ToEntry(string scheme, string archive, Item item) => new(
        item.Name,
        MakePath(scheme, archive, item.Key),
        item.IsDirectory ? VfsEntryKind.Directory : VfsEntryKind.File,
        item.IsDirectory ? -1 : item.Size,
        item.Modified,
        Description: !item.IsDirectory && item.CompressedSize > 0 && item.CompressedSize < item.Size
            ? Loc.T("{0}% po kompresji", item.CompressedSize * 100 / item.Size)
            : null);

    private async Task<ArchiveIndex> GetIndexAsync(string archive, CancellationToken cancellationToken)
    {
        var router = Router ?? throw new InvalidOperationException("Archive plugin used before the router was attached.");
        var file = await router.GetEntryAsync(archive, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException(Loc.T("Nie ma pliku archiwum „{0}”.", archive), archive);
        var stamp = (file.Size, file.LastWriteTime);
        lock (_cache)
        {
            if (_cache.FirstOrDefault(i => string.Equals(i.Archive, archive, StringComparison.OrdinalIgnoreCase)) is { } cached)
            {
                _cache.Remove(cached);
                if (cached.Stamp == stamp)
                {
                    _cache.AddFirst(cached);
                    return cached;
                }
            }
        }

        await using var source = await OpenArchiveFileAsync(archive, cancellationToken).ConfigureAwait(false);
        var index = await Task.Run(() => Read(source, archive, cancellationToken), cancellationToken).ConfigureAwait(false);
        index.Stamp = stamp;
        lock (_cache)
        {
            _cache.AddFirst(index);
            while (_cache.Count > CachedArchives)
            {
                _cache.RemoveLast();
            }
        }

        return index;
    }

    /// <summary>Archives need seeking (central directory at the end); remote streams are spooled to a temp file first.</summary>
    private async Task<Stream> OpenArchiveFileAsync(string archive, CancellationToken cancellationToken)
    {
        var stream = await Router!.OpenReadAsync(archive, cancellationToken).ConfigureAwait(false);
        if (stream.CanSeek)
        {
            return stream;
        }

        await using (stream)
        {
            var spool = TempFile();
            await stream.CopyToAsync(spool, cancellationToken).ConfigureAwait(false);
            spool.Position = 0;
            return spool;
        }
    }

    /// <summary>
    /// PAX/GNU bookkeeping records (<c>PaxHeaders.123/x</c>, <c>pax_global_header</c>, <c>@LongLink</c>) that the
    /// tar reader surfaces as entries; they describe the next entry and are not files.
    /// </summary>
    private static bool IsTarMetadata(string key)
    {
        var name = key[(key.LastIndexOf('/') + 1)..];
        var folder = key.Contains('/', StringComparison.Ordinal) ? key[..key.IndexOf('/', StringComparison.Ordinal)] : key;
        return name is "pax_global_header" or "@LongLink" or "@PaxHeader"
            || (folder.StartsWith("PaxHeaders.", StringComparison.Ordinal) && folder[11..].All(char.IsAsciiDigit) && folder.Length > 11);
    }

    private static bool IsCompressedTar(string? name) =>
        name is not null && SchemeFor(name) == "tar" && !name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Walks the entries until <paramref name="visit"/> returns <c>true</c>. Random-access formats (zip, 7z, rar, tar)
    /// use the archive API; compressed tar (and any gzip) only exists as a stream, so it goes through the
    /// sequential reader — <see cref="ArchiveFactory"/> would otherwise show a .tar.gz as one ".tar" file.
    /// </summary>
    private static void Visit(Stream source, string? name, Func<IEntry, Func<Stream>, bool> visit)
    {
        var options = new ReaderOptions { LeaveStreamOpen = true };
        source.Position = 0;
        var sequential = IsCompressedTar(name);
        if (!sequential)
        {
            using var archive = ArchiveFactory.Open(source, options);
            sequential = archive.Type == ArchiveType.GZip;
            if (!sequential)
            {
                foreach (var entry in archive.Entries)
                {
                    if (visit(entry, entry.OpenEntryStream))
                    {
                        return;
                    }
                }

                return;
            }
        }

        source.Position = 0;
        using var reader = ReaderFactory.Open(source, options);
        while (reader.MoveToNextEntry())
        {
            if (visit(reader.Entry, reader.OpenEntryStream))
            {
                return;
            }
        }
    }

    private static ArchiveIndex Read(Stream source, string? archive, CancellationToken cancellationToken)
    {
        var index = new ArchiveIndex(archive ?? string.Empty);
        index.Children[string.Empty] = [];
        Visit(source, archive, (entry, _) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = NormalizeKey(entry.Key ?? string.Empty);
            if (IsTarMetadata(key))
            {
                return false;
            }
            if (key.Length == 0)
            {
                // Single-file .gz archives often carry no name: use the archive's own name without ".gz".
                key = archive is null ? "data" : Path.GetFileNameWithoutExtension(archive.Replace('/', '\\'));
            }

            var modified = entry.LastModifiedTime is { } time ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Local)) : default;
            index.Add(new Item(key, entry.Key ?? string.Empty, entry.IsDirectory, entry.Size, entry.CompressedSize, modified));
            return false;
        });

        foreach (var children in index.Children.Values)
        {
            children.Sort((a, b) =>
            {
                var (x, y) = (index.Items[a], index.Items[b]);
                return x.IsDirectory != y.IsDirectory ? (x.IsDirectory ? -1 : 1) : StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name);
            });
        }

        return index;
    }

    private static Stream Extract(Stream source, string? archiveName, string entryKey, long size, CancellationToken cancellationToken)
    {
        Stream? target = null;
        Visit(source, archiveName, (entry, open) =>
        {
            if (entry.IsDirectory || (entry.Key ?? string.Empty) != entryKey)
            {
                return false;
            }

            if (entry.IsEncrypted)
            {
                throw new IOException(Loc.T("Plik w archiwum jest zaszyfrowany — hasła nie są jeszcze obsługiwane."));
            }

            target = size is >= 0 and <= InMemoryLimit ? new MemoryStream((int)Math.Max(0, size)) : TempFile();
            using var data = open();

            // Decompression is synchronous in SharpCompress; check the token between buffers.
            var buffer = new byte[81920];
            int read;
            while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
            }

            return true;
        });

        if (target is null)
        {
            throw new FileNotFoundException(Loc.T("W archiwum nie ma „{0}”.", entryKey));
        }

        target.Position = 0;
        return target;
    }

    private static FileStream TempFile() =>
        new(Path.Combine(Path.GetTempPath(), "c4k-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    /// <summary><c>.\a\b/</c> → <c>a/b</c>: forward slashes, no leading <c>./</c>, no leading or trailing slash.</summary>
    private static string NormalizeKey(string key)
    {
        var normalized = key.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Trim('/');
    }

    private sealed record Item(string Key, string EntryKey, bool IsDirectory, long Size, long CompressedSize, DateTimeOffset Modified)
    {
        public string Name => Key[(Key.LastIndexOf('/') + 1)..];
    }

    private sealed class ArchiveIndex(string archive)
    {
        public string Archive { get; } = archive;

        public (long Size, DateTimeOffset Modified) Stamp { get; set; }

        public Dictionary<string, Item> Items { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> Children { get; } = new(StringComparer.Ordinal);

        /// <summary>Adds an entry and every parent folder the archive does not list explicitly.</summary>
        public void Add(Item item)
        {
            if (Items.TryGetValue(item.Key, out var existing))
            {
                if (existing.IsDirectory && item.IsDirectory)
                {
                    Items[item.Key] = item; // An explicit folder entry replaces the implied one (it has a date).
                }

                return;
            }

            Items[item.Key] = item;
            if (item.IsDirectory)
            {
                Children.TryAdd(item.Key, []);
            }

            var cut = item.Key.LastIndexOf('/');
            var parent = cut < 0 ? string.Empty : item.Key[..cut];
            if (parent.Length > 0 && !Items.ContainsKey(parent))
            {
                Add(new Item(parent, string.Empty, true, 0, 0, default));
            }

            if (Children.TryGetValue(parent, out var siblings)) // Malformed archives can nest entries under a file.
            {
                siblings.Add(item.Key);
            }
        }
    }

    /// <summary>Read-only pass-through that reports bytes read (packing progress).</summary>
    private sealed class CountingStream(Stream inner, Action<int> onRead, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inner.Read(buffer, offset, count);
            onRead(read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
