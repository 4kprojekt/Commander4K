using System.Collections.Concurrent;
using Commander4K.PluginSdk;
using SkiaSharp;

namespace Commander4K.Plugins.BuiltIn.Previews;

/// <summary>
/// Small pictures for the gallery view, made on dedicated background threads and kept in a hidden
/// <c>.thumbnail.c4k</c> folder next to the images, so a share with hundreds of photos shows them at once on the next
/// visit. A thumbnail's file name carries the image's size and modification time: one listing of that folder tells
/// which thumbnails are current, which have to be made and which are stale (the image changed or is gone) and get
/// deleted. Where the folder cannot be written (archives, read-only shares, no permission, the setting is off) the
/// thumbnails stay in memory. Nothing here runs on the UI thread or the thread pool: the work waits in a queue, the
/// newest folder first, and a folder the user left keeps its turn at the back.
/// </summary>
public sealed class ThumbnailStore : IDisposable
{
    public const string FolderName = ".thumbnail.c4k";

    /// <summary>Longest edge of a thumbnail in pixels: enough for a 220 px tile, a few kilobytes as WebP.</summary>
    public const int MaxEdge = 256;

    private const int Quality = 70;
    private const string Extension = ".webp";
    private const int MaxImageBytes = 32 * 1024 * 1024;
    private const int MaxThumbnailBytes = 256 * 1024;
    private const int MemoryEntries = 1500;
    private const int Workers = 2;

    private readonly object _gate = new();
    private readonly LinkedList<WorkItem> _pending = new();
    private readonly Dictionary<string, PictureItem> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _unwritable = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _created = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private Thread[]? _threads;

    /// <summary>Keep thumbnails in <see cref="FolderName"/> (setting <c>galleryThumbnails</c>); off = memory only.</summary>
    public bool WriteToFolders { get; set; } = true;

    /// <summary>
    /// Thumbnails for <paramref name="images"/> of <paramref name="folder"/>: <paramref name="ready"/> gets the index
    /// and the encoded picture of each image as it becomes available (on a worker thread, possibly before this method
    /// returns). The task completes when every image has been handled; cancelling <paramref name="cancellationToken"/>
    /// only stops the wait — the folder's remaining work moves to the back of the queue and still gets done.
    /// </summary>
    public Task RequestAsync(IPluginHost host, string folder, IReadOnlyList<VfsEntry> images, Action<int, ReadOnlyMemory<byte>> ready,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
        var job = new FolderJob(host, folder, images, ready);
        EnsureWorkers();
        Enqueue([new SyncItem(job)], front: true);
        return AwaitAsync(job, cancellationToken);
    }

    private async Task AwaitAsync(FolderJob job, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => Demote(job));
        await job.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Where the thumbnails of <paramref name="folder"/> live.</summary>
    public static string ThumbnailFolder(string folder) => Join(folder, FolderName);

    /// <summary>The thumbnail file name of an image: the name plus its size and modification time, so a change shows.</summary>
    public static string ThumbnailName(VfsEntry image) =>
        $"{image.Name}.{image.Size}-{image.LastWriteTime.ToUnixTimeSeconds()}{Extension}";

    /// <summary>A path inside a VFS folder: <c>\</c> on disks and shares, <c>/</c> behind a scheme.</summary>
    internal static string Join(string folder, string name)
    {
        if (folder.EndsWith('/') || folder.EndsWith('\\'))
        {
            return folder + name;
        }

        var separator = folder.Contains("://", StringComparison.Ordinal) ? '/' : '\\';
        return folder + separator + name;
    }

    /// <summary>
    /// Decodes <paramref name="image"/> (scaled while decoding when the codec allows it), turns it the way its EXIF
    /// orientation says, fits it into <see cref="MaxEdge"/> and encodes it as WebP; <c>null</c> if it is not a picture.
    /// </summary>
    public static byte[]? Encode(ReadOnlySpan<byte> image)
    {
        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            return null;
        }

        var info = codec.Info;
        var scale = Math.Min(1f, (float)MaxEdge / Math.Max(info.Width, info.Height));
        var supported = codec.GetScaledDimensions(scale);
        var decodeInfo = new SKImageInfo(Math.Max(1, supported.Width), Math.Max(1, supported.Height), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        var (width, height) = Fit(info.Width, info.Height);
        SKBitmap? resized = null;
        SKBitmap? oriented = null;
        try
        {
            var fitted = decoded;
            if (decoded.Width != width || decoded.Height != height)
            {
                resized = decoded.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (resized is null)
                {
                    return null;
                }

                fitted = resized;
            }

            oriented = Orient(fitted, codec.EncodedOrigin);
            using var encoded = (oriented ?? fitted).Encode(SKEncodedImageFormat.Webp, Quality);
            return encoded?.ToArray();
        }
        finally
        {
            oriented?.Dispose();
            resized?.Dispose();
        }
    }

