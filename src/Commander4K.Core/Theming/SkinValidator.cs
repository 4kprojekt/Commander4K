namespace Commander4K.Core.Theming;

/// <summary>
/// Readability checks (WCAG 2.1 contrast) for hand-made or AI-generated skins. Warnings, not errors: retro palettes
/// such as cyan on blue are intentionally below the strict text threshold.
/// </summary>
public static class SkinValidator
{
    public const double TextContrast = 3.0;
    public const double MinimumContrast = 1.6;

    public static IReadOnlyList<string> Validate(Skin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        var p = skin.Palette;
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(skin.Id) || skin.Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
        {
            warnings.Add($"Id „{skin.Id}” powinno składać się z małych liter, cyfr i myślników (np. „cyber-neon”).");
        }

        Check(warnings, "Pliki", p.FileForeground, p.PanelBackground, TextContrast);
        Check(warnings, "Katalogi", p.DirectoryForeground, p.PanelBackground, TextContrast);
        Check(warnings, "Kursor", p.CursorForeground, p.CursorBackground, TextContrast);
        Check(warnings, "Zaznaczone pliki", p.SelectedForeground, p.PanelBackground, TextContrast);
        Check(warnings, "Pasek stanu", p.StatusForeground, p.StatusBackground, TextContrast);
        Check(warnings, "Klawisze funkcyjne", p.FunctionKeyLabelForeground, p.FunctionKeyLabelBackground, TextContrast);
        Check(warnings, "Linia poleceń", p.CommandLineForeground, p.CommandLineBackground, TextContrast);
        Check(warnings, "Ukryte pliki", p.HiddenForeground, p.PanelBackground, MinimumContrast);

        if (skin.Typography.FontSize is < 8 or > 32)
        {
            warnings.Add($"Rozmiar czcionki {skin.Typography.FontSize} jest poza zakresem 8–32.");
        }

        if (skin.Typography.RowHeightFactor is < 1.0 or > 3.0)
        {
            warnings.Add($"Wysokość wiersza ×{skin.Typography.RowHeightFactor} jest poza zakresem 1,0–3,0.");
        }

        return warnings;
    }

    /// <summary>WCAG relative-luminance contrast ratio, 1 (none) to 21 (black on white).</summary>
    public static double ContrastRatio(RgbColor a, RgbColor b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static void Check(List<string> warnings, string what, RgbColor foreground, RgbColor background, double minimum)
    {
        var ratio = ContrastRatio(foreground, background);
        if (ratio < minimum)
        {
            warnings.Add($"{what}: kontrast {ratio:0.0}:1 ({foreground} na {background}) jest niższy niż {minimum:0.0}:1.");
        }
    }

    private static double Luminance(RgbColor c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
