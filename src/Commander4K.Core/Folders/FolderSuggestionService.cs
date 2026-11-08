using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Folders;

/// <summary>A candidate that asks a plugin process; it gets <see cref="FolderSuggestionService.AskBudget"/> instead of the shared budget.</summary>
public interface IAskingFolderSuggester
{
    bool AsksPlugin { get; }
}

public sealed record RankedSuggestion(VfsEntry Folder, FolderSuggestion Suggestion, IPreviewPlugin Source);

/// <summary>
/// Which view suits the folder under the cursor: one bounded listing, shared by every <see cref="IFolderSuggestingPreview"/>,
/// and a stream of ever better answers. Slow shares, hanging plugins and failures end quietly with nothing.
/// </summary>
public sealed class FolderSuggestionService(
    Func<IReadOnlyList<IPreviewPlugin>> previews,
    Func<IPreviewPlugin, IPluginHost> hostFor,
    Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> list,
    FolderSuggestionMemory memory)
{
    public const int MaxEntries = 500;
    public const double MinScore = 0.5;
    private const int CacheSize = 256;

    /// <summary>Listing plus every candidate that does not ask a plugin process.</summary>
    public static readonly TimeSpan FastBudget = TimeSpan.FromSeconds(1.5);

    /// <summary>Each <c>preview.suggest</c> call, process start included.</summary>
    public static readonly TimeSpan AskBudget = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Path, DateTimeOffset Modified), RankedSuggestion?> _cache = [];
    private readonly Queue<(string Path, DateTimeOffset Modified)> _cacheOrder = new();

    public FolderSuggestionMemory Memory => memory;

    /// <summary>A folder (not a drive, share root or "..") on a disk or a share, where <c>.c4k</c> files work.</summary>
    public static bool IsEligible(VfsEntry entry) =>
        entry.Kind == VfsEntryKind.Directory
        && VfsPath.GetScheme(entry.Path) is VfsPath.FileScheme or VfsPath.SmbScheme
        && !entry.Path.Contains("://", StringComparison.Ordinal);

    /// <summary>Yields each time the best suggestion improves; cancellation simply ends the stream.</summary>
    public async IAsyncEnumerable<RankedSuggestion> SuggestAsync(VfsEntry folder, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsEligible(folder) || memory.IsMuted(folder.Path))
        {
            yield break;
        }

        var key = (folder.Path.ToUpperInvariant(), folder.LastWriteTime);
        if (TryGetCached(key, out var hit))
        {
            if (hit is not null)
            {
                yield return hit with { Folder = folder };
            }

            yield break;
        }

        var candidates = previews().OfType<IFolderSuggestingPreview>().ToList();
        if (candidates.Count == 0)
        {
            yield break;
        }

        using var fast = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        fast.CancelAfter(FastBudget);
        var snapshot = await ReadSnapshotAsync(folder, fast.Token).ConfigureAwait(false);
        if (snapshot is null)
        {
            yield break;
        }

        if (snapshot.Entries.Any(C4kReader.IsItemFile))
        {
            Remember(key, null);
            yield break;
        }

        var channel = Channel.CreateUnbounded<RankedSuggestion>();
        var runs = candidates.Select(c => RunAsync(c, snapshot, channel.Writer, cancellationToken, fast.Token)).ToArray();
        _ = CompleteWhenDoneAsync(runs, channel.Writer);

        RankedSuggestion? best = null;
        while (await WaitToReadAsync(channel.Reader, cancellationToken).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var next))
            {
                if (best is null || IsBetter(next, best))
                {
                    best = next;
                    yield return next;
                }
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            Remember(key, best);
        }
    }

    /// <summary>Drops the cached answer for <paramref name="folder"/> (a <c>.c4k</c> file was just written or changed).</summary>
    public void Forget(string folder)
    {
        var path = folder.ToUpperInvariant();
        lock (_gate)
        {
            foreach (var key in _cache.Keys.Where(k => k.Path == path).ToList())
            {
                _cache.Remove(key);
            }
        }
    }

    /// <summary>Higher score; on a tie a plugin before the built-in views, then the higher priority.</summary>
    public static bool IsBetter(RankedSuggestion candidate, RankedSuggestion current)
    {
        if (candidate.Suggestion.Score != current.Suggestion.Score)
        {
            return candidate.Suggestion.Score > current.Suggestion.Score;
        }

        var external = !IsBuiltIn(candidate.Source);
        if (external != !IsBuiltIn(current.Source))
        {
            return external;
        }

        return candidate.Source.Priority > current.Source.Priority;
    }

    private static bool IsBuiltIn(IPreviewPlugin plugin) => plugin.Manifest.Id.StartsWith("builtin.", StringComparison.Ordinal);

    private async Task<FolderSnapshot?> ReadSnapshotAsync(VfsEntry folder, CancellationToken cancellationToken)
    {
        var entries = new List<VfsEntry>();
        try
        {
            await foreach (var entry in list(folder.Path, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (entries.Count == MaxEntries)
                {
                    return new FolderSnapshot(folder, entries, Truncated: true);
                }

                entries.Add(entry);
            }

            return new FolderSnapshot(folder, entries, Truncated: false);
        }
        catch (Exception)
        {
            return null; // Slow, gone, locked or asking for a login: no hint for this folder.
        }
    }

    private async Task RunAsync(IFolderSuggestingPreview candidate, FolderSnapshot snapshot, ChannelWriter<RankedSuggestion> writer,
        CancellationToken cancellationToken, CancellationToken fastToken)
    {
        using var ask = candidate is IAskingFolderSuggester { AsksPlugin: true } ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        ask?.CancelAfter(AskBudget);
        try
        {
            var suggestion = await candidate.SuggestAsync(snapshot, hostFor(candidate), ask?.Token ?? fastToken).ConfigureAwait(false);
            if (suggestion is { Score: >= MinScore } && !string.IsNullOrWhiteSpace(suggestion.Message))
            {
                writer.TryWrite(new RankedSuggestion(snapshot.Folder, suggestion, candidate));
            }
        }
        catch (Exception)
        {
            // A failing or slow candidate only loses its own suggestion.
        }
    }

    private static async Task CompleteWhenDoneAsync(Task[] runs, ChannelWriter<RankedSuggestion> writer)
    {
        await Task.WhenAll(runs).ConfigureAwait(false); // RunAsync never faults.
        writer.TryComplete();
    }

    private static async Task<bool> WaitToReadAsync(ChannelReader<RankedSuggestion> reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private bool TryGetCached((string, DateTimeOffset) key, out RankedSuggestion? hit)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(key, out hit);
        }
    }

    private void Remember((string, DateTimeOffset) key, RankedSuggestion? best)
    {
        lock (_gate)
        {
            if (_cache.TryAdd(key, best))
            {
                _cacheOrder.Enqueue(key);
                if (_cacheOrder.Count > CacheSize)
                {
                    _cache.Remove(_cacheOrder.Dequeue());
                }
            }
            else
            {
                _cache[key] = best;
            }
        }
    }
}
