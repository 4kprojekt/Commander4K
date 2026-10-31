using Commander4K.Core.Configuration;

namespace Commander4K.Core.Tests;

public sealed class ColumnWidthSettingTests
{
    [Fact]
    public void Widths_round_trip_through_the_settings_text()
    {
        var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["size"] = 96, ["builtin.columns.git/status"] = 72.5 };

        var text = SettingsCatalog.FormatColumnWidths(widths);
        var parsed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        SettingsCatalog.ParseColumnWidths(text, parsed);

        Assert.Equal("builtin.columns.git/status=72.5, size=96", text);
        Assert.Equal(widths, parsed);
    }

    [Fact]
    public void Parsing_replaces_in_place_and_skips_malformed_pairs()
    {
        var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["date"] = 140 };
        var same = widths;

        SettingsCatalog.ParseColumnWidths("size = 90; date=; =5, hash=-1, broken", widths);

        Assert.Same(same, widths);
        Assert.Equal(new Dictionary<string, double> { ["size"] = 90 }, widths);

        SettingsCatalog.ParseColumnWidths("  ", widths);
        Assert.Empty(widths);
    }
}
