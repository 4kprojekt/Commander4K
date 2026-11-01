using Commander4K.Core.Web;

namespace Commander4K.Core.Tests;

public sealed class SearchEngineTests
{
    private static readonly SearchEngine Google = SearchEngines.Get("google");

    [Theory]
    [InlineData("https://github.com/4kprojekt", "https://github.com/4kprojekt")]
    [InlineData("github.com/4kprojekt/Commander4K", "https://github.com/4kprojekt/Commander4K")]
    [InlineData("localhost:8080/api", "http://localhost:8080/api")]
    [InlineData("192.168.1.10", "http://192.168.1.10/")]
    [InlineData("jak działa c#", "https://www.google.com/search?q=jak%20dzia%C5%82a%20c%23")]
    [InlineData("dotnet", "https://www.google.com/search?q=dotnet")]
    [InlineData("  ", null)]
    public void Address_bar_opens_urls_and_searches_everything_else(string input, string? expected) =>
        Assert.Equal(expected, SearchEngines.ResolveAddress(input, Google));

    [Fact]
    public void Unknown_engine_falls_back()
    {
        Assert.Equal("google", SearchEngines.Get("altavista").Id);
        Assert.Equal("bing", SearchEngines.Get("assistant", "bing").Id);
        Assert.Equal("google", SearchEngines.Get("chatgpt", "chatgpt").Id); // an engine removed from an old settings.json
        Assert.All(SearchEngines.All, e => Assert.StartsWith("https://", e.UrlTemplate, StringComparison.Ordinal));
    }
}
