namespace Commander4K.Core.Web;

/// <summary>
/// Serves byte ranges of one previewed file to a web page. A seekable source is reopened through the
/// <c>open</c> delegate for every range, so parallel range requests never share a stream position;
/// a non-seekable source is copied once to a temp file (deleted on dispose) that serves all ranges.
/// </summary>
public sealed class EntryFileSource : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<Stream>> _open;
    private readonly string? _spoolPath;
    private volatile bool _disposed;

    private EntryFileSource(Func<CancellationToken, Task<Stream>> open, long length, string? spoolPath)
    {
        _open = open;
        Length = length;
        _spoolPath = spoolPath;
    }

    /// <summary>32 hex characters, new for every instance; the page's file URL carries it.</summary>
    public string Token { get; } = Guid.NewGuid().ToString("N");

    public long Length { get; }

    public static async Task<EntryFileSource> OpenAsync(Func<CancellationToken, Task<Stream>> open, long? knownLength, string tempFolder, CancellationToken cancellationToken)
    {
        var stream = await open(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stream.CanSeek)
            {
                return new EntryFileSource(open, stream.Length, null);
            }

            Directory.CreateDirectory(tempFolder);
            var path = Path.Combine(tempFolder, "c4k-web-" + Guid.NewGuid().ToString("N") + ".bin");
            long size;
            try
            {
                var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await using (file.ConfigureAwait(false))
                {
                    await stream.CopyToAsync(file, 81920, cancellationToken).ConfigureAwait(false);
                    size = file.Length;
                }
            }
            catch
            {
                TryDelete(path);
                throw;
            }

            return new EntryFileSource(
                _ => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)),
                size,
                path);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A stream over the range (the whole file for null); the caller disposes it.</summary>
    public async Task<Stream> OpenRangeAsync(ByteRange? range, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = await _open(cancellationToken).ConfigureAwait(false);
        if (range is not { } r)
        {
            return stream;
        }

        try
        {
            stream.Seek(r.Start, SeekOrigin.Begin);
            return new LimitedStream(stream, r.Length);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_spoolPath is not null)
        {
            TryDelete(_spoolPath);
        }

        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp file we cannot delete is left for the OS temp cleanup.
        }
    }

    private sealed class LimitedStream(Stream inner, long total) : Stream
    {
        private long _remaining = total;
        private readonly long _total = total;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _total;

        public override long Position { get => _total - _remaining; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
