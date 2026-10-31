using Commander4K.Core.Web;

namespace Commander4K.Core.Tests;

public sealed class ByteRangeTests
{
    [Theory]
    [InlineData("bytes=0-99", 1000, 0, 99)]
    [InlineData("bytes=900-", 1000, 900, 999)]
    [InlineData("bytes=-100", 1000, 900, 999)]
    [InlineData("bytes=0-5000", 1000, 0, 999)]
    public void Valid_ranges(string header, long length, long start, long end)
    {
        var range = ByteRange.Parse(header, length);

        Assert.Equal(new ByteRange(start, end), range);
    }

    [Theory]
    [InlineData("bytes=1000-", 1000)]
    [InlineData("bytes=0-1,5-6", 1000)]
    [InlineData("items=0-1", 1000)]
    [InlineData(null, 1000)]
    [InlineData("bytes=5-2", 1000)]
    [InlineData("bytes=-0", 1000)]
    [InlineData("bytes=0-0", 0)]
    public void Invalid_or_unsatisfiable_ranges_are_null(string? header, long length) =>
        Assert.Null(ByteRange.Parse(header, length));
}
