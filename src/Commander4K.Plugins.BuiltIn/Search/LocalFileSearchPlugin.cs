using System.Buffers;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.BuiltIn.Search;

/// <summary>
/// Alt+F7 for local disks and SMB shares: directories are walked on all cores (<see cref="Parallel.ForEachAsync{TSource}(IAsyncEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
/// over a shared queue) and file contents are scanned with vectorised span searches, which use AVX2/AVX-512 on x64
/// and AdvSIMD (NEON) on ARM64 without any architecture-specific code.
/// </summary>
public sealed class LocalFileSearchPlugin : ISearchProviderPlugin
{
    private const int ChunkSize = 256 * 1024;

    public string Id => "builtin.search.files";

    public string DisplayName => "File search";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.search.files", "Wyszukiwanie plików", "Równoległe wyszukiwanie nazw i treści plików.", PluginPermissions.FileRead);

    public bool CanSearch(string rootPath) =>
        VfsPath.GetScheme(rootPath) is VfsPath.FileScheme or VfsPath.SmbScheme
        && LocalFileSystemPlugin.ToLocalPath(rootPath) is not null;

    /// <remarks>
    /// Text matching is on UTF-8 bytes; case-insensitive matching folds ASCII letters only, so "Zażółć" matches
    /// itself but not "ZAŻÓŁĆ".
    /// </remarks>
    public async IAsyncEnumerable<SearchHit> SearchAsync(SearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var root = LocalFileSystemPlugin.ToLocalPath(query.RootPath) ?? throw new NotSupportedException("Search needs a directory.");
        var needle = string.IsNullOrEmpty(query.ContainsText) ? null : ContentMatcher.Create(query.ContainsText, query.CaseSensitive);

        var hits = Channel.CreateBounded<SearchHit>(new BoundedChannelOptions(4096) { SingleReader = true });
        var work = Channel.CreateUnbounded<string>();
        var outstanding = 1;
        work.Writer.TryWrite(root);

        var producer = Task.Run(
            async () =>
            {
                try
                {
                    var options = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = query.MaxDegreeOfParallelism > 0 ? query.MaxDegreeOfParallelism : Environment.ProcessorCount,
                        CancellationToken = cancellationToken,
                    };
                    await Parallel.ForEachAsync(work.Reader.ReadAllAsync(cancellationToken), options, async (directory, ct) =>
                    {
                        try
                        {
                            await SearchDirectoryAsync(directory, query, needle, hits.Writer, dir =>
                            {
                                Interlocked.Increment(ref outstanding);
                                work.Writer.TryWrite(dir);
                            }, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (Interlocked.Decrement(ref outstanding) == 0)
                            {
                                work.Writer.TryComplete();
                            }
                        }
                    }).ConfigureAwait(false);
                    hits.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    hits.Writer.TryComplete(ex);
                }
            },
            CancellationToken.None);

        await foreach (var hit in hits.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return hit;
        }

        await producer.ConfigureAwait(false);
    }

    private static async Task SearchDirectoryAsync(
        string directory,
        SearchQuery query,
        ContentMatcher? needle,
        ChannelWriter<SearchHit> hits,
        Action<string> schedule,
        CancellationToken cancellationToken)
    {
        List<VfsEntry> entries;
        try
        {
            entries = [.. FileSystemListing.Enumerate(directory)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = entry.Kind == VfsEntryKind.Directory;
            if (isDirectory && query.Recursive && (entry.Flags & VfsEntryFlags.ReparsePoint) == 0)
            {
                schedule(entry.Path);
            }

            if (!FileSystemName.MatchesSimpleExpression(query.NamePattern, entry.Name, ignoreCase: !query.CaseSensitive))
            {
                continue;
            }

            if (needle is null)
            {
                await hits.WriteAsync(new SearchHit(entry), cancellationToken).ConfigureAwait(false);
            }
            else if (!isDirectory && await needle.FindInFileAsync(entry.Path, cancellationToken).ConfigureAwait(false) is long offset)
            {
                await hits.WriteAsync(new SearchHit(entry, offset), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal sealed class ContentMatcher
    {
        private readonly byte[] _needle;
        private readonly bool _caseSensitive;
        private readonly SearchValues<byte>? _firstByte;

        private ContentMatcher(byte[] needle, bool caseSensitive)
        {
            _needle = needle;
            _caseSensitive = caseSensitive;
            if (!caseSensitive)
            {
                var first = needle[0];
                _firstByte = SearchValues.Create(char.IsAsciiLetter((char)first)
                    ? [(byte)char.ToLowerInvariant((char)first), (byte)char.ToUpperInvariant((char)first)]
                    : [first]);
            }
        }

        public static ContentMatcher Create(string text, bool caseSensitive) => new(Encoding.UTF8.GetBytes(text), caseSensitive);

        /// <summary>Index of the first match in <paramref name="haystack"/>, or -1.</summary>
        public int IndexOf(ReadOnlySpan<byte> haystack)
        {
            if (_caseSensitive)
            {
                return haystack.IndexOf(_needle);
            }

            var offset = 0;
            while (offset <= haystack.Length - _needle.Length)
            {
                var candidate = haystack[offset..].IndexOfAny(_firstByte!);
                if (candidate < 0 || offset + candidate > haystack.Length - _needle.Length)
                {
                    return -1;
                }

                var position = offset + candidate;
                if (System.Text.Ascii.EqualsIgnoreCase(haystack.Slice(position, _needle.Length), _needle))
                {
                    return position;
                }

                offset = position + 1;
            }

            return -1;
        }

        /// <summary>Byte offset of the first match in the file, or <c>null</c>; unreadable files never match.</summary>
        public async Task<long?> FindInFileAsync(string path, CancellationToken cancellationToken)
        {
            var overlap = _needle.Length - 1;
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + overlap);
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                long consumed = 0;
                var carried = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(carried, ChunkSize), cancellationToken).ConfigureAwait(false);
                    var length = carried + read;
                    var index = IndexOf(buffer.AsSpan(0, length));
                    if (index >= 0)
                    {
                        return consumed - carried + index;
                    }

                    if (read == 0)
                    {
                        return null;
                    }

                    // Keep the tail so a match straddling two chunks is still found.
                    carried = Math.Min(overlap, length);
                    buffer.AsSpan(length - carried, carried).CopyTo(buffer);
                    consumed += read;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
