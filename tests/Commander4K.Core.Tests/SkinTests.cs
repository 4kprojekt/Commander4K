using Commander4K.Core.Configuration;
using Commander4K.Core.Theming;

namespace Commander4K.Core.Tests;

public sealed class SkinTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("c4k-skins-").FullName;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("#0000AA", 0, 0, 0xAA, 255)]
    [InlineData("#800000ff", 0, 0, 0xFF, 0x80)]
    public void Parses_colours(string text, byte r, byte g, byte b, byte a)
    {
        var color = RgbColor.Parse(text);

        Assert.Equal(new RgbColor(r, g, b, a), color);
        Assert.Equal(text.ToUpperInvariant(), color.ToString());
    }

    [Theory]
    [InlineData("0000AA")]
    [InlineData("#00AA")]
    [InlineData("#GG0000")]
    public void Rejects_bad_colours(string text) => Assert.False(RgbColor.TryParse(text, out _));

    [Fact]
    public void Built_in_skins_include_the_classics()
    {
        Assert.Equal(BuiltInSkins.All.Count, BuiltInSkins.All.Select(s => s.Id).Distinct().Count());
        Assert.Equal(PanelBorderStyle.Double, BuiltInSkins.NortonCommander.Chrome.BorderStyle);
        Assert.Equal(RgbColor.Parse("#0000AA"), BuiltInSkins.MidnightCommander.Palette.PanelBackground);
        Assert.True(BuiltInSkins.NortonCommander.Chrome.UppercaseNames);
    }

    [Fact]
    public void Unknown_skin_falls_back_and_next_wraps()
    {
        var catalog = SkinCatalog.BuiltInOnly;

        Assert.Equal(BuiltInSkins.ModernDarkId, catalog.Get("does-not-exist").Id);
        Assert.Equal(BuiltInSkins.ModernDarkId, catalog.Next(BuiltInSkins.All[^1].Id).Id);
        Assert.Equal(BuiltInSkins.All[1].Id, catalog.Next(BuiltInSkins.All[0].Id).Id);
    }

    [Fact]
    public async Task Exported_skin_round_trips_as_custom_skin_and_override()
    {
        var custom = BuiltInSkins.NortonCommander with { Id = "my-nc", DisplayName = "Mój NC" };
        var overrideBuiltIn = BuiltInSkins.MidnightCommander with
        {
            Palette = BuiltInSkins.MidnightCommander.Palette with { PanelBackground = RgbColor.Parse("#000040") },
        };
        await SkinCatalog.ExportAsync(custom, Path.Combine(_directory, "my-nc.json"), TestToken);
        await SkinCatalog.ExportAsync(overrideBuiltIn, Path.Combine(_directory, "mc.json"), TestToken);
        await File.WriteAllTextAsync(Path.Combine(_directory, "broken.json"), "{ not json", TestToken);

        var catalog = await SkinCatalog.LoadAsync(_directory, TestToken);

        Assert.Equal(custom, catalog.Get("my-nc"));
        Assert.Equal(RgbColor.Parse("#000040"), catalog.Get(BuiltInSkins.MidnightCommanderId).Palette.PanelBackground);
        Assert.Equal("my-nc", catalog.Skins[^1].Id);
        Assert.EndsWith("broken.json", Assert.Single(catalog.Errors).Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_persist_selected_skin()
    {
        var store = new ConfigStore(_directory);

        var settings = await store.LoadSettingsAsync(TestToken);
        Assert.Equal(BuiltInSkins.ModernDarkId, settings.Skin);

        settings.Skin = BuiltInSkins.NortonCommanderId;
        await store.SaveSettingsAsync(settings, TestToken);

        Assert.Equal(BuiltInSkins.NortonCommanderId, (await store.LoadSettingsAsync(TestToken)).Skin);
        Assert.Contains("\"skin\": \"norton-commander\"", await File.ReadAllTextAsync(Path.Combine(_directory, "settings.json"), TestToken), StringComparison.Ordinal);
    }
}
