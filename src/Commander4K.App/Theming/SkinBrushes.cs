using Avalonia.Media;
using Avalonia.Media.Immutable;
using Commander4K.Core.Theming;

namespace Commander4K.App.Theming;

/// <summary>
/// Avalonia resources materialised once per skin, so rendering never allocates brushes or typefaces. Design-system
/// tokens a skin does not define (older and retro skins) are derived from its base colours.
/// </summary>
public sealed class SkinBrushes
{
    /// <param name="density">"compact", "comfortable" or anything else for the skin's own size (design system: 12/1.5, 13/1.7, 14/1.72).</param>
    public SkinBrushes(Skin skin, string? density = null)
    {
        Skin = skin;
        var p = skin.Palette;
        WindowBackground = Brush(p.WindowBackground);
        PanelBackground = Brush(p.PanelBackground);
        PanelBorder = Brush(p.PanelBorder);
        HeaderBackground = Brush(p.HeaderBackground);
        HeaderForeground = Brush(p.HeaderForeground);
        FileForeground = Brush(p.FileForeground);
        DirectoryForeground = Brush(p.DirectoryForeground);
        HiddenForeground = Brush(p.HiddenForeground);
        ExecutableForeground = Brush(p.ExecutableForeground);
        ArchiveForeground = Brush(p.ArchiveForeground);
        NetworkForeground = Brush(p.NetworkForeground);
        SelectedForeground = Brush(p.SelectedForeground);
        CursorBackground = Brush(p.CursorBackground);
        CursorForeground = Brush(p.CursorForeground);
        InactiveCursorBackground = Brush(p.InactiveCursorBackground);
        StatusBackground = Brush(p.StatusBackground);
        StatusForeground = Brush(p.StatusForeground);
        CommandLineBackground = Brush(p.CommandLineBackground);
        CommandLineForeground = Brush(p.CommandLineForeground);
        FunctionKeyNumberBackground = Brush(p.FunctionKeyNumberBackground);
        FunctionKeyNumberForeground = Brush(p.FunctionKeyNumberForeground);
        FunctionKeyLabelBackground = Brush(p.FunctionKeyLabelBackground);
        FunctionKeyLabelForeground = Brush(p.FunctionKeyLabelForeground);
        Accent = Brush(p.Accent);
        Progress = Brush(p.Progress);
        Error = Brush(p.Error);
        Skeleton = new ImmutableSolidColorBrush(ToColor(p.HiddenForeground), 0.25);

        // Design-system tokens, with fallbacks that keep retro skins looking like themselves.
        var warning = p.Warning ?? RgbColor.Parse("#E0A030");
        WindowForeground = Brush(p.WindowForeground ?? p.FileForeground);
        MutedForeground = Brush(p.MutedForeground ?? p.HiddenForeground);
        Surface2 = Brush(p.Surface2 ?? p.PanelBackground.Mix(p.FileForeground, 0.05));
        HoverBackground = Brush(p.HoverBackground ?? p.PanelBackground.Mix(p.FileForeground, 0.08));
        PressedBackground = Brush(p.PressedBackground ?? p.PanelBackground.Mix(p.FileForeground, 0.14));
        FocusRing = Brush(p.FocusRing ?? p.Accent);
        ActiveHeaderBackground = Brush(p.ActiveHeaderBackground ?? p.HeaderBackground.Mix(p.Accent, 0.16));
        ActiveHeaderForeground = Brush(p.ActiveHeaderForeground ?? p.HeaderForeground);
        SelectedBackground = p.SelectedBackground is { } selected ? Brush(selected) : null;
        CursorSelectedForeground = Brush(p.CursorSelectedForeground ?? p.SelectedForeground);
        InactiveCursorBorder = Brush(p.InactiveCursorBorder ?? p.CursorBackground);
        AccentForeground = ContrastingForeground(Accent.Color);
        ErrorForeground = ContrastingForeground(Error.Color);
        Warning = Brush(warning);
        Success = Brush(p.Success ?? p.Progress);
        WarningBackground = Brush(p.WarningBackground ?? p.PanelBackground.Mix(warning, 0.14));
        ErrorBackground = Brush(p.ErrorBackground ?? p.PanelBackground.Mix(p.Error, 0.14));
        HighlightBackground = Brush(p.HighlightBackground ?? p.PanelBackground.Mix(p.Accent, 0.16));
        HighlightForeground = Brush(p.HighlightForeground ?? p.Accent);
        CodeBackground = Brush(p.CodeBackground ?? p.CommandLineBackground);
        IsModern = skin.Chrome.CornerRadius > 0;

        BorderPen = new ImmutablePen(PanelBorder, 1);
        InactiveCursorPen = new ImmutablePen(InactiveCursorBorder, 1);
        FocusPen = new ImmutablePen(FocusRing, 1);
        FontFamily = FontFamily.Parse(skin.Typography.FontFamily);
        MonospaceFamily = FontFamily.Parse("Cascadia Mono, Consolas, JetBrains Mono");
        Typeface = new Typeface(FontFamily);
        BoldTypeface = new Typeface(FontFamily, weight: FontWeight.SemiBold);
        (FontSize, var factor) = density switch
        {
            "compact" => (skin.Typography.FontSize - 1, Math.Min(skin.Typography.RowHeightFactor, 1.5)),
            "comfortable" => (skin.Typography.FontSize + 1, Math.Max(skin.Typography.RowHeightFactor, 1.72)),
            _ => (skin.Typography.FontSize, skin.Typography.RowHeightFactor),
        };
        RowHeight = Math.Round(FontSize * factor);
    }