    /// <summary>Size of a thumbnail for an image of the given size: the longest edge becomes <see cref="MaxEdge"/>.</summary>
    public static (int Width, int Height) Fit(int width, int height)
    {
        var longest = Math.Max(width, height);
        if (longest <= MaxEdge)
        {
            return (Math.Max(1, width), Math.Max(1, height));
        }

        var scale = (double)MaxEdge / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>A copy turned as the EXIF orientation says, or <c>null</c> when the picture is already upright.</summary>
    private static SKBitmap? Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return null;
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = swap ? source.Height : source.Width;
        var h = swap ? source.Width : source.Height;
        // Each origin as the matrix that takes a source pixel to its place in the upright picture.
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        var target = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return target;
    }

    /// <summary>A thumbnail file is used only when its header still parses (an interrupted write leaves a broken file).</summary>
    private static bool LooksLikePicture(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return codec is not null && codec.Info.Width > 0;
    }

    private void EnsureWorkers()
    {
        lock (_gate)
        {
            if (_threads is not null)
            {
                return;
            }

            _threads = new Thread[Workers];
            for (var i = 0; i < Workers; i++)
            {
                _threads[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"c4k-thumbnails-{i + 1}", Priority = ThreadPriority.BelowNormal };
                _threads[i].Start();
            }
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            try
            {
                _available.Wait(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            WorkItem? item;
            lock (_gate)
            {
                item = _pending.First?.Value;
                if (item is not null)
                {
                    _pending.RemoveFirst();
                }
            }

            item?.Run(this);
        }
    }

    private void Enqueue(IReadOnlyList<WorkItem> items, bool front)
    {
        if (items.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (front)
            {
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    _pending.AddFirst(items[i]);
                }
            }
            else
            {
                foreach (var item in items)
                {
                    _pending.AddLast(item);
                }
            }
        }

        _available.Release(items.Count);
    }

    /// <summary>The user left the folder: its pictures not started yet wait behind everything else.</summary>
    private void Demote(FolderJob job)
    {
        job.Cancelled = true;
        lock (_gate)
        {
            var demoted = new List<LinkedListNode<WorkItem>>();
            for (var node = _pending.First; node is not null; node = node.Next)
            {
                if (node.Value is PictureItem picture && picture.AllListenersCancelled)
                {
                    demoted.Add(node);
                }
            }

            foreach (var node in demoted)
            {
                _pending.Remove(node);
                _pending.AddLast(node.Value);
            }
        }
    }

    private void Remember(string key, byte[] bytes)
    {
        if (_memory.Count >= MemoryEntries)
        {
            _memory.Clear();
        }

        _memory[key] = bytes;
    }

    private byte[]? TryRead(IPluginHost host, string path, int maxBytes)
    {
        try
        {
            using var stream = host.OpenReadAsync(path, _stopping.Token).GetAwaiter().GetResult();
            var buffer = new byte[maxBytes + 1];
            var length = stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, _stopping.Token).AsTask().GetAwaiter().GetResult();
            if (length > maxBytes)
            {
                return null;
            }

            Array.Resize(ref buffer, length);
            return buffer;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return null;
        }
    }

