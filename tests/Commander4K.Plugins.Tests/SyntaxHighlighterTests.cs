using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Previews;

namespace Commander4K.Plugins.Tests;

public sealed class SyntaxHighlighterTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public void CSharp_gets_colours_and_keeps_the_text_verbatim()
    {
        const string source = "public class A\r\n{\r\n    // komentarz\r\n    int x = 1;\r\n}\r\n";

        var spans = SyntaxHighlighter.Highlight(source, "A.cs", dark: true, TestToken);

        Assert.NotNull(spans);
        Assert.Equal(source, string.Concat(spans.Select(s => s.Text)));
        Assert.Contains(spans, s => s.Rgb is not null);
        Assert.True(spans.Select(s => s.Rgb).Distinct().Count() > 2);
    }

    [Theory]
    [InlineData("data.json")]
    [InlineData("conf.yaml")]
    [InlineData("script.ps1")]
    [InlineData("page.html")]
    [InlineData("Dockerfile")]
    public void Common_formats_are_recognised(string fileName)
    {
        var spans = SyntaxHighlighter.Highlight("a: 1\n", fileName, dark: false, TestToken);

        Assert.NotNull(spans);
        Assert.Equal("a: 1\n", string.Concat(spans.Select(s => s.Text)));
    }

    [Theory]
    [InlineData("notes.unknownext")]
    [InlineData("README")]
    [InlineData(null)]
    public void Unknown_files_stay_plain(string? fileName) =>
        Assert.Null(SyntaxHighlighter.Highlight("hello", fileName, dark: true, TestToken));

    [Fact]
    public void Very_long_files_are_coloured_only_at_the_start_and_still_complete()
    {
        var source = string.Concat(Enumerable.Repeat("int x = 1;\n", SyntaxHighlighter.MaxLines + 50));

        var spans = SyntaxHighlighter.Highlight(source, "big.cs", dark: true, TestToken);

        Assert.NotNull(spans);
        Assert.Equal(source, string.Concat(spans.Select(s => s.Text)));
    }

    [Fact]
    public void A_cancelled_token_stops_the_work() =>
        Assert.Throws<OperationCanceledException>(() =>
            SyntaxHighlighter.Highlight("int x;\nint y;\n", "a.cs", dark: true, new CancellationToken(canceled: true)));

    [Fact]
    public void A_plugin_preview_beats_the_built_in_text_preview_by_default()
    {
        // External previews default to Priority 10 (PreviewContribution); the built-in text preview, which now colours
        // syntax, must stay below that so a plugin bound to a file type always wins.
        var defaultPluginPriority = new PreviewContribution("p", "t", [".x"]).Priority;

        Assert.True(new TextPreviewPlugin().Priority < defaultPluginPriority);
    }
}