    public Skin Skin { get; }

    public IImmutableSolidColorBrush WindowBackground { get; }
    public IImmutableSolidColorBrush PanelBackground { get; }
    public IImmutableSolidColorBrush PanelBorder { get; }
    public IImmutableSolidColorBrush HeaderBackground { get; }
    public IImmutableSolidColorBrush HeaderForeground { get; }
    public IImmutableSolidColorBrush FileForeground { get; }
    public IImmutableSolidColorBrush DirectoryForeground { get; }
    public IImmutableSolidColorBrush HiddenForeground { get; }
    public IImmutableSolidColorBrush ExecutableForeground { get; }
    public IImmutableSolidColorBrush ArchiveForeground { get; }
    public IImmutableSolidColorBrush NetworkForeground { get; }
    public IImmutableSolidColorBrush SelectedForeground { get; }
    public IImmutableSolidColorBrush CursorBackground { get; }
    public IImmutableSolidColorBrush CursorForeground { get; }
    public IImmutableSolidColorBrush InactiveCursorBackground { get; }
    public IImmutableSolidColorBrush StatusBackground { get; }
    public IImmutableSolidColorBrush StatusForeground { get; }
    public IImmutableSolidColorBrush CommandLineBackground { get; }
    public IImmutableSolidColorBrush CommandLineForeground { get; }
    public IImmutableSolidColorBrush FunctionKeyNumberBackground { get; }
    public IImmutableSolidColorBrush FunctionKeyNumberForeground { get; }
    public IImmutableSolidColorBrush FunctionKeyLabelBackground { get; }
    public IImmutableSolidColorBrush FunctionKeyLabelForeground { get; }
    public IImmutableSolidColorBrush Accent { get; }
    public IImmutableSolidColorBrush Progress { get; }
    public IImmutableSolidColorBrush Error { get; }
    public IImmutableSolidColorBrush Skeleton { get; }

    public IImmutableSolidColorBrush WindowForeground { get; }
    public IImmutableSolidColorBrush MutedForeground { get; }
    public IImmutableSolidColorBrush Surface2 { get; }
    public IImmutableSolidColorBrush HoverBackground { get; }
    public IImmutableSolidColorBrush PressedBackground { get; }
    public IImmutableSolidColorBrush FocusRing { get; }
    public IImmutableSolidColorBrush ActiveHeaderBackground { get; }
    public IImmutableSolidColorBrush ActiveHeaderForeground { get; }

    /// <summary><c>null</c> = marked rows get no background (retro skins mark by text colour only).</summary>
    public IImmutableSolidColorBrush? SelectedBackground { get; }
    public IImmutableSolidColorBrush CursorSelectedForeground { get; }
    public IImmutableSolidColorBrush InactiveCursorBorder { get; }
    public IImmutableSolidColorBrush AccentForeground { get; }

    /// <summary>Text on an <see cref="Error"/> fill (danger buttons).</summary>
    public IImmutableSolidColorBrush ErrorForeground { get; }
    public IImmutableSolidColorBrush Warning { get; }
    public IImmutableSolidColorBrush Success { get; }
    public IImmutableSolidColorBrush WarningBackground { get; }
    public IImmutableSolidColorBrush ErrorBackground { get; }
    public IImmutableSolidColorBrush HighlightBackground { get; }
    public IImmutableSolidColorBrush HighlightForeground { get; }
    public IImmutableSolidColorBrush CodeBackground { get; }

    /// <summary>Rounded, card-like chrome (design system); retro skins (radius 0) keep flat, full-width rows.</summary>
    public bool IsModern { get; }

    public ImmutablePen BorderPen { get; }
    public ImmutablePen InactiveCursorPen { get; }
    public ImmutablePen FocusPen { get; }
    public FontFamily FontFamily { get; }
    public FontFamily MonospaceFamily { get; }
    public Typeface Typeface { get; }
    public Typeface BoldTypeface { get; }
    public double FontSize { get; }
    public double RowHeight { get; }

    /// <summary>Corner radius of controls (4 px in the design system, square for retro skins).</summary>
    public double ControlRadius => IsModern ? 4 : 0;

    public static Color ToColor(RgbColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static IImmutableSolidColorBrush ContrastingForeground(Color background)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        var luminance = 0.2126 * Linear(background.R) + 0.7152 * Linear(background.G) + 0.0722 * Linear(background.B);
        var whiteContrast = 1.05 / (luminance + 0.05);
        var blackContrast = (luminance + 0.05) / 0.05;
        return new ImmutableSolidColorBrush(whiteContrast >= blackContrast ? Colors.White : Colors.Black);
    }

    private static ImmutableSolidColorBrush Brush(RgbColor c) => new(ToColor(c));
}