    private bool TryWrite(IPluginHost host, string folder, string path, byte[] bytes)
    {
        var thumbnails = ThumbnailFolder(folder);
        if (!WriteToFolders || _unwritable.ContainsKey(thumbnails))
        {
            return false;
        }

        try
        {
            if (!_created.ContainsKey(thumbnails))
            {
                host.CreateDirectoryAsync(thumbnails, hidden: true, _stopping.Token).GetAwaiter().GetResult();
                _created[thumbnails] = 0;
            }

            host.WriteBytesAsync(path, bytes, _stopping.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // Read-only location, no fs.write, a vanished folder: this session keeps the folder's thumbnails in memory.
            _unwritable[thumbnails] = 0;
            _created.TryRemove(thumbnails, out _);
            return false;
        }
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or OperationCanceledException
        or NotSupportedException or ArgumentException or InvalidOperationException or TimeoutException or VfsAuthenticationRequiredException
        or VfsProviderNotFoundException;

    public void Dispose()
    {
        _stopping.Cancel();
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    private abstract class WorkItem
    {
        public abstract void Run(ThumbnailStore store);
    }

    /// <summary>One gallery shown: which of its images need what.</summary>
    private sealed class FolderJob(IPluginHost host, string folder, IReadOnlyList<VfsEntry> images, Action<int, ReadOnlyMemory<byte>> ready)
    {
        private int _remaining = 1; // the sync step itself

        public IPluginHost Host { get; } = host;

        public string Folder { get; } = folder;

        public IReadOnlyList<VfsEntry> Images { get; } = images;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool Cancelled;

        public void Expect() => Interlocked.Increment(ref _remaining);

        public void Ready(int index, byte[] bytes)
        {
            if (Cancelled)
            {
                return;
            }

            try
            {
                ready(index, bytes);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The view that asked is gone or broken; the thumbnail itself is kept.
            }
        }

        public void Done()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                Completion.TrySetResult();
            }
        }
    }

    /// <summary>Lists the folder's thumbnails once and queues what is missing (front) and what is stale (back).</summary>
    private sealed class SyncItem(FolderJob job) : WorkItem
    {
        public override void Run(ThumbnailStore store)
        {
            try
            {
                Sync(store);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
            }
            finally
            {
                job.Done();
            }
        }

        private void Sync(ThumbnailStore store)
        {
            var thumbnails = ThumbnailFolder(job.Folder);
            var existing = new Dictionary<string, VfsEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var entry in job.Host.ListAsync(thumbnails, store._stopping.Token).GetAwaiter().GetResult())
                {
                    if (entry.Kind == VfsEntryKind.File)
                    {
                        existing[entry.Name] = entry;
                    }
                }

                store._created[thumbnails] = 0;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // No thumbnails yet (or no access): everything is made fresh.
            }

            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fresh = new List<WorkItem>();
            for (var i = 0; i < job.Images.Count; i++)
            {
                var image = job.Images[i];
                if (image.Size is < 0 or > MaxImageBytes)
                {
                    continue;
                }

                var name = ThumbnailName(image);
                expected.Add(name);
                var path = Join(thumbnails, name);
                if (store._memory.TryGetValue(path, out var cached))
                {
                    job.Ready(i, cached);
                    continue;
                }

                if (store._failed.ContainsKey(path))
                {
                    continue;
                }

                lock (store._gate)
                {
                    if (!store._inFlight.TryGetValue(path, out var item))
                    {
                        item = new PictureItem(job.Host, job.Folder, image, path, existing.TryGetValue(name, out var file) && file.Size is > 0 and <= MaxThumbnailBytes);
                        store._inFlight[path] = item;
                        fresh.Add(item);
                    }

                    // Counted before the item can finish (it snapshots its listeners under the same gate).
                    job.Expect();
                    item.Listeners.Add((job, i));
                }
            }

            store.Enqueue(fresh, front: true);

            var stale = existing.Keys
                .Where(name => !expected.Contains(name) && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                .Select(name => (WorkItem)new DeleteItem(job.Host, Join(thumbnails, name)))
                .ToList();
            store.Enqueue(stale, front: false);
        }
    }

    /// <summary>Reads a current thumbnail or makes (and keeps) a new one, then tells every gallery waiting for it.</summary>
    private sealed class PictureItem(IPluginHost host, string folder, VfsEntry image, string path, bool exists) : WorkItem
    {
        /// <summary>Galleries that want this picture; guarded by the store's gate.</summary>
        public List<(FolderJob Job, int Index)> Listeners { get; } = new(1);

        public bool AllListenersCancelled => Listeners.TrueForAll(l => l.Job.Cancelled);

        public override void Run(ThumbnailStore store)
        {
            byte[]? bytes = null;
            try
            {
                bytes = Produce(store);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A codec failure on a damaged file must not take the worker down; the tile keeps its icon.
            }

            if (bytes is not null)
            {
                store.Remember(path, bytes);
            }
            else
            {
                store._failed[path] = 0;
            }

            (FolderJob Job, int Index)[] listeners;
            lock (store._gate)
            {
                store._inFlight.Remove(path);
                listeners = [.. Listeners];
            }

            foreach (var (job, index) in listeners)
            {
                if (bytes is not null)
                {
                    job.Ready(index, bytes);
                }

                job.Done();
            }
        }

        private byte[]? Produce(ThumbnailStore store)
        {
            if (exists && store.TryRead(host, path, MaxThumbnailBytes) is { } kept && LooksLikePicture(kept))
            {
                return kept;
            }

            var source = store.TryRead(host, image.Path, MaxImageBytes);
            if (source is null)
            {
                return null;
            }

            var thumbnail = Encode(source);
            if (thumbnail is not null)
            {
                store.TryWrite(host, folder, path, thumbnail);
            }

            return thumbnail;
        }
    }

    /// <summary>A thumbnail whose image changed or disappeared.</summary>
    private sealed class DeleteItem(IPluginHost host, string path) : WorkItem
    {
        public override void Run(ThumbnailStore store)
        {
            try
            {
                host.DeleteAsync(path, recursive: false, store._stopping.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (IsExpected(ex))
            {
            }
        }
    }
}
