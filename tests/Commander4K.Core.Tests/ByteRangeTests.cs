using Commander4K.Core.Web;

namespace Commander4K.Core.Tests;

public sealed class ByteRangeTests
{
    [Theory]
    [InlineData("bytes=0-99", 1000, 0, 99)]
    [InlineData("bytes=900-", 1000, 900, 999)]
    [InlineData("bytes=-100", 1000, 900, 999)]
    [InlineData("bytes=0-5000", 1000, 0, 999)]
    [InlineData("BYTES=0-9", 1000, 0, 9)]
    [InlineData("bytes=-5000", 1000, 0, 999)]
    [InlineData("bytes=-99999999999999999999", 1000, 0, 999)]
    [InlineData("bytes=5-99999999999999999999", 1000, 5, 999)]
    public void Satisfiable_ranges(string header, long length, long start, long end)
    {
        var result = ByteRange.Evaluate(header, length);

        Assert.Equal(ByteRangeKind.Satisfiable, result.Kind);
        Assert.Equal(new ByteRange(start, end), result.Range);
    }

    [Theory]
    [InlineData("bytes=1000-", 1000)]
    [InlineData("bytes=-0", 1000)]
    [InlineData("bytes=0-0", 0)]
    [InlineData("bytes=-5", 0)]
    [InlineData("bytes=99999999999999999999-", 1000)]
    public void Unsatisfiable_ranges(string header, long length) =>
        Assert.Equal(ByteRangeKind.Unsatisfiable, ByteRange.Evaluate(header, length).Kind);

    [Theory]
    [InlineData("bytes=0-1,5-6")]
    [InlineData("items=0-1")]
    [InlineData("bytes=5-2")]
    [InlineData("bytes=-")]
    [InlineData("bytes= 0-1")]
    [InlineData(" bytes=0-1")]
    [InlineData("bytes=0 -1")]
    [InlineData("bytes=a-b")]
    [InlineData("bytes=5")]
    [InlineData("")]
    public void Malformed_ranges_are_ignored(string header) =>
        Assert.Equal(ByteRangeKind.Ignore, ByteRange.Evaluate(header, 1000).Kind);

    [Fact]
    public void No_header_means_none() =>
        Assert.Equal(ByteRangeKind.None, ByteRange.Evaluate(null, 1000).Kind);
}
