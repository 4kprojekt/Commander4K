using System.Text.RegularExpressions;
using Commander4K.Core.Web;

namespace Commander4K.Core.Tests;

public sealed partial class WebPreviewOriginTests
{
    [Fact]
    public void Ids_that_differ_only_in_punctuation_get_different_hosts()
    {
        var hosts = new[] { "foo.bar", "foo_bar", "foo-bar" }.Select(WebPreviewOrigin.HostName).ToArray();

        Assert.Equal(3, hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(hosts, host => Assert.EndsWith(".plugin.c4k", host, StringComparison.Ordinal));
    }

    [Fact]
    public void A_plain_label_id_is_kept()
    {
        Assert.Equal("foo-bar", WebPreviewOrigin.Label("foo-bar"));
    }

    [Theory]
    [InlineData("Foo")]
    [InlineData("foo--bar")]
    [InlineData("-foo")]
    [InlineData("foo-")]
    [InlineData("xn.a")]
    [InlineData("..")]
    [InlineData("żółw.wtyczka")]
    public void Other_ids_get_a_hash_suffix(string id)
    {
        var label = WebPreviewOrigin.Label(id);

        Assert.Contains("--", label, StringComparison.Ordinal);
        Assert.NotEqual(id.ToLowerInvariant(), label);
        Assert.False(label.StartsWith("xn--", StringComparison.Ordinal));
    }

    [Fact]
    public void Case_variants_get_different_hosts()
    {
        Assert.NotEqual(WebPreviewOrigin.Label("foo"), WebPreviewOrigin.Label("Foo"));
    }

    [Fact]
    public void Long_ids_fit_in_one_label()
    {
        var plain = new string('a', 64);
        var dotted = string.Concat(Enumerable.Repeat("ab.", 30));

        Assert.True(WebPreviewOrigin.Label(plain).Length <= 63);
        Assert.True(WebPreviewOrigin.Label(dotted).Length <= 63);
        Assert.NotEqual(WebPreviewOrigin.Label(plain), WebPreviewOrigin.Label(plain + "a"));
    }

    [Fact]
    public void Labels_are_deterministic()
    {
        Assert.Equal(WebPreviewOrigin.Label("examples.pdf-viewer"), WebPreviewOrigin.Label("examples.pdf-viewer"));
    }

    [Theory]
    [InlineData("examples.pdf-viewer")]
    [InlineData("foo_bar")]
    [InlineData("A B/C\\D:E")]
    [InlineData("")]
    [InlineData("---")]
    [InlineData("żółw")]
    public void Labels_are_valid_dns_labels(string id)
    {
        var label = WebPreviewOrigin.Label(id);

        Assert.Matches(LabelPattern(), label);
        Assert.True(label.Length is > 0 and <= 63);
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex LabelPattern();
}
