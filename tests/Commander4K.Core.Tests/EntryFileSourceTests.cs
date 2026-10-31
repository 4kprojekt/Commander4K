using Commander4K.Core.Web;

namespace Commander4K.Core.Tests;

public sealed class EntryFileSourceTests : IDisposable
{
    private static readonly byte[] Data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "c4k-efs-" + Guid.NewGuid().ToString("N"));

    public EntryFileSourceTests() => Directory.CreateDirectory(_temp);

    public void Dispose() => Directory.Delete(_temp, true);

    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct);
            return copy.ToArray();
        }
    }

    [Fact]
    public async Task Seekable_stream_serves_ranges_without_temp_file()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = await EntryFileSource.OpenAsync(_ => Task.FromResult<Stream>(new MemoryStream(Data)), null, _temp, ct);

        Assert.Equal(256, source.Length);
        Assert.Empty(Directory.GetFiles(_temp));
        Assert.Equal(Data[10..20], await ReadAsync(await source.OpenRangeAsync(new ByteRange(10, 19), ct), ct));
    }

    [Fact]
    public async Task Non_seekable_stream_is_spooled_once_and_deleted_on_dispose()
    {
        var ct = TestContext.Current.CancellationToken;
        var opens = 0;
        var source = await EntryFileSource.OpenAsync(_ => { opens++; return Task.FromResult<Stream>(new ForwardOnlyStream(Data)); }, null, _temp, ct);

        Assert.Equal(256, source.Length);
        Assert.Single(Directory.GetFiles(_temp, "c4k-web-*.bin"));
        Assert.Equal(Data[250..], await ReadAsync(await source.OpenRangeAsync(new ByteRange(250, 255), ct), ct));
        Assert.Equal(Data, await ReadAsync(await source.OpenRangeAsync(null, ct), ct));
        Assert.Equal(1, opens);

        await source.DisposeAsync();

        Assert.Empty(Directory.GetFiles(_temp));
    }

    [Fact]
    public async Task Ranges_return_exact_bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = await EntryFileSource.OpenAsync(_ => Task.FromResult<Stream>(new MemoryStream(Data)), 256, _temp, ct);

        Assert.Equal(Data[0..1], await ReadAsync(await source.OpenRangeAsync(new ByteRange(0, 0), ct), ct));
        Assert.Equal(Data[100..201], await ReadAsync(await source.OpenRangeAsync(new ByteRange(100, 200), ct), ct));
        Assert.Equal(Data[255..], await ReadAsync(await source.OpenRangeAsync(new ByteRange(255, 255), ct), ct));
        Assert.Equal(Data, await ReadAsync(await source.OpenRangeAsync(null, ct), ct));
    }

    [Fact]
    public async Task Parallel_ranges_do_not_interfere()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = await EntryFileSource.OpenAsync(_ => Task.FromResult<Stream>(new MemoryStream(Data)), null, _temp, ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
            (i, bytes: await ReadAsync(await source.OpenRangeAsync(new ByteRange(i * 10, i * 10 + 9), ct), ct))));

        Assert.All(results, r => Assert.Equal(Data[(r.i * 10)..(r.i * 10 + 10)], r.bytes));
    }

    [Fact]
    public async Task Tokens_differ_per_instance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var a = await EntryFileSource.OpenAsync(_ => Task.FromResult<Stream>(new MemoryStream(Data)), null, _temp, ct);
        await using var b = await EntryFileSource.OpenAsync(_ => Task.FromResult<Stream>(new MemoryStream(Data)), null, _temp, ct);

        Assert.Equal(32, a.Token.Length);
        Assert.NotEqual(a.Token, b.Token);
    }
}
